using Atelia.EventJournal;
using Atelia.SessionJournal.DerivedRecap.Store;

namespace Atelia.SessionJournal.DerivedRecap.Planner;

public sealed record DerivedRecapRestoreDefect(
    string Code,
    string Detail
);

public abstract record DerivedRecapRestoreResult {
    private DerivedRecapRestoreResult() {
    }

    public sealed record Restored(PublishedRecapDescriptor Descriptor)
        : DerivedRecapRestoreResult;

    public sealed record Unavailable(
        IReadOnlyList<DerivedRecapRestoreDefect> Defects
    ) : DerivedRecapRestoreResult;

    public sealed record Retryable(string Code, string Detail)
        : DerivedRecapRestoreResult;

    public sealed record BlockFailed(
        RecapBlockId RecapBlockId,
        string Code,
        string Detail
    ) : DerivedRecapRestoreResult;
}

public static class DerivedRecapRestoreDefectCodes {
    public const string StoreUnavailable = nameof(StoreUnavailable);
    public const string FrozenPlanInvalid = nameof(FrozenPlanInvalid);
    public const string ExecutionLimitExceeded =
        nameof(ExecutionLimitExceeded);
    public const string MaintainerUnavailable =
        nameof(MaintainerUnavailable);
    public const string RawHeadChanged = nameof(RawHeadChanged);
    public const string ConcurrentPublishedChange =
        nameof(ConcurrentPublishedChange);
    public const string MaintainerFailed = nameof(MaintainerFailed);
    public const string MaintainerResultInvalid =
        nameof(MaintainerResultInvalid);
}
