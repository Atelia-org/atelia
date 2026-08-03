using System.Reflection;
using System.Security.Cryptography;
using Atelia.EventJournal;
using Xunit;

namespace Atelia.SessionJournal.DerivedRecap.Store.Tests;

public sealed class DerivedRecapPublisherTests {
    [Fact]
    public async Task PreparedPublicationFailsFastAfterOwnerDisposeBeforeStoreIo() {
        int publishabilityStoreReads = 0;
        int sealedCount = 0;
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(
                new RecapStoreTestHooks(
                    AfterPublicationSealed: () => sealedCount++,
                    BeforePublishabilityStoreRead: () =>
                        publishabilityStoreReads++
                )
            );
        DerivedRecapLineageView lineage = fixture.Lineage();
        EventAddress anchor = lineage.CapturedHead;
        RecapBlockPlan plan = fixture.CreateMaintainPlan(
            anchor,
            lineage.CurrentPrefix.HeadToOldest[^2].Address
        );
        _ = Assert.IsType<CreateBuildingResult.Created>(
            await fixture.Store.CreateBuildingAsync(
                RecapWireTestFacts.CreateManifest(
                    fixture.Engine,
                    anchor,
                    [plan]
                )
            )
        );
        await RecapStoreTestDriver.InstallFinalAsync(
            fixture.Store,
            anchor,
            DerivedRecapCodec.CreateBlock(plan, anchor, "ready")
        );
        BuildingPlanHandle handle = Assert.IsType<
            BuildingPlanReadResult.Available
        >(await fixture.Store.ReadBuildingPlanAsync(anchor)).Snapshot.Handle;
        PreparedRecapPublication prepared = fixture.Publisher.Prepare(
            handle,
            anchor
        );
        Assert.IsType<RecapPublishability.Publishable>(
            await fixture.Publisher.CanPublishAsync(prepared)
        );
        publishabilityStoreReads = 0;
        string buildingPath =
            fixture.Store.GetBuildingPathForTest(anchor);
        string publicationPath = Path.Combine(
            buildingPath,
            "publication.json"
        );
        string[] before = SnapshotTree(buildingPath);
        Assert.False(File.Exists(publicationPath));
        Assert.Equal(0, publishabilityStoreReads);

        fixture.CloseEngine();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await fixture.Publisher.CanPublishAsync(prepared)
        );
        Assert.Throws<ObjectDisposedException>(() => {
            _ = fixture.Publisher.PublishAsync(prepared);
        });
        Assert.Equal(0, publishabilityStoreReads);
        Assert.Equal(0, sealedCount);
        Assert.False(File.Exists(publicationPath));
        Assert.Equal(before, SnapshotTree(buildingPath));
        Assert.True(Directory.Exists(buildingPath));
    }

    [Fact]
    public async Task StoreDamageAfterPublishPreflightIsTypedBeforeSeal() {
        int sealedCount = 0;
        RecapStoreFixture? fixture = null;
        fixture = await RecapStoreFixture.CreateAsync(
            new RecapStoreTestHooks(
                AfterPublicationSealed: () => sealedCount++,
                AfterPublishPreflight: () => File.WriteAllText(
                    Path.Combine(
                        fixture!.Store.StoreRootPathForTest,
                        "store.json"
                    ),
                    "damaged"
                )
            )
        );
        using (fixture) {
            DerivedRecapLineageView lineage = fixture.Lineage();
            EventAddress anchor = lineage.CapturedHead;
            RecapBlockPlan plan = fixture.CreateMaintainPlan(
                anchor,
                lineage.CurrentPrefix.HeadToOldest[^2].Address
            );
            _ = Assert.IsType<CreateBuildingResult.Created>(
                await fixture.Store.CreateBuildingAsync(
                    RecapWireTestFacts.CreateManifest(
                fixture.Engine,
                        anchor,
                        [plan]
                    )
                )
            );
            await RecapStoreTestDriver.InstallFinalAsync(
                fixture.Store,
                anchor,
                DerivedRecapCodec.CreateBlock(
                    plan,
                    anchor,
                    "ready"
                )
            );

            Assert.IsType<PublishRecapResult.StoreUnavailable>(
                await fixture.Publisher.PublishAsync(anchor)
            );
            Assert.Equal(0, sealedCount);
            Assert.True(
                Directory.Exists(
                    fixture.Store.GetBuildingPathForTest(anchor)
                )
            );
            Assert.False(
                Directory.Exists(
                    fixture.Store.GetPublishedPathForTest(anchor)
                )
            );
        }
    }

    [Fact]
    public async Task PublishReusesOneAdmissionPrefixProof() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync();
        DerivedRecapLineageView lineage = fixture.Lineage();
        EventAddress anchor = lineage.CapturedHead;
        RecapBlockPlan plan = fixture.CreateMaintainPlan(
            anchor,
            lineage.CurrentPrefix.HeadToOldest[^2].Address
        );
        _ = await fixture.Store.CreateBuildingAsync(
            RecapWireTestFacts.CreateManifest(
                fixture.Engine,
                anchor,
                [plan]
            )
        );
        await RecapStoreTestDriver.InstallFinalAsync(
            fixture.Store,
            anchor,
            DerivedRecapCodec.CreateBlock(plan, anchor, "ready")
        );
        SessionJournalReadDiagnostics before =
            fixture.Engine.CaptureReadDiagnostics();

        Assert.IsType<PublishRecapResult.Published>(
            await fixture.Publisher.PublishAsync(anchor)
        );

        SessionJournalReadDiagnostics reads =
            fixture.Engine.CaptureReadDiagnostics() - before;
        int prefixLength = lineage.CurrentPrefix.HeadToOldest.Count;
        Assert.Equal(prefixLength * 2, reads.HeaderPreviewReadCount);
        Assert.Equal(0, reads.PayloadReadCount);
    }

    [Fact]
    public async Task ForgedFinalCannotMaskDamageAsBeyondPrefix() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(historyPairs: 257);
        DerivedRecapLineageView lineage = fixture.Lineage();
        EventAddress anchor = lineage.CapturedHead;
        EventAddress beyond = fixture.RawLineage().HeadToRoot[^1].Address;
        RecapBlockPlan plan = fixture.CreateMaintainPlan(
            anchor,
            lineage.CurrentPrefix.HeadToOldest[^2].Address
        );
        var maintain = (MaintainRecapBlockPlan)plan;
        _ = await fixture.Store.CreateBuildingAsync(
            RecapWireTestFacts.CreateManifest(
                fixture.Engine,
                anchor,
                [plan]
            )
        );
        var wrongPlan = new MaintainRecapBlockPlan(
            new RecapBlockId("roleplay.wrong"),
            plan.Target,
            "roleplay.autobiographical",
            maintain.MaintainerCapabilityFingerprint,
            maintain.Source,
            maintain.CatchUpBoundaries,
            EmptyRecapPriorContext.Instance
        );
        string finalPath = Path.Combine(
            fixture.Store.GetBuildingPathForTest(anchor),
            "blocks",
            $"{plan.RecapBlockId.Value}.json"
        );
        await File.WriteAllBytesAsync(
            finalPath,
            DerivedRecapCodec.EncodeBlock(
                DerivedRecapCodec.CreateBlock(
                    wrongPlan,
                    beyond,
                    "forged"
                )
            )
        );

        Assert.IsType<RecapPublishability.NotPublishable>(
            await fixture.Publisher.CanPublishAsync(anchor)
        );
        Assert.IsType<PublishRecapResult.NotPublishable>(
            await fixture.Publisher.PublishAsync(anchor)
        );
    }

    [Fact]
    public void PublicPublishSurfaceCannotAcceptCallerSnapshot() {
        MethodInfo[] storeMethods =
            typeof(DerivedRecapStore).GetMethods(
                BindingFlags.Instance | BindingFlags.Public
            );
        Assert.DoesNotContain(
            storeMethods,
            static method =>
                method.Name.StartsWith(
                    "Publish",
                    StringComparison.Ordinal
                )
                && method.GetParameters().Any(
                    static parameter =>
                        parameter.ParameterType
                            == typeof(SessionCurrentLineageSnapshot)
                )
        );

        MethodInfo[] publicPublish =
            typeof(DerivedRecapPublisher).GetMethods(
                BindingFlags.Instance | BindingFlags.Public
            ).Where(static method =>
                method.Name == nameof(
                    DerivedRecapPublisher.PublishAsync
                )
            ).ToArray();
        MethodInfo publish = Assert.Single(publicPublish);
        Assert.Equal(
            typeof(PreparedRecapPublication),
            publish.GetParameters()[0].ParameterType
        );
        MethodInfo[] publicCanPublish =
            typeof(DerivedRecapPublisher).GetMethods(
                BindingFlags.Instance | BindingFlags.Public
            ).Where(static method =>
                method.Name == nameof(
                    DerivedRecapPublisher.CanPublishAsync
                )
            ).ToArray();
        MethodInfo canPublish = Assert.Single(publicCanPublish);
        Assert.Equal(
            typeof(PreparedRecapPublication),
            canPublish.GetParameters()[0].ParameterType
        );
        MethodInfo prepare = Assert.Single(
            typeof(DerivedRecapPublisher).GetMethods(
                BindingFlags.Instance | BindingFlags.Public
            ),
            static method => method.Name
                == nameof(DerivedRecapPublisher.Prepare)
        );
        Assert.Equal(
            new[] {
                typeof(BuildingPlanHandle),
                typeof(EventAddress),
                typeof(CancellationToken)
            },
            prepare.GetParameters()
                .Select(static parameter => parameter.ParameterType)
                .ToArray()
        );
        Assert.DoesNotContain(
            typeof(DerivedRecapPublisher).GetMethods(
                BindingFlags.Instance | BindingFlags.Public
            ).SelectMany(static method => method.GetParameters()),
            static parameter => parameter.ParameterType
                == typeof(SessionCurrentLineageSnapshot)
        );
    }

    [Fact]
    public async Task PreparedPublicationBindsPublisherAndExpectedHead() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync();
        DerivedRecapLineageView lineage = fixture.Lineage();
        EventAddress anchor = lineage.CapturedHead;
        RecapBlockPlan plan = fixture.CreateMaintainPlan(
            anchor,
            lineage.CurrentPrefix.HeadToOldest[^2].Address
        );
        _ = await fixture.Store.CreateBuildingAsync(
            RecapWireTestFacts.CreateManifest(
                fixture.Engine,
                anchor,
                [plan]
            )
        );
        BuildingPlanHandle handle = Assert.IsType<
            BuildingPlanReadResult.Available
        >(await fixture.Store.ReadBuildingPlanAsync(anchor)).Snapshot.Handle;
        PreparedRecapPublication prepared = fixture.Publisher.Prepare(
            handle,
            anchor
        );
        Assert.IsType<RecapPublishability.NotPublishable>(
            await fixture.Publisher.CanPublishAsync(prepared)
        );
        var foreignPublisher = new DerivedRecapPublisher(
            fixture.Store,
            fixture.ReadView
        );
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await foreignPublisher.CanPublishAsync(prepared)
        );

        _ = fixture.Engine.AppendObservation("raw head drift");
        InvalidOperationException changed = Assert.Throws<
            InvalidOperationException
        >(() => fixture.Publisher.Prepare(handle, anchor));
        Assert.Contains("Raw SessionJournal head changed", changed.Message);
    }

    [Fact]
    public async Task ExactHandleRejectsPlanSwapAfterPreflight() {
        int sealedCount = 0;
        DerivedRecapSetManifest? replacement = null;
        RecapStoreFixture? fixture = null;
        fixture = await RecapStoreFixture.CreateAsync(
            new RecapStoreTestHooks(
                AfterPublicationSealed: () => sealedCount++,
                BeforePublishedPromotion: () => File.WriteAllBytes(
                    Path.Combine(
                        fixture!.Store.GetBuildingPathForTest(
                            replacement!.SetAdmissionAnchor
                        ),
                        "manifest.json"
                    ),
                    DerivedRecapCodec.EncodeManifest(replacement)
                )
            )
        );
        using (fixture) {
            DerivedRecapLineageView lineage = fixture.Lineage();
            EventAddress anchor = lineage.CapturedHead;
            EventAddress replayStart =
                lineage.CurrentPrefix.HeadToOldest[2].Address;
            RecapBlockPlan originalPlan = fixture.CreateMaintainPlan(
                anchor,
                replayStart,
                blockId: "roleplay.original"
            );
            DerivedRecapSetManifest original =
                RecapWireTestFacts.CreateManifest(
                    fixture.Engine,
                    anchor,
                    [originalPlan]
                );
            _ = Assert.IsType<CreateBuildingResult.Created>(
                await fixture.Store.CreateBuildingAsync(original)
            );
            await RecapStoreTestDriver.InstallFinalAsync(
                fixture.Store,
                anchor,
                DerivedRecapCodec.CreateBlock(
                    originalPlan,
                    anchor,
                    "original"
                )
            );
            BuildingPlanSnapshot snapshot = Assert.IsType<
                BuildingPlanReadResult.Available
            >(await fixture.Store.ReadBuildingPlanAsync(anchor)).Snapshot;
            RecapBlockPlan replacementPlan = fixture.CreateMaintainPlan(
                anchor,
                replayStart,
                blockId: "roleplay.replacement"
            );
            replacement = RecapWireTestFacts.CreateManifest(
                fixture.Engine,
                anchor,
                [replacementPlan]
            );

            var changed = Assert.IsType<
                PublishRecapResult.SourceChanged
            >(await fixture.Publisher.PublishAsync(snapshot.Handle));

            Assert.Equal(snapshot.Descriptor, changed.Expected);
            Assert.Equal(
                new BuildingDescriptor(
                    fixture.Engine.BranchRefId,
                    anchor,
                    replacement.ManifestPayloadSha256
                ),
                changed.Observed
            );
            Assert.Equal(1, sealedCount);
            Assert.True(
                Directory.Exists(
                    fixture.Store.GetBuildingPathForTest(anchor)
                )
            );
            Assert.False(
                Directory.Exists(
                    fixture.Store.GetPublishedPathForTest(anchor)
                )
            );
        }
    }

    [Fact]
    public async Task ExactPlanDamageWinsBeforeAdmissionBeyond() {
        int sealedCount = 0;
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(
                new RecapStoreTestHooks(
                    AfterPublicationSealed: () => sealedCount++
                ),
                historyPairs: 1
            );
        DerivedRecapLineageView initial = fixture.Lineage();
        EventAddress anchor = initial.CapturedHead;
        RecapBlockPlan plan = fixture.CreateMaintainPlan(
            anchor,
            initial.CurrentPrefix.HeadToOldest[^2].Address
        );
        DerivedRecapSetManifest manifest =
            RecapWireTestFacts.CreateManifest(
                fixture.Engine,
                anchor,
                [plan]
            );
        _ = Assert.IsType<CreateBuildingResult.Created>(
            await fixture.Store.CreateBuildingAsync(manifest)
        );
        BuildingPlanHandle handle = Assert.IsType<
            BuildingPlanReadResult.Available
        >(await fixture.Store.ReadBuildingPlanAsync(anchor)).Snapshot.Handle;
        await File.WriteAllTextAsync(
            Path.Combine(
                fixture.Store.GetBuildingPathForTest(anchor),
                "manifest.json"
            ),
            "damaged"
        );
        for (int index = 0; index < 257; index++) {
            _ = fixture.AppendPair($"tail-{index}");
        }

        Assert.IsType<RecapPublishability.NotPublishable>(
            await fixture.Publisher.CanPublishAsync(handle)
        );
        Assert.IsType<PublishRecapResult.NotPublishable>(
            await fixture.Publisher.PublishAsync(handle)
        );
        Assert.Equal(0, sealedCount);
        Assert.True(
            Directory.Exists(
                fixture.Store.GetBuildingPathForTest(anchor)
            )
        );
        Assert.False(
            Directory.Exists(
                fixture.Store.GetPublishedPathForTest(anchor)
            )
        );
    }

    [Fact]
    public async Task FinalRawGateRejectsRewindAfterCapture() {
        SessionJournalEngine? engine = null;
        EventAddress capturedHead = default;
        EventAddress rewindTarget = default;
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(
                new RecapStoreTestHooks(
                    BeforePublishedPromotion: () => {
                        Assert.True(
                            engine!.MoveCurrentHeadForTest(
                                capturedHead,
                                rewindTarget
                            )
                        );
                    }
                )
            );
        engine = fixture.Engine;
        RefId refId = engine.BranchRefId;
        DerivedRecapLineageView lineage = fixture.Lineage();
        capturedHead = lineage.CapturedHead;
        rewindTarget = lineage.CurrentPrefix.HeadToOldest[2].Address;
        RecapBlockPlan plan = fixture.CreateMaintainPlan(
            capturedHead,
            rewindTarget
        );
        DerivedRecapSetManifest manifest =
            RecapWireTestFacts.CreateManifest(
                fixture.Engine,
                capturedHead,
                [plan]
            );
        await fixture.Store.CreateBuildingAsync(manifest);
        await RecapStoreTestDriver.InstallFinalAsync(
            fixture.Store,

            capturedHead,
            DerivedRecapCodec.CreateBlock(
                plan,
                capturedHead,
                "recap"
            )
        );

        var changed = Assert.IsType<PublishRecapResult.RawHeadChanged>(
            await fixture.Publisher.PublishAsync(capturedHead)
        );
        Assert.Equal(capturedHead, changed.Expected);
        Assert.Equal(rewindTarget, changed.Observed);
        Assert.True(
            Directory.Exists(
                fixture.Store.GetBuildingPathForTest(capturedHead)
            )
        );
        Assert.False(
            Directory.Exists(
                fixture.Store.GetPublishedPathForTest(capturedHead)
            )
        );
    }

    [Fact]
    public async Task EngineBoundPublisherPublishesNormally() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync();
        DerivedRecapLineageView lineage = fixture.Lineage();

        PublishedRecapDescriptor descriptor =
            await fixture.PublishAsync(
                lineage.CapturedHead,
                lineage.CurrentPrefix.HeadToOldest[2].Address,
                content: "authority-bound"
            );

        Assert.Equal(
            lineage.CapturedHead,
            descriptor.SetAdmissionAnchor
        );
        Assert.IsType<DerivedRecapSelection.Selected>(
            await fixture.Store.SelectNthPreviousAsync(lineage, 0)
        );
    }

    [Fact]
    public async Task TwoPublishersWithSameHandleConvergeIdempotently() {
        var enteredGate =
            new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
        using var releaseGate = new ManualResetEventSlim();
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(
                new RecapStoreTestHooks(
                    BeforePublishedPromotion: () => {
                        enteredGate.SetResult();
                        releaseGate.Wait();
                    }
                )
            );
        DerivedRecapLineageView lineage = fixture.Lineage();
        EventAddress anchor = lineage.CapturedHead;
        RecapBlockPlan plan = fixture.CreateMaintainPlan(
            anchor,
            lineage.CurrentPrefix.HeadToOldest[2].Address
        );
        DerivedRecapSetManifest manifest =
            RecapWireTestFacts.CreateManifest(
                fixture.Engine,
                anchor,
                [plan]
            );
        await fixture.Store.CreateBuildingAsync(manifest);
        await RecapStoreTestDriver.InstallFinalAsync(
            fixture.Store,

            anchor,
            DerivedRecapCodec.CreateBlock(plan, anchor, "recap")
        );
        BuildingPlanSnapshot snapshot = Assert.IsType<
            BuildingPlanReadResult.Available
        >(await fixture.Store.ReadBuildingPlanAsync(anchor)).Snapshot;
        Task<PublishRecapResult> publishing =
            fixture.Publisher.PublishAsync(snapshot.Handle).AsTask();
        await enteredGate.Task.WaitAsync(TimeSpan.FromSeconds(10));

        DerivedRecapStore secondStore = DerivedRecapStore.Open(
            fixture.Path,
            fixture.Engine.BranchRefId
        );
        var secondPublisher = new DerivedRecapPublisher(
            secondStore,
            fixture.ReadView
        );
        Task<PublishRecapResult> contending =
            secondPublisher.PublishAsync(snapshot.Handle).AsTask();
        try {
            Task early = await Task.WhenAny(
                contending,
                Task.Delay(TimeSpan.FromMilliseconds(200))
            );
            Assert.NotSame(contending, early);
        }
        finally {
            releaseGate.Set();
        }
        var published = Assert.IsType<PublishRecapResult.Published>(
            await publishing
        );
        var already = Assert.IsType<
            PublishRecapResult.AlreadyPublished
        >(await contending);
        Assert.Equal(published.Descriptor, already.Descriptor);
        Assert.Equal(
            published.Descriptor,
            Assert.IsType<RecapPublishability.AlreadyPublished>(
                await secondPublisher.CanPublishAsync(snapshot.Handle)
            ).Descriptor
        );
        Assert.Equal(
            snapshot.Descriptor.ManifestPayloadSha256,
            (Assert.IsType<PublishedPlanAtAnchorReadResult.Available>(
                await fixture.Store.ReadPublishedPlanAtAnchorAsync(anchor)
            )).Snapshot.FrozenPlan.ManifestPayloadSha256
        );
        Assert.False(
            Directory.Exists(
                fixture.Store.GetBuildingPathForTest(anchor)
            )
        );
    }

    private static string[] SnapshotTree(string root) => [
        .. Directory.EnumerateFileSystemEntries(
                root,
                "*",
                SearchOption.AllDirectories
            )
            .Select(path => Directory.Exists(path)
                ? $"D:{Path.GetRelativePath(root, path)}"
                : "F:"
                  + Path.GetRelativePath(root, path)
                  + ":"
                  + Convert.ToHexStringLower(
                      SHA256.HashData(File.ReadAllBytes(path))
                  ))
            .Order(StringComparer.Ordinal)
    ];
}
