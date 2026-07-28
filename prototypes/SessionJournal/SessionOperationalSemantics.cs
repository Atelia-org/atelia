using Atelia.EventJournal;

namespace Atelia.SessionJournal;

/// <summary>
/// Pure, context-free classification and identity rules shared by operational
/// replay consumers. This layer performs no IO, traversal, or materialization.
/// </summary>
internal static class SessionOperationalSemantics {
    internal static bool IsSetupKind(SessionEventKind kind) =>
        kind is (
            SessionEventKind.RuntimeConfigSetup
            or SessionEventKind.SystemPromptSetup
        );

    internal static bool IsActionKind(SessionEventKind kind) =>
        kind is (
            SessionEventKind.AgentActionProduced
            or SessionEventKind.ImportedAgentAction
        );

    internal static bool IsToolSegmentKind(SessionEventKind kind) =>
        kind is (
            SessionEventKind.ToolExecutionStarted
            or SessionEventKind.ToolResultObserved
        );

    internal static bool IsReplaySafePhase(SessionExecutionPhase phase) =>
        phase is (
            SessionExecutionPhase.Empty
            or SessionExecutionPhase.Idle
            or SessionExecutionPhase.AwaitingAgentAction
            or SessionExecutionPhase.TurnFailed
        );

    internal static bool IsIdleOrFailedPhase(
        SessionExecutionPhase phase
    ) =>
        phase is (
            SessionExecutionPhase.Idle
            or SessionExecutionPhase.TurnFailed
        );

    internal static bool IsPreparedOrAttemptPhase(
        SessionExecutionPhase phase
    ) =>
        phase is (
            SessionExecutionPhase.AwaitingCompletionDispatch
            or SessionExecutionPhase.AwaitingCompletion
        );

    internal static string BuildObservationCorrelationId(
        EventAddress observationAddress
    ) =>
        $"atelia.session-journal.turn.v1:"
        + EventAddressTextCodec.Format(observationAddress);
}
