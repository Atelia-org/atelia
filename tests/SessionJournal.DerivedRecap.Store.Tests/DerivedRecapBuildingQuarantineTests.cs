using Atelia.EventJournal;
using Xunit;

namespace Atelia.SessionJournal.DerivedRecap.Store.Tests;

public sealed class DerivedRecapBuildingQuarantineTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HealthyOrDamagedBuildingIsRecoverablyQuarantined(
        bool damageManifest
    ) {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(historyPairs: 2);
        SessionCurrentLineageSnapshot lineage = fixture.Lineage();
        EventAddress anchor = lineage.CapturedHead;
        await CreateBuildingAsync(
            fixture,
            anchor,
            lineage.HeadToRoot[2].Address
        );
        string manifestPath = Path.Combine(
            fixture.Store.GetBuildingPathForTest(anchor),
            "manifest.json"
        );
        byte[] expectedManifest;
        if (damageManifest) {
            expectedManifest = "damaged"u8.ToArray();
            await File.WriteAllBytesAsync(
                manifestPath,
                expectedManifest
            );
            Assert.IsType<BuildingReadResult.Invalid>(
                await fixture.Store.ReadBuildingAsync(anchor)
            );
        }
        else {
            expectedManifest =
                await File.ReadAllBytesAsync(manifestPath);
            Assert.IsType<BuildingReadResult.Available>(
                await fixture.Store.ReadBuildingAsync(anchor)
            );
        }

        var quarantined =
            Assert.IsType<QuarantineBuildingResult.Quarantined>(
                await fixture.Store.QuarantineBuildingAsync(anchor)
            );

        Assert.False(string.IsNullOrWhiteSpace(
            quarantined.QuarantineId
        ));
        Assert.IsType<BuildingReadResult.Missing>(
            await fixture.Store.ReadBuildingAsync(anchor)
        );
        Assert.IsType<QuarantineBuildingResult.AlreadyAbsent>(
            await fixture.Store.QuarantineBuildingAsync(anchor)
        );
        string quarantinePath =
            fixture.Store.GetBuildingQuarantinePathForTest(
                anchor,
                quarantined.QuarantineId
            );
        Assert.True(Directory.Exists(quarantinePath));
        Assert.Equal(
            expectedManifest,
            await File.ReadAllBytesAsync(
                Path.Combine(quarantinePath, "manifest.json")
            )
        );
    }

    [Fact]
    public async Task MissingBuildingIsIdempotentlyAlreadyAbsent() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync();
        EventAddress anchor = fixture.Lineage().CapturedHead;

        Assert.IsType<QuarantineBuildingResult.AlreadyAbsent>(
            await fixture.Store.QuarantineBuildingAsync(anchor)
        );
        Assert.IsType<QuarantineBuildingResult.AlreadyAbsent>(
            await fixture.Store.QuarantineBuildingAsync(anchor)
        );
        Assert.False(
            Directory.Exists(
                fixture.Store.BuildingQuarantineRootForTest
            )
        );
    }

    [Fact]
    public async Task PublishedMembershipIsAConflictAndRemainsSelected() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(historyPairs: 2);
        SessionCurrentLineageSnapshot lineage = fixture.Lineage();
        EventAddress anchor = lineage.CapturedHead;
        PublishedRecapDescriptor published =
            await fixture.PublishAsync(
                anchor,
                lineage.HeadToRoot[2].Address
            );

        Assert.IsType<QuarantineBuildingResult.PublishedConflict>(
            await fixture.Store.QuarantineBuildingAsync(anchor)
        );

        var selected = Assert.IsType<DerivedRecapSelection.Selected>(
            await fixture.Store.SelectNthPreviousAsync(
                fixture.Lineage(),
                0
            )
        );
        Assert.Equal(published, selected.Descriptor);
        Assert.Equal(
            "recap",
            Assert.Single(
                (await fixture.Store.MaterializeAsync(published))
                    .Contributions
            ).ExactText
        );
    }

    [Fact]
    public async Task ExactQuarantineLeavesOtherBuildingPublishedAndRawUntouched() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(historyPairs: 4);
        SessionCurrentLineageSnapshot lineage = fixture.Lineage();
        EventAddress rawHead = lineage.CapturedHead;
        EventAddress target = lineage.HeadToRoot[2].Address;
        EventAddress other = lineage.HeadToRoot[0].Address;
        EventAddress publishedAnchor =
            lineage.HeadToRoot[4].Address;
        EventAddress replayStart =
            lineage.HeadToRoot[6].Address;
        PublishedRecapDescriptor published =
            await fixture.PublishAsync(
                publishedAnchor,
                replayStart,
                content: "preserved"
            );
        await CreateBuildingAsync(
            fixture,
            target,
            replayStart,
            blockId: "roleplay.target"
        );
        await CreateBuildingAsync(
            fixture,
            other,
            replayStart,
            blockId: "roleplay.other"
        );

        _ = Assert.IsType<QuarantineBuildingResult.Quarantined>(
            await fixture.Store.QuarantineBuildingAsync(target)
        );

        Assert.Equal(rawHead, fixture.Engine.ReadCurrentHead());
        Assert.IsType<BuildingReadResult.Missing>(
            await fixture.Store.ReadBuildingAsync(target)
        );
        Assert.IsType<BuildingReadResult.Available>(
            await fixture.Store.ReadBuildingAsync(other)
        );
        var selected = Assert.IsType<DerivedRecapSelection.Selected>(
            await fixture.Store.SelectNthPreviousAsync(
                fixture.Lineage(),
                0
            )
        );
        Assert.Equal(published, selected.Descriptor);
        Assert.Equal(
            "preserved",
            Assert.Single(
                (await fixture.Store.MaterializeAsync(published))
                    .Contributions
            ).ExactText
        );
    }

    [Fact]
    public async Task SymlinkedExactBuildingIsUnavailableAndOutsideIsUntouched() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync();
        EventAddress anchor = fixture.Lineage().CapturedHead;
        string outside = Path.Combine(
            Path.GetTempPath(),
            "atelia-derived-recap-quarantine-outside",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(outside);
        string sentinel = Path.Combine(outside, "sentinel");
        await File.WriteAllTextAsync(sentinel, "preserve");
        try {
            Directory.CreateSymbolicLink(
                fixture.Store.GetBuildingPathForTest(anchor),
                outside
            );

            var unavailable =
                Assert.IsType<
                    QuarantineBuildingResult.Unavailable
                >(
                    await fixture.Store.QuarantineBuildingAsync(
                        anchor
                    )
                );

            Assert.Contains(
                "symbolic link",
                unavailable.Reason,
                StringComparison.OrdinalIgnoreCase
            );
            Assert.Equal(
                "preserve",
                await File.ReadAllTextAsync(sentinel)
            );
            Assert.False(
                Directory.Exists(
                    fixture.Store.BuildingQuarantineRootForTest
                )
            );
        }
        finally {
            try {
                Directory.Delete(outside, recursive: true);
            }
            catch {
            }
        }
    }

    private static async ValueTask CreateBuildingAsync(
        RecapStoreFixture fixture,
        EventAddress anchor,
        EventAddress replayStart,
        string blockId = "roleplay.self"
    ) {
        RecapBlockPlan plan = fixture.CreateMaintainPlan(
            anchor,
            replayStart,
            blockId
        );
        _ = Assert.IsType<CreateBuildingResult.Created>(
            await fixture.Store.CreateBuildingAsync(
                DerivedRecapCodec.CreateManifest(
                    fixture.Engine.BranchRefId,
                    anchor,
                    [plan]
                )
            )
        );
    }
}
