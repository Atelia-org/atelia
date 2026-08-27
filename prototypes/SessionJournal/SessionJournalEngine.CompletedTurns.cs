using Atelia.Completion.Abstractions;
using Atelia.EventJournal;

namespace Atelia.SessionJournal;

public sealed partial class SessionJournalEngine {
    /// <summary>
    /// Captures the current selected-branch head and returns up to <paramref name="maximumCount"/>
    /// completed visible turns, newest first. An empty branch returns an empty snapshot.
    /// </summary>
    public SessionCompletedTurnsReadResult ReadRecentCompletedTurns(
        int maximumCount = 32,
        CancellationToken cancellationToken = default
    ) => ReadRecentCompletedTurns(
        maximumCount,
        new SessionCompletedTurnsReadBudget(),
        cancellationToken
    );

    internal SessionCompletedTurnsReadResult ReadRecentCompletedTurns(
        int maximumCount,
        SessionCompletedTurnsReadBudget budget,
        CancellationToken cancellationToken = default
    ) {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(budget);
        ValidateMaximumCount(maximumCount);
        EventAddress? capturedHead = _journal.GetHead(_branchRefId);
        return capturedHead is { } head
            ? ReadRecentCompletedTurnsAt(
                head,
                maximumCount,
                budget,
                cancellationToken
            )
            : new SessionCompletedTurnsReadResult.Snapshot(
                new SessionCompletedTurnsSnapshot(
                    CapturedHead: null,
                    Array.Empty<SessionCompletedTurnProjection>(),
                    DerivedContextNthPrevious: null
                )
            );
    }

    /// <summary>
    /// Returns completed visible turns at an immutable exact raw head. The head need not remain the
    /// selected branch head after capture, but it must resolve to a valid SessionJournal lineage.
    /// </summary>
    public SessionCompletedTurnsReadResult ReadRecentCompletedTurnsAt(
        EventAddress capturedHead,
        int maximumCount = 32,
        CancellationToken cancellationToken = default
    ) => ReadRecentCompletedTurnsAt(
        capturedHead,
        maximumCount,
        new SessionCompletedTurnsReadBudget(),
        cancellationToken
    );

    internal SessionCompletedTurnsReadResult ReadRecentCompletedTurnsAt(
        EventAddress capturedHead,
        int maximumCount,
        SessionCompletedTurnsReadBudget budget,
        CancellationToken cancellationToken = default
    ) {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(budget);
        if (capturedHead == default) {
            throw new ArgumentException(
                "Completed-turn projection head cannot be the default EventAddress.",
                nameof(capturedHead)
            );
        }
        ValidateMaximumCount(maximumCount);
        using IDisposable scope =
            EnterCompletedTurnsReadBudget(budget);
        try {
            SessionCompletedTurnsSnapshot snapshot =
                ReadRecentCompletedTurnsSnapshotAt(
                    capturedHead,
                    maximumCount,
                    cancellationToken
                );
            return new SessionCompletedTurnsReadResult.Snapshot(
                snapshot
            );
        }
        catch (SessionCompletedTurnsLimitException limit) {
            return new SessionCompletedTurnsReadResult.LimitExceeded(
                limit.Limit
            );
        }
        catch (SessionCompletedTurnsUnsupportedSchemaException schema) {
            return new SessionCompletedTurnsReadResult.UnsupportedSchema(
                schema.Message
            );
        }
        catch (NotSupportedException schema) {
            return new SessionCompletedTurnsReadResult.UnsupportedSchema(
                schema.Message
            );
        }
        catch (InvalidDataException corruption) {
            return new SessionCompletedTurnsReadResult.Corruption(
                corruption.Message
            );
        }
    }

    internal SessionExpectedObservationTurnReadResult
        ProveExpectedObservationTurnAtSelectedHead(
        SessionExpectedObservationTurnRequest request,
        CancellationToken cancellationToken = default
    ) => ProveExpectedObservationTurnAtSelectedHead(
        request,
        new SessionCompletedTurnsReadBudget(),
        cancellationToken
    );

    internal SessionExpectedObservationTurnReadResult
        ProveExpectedObservationTurnAtSelectedHead(
        SessionExpectedObservationTurnRequest request,
        SessionCompletedTurnsReadBudget budget,
        CancellationToken cancellationToken = default
    ) {
        ThrowIfDisposed();
        ValidateExpectedObservationTurnRequest(request);
        ArgumentNullException.ThrowIfNull(budget);
        cancellationToken.ThrowIfCancellationRequested();

        EventAddress? observedHead = _journal.GetHead(_branchRefId);
        if (observedHead != request.ExpectedSelectedHead) {
            return new SessionExpectedObservationTurnReadResult.Retryable(
                request.ExpectedSelectedHead,
                observedHead
            );
        }

        try {
            using IDisposable scope = EnterCompletedTurnsReadBudget(budget);
            SessionExecutionRecovery recovery = ResolveExecutionTail(
                request.ExpectedSelectedHead,
                cancellationToken
            );
            var boundary = new SessionExecutionBoundaryInspection(
                request.ExpectedSelectedHead,
                recovery.State.Phase,
                recovery.State.HeadKind
            );

            if (request.ExpectedSelectedHead == request.FreshBaseHead) {
                if (recovery.State.Phase != SessionExecutionPhase.Idle) {
                    observedHead = _journal.GetHead(_branchRefId);
                    if (observedHead != request.ExpectedSelectedHead) {
                        return new SessionExpectedObservationTurnReadResult
                            .Retryable(
                                request.ExpectedSelectedHead,
                                observedHead
                            );
                    }
                    return new SessionExpectedObservationTurnReadResult
                        .Conflict(
                            SessionExpectedObservationConflictReason
                                .FreshBaseNotIdle,
                            request.ExpectedSelectedHead,
                            observedObservationAddress: null,
                            ExpectedObservationDiagnostics(budget)
                        );
                }

                if (request.ExpectedObservationAddress is not { } abandoned) {
                    observedHead = _journal.GetHead(_branchRefId);
                    if (observedHead != request.ExpectedSelectedHead) {
                        return new SessionExpectedObservationTurnReadResult
                            .Retryable(
                                request.ExpectedSelectedHead,
                                observedHead
                            );
                    }
                    return new SessionExpectedObservationTurnReadResult
                        .NotAppended(
                            request.ExpectedSelectedHead,
                            boundary,
                            ExpectedObservationDiagnostics(budget)
                        );
                }

                (EventAddress? parent, string content) =
                    ReadExactObservationAt(abandoned);
                observedHead = _journal.GetHead(_branchRefId);
                if (observedHead != request.ExpectedSelectedHead) {
                    return new SessionExpectedObservationTurnReadResult
                        .Retryable(
                            request.ExpectedSelectedHead,
                            observedHead
                        );
                }
                SessionExpectedObservationTurnDiagnostics abandonedDiagnostics =
                    ExpectedObservationDiagnostics(budget);
                if (parent != request.FreshBaseHead) {
                    return new SessionExpectedObservationTurnReadResult
                        .Conflict(
                            SessionExpectedObservationConflictReason
                                .ObservationParentMismatch,
                            request.ExpectedSelectedHead,
                            abandoned,
                            abandonedDiagnostics
                        );
                }
                if (!string.Equals(
                        content,
                        request.ExactObservationContent,
                        StringComparison.Ordinal)) {
                    return new SessionExpectedObservationTurnReadResult
                        .Conflict(
                            SessionExpectedObservationConflictReason
                                .ObservationContentMismatch,
                            request.ExpectedSelectedHead,
                            abandoned,
                            abandonedDiagnostics
                        );
                }
                return new SessionExpectedObservationTurnReadResult.Abandoned(
                    new SessionExpectedObservationTurnEvidence(
                        request.ExpectedSelectedHead,
                        boundary,
                        abandoned,
                        parent.Value,
                        abandonedDiagnostics
                    )
                );
            }

            EventAddress foldHead = recovery.State.Phase
                == SessionExecutionPhase.AwaitingToolExecution
                    ? ReadCurrentToolActionPredecessor(recovery)
                    : request.ExpectedSelectedHead;
            CompletedTurnLocationSnapshot located = LocateTurnsThrough(
                foldHead,
                maximumCount: 1,
                cancellationToken
            );

            EventAddress? observationAddress;
            EventAddress? observationParent;
            string? observationContent;
            SessionTerminalActionProjection? terminalAction;
            if (located.OpenTurn is { } open) {
                observationAddress = open.ObservationAddress;
                observationParent = open.ObservationPredecessor;
                observationContent = open.ObservationContent;
                terminalAction = null;
            }
            else if (located.CompletedTurns.LastOrDefault()
                     is { } completed) {
                observationAddress =
                    completed.Projection.ObservationAddress;
                observationParent = completed.ObservationPredecessor;
                observationContent =
                    completed.Projection.ObservationContent;
                terminalAction = completed.Projection.TerminalAction;
            }
            else {
                observationAddress = null;
                observationParent = null;
                observationContent = null;
                terminalAction = null;
            }

            observedHead = _journal.GetHead(_branchRefId);
            if (observedHead != request.ExpectedSelectedHead) {
                return new SessionExpectedObservationTurnReadResult.Retryable(
                    request.ExpectedSelectedHead,
                    observedHead
                );
            }

            SessionExpectedObservationTurnDiagnostics proofDiagnostics =
                ExpectedObservationDiagnostics(budget);
            if (observationAddress is null) {
                return new SessionExpectedObservationTurnReadResult.Conflict(
                    SessionExpectedObservationConflictReason.NoVisibleTurn,
                    request.ExpectedSelectedHead,
                    observedObservationAddress: null,
                    proofDiagnostics
                );
            }
            if (observationParent != request.FreshBaseHead) {
                return new SessionExpectedObservationTurnReadResult.Conflict(
                    SessionExpectedObservationConflictReason
                        .ObservationParentMismatch,
                    request.ExpectedSelectedHead,
                    observationAddress,
                    proofDiagnostics
                );
            }
            if (request.ExpectedObservationAddress is { } expectedObservation
                && observationAddress != expectedObservation) {
                return new SessionExpectedObservationTurnReadResult.Conflict(
                    SessionExpectedObservationConflictReason
                        .ObservationAddressMismatch,
                    request.ExpectedSelectedHead,
                    observationAddress,
                    proofDiagnostics
                );
            }
            if (!string.Equals(
                    observationContent,
                    request.ExactObservationContent,
                    StringComparison.Ordinal)) {
                return new SessionExpectedObservationTurnReadResult.Conflict(
                    SessionExpectedObservationConflictReason
                        .ObservationContentMismatch,
                    request.ExpectedSelectedHead,
                    observationAddress,
                    proofDiagnostics
                );
            }

            var evidence = new SessionExpectedObservationTurnEvidence(
                request.ExpectedSelectedHead,
                boundary,
                observationAddress.Value,
                observationParent.Value,
                proofDiagnostics
            );
            if (terminalAction is null) {
                if (recovery.State.Phase == SessionExecutionPhase.Idle) {
                    throw new InvalidDataException(
                        "An idle expected Observation turn has no terminal Action."
                    );
                }
                return new SessionExpectedObservationTurnReadResult
                    .InProgress(evidence);
            }
            if (recovery.State.Phase != SessionExecutionPhase.Idle) {
                throw new InvalidDataException(
                    "A non-idle expected Observation turn resolved a terminal Action."
                );
            }
            return new SessionExpectedObservationTurnReadResult.Terminal(
                evidence,
                terminalAction
            );
        }
        catch (SessionCompletedTurnsLimitException limit) {
            observedHead = _journal.GetHead(_branchRefId);
            return observedHead != request.ExpectedSelectedHead
                ? new SessionExpectedObservationTurnReadResult.Retryable(
                    request.ExpectedSelectedHead,
                    observedHead
                )
                : new SessionExpectedObservationTurnReadResult.LimitExceeded(
                    limit.Limit
                );
        }
        catch (SessionCompletedTurnsUnsupportedSchemaException schema) {
            observedHead = _journal.GetHead(_branchRefId);
            return observedHead != request.ExpectedSelectedHead
                ? new SessionExpectedObservationTurnReadResult.Retryable(
                    request.ExpectedSelectedHead,
                    observedHead
                )
                : new SessionExpectedObservationTurnReadResult
                    .UnsupportedSchema(schema.Message);
        }
        catch (NotSupportedException schema) {
            observedHead = _journal.GetHead(_branchRefId);
            return observedHead != request.ExpectedSelectedHead
                ? new SessionExpectedObservationTurnReadResult.Retryable(
                    request.ExpectedSelectedHead,
                    observedHead
                )
                : new SessionExpectedObservationTurnReadResult
                    .UnsupportedSchema(schema.Message);
        }
        catch (InvalidDataException corruption) {
            observedHead = _journal.GetHead(_branchRefId);
            return observedHead != request.ExpectedSelectedHead
                ? new SessionExpectedObservationTurnReadResult.Retryable(
                    request.ExpectedSelectedHead,
                    observedHead
                )
                : new SessionExpectedObservationTurnReadResult.Corruption(
                    corruption.Message
                );
        }
    }

    private static void ValidateExpectedObservationTurnRequest(
        SessionExpectedObservationTurnRequest request
    ) {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ExpectedSelectedHead == default) {
            throw new ArgumentException(
                "ExpectedSelectedHead cannot be default.",
                nameof(request)
            );
        }
        if (request.FreshBaseHead == default) {
            throw new ArgumentException(
                "FreshBaseHead cannot be default.",
                nameof(request)
            );
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(
            request.ExactObservationContent
        );
        if (request.ExpectedObservationAddress is { } expectedObservation
            && expectedObservation == default) {
            throw new ArgumentException(
                "ExpectedObservationAddress cannot be default.",
                nameof(request)
            );
        }
    }

    private static SessionExpectedObservationTurnDiagnostics
        ExpectedObservationDiagnostics(
        SessionCompletedTurnsReadBudget budget
    ) => new(
        budget.HeaderVisits,
        budget.DecodedLogicalPayloadBytes
    );

    private (EventAddress? Parent, string Content)
        ReadExactObservationAt(EventAddress address) {
        using SessionJournalEventFrame frame =
            _reader.ReadEvent(address).Unwrap();
        ValidateSessionHeaderPreview(address, frame.Header);
        var kind = (SessionEventKind)frame.Header.OpaqueEventKind;
        if (kind != SessionEventKind.ObservationAccepted) {
            throw new InvalidDataException(
                $"Expected ObservationAccepted at '{address}', got '{kind}'."
            );
        }
        object body = SessionEventCodec.Decode(kind, frame.Payload, out _);
        if (body is not ObservationAcceptedBody observation) {
            throw new InvalidDataException(
                $"ObservationAccepted at '{address}' decoded to an unexpected body."
            );
        }
        return (frame.Header.Parent, observation.Content);
    }

    private SessionCompletedTurnsSnapshot
        ReadRecentCompletedTurnsSnapshotAt(
        EventAddress capturedHead,
        int maximumCount,
        CancellationToken cancellationToken
    ) {
        if (maximumCount == 0) {
            return new SessionCompletedTurnsSnapshot(
                capturedHead,
                Array.Empty<SessionCompletedTurnProjection>(),
                DerivedContextNthPrevious: null
            );
        }
        CompletedTurnLocationSnapshot located = LocateCompletedTurns(
            capturedHead,
            maximumCount,
            cancellationToken
        );
        IReadOnlyList<CompletedTurnLocation> turns = located.CompletedTurns;
        if (turns.Count == 0) {
            return new SessionCompletedTurnsSnapshot(
                capturedHead,
                Array.Empty<SessionCompletedTurnProjection>(),
                located.DerivedContextNthPrevious
            );
        }

        int count = Math.Min(maximumCount, turns.Count);
        var newestFirst = new SessionCompletedTurnProjection[count];
        for (int index = 0; index < count; index++) {
            newestFirst[index] = turns[turns.Count - 1 - index].Projection;
        }
        return new SessionCompletedTurnsSnapshot(
            capturedHead,
            Array.AsReadOnly(newestFirst),
            located.DerivedContextNthPrevious
        );
    }

    /// <summary>
    /// Moves the selected branch from an exact <see cref="SessionExecutionPhase.TurnFailed"/> head
    /// to the predecessor of that visible turn's observation. Raw event bytes are retained.
    /// </summary>
    public SessionTurnRetractionResult AbandonFailedTurn(
        EventAddress expectedHead,
        CancellationToken cancellationToken = default
    ) => AbandonFailedTurn(
        expectedHead,
        new SessionCompletedTurnsReadBudget(),
        cancellationToken
    );

    internal SessionTurnRetractionResult AbandonFailedTurn(
        EventAddress expectedHead,
        SessionCompletedTurnsReadBudget budget,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(budget);
        try {
            return AbandonFailedTurnCore(
                expectedHead,
                budget,
                cancellationToken
            );
        }
        catch (SessionCompletedTurnsLimitException limit) {
            throw new InvalidOperationException(
                $"Failed-turn abandonment exceeded '{limit.Limit}'."
            );
        }
        catch (SessionCompletedTurnsUnsupportedSchemaException schema) {
            throw new NotSupportedException(schema.Message);
        }
    }

    private SessionTurnRetractionResult AbandonFailedTurnCore(
        EventAddress expectedHead,
        SessionCompletedTurnsReadBudget budget,
        CancellationToken cancellationToken
    ) {
        using MutationLease mutation = EnterMutation(
            nameof(AbandonFailedTurn)
        );
        ThrowIfReadOnlyMutation(nameof(AbandonFailedTurn));
        cancellationToken.ThrowIfCancellationRequested();
        ValidateRetractionHead(expectedHead, nameof(expectedHead));

        EventAddress? observedHead = _journal.GetHead(_branchRefId);
        if (observedHead != expectedHead) {
            return new SessionTurnRetractionResult.Retryable(
                expectedHead,
                observedHead
            );
        }
        using IDisposable scope =
            EnterCompletedTurnsReadBudget(budget);
        SessionExecutionRecovery recovery = ResolveExecutionTail(
            expectedHead,
            cancellationToken
        );
        if (recovery.State.Phase != SessionExecutionPhase.TurnFailed
            || recovery.State.HeadKind
                != SessionEventKind.CompletionAttemptFailed) {
            return new SessionTurnRetractionResult.Unavailable(
                new SessionExecutionBoundaryInspection(
                    expectedHead,
                    recovery.State.Phase,
                    recovery.State.HeadKind
                )
            );
        }

        CompletedTurnLocationSnapshot located = LocateTurnsThrough(
            expectedHead,
            maximumCount: 1,
            cancellationToken
        );
        OpenTurnLocation openTurn = located.OpenTurn
            ?? throw new InvalidDataException(
                $"TurnFailed at '{expectedHead}' has no visible source observation."
            );
        return TryAbandonLocatedFailedTurn(
            expectedHead,
            openTurn,
            cancellationToken
        );
    }

    /// <summary>
    /// Moves the selected branch only when <paramref name="expectedHead"/> is itself the terminal
    /// no-tool Action of the latest completed visible turn. Raw event bytes are retained.
    /// </summary>
    public SessionTurnRetractionResult RewindLatestCompletedTurn(
        EventAddress expectedHead,
        CancellationToken cancellationToken = default
    ) {
        using MutationLease mutation = EnterMutation(
            nameof(RewindLatestCompletedTurn)
        );
        ThrowIfReadOnlyMutation(nameof(RewindLatestCompletedTurn));
        SessionCompletedTurnRewindPrepareResult prepared =
            PrepareLatestCompletedTurnRewind(
                expectedHead,
                cancellationToken
            );
        return prepared switch {
            SessionCompletedTurnRewindPrepareResult.Prepared ready =>
                CommitPreparedCompletedTurnRewindCore(
                    ready.Value,
                    cancellationToken
                ),
            SessionCompletedTurnRewindPrepareResult.Unavailable unavailable =>
                new SessionTurnRetractionResult.Unavailable(
                    unavailable.Boundary
                ),
            SessionCompletedTurnRewindPrepareResult.Retryable retryable =>
                new SessionTurnRetractionResult.Retryable(
                    retryable.ExpectedHead,
                    retryable.ObservedHead
                ),
            SessionCompletedTurnRewindPrepareResult.LimitExceeded limit =>
                throw new InvalidOperationException(
                    $"Completed-turn rewind exceeded '{limit.Limit}'."
                ),
            SessionCompletedTurnRewindPrepareResult.UnsupportedSchema schema =>
                throw new NotSupportedException(schema.Detail),
            SessionCompletedTurnRewindPrepareResult.Corruption corruption =>
                throw new InvalidDataException(corruption.Detail),
            _ => throw new InvalidDataException(
                "Unknown completed-turn rewind preparation result."
            )
        };
    }

    public SessionCompletedTurnRewindPrepareResult
        PrepareLatestCompletedTurnRewind(
        EventAddress expectedHead,
        CancellationToken cancellationToken = default
    ) => PrepareLatestCompletedTurnRewind(
        expectedHead,
        new SessionCompletedTurnsReadBudget(),
        cancellationToken
    );

    internal SessionCompletedTurnRewindPrepareResult
        PrepareLatestCompletedTurnRewind(
        EventAddress expectedHead,
        SessionCompletedTurnsReadBudget budget,
        CancellationToken cancellationToken = default
    ) {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(budget);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateRetractionHead(expectedHead, nameof(expectedHead));
        EventAddress? observedHead = _journal.GetHead(_branchRefId);
        if (observedHead != expectedHead) {
            return new SessionCompletedTurnRewindPrepareResult.Retryable(
                expectedHead,
                observedHead
            );
        }

        using IDisposable scope =
            EnterCompletedTurnsReadBudget(budget);
        try {
            SessionExecutionRecovery recovery = ResolveExecutionTail(
                expectedHead,
                cancellationToken
            );
            if (recovery.State.Phase != SessionExecutionPhase.Idle
                || recovery.State.HeadKind is not (
                    SessionEventKind.AgentActionProduced
                    or SessionEventKind.ImportedAgentAction
                )) {
                return new SessionCompletedTurnRewindPrepareResult
                    .Unavailable(
                        new SessionExecutionBoundaryInspection(
                            expectedHead,
                            recovery.State.Phase,
                            recovery.State.HeadKind
                        )
                    );
            }
            CompletedTurnLocationSnapshot located = LocateTurnsThrough(
                expectedHead,
                maximumCount: 1,
                cancellationToken
            );
            CompletedTurnLocation? latest =
                located.CompletedTurns.LastOrDefault();
            if (latest is null
                || latest.Projection.TerminalAction.Address
                    != expectedHead) {
                throw new InvalidDataException(
                    $"Exact terminal Action '{expectedHead}' was not located as the latest completed turn."
                );
            }
            SessionCompletedTurnProjection projection = latest.Projection;
            return new SessionCompletedTurnRewindPrepareResult.Prepared(
                new SessionPreparedCompletedTurnRewind(
                    Path,
                    _branchRefId,
                    expectedHead,
                    latest.ObservationPredecessor,
                    new SessionRetractedTurnProjection(
                        projection.ObservationAddress,
                        projection.ObservationContent,
                        projection.TerminalAction
                    )
                )
            );
        }
        catch (SessionCompletedTurnsLimitException limit) {
            return new SessionCompletedTurnRewindPrepareResult
                .LimitExceeded(limit.Limit);
        }
        catch (SessionCompletedTurnsUnsupportedSchemaException schema) {
            return new SessionCompletedTurnRewindPrepareResult
                .UnsupportedSchema(schema.Message);
        }
        catch (NotSupportedException schema) {
            return new SessionCompletedTurnRewindPrepareResult
                .UnsupportedSchema(schema.Message);
        }
        catch (InvalidDataException corruption) {
            return new SessionCompletedTurnRewindPrepareResult
                .Corruption(corruption.Message);
        }
    }

    public SessionTurnRetractionResult
        CommitPreparedCompletedTurnRewind(
        SessionPreparedCompletedTurnRewind prepared,
        CancellationToken cancellationToken = default
    ) {
        using MutationLease mutation = EnterMutation(
            nameof(CommitPreparedCompletedTurnRewind)
        );
        ThrowIfReadOnlyMutation(
            nameof(CommitPreparedCompletedTurnRewind)
        );
        return CommitPreparedCompletedTurnRewindCore(
            prepared,
            cancellationToken
        );
    }

    private SessionTurnRetractionResult
        CommitPreparedCompletedTurnRewindCore(
        SessionPreparedCompletedTurnRewind prepared,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(prepared);
        if (!PathsEqual(prepared.OwnerPath, Path)
            || prepared.BranchRefId != _branchRefId) {
            throw new ArgumentException(
                "Prepared completed-turn rewind does not belong to this SessionJournal branch.",
                nameof(prepared)
            );
        }
        if (!TryMoveCurrentHead(
                prepared.ExpectedHead,
                prepared.NewHead,
                cancellationToken,
                out EventAddress? observedHead
            )) {
            return new SessionTurnRetractionResult.Retryable(
                prepared.ExpectedHead,
                observedHead
            );
        }
        return new SessionTurnRetractionResult.Moved(
            prepared.ExpectedHead,
            prepared.NewHead,
            prepared.Turn
        );
    }

    private CompletedTurnLocationSnapshot LocateCompletedTurns(
        EventAddress capturedHead,
        int maximumCount,
        CancellationToken cancellationToken
    ) {
        SessionExecutionRecovery recovery = ResolveExecutionTail(
            capturedHead,
            cancellationToken
        );
        EventAddress foldHead = recovery.State.Phase
            == SessionExecutionPhase.AwaitingToolExecution
                ? ReadCurrentToolActionPredecessor(recovery)
                : capturedHead;
        return LocateTurnsThrough(
            foldHead,
            maximumCount,
            cancellationToken
        );
    }

    private CompletedTurnLocationSnapshot LocateTurnsThrough(
        EventAddress capturedHead,
        int maximumCount,
        CancellationToken cancellationToken
    ) {
        (EventAddress startExclusive, int rawEventCount) =
            LocateCompletedTurnSuffixStart(
                capturedHead,
                maximumCount,
                cancellationToken
            );
        SessionContextAnchorSetupReferences setups =
            ResolveContextAnchorSetupReferences(
                startExclusive,
                cancellationToken
            );
        SessionHistoryPlanningSeed seed = CreateHistoryPlanningSeed(
            startExclusive,
            setups,
            cancellationToken
        );
        SessionHistoryPlanningWindowReadResult read =
            ReadHistoryPlanningWindowAtBounded(
                capturedHead,
                seed,
                rawEventCount,
                cancellationToken
            );
        SessionHistoryPlanningWindow window = read switch {
            SessionHistoryPlanningWindowReadResult.Available available =>
                available.Window,
            SessionHistoryPlanningWindowReadResult.BeyondPrefix =>
                throw new InvalidDataException(
                    "A proven completed-turn suffix became unavailable before materialization."
                ),
            _ => throw new InvalidDataException(
                "Unknown bounded planning-window result."
            )
        };
        var completed = new List<CompletedTurnLocation>();
        OpenTurnLocation? open = null;
        foreach (SessionHistoryPlanningUnit unit in window.Units) {
            cancellationToken.ThrowIfCancellationRequested();
            switch (unit.Message) {
                case ToolResultsMessage:
                    // ToolResultsMessage shares the observation role in provider context,
                    // but it is protocol material inside the current visible user turn.
                    break;
                case ObservationMessage observation:
                    open = new OpenTurnLocation(
                        unit.SourceStartInclusive,
                        ReadObservationPredecessor(
                            unit.SourceStartInclusive
                        ),
                        observation.Content ?? string.Empty
                    );
                    break;
                case ActionMessage action
                    when open is not null
                         && action.ToolCalls.Count == 0:
                    completed.Add(new CompletedTurnLocation(
                        new SessionCompletedTurnProjection(
                            open.ObservationAddress,
                            open.ObservationContent,
                            new SessionTerminalActionProjection(
                                unit.SourceEndInclusive,
                                action
                            )
                        ),
                        open.ObservationPredecessor
                    ));
                    open = null;
                    break;
            }
        }
        return new CompletedTurnLocationSnapshot(
            completed.AsReadOnly(),
            open,
            window.Folded?.GoverningSetup.RuntimeConfig
                .DerivedContext.NthPrevious
                ?? throw new InvalidDataException(
                    "Completed-turn planning window has no folded governing setup."
                )
        );
    }

    private (EventAddress StartExclusive, int RawEventCount)
        LocateCompletedTurnSuffixStart(
        EventAddress capturedHead,
        int maximumCount,
        CancellationToken cancellationToken
    ) {
        int observationCount = 0;
        int scanned = 0;
        var visited = new HashSet<EventAddress>();
        EventAddress? cursor = capturedHead;
        while (cursor is { } address) {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visited.Add(address)) {
                throw new InvalidDataException(
                    $"SessionJournal Parent chain contains a cycle at {address}."
                );
            }
            EventFrameHeader header = _reader
                .ReadEventHeaderPreview(address)
                .Unwrap();
            ValidateSessionHeaderPreview(address, header);
            scanned++;
            var kind = (SessionEventKind)header.OpaqueEventKind;
            if (kind == SessionEventKind.ObservationAccepted
                && ++observationCount > maximumCount) {
                return (
                    header.Parent
                        ?? throw new InvalidDataException(
                            $"ObservationAccepted at '{address}' has no predecessor."
                        ),
                    scanned
                );
            }
            if (kind == SessionEventKind.SessionCreated) {
                return (address, scanned - 1);
            }
            cursor = header.Parent;
        }
        throw new InvalidDataException(
            "SessionJournal lineage has no SessionCreated planning boundary."
        );
    }

    private EventAddress ReadCurrentToolActionPredecessor(
        SessionExecutionRecovery recovery
    ) {
        EventAddress actionAddress = recovery.Boundary.SourceAction
            ?? throw new InvalidDataException(
                $"AwaitingToolExecution at '{recovery.Head}' has no source Action."
            );
        EventFrameHeader header = _reader
            .ReadEventHeaderPreview(actionAddress)
            .Unwrap();
        ValidateSessionHeaderPreview(actionAddress, header);
        if (!SessionOperationalSemantics.IsActionKind(
                (SessionEventKind)header.OpaqueEventKind
            )) {
            throw new InvalidDataException(
                $"AwaitingToolExecution source '{actionAddress}' is not an Action."
            );
        }
        return header.Parent
            ?? throw new InvalidDataException(
                $"Tool-calling Action at '{actionAddress}' has no predecessor."
            );
    }

    private EventAddress ReadObservationPredecessor(
        EventAddress observationAddress
    ) {
        EventFrameHeader header = _reader
            .ReadEventHeaderPreview(observationAddress)
            .Unwrap();
        ValidateSessionHeaderPreview(observationAddress, header);
        if ((SessionEventKind)header.OpaqueEventKind
            != SessionEventKind.ObservationAccepted) {
            throw new InvalidDataException(
                $"Completed-turn observation '{observationAddress}' has event kind '{header.OpaqueEventKind}'."
            );
        }
        return header.Parent
            ?? throw new InvalidDataException(
                $"ObservationAccepted at '{observationAddress}' has no predecessor."
            );
    }

    private SessionTurnRetractionResult TryAbandonLocatedFailedTurn(
        EventAddress expectedHead,
        OpenTurnLocation openTurn,
        CancellationToken cancellationToken
    ) {
        if (!TryMoveCurrentHead(
                expectedHead,
                openTurn.ObservationPredecessor,
                cancellationToken,
                out EventAddress? observedHead
            )) {
            return new SessionTurnRetractionResult.Retryable(
                expectedHead,
                observedHead
            );
        }
        return new SessionTurnRetractionResult.Moved(
            expectedHead,
            openTurn.ObservationPredecessor,
            new SessionRetractedTurnProjection(
                openTurn.ObservationAddress,
                openTurn.ObservationContent,
                TerminalAction: null
            )
        );
    }

    private bool TryMoveCurrentHead(
        EventAddress expectedHead,
        EventAddress newHead,
        CancellationToken cancellationToken,
        out EventAddress? observedHead
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        _testHooks.BeforeTurnRefMove?.Invoke(_journal);
        var move = _journal.MoveRef(
            _branchRefId,
            expectedHead,
            newHead
        );
        if (move.IsFailure
            && string.Equals(
                move.Error!.ErrorCode,
                "EventJournal.RefCasMismatch",
                StringComparison.Ordinal
            )) {
            InvalidateHeadBoundCaches();
            observedHead = _journal.GetHead(_branchRefId);
            return false;
        }
        _ = move.Unwrap();
        InvalidateHeadBoundCaches();
        observedHead = newHead;
        return true;
    }

    private void InvalidateHeadBoundCaches() {
        _governingSetupCursor = null;
        _lastGoverningSetupResolutionDiagnostics = default;
        _lastTailProjectionDiagnostics = default;
    }

    private static void ValidateMaximumCount(int maximumCount) {
        if (maximumCount < 0) {
            throw new ArgumentOutOfRangeException(
                nameof(maximumCount),
                maximumCount,
                "Completed-turn maximum count cannot be negative."
            );
        }
    }

    private static void ValidateRetractionHead(
        EventAddress expectedHead,
        string parameterName
    ) {
        if (expectedHead == default) {
            throw new ArgumentException(
                "Turn retraction head cannot be the default EventAddress.",
                parameterName
            );
        }
    }

    private IDisposable EnterCompletedTurnsReadBudget(
        SessionCompletedTurnsReadBudget budget
    ) {
        IDisposable scope =
            _reader.EnterCompletedTurnsReadBudget(budget);
        try {
            _testHooks.AfterCompletedTurnsBudgetEntered?.Invoke();
            return scope;
        }
        catch {
            scope.Dispose();
            throw;
        }
    }

    private sealed record OpenTurnLocation(
        EventAddress ObservationAddress,
        EventAddress ObservationPredecessor,
        string ObservationContent
    );

    private sealed record CompletedTurnLocation(
        SessionCompletedTurnProjection Projection,
        EventAddress ObservationPredecessor
    );

    private sealed record CompletedTurnLocationSnapshot(
        IReadOnlyList<CompletedTurnLocation> CompletedTurns,
        OpenTurnLocation? OpenTurn,
        int DerivedContextNthPrevious
    );
}
