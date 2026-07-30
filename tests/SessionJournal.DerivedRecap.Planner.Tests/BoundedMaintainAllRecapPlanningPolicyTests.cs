using Atelia.Data;
using Atelia.EventJournal;
using Atelia.SessionJournal.DerivedRecap.Store;
using Xunit;

namespace Atelia.SessionJournal.DerivedRecap.Planner.Tests;

public sealed class BoundedMaintainAllRecapPlanningPolicyTests {
    [Fact]
    public void FirstBuildChoosesLatestBudgetValidAdmission() {
        PolicyModel model = PolicyModel.FirstBuild(
            replaySafeIndices: [10, 8, 6, 4, 2, 0],
            maxRawEventsPerStep: 4,
            maxRawEventsPerBuild: 6
        );

        RecapPlanningPolicyDecision.Build build = model.Build();

        Assert.Equal(model.At(4), build.SetAdmissionAnchor);
        var maintain = Assert.IsType<
            RecapBlockPlanningDecision.Maintain
        >(Assert.Single(build.Blocks));
        var empty = Assert.IsType<
            RecapPlanningMaintainSource.Empty
        >(maintain.Source);
        Assert.Equal(model.At(10), empty.ReplayStartExclusive);
        Assert.Equal([model.At(6), model.At(4)],
            maintain.CatchUpThrough);
        Assert.Same(
            EmptyRecapPriorContext.Instance,
            maintain.PriorContext
        );
    }

    [Fact]
    public void ExistingBlocksUseTheirExactIndependentCursors() {
        PolicyModel model = PolicyModel.Existing(
            cursorIndices: [10, 8],
            replaySafeIndices: [10, 8, 6, 4, 2, 0],
            latestPublishedIndex: 6,
            maxRawEventsPerStep: 2,
            maxMaintainerCallsPerBuild: 20,
            maxRawEventsPerBuild: 100
        );

        RecapPlanningPolicyDecision.Build build = model.Build();

        Assert.Equal(model.At(0), build.SetAdmissionAnchor);
        Assert.Equal(2, build.Blocks.Count);
        Assert.Equal(
            [
                model.At(8),
                model.At(6),
                model.At(4),
                model.At(2),
                model.At(0)
            ],
            MaintainAt(build, 0).CatchUpThrough
        );
        Assert.Equal(
            [
                model.At(6),
                model.At(4),
                model.At(2),
                model.At(0)
            ],
            MaintainAt(build, 1).CatchUpThrough
        );
        Assert.All(build.Blocks, decision =>
            Assert.IsType<RecapPlanningMaintainSource.Existing>(
                Assert.IsType<
                    RecapBlockPlanningDecision.Maintain
                >(decision).Source
            )
        );
    }

    [Fact]
    public void IrregularReplayBoundariesUseMinimumGreedyRoute() {
        PolicyModel model = PolicyModel.FirstBuild(
            replaySafeIndices: [10, 8, 7, 4, 3, 0],
            maxRawEventsPerStep: 4
        );

        RecapPlanningPolicyDecision.Build build = model.Build();

        Assert.Equal(model.At(0), build.SetAdmissionAnchor);
        Assert.Equal(
            [model.At(7), model.At(3), model.At(0)],
            MaintainAt(build, 0).CatchUpThrough
        );
    }

    [Fact]
    public void DivergentCursorsBackOffToLatestAggregateBudgetAdmission() {
        PolicyModel model = PolicyModel.Existing(
            cursorIndices: [10, 7],
            replaySafeIndices: [10, 9, 7, 6, 4, 3, 1, 0],
            latestPublishedIndex: 6,
            maxRawEventsPerStep: 4,
            maxMaintainerCallsPerBuild: 20,
            maxRawEventsPerBuild: 16
        );

        RecapPlanningPolicyDecision.Build build = model.Build();

        Assert.Equal(model.At(1), build.SetAdmissionAnchor);
        Assert.Equal(
            [model.At(6), model.At(3), model.At(1)],
            MaintainAt(build, 0).CatchUpThrough
        );
        Assert.Equal(
            [model.At(3), model.At(1)],
            MaintainAt(build, 1).CatchUpThrough
        );
    }

    [Fact]
    public void EqualUnitCandidatesPreferRawNewestBoundary() {
        PolicyModel model = PolicyModel.FirstBuild(
            replaySafeIndices: [10, 8, 7, 0],
            maxRawEventsPerStep: 10,
            maxRawEventsPerBuild: 3,
            completedUnitCounts: new Dictionary<int, int> {
                [8] = 2,
                [7] = 2,
                [0] = 10
            }
        );

        RecapPlanningPolicyDecision.Build build = model.Build();

        Assert.Equal(model.At(7), build.SetAdmissionAnchor);
    }

    [Fact]
    public void SparseReplayGapReturnsTypedUnavailable() {
        PolicyModel model = PolicyModel.FirstBuild(
            replaySafeIndices: [10, 0],
            maxRawEventsPerStep: 5
        );

        AssertUnavailable(
            model.Evaluate(),
            RecapPlanDefectCodes.RawStepLimitExceeded
        );
    }

    [Fact]
    public void RouteEndpointCeilingReturnsTypedUnavailable() {
        PolicyModel model = PolicyModel.Existing(
            cursorIndices: [10],
            replaySafeIndices: [10, 8, 6, 4, 2, 0],
            latestPublishedIndex: 6,
            maxRawEventsPerStep: 2,
            maxRouteEndpointsPerBlock: 2
        );

        AssertUnavailable(
            model.Evaluate(),
            RecapPlanDefectCodes.RouteLimitExceeded
        );
    }

    [Fact]
    public void AggregateCallCeilingReturnsTypedUnavailable() {
        PolicyModel model = PolicyModel.Existing(
            cursorIndices: [10, 8],
            replaySafeIndices: [10, 8, 6, 4, 2, 0],
            latestPublishedIndex: 6,
            maxRawEventsPerStep: 2,
            maxMaintainerCallsPerBuild: 4,
            maxRawEventsPerBuild: 100
        );

        AssertUnavailable(
            model.Evaluate(),
            RecapPlanDefectCodes.CallLimitExceeded
        );
    }

    [Fact]
    public void AggregateRawBuildCeilingReturnsTypedUnavailable() {
        PolicyModel model = PolicyModel.Existing(
            cursorIndices: [10, 8],
            replaySafeIndices: [10, 8, 6, 4, 2, 0],
            latestPublishedIndex: 6,
            maxRawEventsPerStep: 2,
            maxMaintainerCallsPerBuild: 20,
            maxRawEventsPerBuild: 9
        );

        AssertUnavailable(
            model.Evaluate(),
            RecapPlanDefectCodes.RawBuildLimitExceeded
        );
    }

    private static RecapBlockPlanningDecision.Maintain MaintainAt(
        RecapPlanningPolicyDecision.Build build,
        int index
    ) => Assert.IsType<RecapBlockPlanningDecision.Maintain>(
        build.Blocks[index]
    );

    private static void AssertUnavailable(
        RecapPlanIntentResult result,
        string code
    ) {
        var unavailable =
            Assert.IsType<RecapPlanIntentResult.Unavailable>(result);
        Assert.Contains(
            unavailable.Defects,
            defect => defect.Code == code
        );
    }

    private sealed class PolicyModel {
        private const string Envelope =
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
            + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        private PolicyModel(
            EventAddress[] lineageAddresses,
            RecapPlanningInputs inputs,
            RecapPlanningLimits limits,
            RecapSchedulingFacts scheduling,
            RecapPolicyFacts policyFacts
        ) {
            LineageAddresses = lineageAddresses;
            Inputs = inputs;
            Limits = limits;
            Scheduling = scheduling;
            PolicyFacts = policyFacts;
        }

        private EventAddress[] LineageAddresses { get; }
        private RecapPlanningInputs Inputs { get; }
        private RecapPlanningLimits Limits { get; }
        private RecapSchedulingFacts Scheduling { get; }
        private RecapPolicyFacts PolicyFacts { get; }

        public EventAddress At(int lineageIndex)
            => LineageAddresses[lineageIndex];

        public static PolicyModel FirstBuild(
            IReadOnlyList<int> replaySafeIndices,
            int maxRawEventsPerStep,
            int maxRawEventsPerBuild = 100,
            IReadOnlyDictionary<int, int>?
                completedUnitCounts = null
        ) {
            EventAddress[] addresses = Addresses();
            PlanningAuthority authority = AuthorityFor(
                blockCount: 1,
                maxRouteEndpointsPerBlock: 10,
                maxMaintainerCallsPerBuild: 10,
                maxRawEventsPerStep,
                maxRawEventsPerBuild
            );
            var scheduling = SchedulingFor(
                addresses,
                replaySafeIndices,
                latestPublishedIndex: null,
                completedUnitCounts
            );
            return new PolicyModel(
                addresses,
                authority.Inputs,
                authority.Limits,
                scheduling,
                new RecapPolicyFacts(addresses[10], [])
            );
        }

        public static PolicyModel Existing(
            IReadOnlyList<int> cursorIndices,
            IReadOnlyList<int> replaySafeIndices,
            int latestPublishedIndex,
            int maxRawEventsPerStep,
            int maxRouteEndpointsPerBlock = 10,
            int maxMaintainerCallsPerBuild = 10,
            int maxRawEventsPerBuild = 100
        ) {
            EventAddress[] addresses = Addresses();
            PlanningAuthority authority = AuthorityFor(
                cursorIndices.Count,
                maxRouteEndpointsPerBlock,
                maxMaintainerCallsPerBuild,
                maxRawEventsPerStep,
                maxRawEventsPerBuild
            );
            var scheduling = SchedulingFor(
                addresses,
                replaySafeIndices,
                latestPublishedIndex
            );
            var source = new RecapSourceIntent(
                addresses[latestPublishedIndex],
                Envelope
            );
            return new PolicyModel(
                addresses,
                authority.Inputs,
                authority.Limits,
                scheduling,
                new RecapPolicyFacts(
                    emptyReplayStartExclusive: null,
                    [
                        .. authority.Inputs.OrderedCatalog
                            .Select((entry, index) =>
                            new RecapBlockSourceIntent(
                                entry.RecapBlockId,
                                source,
                                addresses[cursorIndices[index]]
                            ))
                    ]
                )
            );
        }

        public RecapPlanIntentResult Evaluate() {
            var ready = Assert.IsType<RecapSchedulingResult.Ready>(
                RecapPlanEvaluator.EvaluateSchedule(
                    Inputs,
                    Limits,
                    Scheduling
                )
            );
            return RecapPlanEvaluator.EvaluateIntent(
                ready,
                PolicyFacts
            );
        }

        public RecapPlanningPolicyDecision.Build Build() {
            var ready =
                Assert.IsType<RecapPlanIntentResult.IntentReady>(
                    Evaluate()
                );
            return ready.Intent;
        }

        private static EventAddress[] Addresses()
            => [
                .. Enumerable.Range(0, 11)
                    .Select(index => new EventAddress(
                        SizedPtr.FromPacked((ulong)(100 - index)),
                        1,
                        AddressHint.None
                    ))
            ];

        private static RecapSchedulingFacts SchedulingFor(
            EventAddress[] addresses,
            IReadOnlyList<int> replaySafeIndices,
            int? latestPublishedIndex,
            IReadOnlyDictionary<int, int>?
                completedUnitCounts = null
        ) {
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
            return new RecapSchedulingFacts(
                addresses[0],
                lineage,
                new RecapHistoryWindowFacts(
                    addresses[10],
                    totalHistoryUnitCount: 10,
                    [
                        .. replaySafeIndices
                            .Where(static index => index != 10)
                            .OrderByDescending(static index => index)
                            .Select(index =>
                                new SessionHistoryPlanningBoundary(
                                    addresses[index],
                                    CompletedUnitCount:
                                        completedUnitCounts?
                                            .GetValueOrDefault(
                                                index,
                                                10 - index
                                            )
                                        ?? 10 - index
                                ))
                    ]
                ),
                latestPublishedIndex is { } baseline
                    ? addresses[baseline]
                    : addresses[10],
                latestPublishedIndex is { } latest
                    ? addresses[latest]
                    : null
            );
        }

        private static PlanningAuthority AuthorityFor(
            int blockCount,
            int maxRouteEndpointsPerBlock,
            int maxMaintainerCallsPerBuild,
            int maxRawEventsPerStep,
            int maxRawEventsPerBuild
        ) {
            RecapBlockCatalogEntry[] catalog = [
                .. Enumerable.Range(0, blockCount).Select(index =>
                    new RecapBlockCatalogEntry(
                        new RecapBlockId($"block-{index}"),
                        new ContextHeaderBlockPath(
                            ContextHeaderCarrier.System,
                            $"block-{index}"
                        ),
                        $"maintainer-{index}",
                        1024
                    ))
            ];
            var policy = new BoundedMaintainAllRecapPlanningPolicy();
            return new PlanningAuthority(
                new RecapPlanningInputs(
                    catalog,
                    new RecapCadenceConfig(
                        minimumRecentHistoryUnitCount: 0,
                        recapBuildIntervalUnitCount: 1
                    ),
                    policy
                ),
                new RecapPlanningLimits(
                    maxRawGrowthEventCount: 100,
                    maxRouteEndpointsPerBlock,
                    maxMaintainerCallsPerBuild,
                    maxRawEventsPerStep,
                    maxRawEventsPerBuild
                )
            );
        }

        private sealed record PlanningAuthority(
            RecapPlanningInputs Inputs,
            RecapPlanningLimits Limits
        );
    }
}
