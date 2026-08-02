using Atelia.EventJournal;
using Xunit;

namespace Atelia.SessionJournal.DerivedRecap.Store.Tests;

public sealed class DerivedRecapPublishedRestoreInspectionTests {
    [Fact]
    public async Task AdmissionBeyondPrefixIsTypedBeforePublicationRead() {
        int publicationReads = 0;
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(
                new RecapStoreTestHooks(
                    BeforeRestorePublicationRead: () =>
                        publicationReads++
                ),
                historyPairs: 1
            );
        DerivedRecapLineageView initial = fixture.Lineage();
        EventAddress anchor = initial.CapturedHead;
        _ = await fixture.PublishAsync(
            anchor,
            initial.CurrentPrefix.HeadToOldest[^1].Address
        );
        await File.WriteAllTextAsync(
            Path.Combine(
                fixture.Store.GetPublishedPathForTest(anchor),
                "publication.json"
            ),
            "damaged"
        );
        for (int index = 0; index < 257; index++) {
            _ = fixture.AppendPair($"tail-{index}");
        }

        Assert.IsType<PublishedRestoreInspectionResult.BeyondPrefix>(
            await fixture.Lineage()
                .InspectPublishedForRestoreAsync(anchor)
        );
        Assert.Equal(0, publicationReads);
    }

    [Fact]
    public async Task HealthyPublicationWinsOverManifestAndAuxiliaryLoss() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(historyPairs: 4);
        DerivedRecapLineageView lineage = fixture.Lineage();
        EventAddress anchor = lineage.CapturedHead;
        EventAddress replayStart = lineage.CurrentPrefix.HeadToOldest[^1].Address;
        RecapBlockPlan plan =
            fixture.CreateMaintainPlan(anchor, replayStart);
        PublishedRecapDescriptor descriptor =
            await fixture.PublishAsync(
                anchor,
                replayStart,
                content: "committed"
            );
        string publishedPath =
            fixture.Store.GetPublishedPathForTest(anchor);
        var conflictingPlan = new MaintainRecapBlockPlan(
            plan.RecapBlockId,
            plan.Target,
            "roleplay.changed",
            plan is MaintainRecapBlockPlan identity
                ? identity.MaintainerCapabilityFingerprint
                : throw new InvalidOperationException(),
            plan is MaintainRecapBlockPlan maintain
                ? maintain.Source
                : throw new InvalidOperationException(),
            [anchor],
            EmptyRecapPriorContext.Instance
        );
        DerivedRecapSetManifest conflictingManifest =
            DerivedRecapCodec.CreateManifest(
                fixture.Engine.BranchRefId,
                anchor,
                [conflictingPlan]
            );
        await File.WriteAllBytesAsync(
            Path.Combine(publishedPath, "manifest.json"),
            DerivedRecapCodec.EncodeManifest(conflictingManifest)
        );
        Directory.Delete(
            Path.Combine(publishedPath, "inputs"),
            recursive: true
        );
        Directory.Delete(
            Path.Combine(publishedPath, "work"),
            recursive: true
        );

        PublishedRestoreInspection inspection =
            await RequireAvailableAsync(
                fixture.Store,
                anchor,
                lineage
            );

        Assert.Equal(
            PublishedRestoreAuthorityKind.Publication,
            inspection.Handle.AuthorityKind
        );
        Assert.Equal(
            DerivedRecapCodec.ComputeBlockPlanSha256(plan),
            DerivedRecapCodec.ComputeBlockPlanSha256(
                Assert.Single(inspection.FrozenPlan.Blocks)
            )
        );
        PublishedBlockRestoreInspection block =
            inspection.Blocks[plan.RecapBlockId];
        Assert.IsType<FrozenRecapInputHealth.NotRequired>(
            block.FrozenInput
        );
        Assert.IsType<RollingRecapCheckpointHealth.Missing>(
            block.Checkpoint
        );
        Assert.IsType<
            PublishedBlockRestoreCapability.KeepCommitted
        >(block.Capability);
        Assert.Equal(
            descriptor,
            Assert.IsType<DerivedRecapSelection.Selected>(
                await fixture.Store.SelectNthPreviousAsync(lineage, 0)
            ).Descriptor
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrDamagedPublicationUsesManifestWitness(
        bool damageInsteadOfDelete
    ) {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(historyPairs: 4);
        DerivedRecapLineageView lineage = fixture.Lineage();
        EventAddress anchor = lineage.CapturedHead;
        EventAddress replayStart = lineage.CurrentPrefix.HeadToOldest[^1].Address;
        RecapBlockPlan plan =
            fixture.CreateMaintainPlan(anchor, replayStart);
        _ = await fixture.PublishAsync(
            anchor,
            replayStart,
            content: "candidate"
        );
        string publicationPath = Path.Combine(
            fixture.Store.GetPublishedPathForTest(anchor),
            "publication.json"
        );
        if (damageInsteadOfDelete) {
            await File.WriteAllTextAsync(
                publicationPath,
                "damaged"
            );
        }
        else {
            File.Delete(publicationPath);
        }

        PublishedRestoreInspection inspection =
            await RequireAvailableAsync(
                fixture.Store,
                anchor,
                lineage
            );

        Assert.Equal(
            PublishedRestoreAuthorityKind.ManifestWitness,
            inspection.Handle.AuthorityKind
        );
        Assert.StartsWith(
            damageInsteadOfDelete ? "damaged:" : "missing",
            inspection.Handle.AuthorityStateToken,
            StringComparison.Ordinal
        );
        Assert.IsType<
            PublishedBlockRestoreCapability.AdoptPending
        >(inspection.Blocks[plan.RecapBlockId].Capability);
        var invalid = Assert.IsType<
            DerivedRecapSelection.ExactPublishedSetInvalid
        >(
            await fixture.Store.SelectNthPreviousAsync(lineage, 0)
        );
        Assert.Equal(anchor, invalid.SetAdmissionAnchor);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicationReadFaultDoesNotUseManifestWitness(
        bool unauthorized
    ) {
        bool injectFault = false;
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(
                new RecapStoreTestHooks(
                    BeforeRestorePublicationRead: () => {
                        if (!injectFault) {
                            return;
                        }
                        if (unauthorized) {
                            throw new UnauthorizedAccessException(
                                "injected publication fault"
                            );
                        }
                        throw new IOException(
                            "injected publication fault"
                        );
                    }
                ),
                historyPairs: 4
            );
        DerivedRecapLineageView lineage = fixture.Lineage();
        EventAddress anchor = lineage.CapturedHead;
        _ = await fixture.PublishAsync(
            anchor,
            lineage.CurrentPrefix.HeadToOldest[^1].Address
        );
        injectFault = true;

        var unavailable = Assert.IsType<
            PublishedRestoreInspectionResult.Unavailable
        >(
            await fixture.Store.InspectPublishedForRestoreAsync(
                anchor,
                lineage
            )
        );

        Assert.Single(unavailable.Defects);
        Assert.Equal(
            "PublicationReadUnavailable",
            unavailable.Defects[0].Code
        );
    }

    [Fact]
    public async Task OversizedPublicationDoesNotUseManifestWitness() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(historyPairs: 4);
        DerivedRecapLineageView lineage = fixture.Lineage();
        EventAddress anchor = lineage.CapturedHead;
        _ = await fixture.PublishAsync(
            anchor,
            lineage.CurrentPrefix.HeadToOldest[^1].Address
        );
        string publicationPath = Path.Combine(
            fixture.Store.GetPublishedPathForTest(anchor),
            "publication.json"
        );
        await File.WriteAllBytesAsync(
            publicationPath,
            new byte[
                checked(
                    (int)DerivedRecapStore.MaxPublicationBytes + 1
                )
            ]
        );

        var unavailable = Assert.IsType<
            PublishedRestoreInspectionResult.Unavailable
        >(
            await fixture.Store.InspectPublishedForRestoreAsync(
                anchor,
                lineage
            )
        );

        Assert.Single(unavailable.Defects);
        Assert.Equal(
            "PublicationReadUnavailable",
            unavailable.Defects[0].Code
        );
    }

    [Fact]
    public async Task RestoreAuthorityRequiresCanonicalRawBytes() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(historyPairs: 4);
        DerivedRecapLineageView lineage = fixture.Lineage();
        EventAddress anchor = lineage.CapturedHead;
        RecapBlockPlan plan = fixture.CreateMaintainPlan(
            anchor,
            lineage.CurrentPrefix.HeadToOldest[^1].Address
        );
        _ = await fixture.PublishAsync(
            anchor,
            lineage.CurrentPrefix.HeadToOldest[^1].Address
        );
        string publishedPath =
            fixture.Store.GetPublishedPathForTest(anchor);
        string publicationPath =
            Path.Combine(publishedPath, "publication.json");
        await File.AppendAllTextAsync(publicationPath, "\n");

        PublishedRestoreInspection witness =
            await RequireAvailableAsync(
                fixture.Store,
                anchor,
                lineage
            );
        Assert.Equal(
            PublishedRestoreAuthorityKind.ManifestWitness,
            witness.Handle.AuthorityKind
        );
        Assert.StartsWith(
            "damaged:",
            witness.Handle.AuthorityStateToken,
            StringComparison.Ordinal
        );
        Assert.IsType<
            PublishedBlockRestoreCapability.AdoptPending
        >(witness.Blocks[plan.RecapBlockId].Capability);

        File.Delete(publicationPath);
        await File.AppendAllTextAsync(
            Path.Combine(publishedPath, "manifest.json"),
            "\n"
        );
        var unavailable = Assert.IsType<
            PublishedRestoreInspectionResult.Unavailable
        >(
            await fixture.Store.InspectPublishedForRestoreAsync(
                anchor,
                lineage
            )
        );
        Assert.Contains(
            unavailable.Defects,
            static defect =>
                defect.Code == "ManifestWitnessUnavailable"
        );
    }

    [Fact]
    public async Task CoherentIdentityConflictDoesNotFallbackManifest() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(historyPairs: 4);
        DerivedRecapLineageView lineage = fixture.Lineage();
        EventAddress anchor = lineage.CapturedHead;
        EventAddress replayStart = lineage.CurrentPrefix.HeadToOldest[^1].Address;
        _ = await fixture.PublishAsync(anchor, replayStart);
        EventAddress foreignAnchor = lineage.CurrentPrefix.HeadToOldest[2].Address;
        RecapBlockPlan foreignPlan =
            fixture.CreateMaintainPlan(
                foreignAnchor,
                replayStart
            );
        DerivedRecapSetManifest foreignManifest =
            DerivedRecapCodec.CreateManifest(
                fixture.Engine.BranchRefId,
                foreignAnchor,
                [foreignPlan]
            );
        PublishedRecapSet foreignPublication =
            DerivedRecapCodec.CreatePublication(
                foreignManifest,
                [
                    DerivedRecapCodec.CreateBlock(
                        foreignPlan,
                        foreignAnchor,
                        "foreign"
                    )
                ]
            );
        await File.WriteAllBytesAsync(
            Path.Combine(
                fixture.Store.GetPublishedPathForTest(anchor),
                "publication.json"
            ),
            DerivedRecapCodec.EncodePublication(foreignPublication)
        );

        var unavailable = Assert.IsType<
            PublishedRestoreInspectionResult.Unavailable
        >(
            await fixture.Store.InspectPublishedForRestoreAsync(
                anchor,
                lineage
            )
        );

        Assert.Contains(
            unavailable.Defects,
            static defect =>
                defect.Code == "RestoreAuthorityConflict"
        );
        Assert.IsType<
            DerivedRecapSelection.ExactPublishedSetInvalid
        >(
            await fixture.Store.SelectNthPreviousAsync(lineage, 0)
        );
    }

    [Fact]
    public async Task MissingPublicationAndManifestAreTypedUnavailable() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(historyPairs: 4);
        DerivedRecapLineageView lineage = fixture.Lineage();
        EventAddress anchor = lineage.CapturedHead;
        _ = await fixture.PublishAsync(
            anchor,
            lineage.CurrentPrefix.HeadToOldest[^1].Address
        );
        string publishedPath =
            fixture.Store.GetPublishedPathForTest(anchor);
        File.Delete(Path.Combine(publishedPath, "publication.json"));
        File.Delete(Path.Combine(publishedPath, "manifest.json"));

        var unavailable = Assert.IsType<
            PublishedRestoreInspectionResult.Unavailable
        >(
            await fixture.Store.InspectPublishedForRestoreAsync(
                anchor,
                lineage
            )
        );

        Assert.Contains(
            unavailable.Defects,
            static defect => defect.Code == "PublicationMissing"
        );
        Assert.Contains(
            unavailable.Defects,
            static defect =>
                defect.Code == "ManifestWitnessUnavailable"
        );
        Assert.IsType<
            DerivedRecapSelection.ExactPublishedSetInvalid
        >(
            await fixture.Store.SelectNthPreviousAsync(lineage, 0)
        );
    }

    [Fact]
    public async Task PendingAndCheckpointCapabilitiesAreExact() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(historyPairs: 5);
        DerivedRecapLineageView lineage = fixture.Lineage();
        EventAddress anchor = lineage.CapturedHead;
        EventAddress firstEndpoint =
            lineage.CurrentPrefix.HeadToOldest[2].Address;
        EventAddress replayStart = lineage.CurrentPrefix.HeadToOldest[^1].Address;
        var plan = new MaintainRecapBlockPlan(
            new RecapBlockId("roleplay.self"),
            new ContextHeaderBlockPath(
                ContextHeaderCarrier.System,
                "roleplay.self"
            ),
            "roleplay.autobiographical",
            RecapTestIdentity.CapabilityFingerprint,
            new EmptyRecapMaintainSource(replayStart),
            [firstEndpoint, anchor],
            EmptyRecapPriorContext.Instance
        );
        await PublishAsync(
            fixture,
            anchor,
            plan,
            DerivedRecapCodec.CreateBlock(
                plan,
                anchor,
                "committed"
            )
        );
        string publishedPath =
            fixture.Store.GetPublishedPathForTest(anchor);
        string finalPath = BlockPath(
            publishedPath,
            "blocks",
            plan.RecapBlockId
        );
        string workPath = BlockPath(
            publishedPath,
            "work",
            plan.RecapBlockId
        );
        await File.WriteAllBytesAsync(
            finalPath,
            DerivedRecapCodec.EncodeBlock(
                DerivedRecapCodec.CreateBlock(
                    plan,
                    anchor,
                    "pending"
                )
            )
        );
        Assert.IsType<
            PublishedBlockRestoreCapability.AdoptPending
        >(
            (await RequireAvailableAsync(
                fixture.Store,
                anchor,
                lineage
            )).Blocks[plan.RecapBlockId].Capability
        );

        var wrongPlan = new MaintainRecapBlockPlan(
            plan.RecapBlockId,
            plan.Target,
            "roleplay.changed",
            plan.MaintainerCapabilityFingerprint,
            plan.Source,
            plan.CatchUpThrough,
            plan.PriorContext
        );
        await File.WriteAllBytesAsync(
            finalPath,
            DerivedRecapCodec.EncodeBlock(
                DerivedRecapCodec.CreateBlock(
                    wrongPlan,
                    anchor,
                    "wrong plan"
                )
            )
        );
        Assert.IsType<
            PublishedBlockRestoreCapability.InstallFinalCheckpoint
        >(
            (await RequireAvailableAsync(
                fixture.Store,
                anchor,
                lineage
            )).Blocks[plan.RecapBlockId].Capability
        );

        await File.WriteAllBytesAsync(
            workPath,
            DerivedRecapCodec.EncodeBlock(
                DerivedRecapCodec.CreateBlock(
                    plan,
                    firstEndpoint,
                    "partial"
                )
            )
        );
        var suffix = Assert.IsType<
            PublishedBlockRestoreCapability.ResumeSuffix
        >(
            (await RequireAvailableAsync(
                fixture.Store,
                anchor,
                lineage
            )).Blocks[plan.RecapBlockId].Capability
        );
        Assert.Equal(1, suffix.NextEndpointIndex);

        File.Delete(workPath);
        Assert.IsType<
            PublishedBlockRestoreCapability.ReplayBlock
        >(
            (await RequireAvailableAsync(
                fixture.Store,
                anchor,
                lineage
            )).Blocks[plan.RecapBlockId].Capability
        );
    }

    [Fact]
    public async Task OversizedBlockCannotBecomeRestoreCapability() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(historyPairs: 5);
        DerivedRecapLineageView lineage = fixture.Lineage();
        EventAddress anchor = lineage.CapturedHead;
        EventAddress replayStart = lineage.CurrentPrefix.HeadToOldest[^1].Address;
        var plan = new MaintainRecapBlockPlan(
            new RecapBlockId("roleplay.self"),
            new ContextHeaderBlockPath(
                ContextHeaderCarrier.System,
                "roleplay.self"
            ),
            "roleplay.autobiographical",
            RecapTestIdentity.CapabilityFingerprint,
            new EmptyRecapMaintainSource(replayStart),
            [anchor],
            EmptyRecapPriorContext.Instance,
            maxContentUtf8Bytes: 8
        );
        DerivedRecapSetManifest manifest =
            DerivedRecapCodec.CreateManifest(
                fixture.Engine.BranchRefId,
                anchor,
                [plan]
            );
        await PublishAsync(
            fixture,
            anchor,
            plan,
            DerivedRecapCodec.CreateBlock(plan, anchor, "ok")
        );
        string publishedPath =
            fixture.Store.GetPublishedPathForTest(anchor);
        string publicationPath =
            Path.Combine(publishedPath, "publication.json");
        string finalPath = BlockPath(
            publishedPath,
            "blocks",
            plan.RecapBlockId
        );
        string workPath = BlockPath(
            publishedPath,
            "work",
            plan.RecapBlockId
        );
        byte[] committedPublication =
            await File.ReadAllBytesAsync(publicationPath);
        DerivedRecapBlock oversized =
            DerivedRecapCodec.CreateBlock(
                plan,
                anchor,
                "一二三"
            );

        await File.WriteAllBytesAsync(
            finalPath,
            DerivedRecapCodec.EncodeBlock(oversized)
        );
        await File.WriteAllBytesAsync(
            publicationPath,
            DerivedRecapCodec.EncodePublication(
                DerivedRecapCodec.CreatePublication(
                    manifest,
                    [oversized]
                )
            )
        );
        PublishedBlockRestoreInspection committed =
            (await RequireAvailableAsync(
                fixture.Store,
                anchor,
                lineage
            )).Blocks[plan.RecapBlockId];
        Assert.IsType<FinalRecapBlockHealth.Damaged>(
            committed.Final
        );
        Assert.IsType<
            PublishedBlockRestoreCapability.InstallFinalCheckpoint
        >(committed.Capability);

        await File.WriteAllBytesAsync(
            publicationPath,
            committedPublication
        );
        PublishedBlockRestoreInspection pending =
            (await RequireAvailableAsync(
                fixture.Store,
                anchor,
                lineage
            )).Blocks[plan.RecapBlockId];
        Assert.IsType<FinalRecapBlockHealth.Damaged>(pending.Final);
        Assert.IsType<
            PublishedBlockRestoreCapability.InstallFinalCheckpoint
        >(pending.Capability);

        await File.WriteAllTextAsync(finalPath, "damaged");
        await File.WriteAllBytesAsync(
            workPath,
            DerivedRecapCodec.EncodeBlock(oversized)
        );
        PublishedBlockRestoreInspection checkpoint =
            (await RequireAvailableAsync(
                fixture.Store,
                anchor,
                lineage
            )).Blocks[plan.RecapBlockId];
        Assert.IsType<RollingRecapCheckpointHealth.Unusable>(
            checkpoint.Checkpoint
        );
        Assert.IsType<
            PublishedBlockRestoreCapability.ReplayBlock
        >(checkpoint.Capability);
    }

    [Fact]
    public async Task RequiredInputLossOnlyBlocksDependentRestore() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(historyPairs: 6);
        (
            EventAddress target,
            InheritRecapBlockPlan plan,
            PublishedRecapDescriptor descriptor
        ) = await PublishInheritedTargetAsync(fixture);
        DerivedRecapLineageView lineage = fixture.Lineage();
        string publishedPath =
            fixture.Store.GetPublishedPathForTest(target);
        string inputPath = BlockPath(
            publishedPath,
            "inputs",
            plan.RecapBlockId
        );
        string finalPath = BlockPath(
            publishedPath,
            "blocks",
            plan.RecapBlockId
        );
        string publicationPath =
            Path.Combine(publishedPath, "publication.json");
        byte[] inputBytes = await File.ReadAllBytesAsync(inputPath);
        byte[] finalBytes = await File.ReadAllBytesAsync(finalPath);
        File.Delete(inputPath);

        PublishedRestoreInspection healthyAuthority =
            await RequireAvailableAsync(
                fixture.Store,
                target,
                lineage
            );
        PublishedBlockRestoreInspection committed =
            healthyAuthority.Blocks[plan.RecapBlockId];
        Assert.IsType<FrozenRecapInputHealth.Missing>(
            committed.FrozenInput
        );
        Assert.IsType<
            PublishedBlockRestoreCapability.KeepCommitted
        >(committed.Capability);
        _ = await fixture.Store.MaterializeAsync(descriptor);

        File.Delete(finalPath);
        Assert.IsType<
            PublishedBlockRestoreCapability.Unavailable
        >(
            (await RequireAvailableAsync(
                fixture.Store,
                target,
                lineage
            )).Blocks[plan.RecapBlockId].Capability
        );

        await File.WriteAllBytesAsync(inputPath, inputBytes);
        await File.WriteAllBytesAsync(finalPath, finalBytes);
        File.Delete(publicationPath);
        PublishedRestoreInspection witness =
            await RequireAvailableAsync(
                fixture.Store,
                target,
                lineage
            );
        Assert.Equal(
            PublishedRestoreAuthorityKind.ManifestWitness,
            witness.Handle.AuthorityKind
        );
        Assert.IsType<
            PublishedBlockRestoreCapability.AdoptPending
        >(witness.Blocks[plan.RecapBlockId].Capability);

        File.Delete(inputPath);
        var unavailable = Assert.IsType<
            PublishedRestoreInspectionResult.Unavailable
        >(
            await fixture.Store.InspectPublishedForRestoreAsync(
                target,
                lineage
            )
        );
        Assert.Contains(
            unavailable.Defects,
            static defect =>
                defect.Code == "ManifestWitnessInputMissing"
        );
        var invalid = Assert.IsType<
            DerivedRecapSelection.ExactPublishedSetInvalid
        >(
            await fixture.Store.SelectNthPreviousAsync(lineage, 0)
        );
        Assert.Equal(target, invalid.SetAdmissionAnchor);
    }

    private static async ValueTask<PublishedRestoreInspection>
        RequireAvailableAsync(
        DerivedRecapStore store,
        EventAddress anchor,
        DerivedRecapLineageView lineage
    ) => Assert.IsType<
        PublishedRestoreInspectionResult.Available
    >(
        await store.InspectPublishedForRestoreAsync(anchor, lineage)
    ).Inspection;

    private static async ValueTask PublishAsync(
        RecapStoreFixture fixture,
        EventAddress anchor,
        RecapBlockPlan plan,
        DerivedRecapBlock block
    ) {
        DerivedRecapSetManifest manifest =
            DerivedRecapCodec.CreateManifest(
                fixture.Engine.BranchRefId,
                anchor,
                [plan]
            );
        _ = Assert.IsType<CreateBuildingResult.Created>(
            await fixture.Store.CreateBuildingAsync(manifest)
        );
        await RecapStoreTestDriver.InstallFinalAsync(
            fixture.Store,
            anchor,
            block
        );
        _ = await fixture.Publisher.PublishAsync(anchor);
    }

    private static async ValueTask<(
        EventAddress Target,
        InheritRecapBlockPlan Plan,
        PublishedRecapDescriptor Descriptor
    )> PublishInheritedTargetAsync(
        RecapStoreFixture fixture
    ) {
        DerivedRecapLineageView lineage = fixture.Lineage();
        EventAddress target = lineage.CapturedHead;
        EventAddress source = lineage.CurrentPrefix.HeadToOldest[4].Address;
        EventAddress replayStart = lineage.CurrentPrefix.HeadToOldest[^1].Address;
        const string sourceContent = "source recap";
        PublishedRecapDescriptor sourceDescriptor =
            await fixture.PublishAsync(
                source,
                replayStart,
                content: sourceContent
            );
        var id = new RecapBlockId("roleplay.self");
        var targetPath = new ContextHeaderBlockPath(
            ContextHeaderCarrier.System,
            id.Value
        );
        DerivedRecapFrozenInput input =
            DerivedRecapCodec.CreateFrozenInput(
                id,
                targetPath,
                source,
                sourceContent
            );
        var plan = new InheritRecapBlockPlan(
            id,
            targetPath,
            source,
            sourceDescriptor.EnvelopeSha256,
            input.PayloadSha256
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
        await RecapStoreTestDriver.InstallFinalAsync(
            fixture.Store,
            target,
            DerivedRecapCodec.CreateBlock(
                plan,
                source,
                sourceContent
            )
        );
        PublishedRecapDescriptor descriptor = Assert.IsType<
            PublishRecapResult.Published
        >(await fixture.Publisher.PublishAsync(target)).Descriptor;
        return (target, plan, descriptor);
    }

    private static string BlockPath(
        string publishedPath,
        string directory,
        RecapBlockId blockId
    ) => Path.Combine(
        publishedPath,
        directory,
        $"{blockId.Value}.json"
    );
}
