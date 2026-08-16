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
                    Array.Empty<SessionCompletedTurnProjection>()
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
            _reader.EnterCompletedTurnsReadBudget(budget);
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

    private SessionCompletedTurnsSnapshot
        ReadRecentCompletedTurnsSnapshotAt(
        EventAddress capturedHead,
        int maximumCount,
        CancellationToken cancellationToken
    ) {
        if (maximumCount == 0) {
            return new SessionCompletedTurnsSnapshot(
                capturedHead,
                Array.Empty<SessionCompletedTurnProjection>()
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
                Array.Empty<SessionCompletedTurnProjection>()
            );
        }

        int count = Math.Min(maximumCount, turns.Count);
        var newestFirst = new SessionCompletedTurnProjection[count];
        for (int index = 0; index < count; index++) {
            newestFirst[index] = turns[turns.Count - 1 - index].Projection;
        }
        return new SessionCompletedTurnsSnapshot(
            capturedHead,
            Array.AsReadOnly(newestFirst)
        );
    }

    /// <summary>
    /// Moves the selected branch from an exact <see cref="SessionExecutionPhase.TurnFailed"/> head
    /// to the predecessor of that visible turn's observation. Raw event bytes are retained.
    /// </summary>
    public SessionTurnRetractionResult AbandonFailedTurn(
        EventAddress expectedHead,
        CancellationToken cancellationToken = default
    ) {
        try {
            return AbandonFailedTurnCore(
                expectedHead,
                cancellationToken
            );
        }
        catch (SessionCompletedTurnsLimitException limit) {
            throw new InvalidOperationException(
                $"Failed-turn abandonment exceeded '{limit.Limit}'.",
                limit
            );
        }
        catch (SessionCompletedTurnsUnsupportedSchemaException schema) {
            throw new NotSupportedException(schema.Message, schema);
        }
    }

    private SessionTurnRetractionResult AbandonFailedTurnCore(
        EventAddress expectedHead,
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
            _reader.EnterCompletedTurnsReadBudget(
                new SessionCompletedTurnsReadBudget()
            );
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
            _reader.EnterCompletedTurnsReadBudget(budget);
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
            open
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
        OpenTurnLocation? OpenTurn
    );
}
