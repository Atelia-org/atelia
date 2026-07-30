using Atelia.Data;
using Atelia.EventJournal;
using Xunit;

namespace Atelia.SessionJournal.DerivedRecap.Store.Tests;

public sealed class DerivedRecapCurrentLineageBuildingTests {
    [Fact]
    public async Task NoCurrentLineageMembershipIsNone() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync();

        Assert.IsType<CurrentLineageBuildingSelection.None>(
            await fixture.Store.SelectCurrentLineageBuildingAsync(
                fixture.Lineage()
            )
        );
    }

    [Fact]
    public async Task DamagedStoreIsTypedStoreUnavailable() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync();
        Directory.Delete(
            Path.Combine(
                fixture.Store.StoreRootPathForTest,
                "building"
            )
        );

        Assert.IsType<
            CurrentLineageBuildingSelection.StoreUnavailable
        >(
            await fixture.Store.SelectCurrentLineageBuildingAsync(
                fixture.Lineage()
            )
        );
    }

    [Fact]
    public async Task SingleCurrentLineageMembershipIsReadExactly() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(historyPairs: 3);
        SessionCurrentLineageSnapshot lineage = fixture.Lineage();
        EventAddress anchor = lineage.HeadToRoot[0].Address;
        DerivedRecapSetManifest manifest = CreateManifest(
            fixture,
            anchor,
            lineage.HeadToRoot[2].Address
        );
        _ = Assert.IsType<CreateBuildingResult.Created>(
            await fixture.Store.CreateBuildingAsync(manifest)
        );

        var available =
            Assert.IsType<
                CurrentLineageBuildingSelection.Available
            >(
                await fixture.Store
                    .SelectCurrentLineageBuildingAsync(lineage)
            );

        Assert.Equal(anchor, available.Snapshot.Descriptor
            .SetAdmissionAnchor);
        Assert.Equal(
            manifest.ManifestPayloadSha256,
            available.Snapshot.Descriptor.ManifestPayloadSha256
        );
    }

    [Fact]
    public async Task DotStagingAndOffLineageMembershipAreIgnored() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(historyPairs: 3);
        SessionCurrentLineageSnapshot lineage = fixture.Lineage();
        EventAddress offLineage = new(
            SizedPtr.FromPacked(ulong.MaxValue),
            uint.MaxValue,
            AddressHint.None
        );
        _ = Assert.IsType<CreateBuildingResult.Created>(
            await fixture.Store.CreateBuildingAsync(
                CreateManifest(
                    fixture,
                    offLineage,
                    lineage.HeadToRoot[2].Address
                )
            )
        );
        string buildingRoot = Path.GetDirectoryName(
            fixture.Store.GetBuildingPathForTest(
                lineage.CapturedHead
            )
        )!;
        Directory.CreateDirectory(
            Path.Combine(buildingRoot, ".pending.create.test")
        );

        Assert.IsType<CurrentLineageBuildingSelection.None>(
            await fixture.Store.SelectCurrentLineageBuildingAsync(
                lineage
            )
        );
    }

    [Fact]
    public async Task MultipleCurrentLineageMembershipsAreNotGuessed() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(historyPairs: 4);
        SessionCurrentLineageSnapshot lineage = fixture.Lineage();
        EventAddress newer = lineage.HeadToRoot[0].Address;
        EventAddress older = lineage.HeadToRoot[2].Address;
        EventAddress replayStart = lineage.HeadToRoot[4].Address;
        _ = Assert.IsType<CreateBuildingResult.Created>(
            await fixture.Store.CreateBuildingAsync(
                CreateManifest(fixture, older, replayStart, "older")
            )
        );
        _ = Assert.IsType<CreateBuildingResult.Created>(
            await fixture.Store.CreateBuildingAsync(
                CreateManifest(fixture, newer, replayStart, "newer")
            )
        );

        var multiple =
            Assert.IsType<
                CurrentLineageBuildingSelection.Multiple
            >(
                await fixture.Store
                    .SelectCurrentLineageBuildingAsync(lineage)
            );

        Assert.Equal([newer, older], multiple.SetAdmissionAnchors);
    }

    [Fact]
    public async Task BuildingNotNewerThanLatestPublishedIsStale() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(historyPairs: 4);
        SessionCurrentLineageSnapshot lineage = fixture.Lineage();
        EventAddress publishedAnchor =
            lineage.HeadToRoot[0].Address;
        EventAddress staleBuilding =
            lineage.HeadToRoot[2].Address;
        EventAddress replayStart =
            lineage.HeadToRoot[4].Address;
        _ = Assert.IsType<CreateBuildingResult.Created>(
            await fixture.Store.CreateBuildingAsync(
                CreateManifest(
                    fixture,
                    staleBuilding,
                    replayStart,
                    "stale"
                )
            )
        );
        _ = await fixture.PublishAsync(
            publishedAnchor,
            replayStart,
            blockId: "published"
        );

        var stale =
            Assert.IsType<
                CurrentLineageBuildingSelection.Stale
            >(
                await fixture.Store
                    .SelectCurrentLineageBuildingAsync(lineage)
            );

        Assert.Equal(staleBuilding, stale.SetAdmissionAnchor);
        Assert.Equal(publishedAnchor, stale.LatestPublishedAnchor);
    }

    [Fact]
    public async Task InvalidExactMembershipIsTypedInvalid() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(historyPairs: 3);
        SessionCurrentLineageSnapshot lineage = fixture.Lineage();
        EventAddress anchor = lineage.CapturedHead;
        _ = Assert.IsType<CreateBuildingResult.Created>(
            await fixture.Store.CreateBuildingAsync(
                CreateManifest(
                    fixture,
                    anchor,
                    lineage.HeadToRoot[2].Address
                )
            )
        );
        await File.WriteAllTextAsync(
            Path.Combine(
                fixture.Store.GetBuildingPathForTest(anchor),
                "manifest.json"
            ),
            "damaged"
        );

        var invalid =
            Assert.IsType<
                CurrentLineageBuildingSelection.Invalid
            >(
                await fixture.Store
                    .SelectCurrentLineageBuildingAsync(lineage)
            );

        Assert.Equal(anchor, invalid.SetAdmissionAnchor);
        Assert.NotEmpty(invalid.Defects);
    }

    [Fact]
    public async Task TrustedInstallerRejectsOtherActiveBuildingButLowLevelDoesNot() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(historyPairs: 4);
        SessionCurrentLineageSnapshot lineage = fixture.Lineage();
        EventAddress target = lineage.HeadToRoot[0].Address;
        EventAddress other = lineage.HeadToRoot[2].Address;
        EventAddress replayStart =
            lineage.HeadToRoot[4].Address;
        _ = Assert.IsType<CreateBuildingResult.Created>(
            await fixture.Store.CreateBuildingAsync(
                CreateManifest(
                    fixture,
                    other,
                    replayStart,
                    "other"
                )
            )
        );
        var installer = new DerivedRecapBuildingInstaller(
            fixture.Store,
            fixture.Engine
        );

        var conflict =
            Assert.IsType<
                CreateBuildingResult.ActiveBuildingConflict
            >(
                await installer.InstallAsync(
                    CreateManifest(
                        fixture,
                        target,
                        replayStart,
                        "target"
                    ),
                    lineage.CapturedHead
                )
            );

        Assert.Equal([other], conflict.SetAdmissionAnchors);
        Assert.IsType<BuildingReadResult.Missing>(
            await fixture.Store.ReadBuildingAsync(target)
        );

        _ = Assert.IsType<CreateBuildingResult.Created>(
            await fixture.Store.CreateBuildingAsync(
                CreateManifest(
                    fixture,
                    target,
                    replayStart,
                    "target"
                )
            )
        );
    }

    private static DerivedRecapSetManifest CreateManifest(
        RecapStoreFixture fixture,
        EventAddress anchor,
        EventAddress replayStart,
        string blockId = "roleplay.self"
    ) => DerivedRecapCodec.CreateManifest(
        fixture.Engine.BranchRefId,
        anchor,
        [fixture.CreateMaintainPlan(
            anchor,
            replayStart,
            blockId
        )]
    );
}
