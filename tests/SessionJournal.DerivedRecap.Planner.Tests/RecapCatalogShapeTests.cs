using Atelia.EventJournal;
using Atelia.SessionJournal.DerivedRecap.Store;
using Xunit;

namespace Atelia.SessionJournal.DerivedRecap.Planner.Tests;

public sealed class RecapCatalogShapeTests {
    [Theory]
    [InlineData("")]
    [InlineData("sha256:ABCDEF")]
    [InlineData("sha256:gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    public void CatalogEntryRejectsMalformedCapabilityFingerprint(
        string fingerprint
    ) {
        Assert.Throws<ArgumentException>(() =>
            new RecapBlockCatalogEntry(
                new RecapBlockId("self"),
                new ContextHeaderBlockPath(
                    ContextHeaderCarrier.System,
                    "self"
                ),
                "maintainer",
                fingerprint,
                4096
            )
        );
    }

    [Fact]
    public void ProjectionIgnoresPlanSubtypeAndExecutionIdentity() {
        var id = new RecapBlockId("self");
        var target = new ContextHeaderBlockPath(
            ContextHeaderCarrier.System,
            "self"
        );
        EventAddress address = default;
        IReadOnlyList<RecapCatalogShapeEntry> active =
            RecapCatalogShape.ProjectActive([
                new RecapBlockCatalogEntry(
                    id,
                    target,
                    "new-profile",
                    RecapPlannerTestIdentity.CapabilityFingerprint,
                    4096
                )
            ]);
        IReadOnlyList<RecapCatalogShapeEntry> maintain =
            RecapCatalogShape.ProjectFrozen([
                new MaintainRecapBlockPlan(
                    id,
                    target,
                    "old-profile",
                    RecapPlannerTestIdentity.CapabilityFingerprint,
                    new EmptyRecapMaintainSource(
                        address,
                        RecapPlannerWireTestFacts.SyntheticSetups(
                            address
                        )
                    ),
                    [
                        RecapPlannerWireTestFacts.SyntheticBoundary(
                            address
                        )
                    ],
                    EmptyRecapPriorContext.Instance,
                    4096
                )
            ]);
        IReadOnlyList<RecapCatalogShapeEntry> inherit =
            RecapCatalogShape.ProjectFrozen([
                new InheritRecapBlockPlan(
                    id,
                    target,
                    address,
                    RecapPlannerWireTestFacts.SyntheticSetups(
                        address
                    ),
                    new string('a', 64),
                    new string('b', 64),
                    4096
                )
            ]);

        Assert.True(
            RecapCatalogShape.Compare(active, maintain).IsExactMatch
        );
        Assert.True(
            RecapCatalogShape.Compare(active, inherit).IsExactMatch
        );
    }

    [Fact]
    public void ComparatorRequiresExactOrderAndAllThreeFields() {
        var first = new RecapCatalogShapeEntry(
            new RecapBlockId("first"),
            new ContextHeaderBlockPath(
                ContextHeaderCarrier.System,
                "first"
            ),
            100
        );
        var second = new RecapCatalogShapeEntry(
            new RecapBlockId("second"),
            new ContextHeaderBlockPath(
                ContextHeaderCarrier.System,
                "second"
            ),
            200
        );

        RecapCatalogShapeComparison reordered =
            RecapCatalogShape.Compare(
                [first, second],
                [second, first]
            );
        RecapCatalogShapeComparison resized =
            RecapCatalogShape.Compare(
                [first],
                [first with { MaxContentUtf8Bytes = 101 }]
            );

        Assert.False(reordered.IsExactMatch);
        Assert.Equal(0, reordered.MismatchIndex);
        Assert.False(resized.IsExactMatch);
        Assert.Equal(0, resized.MismatchIndex);
    }
}
