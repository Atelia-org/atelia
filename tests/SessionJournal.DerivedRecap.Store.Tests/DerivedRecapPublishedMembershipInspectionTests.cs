using Atelia.EventJournal;
using Xunit;

namespace Atelia.SessionJournal.DerivedRecap.Store.Tests;

public sealed class DerivedRecapPublishedMembershipInspectionTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PresentIsExactAndIndependentOfCurrentLineage(
        bool rewindOffLineage
    ) {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(historyPairs: 4);
        DerivedRecapLineageView lineage = fixture.Lineage();
        EventAddress anchor = lineage.CapturedHead;
        PublishedRecapDescriptor descriptor =
            await fixture.PublishAsync(
                anchor,
                lineage.CurrentPrefix.HeadToOldest[^2].Address
            );
        if (rewindOffLineage) {
            RewindBefore(fixture, anchor, lineage.CurrentPrefix.HeadToOldest[2].Address);
        }

        var present = Assert.IsType<
            PublishedMembershipInspectionResult.Present
        >(
            await fixture.Store.InspectPublishedMembershipAsync(anchor)
        );

        Assert.Equal(descriptor, present.Descriptor);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AbsentIsExactAndIndependentOfCurrentLineage(
        bool rewindOffLineage
    ) {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(historyPairs: 4);
        DerivedRecapLineageView lineage = fixture.Lineage();
        EventAddress anchor = lineage.CapturedHead;
        if (rewindOffLineage) {
            RewindBefore(fixture, anchor, lineage.CurrentPrefix.HeadToOldest[2].Address);
        }

        var absent = Assert.IsType<
            PublishedMembershipInspectionResult.Absent
        >(
            await fixture.Store.InspectPublishedMembershipAsync(anchor)
        );

        Assert.Equal(anchor, absent.SetAdmissionAnchor);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DamagedMemberIsInvalidRegardlessOfCurrentLineage(
        bool rewindOffLineage
    ) {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(historyPairs: 4);
        DerivedRecapLineageView lineage = fixture.Lineage();
        EventAddress anchor = lineage.CapturedHead;
        _ = await fixture.PublishAsync(
            anchor,
            lineage.CurrentPrefix.HeadToOldest[^2].Address
        );
        await File.WriteAllTextAsync(
            Path.Combine(
                fixture.Store.GetPublishedPathForTest(anchor),
                "publication.json"
            ),
            "damaged"
        );
        if (rewindOffLineage) {
            RewindBefore(fixture, anchor, lineage.CurrentPrefix.HeadToOldest[2].Address);
        }

        var invalid = Assert.IsType<
            PublishedMembershipInspectionResult.Invalid
        >(
            await fixture.Store.InspectPublishedMembershipAsync(anchor)
        );

        Assert.Equal(anchor, invalid.SetAdmissionAnchor);
        Assert.Contains(
            invalid.Defects,
            static defect => defect.Code == "PublishedSetInvalid"
        );
    }

    [Fact]
    public async Task MissingStoreIsTypedStoreUnavailable() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync();
        EventAddress anchor = fixture.Lineage().CapturedHead;
        File.Delete(Path.Combine(
            fixture.Store.StoreRootPathForTest,
            "store.json"
        ));

        var unavailable = Assert.IsType<
            PublishedMembershipInspectionResult.StoreUnavailable
        >(
            await fixture.Store.InspectPublishedMembershipAsync(anchor)
        );

        Assert.Equal(anchor, unavailable.SetAdmissionAnchor);
        Assert.False(string.IsNullOrWhiteSpace(unavailable.Reason));
    }

    private static void RewindBefore(
        RecapStoreFixture fixture,
        EventAddress expectedHead,
        EventAddress rewindTo
    ) {
        RefId refId = fixture.Engine.BranchRefId;
        fixture.Engine.Dispose();
        using (var journal =
               EventJournal.EventJournal.OpenExisting(fixture.Path)) {
            journal.MoveRef(refId, expectedHead, rewindTo).Unwrap();
        }
        fixture.ReopenEngine();
        Assert.DoesNotContain(
            fixture.Lineage().CurrentPrefix.HeadToOldest,
            node => node.Address == expectedHead
        );
    }
}
