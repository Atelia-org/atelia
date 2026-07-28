using Atelia.EventJournal;

namespace Atelia.SessionJournal;

/// <summary>
/// Proves that a forward suffix fold starts from one exact, dependency-closed
/// raw boundary whose governing setup and operational state share the same
/// head.
/// </summary>
internal sealed record SessionDependencyClosedFoldSeed {
    private SessionDependencyClosedFoldSeed(
        SessionGoverningSetup governingSetup,
        SessionEventKind headKind,
        SessionExecutionPhase phase,
        long toolExecutionSequenceCheckpoint,
        string? activeCorrelationId
    ) {
        GoverningSetup = governingSetup;
        HeadKind = headKind;
        Phase = phase;
        ToolExecutionSequenceCheckpoint =
            toolExecutionSequenceCheckpoint;
        ActiveCorrelationId = activeCorrelationId;
    }

    internal SessionGoverningSetup GoverningSetup { get; }
    internal EventAddress Head => GoverningSetup.Head;
    internal SessionEventKind HeadKind { get; }
    internal SessionExecutionPhase Phase { get; }
    internal long ToolExecutionSequenceCheckpoint { get; }
    internal string? ActiveCorrelationId { get; }

    internal static SessionDependencyClosedFoldSeed Create(
        SessionGoverningSetup governingSetup,
        SessionExecutionRecovery recovery
    ) {
        ArgumentNullException.ThrowIfNull(governingSetup);
        ArgumentNullException.ThrowIfNull(recovery);
        if (recovery.Head is not EventAddress recoveryHead) {
            throw new InvalidDataException(
                "A dependency-closed fold seed requires a non-empty recovery head."
            );
        }
        if (governingSetup.Head != recoveryHead) {
            throw new InvalidDataException(
                "Fold governing setup and execution recovery must share the same head."
            );
        }
        SessionExecutionState state = recovery.State
            ?? throw new InvalidDataException(
                "A dependency-closed fold seed requires execution state."
            );
        if (state.HeadKind is not SessionEventKind headKind) {
            throw new InvalidDataException(
                "A dependency-closed fold seed requires a known head kind."
            );
        }
        if (state.ToolExecutionSequenceCheckpoint < 0) {
            throw new InvalidDataException(
                "A dependency-closed fold seed cannot have a negative tool execution checkpoint."
            );
        }
        if (state.PendingToolCall is not null
            || state.PendingOperationId is not null
            || state.PendingToolExecutionStarted
            || state.PendingRequestPreparedAddress is not null
            || state.ActiveCompletionAttemptAddress is not null
            || state.PendingToolRuntimeIdentity is not null) {
            throw new InvalidDataException(
                "A dependency-closed fold seed cannot retain pending operational state."
            );
        }
        if (!IsLegalPhaseAndHeadKind(state.Phase, headKind)) {
            throw new InvalidDataException(
                $"Execution phase '{state.Phase}' and head kind '{headKind}' "
                + "do not form a dependency-closed fold boundary."
            );
        }
        if (state.Phase == SessionExecutionPhase.AwaitingAgentAction) {
            if (string.IsNullOrWhiteSpace(state.ActiveCorrelationId)) {
                throw new InvalidDataException(
                    "An AwaitingAgentAction fold seed requires an active correlation id."
                );
            }
        }
        else if (state.ActiveCorrelationId is not null) {
            throw new InvalidDataException(
                "Only an AwaitingAgentAction fold seed may retain an active correlation id."
            );
        }

        return new SessionDependencyClosedFoldSeed(
            governingSetup,
            headKind,
            state.Phase,
            state.ToolExecutionSequenceCheckpoint,
            state.ActiveCorrelationId
        );
    }

    private static bool IsLegalPhaseAndHeadKind(
        SessionExecutionPhase phase,
        SessionEventKind headKind
    ) => phase switch {
        SessionExecutionPhase.Empty =>
            headKind == SessionEventKind.SystemPromptSetup,
        SessionExecutionPhase.Idle =>
            headKind is (
                SessionEventKind.SessionCreated
                or SessionEventKind.RuntimeConfigSetup
                or SessionEventKind.SystemPromptSetup
                or SessionEventKind.AgentActionProduced
                or SessionEventKind.ImportedAgentAction
            ),
        SessionExecutionPhase.AwaitingAgentAction =>
            headKind is (
                SessionEventKind.ObservationAccepted
                or SessionEventKind.ToolResultObserved
            ),
        SessionExecutionPhase.TurnFailed =>
            headKind == SessionEventKind.CompletionAttemptFailed,
        _ => false
    };
}
