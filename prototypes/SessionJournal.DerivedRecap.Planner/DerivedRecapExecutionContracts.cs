using Atelia.EventJournal;
using Atelia.SessionJournal.DerivedRecap.Store;

namespace Atelia.SessionJournal.DerivedRecap.Planner;

public interface IRecapBlockMaintainerRegistry {
    bool TryResolve(
        string maintainerId,
        ContextHeaderBlockPath target,
        out IRecapBlockMaintainer maintainer
    );
}

public sealed class RecapBlockMaintainerRegistry
    : IRecapBlockMaintainerRegistry {
    private readonly IReadOnlyDictionary<
        (string Id, ContextHeaderBlockPath Target),
        IRecapBlockMaintainer
    > _maintainers;

    public RecapBlockMaintainerRegistry(
        IReadOnlyList<IRecapBlockMaintainer> maintainers
    ) {
        ArgumentNullException.ThrowIfNull(maintainers);
        var index = new Dictionary<
            (string Id, ContextHeaderBlockPath Target),
            IRecapBlockMaintainer
        >();
        foreach (IRecapBlockMaintainer? maintainer in maintainers) {
            ArgumentNullException.ThrowIfNull(maintainer);
            if (string.IsNullOrWhiteSpace(maintainer.Id)
                || maintainer.Target is null) {
                throw new ArgumentException(
                    "Maintainer Id and Target must be present.",
                    nameof(maintainers)
                );
            }
            if (!index.TryAdd(
                    (maintainer.Id, maintainer.Target),
                    maintainer
                )) {
                throw new ArgumentException(
                    "Maintainer registry contains a duplicate "
                    + $"('{maintainer.Id}', '{maintainer.Target}').",
                    nameof(maintainers)
                );
            }
        }
        _maintainers = index;
    }

    public bool TryResolve(
        string maintainerId,
        ContextHeaderBlockPath target,
        out IRecapBlockMaintainer maintainer
    ) => _maintainers.TryGetValue(
        (maintainerId, target),
        out maintainer!
    );
}

public sealed record DerivedRecapExecutionDefect(
    string Code,
    string Detail
);

/// <summary>
/// Host-pinned authority for one new-planning attempt. A healthy latest
/// Published set pins its full descriptor. An invalid exact membership may
/// pin only its anchor before Restore; the subsequent Planner read must still
/// resolve that same anchor to a healthy Selected descriptor.
/// </summary>
public sealed record DerivedRecapPlanningBaseline {
    public DerivedRecapPlanningBaseline(
        EventAddress capturedRawHead,
        EventAddress? expectedLatestAnchor,
        PublishedRecapDescriptor? expectedLatestPublished
    ) {
        if (capturedRawHead == default) {
            throw new ArgumentException(
                "Captured raw head cannot be default.",
                nameof(capturedRawHead)
            );
        }
        if (expectedLatestPublished is not null
            && expectedLatestPublished.SetAdmissionAnchor
                != expectedLatestAnchor) {
            throw new ArgumentException(
                "Exact latest Published identity must match the "
                + "expected latest anchor.",
                nameof(expectedLatestPublished)
            );
        }
        if (expectedLatestPublished is not null
            && expectedLatestAnchor is null) {
            throw new ArgumentException(
                "Exact latest Published identity requires an anchor.",
                nameof(expectedLatestAnchor)
            );
        }

        CapturedRawHead = capturedRawHead;
        ExpectedLatestAnchor = expectedLatestAnchor;
        ExpectedLatestPublished = expectedLatestPublished;
    }

    public EventAddress CapturedRawHead { get; }
    public EventAddress? ExpectedLatestAnchor { get; }
    public PublishedRecapDescriptor? ExpectedLatestPublished { get; }

    public static DerivedRecapPlanningBaseline FromSelection(
        EventAddress capturedRawHead,
        DerivedRecapSelection selection
    ) {
        ArgumentNullException.ThrowIfNull(selection);
        return selection switch {
            DerivedRecapSelection.Selected selected => new(
                capturedRawHead,
                selected.Descriptor.SetAdmissionAnchor,
                selected.Descriptor
            ),
            DerivedRecapSelection.EmptyLineage => new(
                capturedRawHead,
                expectedLatestAnchor: null,
                expectedLatestPublished: null
            ),
            DerivedRecapSelection.ExactPublishedSetInvalid invalid =>
                new(
                    capturedRawHead,
                    invalid.SetAdmissionAnchor,
                    expectedLatestPublished: null
                ),
            _ => throw new ArgumentException(
                "Planning baseline requires Selected, EmptyLineage, "
                + "or ExactPublishedSetInvalid latest selection.",
                nameof(selection)
            )
        };
    }
}

/// <summary>
/// Read-only measurement from the most recent new-planning attempt. Header
/// negatives intentionally expose no exact HistoryUnit counts.
/// </summary>
public abstract record DerivedRecapPlanningDiagnostics {
    private DerivedRecapPlanningDiagnostics() {
    }

    public sealed record HeaderNegative(
        int RawGrowthEventUpperBound
    ) : DerivedRecapPlanningDiagnostics;

    public sealed record ExactSchedule(
        int GrowthHistoryUnitCount,
        int RawGrowthEventCount
    ) : DerivedRecapPlanningDiagnostics;
}

public abstract record DerivedRecapExecutionResult {
    private DerivedRecapExecutionResult() {
    }

    public sealed record NoBuild(string Reason)
        : DerivedRecapExecutionResult;

    public sealed record Published(PublishedRecapDescriptor Descriptor)
        : DerivedRecapExecutionResult;

    public sealed record Unavailable(
        IReadOnlyList<DerivedRecapExecutionDefect> Defects
    ) : DerivedRecapExecutionResult;

    public sealed record Retryable(string Code, string Detail)
        : DerivedRecapExecutionResult;

    public sealed record BlockFailed(
        EventAddress SetAdmissionAnchor,
        RecapBlockId RecapBlockId,
        string Code,
        string Detail
    ) : DerivedRecapExecutionResult;
}

public static class DerivedRecapExecutionDefectCodes {
    public const string StoreUnavailable = nameof(StoreUnavailable);
    public const string PublishedSourceUnavailable =
        nameof(PublishedSourceUnavailable);
    public const string BuildingInvalid = nameof(BuildingInvalid);
    public const string ExecutionLimitExceeded =
        nameof(ExecutionLimitExceeded);
    public const string MaintainerUnavailable =
        nameof(MaintainerUnavailable);
    public const string RawPlanningUnavailable =
        nameof(RawPlanningUnavailable);
    public const string RawHeadChanged = nameof(RawHeadChanged);
    public const string SourceChanged = nameof(SourceChanged);
    public const string BuildingRace = nameof(BuildingRace);
    public const string ConcurrentBuildingChange =
        nameof(ConcurrentBuildingChange);
    public const string MaintainerFailed = nameof(MaintainerFailed);
    public const string MaintainerResultInvalid =
        nameof(MaintainerResultInvalid);
    public const string PublicationUnavailable =
        nameof(PublicationUnavailable);
    public const string CatalogMigrationRequired =
        nameof(CatalogMigrationRequired);
}
