using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Xunit;

namespace Atelia.SessionJournal.DerivedRecap.Store.Tests;

public sealed class DerivedRecapEpochStoreCandidateTests {
    private const string RangeHash =
        "3333333333333333333333333333333333333333333333333333333333333333";

    [Fact]
    public async Task IncompleteBoundedLineageReturnsTypedBeyondPrefix() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(historyPairs: 1);
        DerivedRecapEpochStore store = DerivedRecapEpochStore.Open(
            fixture.Path,
            fixture.Engine.BranchRefId
        );
        await store.CreateAsync();
        for (int index = 0; index < 514; index++) {
            _ = fixture.Engine.AppendSystemPromptSetup(
                $"bounded-prefix-padding-{index}"
            );
        }
        EventAddress rawHead = fixture.RawLineage().CapturedHead;
        var source = new DerivedRecapContextCandidateSource(
            store,
            fixture.ReadView
        );

        SessionContextCandidateSelection selected =
            await source.SelectAsync(
                new SessionContextSelectionRequest(rawHead, 0),
                CancellationToken.None
            );

        Assert.Equal(
            SessionContextCandidateSelectionStatus.BeyondPrefix,
            selected.Status
        );
        Assert.Null(selected.Candidate);
        Assert.False(string.IsNullOrWhiteSpace(selected.Detail));
    }

    [Fact]
    public async Task ContextCandidateIsExactOrdinalAndRawHeadBound() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(historyPairs: 4);
        DerivedRecapEpochStore store = DerivedRecapEpochStore.Open(
            fixture.Path,
            fixture.Engine.BranchRefId
        );
        await store.CreateAsync();
        SessionCurrentLineageSnapshot lineage = fixture.RawLineage();
        EventAddress admission = lineage.HeadToRoot[2].Address;
        EventAddress start = lineage.HeadToRoot[6].Address;
        EpochFacts epoch = CreateEpoch(
            fixture,
            start,
            admission,
            RecapEpochPrevious.Empty.Instance,
            "history"
        );
        Assert.IsType<InstallRecapEpochBuildingResult.Installed>(
            await store.InstallBuildingAsync(
                epoch.Manifest,
                epoch.Input,
                lineage.CapturedHead,
                () => fixture.RawLineage().CapturedHead
            )
        );
        _ = await CompleteAsync(
            store,
            epoch.Manifest,
            ["self recap", "world recap"]
        );

        var source = new DerivedRecapContextCandidateSource(
            store,
            fixture.ReadView
        );
        var request = new SessionContextSelectionRequest(
            lineage.CapturedHead,
            0
        );
        SessionContextCandidateSelection selected =
            await source.SelectAsync(request, CancellationToken.None);
        Assert.Equal(
            SessionContextCandidateSelectionStatus.Selected,
            selected.Status
        );
        SessionContextCandidate candidate = await source.MaterializeAsync(
            selected.Candidate!,
            CancellationToken.None
        );
        Assert.Equal(admission, candidate.SetAdmissionAnchor);
        Assert.Equal(
            ["self recap", "world recap"],
            candidate.Contributions.Select(static item => item.ExactText)
        );

        SessionContextCandidateSelection beyond =
            await source.SelectAsync(
                request with { NthPrevious = 1 },
                CancellationToken.None
            );
        Assert.Equal(
            SessionContextCandidateSelectionStatus.OrdinalUnavailable,
            beyond.Status
        );
        _ = fixture.AppendPair("head-drift");
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await source.MaterializeAsync(
                selected.Candidate!,
                CancellationToken.None
            )
        );
    }

    [Fact]
    public async Task BuildingSelectionRecoversBoundedStagingBacklogAcrossRetries() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(historyPairs: 1);
        DerivedRecapEpochStore store = DerivedRecapEpochStore.Open(
            fixture.Path,
            fixture.Engine.BranchRefId
        );
        await store.CreateAsync();
        EventAddress rawHead = fixture.RawLineage().CapturedHead;
        string buildingRoot = Path.GetDirectoryName(BuildingPath(
            fixture,
            rawHead
        ))!;
        string anchor = EventAddressFileNameCodec.Format(rawHead);
        for (int index = 0; index < 1025; index++) {
            Directory.CreateDirectory(Path.Combine(
                buildingRoot,
                $".{anchor}.create.{index:x32}"
            ));
        }

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.SelectBuildingAsync());
        Assert.IsType<RecapEpochBuildingSelectionResult.Empty>(
            await store.SelectBuildingAsync()
        );
        Assert.Empty(Directory.EnumerateFileSystemEntries(buildingRoot));
    }

    [Fact]
    public async Task TwoEpochsFreezePriorAndResumeWithoutSource() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(historyPairs: 4);
        DerivedRecapEpochStore store = DerivedRecapEpochStore.Open(
            fixture.Path,
            fixture.Engine.BranchRefId
        );
        await store.CreateAsync();
        SessionCurrentLineageSnapshot lineage = fixture.RawLineage();
        EventAddress firstAdmission = lineage.HeadToRoot[2].Address;
        EventAddress firstStart = lineage.HeadToRoot[6].Address;
        EpochFacts first = CreateEpoch(
            fixture,
            firstStart,
            firstAdmission,
            RecapEpochPrevious.Empty.Instance,
            "A"
        );

        Assert.IsType<InstallRecapEpochBuildingResult.Installed>(
            await store.InstallBuildingAsync(
                first.Manifest,
                first.Input,
                lineage.CapturedHead,
                () => fixture.RawLineage().CapturedHead
            )
        );
        PublishedRecapEpochDescriptor firstPublished = await CompleteAsync(
            store,
            first.Manifest,
            ["A-self", "A-world"]
        );

        EventAddress secondAdmission = fixture.AppendPair("cycle-2");
        SessionCurrentLineageSnapshot secondLineage = fixture.RawLineage();
        PriorRecapPackSnapshot prior = await store.ReadPriorPackAsync(
            firstPublished
        );
        EpochFacts second = CreateEpoch(
            fixture,
            firstAdmission,
            secondAdmission,
            new RecapEpochPrevious.Prior(prior),
            "B"
        );
        Assert.IsType<InstallRecapEpochBuildingResult.Installed>(
            await store.InstallBuildingAsync(
                second.Manifest,
                second.Input,
                secondLineage.CapturedHead,
                () => fixture.RawLineage().CapturedHead
            )
        );

        Directory.Delete(
            PublishedPath(fixture, firstAdmission),
            recursive: true
        );
        RecapEpochStoreSnapshot resumed = Assert.IsType<
            RecapEpochStoreReadResult.Available
        >(await store.ReadBuildingAsync(secondAdmission)).Snapshot;
        var frozenPrior = Assert.IsType<RecapEpochPrevious.Prior>(
            resumed.EpochInput.Previous
        );
        Assert.Equal(["A-self", "A-world"],
            frozenPrior.Pack.Blocks.Select(static block => block.Content));

        PublishedRecapEpochDescriptor secondPublished =
            await CompleteAsync(
                store,
                second.Manifest,
                frozenPrior.Pack.Blocks
                    .Select(static block => block.Content)
                    .ToArray()
            );
        DerivedRecapMaterialization materialized =
            await store.MaterializeAsync(secondPublished);

        Assert.All(
            materialized.Contributions,
            contribution => Assert.Equal(
                secondAdmission,
                contribution.AbsorbedThrough
            )
        );
        Assert.False(Directory.Exists(Path.Combine(
            PublishedPath(fixture, secondAdmission),
            "work"
        )));
        Assert.False(Directory.Exists(Path.Combine(
            PublishedPath(fixture, secondAdmission),
            "inputs"
        )));
    }

    [Fact]
    public async Task PartialFinalPersistsAndPublishedRepairResealsOnlyDamage() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(historyPairs: 4);
        DerivedRecapEpochStore store = DerivedRecapEpochStore.Open(
            fixture.Path,
            fixture.Engine.BranchRefId
        );
        await store.CreateAsync();
        SessionCurrentLineageSnapshot lineage = fixture.RawLineage();
        EventAddress admission = lineage.HeadToRoot[2].Address;
        EpochFacts epoch = CreateEpoch(
            fixture,
            lineage.HeadToRoot[6].Address,
            admission,
            RecapEpochPrevious.Empty.Instance,
            "A"
        );
        string staleStaging = Path.Combine(
            Path.GetDirectoryName(BuildingPath(fixture, admission))!,
            $".{EventAddressFileNameCodec.Format(admission)}.create."
            + new string('a', 32)
        );
        Directory.CreateDirectory(staleStaging);
        await File.WriteAllTextAsync(
            Path.Combine(staleStaging, "partial"),
            "crash"
        );
        RecapEpochBuildingDescriptor descriptor = Assert.IsType<
            InstallRecapEpochBuildingResult.Installed
        >(await store.InstallBuildingAsync(epoch.Manifest, epoch.Input))
            .Descriptor;
        Assert.False(Directory.Exists(staleStaging));

        RecapEpochStoreSnapshot building = Assert.IsType<
            RecapEpochStoreReadResult.Available
        >(await store.ReadBuildingAsync(admission)).Snapshot;
        await WriteAsync(
            store,
            epoch.Manifest,
            building.Blocks[0],
            "self-v1"
        );
        Assert.IsType<PublishRecapEpochResult.NotPublishable>(
            await store.PublishBuildingAsync(descriptor)
        );
        RecapEpochStoreSnapshot resumed = Assert.IsType<
            RecapEpochStoreReadResult.Available
        >(await store.ReadBuildingAsync(admission)).Snapshot;
        Assert.IsType<RecapEpochFinalHealth.Healthy>(
            resumed.Blocks[0].Final
        );
        Assert.IsType<RecapEpochFinalHealth.Missing>(
            resumed.Blocks[1].Final
        );
        await WriteAsync(
            store,
            epoch.Manifest,
            resumed.Blocks[1],
            "world-v1"
        );
        PublishedRecapEpochDescriptor published = Assert.IsType<
            PublishRecapEpochResult.Published
        >(await store.PublishBuildingAsync(descriptor)).Descriptor;
        Assert.IsType<PublishRecapEpochResult.Stale>(
            await store.PublishBuildingAsync(descriptor with {
                ManifestPayloadSha256 = new string('f', 64)
            })
        );

        string damagedPath = Path.Combine(
            PublishedPath(fixture, admission),
            "blocks",
            "self.json"
        );
        await File.WriteAllTextAsync(damagedPath, "not-json");
        RecapEpochStoreSnapshot repair = Assert.IsType<
            RecapEpochStoreReadResult.Available
        >(await store.ReadPublishedForRepairAsync(admission)).Snapshot;
        Assert.IsType<RecapEpochFinalHealth.Damaged>(
            repair.Blocks[0].Final
        );
        Assert.IsType<RecapEpochFinalHealth.Healthy>(
            repair.Blocks[1].Final
        );
        await WriteAsync(
            store,
            epoch.Manifest,
            repair.Blocks[0],
            "self-v2"
        );
        PublishedRecapEpochDescriptor repaired = Assert.IsType<
            PublishRecapEpochResult.Published
        >(await store.ResealPublishedAsync(
            repair.PublishedRepairAuthority!
        )).Descriptor;

        Assert.NotEqual(published.EnvelopeSha256, repaired.EnvelopeSha256);
        DerivedRecapMaterialization materialized =
            await store.MaterializeAsync(repaired);
        Assert.Contains(
            materialized.Contributions,
            contribution => contribution.ExactText == "self-v2"
        );
        Assert.Contains(
            materialized.Contributions,
            contribution => contribution.ExactText == "world-v1"
        );
    }

    [Fact]
    public async Task MissingPublicationUsesManifestWitnessAndInstallsEnvelopeLast() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(historyPairs: 4);
        DerivedRecapEpochStore store = DerivedRecapEpochStore.Open(
            fixture.Path,
            fixture.Engine.BranchRefId
        );
        await store.CreateAsync();
        SessionCurrentLineageSnapshot lineage = fixture.RawLineage();
        EventAddress admission = lineage.HeadToRoot[2].Address;
        EpochFacts epoch = CreateEpoch(
            fixture,
            lineage.HeadToRoot[6].Address,
            admission,
            RecapEpochPrevious.Empty.Instance,
            "A"
        );
        Assert.IsType<InstallRecapEpochBuildingResult.Installed>(
            await store.InstallBuildingAsync(epoch.Manifest, epoch.Input)
        );
        PublishedRecapEpochDescriptor published = await CompleteAsync(
            store,
            epoch.Manifest,
            ["self", "world"]
        );
        File.Delete(Path.Combine(
            PublishedPath(fixture, admission),
            "publication.json"
        ));

        RecapEpochStoreSnapshot witness = Assert.IsType<
            RecapEpochStoreReadResult.Available
        >(await store.ReadPublishedForRepairAsync(admission)).Snapshot;
        Assert.Null(witness.Publication);
        Assert.Equal(
            RecapEpochPublishedAuthorityKind.ManifestWitness,
            witness.PublishedRepairAuthority!.Kind
        );
        Assert.All(
            witness.Blocks,
            block => Assert.IsType<RecapEpochFinalHealth.Healthy>(
                block.Final
            )
        );

        PublishedRecapEpochDescriptor repaired = Assert.IsType<
            PublishRecapEpochResult.Published
        >(await store.ResealPublishedAsync(
            witness.PublishedRepairAuthority
        )).Descriptor;
        Assert.Equal(published.AdmissionAnchor, repaired.AdmissionAnchor);
        _ = await store.MaterializeAsync(repaired);
    }

    [Fact]
    public async Task AggregateGateRejectsPublicationBeforePromotion() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(historyPairs: 4);
        var limits = new DerivedRecapEpochStoreLimits(
            maxTotalRecapPackUtf8Bytes: 8
        );
        DerivedRecapEpochStore store = DerivedRecapEpochStore.Open(
            fixture.Path,
            fixture.Engine.BranchRefId,
            limits
        );
        await store.CreateAsync();
        SessionCurrentLineageSnapshot lineage = fixture.RawLineage();
        EventAddress admission = lineage.HeadToRoot[2].Address;
        EpochFacts epoch = CreateEpoch(
            fixture,
            lineage.HeadToRoot[6].Address,
            admission,
            RecapEpochPrevious.Empty.Instance,
            "A"
        );
        RecapEpochBuildingDescriptor descriptor = Assert.IsType<
            InstallRecapEpochBuildingResult.Installed
        >(await store.InstallBuildingAsync(epoch.Manifest, epoch.Input))
            .Descriptor;
        RecapEpochStoreSnapshot building = Assert.IsType<
            RecapEpochStoreReadResult.Available
        >(await store.ReadBuildingAsync(admission)).Snapshot;
        await WriteAsync(
            store,
            epoch.Manifest,
            building.Blocks[0],
            "12345678"
        );
        await WriteAsync(
            store,
            epoch.Manifest,
            building.Blocks[1],
            "9"
        );

        Assert.IsType<PublishRecapEpochResult.NotPublishable>(
            await store.PublishBuildingAsync(descriptor)
        );
        Assert.True(Directory.Exists(BuildingPath(fixture, admission)));
        Assert.False(Directory.Exists(PublishedPath(fixture, admission)));
    }

    [Fact]
    public async Task RepairAuthorityRequiresBoundedCapturedDamage() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(historyPairs: 4);
        DerivedRecapEpochStore store = DerivedRecapEpochStore.Open(
            fixture.Path,
            fixture.Engine.BranchRefId
        );
        await store.CreateAsync();
        SessionCurrentLineageSnapshot lineage = fixture.RawLineage();
        EventAddress admission = lineage.HeadToRoot[2].Address;
        EpochFacts epoch = CreateEpoch(
            fixture,
            lineage.HeadToRoot[6].Address,
            admission,
            RecapEpochPrevious.Empty.Instance,
            "A"
        );
        Assert.IsType<InstallRecapEpochBuildingResult.Installed>(
            await store.InstallBuildingAsync(epoch.Manifest, epoch.Input)
        );
        _ = await CompleteAsync(
            store,
            epoch.Manifest,
            ["self", "world"]
        );

        var publicationBoundedOut = DerivedRecapEpochStore.Open(
            fixture.Path,
            fixture.Engine.BranchRefId,
            new DerivedRecapEpochStoreLimits(maxPublicationBytes: 1)
        );
        Assert.IsType<RecapEpochStoreReadResult.Invalid>(
            await publicationBoundedOut.ReadPublishedForRepairAsync(
                admission
            )
        );

        var finalBoundedOut = DerivedRecapEpochStore.Open(
            fixture.Path,
            fixture.Engine.BranchRefId,
            new DerivedRecapEpochStoreLimits(maxFinalBlockBytes: 1)
        );
        RecapEpochStoreSnapshot unavailable = Assert.IsType<
            RecapEpochStoreReadResult.Available
        >(await finalBoundedOut.ReadPublishedForRepairAsync(admission))
            .Snapshot;
        Assert.All(unavailable.Blocks, block => {
            Assert.IsType<RecapEpochFinalHealth.Unavailable>(block.Final);
            Assert.Null(block.WriteAuthority);
        });

        RefId otherRef = new(0x7fff);
        DerivedRecapEpochManifest otherManifest =
            DerivedRecapV8Codec.CreateManifest(
                otherRef,
                admission,
                epoch.Input.PayloadSha256,
                epoch.Manifest.Blocks
            );
        DerivedRecapFinalBlock[] otherFinals = [
            .. otherManifest.Blocks.Select(definition =>
                DerivedRecapV8Codec.CreateFinalBlock(
                    otherManifest,
                    definition,
                    definition.RecapBlockId.Value
                ))
        ];
        await File.WriteAllBytesAsync(
            Path.Combine(
                PublishedPath(fixture, admission),
                "publication.json"
            ),
            DerivedRecapV8Codec.EncodePublication(
                DerivedRecapV8Codec.CreatePublication(
                    otherManifest,
                    otherFinals
                )
            )
        );
        Assert.IsType<RecapEpochStoreReadResult.Invalid>(
            await store.ReadPublishedForRepairAsync(admission)
        );
    }

    [Fact]
    public void OpenMissingRepositoryFailsWithoutCreatingIt() {
        string missing = Path.Combine(
            Path.GetTempPath(),
            "atelia-recap-v8-missing",
            Guid.NewGuid().ToString("N")
        );

        Assert.Throws<DirectoryNotFoundException>(() =>
            DerivedRecapEpochStore.Open(missing, new RefId(1))
        );
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public async Task SealPromotionAndFinalReplaceCrashSeamsResume() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(historyPairs: 4);
        DerivedRecapEpochStore store = DerivedRecapEpochStore.Open(
            fixture.Path,
            fixture.Engine.BranchRefId
        );
        await store.CreateAsync();
        SessionCurrentLineageSnapshot lineage = fixture.RawLineage();
        EventAddress admission = lineage.HeadToRoot[2].Address;
        EpochFacts epoch = CreateEpoch(
            fixture,
            lineage.HeadToRoot[6].Address,
            admission,
            RecapEpochPrevious.Empty.Instance,
            "A"
        );
        RecapEpochBuildingDescriptor descriptor = Assert.IsType<
            InstallRecapEpochBuildingResult.Installed
        >(await store.InstallBuildingAsync(epoch.Manifest, epoch.Input))
            .Descriptor;
        RecapEpochStoreSnapshot building = Assert.IsType<
            RecapEpochStoreReadResult.Available
        >(await store.ReadBuildingAsync(admission)).Snapshot;
        await WriteAsync(store, epoch.Manifest, building.Blocks[0], "self");
        await WriteAsync(store, epoch.Manifest, building.Blocks[1], "world");

        var promotionCrash = DerivedRecapEpochStore.OpenForTest(
            fixture.Path,
            fixture.Engine.BranchRefId,
            limits: null,
            new RecapEpochStoreTestHooks(
                BeforePublishedPromotion: () => throw new IOException(
                    "simulated promotion crash"
                )
            )
        );
        await Assert.ThrowsAsync<IOException>(async () =>
            await promotionCrash.PublishBuildingAsync(descriptor)
        );
        Assert.True(Directory.Exists(BuildingPath(fixture, admission)));
        PublishedRecapEpochDescriptor published = Assert.IsType<
            PublishRecapEpochResult.Published
        >(await store.PublishBuildingAsync(descriptor)).Descriptor;

        string damagedPath = Path.Combine(
            PublishedPath(fixture, admission),
            "blocks",
            "self.json"
        );
        await File.WriteAllTextAsync(damagedPath, "damaged");
        RecapEpochStoreSnapshot repair = Assert.IsType<
            RecapEpochStoreReadResult.Available
        >(await store.ReadPublishedForRepairAsync(admission)).Snapshot;
        var replaceCrash = DerivedRecapEpochStore.OpenForTest(
            fixture.Path,
            fixture.Engine.BranchRefId,
            limits: null,
            new RecapEpochStoreTestHooks(
                BeforeFinalReplace: () => throw new IOException(
                    "simulated final replace crash"
                )
            )
        );
        DerivedRecapFinalBlock candidate =
            DerivedRecapV8Codec.CreateFinalBlock(
                epoch.Manifest,
                repair.Blocks[0].Definition,
                "self-repaired"
            );
        await Assert.ThrowsAsync<IOException>(async () =>
            await replaceCrash.WriteFinalAsync(
                repair.Blocks[0].WriteAuthority!,
                candidate
            )
        );
        RecapEpochStoreSnapshot afterCrash = Assert.IsType<
            RecapEpochStoreReadResult.Available
        >(await store.ReadPublishedForRepairAsync(admission)).Snapshot;
        Assert.IsType<RecapEpochFinalHealth.Damaged>(
            afterCrash.Blocks[0].Final
        );
        await WriteAsync(
            store,
            epoch.Manifest,
            afterCrash.Blocks[0],
            "self-repaired"
        );
        RecapEpochStoreSnapshot readyToSeal = Assert.IsType<
            RecapEpochStoreReadResult.Available
        >(await store.ReadPublishedForRepairAsync(admission)).Snapshot;
        PublishedRecapEpochDescriptor resealed = Assert.IsType<
            PublishRecapEpochResult.Published
        >(await store.ResealPublishedAsync(
            readyToSeal.PublishedRepairAuthority!
        )).Descriptor;
        Assert.NotEqual(published.EnvelopeSha256, resealed.EnvelopeSha256);
    }

    private static EpochFacts CreateEpoch(
        RecapStoreFixture fixture,
        EventAddress start,
        EventAddress admission,
        RecapEpochPrevious previous,
        string history
    ) {
        DerivedRecapEpochInput input = DerivedRecapV8Codec.CreateEpochInput(
            new RecapEpochBoundary(start, fixture.Setups(start)),
            new RecapEpochBoundary(admission, fixture.Setups(admission)),
            rawEventCount: 2,
            RangeHash,
            [new ObservationMessage(history)],
            previous
        );
        RecapEpochBlockDefinition[] definitions = [
            Definition("self", ContextHeaderCarrier.System, 0),
            Definition("world", ContextHeaderCarrier.Observation, 1)
        ];
        DerivedRecapEpochManifest manifest =
            DerivedRecapV8Codec.CreateManifest(
                fixture.Engine.BranchRefId,
                admission,
                input.PayloadSha256,
                definitions
            );
        return new EpochFacts(input, manifest);
    }

    private static RecapEpochBlockDefinition Definition(
        string id,
        ContextHeaderCarrier carrier,
        int ordinal
    ) => new(
        new RecapBlockId(id),
        new ContextHeaderBlockPath(carrier, id),
        id,
        RecapTestIdentity.CapabilityFingerprint,
        1024,
        ordinal
    );

    private static async ValueTask<PublishedRecapEpochDescriptor>
        CompleteAsync(
        DerivedRecapEpochStore store,
        DerivedRecapEpochManifest manifest,
        IReadOnlyList<string> contents
    ) {
        RecapEpochStoreSnapshot building = Assert.IsType<
            RecapEpochStoreReadResult.Available
        >(await store.ReadBuildingAsync(manifest.AdmissionAnchor)).Snapshot;
        for (int ordinal = 0; ordinal < building.Blocks.Count; ordinal++) {
            await WriteAsync(
                store,
                manifest,
                building.Blocks[ordinal],
                contents[ordinal]
            );
        }
        return Assert.IsType<PublishRecapEpochResult.Published>(
            await store.PublishBuildingAsync(building.Descriptor)
        ).Descriptor;
    }

    private static async ValueTask WriteAsync(
        DerivedRecapEpochStore store,
        DerivedRecapEpochManifest manifest,
        RecapEpochBlockInspection inspection,
        string content
    ) {
        WriteRecapEpochFinalResult result = await store.WriteFinalAsync(
            inspection.WriteAuthority!,
            DerivedRecapV8Codec.CreateFinalBlock(
                manifest,
                inspection.Definition,
                content
            )
        );
        Assert.True(
            result is WriteRecapEpochFinalResult.Installed
                or WriteRecapEpochFinalResult.AlreadyHealthy,
            result.GetType().Name
        );
    }

    private static string BuildingPath(
        RecapStoreFixture fixture,
        EventAddress admission
    ) => StagePath(fixture, "building", admission);

    private static string PublishedPath(
        RecapStoreFixture fixture,
        EventAddress admission
    ) => StagePath(fixture, "published", admission);

    private static string StagePath(
        RecapStoreFixture fixture,
        string stage,
        EventAddress admission
    ) => Path.Combine(
        fixture.Path,
        "derived",
        "recap",
        "v8",
        "refs",
        fixture.Engine.BranchRefId.ToHexString(),
        stage,
        EventAddressFileNameCodec.Format(admission)
    );

    private sealed record EpochFacts(
        DerivedRecapEpochInput Input,
        DerivedRecapEpochManifest Manifest
    );
}
