using Atelia.EventJournal;
using Xunit;

namespace Atelia.SessionJournal.DerivedRecap.Store.Tests;

public sealed class DerivedRecapStoreR1Tests {
    [Fact]
    public async Task ExactSourceReadDetectsMultiBlockEnvelopeChange() {
        RecapStoreFixture? fixture = null;
        Action? replaceEnvelope = null;
        var hooks = new RecapStoreTestHooks(
            BeforeSourceEnvelopeRecheck:
                () => replaceEnvelope?.Invoke(),
            BeforeBuildingSourceFinalRecheck:
                () => replaceEnvelope?.Invoke()
        );
        using (fixture =
               await RecapStoreFixture.CreateAsync(
                   hooks,
                   historyPairs: 5
               )) {
            SessionCurrentLineageSnapshot lineage = fixture.Lineage();
            EventAddress source = lineage.HeadToRoot[2].Address;
            EventAddress replayStart =
                lineage.HeadToRoot[^1].Address;
            RecapBlockPlan[] plans = [
                fixture.CreateMaintainPlan(
                    source,
                    replayStart,
                    "roleplay.customer"
                ),
                fixture.CreateMaintainPlan(
                    source,
                    replayStart,
                    "roleplay.self"
                )
            ];
            DerivedRecapBlock[] blocks = [
                DerivedRecapCodec.CreateBlock(
                    plans[0],
                    source,
                    "customer recap"
                ),
                DerivedRecapCodec.CreateBlock(
                    plans[1],
                    source,
                    "self recap"
                )
            ];
            PublishedRecapDescriptor descriptor =
                await PublishAsync(fixture, source, plans, blocks);
            string publicationPath = Path.Combine(
                fixture.Store.GetPublishedPathForTest(source),
                "publication.json"
            );
            byte[] originalEnvelope =
                await File.ReadAllBytesAsync(publicationPath);
            PublishedRecapSourceReadResult.Available available =
                Assert.IsType<
                    PublishedRecapSourceReadResult.Available
                >(
                    await fixture.Store.ReadPublishedSourceAsync(
                        descriptor,
                        [
                            plans[0].RecapBlockId,
                            plans[1].RecapBlockId
                        ]
                    )
                );
            Assert.Equal(2, available.Snapshot.FrozenInputs.Count);
            Assert.Equal(
                plans.Select(
                    DerivedRecapCodec.ComputeBlockPlanSha256
                ),
                available.Snapshot.Publication
                    .FrozenPlanSnapshot.Blocks
                    .Select(
                        DerivedRecapCodec.ComputeBlockPlanSha256
                    )
            );
            Assert.Equal(
                2,
                available.Snapshot.Publication
                    .BlockCommitments.Count
            );
            Assert.IsType<
                PublishedRecapSourceReadResult.SnapshotTokenMismatch
            >(
                await fixture.Store.ReadPublishedSourceAsync(
                    descriptor with {
                        EnvelopeSha256 = new string('a', 64)
                    },
                    [plans[0].RecapBlockId]
                )
            );
            Assert.IsType<PublishedRecapSourceReadResult.Missing>(
                await fixture.Store.ReadPublishedSourceAsync(
                    descriptor with {
                        SetAdmissionAnchor =
                            lineage.HeadToRoot[4].Address
                    },
                    [plans[0].RecapBlockId]
                )
            );

            RecapBlockPlan[] changedPlans = [
                fixture.CreateMaintainPlan(
                    source,
                    replayStart,
                    "roleplay.customer"
                ),
                new MaintainRecapBlockPlan(
                    plans[1].RecapBlockId,
                    plans[1].Target,
                    "roleplay.changed",
                    RecapTestIdentity.CapabilityFingerprint,
                    new EmptyRecapMaintainSource(replayStart),
                    [source],
                    EmptyRecapPriorContext.Instance
                )
            ];
            PublishedRecapSet changed =
                DerivedRecapCodec.CreatePublication(
                    DerivedRecapCodec.CreateManifest(
                        fixture.Engine.BranchRefId,
                        source,
                        changedPlans
                    ),
                    blocks
                );
            replaceEnvelope = () => File.WriteAllBytes(
                publicationPath,
                DerivedRecapCodec.EncodePublication(changed)
            );

            PublishedRecapSourceReadResult result =
                await fixture.Store.ReadPublishedSourceAsync(
                    descriptor,
                    [
                        plans[0].RecapBlockId,
                        plans[1].RecapBlockId
                    ]
                );

            Assert.IsType<
                PublishedRecapSourceReadResult.ChangedDuringRead
            >(result);

            await File.WriteAllBytesAsync(
                publicationPath,
                originalEnvelope
            );
            EventAddress target = lineage.CapturedHead;
            DerivedRecapFrozenInput customerInput =
                DerivedRecapCodec.CreateFrozenInput(
                    blocks[0].RecapBlockId,
                    blocks[0].Target,
                    source,
                    blocks[0].Content
                );
            DerivedRecapFrozenInput selfInput =
                DerivedRecapCodec.CreateFrozenInput(
                    blocks[1].RecapBlockId,
                    blocks[1].Target,
                    source,
                    blocks[1].Content
                );
            RecapBlockPlan[] targetPlans = [
                new InheritRecapBlockPlan(
                    blocks[0].RecapBlockId,
                    blocks[0].Target,
                    source,
                    descriptor.EnvelopeSha256,
                    customerInput.PayloadSha256
                ),
                new MaintainRecapBlockPlan(
                    blocks[1].RecapBlockId,
                    blocks[1].Target,
                    "roleplay.autobiographical",
                    RecapTestIdentity.CapabilityFingerprint,
                    new ExistingRecapMaintainSource(
                        source,
                        descriptor.EnvelopeSha256,
                        selfInput.PayloadSha256
                    ),
                    [target],
                    EmptyRecapPriorContext.Instance
                )
            ];
            DerivedRecapSetManifest targetManifest =
                DerivedRecapCodec.CreateManifest(
                    fixture.Engine.BranchRefId,
                    target,
                    targetPlans
                );
            Assert.IsType<CreateBuildingResult.SourceChanged>(
                await fixture.Store.CreateBuildingAsync(
                    targetManifest
                )
            );
            Assert.IsType<BuildingReadResult.Missing>(
                await fixture.Store.ReadBuildingAsync(target)
            );
        }
    }

    [Fact]
    public async Task BuildingSnapshotIsIndependentAfterManifestInstall() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(historyPairs: 5);
        SessionCurrentLineageSnapshot lineage = fixture.Lineage();
        EventAddress target = lineage.HeadToRoot[0].Address;
        EventAddress source = lineage.HeadToRoot[2].Address;
        EventAddress replayStart =
            lineage.HeadToRoot[^1].Address;
        PublishedRecapDescriptor published =
            await fixture.PublishAsync(
                source,
                replayStart,
                content: "durable source"
            );
        var id = new RecapBlockId("roleplay.self");
        var targetPath = new ContextHeaderBlockPath(
            ContextHeaderCarrier.System,
            id.Value
        );
        DerivedRecapFrozenInput expected =
            DerivedRecapCodec.CreateFrozenInput(
                id,
                targetPath,
                source,
                "durable source"
            );
        var plan = new InheritRecapBlockPlan(
            id,
            targetPath,
            source,
            published.EnvelopeSha256,
            expected.PayloadSha256
        );
        DerivedRecapSetManifest manifest =
            DerivedRecapCodec.CreateManifest(
                fixture.Engine.BranchRefId,
                target,
                [plan]
            );
        _ = Assert.IsType<CreateBuildingResult.Created>(
            await fixture.Store.CreateBuildingAsync(manifest)
        );

        File.WriteAllText(
            Path.Combine(
                fixture.Store.GetPublishedPathForTest(source),
                "publication.json"
            ),
            "damaged"
        );
        File.WriteAllText(
            Path.Combine(
                fixture.Store.GetPublishedPathForTest(source),
                "blocks",
                $"{id.Value}.json"
            ),
            "damaged"
        );

        BuildingReadResult.Available read =
            Assert.IsType<BuildingReadResult.Available>(
                await fixture.Store.ReadBuildingAsync(target)
            );
        Assert.Equal(expected, read.Snapshot.FrozenInputs[id]);
    }

    [Fact]
    public async Task InspectionRejectsWrongPlanAndOffRouteCheckpoint() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(historyPairs: 5);
        SessionCurrentLineageSnapshot lineage = fixture.Lineage();
        EventAddress target = lineage.HeadToRoot[0].Address;
        EventAddress endpoint = lineage.HeadToRoot[2].Address;
        EventAddress replayStart =
            lineage.HeadToRoot[^1].Address;
        var plan = new MaintainRecapBlockPlan(
            new RecapBlockId("roleplay.self"),
            new ContextHeaderBlockPath(
                ContextHeaderCarrier.System,
                "roleplay.self"
            ),
            "roleplay.autobiographical",
            RecapTestIdentity.CapabilityFingerprint,
            new EmptyRecapMaintainSource(replayStart),
            [endpoint, target],
            EmptyRecapPriorContext.Instance
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
        string workPath = Path.Combine(
            fixture.Store.GetBuildingPathForTest(target),
            "work",
            $"{plan.RecapBlockId.Value}.json"
        );
        DerivedRecapBlock offRoute =
            DerivedRecapCodec.CreateBlock(
                plan,
                replayStart,
                "off route"
            );
        await File.WriteAllBytesAsync(
            workPath,
            DerivedRecapCodec.EncodeBlock(offRoute)
        );

        BuildingBlockInspection offRouteInspection =
            await fixture.Store.InspectBuildingBlockAsync(
                created.Descriptor,
                plan.RecapBlockId
            );
        Assert.IsType<RollingRecapCheckpointHealth.Unusable>(
            offRouteInspection.Checkpoint
        );

        var wrongPlan = new MaintainRecapBlockPlan(
            plan.RecapBlockId,
            plan.Target,
            "roleplay.wrong",
            plan.MaintainerCapabilityFingerprint,
            plan.Source,
            plan.CatchUpThrough,
            plan.PriorContext,
            plan.MaxContentUtf8Bytes
        );
        DerivedRecapBlock wrong =
            DerivedRecapCodec.CreateBlock(
                wrongPlan,
                endpoint,
                "wrong plan"
            );
        await File.WriteAllBytesAsync(
            workPath,
            DerivedRecapCodec.EncodeBlock(wrong)
        );
        BuildingBlockInspection wrongInspection =
            await fixture.Store.InspectBuildingBlockAsync(
                created.Descriptor,
                plan.RecapBlockId
            );
        Assert.IsType<RollingRecapCheckpointHealth.Unusable>(
            wrongInspection.Checkpoint
        );
    }

    [Fact]
    public async Task FinalEndpointCheckpointCanInstallFinalWithoutRewrite() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(historyPairs: 4);
        SessionCurrentLineageSnapshot lineage = fixture.Lineage();
        EventAddress target = lineage.HeadToRoot[0].Address;
        EventAddress replayStart =
            lineage.HeadToRoot[^1].Address;
        RecapBlockPlan plan =
            fixture.CreateMaintainPlan(target, replayStart);
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
        DerivedRecapBlock finalCheckpoint =
            DerivedRecapCodec.CreateBlock(
                plan,
                target,
                "final recap"
            );
        CheckpointWriteResult.Updated checkpoint =
            Assert.IsType<CheckpointWriteResult.Updated>(
                await fixture.Store.AdvanceRollingCheckpointAsync(
                    created.Descriptor,
                    plan.RecapBlockId,
                    "missing",
                    finalCheckpoint
                )
            );
        BuildingBlockInspection beforeFinal =
            await fixture.Store.InspectBuildingBlockAsync(
                created.Descriptor,
                plan.RecapBlockId
            );
        Assert.Equal(
            checkpoint.StateToken,
            beforeFinal.Checkpoint.StateToken
        );

        _ = Assert.IsType<FinalBlockWriteResult.Installed>(
            await fixture.Store.EnsureFinalBlockAsync(
                created.Descriptor,
                plan.RecapBlockId,
                beforeFinal.Final.StateToken,
                finalCheckpoint
            )
        );
        BuildingBlockInspection afterFinal =
            await fixture.Store.InspectBuildingBlockAsync(
                created.Descriptor,
                plan.RecapBlockId
            );
        FinalRecapBlockHealth.Healthy final =
            Assert.IsType<FinalRecapBlockHealth.Healthy>(
                afterFinal.Final
            );
        Assert.Equal(finalCheckpoint, final.Block);
    }

    [Fact]
    public async Task FinalInstallUsesHealthTokenAndRepairsOnlyDamage() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(historyPairs: 4);
        SessionCurrentLineageSnapshot lineage = fixture.Lineage();
        EventAddress target = lineage.CapturedHead;
        EventAddress firstEndpoint =
            lineage.HeadToRoot[2].Address;
        var plan = new MaintainRecapBlockPlan(
            new RecapBlockId("roleplay.self"),
            new ContextHeaderBlockPath(
                ContextHeaderCarrier.System,
                "roleplay.self"
            ),
            "roleplay.autobiographical",
            RecapTestIdentity.CapabilityFingerprint,
            new EmptyRecapMaintainSource(
                lineage.HeadToRoot[^1].Address
            ),
            [firstEndpoint, target],
            EmptyRecapPriorContext.Instance
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
        DerivedRecapBlock candidate =
            DerivedRecapCodec.CreateBlock(plan, target, "healthy");
        await Assert.ThrowsAsync<InvalidDataException>(
            async () =>
                await fixture.Store.EnsureFinalBlockAsync(
                    created.Descriptor,
                    plan.RecapBlockId,
                    "missing",
                    candidate
                )
        );
        CheckpointWriteResult.Updated first =
            Assert.IsType<CheckpointWriteResult.Updated>(
                await fixture.Store.AdvanceRollingCheckpointAsync(
                    created.Descriptor,
                    plan.RecapBlockId,
                    "missing",
                    DerivedRecapCodec.CreateBlock(
                        plan,
                        firstEndpoint,
                        "intermediate"
                    )
                )
            );
        await Assert.ThrowsAsync<InvalidDataException>(
            async () =>
                await fixture.Store.EnsureFinalBlockAsync(
                    created.Descriptor,
                    plan.RecapBlockId,
                    "missing",
                    candidate
                )
        );
        _ = Assert.IsType<CheckpointWriteResult.Updated>(
            await fixture.Store.AdvanceRollingCheckpointAsync(
                created.Descriptor,
                plan.RecapBlockId,
                first.StateToken,
                candidate
            )
        );
        DerivedRecapBlock conflicting =
            DerivedRecapCodec.CreateBlock(plan, target, "different");
        await Assert.ThrowsAsync<InvalidDataException>(
            async () =>
                await fixture.Store.EnsureFinalBlockAsync(
                    created.Descriptor,
                    plan.RecapBlockId,
                    "missing",
                    conflicting
                )
        );
        FinalBlockWriteResult.Installed installed =
            Assert.IsType<FinalBlockWriteResult.Installed>(
                await fixture.Store.EnsureFinalBlockAsync(
                    created.Descriptor,
                    plan.RecapBlockId,
                    "missing",
                    candidate
                )
            );
        FinalBlockWriteResult.AlreadyHealthy exact =
            Assert.IsType<FinalBlockWriteResult.AlreadyHealthy>(
                await fixture.Store.EnsureFinalBlockAsync(
                    created.Descriptor,
                    plan.RecapBlockId,
                    installed.StateToken,
                    candidate
                )
            );
        Assert.Equal(candidate, exact.Block);

        _ = Assert.IsType<FinalBlockWriteResult.HealthyConflict>(
            await fixture.Store.EnsureFinalBlockAsync(
                created.Descriptor,
                plan.RecapBlockId,
                installed.StateToken,
                conflicting
            )
        );

        string finalPath = Path.Combine(
            fixture.Store.GetBuildingPathForTest(target),
            "blocks",
            $"{plan.RecapBlockId.Value}.json"
        );
        await File.WriteAllTextAsync(finalPath, "damaged");
        BuildingBlockInspection damaged =
            await fixture.Store.InspectBuildingBlockAsync(
                created.Descriptor,
                plan.RecapBlockId
            );
        Assert.IsType<FinalRecapBlockHealth.Damaged>(
            damaged.Final
        );
        _ = Assert.IsType<FinalBlockWriteResult.Stale>(
            await fixture.Store.EnsureFinalBlockAsync(
                created.Descriptor,
                plan.RecapBlockId,
                installed.StateToken,
                candidate
            )
        );
        _ = Assert.IsType<FinalBlockWriteResult.ReplacedDamaged>(
            await fixture.Store.EnsureFinalBlockAsync(
                created.Descriptor,
                plan.RecapBlockId,
                damaged.Final.StateToken,
                candidate
            )
        );
    }

    private static async ValueTask<PublishedRecapDescriptor>
        PublishAsync(
        RecapStoreFixture fixture,
        EventAddress anchor,
        IReadOnlyList<RecapBlockPlan> plans,
        IReadOnlyList<DerivedRecapBlock> blocks
    ) {
        DerivedRecapSetManifest manifest =
            DerivedRecapCodec.CreateManifest(
                fixture.Engine.BranchRefId,
                anchor,
                plans
            );
        _ = Assert.IsType<CreateBuildingResult.Created>(
            await fixture.Store.CreateBuildingAsync(manifest)
        );
        foreach (DerivedRecapBlock block in blocks) {
            await RecapStoreTestDriver.InstallFinalAsync(
            fixture.Store,
            anchor, block);
        }
        return await fixture.Publisher.PublishAsync(anchor);
    }
}
