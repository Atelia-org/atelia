using Atelia.EventJournal;
using Xunit;

namespace Atelia.SessionJournal.DerivedRecap.Store.Tests;

public sealed class DerivedRecapPublishedRestoreWriteTests {
    [Fact]
    public async Task MissingFinalPrecedesPlanBeyondAcrossExactSurfaces() {
        int mutationCount = 0;
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(
                new RecapStoreTestHooks(
                    BeforeAtomicFileReplace: _ => mutationCount++
                ),
                historyPairs: 259
            );
        DerivedRecapLineageView lineage = fixture.Lineage();
        EventAddress anchor = lineage.CapturedHead;
        EventAddress beyond = fixture.RawLineage().HeadToRoot[
            lineage.CurrentPrefix.MaxHeaderCount
        ].Address;
        _ = await fixture.PublishAsync(
            anchor,
            lineage.CurrentPrefix.HeadToOldest[^2].Address
        );
        var plan = (MaintainRecapBlockPlan)fixture.CreateMaintainPlan(
            anchor,
            beyond
        );
        DerivedRecapSetManifest manifest =
            RecapWireTestFacts.CreateManifest(
                fixture.Engine,
                anchor,
                [plan]
            );
        DerivedRecapBlock committed = DerivedRecapCodec.CreateBlock(
            plan,
            anchor,
            "deep plan"
        );
        _ = await RecapStoreTestDriver.RewritePublishedUncheckedAsync(
                fixture.Store,
                manifest,
                [committed]
            );
        File.Delete(BlockPath(
            fixture,
            anchor,
            "blocks",
            plan.RecapBlockId
        ));

        Assert.IsType<DerivedRecapSelection.BeyondPrefix>(
            await fixture.Store.SelectNthPreviousAsync(lineage, 0)
        );
        var inspection = Assert.IsType<
            PublishedRestoreInspectionResult.Available
        >(
            await fixture.Store.InspectPublishedForOfflineDiagnosticsAsync(
                anchor,
                lineage
            )
        ).Inspection;
        Assert.IsType<FinalRecapBlockHealth.Missing>(
            inspection.Blocks[plan.RecapBlockId].Final
        );
        mutationCount = 0;

        Assert.IsType<PublishedEnvelopeCommitResult.Unavailable>(
            await new DerivedRecapRestorer(
                    fixture.Store,
                    fixture.ReadView
                )
                .CommitEnvelopeAsync(
                    CommitAuthority(fixture, inspection),
                    lineage.CapturedHead
                )
        );
        Assert.Equal(0, mutationCount);

        DerivedRecapBlock pending = DerivedRecapCodec.CreateBlock(
            plan,
            anchor,
            "valid pending"
        );
        await File.WriteAllBytesAsync(
            BlockPath(
                fixture,
                anchor,
                "blocks",
                plan.RecapBlockId
            ),
            DerivedRecapCodec.EncodeBlock(pending)
        );
        Assert.IsType<PublishedRestoreInspectionResult.BeyondPrefix>(
            await fixture.Store.InspectPublishedForOfflineDiagnosticsAsync(
                anchor,
                lineage
            )
        );
        Assert.Equal(0, mutationCount);
    }

    [Fact]
    public async Task
        PendingSemanticsPrecedeInputBeyondAndCommittedIgnoresIt() {
        int mutationCount = 0;
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(
                new RecapStoreTestHooks(
                    BeforeAtomicFileReplace: _ => mutationCount++
                ),
                historyPairs: 259
            );
        DerivedRecapLineageView lineage = fixture.Lineage();
        EventAddress anchor = lineage.CapturedHead;
        EventAddress source =
            lineage.CurrentPrefix.HeadToOldest[2].Address;
        EventAddress beyond = fixture.RawLineage().HeadToRoot[
            lineage.CurrentPrefix.MaxHeaderCount
        ].Address;
        _ = await fixture.PublishAsync(
            anchor,
            lineage.CurrentPrefix.HeadToOldest[^2].Address
        );
        var id = new RecapBlockId("roleplay.self");
        var target = new ContextHeaderBlockPath(
            ContextHeaderCarrier.System,
            id.Value
        );
        DerivedRecapFrozenInput input =
            RecapWireTestFacts.CreateFrozenInput(fixture.Engine,
                id,
                target,
                beyond,
                "deep input"
            );
        var plan = new InheritRecapBlockPlan(
            id,
            target,
            source,
            input.AbsorbedThroughSetups,
            new string('a', 64),
            input.PayloadSha256
        );
        DerivedRecapSetManifest manifest =
            RecapWireTestFacts.CreateManifest(
                fixture.Engine,
                anchor,
                [plan]
            );
        DerivedRecapBlock committed = DerivedRecapCodec.CreateBlock(
            plan,
            source,
            "committed authority"
        );
        DerivedRecapBlock forged = DerivedRecapCodec.CreateBlock(
            plan,
            beyond,
            input.Content + " forged"
        );
        PublishedRecapSet publication =
            await RecapStoreTestDriver.RewritePublishedUncheckedAsync(
                fixture.Store,
                manifest,
                [committed],
                [forged]
            );
        await File.WriteAllBytesAsync(
            BlockPath(fixture, anchor, "inputs", id),
            DerivedRecapCodec.EncodeFrozenInput(input)
        );

        Assert.IsType<DerivedRecapSelection.Selected>(
            await fixture.Store.SelectNthPreviousAsync(lineage, 0)
        );
        var inspection = Assert.IsType<
            PublishedRestoreInspectionResult.Available
        >(
            await fixture.Store.InspectPublishedForOfflineDiagnosticsAsync(
                anchor,
                lineage
            )
        ).Inspection;
        Assert.IsType<FrozenRecapInputHealth.Healthy>(
            inspection.Blocks[id].FrozenInput
        );
        Assert.IsType<FinalRecapBlockHealth.Damaged>(
            inspection.Blocks[id].Final
        );
        mutationCount = 0;

        Assert.IsType<PublishedEnvelopeCommitResult.Unavailable>(
            await new DerivedRecapRestorer(
                    fixture.Store,
                    fixture.ReadView
                )
                .CommitEnvelopeAsync(
                    CommitAuthority(fixture, inspection),
                    lineage.CapturedHead
                )
        );
        Assert.Equal(0, mutationCount);

        await File.WriteAllBytesAsync(
            BlockPath(fixture, anchor, "blocks", id),
            DerivedRecapCodec.EncodeBlock(committed)
        );
        var committedInspection = Assert.IsType<
            PublishedRestoreInspectionResult.Available
        >(
            await fixture.Store.InspectPublishedForOfflineDiagnosticsAsync(
                anchor,
                lineage
            )
        ).Inspection;
        Assert.IsType<PublishedBlockRestoreCapability.KeepCommitted>(
            committedInspection.Blocks[id].Capability
        );
        var already = Assert.IsType<
            PublishedEnvelopeCommitResult.AlreadyCommitted
        >(
            await new DerivedRecapRestorer(
                    fixture.Store,
                    fixture.ReadView
                )
                .CommitEnvelopeAsync(
                    CommitAuthority(fixture, committedInspection),
                    lineage.CapturedHead
                )
        );
        Assert.Equal(
            publication.EnvelopeSha256,
            already.Descriptor.EnvelopeSha256
        );
        Assert.Equal(0, mutationCount);
    }

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
            lineage.CurrentPrefix.HeadToOldest[^2].Address
        );
        _ = await fixture.PublishAsync(
            anchor,
            lineage.CurrentPrefix.HeadToOldest[^2].Address
        );
        DerivedRecapSetManifest manifest =
            RecapWireTestFacts.CreateManifest(
                fixture.Engine,
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
                plan.CatchUpBoundaries,
                plan.PriorContext
            );
            persisted = DerivedRecapCodec.CreateBlock(
                wrongPlan,
                beyond,
                "committed beyond prefix"
            );
        }
        _ = await RecapStoreTestDriver.RewritePublishedUncheckedAsync(
                fixture.Store,
                manifest,
                [committed],
                [persisted]
            );
        mutationCount = 0;

        PublishedRestoreInspectionResult result =
            await fixture.Store.InspectPublishedForOfflineDiagnosticsAsync(
                anchor,
                lineage
            );

        if (persistCommitmentMismatch) {
            PublishedRestoreInspection damaged = Assert.IsType<
                PublishedRestoreInspectionResult.Available
            >(result).Inspection;
            Assert.IsType<PublishedEnvelopeCommitResult.Unavailable>(
                await new DerivedRecapRestorer(
                        fixture.Store,
                        fixture.ReadView
                    )
                    .CommitEnvelopeAsync(
                        CommitAuthority(fixture, damaged),
                        lineage.CapturedHead
                    )
            );
        }
        else {
            var beyondResult = Assert.IsType<
                PublishedRestoreInspectionResult.BeyondPrefix
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
        EventAddress anchor = plan.CatchUpBoundaries[^1].Address;
        string workPath = BlockPath(
            fixture,
            anchor,
            "work",
            plan.RecapBlockId
        );
        File.Delete(workPath);
        PublishedRestoreInspection inspection =
            await RequireInspectionAsync(fixture, anchor, lineage);
        PublishedBlockWriteAuthority missingAuthority =
            inspection.Blocks[plan.RecapBlockId].WriteAuthority;
        DerivedRecapBlock first =
            DerivedRecapCodec.CreateBlock(
                plan,
                plan.CatchUpBoundaries[0].Address,
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
                        missingAuthority,
                        final
                    )
        );
        var updated = Assert.IsType<
            PublishedCheckpointWriteResult.Updated
        >(
            await fixture.Store.AdvancePublishedCheckpointAsync(
                missingAuthority,
                first
            )
        );
        var alreadyCurrent = Assert.IsType<
            PublishedCheckpointWriteResult.AlreadyCurrent
        >(
            await fixture.Store.AdvancePublishedCheckpointAsync(
                updated.WriteAuthority,
                first
            )
        );
        Assert.NotNull(alreadyCurrent.WriteAuthority);
        Assert.IsType<PublishedCheckpointWriteResult.Stale>(
            await fixture.Store.AdvancePublishedCheckpointAsync(
                missingAuthority,
                first
            )
        );
        Assert.IsType<
            PublishedCheckpointWriteResult.Updated
        >(
            await fixture.Store.AdvancePublishedCheckpointAsync(
                alreadyCurrent.WriteAuthority,
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
        EventAddress anchor = plan.CatchUpBoundaries[^1].Address;
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
        PublishedBlockWriteAuthority initialAuthority =
            initial.Blocks[plan.RecapBlockId].WriteAuthority;
        await Assert.ThrowsAsync<InvalidDataException>(
            async () =>
                await fixture.Store
                    .InstallPublishedReplacementAsync(
                        initialAuthority,
                        replacement
                    )
        );

        File.Delete(workPath);
        PublishedRestoreInspection missingCheckpoint =
            await RequireInspectionAsync(fixture, anchor, lineage);
        var checkpoint = Assert.IsType<
            PublishedCheckpointWriteResult.Updated
        >(
            await fixture.Store.AdvancePublishedCheckpointAsync(
                missingCheckpoint.Blocks[plan.RecapBlockId]
                    .WriteAuthority,
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
                checkpoint.WriteAuthority,
                replacement
            )
        );
        var alreadyHealthy = Assert.IsType<
            PublishedFinalWriteResult.AlreadyHealthy
        >(
            await fixture.Store.InstallPublishedReplacementAsync(
                installed.WriteAuthority,
                replacement
            )
        );
        Assert.NotNull(alreadyHealthy.WriteAuthority);
        Assert.IsType<PublishedFinalWriteResult.HealthyConflict>(
            await fixture.Store.InstallPublishedReplacementAsync(
                alreadyHealthy.WriteAuthority,
                DerivedRecapCodec.CreateBlock(
                    plan,
                    anchor,
                    "conflict"
                )
            )
        );
        Assert.IsType<PublishedFinalWriteResult.Stale>(
            await fixture.Store.InstallPublishedReplacementAsync(
                checkpoint.WriteAuthority,
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
        EventAddress anchor = plan.CatchUpBoundaries[^1].Address;
        (
            DerivedRecapBlock replacement,
            PublishedBlockWriteAuthority staleBlockAuthority,
            PublishedEnvelopeCommitAuthority commitAuthority,
            PublishedEnvelopeCommitAuthority staleCommitAuthority
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
            fixture.ReadView
        );
        var stale =
            Assert.IsType<PublishedEnvelopeCommitResult.Stale>(
                await restorer.CommitEnvelopeAsync(
                    staleCommitAuthority,
                    lineage.CapturedHead
                )
            );
        Assert.Equal("FinalComponentChanged", stale.Code);

        var committed = Assert.IsType<
            PublishedEnvelopeCommitResult.Committed
        >(
            await restorer.CommitEnvelopeAsync(
                commitAuthority,
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
                staleBlockAuthority,
                replacement
            )
        );
        Assert.IsType<PublishedCheckpointWriteResult.Stale>(
            await fixture.Store.AdvancePublishedCheckpointAsync(
                staleBlockAuthority,
                replacement
            )
        );

        PublishedRestoreInspection current =
            await RequireInspectionAsync(fixture, anchor, lineage);
        var already = Assert.IsType<
            PublishedEnvelopeCommitResult.AlreadyCommitted
        >(
            await restorer.CommitEnvelopeAsync(
                CommitAuthority(fixture, current),
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
        EventAddress anchor = plan.CatchUpBoundaries[^1].Address;
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
            fixture.ReadView
        );

        var committed = Assert.IsType<
            PublishedEnvelopeCommitResult.Committed
        >(
            await restorer.CommitEnvelopeAsync(
                CommitAuthority(fixture, witness),
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
            fixture.ReadView
        );

        var unavailable = Assert.IsType<
            PublishedEnvelopeCommitResult.Unavailable
        >(
            await restorer.CommitEnvelopeAsync(
                CommitAuthority(fixture, witness),
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AlreadyCommittedIgnoresAuxiliaryInputLoss(
        bool damageInput
    ) {
        int atomicWrites = 0;
        using RecapStoreFixture fixture =
            await RecapStoreFixture.CreateAsync(
                new RecapStoreTestHooks(
                    BeforeAtomicFileReplace: _ => atomicWrites++
                ),
                historyPairs: 6
            );
        (
            DerivedRecapLineageView lineage,
            EventAddress target,
            InheritRecapBlockPlan plan
        ) = await PublishInheritAsync(fixture);
        var descriptor = Assert.IsType<DerivedRecapSelection.Selected>(
            await fixture.Store.SelectNthPreviousAsync(lineage, 0)
        ).Descriptor;
        string inputPath = BlockPath(
            fixture,
            target,
            "inputs",
            plan.RecapBlockId
        );
        if (damageInput) {
            await File.WriteAllTextAsync(inputPath, "damaged");
        }
        else {
            File.Delete(inputPath);
        }
        PublishedRestoreInspection inspection =
            await RequireInspectionAsync(fixture, target, lineage);
        Assert.IsType<
            PublishedBlockRestoreCapability.KeepCommitted
        >(inspection.Blocks[plan.RecapBlockId].Capability);
        string publicationPath = Path.Combine(
            fixture.Store.GetPublishedPathForTest(target),
            "publication.json"
        );
        byte[] publicationBefore =
            await File.ReadAllBytesAsync(publicationPath);
        atomicWrites = 0;
        var restorer = new DerivedRecapRestorer(
            fixture.Store,
            fixture.ReadView
        );

        var already = Assert.IsType<
            PublishedEnvelopeCommitResult.AlreadyCommitted
        >(
            await restorer.CommitEnvelopeAsync(
                CommitAuthority(fixture, inspection),
                lineage.CapturedHead
            )
        );

        Assert.Equal(descriptor, already.Descriptor);
        Assert.Equal(0, atomicWrites);
        Assert.Equal(
            publicationBefore,
            await File.ReadAllBytesAsync(publicationPath)
        );
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
        EventAddress anchor = plan.CatchUpBoundaries[^1].Address;
        (
            DerivedRecapBlock replacement,
            _,
            PublishedEnvelopeCommitAuthority commitAuthority,
            _
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
            fixture.ReadView
        );

        var stale =
            Assert.IsType<PublishedEnvelopeCommitResult.Stale>(
                await restorer.CommitEnvelopeAsync(
                    commitAuthority,
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
            fixture.ReadView
        );

        var stale =
            Assert.IsType<PublishedEnvelopeCommitResult.Stale>(
                await restorer.CommitEnvelopeAsync(
                    CommitAuthority(fixture, inspection),
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
                first.Blocks[plan.RecapBlockId].WriteAuthority,
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
                block.WriteAuthority,
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
                lineage.CurrentPrefix.HeadToOldest[^2].Address,
                fixture.Setups(
                    lineage.CurrentPrefix.HeadToOldest[^2].Address
                )
            ),
            RecapWireTestFacts.ResolveBoundaries(
                fixture.Engine,
                endpoints
            ),
            EmptyRecapPriorContext.Instance
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
                lineage.CurrentPrefix.HeadToOldest[^2].Address,
                content: sourceContent
            );
        var id = new RecapBlockId("roleplay.self");
        var targetPath = new ContextHeaderBlockPath(
            ContextHeaderCarrier.System,
            id.Value
        );
        DerivedRecapFrozenInput input =
            RecapWireTestFacts.CreateFrozenInput(fixture.Engine,
                id,
                targetPath,
                source,
                sourceContent
            );
        var plan = new InheritRecapBlockPlan(
            id,
            targetPath,
            source,
            input.AbsorbedThroughSetups,
            sourceDescriptor.EnvelopeSha256,
            input.PayloadSha256
        );
        DerivedRecapSetManifest manifest =
            RecapWireTestFacts.CreateManifest(
                fixture.Engine,
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
        DerivedRecapBlock Replacement,
        PublishedBlockWriteAuthority StaleBlockAuthority,
        PublishedEnvelopeCommitAuthority CommitAuthority,
        PublishedEnvelopeCommitAuthority StaleCommitAuthority
    )> InstallPendingAsync(
        RecapStoreFixture fixture,
        DerivedRecapLineageView lineage,
        MaintainRecapBlockPlan plan,
        string content
    ) {
        EventAddress anchor = plan.CatchUpBoundaries[^1].Address;
        File.Delete(
            BlockPath(
                fixture,
                anchor,
                "work",
                plan.RecapBlockId
            )
        );
        PublishedRestoreInspection inspection =
            await RequireInspectionAsync(fixture, anchor, lineage);
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
                inspection.Blocks[plan.RecapBlockId].WriteAuthority,
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
        PublishedEnvelopeCommitAuthority staleCommitAuthority =
            CommitAuthority(fixture, damaged);
        var installed = Assert.IsType<
            PublishedFinalWriteResult.ReplacedDamaged
        >(
            await fixture.Store.InstallPublishedReplacementAsync(
                damaged.Blocks[plan.RecapBlockId].WriteAuthority,
                replacement
            )
        );
        return (
            replacement,
            installed.WriteAuthority,
            fixture.Store.IssuePublishedEnvelopeCommitAuthority(
                damaged.Handle,
                [installed.WriteAuthority]
            ),
            staleCommitAuthority
        );
    }

    private static PublishedEnvelopeCommitAuthority CommitAuthority(
        RecapStoreFixture fixture,
        PublishedRestoreInspection inspection
    ) => fixture.Store.IssuePublishedEnvelopeCommitAuthority(
        inspection.Handle,
        inspection.Blocks.Values
            .Select(static block => block.WriteAuthority)
            .ToArray()
    );

    private static async ValueTask<PublishedRestoreInspection>
        RequireInspectionAsync(
        RecapStoreFixture fixture,
        EventAddress anchor,
        DerivedRecapLineageView lineage
    ) => Assert.IsType<
        PublishedRestoreInspectionResult.Available
    >(
        await fixture.Store.InspectPublishedForOfflineDiagnosticsAsync(
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
