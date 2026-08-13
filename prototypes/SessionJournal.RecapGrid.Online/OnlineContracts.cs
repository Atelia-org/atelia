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

public static class RecapGridOnlineOperationLimits {
    public const int MaximumTimelineRows = 1_000_000;
}

public sealed record RecapGridOnlineLimits {
    public RecapGridOnlineLimits(
        int maximumAuditEvents,
        int maximumTimelineRows,
        RecapGridBuildBudget buildBudget
    ) {
        if (maximumAuditEvents is < 1
            or > HistoryRecentReserveOperationLimits.MaximumRawEvents) {
            throw new ArgumentOutOfRangeException(nameof(maximumAuditEvents));
        }
        if (maximumTimelineRows is < 1
            or > RecapGridOnlineOperationLimits.MaximumTimelineRows) {
            throw new ArgumentOutOfRangeException(nameof(maximumTimelineRows));
        }
        MaximumAuditEvents = maximumAuditEvents;
        MaximumTimelineRows = maximumTimelineRows;
        BuildBudget = buildBudget
            ?? throw new ArgumentNullException(nameof(buildBudget));
    }

    public int MaximumAuditEvents { get; }
    public int MaximumTimelineRows { get; }
    public RecapGridBuildBudget BuildBudget { get; }

    public static RecapGridOnlineLimits Production { get; } = new(
        maximumAuditEvents:
            HistoryRecentReserveOperationLimits.MaximumRawEvents,
        maximumTimelineRows: 4_096,
        new RecapGridBuildBudget(
            maximumRecipeRowSteps: 262_144,
            maximumNewCalls: 4_096,
            maximumElapsed: TimeSpan.FromMinutes(15)
        )
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

    public sealed record Ready : RecapGridOnlinePassResult;
    public sealed record RawHistoryAuthorized : RecapGridOnlinePassResult;
    public sealed record Backpressure(
        RecapGridOnlineComponent Component,
        string Code,
        string Detail,
        SessionCurrentLineageBeyondPrefix? BoundedLineageEvidence = null
    ) : RecapGridOnlinePassResult;
    public sealed record Unavailable(
        RecapGridOnlineComponent Component,
        string Code,
        string Detail
    ) : RecapGridOnlinePassResult;
    public sealed record Disposed : RecapGridOnlinePassResult;
}
