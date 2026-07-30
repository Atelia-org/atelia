using Atelia.SessionJournal.DerivedRecap.Store;
using Xunit;

namespace Atelia.SessionJournal.DerivedRecap.Planner.Tests;

public sealed class RecapRuntimeAuthorityTests {
    [Fact]
    public void Planning_inputs_snapshot_the_ordered_catalog_and_policy() {
        var first = CatalogEntry("first");
        var second = CatalogEntry("second");
        RecapBlockCatalogEntry[] source = [first, second];
        var cadence = new RecapCadenceConfig(20, 24);
        var policy = new BoundedMaintainAllRecapPlanningPolicy();

        var inputs = new RecapPlanningInputs(
            source,
            cadence,
            policy
        );
        source[0] = second;

        Assert.Equal([first, second], inputs.OrderedCatalog);
        Assert.Same(cadence, inputs.Cadence);
        Assert.Same(policy, inputs.Policy);
    }

    [Fact]
    public void Planning_inputs_reject_duplicate_block_or_target() {
        RecapBlockCatalogEntry first = CatalogEntry("first");

        Assert.Throws<ArgumentException>(() =>
            new RecapPlanningInputs(
                [first, first],
                new RecapCadenceConfig(20, 24),
                new BoundedMaintainAllRecapPlanningPolicy()
            )
        );
    }

    [Fact]
    public void V4_hard_caps_are_stable_and_bound_repo_limits() {
        RecapProtocolHardCaps caps = RecapProtocolHardCaps.V4;

        Assert.Equal(512, caps.MaxRawGrowthEventCount);
        Assert.Equal(4, caps.MaxRouteEndpointsPerBlock);
        Assert.Equal(8, caps.MaxMaintainerCallsPerBuild);
        Assert.Equal(64, caps.MaxRawEventsPerStep);
        Assert.Equal(512, caps.MaxRawEventsPerBuild);
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

    private static RecapBlockCatalogEntry CatalogEntry(
        string blockId
    ) => new(
        new RecapBlockId(blockId),
        new ContextHeaderBlockPath(
            ContextHeaderCarrier.System,
            blockId
        ),
        $"maintainer-{blockId}",
        maxContentUtf8Bytes: 1024
    );
}
