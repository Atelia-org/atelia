using Atelia.EventJournal;
using Xunit;

namespace Atelia.SessionJournal.DerivedRecap.Store.Tests;

public sealed class DerivedRecapPublishedPlanReadTests {
    [Fact]
    public async Task ExactPlanReadDoesNotReadFinalBlocks() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(historyPairs: 3);
        SessionCurrentLineageSnapshot lineage = fixture.Lineage();
        EventAddress anchor = lineage.CapturedHead;
        PublishedRecapDescriptor descriptor =
            await fixture.PublishAsync(
                anchor,
                lineage.HeadToRoot[2].Address
            );
        Directory.Delete(
            Path.Combine(
                fixture.Store.GetPublishedPathForTest(anchor),
                "blocks"
            ),
            recursive: true
        );

        var available =
            Assert.IsType<PublishedPlanReadResult.Available>(
                await fixture.Store.ReadPublishedPlanAsync(descriptor)
            );

        Assert.Equal(descriptor, available.Snapshot.Descriptor);
        Assert.Equal(
            anchor,
            available.Snapshot.FrozenPlan.SetAdmissionAnchor
        );
        Assert.Single(available.Snapshot.FrozenPlan.Blocks);
    }

    [Fact]
    public async Task DescriptorHashMismatchIsTypedChanged() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(historyPairs: 3);
        SessionCurrentLineageSnapshot lineage = fixture.Lineage();
        PublishedRecapDescriptor published =
            await fixture.PublishAsync(
                lineage.CapturedHead,
                lineage.HeadToRoot[2].Address
            );
        var expected = published with {
            EnvelopeSha256 = new string('0', 64)
        };

        var changed =
            Assert.IsType<PublishedPlanReadResult.Changed>(
                await fixture.Store.ReadPublishedPlanAsync(expected)
            );

        Assert.Equal(expected, changed.Expected);
        Assert.Equal(published, changed.Observed);
    }

    [Fact]
    public async Task MissingOrNonCanonicalEnvelopeIsTypedUnavailable() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(historyPairs: 3);
        SessionCurrentLineageSnapshot lineage = fixture.Lineage();
        EventAddress anchor = lineage.CapturedHead;
        var missing = new PublishedRecapDescriptor(
            fixture.Engine.BranchRefId,
            anchor,
            new string('0', 64)
        );
        Assert.IsType<PublishedPlanReadResult.Unavailable>(
            await fixture.Store.ReadPublishedPlanAsync(missing)
        );

        PublishedRecapDescriptor published =
            await fixture.PublishAsync(
                anchor,
                lineage.HeadToRoot[2].Address
            );
        string publicationPath = Path.Combine(
            fixture.Store.GetPublishedPathForTest(anchor),
            "publication.json"
        );
        byte[] canonical =
            await File.ReadAllBytesAsync(publicationPath);
        await File.WriteAllBytesAsync(
            publicationPath,
            [.. canonical, (byte)'\n']
        );

        var unavailable =
            Assert.IsType<PublishedPlanReadResult.Unavailable>(
                await fixture.Store.ReadPublishedPlanAsync(published)
            );
        Assert.Contains(
            unavailable.Defects,
            defect => defect.Code == "PublishedPlanUnavailable"
        );
    }

    [Fact]
    public async Task CanonicalReplacementBetweenReadsIsTypedChanged() {
        Action? replace = null;
        var hooks = new RecapStoreTestHooks(
            BeforePublishedPlanEnvelopeRecheck:
                () => replace?.Invoke()
        );
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(
                hooks,
                historyPairs: 3
            );
        SessionCurrentLineageSnapshot lineage = fixture.Lineage();
        EventAddress anchor = lineage.CapturedHead;
        EventAddress replayStart =
            lineage.HeadToRoot[2].Address;
        PublishedRecapDescriptor published =
            await fixture.PublishAsync(anchor, replayStart);
        var original = Assert.IsType<MaintainRecapBlockPlan>(
            fixture.CreateMaintainPlan(anchor, replayStart)
        );
        RecapBlockPlan replacementPlan =
            new MaintainRecapBlockPlan(
                original.RecapBlockId,
                original.Target,
                "replacement-maintainer",
                original.Source,
                original.CatchUpThrough,
                original.PriorContext,
                original.MaxContentUtf8Bytes
            );
        PublishedRecapSet replacement =
            DerivedRecapCodec.CreatePublication(
                DerivedRecapCodec.CreateManifest(
                    fixture.Engine.BranchRefId,
                    anchor,
                    [replacementPlan]
                ),
                [
                    DerivedRecapCodec.CreateBlock(
                        replacementPlan,
                        anchor,
                        "replacement"
                    )
                ]
            );
        replace = () => File.WriteAllBytes(
            Path.Combine(
                fixture.Store.GetPublishedPathForTest(anchor),
                "publication.json"
            ),
            DerivedRecapCodec.EncodePublication(replacement)
        );

        var changed =
            Assert.IsType<PublishedPlanReadResult.Changed>(
                await fixture.Store.ReadPublishedPlanAsync(published)
            );

        Assert.Equal(published, changed.Expected);
        Assert.Equal(
            replacement.EnvelopeSha256,
            changed.Observed.EnvelopeSha256
        );
    }
}
