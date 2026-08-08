using Atelia.EventJournal;
using Atelia.SessionJournal.DerivedRecap.Store;

namespace Atelia.SessionJournal.DerivedRecap.Planner;

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
/// Read-only measurement from the most recent new-planning attempt.
/// </summary>
public abstract record DerivedRecapPlanningDiagnostics {
    private DerivedRecapPlanningDiagnostics() {
    }

    public sealed record FullRebuildRequired(
        DerivedRecapFullRebuildRequirement Requirement
    ) : DerivedRecapPlanningDiagnostics;

    public sealed record ExactSchedule(
        RecapExactScheduleMeasurement Measurement
    ) : DerivedRecapPlanningDiagnostics;
}

public enum DerivedRecapFullRebuildReason {
    BoundedRawAuthorityInsufficient = 1,
    RawGrowthLimitExceeded = 2
}

/// <summary>
/// Typed refusal from the bounded online path. It is evidence that an
/// operator must explicitly prepare full raw authority; it does not itself
/// start a scan, create a spool, reset Store truth, or run a Maintainer.
/// </summary>
public sealed record DerivedRecapFullRebuildRequirement {
    public DerivedRecapFullRebuildRequirement(
        EventAddress capturedRawHead,
        DerivedRecapFullRebuildReason reason,
        DerivedRecapBeyondPrefixStage stage,
        int maxRawGrowthEventCount,
        int? provenRawGrowthEventCount = null,
        SessionCurrentLineageBeyondPrefix? beyondPrefix = null
    ) {
        if (capturedRawHead == default) {
            throw new ArgumentException(
                "Full-rebuild requirement needs a captured raw head.",
                nameof(capturedRawHead)
            );
        }
        if (!Enum.IsDefined(reason)) {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }
        if (maxRawGrowthEventCount <= 0) {
            throw new ArgumentOutOfRangeException(
                nameof(maxRawGrowthEventCount)
            );
        }
        if (provenRawGrowthEventCount < 0) {
            throw new ArgumentOutOfRangeException(
                nameof(provenRawGrowthEventCount)
            );
        }
        if (stage is not (
                DerivedRecapBeyondPrefixStage.NewPlanningSourceAnchor
                or DerivedRecapBeyondPrefixStage.NewPlanningRawGrowth
                or DerivedRecapBeyondPrefixStage.NewPlanningPendingWindow
            )) {
            throw new ArgumentOutOfRangeException(
                nameof(stage),
                "Only new-planning bounded-authority failures require an explicit full rebuild."
            );
        }
        if (reason
            == DerivedRecapFullRebuildReason
                .BoundedRawAuthorityInsufficient) {
            if (beyondPrefix is null
                || provenRawGrowthEventCount is not null) {
                throw new ArgumentException(
                    "Bounded-authority rebuild requirements need only exact BeyondPrefix evidence."
                );
            }
        }
        else if (stage
                 != DerivedRecapBeyondPrefixStage.NewPlanningRawGrowth
                 || beyondPrefix is not null
                 || provenRawGrowthEventCount is not int proven
                 || proven <= maxRawGrowthEventCount) {
            throw new ArgumentException(
                "Raw-growth rebuild requirements need an exact over-limit count at the raw-growth stage."
            );
        }
        CapturedRawHead = capturedRawHead;
        Reason = reason;
        Stage = stage;
        MaxRawGrowthEventCount = maxRawGrowthEventCount;
        ProvenRawGrowthEventCount = provenRawGrowthEventCount;
        BeyondPrefix = beyondPrefix;
    }

    public EventAddress CapturedRawHead { get; }
    public DerivedRecapFullRebuildReason Reason { get; }
    public DerivedRecapBeyondPrefixStage Stage { get; }
    public int MaxRawGrowthEventCount { get; }
    public int? ProvenRawGrowthEventCount { get; }
    public SessionCurrentLineageBeyondPrefix? BeyondPrefix { get; }
}

public enum DerivedRecapBeyondPrefixStage {
    PreparationCurrentLineage,
    PreparationBuildingAdmission,
    NewPlanningSourceAnchor,
    NewPlanningRawGrowth,
    NewPlanningPendingWindow,
    ResumeBuildingAdmission,
    ResumePendingWindow,
    RestoreAdmission,
    RestorePendingWindow,
    LifecycleCandidateAdmission,
    LifecycleRecentHistory,
    Publish,
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

    public sealed record BeyondPrefix(
        DerivedRecapBeyondPrefixStage Stage,
        SessionCurrentLineageBeyondPrefix Evidence
    ) : DerivedRecapExecutionResult;

    public sealed record FullRebuildRequired(
        DerivedRecapFullRebuildRequirement Requirement
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
    public const string FullRebuildRequired =
        nameof(FullRebuildRequired);
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
