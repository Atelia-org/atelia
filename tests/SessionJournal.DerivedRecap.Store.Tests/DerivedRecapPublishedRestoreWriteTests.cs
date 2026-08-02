using Atelia.EventJournal;
using Xunit;

namespace Atelia.SessionJournal.DerivedRecap.Store.Tests;

public sealed class DerivedRecapPublishedRestoreWriteTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommitAuthenticatesExactBlockBeforeCursorBeyond(
        bool persistCommitmentMismatch
    ) {
        int mutationCount = 0;
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(
                new RecapStoreTestHooks(
                    BeforeAtomicFileReplace: _ => mutationCount++
                ),
                historyPairs: 257
            );
        DerivedRecapLineageView lineage = fixture.Lineage();
        EventAddress anchor = lineage.CapturedHead;
        EventAddress beyond = fixture.RawLineage().HeadToRoot[^1].Address;
        var plan = (MaintainRecapBlockPlan)fixture.CreateMaintainPlan(
            anchor,
            lineage.CurrentPrefix.HeadToOldest[^1].Address
        );
        _ = await fixture.PublishAsync(
            anchor,
            lineage.CurrentPrefix.HeadToOldest[^1].Address
        );
        DerivedRecapSetManifest manifest =
            DerivedRecapCodec.CreateManifest(
                fixture.Engine.BranchRefId,
                anchor,
                [plan]
            );
        DerivedRecapBlock committed = DerivedRecapCodec.CreateBlock(
            plan,
            beyond,
            "committed beyond prefix"
        );
        DerivedRecapBlock persisted = committed;
        if (persistCommitmentMismatch) {
            var wrongPlan = new MaintainRecapBlockPlan(
                plan.RecapBlockId,
                plan.Target,
                "roleplay.changed",
                plan.MaintainerCapabilityFingerprint,
                plan.Source,
                plan.CatchUpThrough,
                plan.PriorContext
            );
            persisted = DerivedRecapCodec.CreateBlock(
                wrongPlan,
                beyond,
                "committed beyond prefix"
            );
        }
        PublishedRecapSet publication =
            await RecapStoreTestDriver.RewritePublishedUncheckedAsync(
                fixture.Store,
                manifest,
                [committed],
                [persisted]
            );
        var handle = new PublishedRestoreHandle(
            fixture.Engine.BranchRefId,
            anchor,
            PublishedRestoreAuthorityKind.Publication,
            $"publication:{publication.EnvelopeSha256}",
            manifest.ManifestPayloadSha256
        );
        byte[] persistedBytes =
            DerivedRecapCodec.EncodeBlock(persisted);
        string expectedStateToken = persistCommitmentMismatch
            ? $"damaged:{DerivedRecapCodec.Sha256Hex(persistedBytes)}"
            : $"healthy:{persisted.PayloadSha256}";
        var restorer = new DerivedRecapRestorer(
            fixture.Store,
            fixture.Engine
        );
        mutationCount = 0;

        PublishedEnvelopeCommitResult result =
            await restorer.CommitEnvelopeAsync(
                handle,
                new Dictionary<RecapBlockId, string> {
                    [plan.RecapBlockId] = expectedStateToken
                },
                lineage.CapturedHead
            );

        if (persistCommitmentMismatch) {
            Assert.IsType<PublishedEnvelopeCommitResult.Unavailable>(
                result
            );
        }
        else {
            var beyondResult = Assert.IsType<
                PublishedEnvelopeCommitResult.BeyondPrefix
            >(result);
            Assert.Equal(
                beyond,
                beyondResult.Evidence.RequiredAnchor
            );
        }
        Assert.Equal(0, mutationCount);
    }

    [Fact]
    public async Task PublishedCheckpointUsesExactAdjacentCas() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(historyPairs: 5);
        (
            DerivedRecapLineageView lineage,
            MaintainRecapBlockPlan plan,
            _
        ) = await PublishMaintainAsync(
            fixture,
            endpointCount: 2
        );
        EventAddress anchor = plan.CatchUpThrough[^1];
        string workPath = BlockPath(
            fixture,
            anchor,
            "work",
            plan.RecapBlockId
        );
        File.Delete(workPath);
        PublishedRestoreInspection inspection =
            await RequireInspectionAsync(fixture, anchor, lineage);
        string missingToken =
            inspection.Blocks[plan.RecapBlockId]
                .Checkpoint.StateToken;
        DerivedRecapBlock first =
            DerivedRecapCodec.CreateBlock(
                plan,
                plan.CatchUpThrough[0],
                "first"
            );
        DerivedRecapBlock final =
            DerivedRecapCodec.CreateBlock(
                plan,
                anchor,
                "final"
            );

        await Assert.ThrowsAsync<InvalidDataException>(
            async () =>
                await fixture.Store
                    .AdvancePublishedCheckpointAsync(
                        inspection.Handle,
                        plan.RecapBlockId,
                        missingToken,
                        final
                    )
        );
        var updated = Assert.IsType<
            PublishedCheckpointWriteResult.Updated
        >(
            await fixture.Store.AdvancePublishedCheckpointAsync(
                inspection.Handle,
                plan.RecapBlockId,
                missingToken,
                first
            )
        );
        Assert.IsType<
            PublishedCheckpointWriteResult.AlreadyCurrent
        >(
            await fixture.Store.AdvancePublishedCheckpointAsync(
                inspection.Handle,
                plan.RecapBlockId,
                updated.StateToken,
                first
            )
        );
        Assert.IsType<PublishedCheckpointWriteResult.Stale>(
            await fixture.Store.AdvancePublishedCheckpointAsync(
                inspection.Handle,
                plan.RecapBlockId,
                missingToken,
                first
            )
        );
        Assert.IsType<
            PublishedCheckpointWriteResult.Updated
        >(
            await fixture.Store.AdvancePublishedCheckpointAsync(
                inspection.Handle,
                plan.RecapBlockId,
                updated.StateToken,
                final
            )
        );
    }

    [Fact]
    public async Task PublishedFinalUsesCheckpointAndIsIdempotent() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(historyPairs: 4);
        (
            DerivedRecapLineageView lineage,
            MaintainRecapBlockPlan plan,
            _
        ) = await PublishMaintainAsync(
            fixture,
            endpointCount: 1
        );
        EventAddress anchor = plan.CatchUpThrough[^1];
        string finalPath = BlockPath(
            fixture,
            anchor,
            "blocks",
            plan.RecapBlockId
        );
        string workPath = BlockPath(
            fixture,
            anchor,
            "work",
            plan.RecapBlockId
        );
        DerivedRecapBlock replacement =
            DerivedRecapCodec.CreateBlock(
                plan,
                anchor,
                "replacement"
            );
        await File.WriteAllTextAsync(finalPath, "damaged");
        PublishedRestoreInspection initial =
            await RequireInspectionAsync(fixture, anchor, lineage);
        string damagedToken =
            initial.Blocks[plan.RecapBlockId].Final.StateToken;
        await Assert.ThrowsAsync<InvalidDataException>(
            async () =>
                await fixture.Store
                    .InstallPublishedReplacementAsync(
                        initial.Handle,
                        plan.RecapBlockId,
                        damagedToken,
                        replacement
                    )
        );

        File.Delete(workPath);
        var checkpoint = Assert.IsType<
            PublishedCheckpointWriteResult.Updated
        >(
            await fixture.Store.AdvancePublishedCheckpointAsync(
                initial.Handle,
                plan.RecapBlockId,
                "missing",
                replacement
            )
        );
        Assert.StartsWith(
            "healthy:",
            checkpoint.StateToken,
            StringComparison.Ordinal
        );
        var installed = Assert.IsType<
            PublishedFinalWriteResult.ReplacedDamaged
        >(
            await fixture.Store.InstallPublishedReplacementAsync(
                initial.Handle,
                plan.RecapBlockId,
                damagedToken,
                replacement
            )
        );
        Assert.IsType<PublishedFinalWriteResult.AlreadyHealthy>(
            await fixture.Store.InstallPublishedReplacementAsync(
                initial.Handle,
                plan.RecapBlockId,
                installed.StateToken,
                replacement
            )
        );
        Assert.IsType<PublishedFinalWriteResult.HealthyConflict>(
            await fixture.Store.InstallPublishedReplacementAsync(
                initial.Handle,
                plan.RecapBlockId,
                installed.StateToken,
                DerivedRecapCodec.CreateBlock(
                    plan,
                    anchor,
                    "conflict"
                )
            )
        );
        Assert.IsType<PublishedFinalWriteResult.Stale>(
            await fixture.Store.InstallPublishedReplacementAsync(
                initial.Handle,
                plan.RecapBlockId,
                damagedToken,
                replacement
            )
        );
    }

    [Fact]
    public async Task EnvelopeCommitIsLastAndInvalidatesOldDescriptor() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(historyPairs: 4);
        (
            DerivedRecapLineageView lineage,
            MaintainRecapBlockPlan plan,
            PublishedRecapDescriptor oldDescriptor
        ) = await PublishMaintainAsync(
            fixture,
            endpointCount: 1
        );
        EventAddress anchor = plan.CatchUpThrough[^1];
        (
            PublishedRestoreHandle handle,
            DerivedRecapBlock replacement,
            string finalToken
        ) = await InstallPendingAsync(
            fixture,
            lineage,
            plan,
            "replacement"
        );
        await Assert.ThrowsAsync<InvalidDataException>(
            async () =>
                await fixture.Store.MaterializeAsync(oldDescriptor)
        );
        var restorer = new DerivedRecapRestorer(
            fixture.Store,
            fixture.Engine
        );
        var stale =
            Assert.IsType<PublishedEnvelopeCommitResult.Stale>(
                await restorer.CommitEnvelopeAsync(
                    handle,
                    new Dictionary<RecapBlockId, string> {
                        [plan.RecapBlockId] = "missing"
                    },
                    lineage.CapturedHead
                )
            );
        Assert.Equal("FinalComponentChanged", stale.Code);

        var committed = Assert.IsType<
            PublishedEnvelopeCommitResult.Committed
        >(
            await restorer.CommitEnvelopeAsync(
                handle,
                new Dictionary<RecapBlockId, string> {
                    [plan.RecapBlockId] = finalToken
                },
                lineage.CapturedHead
            )
        );

        Assert.NotEqual(
            oldDescriptor.EnvelopeSha256,
            committed.Descriptor.EnvelopeSha256
        );
        await Assert.ThrowsAsync<InvalidDataException>(
            async () =>
                await fixture.Store.MaterializeAsync(oldDescriptor)
        );
        DerivedRecapMaterialization materialized =
            await fixture.Store.MaterializeAsync(
                committed.Descriptor
            );
        Assert.Equal(
            replacement.Content,
            Assert.Single(materialized.Contributions).ExactText
        );
        var selected =
            Assert.IsType<DerivedRecapSelection.Selected>(
                await fixture.Store.SelectNthPreviousAsync(lineage, 0)
            );
        Assert.Equal(committed.Descriptor, selected.Descriptor);
        Assert.IsType<PublishedFinalWriteResult.Stale>(
            await fixture.Store.InstallPublishedReplacementAsync(
                handle,
                plan.RecapBlockId,
                finalToken,
                replacement
            )
        );
        Assert.IsType<PublishedCheckpointWriteResult.Stale>(
            await fixture.Store.AdvancePublishedCheckpointAsync(
                handle,
                plan.RecapBlockId,
                finalToken,
                replacement
            )
        );

        PublishedRestoreInspection current =
            await RequireInspectionAsync(fixture, anchor, lineage);
        var already = Assert.IsType<
            PublishedEnvelopeCommitResult.AlreadyCommitted
        >(
            await restorer.CommitEnvelopeAsync(
                current.Handle,
                new Dictionary<RecapBlockId, string> {
                    [plan.RecapBlockId] =
                        current.Blocks[plan.RecapBlockId]
                            .Final.StateToken
                },
                lineage.CapturedHead
            )
        );
        Assert.Equal(committed.Descriptor, already.Descriptor);
    }

    [Fact]
    public async Task ManifestWitnessCanRebuildEnvelopeLast() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(historyPairs: 4);
        (
            DerivedRecapLineageView lineage,
            MaintainRecapBlockPlan plan,
            _
        ) = await PublishMaintainAsync(
            fixture,
            endpointCount: 1
        );
        EventAddress anchor = plan.CatchUpThrough[^1];
        File.Delete(
            Path.Combine(
                fixture.Store.GetPublishedPathForTest(anchor),
                "publication.json"
            )
        );
        PublishedRestoreInspection witness =
            await RequireInspectionAsync(fixture, anchor, lineage);
        Assert.Equal(
            PublishedRestoreAuthorityKind.ManifestWitness,
            witness.Handle.AuthorityKind
        );
        var restorer = new DerivedRecapRestorer(
            fixture.Store,
            fixture.Engine
        );

        var committed = Assert.IsType<
            PublishedEnvelopeCommitResult.Committed
        >(
            await restorer.CommitEnvelopeAsync(
                witness.Handle,
                new Dictionary<RecapBlockId, string> {
                    [plan.RecapBlockId] =
                        witness.Blocks[plan.RecapBlockId]
                            .Final.StateToken
                },
                lineage.CapturedHead
            )
        );

        _ = await fixture.Store.MaterializeAsync(
            committed.Descriptor
        );
        Assert.Equal(
            anchor,
            Assert.IsType<DerivedRecapSelection.Selected>(
                await fixture.Store.SelectNthPreviousAsync(lineage, 0)
            ).Descriptor.SetAdmissionAnchor
        );
    }

    [Fact]
    public async Task ManifestWitnessRevalidatesRequiredInputsAtCommit() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(historyPairs: 6);
        (
            DerivedRecapLineageView lineage,
            EventAddress target,
            InheritRecapBlockPlan plan
        ) = await PublishInheritAsync(fixture);
        string publishedPath =
            fixture.Store.GetPublishedPathForTest(target);
        string publicationPath =
            Path.Combine(publishedPath, "publication.json");
        File.Delete(publicationPath);
        PublishedRestoreInspection witness =
            await RequireInspectionAsync(
                fixture,
                target,
                lineage
            );
        File.Delete(
            BlockPath(
                fixture,
                target,
                "inputs",
                plan.RecapBlockId
            )
        );
        var restorer = new DerivedRecapRestorer(
            fixture.Store,
            fixture.Engine
        );

        var unavailable = Assert.IsType<
            PublishedEnvelopeCommitResult.Unavailable
        >(
            await restorer.CommitEnvelopeAsync(
                witness.Handle,
                new Dictionary<RecapBlockId, string> {
                    [plan.RecapBlockId] =
                        witness.Blocks[plan.RecapBlockId]
                            .Final.StateToken
                },
                lineage.CapturedHead
            )
        );

        Assert.Contains(
            unavailable.Defects,
            static defect =>
                defect.Code == "RestoreDependencyMissing"
        );
        Assert.False(File.Exists(publicationPath));
    }

    [Fact]
    public async Task RawHeadRaceLeavesPendingWithoutEnvelopeWrite() {
        SessionJournalEngine? engine = null;
        EventAddress capturedHead = default;
        EventAddress rewindTarget = default;
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(
                new RecapStoreTestHooks(
                    BeforeRestoreEnvelopeRawHeadRecheck: () => {
                        Assert.True(
                            engine!.MoveCurrentHeadForTest(
                                capturedHead,
                                rewindTarget
                            )
                        );
                    }
                ),
                historyPairs: 4
            );
        engine = fixture.Engine;
        (
            DerivedRecapLineageView lineage,
            MaintainRecapBlockPlan plan,
            PublishedRecapDescriptor oldDescriptor
        ) = await PublishMaintainAsync(
            fixture,
            endpointCount: 1
        );
        capturedHead = lineage.CapturedHead;
        rewindTarget = lineage.CurrentPrefix.HeadToOldest[2].Address;
        EventAddress anchor = plan.CatchUpThrough[^1];
        (
            PublishedRestoreHandle handle,
            DerivedRecapBlock replacement,
            string finalToken
        ) = await InstallPendingAsync(
            fixture,
            lineage,
            plan,
            "pending after race"
        );
        string publicationPath = Path.Combine(
            fixture.Store.GetPublishedPathForTest(anchor),
            "publication.json"
        );
        byte[] before = await File.ReadAllBytesAsync(publicationPath);
        var restorer = new DerivedRecapRestorer(
            fixture.Store,
            fixture.Engine
        );

        var stale =
            Assert.IsType<PublishedEnvelopeCommitResult.Stale>(
                await restorer.CommitEnvelopeAsync(
                    handle,
                    new Dictionary<RecapBlockId, string> {
                        [plan.RecapBlockId] = finalToken
                    },
                    capturedHead
                )
            );

        Assert.Equal("RawHeadChanged", stale.Code);
        Assert.Equal(
            before,
            await File.ReadAllBytesAsync(publicationPath)
        );
        Assert.Equal(
            replacement,
            DerivedRecapCodec.DecodeBlock(
                await File.ReadAllBytesAsync(
                    BlockPath(
                        fixture,
                        anchor,
                        "blocks",
                        plan.RecapBlockId
                    )
                )
            )
        );
        await Assert.ThrowsAsync<InvalidDataException>(
            async () =>
                await fixture.Store.MaterializeAsync(oldDescriptor)
        );
    }

    [Fact]
    public async Task AlreadyCommittedStillUsesFinalRawHeadGate() {
        SessionJournalEngine? engine = null;
        bool injectRace = false;
        EventAddress capturedHead = default;
        EventAddress rewindTarget = default;
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(
                new RecapStoreTestHooks(
                    BeforeRestoreEnvelopeRawHeadRecheck: () => {
                        if (!injectRace) {
                            return;
                        }
                        Assert.True(
                            engine!.MoveCurrentHeadForTest(
                                capturedHead,
                                rewindTarget
                            )
                        );
                    }
                ),
                historyPairs: 4
            );
        engine = fixture.Engine;
        (
            DerivedRecapLineageView lineage,
            MaintainRecapBlockPlan plan,
            PublishedRecapDescriptor descriptor
        ) = await PublishMaintainAsync(
            fixture,
            endpointCount: 1
        );
        capturedHead = lineage.CapturedHead;
        rewindTarget = lineage.CurrentPrefix.HeadToOldest[2].Address;
        PublishedRestoreInspection inspection =
            await RequireInspectionAsync(
                fixture,
                descriptor.SetAdmissionAnchor,
                lineage
            );
        string publicationPath = Path.Combine(
            fixture.Store.GetPublishedPathForTest(
                descriptor.SetAdmissionAnchor
            ),
            "publication.json"
        );
        byte[] before =
            await File.ReadAllBytesAsync(publicationPath);
        injectRace = true;
        var restorer = new DerivedRecapRestorer(
            fixture.Store,
            fixture.Engine
        );

        var stale =
            Assert.IsType<PublishedEnvelopeCommitResult.Stale>(
                await restorer.CommitEnvelopeAsync(
                    inspection.Handle,
                    new Dictionary<RecapBlockId, string> {
                        [plan.RecapBlockId] =
                            inspection.Blocks[plan.RecapBlockId]
                                .Final.StateToken
                    },
                    capturedHead
                )
            );

        Assert.Equal("RawHeadChanged", stale.Code);
        Assert.Equal(
            before,
            await File.ReadAllBytesAsync(publicationPath)
        );
    }

    [Fact]
    public async Task DamagedFinalCasTokenDetectsByteRace() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(historyPairs: 4);
        (
            DerivedRecapLineageView lineage,
            MaintainRecapBlockPlan plan,
            PublishedRecapDescriptor descriptor
        ) = await PublishMaintainAsync(
            fixture,
            endpointCount: 1
        );
        string finalPath = BlockPath(
            fixture,
            descriptor.SetAdmissionAnchor,
            "blocks",
            plan.RecapBlockId
        );
        string workPath = BlockPath(
            fixture,
            descriptor.SetAdmissionAnchor,
            "work",
            plan.RecapBlockId
        );
        await File.WriteAllTextAsync(finalPath, "damaged-a");
        PublishedRestoreInspection first =
            await RequireInspectionAsync(
                fixture,
                descriptor.SetAdmissionAnchor,
                lineage
            );
        var firstDamage =
            Assert.IsType<FinalRecapBlockHealth.Damaged>(
                first.Blocks[plan.RecapBlockId].Final
            );
        DerivedRecapBlock candidate =
            DerivedRecapCodec.DecodeBlock(
                await File.ReadAllBytesAsync(workPath)
            );
        await File.WriteAllTextAsync(finalPath, "damaged-b");

        var stale = Assert.IsType<PublishedFinalWriteResult.Stale>(
            await fixture.Store.InstallPublishedReplacementAsync(
                first.Handle,
                plan.RecapBlockId,
                firstDamage.StateToken,
                candidate
            )
        );

        Assert.NotEqual(firstDamage.StateToken, stale.CurrentStateToken);
        Assert.Equal(
            "damaged-b",
            await File.ReadAllTextAsync(finalPath)
        );
    }

    [Fact]
    public async Task UnobservableFinalIsUnavailableForMutation() {
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(historyPairs: 4);
        (
            DerivedRecapLineageView lineage,
            MaintainRecapBlockPlan plan,
            PublishedRecapDescriptor descriptor
        ) = await PublishMaintainAsync(
            fixture,
            endpointCount: 1
        );
        string finalPath = BlockPath(
            fixture,
            descriptor.SetAdmissionAnchor,
            "blocks",
            plan.RecapBlockId
        );
        string workPath = BlockPath(
            fixture,
            descriptor.SetAdmissionAnchor,
            "work",
            plan.RecapBlockId
        );
        byte[] oversized = new byte[
            checked((int)DerivedRecapStore.MaxBlockBytes + 1)
        ];
        await File.WriteAllBytesAsync(finalPath, oversized);
        PublishedRestoreInspection inspection =
            await RequireInspectionAsync(
                fixture,
                descriptor.SetAdmissionAnchor,
                lineage
            );
        PublishedBlockRestoreInspection block =
            inspection.Blocks[plan.RecapBlockId];
        Assert.IsType<FinalRecapBlockHealth.Unavailable>(
            block.Final
        );
        Assert.IsType<PublishedBlockRestoreCapability.Unavailable>(
            block.Capability
        );
        DerivedRecapBlock candidate =
            DerivedRecapCodec.DecodeBlock(
                await File.ReadAllBytesAsync(workPath)
            );

        Assert.IsType<PublishedFinalWriteResult.Unavailable>(
            await fixture.Store.InstallPublishedReplacementAsync(
                inspection.Handle,
                plan.RecapBlockId,
                "unavailable",
                candidate
            )
        );
        Assert.Equal(
            oversized.Length,
            new FileInfo(finalPath).Length
        );
    }

    private static async ValueTask<(
        DerivedRecapLineageView Lineage,
        MaintainRecapBlockPlan Plan,
        PublishedRecapDescriptor Descriptor
    )> PublishMaintainAsync(
        RecapStoreFixture fixture,
        int endpointCount
    ) {
        DerivedRecapLineageView lineage = fixture.Lineage();
        EventAddress anchor = lineage.CapturedHead;
        EventAddress[] endpoints = endpointCount switch {
            1 => [anchor],
            2 => [lineage.CurrentPrefix.HeadToOldest[2].Address, anchor],
            _ => throw new ArgumentOutOfRangeException(
                nameof(endpointCount)
            )
        };
        var plan = new MaintainRecapBlockPlan(
            new RecapBlockId("roleplay.self"),
            new ContextHeaderBlockPath(
                ContextHeaderCarrier.System,
                "roleplay.self"
            ),
            "roleplay.autobiographical",
            RecapTestIdentity.CapabilityFingerprint,
            new EmptyRecapMaintainSource(
                lineage.CurrentPrefix.HeadToOldest[^1].Address
            ),
            endpoints,
            EmptyRecapPriorContext.Instance
        );
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
            DerivedRecapCodec.CreateBlock(
                plan,
                anchor,
                "committed"
            )
        );
        PublishedRecapDescriptor descriptor = Assert.IsType<
            PublishRecapResult.Published
        >(await fixture.Publisher.PublishAsync(anchor)).Descriptor;
        return (lineage, plan, descriptor);
    }

    private static async ValueTask<(
        DerivedRecapLineageView Lineage,
        EventAddress Target,
        InheritRecapBlockPlan Plan
    )> PublishInheritAsync(
        RecapStoreFixture fixture
    ) {
        DerivedRecapLineageView lineage = fixture.Lineage();
        EventAddress target = lineage.CapturedHead;
        EventAddress source = lineage.CurrentPrefix.HeadToOldest[4].Address;
        const string sourceContent = "source recap";
        PublishedRecapDescriptor sourceDescriptor =
            await fixture.PublishAsync(
                source,
                lineage.CurrentPrefix.HeadToOldest[^1].Address,
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
        _ = await fixture.Publisher.PublishAsync(target);
        return (lineage, target, plan);
    }

    private static async ValueTask<(
        PublishedRestoreHandle Handle,
        DerivedRecapBlock Replacement,
        string FinalToken
    )> InstallPendingAsync(
        RecapStoreFixture fixture,
        DerivedRecapLineageView lineage,
        MaintainRecapBlockPlan plan,
        string content
    ) {
        EventAddress anchor = plan.CatchUpThrough[^1];
        PublishedRestoreInspection inspection =
            await RequireInspectionAsync(fixture, anchor, lineage);
        File.Delete(
            BlockPath(
                fixture,
                anchor,
                "work",
                plan.RecapBlockId
            )
        );
        DerivedRecapBlock replacement =
            DerivedRecapCodec.CreateBlock(
                plan,
                anchor,
                content
            );
        _ = Assert.IsType<
            PublishedCheckpointWriteResult.Updated
        >(
            await fixture.Store.AdvancePublishedCheckpointAsync(
                inspection.Handle,
                plan.RecapBlockId,
                "missing",
                replacement
            )
        );
        await File.WriteAllTextAsync(
            BlockPath(
                fixture,
                anchor,
                "blocks",
                plan.RecapBlockId
            ),
            "damaged"
        );
        PublishedRestoreInspection damaged =
            await RequireInspectionAsync(fixture, anchor, lineage);
        var installed = Assert.IsType<
            PublishedFinalWriteResult.ReplacedDamaged
        >(
            await fixture.Store.InstallPublishedReplacementAsync(
                damaged.Handle,
                plan.RecapBlockId,
                damaged.Blocks[plan.RecapBlockId]
                    .Final.StateToken,
                replacement
            )
        );
        return (
            damaged.Handle,
            replacement,
            installed.StateToken
        );
    }

    private static async ValueTask<PublishedRestoreInspection>
        RequireInspectionAsync(
        RecapStoreFixture fixture,
        EventAddress anchor,
        DerivedRecapLineageView lineage
    ) => Assert.IsType<
        PublishedRestoreInspectionResult.Available
    >(
        await fixture.Store.InspectPublishedForRestoreAsync(
            anchor,
            lineage
        )
    ).Inspection;

    private static string BlockPath(
        RecapStoreFixture fixture,
        EventAddress anchor,
        string directory,
        RecapBlockId blockId
    ) => Path.Combine(
        fixture.Store.GetPublishedPathForTest(anchor),
        directory,
        $"{blockId.Value}.json"
    );
}
