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
        int maxMaintainerCallsPerBuild,
        int maxRawEventsPerStep,
        int maxRawEventsPerBuild
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
        if (maxRawEventsPerStep <= 0) {
            throw new ArgumentOutOfRangeException(
                nameof(maxRawEventsPerStep)
            );
        }
        if (maxRawEventsPerBuild <= 0) {
            throw new ArgumentOutOfRangeException(
                nameof(maxRawEventsPerBuild)
            );
        }

        RecapBlockCatalogEntry[] snapshot = [.. catalog];
        var ids = new HashSet<RecapBlockId>();
        var targets = new HashSet<ContextHeaderBlockPath>();
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
        }

        Catalog = Array.AsReadOnly(snapshot);
        RawGrowthTrigger = rawGrowthTrigger;
        RawGrowthHardLimit = rawGrowthHardLimit;
        MaxRouteEndpointsPerBlock = maxRouteEndpointsPerBlock;
        MaxMaintainerCallsPerBuild = maxMaintainerCallsPerBuild;
        MaxRawEventsPerStep = maxRawEventsPerStep;
        MaxRawEventsPerBuild = maxRawEventsPerBuild;
    }

    public IReadOnlyList<RecapBlockCatalogEntry> Catalog { get; }
    public int RawGrowthTrigger { get; }
    public int RawGrowthHardLimit { get; }
    public int MaxRouteEndpointsPerBlock { get; }
    public int MaxMaintainerCallsPerBuild { get; }
    public int MaxRawEventsPerStep { get; }
    public int MaxRawEventsPerBuild { get; }
}

/// <summary>
/// Raw-only facts used before source reads or policy execution.
/// </summary>
public sealed class RecapSchedulingFacts {
    public RecapSchedulingFacts(
        EventAddress capturedHead,
        IReadOnlyList<SessionCurrentLineageHeader> headToRoot,
        IReadOnlyList<EventAddress> replaySafeBoundaries,
        EventAddress? latestPublishedSetAnchor
    ) {
        if (capturedHead == default) {
            throw new ArgumentException(
                "Captured head cannot be default.",
                nameof(capturedHead)
            );
        }
        ArgumentNullException.ThrowIfNull(headToRoot);
        ArgumentNullException.ThrowIfNull(replaySafeBoundaries);
        CapturedHead = capturedHead;
        HeadToRoot = Array.AsReadOnly([.. headToRoot]);
        ReplaySafeBoundaries =
            Array.AsReadOnly([.. replaySafeBoundaries]);
        LatestPublishedSetAnchor = latestPublishedSetAnchor;
    }

    public EventAddress CapturedHead { get; }
    public IReadOnlyList<SessionCurrentLineageHeader> HeadToRoot {
        get;
    }
    public IReadOnlyList<EventAddress> ReplaySafeBoundaries { get; }
    public EventAddress? LatestPublishedSetAnchor { get; }
}

/// <summary>
/// A pre-freeze source choice. R1B2 must bind this exact set token to the
/// Store's full double-read frozen source snapshot before plan validation.
/// </summary>
public sealed record RecapSourceIntent {
    public RecapSourceIntent(
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
            string.IsNullOrWhiteSpace(sourcePublicationEnvelopeSha256)
                ? throw new ArgumentException(
                    "Source publication token cannot be empty.",
                    nameof(sourcePublicationEnvelopeSha256)
                )
                : sourcePublicationEnvelopeSha256;
    }

    public EventAddress SourceSetAnchor { get; }
    public string SourcePublicationEnvelopeSha256 { get; }
}

public sealed record RecapBlockSourceIntent(
    RecapBlockId RecapBlockId,
    RecapSourceIntent Source
);

public sealed class RecapPolicyFacts {
    public RecapPolicyFacts(
        IReadOnlyList<RecapBlockSourceIntent> availableSources
    ) {
        ArgumentNullException.ThrowIfNull(availableSources);
        AvailableSources = Array.AsReadOnly([.. availableSources]);
    }

    public IReadOnlyList<RecapBlockSourceIntent> AvailableSources {
        get;
    }
}

/// <summary>
/// The source cursor reproduced from the exact frozen source snapshot.
/// This is a Planner-neutral integration fact, not a Store snapshot type.
/// </summary>
public sealed record RecapSourceReplayFact(
    RecapBlockId RecapBlockId,
    RecapSourceIntent Source,
    EventAddress AbsorbedThrough
);

public sealed record RecapPlannedStepCost(
    RecapBlockId RecapBlockId,
    EventAddress StartExclusive,
    EventAddress EndInclusive,
    int RawEventCount
);

public sealed class RecapPlanPreflightFacts {
    public RecapPlanPreflightFacts(
        IReadOnlyList<RecapSourceReplayFact> sourceReplayFacts,
        IReadOnlyList<RecapPlannedStepCost> stepCosts
    ) {
        ArgumentNullException.ThrowIfNull(sourceReplayFacts);
        ArgumentNullException.ThrowIfNull(stepCosts);
        SourceReplayFacts =
            Array.AsReadOnly([.. sourceReplayFacts]);
        StepCosts = Array.AsReadOnly([.. stepCosts]);
    }

    public IReadOnlyList<RecapSourceReplayFact> SourceReplayFacts {
        get;
    }
    public IReadOnlyList<RecapPlannedStepCost> StepCosts { get; }
}

public abstract record RecapPlanningMaintainSource {
    private RecapPlanningMaintainSource() {
    }

    public sealed record Existing(RecapSourceIntent Source)
        : RecapPlanningMaintainSource;

    public sealed record Empty(EventAddress ReplayStartExclusive)
        : RecapPlanningMaintainSource;
}

public abstract record RecapBlockPlanningDecision {
    private RecapBlockPlanningDecision() {
    }

    public sealed record Inherit(
        RecapBlockId RecapBlockId,
        RecapSourceIntent Source
    ) : RecapBlockPlanningDecision;

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
    RecapSchedulingFacts Scheduling,
    RecapPolicyFacts PolicyFacts
);

/// <summary>
/// Pure synchronous policy seam. Policy output remains an intent and is
/// independently validated against supplied raw/source/preflight facts.
/// </summary>
public interface IRecapPlanningPolicy {
    RecapPlanningPolicyDecision Decide(
        RecapPlanningPolicyContext context
    );
}

public sealed record RecapPlanDefect(string Code, string Detail);

public abstract record RecapSchedulingResult {
    private RecapSchedulingResult() {
    }

    public sealed record NoBuild(string Reason)
        : RecapSchedulingResult;

    public sealed record Unavailable(
        IReadOnlyList<RecapPlanDefect> Defects
    ) : RecapSchedulingResult;

    public sealed record Ready : RecapSchedulingResult {
        internal Ready(
            RecapPlannerConfig config,
            RecapSchedulingFacts facts,
            int rawGrowth
        ) {
            Config = config;
            Facts = facts;
            RawGrowth = rawGrowth;
        }

        public RecapPlannerConfig Config { get; }
        public RecapSchedulingFacts Facts { get; }
        public int RawGrowth { get; }
    }
}

public abstract record RecapPlanIntentResult {
    private RecapPlanIntentResult() {
    }

    public sealed record NoBuild(string Reason)
        : RecapPlanIntentResult;

    public sealed record Unavailable(
        IReadOnlyList<RecapPlanDefect> Defects
    ) : RecapPlanIntentResult;

    public sealed record IntentReady : RecapPlanIntentResult {
        internal IntentReady(
            RecapSchedulingResult.Ready schedule,
            RecapPlanningPolicyDecision.Build intent
        ) {
            Schedule = schedule;
            Intent = intent;
        }

        public RecapSchedulingResult.Ready Schedule { get; }
        public RecapPlanningPolicyDecision.Build Intent { get; }
    }
}

public abstract record RecapPlanResult {
    private RecapPlanResult() {
    }

    public sealed record Unavailable(
        IReadOnlyList<RecapPlanDefect> Defects
    ) : RecapPlanResult;

    public sealed record PlanReady : RecapPlanResult {
        internal PlanReady(
            RecapSchedulingResult.Ready schedule,
            RecapPlanningPolicyDecision.Build intent,
            RecapPlanPreflightFacts preflight
        ) {
            Schedule = schedule;
            Intent = intent;
            Preflight = preflight;
        }

        public RecapSchedulingResult.Ready Schedule { get; }
        public RecapPlanningPolicyDecision.Build Intent { get; }
        public RecapPlanPreflightFacts Preflight { get; }
    }
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
    public const string RawStepLimitExceeded =
        nameof(RawStepLimitExceeded);
    public const string RawBuildLimitExceeded =
        nameof(RawBuildLimitExceeded);
}

public static class RecapPlanReasons {
    public const string BelowRawGrowthTrigger =
        nameof(BelowRawGrowthTrigger);
}
