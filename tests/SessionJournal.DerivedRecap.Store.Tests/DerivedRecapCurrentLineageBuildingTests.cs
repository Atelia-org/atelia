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
        DerivedRecapLineageView lineage = fixture.Lineage();
        EventAddress anchor = lineage.CurrentPrefix.HeadToOldest[0].Address;
        DerivedRecapSetManifest manifest = CreateManifest(
            fixture,
            anchor,
            lineage.CurrentPrefix.HeadToOldest[2].Address
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
        Assert.Empty(typeof(BuildingPlanHandle).GetConstructors());
        Assert.Empty(typeof(BuildingBlockWriteAuthority)
            .GetConstructors());
    }

    [Fact]
    public async Task DotStagingAndOffLineageMembershipAreIgnored() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(historyPairs: 3);
        DerivedRecapLineageView initialLineage = fixture.Lineage();
        EventAddress offLineage = initialLineage.CapturedHead;
        EventAddress rewindTarget =
            initialLineage.CurrentPrefix.HeadToOldest[2].Address;
        _ = Assert.IsType<CreateBuildingResult.Created>(
            await fixture.Store.CreateBuildingAsync(
                CreateManifest(
                    fixture,
                    offLineage,
                    rewindTarget
                )
            )
        );
        Assert.True(
            fixture.Engine.MoveCurrentHeadForTest(
                offLineage,
                rewindTarget
            )
        );
        DerivedRecapLineageView lineage = fixture.Lineage();
        string buildingRoot = Path.GetDirectoryName(
            fixture.Store.GetBuildingPathForTest(
                lineage.CapturedHead
            )
        )!;
        Directory.CreateDirectory(
            Path.Combine(buildingRoot, ".pending.create.test")
        );
        Directory.CreateDirectory(
            Path.Combine(buildingRoot, ".staging-create-test")
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
        DerivedRecapLineageView lineage = fixture.Lineage();
        EventAddress newer = lineage.CurrentPrefix.HeadToOldest[0].Address;
        EventAddress older = lineage.CurrentPrefix.HeadToOldest[2].Address;
        EventAddress replayStart = lineage.CurrentPrefix.HeadToOldest[4].Address;
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
        DerivedRecapLineageView lineage = fixture.Lineage();
        EventAddress publishedAnchor =
            lineage.CurrentPrefix.HeadToOldest[0].Address;
        EventAddress staleBuilding =
            lineage.CurrentPrefix.HeadToOldest[2].Address;
        EventAddress replayStart =
            lineage.CurrentPrefix.HeadToOldest[4].Address;
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
        DerivedRecapLineageView lineage = fixture.Lineage();
        EventAddress anchor = lineage.CapturedHead;
        _ = Assert.IsType<CreateBuildingResult.Created>(
            await fixture.Store.CreateBuildingAsync(
                CreateManifest(
                    fixture,
                    anchor,
                    lineage.CurrentPrefix.HeadToOldest[2].Address
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
        DerivedRecapLineageView lineage = fixture.Lineage();
        EventAddress target = lineage.CurrentPrefix.HeadToOldest[0].Address;
        EventAddress other = lineage.CurrentPrefix.HeadToOldest[2].Address;
        EventAddress replayStart =
            lineage.CurrentPrefix.HeadToOldest[4].Address;
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

    [Fact]
    public async Task BuildingBeyondCurrentPrefixIsTypedWithoutExactRead() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(historyPairs: 1);
        DerivedRecapLineageView initial = fixture.Lineage();
        EventAddress oldAnchor = initial.CapturedHead;
        _ = Assert.IsType<CreateBuildingResult.Created>(
            await fixture.Store.CreateBuildingAsync(
                CreateManifest(
                    fixture,
                    oldAnchor,
                    initial.CurrentPrefix.HeadToOldest[^2].Address
                )
            )
        );
        await File.WriteAllTextAsync(
            Path.Combine(
                fixture.Store.GetBuildingPathForTest(oldAnchor),
                "manifest.json"
            ),
            "damaged"
        );
        for (int index = 0; index < 257; index++) {
            _ = fixture.AppendPair($"tail-{index}");
        }

        var beyond = Assert.IsType<
            CurrentLineageBuildingSelection.BeyondPrefix
        >(await fixture.Lineage().SelectCurrentBuildingAsync());

        Assert.Equal(oldAnchor, beyond.Evidence.RequiredAnchor);
        Assert.Equal(513, beyond.Evidence.HeaderCount);
    }

    [Fact]
    public async Task TruncatedShuffledInventoryOverCapWinsBeforeClassification() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(historyPairs: 1);
        EventAddress beyondAnchor = fixture.Lineage().CapturedHead;
        for (int index = 0; index < 257; index++) {
            _ = fixture.AppendPair($"tail-{index}");
        }
        string buildingRoot = Path.GetDirectoryName(
            fixture.Store.GetBuildingPathForTest(beyondAnchor)
        )!;
        var entries = new List<string> {
            fixture.Store.GetBuildingPathForTest(beyondAnchor),
            Path.Combine(buildingRoot, ".staging-over-cap"),
            Path.Combine(buildingRoot, "malformed-over-cap")
        };
        for (int index = 0;
             index < DerivedRecapStore.MaxBuildingInventoryEntries - 2;
             index++) {
            var address = new EventAddress(
                SizedPtr.FromPacked(ulong.MaxValue - (ulong)index),
                (uint)index + 1,
                AddressHint.None
            );
            entries.Add(
                fixture.Store.GetBuildingPathForTest(address)
            );
        }
        Assert.Equal(
            DerivedRecapStore.MaxBuildingInventoryEntries + 1,
            entries.Count
        );
        foreach (string entry in entries
                     .Select(static (path, index) => (path, index))
                     .OrderBy(item =>
                         item.index * 37 % entries.Count
                     )
                     .Select(static item => item.path)) {
            Directory.CreateDirectory(entry);
        }

        Assert.IsType<
            CurrentLineageBuildingSelection.StoreUnavailable
        >(await fixture.Lineage().SelectCurrentBuildingAsync());
    }

    private static DerivedRecapSetManifest CreateManifest(
        RecapStoreFixture fixture,
        EventAddress anchor,
        EventAddress replayStart,
        string blockId = "roleplay.self"
    ) => RecapWireTestFacts.CreateManifest(
        fixture.Engine,
        anchor,
        [fixture.CreateMaintainPlan(
            anchor,
            replayStart,
            blockId
        )]
    );
}
