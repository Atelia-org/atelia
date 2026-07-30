using System.Reflection;
using Atelia.Data;
using Atelia.EventJournal;
using Atelia.SessionJournal.DerivedRecap.Store;
using Xunit;

namespace Atelia.SessionJournal.DerivedRecap.Planner.Tests;

public sealed class RecapPlanEvaluatorTests {
    [Fact]
    public void BelowTriggerStopsBeforeSourceOrPolicyPhase() {
        TestModel model = TestModel.Create(
            trigger: 2,
            hardLimit: 10
        );

        RecapSchedulingResult result =
            RecapPlanEvaluator.EvaluateSchedule(
                model.Config,
                model.Scheduling
            );

        var noBuild =
            Assert.IsType<RecapSchedulingResult.NoBuild>(result);
        Assert.Equal(
            RecapPlanReasons.BelowRawGrowthTrigger,
            noBuild.Reason
        );
    }

    [Fact]
    public void RawGrowthIsDerivedFromContiguousLatestAnchorIndex() {
        TestModel model = TestModel.Create(
            trigger: 0,
            hardLimit: 10
        );

        var ready = Assert.IsType<RecapSchedulingResult.Ready>(
            RecapPlanEvaluator.EvaluateSchedule(
                model.Config,
                model.Scheduling
            )
        );

        Assert.Equal(1, ready.RawGrowth);
    }

    [Fact]
    public void RawHardLimitStopsBeforePolicyPhase() {
        TestModel model = TestModel.Create(
            trigger: 0,
            hardLimit: 0
        );

        RecapSchedulingResult result =
            RecapPlanEvaluator.EvaluateSchedule(
                model.Config,
                model.Scheduling
            );

        AssertDefect(
            result,
            RecapPlanDefectCodes.RawGrowthHardLimitExceeded
        );
    }

    [Fact]
    public void PlanReadyCannotBeConstructedOutsideEvaluator() {
        ConstructorInfo[] constructors =
            typeof(RecapPlanResult.PlanReady).GetConstructors(
                BindingFlags.Instance | BindingFlags.Public
            );

        Assert.Empty(constructors);
    }

    [Fact]
    public void NullFirstLineageNodeReturnsUnavailable() {
        TestModel model = TestModel.Create();
        var malformed = new RecapSchedulingFacts(
            model.Admission,
            [null!],
            [],
            null
        );

        RecapSchedulingResult result =
            RecapPlanEvaluator.EvaluateSchedule(
                model.Config,
                malformed
            );

        AssertDefect(
            result,
            RecapPlanDefectCodes.PlanningFactsInvalid
        );
    }

    [Fact]
    public void DuplicateCatalogIdentityOrTargetIsRejected() {
        TestModel model = TestModel.Create();
        RecapBlockCatalogEntry entry = model.Config.Catalog[0];

        Assert.Throws<ArgumentException>(() =>
            model.NewConfig([entry, entry with { }])
        );
        Assert.Throws<ArgumentException>(() =>
            model.NewConfig([
                entry,
                new RecapBlockCatalogEntry(
                    new RecapBlockId("other"),
                    entry.Target,
                    "other-maintainer",
                    1024
                )
            ])
        );
    }

    [Fact]
    public void MaintainerIdMayBeSharedAcrossDistinctCatalogEntries() {
        TestModel model = TestModel.Create();
        RecapBlockCatalogEntry first = model.Config.Catalog[0];

        RecapPlannerConfig config = model.NewConfig([
            first,
            new RecapBlockCatalogEntry(
                new RecapBlockId("roleplay.self"),
                new ContextHeaderBlockPath(
                    ContextHeaderCarrier.System,
                    "roleplay.self"
                ),
                first.MaintainerId,
                1024
            )
        ]);

        Assert.Equal(2, config.Catalog.Count);
    }

    [Fact]
    public void DuplicateOrConflictingSourceFactsAreRejected() {
        TestModel model = TestModel.Create();
        RecapSchedulingResult.Ready schedule = model.Schedule();
        var conflicting = new RecapPolicyFacts(
            emptyReplayStartExclusive: null,
            [
                model.AvailableSource,
                model.AvailableSource with {
                    Source = new RecapSourceIntent(
                        model.SourceSet,
                        new string('b', 64)
                    )
                }
            ]
        );
        var policy = new StubPolicy(model.ValidMaintainIntent());

        RecapPlanIntentResult result =
            RecapPlanEvaluator.EvaluateIntent(
                schedule,
                conflicting,
                policy
            );

        AssertDefect(
            result,
            RecapPlanDefectCodes.PlanningFactsInvalid
        );
        Assert.Equal(0, policy.CallCount);
    }

    [Fact]
    public void MissingCatalogSourceOrNewerCursorIsRejected() {
        TestModel twoBlocks = TestModel.Create(twoBlocks: true);
        var missing = new RecapPolicyFacts(
            emptyReplayStartExclusive: null,
            [twoBlocks.AvailableSource]
        );
        var missingPolicy = new StubPolicy(
            twoBlocks.ValidMaintainIntent()
        );

        RecapPlanIntentResult missingResult =
            RecapPlanEvaluator.EvaluateIntent(
                twoBlocks.Schedule(),
                missing,
                missingPolicy
            );

        TestModel oneBlock = TestModel.Create();
        var newerCursor = new RecapPolicyFacts(
            emptyReplayStartExclusive: null,
            [
                oneBlock.AvailableSource with {
                    AbsorbedThrough = oneBlock.Admission
                }
            ]
        );
        var cursorPolicy = new StubPolicy(
            oneBlock.ValidMaintainIntent()
        );
        RecapPlanIntentResult cursorResult =
            RecapPlanEvaluator.EvaluateIntent(
                oneBlock.Schedule(),
                newerCursor,
                cursorPolicy
            );

        AssertDefect(
            missingResult,
            RecapPlanDefectCodes.PlanningFactsInvalid
        );
        AssertDefect(
            cursorResult,
            RecapPlanDefectCodes.PlanningFactsInvalid
        );
        Assert.Equal(0, missingPolicy.CallCount);
        Assert.Equal(0, cursorPolicy.CallCount);
    }

    [Fact]
    public void FirstBuildAndExistingSourceFactsCannotBeMixed() {
        TestModel model = TestModel.Create();
        var mixed = new RecapPolicyFacts(
            model.A1,
            [model.AvailableSource]
        );
        var policy = new StubPolicy(model.ValidMaintainIntent());

        RecapPlanIntentResult result =
            RecapPlanEvaluator.EvaluateIntent(
                model.Schedule(),
                mixed,
                policy
            );

        AssertDefect(
            result,
            RecapPlanDefectCodes.PlanningFactsInvalid
        );
        Assert.Equal(0, policy.CallCount);
    }

    [Fact]
    public void PolicyUnavailableDefectsAreMappedWithoutNoBuild() {
        TestModel model = TestModel.Create();
        var policy = new StubPolicy(
            new RecapPlanningPolicyDecision.Unavailable([
                new RecapPlanDefect(
                    RecapPlanDefectCodes.RawBuildLimitExceeded,
                    "bounded policy cannot admit a set"
                )
            ])
        );

        RecapPlanIntentResult result =
            RecapPlanEvaluator.EvaluateIntent(
                model.Schedule(),
                model.PolicyFacts(),
                policy
            );

        AssertDefect(
            result,
            RecapPlanDefectCodes.RawBuildLimitExceeded
        );
        Assert.IsNotType<RecapPlanIntentResult.NoBuild>(result);
    }

    [Fact]
    public void InheritAndMaintainAreExplicitIntents() {
        TestModel model = TestModel.Create(twoBlocks: true);
        RecapSourceIntent source = model.AvailableSource.Source;
        var decision = new RecapPlanningPolicyDecision.Build(
            model.Admission,
            [
                new RecapBlockPlanningDecision.Inherit(
                    model.ClientId,
                    source
                ),
                new RecapBlockPlanningDecision.Maintain(
                    model.SelfId,
                    new RecapPlanningMaintainSource.Empty(model.A1),
                    [model.A5, model.A11, model.Admission],
                    EmptyRecapPriorContext.Instance
                )
            ]
        );

        RecapPlanIntentResult result = model.EvaluateIntent(decision);

        var ready =
            Assert.IsType<RecapPlanIntentResult.IntentReady>(result);
        Assert.IsType<RecapBlockPlanningDecision.Inherit>(
            ready.Intent.Blocks[0]
        );
        Assert.IsType<RecapBlockPlanningDecision.Maintain>(
            ready.Intent.Blocks[1]
        );
    }

    [Fact]
    public void RouteMustStrictlyIncreaseAndEndAtAdmission() {
        TestModel model = TestModel.Create();

        RecapPlanIntentResult nonIncreasing = model.EvaluateIntent(
            model.MaintainIntent([
                model.A11,
                model.A5,
                model.Admission
            ])
        );
        RecapPlanIntentResult wrongFinal = model.EvaluateIntent(
            model.MaintainIntent([model.A5, model.A11])
        );

        AssertDefect(nonIncreasing, RecapPlanDefectCodes.RouteInvalid);
        AssertDefect(wrongFinal, RecapPlanDefectCodes.RouteInvalid);
    }

    [Fact]
    public void ExactSourceCursorAndInlinePriorAreValidatedPreflight() {
        TestModel model = TestModel.Create();
        RecapPlanIntentResult.IntentReady intent =
            model.IntentReady(model.ValidMaintainIntent(
                new InlineRecapPriorContext(
                    model.A5,
                    ContextHeaderSnapshot.Empty
                )
            ));

        RecapPlanResult result = RecapPlanEvaluator.ValidatePlan(
            intent,
            model.Preflight()
        );

        AssertDefect(
            result,
            RecapPlanDefectCodes.PriorContextInvalid
        );
    }

    [Fact]
    public void ExistingRouteStartsAtExactSourceCursorNotContainer() {
        TestModel model = TestModel.Create();
        RecapPlanIntentResult.IntentReady intent =
            model.IntentReady(model.ValidMaintainIntent());

        RecapPlanResult result = RecapPlanEvaluator.ValidatePlan(
            intent,
            model.Preflight()
        );

        Assert.IsType<RecapPlanResult.PlanReady>(result);
    }

    [Fact]
    public void RouteAndCallLimitsProvideIntentBackpressure() {
        TestModel routeLimited = TestModel.Create(
            maxRouteEndpoints: 2
        );
        TestModel callLimited = TestModel.Create(
            maxMaintainerCalls: 2
        );

        AssertDefect(
            routeLimited.EvaluateIntent(
                routeLimited.ValidMaintainIntent()
            ),
            RecapPlanDefectCodes.RouteLimitExceeded
        );
        AssertDefect(
            callLimited.EvaluateIntent(
                callLimited.ValidMaintainIntent()
            ),
            RecapPlanDefectCodes.CallLimitExceeded
        );
    }

    [Fact]
    public void RawStepAndBuildCostsProvidePreCallBackpressure() {
        TestModel stepLimited = TestModel.Create(
            maxRawEventsPerStep: 1,
            maxRawEventsPerBuild: 10
        );
        TestModel buildLimited = TestModel.Create(
            maxRawEventsPerStep: 10,
            maxRawEventsPerBuild: 3
        );

        RecapPlanResult step = RecapPlanEvaluator.ValidatePlan(
            stepLimited.IntentReady(
                stepLimited.ValidMaintainIntent()
            ),
            stepLimited.Preflight([2, 1, 1])
        );
        RecapPlanResult build = RecapPlanEvaluator.ValidatePlan(
            buildLimited.IntentReady(
                buildLimited.ValidMaintainIntent()
            ),
            buildLimited.Preflight([2, 1, 1])
        );

        AssertDefect(
            step,
            RecapPlanDefectCodes.RawStepLimitExceeded
        );
        AssertDefect(
            build,
            RecapPlanDefectCodes.RawBuildLimitExceeded
        );
    }

    [Fact]
    public void CatalogContentLimitUsesNeutralHardLimit() {
        TestModel model = TestModel.Create();
        RecapBlockCatalogEntry entry = model.Config.Catalog[0];

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RecapBlockCatalogEntry(
                entry.RecapBlockId,
                entry.Target,
                entry.MaintainerId,
                SessionContextContributionContract
                    .MaxContributionUtf8Bytes + 1
            )
        );
    }

    private static void AssertDefect(
        object result,
        string code
    ) {
        IReadOnlyList<RecapPlanDefect> defects = result switch {
            RecapSchedulingResult.Unavailable unavailable =>
                unavailable.Defects,
            RecapPlanIntentResult.Unavailable unavailable =>
                unavailable.Defects,
            RecapPlanResult.Unavailable unavailable =>
                unavailable.Defects,
            _ => throw new Xunit.Sdk.XunitException(
                $"Expected unavailable result, got {result.GetType().Name}."
            )
        };
        Assert.Contains(defects, defect => defect.Code == code);
    }

    private sealed class StubPolicy(
        RecapPlanningPolicyDecision decision
    ) : IRecapPlanningPolicy {
        public int CallCount { get; private set; }

        public RecapPlanningPolicyDecision Decide(
            RecapPlanningPolicyContext context
        ) {
            CallCount++;
            return decision;
        }
    }

    private sealed class TestModel {
        private const string Envelope =
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
            + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        private TestModel(
            RecapPlannerConfig config,
            RecapSchedulingFacts scheduling,
            RecapBlockSourceIntent availableSource,
            RecapBlockId clientId,
            RecapBlockId selfId,
            EventAddress admission,
            EventAddress sourceSet,
            EventAddress a11,
            EventAddress a5,
            EventAddress a1
        ) {
            Config = config;
            Scheduling = scheduling;
            AvailableSource = availableSource;
            ClientId = clientId;
            SelfId = selfId;
            Admission = admission;
            SourceSet = sourceSet;
            A11 = a11;
            A5 = a5;
            A1 = a1;
        }

        public RecapPlannerConfig Config { get; }
        public RecapSchedulingFacts Scheduling { get; }
        public RecapBlockSourceIntent AvailableSource { get; }
        public RecapBlockId ClientId { get; }
        public RecapBlockId SelfId { get; }
        public EventAddress Admission { get; }
        public EventAddress SourceSet { get; }
        public EventAddress A11 { get; }
        public EventAddress A5 { get; }
        public EventAddress A1 { get; }

        public static TestModel Create(
            int trigger = 1,
            int hardLimit = 10,
            int maxRouteEndpoints = 3,
            int maxMaintainerCalls = 3,
            int maxRawEventsPerStep = 10,
            int maxRawEventsPerBuild = 30,
            bool twoBlocks = false
        ) {
            EventAddress admission = Address(6);
            EventAddress sourceSet = Address(5);
            EventAddress a11 = Address(4);
            EventAddress a5 = Address(3);
            EventAddress a1 = Address(2);
            EventAddress root = Address(1);
            EventAddress[] addresses = [
                admission,
                sourceSet,
                a11,
                a5,
                a1,
                root
            ];
            SessionCurrentLineageHeader[] lineage = [
                .. addresses.Select((address, index) =>
                    new SessionCurrentLineageHeader(
                        address,
                        index + 1 < addresses.Length
                            ? addresses[index + 1]
                            : null,
                        SessionEventKind.ObservationAccepted
                    ))
            ];

            var clientId = new RecapBlockId("roleplay.client");
            var selfId = new RecapBlockId("roleplay.self");
            var clientTarget = new ContextHeaderBlockPath(
                ContextHeaderCarrier.System,
                "roleplay.client"
            );
            var selfTarget = new ContextHeaderBlockPath(
                ContextHeaderCarrier.System,
                "roleplay.self"
            );
            RecapBlockCatalogEntry[] catalog = twoBlocks
                ? [
                    new(
                        clientId,
                        clientTarget,
                        "roleplay.client-maintainer",
                        1024
                    ),
                    new(
                        selfId,
                        selfTarget,
                        "roleplay.self-maintainer",
                        1024
                    )
                ]
                : [
                    new(
                        clientId,
                        clientTarget,
                        "roleplay.client-maintainer",
                        1024
                    )
                ];
            var config = new RecapPlannerConfig(
                catalog,
                trigger,
                hardLimit,
                maxRouteEndpoints,
                maxMaintainerCalls,
                maxRawEventsPerStep,
                maxRawEventsPerBuild
            );
            var scheduling = new RecapSchedulingFacts(
                admission,
                lineage,
                [.. addresses],
                sourceSet
            );
            var availableSource = new RecapBlockSourceIntent(
                clientId,
                new RecapSourceIntent(sourceSet, Envelope),
                a1
            );
            return new TestModel(
                config,
                scheduling,
                availableSource,
                clientId,
                selfId,
                admission,
                sourceSet,
                a11,
                a5,
                a1
            );
        }

        public RecapPlannerConfig NewConfig(
            IReadOnlyList<RecapBlockCatalogEntry> catalog
        ) => new(
            catalog,
            Config.RawGrowthTrigger,
            Config.RawGrowthHardLimit,
            Config.MaxRouteEndpointsPerBlock,
            Config.MaxMaintainerCallsPerBuild,
            Config.MaxRawEventsPerStep,
            Config.MaxRawEventsPerBuild
        );

        public RecapSchedulingResult.Ready Schedule()
            => Assert.IsType<RecapSchedulingResult.Ready>(
                RecapPlanEvaluator.EvaluateSchedule(Config, Scheduling)
            );

        public RecapPlanningPolicyDecision.Build MaintainIntent(
            IReadOnlyList<EventAddress> route,
            RecapPriorContext? prior = null
        ) => new(
            Admission,
            [
                new RecapBlockPlanningDecision.Maintain(
                    ClientId,
                    new RecapPlanningMaintainSource.Existing(
                        AvailableSource.Source
                    ),
                    route,
                    prior ?? EmptyRecapPriorContext.Instance
                )
            ]
        );

        public RecapPlanningPolicyDecision.Build ValidMaintainIntent(
            RecapPriorContext? prior = null
        ) => MaintainIntent([A5, A11, Admission], prior);

        public RecapPlanIntentResult EvaluateIntent(
            RecapPlanningPolicyDecision decision
        ) => RecapPlanEvaluator.EvaluateIntent(
            Schedule(),
            PolicyFacts(),
            new StubPolicy(decision)
        );

        public RecapPolicyFacts PolicyFacts() => new(
            emptyReplayStartExclusive: null,
            [
                .. Config.Catalog.Select(entry =>
                    new RecapBlockSourceIntent(
                        entry.RecapBlockId,
                        AvailableSource.Source,
                        AvailableSource.AbsorbedThrough
                    ))
            ]
        );

        public RecapPlanIntentResult.IntentReady IntentReady(
            RecapPlanningPolicyDecision decision
        ) => Assert.IsType<RecapPlanIntentResult.IntentReady>(
            EvaluateIntent(decision)
        );

        public RecapPlanPreflightFacts Preflight(
            IReadOnlyList<int>? costs = null
        ) {
            IReadOnlyList<int> counts = costs ?? [1, 1, 1];
            EventAddress[] starts = [A1, A5, A11];
            EventAddress[] ends = [A5, A11, Admission];
            return new RecapPlanPreflightFacts(
                [
                    new RecapSourceReplayFact(
                        ClientId,
                        AvailableSource.Source,
                        AvailableSource.AbsorbedThrough
                    )
                ],
                [
                    .. counts.Select((count, index) =>
                        new RecapPlannedStepCost(
                            ClientId,
                            starts[index],
                            ends[index],
                            count
                        ))
                ]
            );
        }

        private static EventAddress Address(ulong value)
            => new(
                SizedPtr.FromPacked(value),
                1,
                AddressHint.None
            );
    }
}
