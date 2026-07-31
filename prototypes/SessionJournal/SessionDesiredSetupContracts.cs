using Atelia.EventJournal;

namespace Atelia.SessionJournal;

/// <summary>
/// Host intent for the next new request. Schema and derived-context selection are deliberately
/// absent: reconciliation preserves those repository-owned values from the governing setup.
/// </summary>
public sealed record SessionDesiredSetup(
    string ModelId,
    string CompletionSurfaceId,
    string SystemPrompt
);

public enum SessionDesiredSetupUnavailableReason {
    Unprovisioned,
    FailedTurnMustBeAbandoned,
    ActiveTurn,
}

/// <summary>
/// Exact-head result of reconciling Host intent into raw setup events.
/// </summary>
public abstract record SessionDesiredSetupReconciliationResult {
    private SessionDesiredSetupReconciliationResult() { }

    public sealed record Ready(
        SessionGoverningSetup GoverningSetup,
        bool RuntimeConfigChanged,
        bool SystemPromptChanged
    ) : SessionDesiredSetupReconciliationResult;

    public sealed record Unavailable(
        EventAddress? CapturedHead,
        SessionExecutionPhase Phase,
        SessionDesiredSetupUnavailableReason Reason
    ) : SessionDesiredSetupReconciliationResult;

    public sealed record Retryable(
        EventAddress? ExpectedHead,
        EventAddress? ObservedHead
    ) : SessionDesiredSetupReconciliationResult;
}
