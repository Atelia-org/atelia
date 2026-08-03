using Atelia.SessionJournal.DerivedRecap.Store;
using Xunit;

namespace Atelia.SessionJournal.DerivedRecap.Planner.Tests;

public sealed class RecapRuntimeAuthorityTests {
    [Fact]
    public void Planning_inputs_snapshot_the_ordered_catalog_and_policy() {
        var first = CatalogEntry("first");
        var second = CatalogEntry("second");
        RecapBlockCatalogEntry[] source = [first, second];
        var estimator = new TestHistoryUnitLoadEstimator();
        var cadence = new RecapCadenceConfig(
            estimator.Id,
            new HistoryLoadUnit(20),
            new HistoryLoadUnit(24)
        );
        var policy = new BoundedMaintainAllRecapPlanningPolicy();

        var inputs = new RecapPlanningInputs(
            source,
            cadence,
            estimator,
            policy
        );
        source[0] = second;

        Assert.Equal([first, second], inputs.OrderedCatalog);
        Assert.Same(cadence, inputs.Cadence);
        Assert.Same(estimator, inputs.HistoryUnitLoadEstimator);
        Assert.Same(policy, inputs.Policy);
    }

    [Fact]
    public void Planning_inputs_reject_duplicate_block_or_target() {
        RecapBlockCatalogEntry first = CatalogEntry("first");
        var estimator = new TestHistoryUnitLoadEstimator();

        Assert.Throws<ArgumentException>(() =>
            new RecapPlanningInputs(
                [first, first],
                new RecapCadenceConfig(
                    estimator.Id,
                    new HistoryLoadUnit(20),
                    new HistoryLoadUnit(24)
                ),
                estimator,
                new BoundedMaintainAllRecapPlanningPolicy()
            )
        );
    }

    [Fact]
    public void Planning_inputs_require_exact_estimator_identity() {
        RecapBlockCatalogEntry first = CatalogEntry("first");
        var estimator = new TestHistoryUnitLoadEstimator(
            id: "atelia.tests.history-load.actual-v1"
        );

        Assert.Throws<ArgumentException>(() =>
            new RecapPlanningInputs(
                [first],
                new RecapCadenceConfig(
                    "atelia.tests.history-load.configured-v1",
                    new HistoryLoadUnit(20),
                    new HistoryLoadUnit(24)
                ),
                estimator,
                new BoundedMaintainAllRecapPlanningPolicy()
            )
        );
    }

    [Fact]
    public void V4_hard_caps_are_stable_and_bound_repo_limits() {
        RecapProtocolHardCaps caps = RecapProtocolHardCaps.V4;

        Assert.Equal(
            513,
            DerivedRecapLineageView.MaxPrefixHeaderCount
        );
        Assert.Equal(512, caps.MaxRawGrowthEventCount);
        Assert.Equal(
            DerivedRecapLineageView.MaxPrefixHeaderCount,
            checked(caps.MaxRawGrowthEventCount + 1)
        );
        Assert.Equal(4, caps.MaxRouteEndpointsPerBlock);
        Assert.Equal(8, caps.MaxMaintainerCallsPerBuild);
        Assert.Equal(64, caps.MaxRawEventsPerStep);
        Assert.Equal(512, caps.MaxRawEventsPerBuild);
        Assert.Equal(
            1025,
            RecapFrozenPlanBarrier.ProofPrefixHeaderCount(caps)
        );
        Assert.Equal(
            SessionContextContributionContract
                .MaxContributionUtf8Bytes,
            caps.MaxContentUtf8Bytes
        );
        Assert.Equal(
            SessionContextContributionContract.MaxContributionCount,
            caps.MaxCatalogEntries
        );

        caps.ValidatePlanningLimits(new RecapPlanningLimits(
            maxRawGrowthEventCount: 512,
            maxRouteEndpointsPerBlock: 4,
            maxMaintainerCallsPerBuild: 8,
            maxRawEventsPerStep: 64,
            maxRawEventsPerBuild: 512
        ));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            caps.ValidatePlanningLimits(new RecapPlanningLimits(
                maxRawGrowthEventCount: 513,
                maxRouteEndpointsPerBlock: 4,
                maxMaintainerCallsPerBuild: 8,
                maxRawEventsPerStep: 64,
                maxRawEventsPerBuild: 512
            ))
        );
    }

    [Fact]
    public void Hard_caps_reject_raw_growth_beyond_store_lineage_limit() {
        ArgumentOutOfRangeException exception = Assert.Throws<
            ArgumentOutOfRangeException
        >(() => new RecapProtocolHardCaps(
            maxRawGrowthEventCount:
                DerivedRecapLineageView.MaxPrefixHeaderCount,
            maxRouteEndpointsPerBlock: 4,
            maxMaintainerCallsPerBuild: 8,
            maxRawEventsPerStep: 64,
            maxRawEventsPerBuild: 512,
            maxContentUtf8Bytes:
                SessionContextContributionContract
                    .MaxContributionUtf8Bytes,
            maxCatalogEntries:
                SessionContextContributionContract.MaxContributionCount
        ));

        Assert.Equal("maxRawGrowthEventCount", exception.ParamName);
        Assert.Contains(
            nameof(DerivedRecapLineageView.MaxPrefixHeaderCount),
            exception.Message,
            StringComparison.Ordinal
        );
    }

    private static RecapBlockCatalogEntry CatalogEntry(
        string blockId
    ) => new(
        new RecapBlockId(blockId),
        new ContextHeaderBlockPath(
            ContextHeaderCarrier.System,
            blockId
        ),
        $"maintainer-{blockId}",
        RecapPlannerTestIdentity.CapabilityFingerprint,
        maxContentUtf8Bytes: 1024
    );
}
