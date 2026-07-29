using System.Text;
using Atelia.Data;
using Atelia.EventJournal;
using Xunit;

namespace Atelia.SessionJournal.DerivedRecap.Store.Tests;

public sealed class DerivedRecapStoreTests {
    [Fact]
    public async Task MissingOrDamagedRootIsStoreUnavailableNotEmpty() {
        using SessionJournalEngine engine = SessionJournalEngine.Create(
            NewPath(),
            new SessionCreateOptions(
                "model-a",
                "system-a",
                "surface-a"
            )
        );
        try {
            DerivedRecapStore store = DerivedRecapStore.Open(
                engine.Path,
                engine.BranchRefId
            );
            SessionCurrentLineageSnapshot lineage =
                engine.ReadCurrentLineageHeaders();

            Assert.IsType<DerivedRecapSelection.StoreUnavailable>(
                await store.SelectNthPreviousAsync(lineage, 0)
            );

            await store.CreateAsync();
            Directory.Delete(
                Path.Combine(
                    store.StoreRootPathForTest,
                    "building"
                )
            );
            Assert.IsType<DerivedRecapSelection.StoreUnavailable>(
                await store.SelectNthPreviousAsync(lineage, 0)
            );
        }
        finally {
            string path = engine.Path;
            engine.Dispose();
            TryDelete(path);
        }
    }

    [Fact]
    public async Task CreateAndResetInstallHealthyRootAndQuarantineOld() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync();
        string refsRoot = Path.GetDirectoryName(
            fixture.Store.StoreRootPathForTest
        )!;
        await File.WriteAllTextAsync(
            Path.Combine(
                fixture.Store.StoreRootPathForTest,
                "damage.txt"
            ),
            "old"
        );

        await fixture.Store.ResetAsync();

        Assert.False(
            File.Exists(
                Path.Combine(
                    fixture.Store.StoreRootPathForTest,
                    "damage.txt"
                )
            )
        );
        Assert.Single(
            Directory.EnumerateFileSystemEntries(
                refsRoot,
                $".{fixture.Engine.BranchRefId}.quarantine.*"
            )
        );
        Assert.IsType<DerivedRecapSelection.EmptyLineage>(
            await fixture.Store.SelectNthPreviousAsync(
                fixture.Lineage(),
                0
            )
        );
    }

    [Fact]
    public async Task FakeBlockFlowsBuildingPublishSelectMaterialize() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync();
        SessionCurrentLineageSnapshot lineage = fixture.Lineage();
        EventAddress anchor = lineage.CapturedHead;
        EventAddress replayStart = lineage.HeadToRoot[2].Address;
        RecapBlockPlan plan =
            fixture.CreateMaintainPlan(anchor, replayStart);
        DerivedRecapSetManifest manifest =
            DerivedRecapCodec.CreateManifest(
                fixture.Engine.BranchRefId,
                anchor,
                [plan]
            );

        await fixture.Store.CreateBuildingAsync(manifest, []);
        Assert.IsType<DerivedRecapSelection.EmptyLineage>(
            await fixture.Store.SelectNthPreviousAsync(lineage, 0)
        );
        RecapPublishability incomplete =
            await fixture.Store.CanPublishAsync(anchor, lineage);
        Assert.False(incomplete.IsPublishable);

        DerivedRecapBlock block =
            DerivedRecapCodec.CreateBlock(
                plan,
                anchor,
                "finite recap"
            );
        await fixture.Store.WriteFinalBlockAsync(anchor, block);
        Assert.True(
            (await fixture.Store.CanPublishAsync(anchor, lineage))
                .IsPublishable
        );
        PublishedRecapDescriptor published =
            await fixture.Store.PublishAsync(anchor, lineage);

        var selected =
            Assert.IsType<DerivedRecapSelection.Selected>(
                await fixture.Store.SelectNthPreviousAsync(lineage, 0)
            );
        Assert.Equal(published, selected.Descriptor);
        DerivedRecapMaterialization materialized =
            await fixture.Store.MaterializeAsync(selected.Descriptor);
        SessionContextContribution contribution =
            Assert.Single(materialized.Contributions);
        Assert.Equal("finite recap", contribution.ExactText);
        Assert.Equal(anchor, contribution.AbsorbedThrough);
    }

    [Fact]
    public async Task SealedBuildingDoesNotCountAndCanFinishPromotion() {
        var simulatedCrash = new IOException("simulated crash");
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(
                new RecapStoreTestHooks(
                    AfterPublicationSealed: () =>
                        throw simulatedCrash
                )
            );
        SessionCurrentLineageSnapshot lineage = fixture.Lineage();
        EventAddress anchor = lineage.CapturedHead;
        EventAddress replayStart = lineage.HeadToRoot[2].Address;
        RecapBlockPlan plan =
            fixture.CreateMaintainPlan(anchor, replayStart);
        DerivedRecapSetManifest manifest =
            DerivedRecapCodec.CreateManifest(
                fixture.Engine.BranchRefId,
                anchor,
                [plan]
            );
        await fixture.Store.CreateBuildingAsync(manifest, []);
        await fixture.Store.WriteFinalBlockAsync(
            anchor,
            DerivedRecapCodec.CreateBlock(plan, anchor, "recap")
        );

        IOException observed = await Assert.ThrowsAsync<IOException>(
            async () => await fixture.Store.PublishAsync(anchor, lineage)
        );
        Assert.Same(simulatedCrash, observed);
        Assert.IsType<DerivedRecapSelection.EmptyLineage>(
            await fixture.Store.SelectNthPreviousAsync(lineage, 0)
        );

        DerivedRecapStore reopened = DerivedRecapStore.Open(
            fixture.Path,
            fixture.Engine.BranchRefId
        );
        _ = await reopened.PublishAsync(anchor, lineage);
        Assert.IsType<DerivedRecapSelection.Selected>(
            await reopened.SelectNthPreviousAsync(lineage, 0)
        );
    }

    [Fact]
    public async Task PublishSealsEnvelopeBeforeDirectoryPromotionAndBarriers() {
        var observed = new List<(RecapIoPoint Point, string Path)>();
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(
                new RecapStoreTestHooks(
                    IoObserver: (point, path) =>
                        observed.Add((point, path))
                ),
                historyPairs: 1
            );
        SessionCurrentLineageSnapshot lineage = fixture.Lineage();
        EventAddress anchor = lineage.CapturedHead;
        RecapBlockPlan plan = fixture.CreateMaintainPlan(
            anchor,
            lineage.HeadToRoot[2].Address
        );
        DerivedRecapSetManifest manifest =
            DerivedRecapCodec.CreateManifest(
                fixture.Engine.BranchRefId,
                anchor,
                [plan]
            );
        await fixture.Store.CreateBuildingAsync(manifest, []);
        await fixture.Store.WriteFinalBlockAsync(
            anchor,
            DerivedRecapCodec.CreateBlock(plan, anchor, "recap")
        );
        observed.Clear();

        await fixture.Store.PublishAsync(anchor, lineage);

        int envelopeInstall = observed.FindIndex(item =>
            item.Point == RecapIoPoint.FileInstalled
            && item.Path.EndsWith(
                "publication.json",
                StringComparison.Ordinal
            )
        );
        int promotion = observed.FindIndex(item =>
            item.Point == RecapIoPoint.DirectoryPromoted
            && item.Path
                == fixture.Store.GetPublishedPathForTest(anchor)
        );
        int sourceParentBarrier = observed.FindLastIndex(item =>
            item.Point == RecapIoPoint.DirectoryBarrier
            && item.Path.EndsWith(
                Path.DirectorySeparatorChar + "building",
                StringComparison.Ordinal
            )
        );
        int destinationParentBarrier = observed.FindLastIndex(item =>
            item.Point == RecapIoPoint.DirectoryBarrier
            && item.Path.EndsWith(
                Path.DirectorySeparatorChar + "published",
                StringComparison.Ordinal
            )
        );

        Assert.True(envelopeInstall >= 0);
        Assert.True(promotion > envelopeInstall);
        Assert.True(sourceParentBarrier > promotion);
        Assert.True(destinationParentBarrier > sourceParentBarrier);
    }

    [Fact]
    public async Task StrictOrdinalCountsInvalidExactSetWithoutFallback() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync();
        SessionCurrentLineageSnapshot firstLineage = fixture.Lineage();
        EventAddress first = firstLineage.CapturedHead;
        await fixture.PublishAsync(
            first,
            firstLineage.HeadToRoot[2].Address,
            content: "older"
        );
        EventAddress second = fixture.AppendPair("newer");
        SessionCurrentLineageSnapshot secondLineage =
            fixture.Lineage();
        await fixture.PublishAsync(
            second,
            first,
            content: "newer"
        );
        string newerBlock = Path.Combine(
            fixture.Store.GetPublishedPathForTest(second),
            "blocks",
            "roleplay.self.json"
        );
        await File.WriteAllTextAsync(newerBlock, "{}");

        var invalid = Assert.IsType<
            DerivedRecapSelection.ExactPublishedSetInvalid
        >(
            await fixture.Store.SelectNthPreviousAsync(
                secondLineage,
                0
            )
        );
        Assert.Equal(second, invalid.SetAdmissionAnchor);

        var older = Assert.IsType<DerivedRecapSelection.Selected>(
            await fixture.Store.SelectNthPreviousAsync(
                secondLineage,
                1
            )
        );
        Assert.Equal(first, older.Descriptor.SetAdmissionAnchor);
    }

    [Fact]
    public async Task InvalidPlanConstructorMapsToExactInvalid() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync();
        SessionCurrentLineageSnapshot lineage = fixture.Lineage();
        EventAddress anchor = lineage.CapturedHead;
        await fixture.PublishAsync(
            anchor,
            lineage.HeadToRoot[2].Address
        );
        string publicationPath = Path.Combine(
            fixture.Store.GetPublishedPathForTest(anchor),
            "publication.json"
        );
        string publication =
            await File.ReadAllTextAsync(publicationPath);
        publication = publication.Replace(
            "\"maxContentUtf8Bytes\":262144",
            "\"maxContentUtf8Bytes\":0",
            StringComparison.Ordinal
        );
        await File.WriteAllTextAsync(publicationPath, publication);

        Assert.IsType<
            DerivedRecapSelection.ExactPublishedSetInvalid
        >(
            await fixture.Store.SelectNthPreviousAsync(lineage, 0)
        );
    }

    [Fact]
    public async Task NthPreviousAndOrdinalUnavailableAreRawLineageBased() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync();
        SessionCurrentLineageSnapshot initial = fixture.Lineage();
        EventAddress first = initial.CapturedHead;
        await fixture.PublishAsync(
            first,
            initial.HeadToRoot[2].Address,
            content: "first"
        );
        EventAddress second = fixture.AppendPair("second");
        await fixture.PublishAsync(
            second,
            first,
            content: "second"
        );
        SessionCurrentLineageSnapshot lineage = fixture.Lineage();

        Assert.Equal(
            second,
            Assert.IsType<DerivedRecapSelection.Selected>(
                await fixture.Store.SelectNthPreviousAsync(lineage, 0)
            ).Descriptor.SetAdmissionAnchor
        );
        Assert.Equal(
            first,
            Assert.IsType<DerivedRecapSelection.Selected>(
                await fixture.Store.SelectNthPreviousAsync(lineage, 1)
            ).Descriptor.SetAdmissionAnchor
        );
        Assert.IsType<DerivedRecapSelection.OrdinalUnavailable>(
            await fixture.Store.SelectNthPreviousAsync(lineage, 2)
        );
    }

    [Fact]
    public async Task FinalGateRejectsRetroactivePublication() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(historyPairs: 5);
        SessionCurrentLineageSnapshot lineage = fixture.Lineage();
        EventAddress newer = lineage.HeadToRoot[0].Address;
        EventAddress older = lineage.HeadToRoot[2].Address;
        EventAddress olderReplay = lineage.HeadToRoot[4].Address;

        RecapBlockPlan olderPlan =
            fixture.CreateMaintainPlan(older, olderReplay);
        DerivedRecapSetManifest olderManifest =
            DerivedRecapCodec.CreateManifest(
                fixture.Engine.BranchRefId,
                older,
                [olderPlan]
            );
        await fixture.Store.CreateBuildingAsync(olderManifest, []);
        await fixture.Store.WriteFinalBlockAsync(
            older,
            DerivedRecapCodec.CreateBlock(
                olderPlan,
                older,
                "older"
            )
        );
        await fixture.PublishAsync(
            newer,
            older,
            blockId: "roleplay.newer",
            content: "newer"
        );

        RecapPublishability result =
            await fixture.Store.CanPublishAsync(older, lineage);

        Assert.False(result.IsPublishable);
        Assert.Contains(
            result.Defects,
            static defect =>
                defect.Code == "RetroactivePublication"
        );
        await Assert.ThrowsAsync<InvalidDataException>(
            async () =>
                await fixture.Store.PublishAsync(older, lineage)
        );
    }

    [Fact]
    public async Task R0CreateRejectsInheritUntilExactSourceFreezeExists() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(historyPairs: 4);
        SessionCurrentLineageSnapshot lineage = fixture.Lineage();
        EventAddress target = lineage.HeadToRoot[0].Address;
        EventAddress source = lineage.HeadToRoot[2].Address;
        var id = new RecapBlockId("roleplay.self");
        var targetPath = new ContextHeaderBlockPath(
            ContextHeaderCarrier.System,
            id.Value
        );
        DerivedRecapFrozenInput input =
            DerivedRecapCodec.CreateFrozenInput(
                id,
                targetPath,
                target,
                "source recap"
            );
        var plan = new InheritRecapBlockPlan(
            id,
            targetPath,
            source,
            new string('a', 64),
            input.PayloadSha256
        );
        DerivedRecapSetManifest manifest =
            DerivedRecapCodec.CreateManifest(
                fixture.Engine.BranchRefId,
                target,
                [plan]
            );
        await Assert.ThrowsAsync<NotSupportedException>(
            async () =>
                await fixture.Store.CreateBuildingAsync(
                    manifest,
                    [input]
                )
        );
    }

    [Fact]
    public async Task ExistingCatchUpShapePreservesEarlyEndpoint() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(historyPairs: 5);
        SessionCurrentLineageSnapshot lineage = fixture.Lineage();
        EventAddress target = lineage.HeadToRoot[0].Address;
        EventAddress source = lineage.HeadToRoot[4].Address;
        EventAddress earlyEndpoint =
            lineage.HeadToRoot[6].Address;
        EventAddress absorbedThrough =
            lineage.HeadToRoot[8].Address;
        var id = new RecapBlockId("roleplay.self");
        var targetPath = new ContextHeaderBlockPath(
            ContextHeaderCarrier.System,
            id.Value
        );
        DerivedRecapFrozenInput input =
            DerivedRecapCodec.CreateFrozenInput(
                id,
                targetPath,
                absorbedThrough,
                "source recap"
            );
        var plan = new MaintainRecapBlockPlan(
            id,
            targetPath,
            "roleplay.autobiographical",
            new ExistingRecapMaintainSource(
                source,
                new string('a', 64),
                input.PayloadSha256
            ),
            [earlyEndpoint, target],
            EmptyRecapPriorContext.Instance
        );
        DerivedRecapSetManifest manifest =
            DerivedRecapCodec.CreateManifest(
                fixture.Engine.BranchRefId,
                target,
                [plan]
            );
        DerivedRecapSetManifest decoded =
            DerivedRecapCodec.DecodeManifest(
                DerivedRecapCodec.EncodeManifest(manifest)
            );
        var decodedPlan =
            Assert.IsType<MaintainRecapBlockPlan>(
                Assert.Single(decoded.Blocks)
            );

        Assert.Equal(
            [earlyEndpoint, target],
            decodedPlan.CatchUpThrough
        );
        Assert.True(
            lineage.HeadToRoot
                    .Select(static node => node.Address)
                    .ToList()
                    .IndexOf(earlyEndpoint)
                > lineage.HeadToRoot
                    .Select(static node => node.Address)
                    .ToList()
                    .IndexOf(source)
        );
    }

    [Fact]
    public async Task PublishDestinationExistsFailsWithoutOverwrite() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync();
        SessionCurrentLineageSnapshot lineage = fixture.Lineage();
        EventAddress anchor = lineage.CapturedHead;
        RecapBlockPlan plan = fixture.CreateMaintainPlan(
            anchor,
            lineage.HeadToRoot[2].Address
        );
        DerivedRecapSetManifest manifest =
            DerivedRecapCodec.CreateManifest(
                fixture.Engine.BranchRefId,
                anchor,
                [plan]
            );
        await fixture.Store.CreateBuildingAsync(manifest, []);
        await fixture.Store.WriteFinalBlockAsync(
            anchor,
            DerivedRecapCodec.CreateBlock(plan, anchor, "candidate")
        );
        string destination =
            fixture.Store.GetPublishedPathForTest(anchor);
        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(
            Path.Combine(destination, "sentinel"),
            "preserve"
        );

        await Assert.ThrowsAsync<IOException>(
            async () =>
                await fixture.Store.PublishAsync(anchor, lineage)
        );
        Assert.Equal(
            "preserve",
            await File.ReadAllTextAsync(
                Path.Combine(destination, "sentinel")
            )
        );
        Assert.True(
            Directory.Exists(
                fixture.Store.GetBuildingPathForTest(anchor)
            )
        );
    }

    [Fact]
    public async Task SymlinkedRequiredDirectoryMakesStoreUnavailable() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync();
        string outside = NewPath();
        Directory.CreateDirectory(outside);
        try {
            string building = Path.Combine(
                fixture.Store.StoreRootPathForTest,
                "building"
            );
            Directory.Delete(building);
            Directory.CreateSymbolicLink(building, outside);

            Assert.IsType<DerivedRecapSelection.StoreUnavailable>(
                await fixture.Store.SelectNthPreviousAsync(
                    fixture.Lineage(),
                    0
                )
            );
        }
        finally {
            TryDelete(outside);
        }
    }

    [Fact]
    public async Task RewindMakesPublishedAnchorInvisible() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync();
        SessionCurrentLineageSnapshot lineage = fixture.Lineage();
        EventAddress published = lineage.CapturedHead;
        EventAddress rewindTo = lineage.HeadToRoot[2].Address;
        await fixture.PublishAsync(
            published,
            rewindTo,
            content: "abandoned"
        );
        RefId refId = fixture.Engine.BranchRefId;
        fixture.Engine.Dispose();
        using (var journal =
               EventJournal.EventJournal.OpenExisting(fixture.Path)) {
            journal.MoveRef(refId, published, rewindTo).Unwrap();
        }
        fixture.ReopenEngine();

        Assert.IsType<DerivedRecapSelection.EmptyLineage>(
            await fixture.Store.SelectNthPreviousAsync(
                fixture.Lineage(),
                0
            )
        );
    }

    [Fact]
    public async Task MaterializationDoubleReadsEnvelopeToken() {
        string? publicationPath = null;
        byte[]? replacement = null;
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(
                new RecapStoreTestHooks(
                    BeforeMaterializationEnvelopeRecheck: () =>
                        File.WriteAllBytes(
                            publicationPath!,
                            replacement!
                        )
                )
            );
        SessionCurrentLineageSnapshot lineage = fixture.Lineage();
        EventAddress anchor = lineage.CapturedHead;
        EventAddress replay = lineage.HeadToRoot[2].Address;
        RecapBlockPlan plan =
            fixture.CreateMaintainPlan(anchor, replay);
        DerivedRecapSetManifest manifest =
            DerivedRecapCodec.CreateManifest(
                fixture.Engine.BranchRefId,
                anchor,
                [plan]
            );
        await fixture.Store.CreateBuildingAsync(manifest, []);
        DerivedRecapBlock block =
            DerivedRecapCodec.CreateBlock(plan, anchor, "old");
        await fixture.Store.WriteFinalBlockAsync(anchor, block);
        PublishedRecapDescriptor descriptor =
            await fixture.Store.PublishAsync(anchor, lineage);
        publicationPath = Path.Combine(
            fixture.Store.GetPublishedPathForTest(anchor),
            "publication.json"
        );
        DerivedRecapBlock changed =
            DerivedRecapCodec.CreateBlock(plan, anchor, "new");
        replacement = DerivedRecapCodec.EncodePublication(
            DerivedRecapCodec.CreatePublication(manifest, [changed])
        );

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await fixture.Store.MaterializeAsync(descriptor)
        );
    }

    [Fact]
    public async Task TenThousandColdHeadersUsePointLookupSemantics() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync();
        var nodes = new SessionCurrentLineageHeader[10_000];
        EventAddress? parent = null;
        for (int index = nodes.Length - 1; index >= 0; index--) {
            var address = new EventAddress(
                SizedPtr.FromPacked((ulong)index + 1),
                1,
                AddressHint.None
            );
            nodes[index] = new SessionCurrentLineageHeader(
                address,
                parent,
                SessionEventKind.ObservationAccepted
            );
            parent = address;
        }
        var snapshot = new SessionCurrentLineageSnapshot(
            nodes[0].Address,
            Array.AsReadOnly(nodes),
            new SessionCurrentLineageDiagnostics(
                HeaderVisits: nodes.Length,
                PayloadReads: 0,
                DecodedPayloadBytes: 0
            )
        );

        Assert.IsType<DerivedRecapSelection.EmptyLineage>(
            await fixture.Store.SelectNthPreviousAsync(snapshot, 0)
        );
        Assert.Equal(0, snapshot.Diagnostics.PayloadReads);
    }

    private static string NewPath()
        => Path.Combine(
            Path.GetTempPath(),
            "atelia-derived-recap-store-tests",
            Guid.NewGuid().ToString("N")
        );

    private static void TryDelete(string path) {
        try {
            if (Directory.Exists(path)) {
                Directory.Delete(path, recursive: true);
            }
        }
        catch {
        }
    }
}
