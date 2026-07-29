using Atelia.Data;
using Atelia.EventJournal;
using Atelia.SessionJournal.DerivedRecap.Store;
using Xunit;

namespace Atelia.SessionJournal.DerivedRecap.Planner.Tests;

public sealed class RecapPlanEvaluatorTests {
    [Fact]
    public void BelowTriggerReturnsNoBuildWithoutCallingPolicy() {
        TestModel model = TestModel.Create(rawGrowth: 2);
        var policy = new StubPolicy(_ =>
            throw new InvalidOperationException("must not be called"));

        RecapPlanResult result = RecapPlanEvaluator.Evaluate(
            model.Config,
            model.Facts,
            policy
        );

        var noBuild = Assert.IsType<RecapPlanResult.NoBuild>(result);
        Assert.Equal(
            RecapPlanReasons.BelowRawGrowthTrigger,
            noBuild.Reason
        );
        Assert.Equal(0, policy.CallCount);
    }

    [Fact]
    public void RawHardLimitReturnsUnavailableWithoutCallingPolicy() {
        TestModel model = TestModel.Create(rawGrowth: 11);
        var policy = new StubPolicy(_ =>
            throw new InvalidOperationException("must not be called"));

        RecapPlanResult result = RecapPlanEvaluator.Evaluate(
            model.Config,
            model.Facts,
            policy
        );

        AssertDefect(
            result,
            RecapPlanDefectCodes.RawGrowthHardLimitExceeded
        );
        Assert.Equal(0, policy.CallCount);
    }

    [Fact]
    public void DuplicateCatalogIdentityOrTargetIsRejected() {
        TestModel model = TestModel.Create();
        RecapBlockCatalogEntry entry = model.Config.Catalog[0];

        Assert.Throws<ArgumentException>(() =>
            new RecapPlannerConfig(
                [entry, entry with { }],
                3,
                10,
                3,
                3
            )
        );
        Assert.Throws<ArgumentException>(() =>
            new RecapPlannerConfig(
                [
                    entry,
                    new RecapBlockCatalogEntry(
                        new RecapBlockId("other"),
                        entry.Target,
                        "other-maintainer",
                        1024
                    )
                ],
                3,
                10,
                3,
                3
            )
        );
    }

    [Fact]
    public void CatalogContentLimitCannotExceedNeutralContract() {
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

    [Fact]
    public void InheritAndMaintainAreExplicitValidatedDiscriminants() {
        TestModel model = TestModel.Create(twoBlocks: true);
        var inherit = new RecapBlockPlanningDecision.Inherit(
            model.ClientId,
            model.SourceSet,
            TestModel.Envelope
        );
        var maintain = new RecapBlockPlanningDecision.Maintain(
            model.SelfId,
            new RecapPlanningMaintainSource.Empty(model.A1),
            [model.A5, model.A11, model.Admission],
            EmptyRecapPriorContext.Instance
        );

        RecapPlanResult result = model.Evaluate(
            new RecapPlanningPolicyDecision.Build(
                model.Admission,
                [inherit, maintain]
            )
        );

        var ready = Assert.IsType<RecapPlanResult.PlanReady>(result);
        Assert.IsType<RecapBlockPlanningDecision.Inherit>(
            ready.Blocks[0]
        );
        Assert.IsType<RecapBlockPlanningDecision.Maintain>(
            ready.Blocks[1]
        );
    }

    [Fact]
    public void ExistingRouteStartsAtFrozenCursorNotSourceContainer() {
        TestModel model = TestModel.Create();
        var maintain = new RecapBlockPlanningDecision.Maintain(
            model.ClientId,
            new RecapPlanningMaintainSource.Existing(
                model.SourceSet,
                TestModel.Envelope
            ),
            [model.A5, model.A11, model.Admission],
            EmptyRecapPriorContext.Instance
        );

        RecapPlanResult result = model.Evaluate(
            new RecapPlanningPolicyDecision.Build(
                model.Admission,
                [maintain]
            )
        );

        Assert.IsType<RecapPlanResult.PlanReady>(result);
    }

    [Fact]
    public void RouteMustStrictlyIncreaseAndEndAtAdmission() {
        TestModel model = TestModel.Create();
        RecapPlanResult nonIncreasing = model.Evaluate(
            model.Maintain([model.A11, model.A5, model.Admission])
        );
        RecapPlanResult wrongFinal = model.Evaluate(
            model.Maintain([model.A5, model.A11])
        );

        AssertDefect(nonIncreasing, RecapPlanDefectCodes.RouteInvalid);
        AssertDefect(wrongFinal, RecapPlanDefectCodes.RouteInvalid);
    }

    [Fact]
    public void PolicyCannotInventRawAdmissionOrPublishedSourceFacts() {
        TestModel model = TestModel.Create();
        EventAddress invented = TestModel.Address(99);
        var unknownSource =
            new RecapBlockPlanningDecision.Inherit(
                model.ClientId,
                model.SourceSet,
                new string('b', 64)
            );

        RecapPlanResult result = model.Evaluate(
            new RecapPlanningPolicyDecision.Build(
                invented,
                [unknownSource]
            )
        );

        AssertDefect(result, RecapPlanDefectCodes.AdmissionInvalid);
        AssertDefect(result, RecapPlanDefectCodes.SourceInvalid);
    }

    [Fact]
    public void InlinePriorMustBeAncestorOfReplayStart() {
        TestModel model = TestModel.Create();
        var invalidPrior = new InlineRecapPriorContext(
            model.A5,
            ContextHeaderSnapshot.Empty
        );

        RecapPlanResult result = model.Evaluate(
            model.Maintain(
                [model.A5, model.A11, model.Admission],
                invalidPrior
            )
        );

        AssertDefect(
            result,
            RecapPlanDefectCodes.PriorContextInvalid
        );
    }

    [Fact]
    public void RouteAndAggregateCallLimitsProvideBackpressure() {
        TestModel routeLimited = TestModel.Create(
            maxRouteEndpoints: 2
        );
        TestModel callLimited = TestModel.Create(
            maxMaintainerCalls: 2
        );
        RecapPlanningPolicyDecision route =
            routeLimited.Maintain([
                routeLimited.A5,
                routeLimited.A11,
                routeLimited.Admission
            ]);
        RecapPlanningPolicyDecision calls =
            callLimited.Maintain([
                callLimited.A5,
                callLimited.A11,
                callLimited.Admission
            ]);

        AssertDefect(
            routeLimited.Evaluate(route),
            RecapPlanDefectCodes.RouteLimitExceeded
        );
        AssertDefect(
            callLimited.Evaluate(calls),
            RecapPlanDefectCodes.CallLimitExceeded
        );
    }

    private static void AssertDefect(
        RecapPlanResult result,
        string code
    ) {
        var unavailable =
            Assert.IsType<RecapPlanResult.Unavailable>(result);
        Assert.Contains(
            unavailable.Defects,
            defect => defect.Code == code
        );
    }

    private sealed class StubPolicy(
        Func<RecapPlanningPolicyContext, RecapPlanningPolicyDecision>
            decide
    ) : IRecapPlanningPolicy {
        public int CallCount { get; private set; }

        public RecapPlanningPolicyDecision Decide(
            RecapPlanningPolicyContext context
        ) {
            CallCount++;
            return decide(context);
        }
    }

    private sealed class TestModel {
        public const string Envelope =
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
            + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        private TestModel(
            RecapPlannerConfig config,
            RecapPlanningFacts facts,
            RecapBlockId clientId,
            RecapBlockId selfId,
            EventAddress admission,
            EventAddress sourceSet,
            EventAddress a11,
            EventAddress a5,
            EventAddress a1
        ) {
            Config = config;
            Facts = facts;
            ClientId = clientId;
            SelfId = selfId;
            Admission = admission;
            SourceSet = sourceSet;
            A11 = a11;
            A5 = a5;
            A1 = a1;
        }

        public RecapPlannerConfig Config { get; }
        public RecapPlanningFacts Facts { get; }
        public RecapBlockId ClientId { get; }
        public RecapBlockId SelfId { get; }
        public EventAddress Admission { get; }
        public EventAddress SourceSet { get; }
        public EventAddress A11 { get; }
        public EventAddress A5 { get; }
        public EventAddress A1 { get; }

        public static TestModel Create(
            int rawGrowth = 5,
            int maxRouteEndpoints = 3,
            int maxMaintainerCalls = 3,
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
                rawGrowthTrigger: 3,
                rawGrowthHardLimit: 10,
                maxRouteEndpoints,
                maxMaintainerCalls
            );
            RecapPublishedBlockFact[] sources = [
                new(
                    clientId,
                    clientTarget,
                    sourceSet,
                    Envelope,
                    a1
                )
            ];
            var facts = new RecapPlanningFacts(
                admission,
                lineage,
                [.. addresses],
                sources,
                sourceSet,
                rawGrowth
            );
            return new TestModel(
                config,
                facts,
                clientId,
                selfId,
                admission,
                sourceSet,
                a11,
                a5,
                a1
            );
        }

        public RecapPlanningPolicyDecision Maintain(
            IReadOnlyList<EventAddress> route,
            RecapPriorContext? prior = null
        ) => new RecapPlanningPolicyDecision.Build(
            Admission,
            [
                new RecapBlockPlanningDecision.Maintain(
                    ClientId,
                    new RecapPlanningMaintainSource.Existing(
                        SourceSet,
                        Envelope
                    ),
                    route,
                    prior ?? EmptyRecapPriorContext.Instance
                )
            ]
        );

        public RecapPlanResult Evaluate(
            RecapPlanningPolicyDecision decision
        ) => RecapPlanEvaluator.Evaluate(
            Config,
            Facts,
            new StubPolicy(_ => decision)
        );

        internal static EventAddress Address(ulong value)
            => new(
                SizedPtr.FromPacked(value),
                1,
                AddressHint.None
            );
    }
}
