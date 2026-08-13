using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid.Getter;
using Atelia.SessionJournal.RecapGrid.Manager;

namespace Atelia.SessionJournal.RecapGrid.Online;

public enum RecapGridOnlineComponent {
    RawAuthority,
    Cadence,
    Timeline,
    Control,
    Store,
    Manager,
    Getter
}

public sealed record RecapGridOnlineLimits {
    public RecapGridOnlineLimits(
        int maximumAuditEvents,
        int maximumNewCalls,
        TimeSpan softMaximumElapsed
    ) {
        if (maximumAuditEvents is < 1
            or > HistoryRecentReserveOperationLimits.MaximumRawEvents) {
            throw new ArgumentOutOfRangeException(nameof(maximumAuditEvents));
        }
        if (maximumNewCalls is < 0
            or > RecapGridLimits.MaximumColumnCount) {
            throw new ArgumentOutOfRangeException(nameof(maximumNewCalls));
        }
        if (softMaximumElapsed <= TimeSpan.Zero
            || softMaximumElapsed > TimeSpan.FromDays(1)) {
            throw new ArgumentOutOfRangeException(nameof(softMaximumElapsed));
        }
        MaximumAuditEvents = maximumAuditEvents;
        MaximumNewCalls = maximumNewCalls;
        SoftMaximumElapsed = softMaximumElapsed;
    }

    public int MaximumAuditEvents { get; }
    public int MaximumNewCalls { get; }
    /// <summary>
    /// Cooperative elapsed-time budget checked only at safe Manager
    /// boundaries; it is not a dispatch timeout or hard cancellation.
    /// </summary>
    public TimeSpan SoftMaximumElapsed { get; }

    public static RecapGridOnlineLimits Production { get; } = new(
        maximumAuditEvents:
            HistoryRecentReserveOperationLimits.MaximumRawEvents,
        maximumNewCalls: RecapGridLimits.MaximumColumnCount,
        softMaximumElapsed: TimeSpan.FromMinutes(15)
    );
}

public abstract record RecapGridOnlineOpenResult {
    private RecapGridOnlineOpenResult() { }

    public sealed record Opened(RecapGridOnlineContextHandle Handle)
        : RecapGridOnlineOpenResult;
    public sealed record Absent(RecapGridOnlineComponent Component)
        : RecapGridOnlineOpenResult;
    public sealed record Busy(RecapGridOnlineComponent Component)
        : RecapGridOnlineOpenResult;
    public sealed record UnsupportedSchema(
        RecapGridOnlineComponent Component,
        int SchemaVersion
    ) : RecapGridOnlineOpenResult;
    public sealed record DisposedRawAuthority : RecapGridOnlineOpenResult;
    public sealed record Invalid(
        RecapGridOnlineComponent Component,
        string Code,
        string Detail
    ) : RecapGridOnlineOpenResult;
}

public abstract record RecapGridOnlinePassResult {
    private RecapGridOnlinePassResult() { }

    public sealed record Ready(
        RecapGridOnlineMaintenanceEvidence? Evidence = null
    ) : RecapGridOnlinePassResult;
    public sealed record RawHistoryAuthorized(
        RecapGridOnlineMaintenanceEvidence? Evidence = null
    ) : RecapGridOnlinePassResult;
    public sealed record MaintenanceContinuation(
        RecapGridOnlineComponent Component,
        string Code,
        string Detail,
        RecapGridOnlineMaintenanceEvidence Evidence
    ) : RecapGridOnlinePassResult;
    public sealed record Backpressure(
        RecapGridOnlineComponent Component,
        string Code,
        string Detail,
        SessionCurrentLineageBeyondPrefix? BoundedLineageEvidence = null,
        RecapGridOnlineMaintenanceEvidence? MaintenanceEvidence = null
    ) : RecapGridOnlinePassResult;
    public sealed record Unavailable(
        RecapGridOnlineComponent Component,
        string Code,
        string Detail,
        RecapGridOnlineMaintenanceEvidence? MaintenanceEvidence = null
    ) : RecapGridOnlinePassResult;
    public sealed record Disposed(
        RecapGridOnlineMaintenanceEvidence? Evidence = null
    ) : RecapGridOnlinePassResult;
}

public sealed record RecapGridOnlineMaintenanceEvidence(
    int Passes,
    bool EntryDebt,
    int TimelineRowsCommitted,
    RecapGridRecipeRowCoordinate? LastAttemptedRecipeRow,
    RecapGridBuildProgressAuthority? LastAttemptedAuthority,
    int RecipeRowSteps,
    int RowViewsCommitted,
    int CellsCommitted,
    int NewCalls,
    RecapGridRecipeRowCoordinate? NextRecipeRow,
    RecapGridBuildProgressAuthority? NextAuthority,
    RecapGridOnlineContinuationKind ContinuationKind
);

public sealed record RecapGridRecipeRowCoordinate(
    HistoryRowId RowId,
    GridBuildRecipeDigest RecipeDigest
);

public enum RecapGridOnlineContinuationKind {
    Ready,
    RawHistoryAuthorized,
    GridDebtCleared,
    GridDebtRemaining,
    TimelineDebtRemaining,
    PostMutationFailure,
    CatchUpBudgetExhausted
}

public static class RecapGridOnlineCatchUpLimits {
    public const int MaximumPasses = 256;
}
