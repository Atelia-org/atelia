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
            DerivedRecapLineageView lineage =
                DerivedRecapLineageView.Capture(store, engine);

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
        DerivedRecapLineageView lineage = fixture.Lineage();
        EventAddress anchor = lineage.CapturedHead;
        EventAddress replayStart = lineage.CurrentPrefix.HeadToOldest[2].Address;
        RecapBlockPlan plan =
            fixture.CreateMaintainPlan(anchor, replayStart);
        DerivedRecapSetManifest manifest =
            DerivedRecapCodec.CreateManifest(
                fixture.Engine.BranchRefId,
                anchor,
                [plan]
            );

        await fixture.Store.CreateBuildingAsync(manifest);
        Assert.IsType<DerivedRecapSelection.EmptyLineage>(
            await fixture.Store.SelectNthPreviousAsync(lineage, 0)
        );
        RecapPublishability incomplete =
            await fixture.Publisher.CanPublishAsync(anchor);
        Assert.IsType<RecapPublishability.NotPublishable>(incomplete);

        DerivedRecapBlock block =
            DerivedRecapCodec.CreateBlock(
                plan,
                anchor,
                "finite recap"
            );
        await RecapStoreTestDriver.InstallFinalAsync(
            fixture.Store,
            anchor, block);
        Assert.IsType<RecapPublishability.Publishable>(
            await fixture.Publisher.CanPublishAsync(anchor)
        );
        PublishedRecapDescriptor published = Assert.IsType<
            PublishRecapResult.Published
        >(await fixture.Publisher.PublishAsync(anchor)).Descriptor;

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
        DerivedRecapLineageView lineage = fixture.Lineage();
        EventAddress anchor = lineage.CapturedHead;
        EventAddress replayStart = lineage.CurrentPrefix.HeadToOldest[2].Address;
        RecapBlockPlan plan =
            fixture.CreateMaintainPlan(anchor, replayStart);
        DerivedRecapSetManifest manifest =
            DerivedRecapCodec.CreateManifest(
                fixture.Engine.BranchRefId,
                anchor,
                [plan]
            );
        await fixture.Store.CreateBuildingAsync(manifest);
        await RecapStoreTestDriver.InstallFinalAsync(
            fixture.Store,

            anchor,
            DerivedRecapCodec.CreateBlock(plan, anchor, "recap")
        );

        var observed = Assert.IsType<PublishRecapResult.StoreUnavailable>(
            await fixture.Publisher.PublishAsync(anchor)
        );
        Assert.Equal(simulatedCrash.Message, observed.Reason);
        Assert.IsType<DerivedRecapSelection.EmptyLineage>(
            await fixture.Store.SelectNthPreviousAsync(lineage, 0)
        );

        DerivedRecapStore reopened = DerivedRecapStore.Open(
            fixture.Path,
            fixture.Engine.BranchRefId
        );
        var reopenedPublisher = new DerivedRecapPublisher(
            reopened,
            fixture.Engine
        );
        _ = await reopenedPublisher.PublishAsync(anchor);
        Assert.IsType<DerivedRecapSelection.Selected>(
            await reopened.SelectNthPreviousAsync(lineage, 0)
        );
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("noncanonical")]
    [InlineData("oversized")]
    public async Task DamagedBuildingPublicationCandidateIsResealed(
        string damage
    ) {
        int sealCount = 0;
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(
                new RecapStoreTestHooks(
                    AfterPublicationSealed: () => {
                        if (++sealCount == 1) {
                            throw new IOException("stop after first seal");
                        }
                    }
                )
            );
        DerivedRecapLineageView lineage = fixture.Lineage();
        EventAddress anchor = lineage.CapturedHead;
        RecapBlockPlan plan = fixture.CreateMaintainPlan(
            anchor,
            lineage.CurrentPrefix.HeadToOldest[2].Address
        );
        _ = await fixture.Store.CreateBuildingAsync(
            DerivedRecapCodec.CreateManifest(
                fixture.Engine.BranchRefId,
                anchor,
                [plan]
            )
        );
        await RecapStoreTestDriver.InstallFinalAsync(
            fixture.Store,
            anchor,
            DerivedRecapCodec.CreateBlock(plan, anchor, "recap")
        );
        Assert.IsType<PublishRecapResult.StoreUnavailable>(
            await fixture.Publisher.PublishAsync(anchor)
        );

        string candidatePath = Path.Combine(
            fixture.Store.GetBuildingPathForTest(anchor),
            "publication.json"
        );
        switch (damage) {
            case "malformed":
                await File.WriteAllTextAsync(candidatePath, "{");
                break;
            case "noncanonical":
                await File.AppendAllTextAsync(candidatePath, "\n");
                break;
            case "oversized":
                await File.WriteAllBytesAsync(
                    candidatePath,
                    new byte[checked(
                        (int)DerivedRecapStore.MaxPublicationBytes + 1
                    )]
                );
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(damage));
        }

        PublishedRecapDescriptor descriptor = Assert.IsType<
            PublishRecapResult.Published
        >(await fixture.Publisher.PublishAsync(anchor)).Descriptor;

        Assert.False(Directory.Exists(
            fixture.Store.GetBuildingPathForTest(anchor)
        ));
        Assert.Equal(
            descriptor,
            Assert.IsType<DerivedRecapSelection.Selected>(
                await fixture.Store.SelectNthPreviousAsync(lineage, 0)
            ).Descriptor
        );
    }

    [Fact]
    public async Task StaleCandidateAfterFinalRepairIsResealed() {
        int sealCount = 0;
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(
                new RecapStoreTestHooks(
                    AfterPublicationSealed: () => {
                        if (++sealCount == 1) {
                            throw new IOException("stop after first seal");
                        }
                    }
                )
            );
        DerivedRecapLineageView lineage = fixture.Lineage();
        EventAddress anchor = lineage.CapturedHead;
        RecapBlockPlan plan = fixture.CreateMaintainPlan(
            anchor,
            lineage.CurrentPrefix.HeadToOldest[2].Address
        );
        _ = await fixture.Store.CreateBuildingAsync(
            DerivedRecapCodec.CreateManifest(
                fixture.Engine.BranchRefId,
                anchor,
                [plan]
            )
        );
        await RecapStoreTestDriver.InstallFinalAsync(
            fixture.Store,
            anchor,
            DerivedRecapCodec.CreateBlock(plan, anchor, "old recap")
        );
        Assert.IsType<PublishRecapResult.StoreUnavailable>(
            await fixture.Publisher.PublishAsync(anchor)
        );

        string finalPath = Path.Combine(
            fixture.Store.GetBuildingPathForTest(anchor),
            "blocks",
            $"{plan.RecapBlockId.Value}.json"
        );
        string checkpointPath = Path.Combine(
            fixture.Store.GetBuildingPathForTest(anchor),
            "work",
            $"{plan.RecapBlockId.Value}.json"
        );
        await File.WriteAllTextAsync(finalPath, "damaged");
        await File.WriteAllTextAsync(checkpointPath, "damaged");
        await RecapStoreTestDriver.InstallFinalAsync(
            fixture.Store,
            anchor,
            DerivedRecapCodec.CreateBlock(plan, anchor, "new recap")
        );

        PublishedRecapDescriptor descriptor = Assert.IsType<
            PublishRecapResult.Published
        >(await fixture.Publisher.PublishAsync(anchor)).Descriptor;
        DerivedRecapMaterialization materialized =
            await fixture.Store.MaterializeAsync(descriptor);

        Assert.Equal(
            "new recap",
            Assert.Single(materialized.Contributions).ExactText
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WrongKindBuildingCandidateFailsClosed(
        bool symlink
    ) {
        if (symlink && !OperatingSystem.IsLinux()) {
            return;
        }
        int sealCount = 0;
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(
                new RecapStoreTestHooks(
                    AfterPublicationSealed: () => {
                        if (++sealCount == 1) {
                            throw new IOException("stop after first seal");
                        }
                    }
                )
            );
        DerivedRecapLineageView lineage = fixture.Lineage();
        EventAddress anchor = lineage.CapturedHead;
        RecapBlockPlan plan = fixture.CreateMaintainPlan(
            anchor,
            lineage.CurrentPrefix.HeadToOldest[2].Address
        );
        _ = await fixture.Store.CreateBuildingAsync(
            DerivedRecapCodec.CreateManifest(
                fixture.Engine.BranchRefId,
                anchor,
                [plan]
            )
        );
        await RecapStoreTestDriver.InstallFinalAsync(
            fixture.Store,
            anchor,
            DerivedRecapCodec.CreateBlock(plan, anchor, "recap")
        );
        Assert.IsType<PublishRecapResult.StoreUnavailable>(
            await fixture.Publisher.PublishAsync(anchor)
        );
        string buildingPath =
            fixture.Store.GetBuildingPathForTest(anchor);
        string candidatePath = Path.Combine(
            buildingPath,
            "publication.json"
        );
        File.Delete(candidatePath);
        string? symlinkTarget = null;
        if (symlink) {
            symlinkTarget = Path.Combine(
                fixture.Path,
                "candidate-target.json"
            );
            await File.WriteAllTextAsync(symlinkTarget, "unchanged");
            File.CreateSymbolicLink(candidatePath, symlinkTarget);
        }
        else {
            Directory.CreateDirectory(candidatePath);
        }

        Assert.IsType<PublishRecapResult.StoreUnavailable>(
            await fixture.Publisher.PublishAsync(anchor)
        );

        Assert.True(Directory.Exists(buildingPath));
        Assert.False(Directory.Exists(
            fixture.Store.GetPublishedPathForTest(anchor)
        ));
        if (symlinkTarget is not null) {
            Assert.Equal("unchanged", await File.ReadAllTextAsync(
                symlinkTarget
            ));
        }
    }

    [Fact]
    public async Task PublishNeverResealsExistingPublishedMembership() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync();
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
        await File.AppendAllTextAsync(publicationPath, "\n");
        byte[] damaged = await File.ReadAllBytesAsync(publicationPath);

        Assert.IsType<PublishRecapResult.NotPublishable>(
            await fixture.Publisher.PublishAsync(anchor)
        );

        Assert.Equal(
            damaged,
            await File.ReadAllBytesAsync(publicationPath)
        );
    }

    [Fact]
    public async Task PublicationReadersShareCanonicalHealthDefinition() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync();
        DerivedRecapLineageView lineage = fixture.Lineage();
        EventAddress anchor = lineage.CapturedHead;
        PublishedRecapDescriptor descriptor =
            await fixture.PublishAsync(
                anchor,
                lineage.CurrentPrefix.HeadToOldest[2].Address
            );
        await File.AppendAllTextAsync(
            Path.Combine(
                fixture.Store.GetPublishedPathForTest(anchor),
                "publication.json"
            ),
            "\n"
        );

        Assert.IsType<DerivedRecapSelection.ExactPublishedSetInvalid>(
            await fixture.Store.SelectNthPreviousAsync(lineage, 0)
        );
        Assert.IsType<PublishedMembershipInspectionResult.Invalid>(
            await fixture.Store.InspectPublishedMembershipAsync(anchor)
        );
        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await fixture.Store.MaterializeAsync(descriptor)
        );
        Assert.IsType<PublishedPlanReadResult.Unavailable>(
            await fixture.Store.ReadPublishedPlanAsync(descriptor)
        );
        Assert.IsType<PublishedPlanAtAnchorReadResult.Unavailable>(
            await fixture.Store.ReadPublishedPlanAtAnchorAsync(anchor)
        );
        Assert.IsType<PublishedRecapSourceReadResult.Invalid>(
            await fixture.Store.ReadPublishedSourceAsync(
                descriptor,
                [new RecapBlockId("roleplay.self")]
            )
        );
        PublishedRestoreInspectionResult.Available restore =
            Assert.IsType<PublishedRestoreInspectionResult.Available>(
                await fixture.Store.InspectPublishedForRestoreAsync(
                    anchor,
                    lineage
                )
            );
        Assert.Equal(
            PublishedRestoreAuthorityKind.ManifestWitness,
            restore.Inspection.Handle.AuthorityKind
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
        DerivedRecapLineageView lineage = fixture.Lineage();
        EventAddress anchor = lineage.CapturedHead;
        RecapBlockPlan plan = fixture.CreateMaintainPlan(
            anchor,
            lineage.CurrentPrefix.HeadToOldest[2].Address
        );
        DerivedRecapSetManifest manifest =
            DerivedRecapCodec.CreateManifest(
                fixture.Engine.BranchRefId,
                anchor,
                [plan]
            );
        await fixture.Store.CreateBuildingAsync(manifest);
        await RecapStoreTestDriver.InstallFinalAsync(
            fixture.Store,

            anchor,
            DerivedRecapCodec.CreateBlock(plan, anchor, "recap")
        );
        observed.Clear();

        await fixture.Publisher.PublishAsync(anchor);

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
        DerivedRecapLineageView firstLineage = fixture.Lineage();
        EventAddress first = firstLineage.CapturedHead;
        await fixture.PublishAsync(
            first,
            firstLineage.CurrentPrefix.HeadToOldest[2].Address,
            content: "older"
        );
        EventAddress second = fixture.AppendPair("newer");
        DerivedRecapLineageView secondLineage =
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
        DerivedRecapLineageView lineage = fixture.Lineage();
        EventAddress anchor = lineage.CapturedHead;
        await fixture.PublishAsync(
            anchor,
            lineage.CurrentPrefix.HeadToOldest[2].Address
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
        DerivedRecapLineageView initial = fixture.Lineage();
        EventAddress first = initial.CapturedHead;
        await fixture.PublishAsync(
            first,
            initial.CurrentPrefix.HeadToOldest[2].Address,
            content: "first"
        );
        EventAddress second = fixture.AppendPair("second");
        await fixture.PublishAsync(
            second,
            first,
            content: "second"
        );
        DerivedRecapLineageView lineage = fixture.Lineage();

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
        DerivedRecapLineageView lineage = fixture.Lineage();
        EventAddress newer = lineage.CurrentPrefix.HeadToOldest[0].Address;
        EventAddress older = lineage.CurrentPrefix.HeadToOldest[2].Address;
        EventAddress olderReplay = lineage.CurrentPrefix.HeadToOldest[4].Address;

        RecapBlockPlan olderPlan =
            fixture.CreateMaintainPlan(older, olderReplay);
        DerivedRecapSetManifest olderManifest =
            DerivedRecapCodec.CreateManifest(
                fixture.Engine.BranchRefId,
                older,
                [olderPlan]
            );
        await fixture.Store.CreateBuildingAsync(olderManifest);
        await RecapStoreTestDriver.InstallFinalAsync(
            fixture.Store,

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
            await fixture.Publisher.CanPublishAsync(older);

        var notPublishable = Assert.IsType<
            RecapPublishability.NotPublishable
        >(result);
        Assert.Contains(
            notPublishable.Defects,
            static defect =>
                defect.Code == "RetroactivePublication"
        );
        Assert.IsType<PublishRecapResult.NotPublishable>(
            await fixture.Publisher.PublishAsync(older)
        );
    }

    [Fact]
    public async Task CreateBuildingFreezesExactInheritSource() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(historyPairs: 4);
        DerivedRecapLineageView lineage = fixture.Lineage();
        EventAddress target = lineage.CurrentPrefix.HeadToOldest[0].Address;
        EventAddress source = lineage.CurrentPrefix.HeadToOldest[2].Address;
        EventAddress replayStart =
            lineage.CurrentPrefix.HeadToOldest[^1].Address;
        var id = new RecapBlockId("roleplay.self");
        var targetPath = new ContextHeaderBlockPath(
            ContextHeaderCarrier.System,
            id.Value
        );
        PublishedRecapDescriptor published =
            await fixture.PublishAsync(
                source,
                replayStart,
                content: "source recap"
            );
        DerivedRecapFrozenInput input =
            DerivedRecapCodec.CreateFrozenInput(
                id,
                targetPath,
                source,
                "source recap"
            );
        var plan = new InheritRecapBlockPlan(
            id,
            targetPath,
            source,
            published.EnvelopeSha256,
            input.PayloadSha256
        );
        DerivedRecapSetManifest manifest =
            DerivedRecapCodec.CreateManifest(
                fixture.Engine.BranchRefId,
                target,
                [plan]
            );
        CreateBuildingResult.Created created =
            Assert.IsType<CreateBuildingResult.Created>(
                await fixture.Store.CreateBuildingAsync(manifest)
            );
        BuildingReadResult.Available building =
            Assert.IsType<BuildingReadResult.Available>(
                await fixture.Store.ReadBuildingAsync(target)
            );
        Assert.Equal(created.Descriptor, building.Snapshot.Descriptor);
        Assert.Equal(
            input,
            building.Snapshot.FrozenInputs[id]
        );
    }

    [Fact]
    public async Task ExistingCatchUpShapePreservesEarlyEndpoint() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(historyPairs: 5);
        DerivedRecapLineageView lineage = fixture.Lineage();
        EventAddress target = lineage.CurrentPrefix.HeadToOldest[0].Address;
        EventAddress source = lineage.CurrentPrefix.HeadToOldest[4].Address;
        EventAddress earlyEndpoint =
            lineage.CurrentPrefix.HeadToOldest[6].Address;
        EventAddress absorbedThrough =
            lineage.CurrentPrefix.HeadToOldest[8].Address;
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
            RecapTestIdentity.CapabilityFingerprint,
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
            lineage.CurrentPrefix.HeadToOldest
                    .Select(static node => node.Address)
                    .ToList()
                    .IndexOf(earlyEndpoint)
                > lineage.CurrentPrefix.HeadToOldest
                    .Select(static node => node.Address)
                    .ToList()
                    .IndexOf(source)
        );
    }

    [Fact]
    public async Task PublishDestinationExistsFailsWithoutOverwrite() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync();
        DerivedRecapLineageView lineage = fixture.Lineage();
        EventAddress anchor = lineage.CapturedHead;
        RecapBlockPlan plan = fixture.CreateMaintainPlan(
            anchor,
            lineage.CurrentPrefix.HeadToOldest[2].Address
        );
        DerivedRecapSetManifest manifest =
            DerivedRecapCodec.CreateManifest(
                fixture.Engine.BranchRefId,
                anchor,
                [plan]
            );
        await fixture.Store.CreateBuildingAsync(manifest);
        await RecapStoreTestDriver.InstallFinalAsync(
            fixture.Store,

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

        Assert.IsType<PublishRecapResult.StoreUnavailable>(
            await fixture.Publisher.PublishAsync(anchor)
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
        DerivedRecapLineageView lineage = fixture.Lineage();
        EventAddress published = lineage.CapturedHead;
        EventAddress rewindTo = lineage.CurrentPrefix.HeadToOldest[2].Address;
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
        DerivedRecapLineageView lineage = fixture.Lineage();
        EventAddress anchor = lineage.CapturedHead;
        EventAddress replay = lineage.CurrentPrefix.HeadToOldest[2].Address;
        RecapBlockPlan plan =
            fixture.CreateMaintainPlan(anchor, replay);
        DerivedRecapSetManifest manifest =
            DerivedRecapCodec.CreateManifest(
                fixture.Engine.BranchRefId,
                anchor,
                [plan]
            );
        await fixture.Store.CreateBuildingAsync(manifest);
        DerivedRecapBlock block =
            DerivedRecapCodec.CreateBlock(plan, anchor, "old");
        await RecapStoreTestDriver.InstallFinalAsync(
            fixture.Store,
            anchor, block);
        PublishedRecapDescriptor descriptor = Assert.IsType<
            PublishRecapResult.Published
        >(await fixture.Publisher.PublishAsync(anchor)).Descriptor;
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
    public async Task TruncatedColdLineageIsTypedBeyondPrefix() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(historyPairs: 257);
        DerivedRecapLineageView lineage = fixture.Lineage();

        var beyond = Assert.IsType<DerivedRecapSelection.BeyondPrefix>(
            await lineage.SelectNthPreviousAsync(0)
        );
        Assert.Equal(513, beyond.Evidence.HeaderCount);
        Assert.Equal(0, lineage.CurrentPrefix.Diagnostics.PayloadReads);
    }

    [Fact]
    public async Task InsufficientStrictOrdinalOnTruncatedLineageIsBeyondPrefix() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(historyPairs: 257);
        DerivedRecapLineageView lineage = fixture.Lineage();
        _ = await fixture.PublishAsync(
            lineage.CapturedHead,
            lineage.CurrentPrefix.HeadToOldest[2].Address
        );

        Assert.IsType<DerivedRecapSelection.BeyondPrefix>(
            await fixture.Lineage().SelectNthPreviousAsync(1)
        );
        Assert.IsType<DerivedRecapSelection.Selected>(
            await fixture.Lineage().SelectNthPreviousAsync(0)
        );
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
