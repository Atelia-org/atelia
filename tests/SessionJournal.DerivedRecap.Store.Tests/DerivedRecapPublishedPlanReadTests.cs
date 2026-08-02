using Atelia.EventJournal;
using Xunit;

namespace Atelia.SessionJournal.DerivedRecap.Store.Tests;

public sealed class DerivedRecapPublishedPlanReadTests {
    [Fact]
    public async Task AnchorReadDiscoversPlanWithoutReadingFinalBlocks() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(historyPairs: 3);
        DerivedRecapLineageView lineage = fixture.Lineage();
        EventAddress anchor = lineage.CapturedHead;
        PublishedRecapDescriptor descriptor =
            await fixture.PublishAsync(
                anchor,
                lineage.CurrentPrefix.HeadToOldest[2].Address
            );
        Directory.Delete(
            Path.Combine(
                fixture.Store.GetPublishedPathForTest(anchor),
                "blocks"
            ),
            recursive: true
        );

        var available =
            Assert.IsType<
                PublishedPlanAtAnchorReadResult.Available
            >(
                await fixture.Store
                    .ReadPublishedPlanAtAnchorAsync(anchor)
            );

        Assert.Equal(descriptor, available.Snapshot.Descriptor);
        Assert.Equal(
            descriptor.RefId,
            available.Snapshot.FrozenPlan.RefId
        );
        Assert.Equal(
            anchor,
            available.Snapshot.FrozenPlan.SetAdmissionAnchor
        );
        Assert.Single(available.Snapshot.FrozenPlan.Blocks);
    }

    [Fact]
    public async Task AnchorReadMissingMembershipIsTypedMissing() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync();
        EventAddress anchor = fixture.Lineage().CapturedHead;

        var missing =
            Assert.IsType<PublishedPlanAtAnchorReadResult.Missing>(
                await fixture.Store
                    .ReadPublishedPlanAtAnchorAsync(anchor)
            );

        Assert.Equal(anchor, missing.SetAdmissionAnchor);
    }

    [Fact]
    public async Task AnchorReadRejectsPublicationIdentityMismatch() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(historyPairs: 3);
        DerivedRecapLineageView lineage = fixture.Lineage();
        EventAddress publishedAnchor = lineage.CapturedHead;
        EventAddress wrongPathAnchor =
            lineage.CurrentPrefix.HeadToOldest[2].Address;
        _ = await fixture.PublishAsync(
            publishedAnchor,
            wrongPathAnchor
        );
        string sourcePath = Path.Combine(
            fixture.Store.GetPublishedPathForTest(publishedAnchor),
            "publication.json"
        );
        string wrongPath =
            fixture.Store.GetPublishedPathForTest(wrongPathAnchor);
        Directory.CreateDirectory(wrongPath);
        await File.WriteAllBytesAsync(
            Path.Combine(wrongPath, "publication.json"),
            await File.ReadAllBytesAsync(sourcePath)
        );

        var unavailable =
            Assert.IsType<
                PublishedPlanAtAnchorReadResult.Unavailable
            >(
                await fixture.Store
                    .ReadPublishedPlanAtAnchorAsync(wrongPathAnchor)
            );

        Assert.Equal(
            wrongPathAnchor,
            unavailable.SetAdmissionAnchor
        );
        Assert.Contains(
            unavailable.Defects,
            defect => defect.Code == "PublishedPlanUnavailable"
        );
    }

    [Fact]
    public async Task AnchorReadRejectsNonCanonicalEnvelope() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(historyPairs: 3);
        DerivedRecapLineageView lineage = fixture.Lineage();
        EventAddress anchor = lineage.CapturedHead;
        _ = await fixture.PublishAsync(
            anchor,
            lineage.CurrentPrefix.HeadToOldest[2].Address
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

        Assert.IsType<PublishedPlanAtAnchorReadResult.Unavailable>(
            await fixture.Store
                .ReadPublishedPlanAtAnchorAsync(anchor)
        );
    }

    [Fact]
    public async Task AnchorReadRejectsEnvelopeHashCorruption() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(historyPairs: 3);
        DerivedRecapLineageView lineage = fixture.Lineage();
        EventAddress anchor = lineage.CapturedHead;
        PublishedRecapDescriptor published =
            await fixture.PublishAsync(
                anchor,
                lineage.CurrentPrefix.HeadToOldest[2].Address
            );
        string publicationPath = Path.Combine(
            fixture.Store.GetPublishedPathForTest(anchor),
            "publication.json"
        );
        string canonical =
            await File.ReadAllTextAsync(publicationPath);
        string corrupted = canonical.Replace(
            published.EnvelopeSha256,
            new string('0', 64),
            StringComparison.Ordinal
        );
        Assert.NotEqual(canonical, corrupted);
        await File.WriteAllTextAsync(publicationPath, corrupted);

        Assert.IsType<PublishedPlanAtAnchorReadResult.Unavailable>(
            await fixture.Store
                .ReadPublishedPlanAtAnchorAsync(anchor)
        );
    }

    [Fact]
    public async Task AnchorReadCanonicalReplacementIsTypedChanged() {
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
        DerivedRecapLineageView lineage = fixture.Lineage();
        EventAddress anchor = lineage.CapturedHead;
        EventAddress replayStart =
            lineage.CurrentPrefix.HeadToOldest[2].Address;
        PublishedRecapDescriptor published =
            await fixture.PublishAsync(anchor, replayStart);
        PublishedRecapSet replacement =
            CreateReplacementPublication(
                fixture,
                anchor,
                replayStart
            );
        replace = () => File.WriteAllBytes(
            Path.Combine(
                fixture.Store.GetPublishedPathForTest(anchor),
                "publication.json"
            ),
            DerivedRecapCodec.EncodePublication(replacement)
        );

        var changed =
            Assert.IsType<PublishedPlanAtAnchorReadResult.Changed>(
                await fixture.Store
                    .ReadPublishedPlanAtAnchorAsync(anchor)
            );

        Assert.Equal(published, changed.Before);
        Assert.Equal(
            replacement.EnvelopeSha256,
            changed.After?.EnvelopeSha256
        );
    }

    [Fact]
    public async Task ExactPlanReadDoesNotReadFinalBlocks() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(historyPairs: 3);
        DerivedRecapLineageView lineage = fixture.Lineage();
        EventAddress anchor = lineage.CapturedHead;
        PublishedRecapDescriptor descriptor =
            await fixture.PublishAsync(
                anchor,
                lineage.CurrentPrefix.HeadToOldest[2].Address
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
        DerivedRecapLineageView lineage = fixture.Lineage();
        PublishedRecapDescriptor published =
            await fixture.PublishAsync(
                lineage.CapturedHead,
                lineage.CurrentPrefix.HeadToOldest[2].Address
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
        DerivedRecapLineageView lineage = fixture.Lineage();
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
                lineage.CurrentPrefix.HeadToOldest[2].Address
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
        DerivedRecapLineageView lineage = fixture.Lineage();
        EventAddress anchor = lineage.CapturedHead;
        EventAddress replayStart =
            lineage.CurrentPrefix.HeadToOldest[2].Address;
        PublishedRecapDescriptor published =
            await fixture.PublishAsync(anchor, replayStart);
        PublishedRecapSet replacement =
            CreateReplacementPublication(
                fixture,
                anchor,
                replayStart
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

    private static PublishedRecapSet CreateReplacementPublication(
        RecapStoreFixture fixture,
        EventAddress anchor,
        EventAddress replayStart
    ) {
        var original = Assert.IsType<MaintainRecapBlockPlan>(
            fixture.CreateMaintainPlan(anchor, replayStart)
        );
        RecapBlockPlan replacementPlan =
            new MaintainRecapBlockPlan(
                original.RecapBlockId,
                original.Target,
                "replacement-maintainer",
                original.MaintainerCapabilityFingerprint,
                original.Source,
                original.CatchUpThrough,
                original.PriorContext,
                original.MaxContentUtf8Bytes
            );
        return DerivedRecapCodec.CreatePublication(
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
    }
}
