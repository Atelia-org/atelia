using Atelia.EventJournal;
using Atelia.SessionJournal.DerivedRecap.Store;

namespace Atelia.SessionJournal.DerivedRecap.Planner;

public sealed record RecapBlockCatalogEntry {
    public RecapBlockCatalogEntry(
        RecapBlockId recapBlockId,
        ContextHeaderBlockPath target,
        string maintainerId,
        int maxContentUtf8Bytes
    ) {
        RecapBlockId = recapBlockId
            ?? throw new ArgumentNullException(nameof(recapBlockId));
        Target = target ?? throw new ArgumentNullException(nameof(target));
        MaintainerId = string.IsNullOrWhiteSpace(maintainerId)
            ? throw new ArgumentException(
                "MaintainerId cannot be empty.",
                nameof(maintainerId)
            )
            : maintainerId;
        if (maxContentUtf8Bytes <= 0
            || maxContentUtf8Bytes
                > SessionContextContributionContract
                    .MaxContributionUtf8Bytes) {
            throw new ArgumentOutOfRangeException(
                nameof(maxContentUtf8Bytes)
            );
        }
        MaxContentUtf8Bytes = maxContentUtf8Bytes;
    }

    public RecapBlockId RecapBlockId { get; }
    public ContextHeaderBlockPath Target { get; }
    public string MaintainerId { get; }
    public int MaxContentUtf8Bytes { get; }
}

public sealed class RecapPlannerConfig {
    public RecapPlannerConfig(
        IReadOnlyList<RecapBlockCatalogEntry> catalog,
        int rawGrowthTrigger,
        int rawGrowthHardLimit,
        int maxRouteEndpointsPerBlock,
        int maxMaintainerCallsPerBuild
    ) {
        ArgumentNullException.ThrowIfNull(catalog);
        if (catalog.Count is < 1
            or > SessionContextContributionContract
                .MaxContributionCount) {
            throw new ArgumentOutOfRangeException(nameof(catalog));
        }
        if (rawGrowthTrigger < 0) {
            throw new ArgumentOutOfRangeException(
                nameof(rawGrowthTrigger)
            );
        }
        if (rawGrowthHardLimit < rawGrowthTrigger) {
            throw new ArgumentOutOfRangeException(
                nameof(rawGrowthHardLimit)
            );
        }
        if (maxRouteEndpointsPerBlock <= 0) {
            throw new ArgumentOutOfRangeException(
                nameof(maxRouteEndpointsPerBlock)
            );
        }
        if (maxMaintainerCallsPerBuild <= 0) {
            throw new ArgumentOutOfRangeException(
                nameof(maxMaintainerCallsPerBuild)
            );
        }

        RecapBlockCatalogEntry[] snapshot = [.. catalog];
        var ids = new HashSet<RecapBlockId>();
        var targets = new HashSet<ContextHeaderBlockPath>();
        var maintainers = new HashSet<string>(StringComparer.Ordinal);
        foreach (RecapBlockCatalogEntry entry in snapshot) {
            ArgumentNullException.ThrowIfNull(entry);
            if (!ids.Add(entry.RecapBlockId)) {
                throw new ArgumentException(
                    $"Duplicate RecapBlockId '{entry.RecapBlockId}'.",
                    nameof(catalog)
                );
            }
            if (!targets.Add(entry.Target)) {
                throw new ArgumentException(
                    "Recap catalog targets must be unique.",
                    nameof(catalog)
                );
            }
            if (!maintainers.Add(entry.MaintainerId)) {
                throw new ArgumentException(
                    "Recap catalog MaintainerIds must be unique.",
                    nameof(catalog)
                );
            }
        }

        Catalog = Array.AsReadOnly(snapshot);
        RawGrowthTrigger = rawGrowthTrigger;
        RawGrowthHardLimit = rawGrowthHardLimit;
        MaxRouteEndpointsPerBlock = maxRouteEndpointsPerBlock;
        MaxMaintainerCallsPerBuild = maxMaintainerCallsPerBuild;
    }

    public IReadOnlyList<RecapBlockCatalogEntry> Catalog { get; }
    public int RawGrowthTrigger { get; }
    public int RawGrowthHardLimit { get; }
    public int MaxRouteEndpointsPerBlock { get; }
    public int MaxMaintainerCallsPerBuild { get; }
}

public sealed record RecapPublishedBlockFact {
    public RecapPublishedBlockFact(
        RecapBlockId recapBlockId,
        ContextHeaderBlockPath target,
        EventAddress sourceSetAnchor,
        string sourcePublicationEnvelopeSha256,
        EventAddress absorbedThrough
    ) {
        RecapBlockId = recapBlockId
            ?? throw new ArgumentNullException(nameof(recapBlockId));
        Target = target ?? throw new ArgumentNullException(nameof(target));
        if (sourceSetAnchor == default) {
            throw new ArgumentException(
                "Source set anchor cannot be default.",
                nameof(sourceSetAnchor)
            );
        }
        SourceSetAnchor = sourceSetAnchor;
        SourcePublicationEnvelopeSha256 =
            string.IsNullOrWhiteSpace(sourcePublicationEnvelopeSha256)
                ? throw new ArgumentException(
                    "Source publication token cannot be empty.",
                    nameof(sourcePublicationEnvelopeSha256)
                )
                : sourcePublicationEnvelopeSha256;
        if (absorbedThrough == default) {
            throw new ArgumentException(
                "AbsorbedThrough cannot be default.",
                nameof(absorbedThrough)
            );
        }
        AbsorbedThrough = absorbedThrough;
    }

    public RecapBlockId RecapBlockId { get; }
    public ContextHeaderBlockPath Target { get; }
    public EventAddress SourceSetAnchor { get; }
    public string SourcePublicationEnvelopeSha256 { get; }
    public EventAddress AbsorbedThrough { get; }
}

/// <summary>
/// Immutable facts captured by a future engine-bound adapter. These values
/// are validation input, not raw or Store authority by themselves.
/// </summary>
public sealed class RecapPlanningFacts {
    public RecapPlanningFacts(
        EventAddress capturedHead,
        IReadOnlyList<SessionCurrentLineageHeader> headToRoot,
        IReadOnlyList<EventAddress> replaySafeBoundaries,
        IReadOnlyList<RecapPublishedBlockFact> publishedBlocks,
        EventAddress? latestPublishedSetAnchor,
        int rawGrowth
    ) {
        if (capturedHead == default) {
            throw new ArgumentException(
                "Captured head cannot be default.",
                nameof(capturedHead)
            );
        }
        ArgumentNullException.ThrowIfNull(headToRoot);
        ArgumentNullException.ThrowIfNull(replaySafeBoundaries);
        ArgumentNullException.ThrowIfNull(publishedBlocks);
        if (rawGrowth < 0) {
            throw new ArgumentOutOfRangeException(nameof(rawGrowth));
        }

        CapturedHead = capturedHead;
        HeadToRoot = Array.AsReadOnly([.. headToRoot]);
        ReplaySafeBoundaries =
            Array.AsReadOnly([.. replaySafeBoundaries]);
        PublishedBlocks = Array.AsReadOnly([.. publishedBlocks]);
        LatestPublishedSetAnchor = latestPublishedSetAnchor;
        RawGrowth = rawGrowth;
    }

    public EventAddress CapturedHead { get; }
    public IReadOnlyList<SessionCurrentLineageHeader> HeadToRoot { get; }
    public IReadOnlyList<EventAddress> ReplaySafeBoundaries { get; }
    public IReadOnlyList<RecapPublishedBlockFact> PublishedBlocks {
        get;
    }
    public EventAddress? LatestPublishedSetAnchor { get; }
    public int RawGrowth { get; }
}

public abstract record RecapPlanningMaintainSource {
    private RecapPlanningMaintainSource() {
    }

    public sealed record Existing : RecapPlanningMaintainSource {
        public Existing(
            EventAddress sourceSetAnchor,
            string sourcePublicationEnvelopeSha256
        ) {
            if (sourceSetAnchor == default) {
                throw new ArgumentException(
                    "Source set anchor cannot be default.",
                    nameof(sourceSetAnchor)
                );
            }
            SourceSetAnchor = sourceSetAnchor;
            SourcePublicationEnvelopeSha256 =
                string.IsNullOrWhiteSpace(
                    sourcePublicationEnvelopeSha256
                )
                    ? throw new ArgumentException(
                        "Source publication token cannot be empty.",
                        nameof(sourcePublicationEnvelopeSha256)
                    )
                    : sourcePublicationEnvelopeSha256;
        }

        public EventAddress SourceSetAnchor { get; }
        public string SourcePublicationEnvelopeSha256 { get; }
    }

    public sealed record Empty : RecapPlanningMaintainSource {
        public Empty(EventAddress replayStartExclusive) {
            if (replayStartExclusive == default) {
                throw new ArgumentException(
                    "Replay start cannot be default.",
                    nameof(replayStartExclusive)
                );
            }
            ReplayStartExclusive = replayStartExclusive;
        }

        public EventAddress ReplayStartExclusive { get; }
    }
}

public abstract record RecapBlockPlanningDecision {
    private RecapBlockPlanningDecision() {
    }

    public sealed record Inherit : RecapBlockPlanningDecision {
        public Inherit(
            RecapBlockId recapBlockId,
            EventAddress sourceSetAnchor,
            string sourcePublicationEnvelopeSha256
        ) {
            RecapBlockId = recapBlockId
                ?? throw new ArgumentNullException(nameof(recapBlockId));
            var source = new RecapPlanningMaintainSource.Existing(
                sourceSetAnchor,
                sourcePublicationEnvelopeSha256
            );
            SourceSetAnchor = source.SourceSetAnchor;
            SourcePublicationEnvelopeSha256 =
                source.SourcePublicationEnvelopeSha256;
        }

        public RecapBlockId RecapBlockId { get; }
        public EventAddress SourceSetAnchor { get; }
        public string SourcePublicationEnvelopeSha256 { get; }
    }

    public sealed record Maintain : RecapBlockPlanningDecision {
        public Maintain(
            RecapBlockId recapBlockId,
            RecapPlanningMaintainSource source,
            IReadOnlyList<EventAddress> catchUpThrough,
            RecapPriorContext priorContext
        ) {
            RecapBlockId = recapBlockId
                ?? throw new ArgumentNullException(nameof(recapBlockId));
            Source = source ?? throw new ArgumentNullException(
                nameof(source)
            );
            ArgumentNullException.ThrowIfNull(catchUpThrough);
            CatchUpThrough = Array.AsReadOnly([.. catchUpThrough]);
            PriorContext = priorContext
                ?? throw new ArgumentNullException(nameof(priorContext));
        }

        public RecapBlockId RecapBlockId { get; }
        public RecapPlanningMaintainSource Source { get; }
        public IReadOnlyList<EventAddress> CatchUpThrough { get; }
        public RecapPriorContext PriorContext { get; }
    }
}

public abstract record RecapPlanningPolicyDecision {
    private RecapPlanningPolicyDecision() {
    }

    public sealed record NoBuild(string Reason)
        : RecapPlanningPolicyDecision;

    public sealed record Build : RecapPlanningPolicyDecision {
        public Build(
            EventAddress setAdmissionAnchor,
            IReadOnlyList<RecapBlockPlanningDecision> blocks
        ) {
            if (setAdmissionAnchor == default) {
                throw new ArgumentException(
                    "Admission anchor cannot be default.",
                    nameof(setAdmissionAnchor)
                );
            }
            ArgumentNullException.ThrowIfNull(blocks);
            SetAdmissionAnchor = setAdmissionAnchor;
            Blocks = Array.AsReadOnly([.. blocks]);
        }

        public EventAddress SetAdmissionAnchor { get; }
        public IReadOnlyList<RecapBlockPlanningDecision> Blocks { get; }
    }
}

public sealed record RecapPlanningPolicyContext(
    RecapPlannerConfig Config,
    RecapPlanningFacts Facts
);

/// <summary>
/// Pure synchronous policy seam. Implementations choose among supplied
/// facts; the evaluator independently validates every returned address,
/// source, mode, route, prior context, and limit.
/// </summary>
public interface IRecapPlanningPolicy {
    RecapPlanningPolicyDecision Decide(
        RecapPlanningPolicyContext context
    );
}

public sealed record RecapPlanDefect(string Code, string Detail);

public abstract record RecapPlanResult {
    private RecapPlanResult() {
    }

    public sealed record NoBuild(string Reason) : RecapPlanResult;

    public sealed record PlanReady(
        RecapPlannerConfig Config,
        EventAddress SetAdmissionAnchor,
        IReadOnlyList<RecapBlockPlanningDecision> Blocks
    ) : RecapPlanResult;

    public sealed record Unavailable(
        IReadOnlyList<RecapPlanDefect> Defects
    ) : RecapPlanResult;
}

public static class RecapPlanDefectCodes {
    public const string RawGrowthHardLimitExceeded =
        nameof(RawGrowthHardLimitExceeded);
    public const string PlanningFactsInvalid =
        nameof(PlanningFactsInvalid);
    public const string PolicyDecisionInvalid =
        nameof(PolicyDecisionInvalid);
    public const string CatalogMismatch = nameof(CatalogMismatch);
    public const string AdmissionInvalid = nameof(AdmissionInvalid);
    public const string SourceInvalid = nameof(SourceInvalid);
    public const string RouteInvalid = nameof(RouteInvalid);
    public const string PriorContextInvalid =
        nameof(PriorContextInvalid);
    public const string RouteLimitExceeded =
        nameof(RouteLimitExceeded);
    public const string CallLimitExceeded =
        nameof(CallLimitExceeded);
}

public static class RecapPlanReasons {
    public const string BelowRawGrowthTrigger =
        nameof(BelowRawGrowthTrigger);
}
