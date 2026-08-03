using Atelia.EventJournal;
using Atelia.SessionJournal.DerivedRecap.Store;

namespace Atelia.SessionJournal.DerivedRecap.Planner;

internal static class RecapMaintainerCapabilityFingerprintSyntax {
    internal static string Require(
        string value,
        string parameterName
    ) {
        const string Prefix = "sha256:";
        if (value is null
            || !value.StartsWith(Prefix, StringComparison.Ordinal)
            || value.Length != Prefix.Length + 64
            || value.AsSpan(Prefix.Length).ContainsAnyExcept(
                "0123456789abcdef"
            )) {
            throw new ArgumentException(
                "Maintainer capability fingerprint must be sha256: "
                + "followed by lowercase SHA-256 hex.",
                parameterName
            );
        }
        return value;
    }
}

public sealed record RecapBlockCatalogEntry {
    public RecapBlockCatalogEntry(
        RecapBlockId recapBlockId,
        ContextHeaderBlockPath target,
        string maintainerId,
        string maintainerCapabilityFingerprint,
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
        MaintainerCapabilityFingerprint =
            RecapMaintainerCapabilityFingerprintSyntax.Require(
                maintainerCapabilityFingerprint,
                nameof(maintainerCapabilityFingerprint)
            );
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
    public string MaintainerCapabilityFingerprint { get; }
    public int MaxContentUtf8Bytes { get; }
}

public sealed record RecapCadenceConfig {
    public RecapCadenceConfig(
        string historyUnitLoadEstimatorId,
        HistoryLoadUnit minimumRecentHistoryLoad,
        HistoryLoadUnit recapBuildIntervalHistoryLoad
    ) {
        HistoryUnitLoadEstimatorId =
            string.IsNullOrWhiteSpace(historyUnitLoadEstimatorId)
                ? throw new ArgumentException(
                    "History-unit load estimator ID cannot be empty.",
                    nameof(historyUnitLoadEstimatorId)
                )
                : historyUnitLoadEstimatorId;
        if (recapBuildIntervalHistoryLoad.Value <= 0) {
            throw new ArgumentOutOfRangeException(
                nameof(recapBuildIntervalHistoryLoad)
            );
        }
        _ = new HistoryLoadUnit(checked(
            minimumRecentHistoryLoad.Value
            + recapBuildIntervalHistoryLoad.Value
        ));

        MinimumRecentHistoryLoad = minimumRecentHistoryLoad;
        RecapBuildIntervalHistoryLoad =
            recapBuildIntervalHistoryLoad;
    }

    public string HistoryUnitLoadEstimatorId { get; }
    public HistoryLoadUnit MinimumRecentHistoryLoad { get; }
    public HistoryLoadUnit RecapBuildIntervalHistoryLoad { get; }
    public HistoryLoadUnit BuildThresholdHistoryLoad => new(checked(
        MinimumRecentHistoryLoad.Value
        + RecapBuildIntervalHistoryLoad.Value
    ));
}

public static class RecapPlanningPolicyIds {
    public const string BoundedMaintainAllV1 =
        "bounded-maintain-all-v1";
}

public sealed class RecapPlanningInputs {
    public RecapPlanningInputs(
        IReadOnlyList<RecapBlockCatalogEntry> orderedCatalog,
        RecapCadenceConfig cadence,
        IHistoryUnitLoadEstimator historyUnitLoadEstimator,
        IRecapPlanningPolicy policy
    ) {
        ArgumentNullException.ThrowIfNull(orderedCatalog);
        if (orderedCatalog.Count is < 1
            or > SessionContextContributionContract
                .MaxContributionCount) {
            throw new ArgumentOutOfRangeException(
                nameof(orderedCatalog)
            );
        }
        Cadence = cadence
            ?? throw new ArgumentNullException(nameof(cadence));
        HistoryUnitLoadEstimator = historyUnitLoadEstimator
            ?? throw new ArgumentNullException(
                nameof(historyUnitLoadEstimator)
            );
        string estimatorId = HistoryUnitLoadEstimator.Id;
        if (!string.Equals(
                estimatorId,
                cadence.HistoryUnitLoadEstimatorId,
                StringComparison.Ordinal
            )) {
            throw new ArgumentException(
                "History-unit load estimator does not match the "
                + "configured cadence identity.",
                nameof(historyUnitLoadEstimator)
            );
        }
        Policy = policy
            ?? throw new ArgumentNullException(nameof(policy));

        RecapBlockCatalogEntry[] snapshot = [.. orderedCatalog];
        var ids = new HashSet<RecapBlockId>();
        var targets = new HashSet<ContextHeaderBlockPath>();
        foreach (RecapBlockCatalogEntry entry in snapshot) {
            ArgumentNullException.ThrowIfNull(entry);
            if (!ids.Add(entry.RecapBlockId)) {
                throw new ArgumentException(
                    $"Duplicate RecapBlockId '{entry.RecapBlockId}'.",
                    nameof(orderedCatalog)
                );
            }
            if (!targets.Add(entry.Target)) {
                throw new ArgumentException(
                    "Recap catalog targets must be unique.",
                    nameof(orderedCatalog)
                );
            }
        }

        OrderedCatalog = Array.AsReadOnly(snapshot);
    }

    public IReadOnlyList<RecapBlockCatalogEntry> OrderedCatalog {
        get;
    }
    public RecapCadenceConfig Cadence { get; }
    public IHistoryUnitLoadEstimator HistoryUnitLoadEstimator {
        get;
    }
    public IRecapPlanningPolicy Policy { get; }
}

public sealed record RecapPlanningLimits {
    public RecapPlanningLimits(
        int maxRawGrowthEventCount,
        int maxRouteEndpointsPerBlock,
        int maxMaintainerCallsPerBuild,
        int maxRawEventsPerStep,
        int maxRawEventsPerBuild
    ) {
        if (maxRawGrowthEventCount <= 0) {
            throw new ArgumentOutOfRangeException(
                nameof(maxRawGrowthEventCount)
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

        MaxRawGrowthEventCount = maxRawGrowthEventCount;
        MaxRouteEndpointsPerBlock = maxRouteEndpointsPerBlock;
        MaxMaintainerCallsPerBuild = maxMaintainerCallsPerBuild;
        MaxRawEventsPerStep = maxRawEventsPerStep;
        MaxRawEventsPerBuild = maxRawEventsPerBuild;
    }

    public int MaxRawGrowthEventCount { get; }
    public int MaxRouteEndpointsPerBlock { get; }
    public int MaxMaintainerCallsPerBuild { get; }
    public int MaxRawEventsPerStep { get; }
    public int MaxRawEventsPerBuild { get; }
}

/// <summary>
/// Stable schema/code-owned safety bounds for frozen V4 recap plans. These
/// values are not operator configuration and are never read from the active
/// repo config while resuming or restoring an existing plan.
/// </summary>
public sealed record RecapProtocolHardCaps {
    public static RecapProtocolHardCaps V4 { get; } = new(
        maxRawGrowthEventCount: 512,
        maxRouteEndpointsPerBlock: 4,
        maxMaintainerCallsPerBuild: 8,
        maxRawEventsPerStep: 64,
        maxRawEventsPerBuild: 512,
        maxContentUtf8Bytes:
            SessionContextContributionContract
                .MaxContributionUtf8Bytes,
        maxCatalogEntries:
            SessionContextContributionContract.MaxContributionCount
    );

    internal RecapProtocolHardCaps(
        int maxRawGrowthEventCount,
        int maxRouteEndpointsPerBlock,
        int maxMaintainerCallsPerBuild,
        int maxRawEventsPerStep,
        int maxRawEventsPerBuild,
        int maxContentUtf8Bytes,
        int maxCatalogEntries
    ) {
        MaxRawGrowthEventCount = RequireLineageCompatibleRawGrowth(
            maxRawGrowthEventCount,
            nameof(maxRawGrowthEventCount)
        );
        MaxRouteEndpointsPerBlock = RequirePositive(
            maxRouteEndpointsPerBlock,
            nameof(maxRouteEndpointsPerBlock)
        );
        MaxMaintainerCallsPerBuild = RequirePositive(
            maxMaintainerCallsPerBuild,
            nameof(maxMaintainerCallsPerBuild)
        );
        MaxRawEventsPerStep = RequirePositive(
            maxRawEventsPerStep,
            nameof(maxRawEventsPerStep)
        );
        MaxRawEventsPerBuild = RequirePositive(
            maxRawEventsPerBuild,
            nameof(maxRawEventsPerBuild)
        );
        MaxContentUtf8Bytes = RequirePositive(
            maxContentUtf8Bytes,
            nameof(maxContentUtf8Bytes)
        );
        MaxCatalogEntries = RequirePositive(
            maxCatalogEntries,
            nameof(maxCatalogEntries)
        );
    }

    public int MaxRawGrowthEventCount { get; }
    internal int RawGrowthProofPrefixHeaderCount => checked(
        MaxRawGrowthEventCount + 1
    );
    public int MaxRouteEndpointsPerBlock { get; }
    public int MaxMaintainerCallsPerBuild { get; }
    public int MaxRawEventsPerStep { get; }
    public int MaxRawEventsPerBuild { get; }
    public int MaxContentUtf8Bytes { get; }
    public int MaxCatalogEntries { get; }

    internal void ValidatePlanningLimits(RecapPlanningLimits limits) {
        ArgumentNullException.ThrowIfNull(limits);
        RequireAtMost(
            limits.MaxRawGrowthEventCount,
            MaxRawGrowthEventCount,
            nameof(limits.MaxRawGrowthEventCount)
        );
        RequireAtMost(
            limits.MaxRouteEndpointsPerBlock,
            MaxRouteEndpointsPerBlock,
            nameof(limits.MaxRouteEndpointsPerBlock)
        );
        RequireAtMost(
            limits.MaxMaintainerCallsPerBuild,
            MaxMaintainerCallsPerBuild,
            nameof(limits.MaxMaintainerCallsPerBuild)
        );
        RequireAtMost(
            limits.MaxRawEventsPerStep,
            MaxRawEventsPerStep,
            nameof(limits.MaxRawEventsPerStep)
        );
        RequireAtMost(
            limits.MaxRawEventsPerBuild,
            MaxRawEventsPerBuild,
            nameof(limits.MaxRawEventsPerBuild)
        );
    }

    public void ValidatePlanningAuthority(
        RecapPlanningInputs inputs,
        RecapPlanningLimits limits
    ) {
        ArgumentNullException.ThrowIfNull(inputs);
        ValidatePlanningLimits(limits);
        if (inputs.OrderedCatalog.Count > MaxCatalogEntries) {
            throw new ArgumentOutOfRangeException(
                nameof(inputs),
                "Ordered catalog exceeds the protocol hard cap."
            );
        }
        if (inputs.OrderedCatalog.Any(entry =>
                entry.MaxContentUtf8Bytes > MaxContentUtf8Bytes)) {
            throw new ArgumentOutOfRangeException(
                nameof(inputs),
                "Catalog content ceiling exceeds the protocol hard cap."
            );
        }
    }

    private static int RequirePositive(int value, string name)
        => value > 0
            ? value
            : throw new ArgumentOutOfRangeException(name);

    private static int RequireLineageCompatibleRawGrowth(
        int value,
        string name
    ) {
        _ = RequirePositive(value, name);
        if (value >= DerivedRecapLineageView.MaxPrefixHeaderCount) {
            throw new ArgumentOutOfRangeException(
                name,
                value,
                "Raw-growth hard cap plus its baseline header exceeds "
                + "DerivedRecapLineageView.MaxPrefixHeaderCount "
                + $"({DerivedRecapLineageView.MaxPrefixHeaderCount})."
            );
        }
        return value;
    }

    private static void RequireAtMost(
        int value,
        int maximum,
        string name
    ) {
        if (value > maximum) {
            throw new ArgumentOutOfRangeException(
                name,
                $"Value {value} exceeds protocol hard cap {maximum}."
            );
        }
    }
}

/// <summary>
/// Content-free exact history projection used by cadence evaluation.
/// <see cref="StartExclusive"/> is an implicit replay-safe boundary with
/// zero completed units and is not repeated in
/// <see cref="ReplaySafeBoundaries"/>.
/// </summary>
public sealed class RecapHistoryWindowFacts {
    public RecapHistoryWindowFacts(
        EventAddress startExclusive,
        int totalHistoryUnitCount,
        IReadOnlyList<SessionHistoryPlanningBoundary>
            replaySafeBoundaries
    ) {
        if (startExclusive == default) {
            throw new ArgumentException(
                "History window start cannot be default.",
                nameof(startExclusive)
            );
        }
        if (totalHistoryUnitCount < 0) {
            throw new ArgumentOutOfRangeException(
                nameof(totalHistoryUnitCount)
            );
        }
        ArgumentNullException.ThrowIfNull(replaySafeBoundaries);
        StartExclusive = startExclusive;
        TotalHistoryUnitCount = totalHistoryUnitCount;
        ReplaySafeBoundaries =
            Array.AsReadOnly([.. replaySafeBoundaries]);
    }

    public EventAddress StartExclusive { get; }
    public int TotalHistoryUnitCount { get; }
    public IReadOnlyList<SessionHistoryPlanningBoundary>
        ReplaySafeBoundaries { get; }
}

/// <summary>
/// Exact content-free facts used to make a final cadence decision.
/// </summary>
public sealed class RecapSchedulingFacts {
    public RecapSchedulingFacts(
        EventAddress capturedHead,
        IReadOnlyList<SessionCurrentLineageHeader> headToRoot,
        RecapHistoryWindowFacts historyWindow,
        EventAddress cadenceBaseline,
        EventAddress? latestPublishedSetAnchor,
        RecapHistoryLoadMeasurement historyLoadMeasurement
    ) {
        if (capturedHead == default) {
            throw new ArgumentException(
                "Captured head cannot be default.",
                nameof(capturedHead)
            );
        }
        ArgumentNullException.ThrowIfNull(headToRoot);
        ArgumentNullException.ThrowIfNull(historyWindow);
        if (cadenceBaseline == default) {
            throw new ArgumentException(
                "Cadence baseline cannot be default.",
                nameof(cadenceBaseline)
            );
        }
        CapturedHead = capturedHead;
        HeadToRoot = Array.AsReadOnly([.. headToRoot]);
        HistoryWindow = historyWindow;
        CadenceBaseline = cadenceBaseline;
        LatestPublishedSetAnchor = latestPublishedSetAnchor;
        HistoryLoadMeasurement = historyLoadMeasurement
            ?? throw new ArgumentNullException(
                nameof(historyLoadMeasurement)
            );
    }

    public EventAddress CapturedHead { get; }
    public IReadOnlyList<SessionCurrentLineageHeader> HeadToRoot {
        get;
    }
    public RecapHistoryWindowFacts HistoryWindow { get; }
    public EventAddress CadenceBaseline { get; }
    public EventAddress? LatestPublishedSetAnchor { get; }
    public RecapHistoryLoadMeasurement HistoryLoadMeasurement {
        get;
    }
}

public sealed record RecapCadenceBoundary(
    EventAddress Address,
    HistoryLoadUnit AbsorbedHistoryLoad,
    HistoryLoadUnit RecentHistoryLoad
);

public sealed class RecapCadenceFacts {
    internal RecapCadenceFacts(
        EventAddress baseline,
        string historyUnitLoadEstimatorId,
        HistoryLoadUnit growthHistoryLoad,
        int growthHistoryUnitCount,
        int rawGrowthEventCount,
        IReadOnlyList<RecapCadenceBoundary> admissionCandidates
    ) {
        Baseline = baseline;
        HistoryUnitLoadEstimatorId = historyUnitLoadEstimatorId;
        GrowthHistoryLoad = growthHistoryLoad;
        GrowthHistoryUnitCount = growthHistoryUnitCount;
        RawGrowthEventCount = rawGrowthEventCount;
        AdmissionCandidates =
            Array.AsReadOnly([.. admissionCandidates]);
    }

    public EventAddress Baseline { get; }
    public string HistoryUnitLoadEstimatorId { get; }
    public HistoryLoadUnit GrowthHistoryLoad { get; }
    public int GrowthHistoryUnitCount { get; }
    public int RawGrowthEventCount { get; }
    public IReadOnlyList<RecapCadenceBoundary>
        AdmissionCandidates { get; }
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

/// <summary>
/// Header/cursor-only policy fact for one exact Published source block.
/// Recap content and frozen request payloads are deliberately absent.
/// </summary>
public sealed record RecapBlockSourceIntent(
    RecapBlockId RecapBlockId,
    RecapSourceIntent Source,
    EventAddress AbsorbedThrough
);

/// <summary>
/// Mutually exclusive first-build or exact Published source facts.
/// Shape validation remains in <see cref="RecapPlanEvaluator"/>.
/// </summary>
public sealed class RecapPolicyFacts {
    public RecapPolicyFacts(
        EventAddress? emptyReplayStartExclusive,
        IReadOnlyList<RecapBlockSourceIntent> availableSources
    ) {
        ArgumentNullException.ThrowIfNull(availableSources);
        EmptyReplayStartExclusive = emptyReplayStartExclusive;
        AvailableSources = Array.AsReadOnly([.. availableSources]);
    }

    public EventAddress? EmptyReplayStartExclusive { get; }
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

    public sealed record Unavailable : RecapPlanningPolicyDecision {
        public Unavailable(IReadOnlyList<RecapPlanDefect> defects) {
            ArgumentNullException.ThrowIfNull(defects);
            Defects = Array.AsReadOnly([.. defects]);
        }

        public IReadOnlyList<RecapPlanDefect> Defects { get; }
    }

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
    RecapPlanningInputs Inputs,
    RecapPlanningLimits Limits,
    RecapSchedulingFacts Scheduling,
    RecapCadenceFacts Cadence,
    RecapPolicyFacts PolicyFacts
);

/// <summary>
/// Pure synchronous policy seam. Policy output remains an intent and is
/// independently validated against supplied raw/source/preflight facts.
/// </summary>
public interface IRecapPlanningPolicy {
    string Id { get; }

    RecapPlanningPolicyDecision Decide(
        RecapPlanningPolicyContext context
    );
}

public sealed record RecapPlanDefect(string Code, string Detail);

public abstract record RecapSchedulingResult {
    private RecapSchedulingResult() {
    }

    public sealed record NoBuild(
        string Reason,
        RecapExactScheduleMeasurement Measurement
    )
        : RecapSchedulingResult;

    public sealed record Unavailable(
        IReadOnlyList<RecapPlanDefect> Defects,
        RecapExactScheduleMeasurement? Measurement = null
    ) : RecapSchedulingResult;

    public sealed record Ready : RecapSchedulingResult {
        internal Ready(
            RecapPlanningInputs inputs,
            RecapPlanningLimits limits,
            RecapSchedulingFacts facts,
            RecapCadenceFacts cadence
        ) {
            Inputs = inputs;
            Limits = limits;
            Facts = facts;
            Cadence = cadence;
        }

        public RecapPlanningInputs Inputs { get; }
        public RecapPlanningLimits Limits { get; }
        public RecapSchedulingFacts Facts { get; }
        public RecapCadenceFacts Cadence { get; }
    }
}

public sealed record RecapExactScheduleMeasurement(
    string HistoryUnitLoadEstimatorId,
    HistoryLoadUnit GrowthHistoryLoad,
    int GrowthHistoryUnitCount,
    int RawGrowthEventCount,
    HistoryLoadUnit? SelectedAbsorbedHistoryLoad = null,
    HistoryLoadUnit? SelectedRecentHistoryLoad = null
);

public abstract record RecapRawSafetyResult {
    private RecapRawSafetyResult() {
    }

    public sealed record Safe(int RawGrowthEventCount)
        : RecapRawSafetyResult;

    public sealed record Unavailable(
        IReadOnlyList<RecapPlanDefect> Defects,
        int? RawGrowthEventCount = null
    ) : RecapRawSafetyResult;
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
    public const string MaxRawGrowthEventCountExceeded =
        nameof(MaxRawGrowthEventCountExceeded);
    public const string CadenceBaselineInvalid =
        nameof(CadenceBaselineInvalid);
    public const string PlanningFactsInvalid =
        nameof(PlanningFactsInvalid);
    public const string PolicyDecisionInvalid =
        nameof(PolicyDecisionInvalid);
    public const string PolicyFailed = nameof(PolicyFailed);
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
    public const string BelowCadenceThreshold =
        nameof(BelowCadenceThreshold);
    public const string AwaitingReplaySafeAdmission =
        nameof(AwaitingReplaySafeAdmission);
    public const string FrozenBuildingHandled =
        nameof(FrozenBuildingHandled);
}
