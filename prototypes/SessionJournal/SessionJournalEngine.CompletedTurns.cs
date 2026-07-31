using Atelia.Completion.Abstractions;
using Atelia.EventJournal;

namespace Atelia.SessionJournal;

public sealed partial class SessionJournalEngine {
    /// <summary>
    /// Captures the current selected-branch head and returns up to <paramref name="maximumCount"/>
    /// completed visible turns, newest first. An empty branch returns an empty snapshot.
    /// </summary>
    public SessionCompletedTurnsSnapshot ReadRecentCompletedTurns(
        int maximumCount = 32,
        CancellationToken cancellationToken = default
    ) {
        ThrowIfDisposed();
        ValidateMaximumCount(maximumCount);
        EventAddress? capturedHead = _journal.GetHead(_branchRefId);
        return capturedHead is { } head
            ? ReadRecentCompletedTurnsAt(
                head,
                maximumCount,
                cancellationToken
            )
            : new SessionCompletedTurnsSnapshot(
                CapturedHead: null,
                Array.Empty<SessionCompletedTurnProjection>()
            );
    }

    /// <summary>
    /// Returns completed visible turns at an immutable exact raw head. The head need not remain the
    /// selected branch head after capture, but it must resolve to a valid SessionJournal lineage.
    /// </summary>
    public SessionCompletedTurnsSnapshot ReadRecentCompletedTurnsAt(
        EventAddress capturedHead,
        int maximumCount = 32,
        CancellationToken cancellationToken = default
    ) {
        ThrowIfDisposed();
        if (capturedHead == default) {
            throw new ArgumentException(
                "Completed-turn projection head cannot be the default EventAddress.",
                nameof(capturedHead)
            );
        }
        ValidateMaximumCount(maximumCount);

        CompletedTurnLocationSnapshot located = LocateCompletedTurns(
            capturedHead,
            cancellationToken
        );
        IReadOnlyList<CompletedTurnLocation> turns = located.CompletedTurns;
        if (maximumCount == 0 || turns.Count == 0) {
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
        ThrowIfReadOnlyMutation(nameof(RewindLatestCompletedTurn));
        cancellationToken.ThrowIfCancellationRequested();
        ValidateRetractionHead(expectedHead, nameof(expectedHead));

        EventAddress? observedHead = _journal.GetHead(_branchRefId);
        if (observedHead != expectedHead) {
            return new SessionTurnRetractionResult.Retryable(
                expectedHead,
                observedHead
            );
        }
        SessionExecutionRecovery recovery = ResolveExecutionTail(
            expectedHead,
            cancellationToken
        );
        if (recovery.State.Phase != SessionExecutionPhase.Idle
            || recovery.State.HeadKind is not (
                SessionEventKind.AgentActionProduced
                or SessionEventKind.ImportedAgentAction
            )) {
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
            cancellationToken
        );
        CompletedTurnLocation? latest = located.CompletedTurns.Count == 0
            ? null
            : located.CompletedTurns[^1];
        if (latest is null
            || latest.Projection.TerminalAction.Address
                != expectedHead) {
            throw new InvalidDataException(
                $"Exact terminal Action '{expectedHead}' was not located as the latest completed turn."
            );
        }
        return TryRewindLocatedCompletedTurn(
            expectedHead,
            latest,
            cancellationToken
        );
    }

    private CompletedTurnLocationSnapshot LocateCompletedTurns(
        EventAddress capturedHead,
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
        return LocateTurnsThrough(foldHead, cancellationToken);
    }

    private CompletedTurnLocationSnapshot LocateTurnsThrough(
        EventAddress capturedHead,
        CancellationToken cancellationToken
    ) {
        SessionHistoryPlanningWindow window = ReadHistoryPlanningWindowAt(
            capturedHead,
            startExclusive: null,
            cancellationToken
        );
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

    private SessionTurnRetractionResult TryRewindLocatedCompletedTurn(
        EventAddress expectedHead,
        CompletedTurnLocation completedTurn,
        CancellationToken cancellationToken
    ) {
        if (!TryMoveCurrentHead(
                expectedHead,
                completedTurn.ObservationPredecessor,
                cancellationToken,
                out EventAddress? observedHead
            )) {
            return new SessionTurnRetractionResult.Retryable(
                expectedHead,
                observedHead
            );
        }
        SessionCompletedTurnProjection projection =
            completedTurn.Projection;
        return new SessionTurnRetractionResult.Moved(
            expectedHead,
            completedTurn.ObservationPredecessor,
            new SessionRetractedTurnProjection(
                projection.ObservationAddress,
                projection.ObservationContent,
                projection.TerminalAction
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
        _testHooks.BeforeTurnRefMove?.Invoke();
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
