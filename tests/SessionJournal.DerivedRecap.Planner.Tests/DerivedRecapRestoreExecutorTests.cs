using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.SessionJournal.DerivedRecap.Store;
using Xunit;

namespace Atelia.SessionJournal.DerivedRecap.Planner.Tests;

public sealed class DerivedRecapRestoreExecutorTests {
    [Fact]
    public async Task HealthyRestoreProvesRoutesWithoutRawMaterialization() {
        using RestoreFixture fixture =
            await RestoreFixture.CreateAsync();
        (
            MaintainRecapBlockPlan plan,
            _
        ) = await fixture.PublishMaintainAsync(endpointCount: 1);
        EventAddress anchor = plan.CatchUpBoundaries[^1].Address;
        var maintainer = fixture.CreateMaintainer(
            plan,
            static (_, _) => "must-not-run"
        );
        SessionJournalReadDiagnostics before =
            fixture.Engine.CaptureReadDiagnostics();

        _ = Assert.IsType<DerivedRecapRestoreResult.Restored>(
            await fixture.CreateExecutor([maintainer])
                .RestoreAsync(anchor, fixture.CurrentHead)
        );

        SessionJournalReadDiagnostics after =
            fixture.Engine.CaptureReadDiagnostics();
        Assert.Equal(
            2,
            after.PayloadReadCount - before.PayloadReadCount
        );
        Assert.Equal(0, maintainer.CallCount);
    }

    [Fact]
    public async Task PublishedCommitmentBeyondStopsBeforeRestoreComponents() {
        int componentReads = 0;
        using RestoreFixture fixture =
            await RestoreFixture.CreateAsync(
                historyPairs: 259,
                hooks: new RecapStoreTestHooks(
                    BeforeRestoreComponentRead: () => componentReads++
                )
            );
        MaintainRecapBlockPlan template = fixture.CreateMaintainPlan(
            "frozen.self",
            "frozen-maintainer",
            endpointCount: 1
        );
        _ = fixture.Engine.AppendRuntimeConfigSetup(
            new SessionRuntimeConfiguration(
                "model-current-commitment",
                "surface-current-commitment",
                SessionJournalDefaults.Schema,
                new(0)
            )
        );
        _ = fixture.Engine.AppendSystemPromptSetup(
            "system-current-commitment"
        );
        EventAddress recentStart = fixture.AppendPair(
            "current-commitment-start"
        );
        EventAddress anchor = fixture.AppendPair(
            "current-commitment-anchor"
        );
        var plan = new MaintainRecapBlockPlan(
            template.RecapBlockId,
            template.Target,
            template.MaintainerId,
            template.MaintainerCapabilityFingerprint,
            new EmptyRecapMaintainSource(
                recentStart,
                RecapPlannerWireTestFacts.SetupsAt(
                    fixture.Engine,
                    recentStart
                )
            ),
            [new RecapReplayBoundary(
                anchor,
                RecapPlannerWireTestFacts.SetupsAt(
                    fixture.Engine,
                    anchor
                )
            )],
            template.PriorContext,
            template.MaxContentUtf8Bytes
        );
        _ = await fixture.PublishAsync(
            anchor,
            [plan],
            new Dictionary<RecapBlockId, string> {
                [plan.RecapBlockId] = "committed"
            }
        );
        EventAddress beyond = fixture.Engine
            .ReadCurrentLineageHeaders().HeadToRoot[
                fixture.Lineage.MaxHeaderCount
            ].Address;
        DerivedRecapSetManifest manifest = Assert.IsType<
            PublishedPlanAtAnchorReadResult.Available
        >(await fixture.Store.ReadPublishedPlanAtAnchorAsync(anchor))
            .Snapshot.FrozenPlan;
        DerivedRecapBlock rewrittenFinal =
            DerivedRecapCodec.CreateBlock(
                plan,
                beyond,
                "rewritten beyond final"
            );
        await File.WriteAllBytesAsync(
            fixture.PublicationPath(anchor),
            DerivedRecapCodec.EncodePublication(
                DerivedRecapCodec.CreatePublication(
                    manifest,
                    [rewrittenFinal]
                )
            )
        );
        await File.WriteAllBytesAsync(
            fixture.BlockPath(anchor, "blocks", plan.RecapBlockId),
            DerivedRecapCodec.EncodeBlock(rewrittenFinal)
        );
        componentReads = 0;
        var maintainer = fixture.CreateMaintainer(
            plan,
            static (_, _) => "must-not-run"
        );

        var result = Assert.IsType<
            DerivedRecapRestoreResult.BeyondPrefix
        >(await fixture.CreateExecutor([maintainer])
            .RestoreAsync(anchor, fixture.CurrentHead));

        Assert.Equal(
            DerivedRecapBeyondPrefixStage.RestorePendingWindow,
            result.Stage
        );
        Assert.Equal(beyond, result.Evidence.RequiredAnchor);
        Assert.Equal(0, componentReads);
        Assert.Equal(0, maintainer.CallCount);
    }

    [Fact]
    public async Task RestoreInheritSourceAt514IsBeyondBeforePayloadOrComponents() {
        int componentReads = 0;
        using RestoreFixture fixture =
            await RestoreFixture.CreateAsync(
                historyPairs: 2,
                hooks: new RecapStoreTestHooks(
                    BeforeRestoreComponentRead: () => componentReads++
                )
            );
        MaintainRecapBlockPlan sourcePlan = fixture.CreateMaintainPlan(
            "frozen.self",
            "frozen-maintainer",
            endpointCount: 1
        );
        EventAddress sourceAnchor = fixture.CurrentHead;
        PublishedRecapDescriptor sourceDescriptor =
            await fixture.PublishAsync(
                sourceAnchor,
                [sourcePlan],
                new Dictionary<RecapBlockId, string> {
                    [sourcePlan.RecapBlockId] = "source-content"
                }
            );
        DerivedRecapFrozenInput input =
            DerivedRecapCodec.CreateFrozenInput(
                sourcePlan.RecapBlockId,
                sourcePlan.Target,
                sourceAnchor,
                RecapPlannerWireTestFacts.SetupsAt(
                    fixture.Engine,
                    sourceAnchor
                ),
                "source-content"
            );
        for (int index = 0; index < 256; index++) {
            fixture.AppendPair($"inherit-514-{index}");
        }
        EventAddress target = fixture.Engine.AppendObservation(
            "inherit-514-target"
        );
        var inherit = new InheritRecapBlockPlan(
            sourcePlan.RecapBlockId,
            sourcePlan.Target,
            sourceAnchor,
            input.AbsorbedThroughSetups,
            sourceDescriptor.EnvelopeSha256,
            input.PayloadSha256,
            RestoreFixture.MaxContent
        );
        DerivedRecapSetManifest manifest =
            DerivedRecapCodec.CreateManifest(
                fixture.Engine.BranchRefId,
                target,
                RecapPlannerWireTestFacts.SetupsAt(
                    fixture.Engine,
                    target
                ),
                [inherit]
            );
        SessionCurrentLineagePrefix prefix =
            fixture.Engine.ReadLineagePrefixAt(
                target,
                RecapFrozenPlanBarrier.MaxHeaderCount
            );
        Assert.Equal(
            sourceAnchor,
            prefix.Continuation!.NextAddress
        );
        componentReads = 0;
        SessionJournalReadDiagnostics before =
            fixture.Engine.CaptureReadDiagnostics();

        RecapFrozenPlanBarrierResult result =
            await RecapFrozenPlanBarrier.ProveAsync(
                fixture.Engine,
                fixture.Store,
                manifest,
                prefix,
                target,
                RecapProtocolHardCaps.V4,
                CancellationToken.None
            );

        Assert.Empty(result.Defects);
        SessionCurrentLineageBeyondPrefix beyond = Assert.IsType<
            SessionCurrentLineageBeyondPrefix
        >(result.BeyondPrefix);
        Assert.Equal(
            sourceAnchor,
            beyond.RequiredAnchor
        );
        Assert.Equal(
            sourceAnchor,
            beyond.NextAddress
        );
        Assert.Equal(
            before.PayloadReadCount,
            fixture.Engine.CaptureReadDiagnostics().PayloadReadCount
        );
        Assert.Equal(0, componentReads);
    }

    [Fact]
    public async Task ForgedMaintainSourceCommitmentIsFrozenAuthorityDefect() {
        using RestoreFixture fixture =
            await RestoreFixture.CreateAsync();
        MaintainRecapBlockPlan sourcePlan = fixture.CreateMaintainPlan(
            "frozen.self",
            "frozen-maintainer",
            endpointCount: 1
        );
        EventAddress sourceAnchor = fixture.CurrentHead;
        _ = await fixture.PublishAsync(
            sourceAnchor,
            [sourcePlan],
            new Dictionary<RecapBlockId, string> {
                [sourcePlan.RecapBlockId] = "source-content"
            }
        );
        DerivedRecapSetManifest sourceManifest = Assert.IsType<
            PublishedPlanAtAnchorReadResult.Available
        >(
            await fixture.Store.ReadPublishedPlanAtAnchorAsync(
                sourceAnchor
            )
        ).Snapshot.FrozenPlan;
        var sourceEmpty = (EmptyRecapMaintainSource)sourcePlan.Source;
        DerivedRecapBlock forgedSourceFinal =
            DerivedRecapCodec.CreateBlock(
                sourcePlan,
                sourceEmpty.ReplayStartExclusive,
                "source-content"
            );
        PublishedRecapSet forgedSourcePublication =
            DerivedRecapCodec.CreatePublication(
                sourceManifest,
                [forgedSourceFinal]
            );
        await File.WriteAllBytesAsync(
            fixture.PublicationPath(sourceAnchor),
            DerivedRecapCodec.EncodePublication(
                forgedSourcePublication
            )
        );
        await File.WriteAllBytesAsync(
            fixture.BlockPath(
                sourceAnchor,
                "blocks",
                sourcePlan.RecapBlockId
            ),
            DerivedRecapCodec.EncodeBlock(forgedSourceFinal)
        );
        DerivedRecapFrozenInput input =
            DerivedRecapCodec.CreateFrozenInput(
                sourcePlan.RecapBlockId,
                sourcePlan.Target,
                sourceEmpty.ReplayStartExclusive,
                sourceEmpty.ReplayStartSetups,
                "source-content"
            );
        EventAddress target = fixture.AppendPair("forged-target");
        var targetPlan = new MaintainRecapBlockPlan(
            sourcePlan.RecapBlockId,
            sourcePlan.Target,
            sourcePlan.MaintainerId,
            sourcePlan.MaintainerCapabilityFingerprint,
            new ExistingRecapMaintainSource(
                sourceAnchor,
                input.AbsorbedThroughSetups,
                forgedSourcePublication.EnvelopeSha256,
                input.PayloadSha256
            ),
            [new RecapReplayBoundary(
                target,
                RecapPlannerWireTestFacts.SetupsAt(
                    fixture.Engine,
                    target
                )
            )],
            EmptyRecapPriorContext.Instance,
            RestoreFixture.MaxContent
        );
        DerivedRecapSetManifest targetManifest =
            DerivedRecapCodec.CreateManifest(
                fixture.Engine.BranchRefId,
                target,
                RecapPlannerWireTestFacts.SetupsAt(
                    fixture.Engine,
                    target
                ),
                [targetPlan]
            );

        RecapFrozenPlanBarrierResult result =
            await RecapFrozenPlanBarrier.ProveAsync(
                fixture.Engine,
                fixture.Store,
                targetManifest,
                fixture.Engine.ReadLineagePrefixAt(
                    target,
                    RecapFrozenPlanBarrier.MaxHeaderCount
                ),
                target,
                RecapProtocolHardCaps.V4,
                CancellationToken.None
            );

        RecapFrozenPlanBarrierDefect defect =
            Assert.Single(result.Defects);
        Assert.Equal(
            RecapFrozenPlanBarrierDefectKind.FrozenAuthority,
            defect.Kind
        );
        Assert.Contains(
            "does not absorb through",
            defect.Detail,
            StringComparison.Ordinal
        );
    }



    [Fact]
    public async Task RestoreRejectsStructurallyValidWrongReplayStartSetupsBeforeMaintainer() {
        int componentReads = 0;
        int mutations = 0;
        using RestoreFixture fixture =
            await RestoreFixture.CreateAsync(
                hooks: new RecapStoreTestHooks(
                    BeforeRestoreComponentRead: () => componentReads++,
                    BeforeAtomicFileReplace: _ => mutations++
                )
            );
        MaintainRecapBlockPlan valid = fixture.CreateMaintainPlan(
            "frozen.self",
            "frozen-maintainer",
            endpointCount: 1
        );
        var plan = new MaintainRecapBlockPlan(
            valid.RecapBlockId,
            valid.Target,
            valid.MaintainerId,
            valid.MaintainerCapabilityFingerprint,
            new EmptyRecapMaintainSource(
                ((EmptyRecapMaintainSource)valid.Source)
                    .ReplayStartExclusive,
                RecapPlannerWireTestFacts.WrongSetups(
                    ((EmptyRecapMaintainSource)valid.Source)
                        .ReplayStartSetups
                )
            ),
            valid.CatchUpBoundaries,
            valid.PriorContext,
            valid.MaxContentUtf8Bytes
        );
        EventAddress anchor = plan.CatchUpBoundaries[^1].Address;
        _ = await fixture.PublishAsync(
            anchor,
            [plan],
            new Dictionary<RecapBlockId, string> {
                [plan.RecapBlockId] = "committed"
            }
        );
        await fixture.DamageFinalAsync(plan);
        File.Delete(
            fixture.BlockPath(anchor, "work", plan.RecapBlockId)
        );
        var maintainer = fixture.CreateMaintainer(
            plan,
            static (_, _) => "must-not-run"
        );
        componentReads = 0;
        mutations = 0;

        var unavailable =
            Assert.IsType<DerivedRecapRestoreResult.Unavailable>(
                await fixture.CreateExecutor([maintainer])
                    .RestoreAsync(anchor, fixture.CurrentHead)
            );

        Assert.Contains(
            unavailable.Defects,
            defect => defect.Detail.Contains(
                "conflicting frozen identity",
                StringComparison.Ordinal
            )
        );
        Assert.Equal(0, maintainer.CallCount);
        Assert.Equal(0, componentReads);
        Assert.Equal(0, mutations);
        PublishedBlockRestoreInspection inspection =
            (await fixture.InspectAsync(anchor))
                .Blocks[plan.RecapBlockId];
        Assert.IsType<RollingRecapCheckpointHealth.Missing>(
            inspection.Checkpoint
        );
        Assert.IsType<FinalRecapBlockHealth.Damaged>(inspection.Final);
    }

    [Fact]
    public async Task RestoreRejectsForgedAdmissionSetupHashBeforeComponents() {
        int componentReads = 0;
        int mutations = 0;
        using RestoreFixture fixture =
            await RestoreFixture.CreateAsync(
                hooks: new RecapStoreTestHooks(
                    BeforeRestoreComponentRead: () => componentReads++,
                    BeforeAtomicFileReplace: _ => mutations++
                )
            );
        MaintainRecapBlockPlan validPlan = fixture.CreateMaintainPlan(
            "frozen.self",
            "frozen-maintainer",
            endpointCount: 1
        );
        EventAddress anchor = validPlan.CatchUpBoundaries[^1].Address;
        SessionContextAnchorSetupReferences forgedAdmission =
            RecapPlannerWireTestFacts.WrongSetups(
                RecapPlannerWireTestFacts.SetupsAt(
                    fixture.Engine,
                    anchor
                )
            );
        var plan = new MaintainRecapBlockPlan(
            validPlan.RecapBlockId,
            validPlan.Target,
            validPlan.MaintainerId,
            validPlan.MaintainerCapabilityFingerprint,
            validPlan.Source,
            [new RecapReplayBoundary(anchor, forgedAdmission)],
            validPlan.PriorContext,
            validPlan.MaxContentUtf8Bytes
        );
        _ = await fixture.PublishAsync(
            anchor,
            [plan],
            new Dictionary<RecapBlockId, string> {
                [plan.RecapBlockId] = "committed"
            },
            admissionSetups: forgedAdmission
        );
        var maintainer = fixture.CreateMaintainer(
            plan,
            static (_, _) => "must-not-run"
        );
        componentReads = 0;
        mutations = 0;

        var unavailable = Assert.IsType<
            DerivedRecapRestoreResult.Unavailable
        >(await fixture.CreateExecutor([maintainer])
            .RestoreAsync(anchor, fixture.CurrentHead));

        Assert.Contains(
            unavailable.Defects,
            defect => defect.Code
                    == DerivedRecapRestoreDefectCodes.FrozenPlanInvalid
                && defect.Detail.Contains(
                    "conflicting frozen identity",
                    StringComparison.Ordinal
                )
        );
        Assert.Equal(0, componentReads);
        Assert.Equal(0, mutations);
        Assert.Equal(0, maintainer.CallCount);
    }

    [Fact]
    public async Task MissingExistingInputResumeSuffixRejectsStaleBoundarySetupAuthority() {
        int componentReads = 0;
        int mutations = 0;
        using RestoreFixture fixture =
            await RestoreFixture.CreateAsync(
                hooks: new RecapStoreTestHooks(
                    BeforeRestoreComponentRead: () => componentReads++,
                    BeforeAtomicFileReplace: _ => mutations++
                )
            );
        (
            MaintainRecapBlockPlan plan,
            EventAddress target
        ) = await fixture.PublishExistingTwoStepAsync(
            useStaleMidBoundarySetup: true
        );
        await fixture.DamageFinalAsync(plan, target);
        DerivedRecapBlock checkpoint =
            DerivedRecapCodec.CreateBlock(
                plan,
                plan.CatchUpBoundaries[0].Address,
                "checkpoint"
            );
        string checkpointPath = fixture.BlockPath(
            target,
            "work",
            plan.RecapBlockId
        );
        await File.WriteAllBytesAsync(
            checkpointPath,
            DerivedRecapCodec.EncodeBlock(checkpoint)
        );
        File.Delete(
            fixture.BlockPath(target, "inputs", plan.RecapBlockId)
        );
        byte[] checkpointBefore =
            await File.ReadAllBytesAsync(checkpointPath);
        var suffix = Assert.IsType<
            PublishedBlockRestoreCapability.ResumeSuffix
        >(
            (await fixture.InspectAsync(target))
                .Blocks[plan.RecapBlockId].Capability
        );
        Assert.Equal(1, suffix.NextEndpointIndex);
        var maintainer = fixture.CreateMaintainer(
            plan,
            static (_, _) => "must-not-run"
        );
        componentReads = 0;
        mutations = 0;

        var unavailable =
            Assert.IsType<DerivedRecapRestoreResult.Unavailable>(
                await fixture.CreateExecutor([maintainer])
                    .RestoreAsync(target, fixture.CurrentHead)
            );

        Assert.Equal(0, maintainer.CallCount);
        Assert.Equal(0, componentReads);
        Assert.Equal(0, mutations);
        Assert.Contains(
            unavailable.Defects,
            defect => defect.Detail.Contains(
                "does not match the frozen endpoint setup addresses",
                StringComparison.Ordinal
            )
        );
        Assert.Equal(
            checkpointBefore,
            await File.ReadAllBytesAsync(checkpointPath)
        );
        Assert.IsType<FinalRecapBlockHealth.Damaged>(
            (await fixture.InspectAsync(target))
                .Blocks[plan.RecapBlockId].Final
        );
    }

    [Fact]
    public async Task MissingExistingInputResumeSuffixUsesValidatedBoundarySetupAuthority() {
        using RestoreFixture fixture =
            await RestoreFixture.CreateAsync();
        (
            MaintainRecapBlockPlan plan,
            EventAddress target
        ) = await fixture.PublishExistingTwoStepAsync(
            useStaleMidBoundarySetup: false
        );
        byte[] canonicalManifestBefore =
            await File.ReadAllBytesAsync(fixture.ManifestPath(target));
        byte[] canonicalPublicationBefore =
            await File.ReadAllBytesAsync(fixture.PublicationPath(target));
        await fixture.DamageFinalAsync(plan, target);
        DerivedRecapBlock checkpoint =
            DerivedRecapCodec.CreateBlock(
                plan,
                plan.CatchUpBoundaries[0].Address,
                "checkpoint"
            );
        await File.WriteAllBytesAsync(
            fixture.BlockPath(target, "work", plan.RecapBlockId),
            DerivedRecapCodec.EncodeBlock(checkpoint)
        );
        File.Delete(
            fixture.BlockPath(target, "inputs", plan.RecapBlockId)
        );
        var suffix = Assert.IsType<
            PublishedBlockRestoreCapability.ResumeSuffix
        >(
            (await fixture.InspectAsync(target))
                .Blocks[plan.RecapBlockId].Capability
        );
        Assert.Equal(1, suffix.NextEndpointIndex);
        var maintainer = fixture.CreateMaintainer(
            plan,
            static (_, _) => "target-content"
        );

        DerivedRecapRestoreResult result =
            await fixture.CreateExecutor([maintainer])
                .RestoreAsync(target, fixture.CurrentHead);

        _ = Assert.IsType<DerivedRecapRestoreResult.Restored>(result);
        Assert.Equal(1, maintainer.CallCount);
        Assert.Equal(["checkpoint"], maintainer.OldBlocks);
        Assert.Equal(
            "target-content",
            await fixture.MaterializedTextAsync(target)
        );
        Assert.Equal(
            canonicalManifestBefore,
            await File.ReadAllBytesAsync(fixture.ManifestPath(target))
        );
        Assert.Equal(
            canonicalPublicationBefore,
            await File.ReadAllBytesAsync(fixture.PublicationPath(target))
        );
    }

    [Fact]
    public async Task FrozenPlanRawValidatorReportsOffLineageAdmissionOnce() {
        using RestoreFixture fixture =
            await RestoreFixture.CreateAsync();
        MaintainRecapBlockPlan valid = fixture.CreateMaintainPlan(
            "frozen.self",
            "frozen-maintainer",
            endpointCount: 1
        );
        RecapReplayBoundary validAdmission =
            valid.CatchUpBoundaries[^1];
        EventAddress offLineage = validAdmission.Address with {
            SegmentNumber = validAdmission.Address.SegmentNumber
                == uint.MaxValue
                    ? uint.MaxValue - 1
                    : uint.MaxValue
        };
        var plan = new MaintainRecapBlockPlan(
            valid.RecapBlockId,
            valid.Target,
            valid.MaintainerId,
            valid.MaintainerCapabilityFingerprint,
            valid.Source,
            [new RecapReplayBoundary(offLineage, validAdmission.Setups)],
            valid.PriorContext,
            valid.MaxContentUtf8Bytes
        );
        DerivedRecapSetManifest manifest =
            DerivedRecapCodec.CreateManifest(
                fixture.Engine.BranchRefId,
                offLineage,
                validAdmission.Setups,
                [plan]
            );

        IReadOnlyList<RecapFrozenPlanRawDefect> defects =
            RecapFrozenPlanRawValidator.ValidateBlock(
                fixture.Engine,
                manifest,
                new Dictionary<
                    RecapBlockId,
                    DerivedRecapFrozenInput
                >(),
                fixture.Lineage,
                plan
            );

        RecapFrozenPlanRawDefect defect = Assert.Single(defects);
        Assert.Equal(
            "SetAdmissionAnchor is outside current raw lineage.",
            defect.Detail
        );
    }

    [Fact]
    public async Task HealthyPublishedRestoreHasNoMaintainerCalls() {
        using RestoreFixture fixture =
            await RestoreFixture.CreateAsync();
        (
            MaintainRecapBlockPlan plan,
            PublishedRecapDescriptor descriptor
        ) = await fixture.PublishMaintainAsync(endpointCount: 1);
        var maintainer = fixture.CreateMaintainer(plan);

        DerivedRecapRestoreResult result =
            await fixture.CreateExecutor([maintainer])
                .RestoreAsync(
                    plan.CatchUpBoundaries[^1].Address,
                    fixture.CurrentHead
                );

        var restored =
            Assert.IsType<DerivedRecapRestoreResult.Restored>(
                result
            );
        Assert.Equal(descriptor, restored.Descriptor);
        Assert.Equal(0, maintainer.CallCount);
    }

    [Fact]
    public async Task ManifestWitnessRebuildsByteIdenticalEnvelope() {
        using RestoreFixture fixture =
            await RestoreFixture.CreateAsync();
        (
            MaintainRecapBlockPlan plan,
            PublishedRecapDescriptor originalDescriptor
        ) = await fixture.PublishMaintainAsync(endpointCount: 1);
        EventAddress anchor = plan.CatchUpBoundaries[^1].Address;
        byte[] originalEnvelope =
            await File.ReadAllBytesAsync(
                fixture.PublicationPath(anchor)
            );
        File.Delete(fixture.PublicationPath(anchor));
        PublishedRestoreInspection before =
            await fixture.InspectAsync(anchor);
        Assert.Equal(
            PublishedRestoreAuthorityKind.ManifestWitness,
            before.Handle.AuthorityKind
        );
        Assert.IsType<
            PublishedBlockRestoreCapability.AdoptPending
        >(before.Blocks[plan.RecapBlockId].Capability);
        var maintainer = fixture.CreateMaintainer(plan);

        DerivedRecapRestoreResult result =
            await fixture.CreateExecutor([maintainer])
                .RestoreAsync(anchor, fixture.CurrentHead);

        Assert.True(
            result is DerivedRecapRestoreResult.Restored,
            result is DerivedRecapRestoreResult.Unavailable unavailable
                ? string.Join(
                    " | ",
                    unavailable.Defects.Select(static defect =>
                        $"{defect.Code}: {defect.Detail}"
                    )
                )
                : result.ToString()
        );
        var restored = (DerivedRecapRestoreResult.Restored)result;
        Assert.Equal(originalDescriptor, restored.Descriptor);
        Assert.Equal(
            originalDescriptor.EnvelopeSha256,
            restored.Descriptor.EnvelopeSha256
        );
        Assert.Equal(
            originalEnvelope,
            await File.ReadAllBytesAsync(
                fixture.PublicationPath(anchor)
            )
        );
        Assert.Equal(0, maintainer.CallCount);
        var selected =
            Assert.IsType<DerivedRecapSelection.Selected>(
                await fixture.Store.SelectNthPreviousAsync(
                    fixture.LineageView,
                    0
                )
            );
        Assert.Equal(originalDescriptor, selected.Descriptor);
        DerivedRecapMaterialization materialized =
            await fixture.Store.MaterializeAsync(
                selected.Descriptor
            );
        Assert.Equal(
            "committed",
            Assert.Single(materialized.Contributions).ExactText
        );
    }

    [Fact]
    public async Task FinalCheckpointInstallsWithoutMaintainerCall() {
        using RestoreFixture fixture =
            await RestoreFixture.CreateAsync();
        (
            MaintainRecapBlockPlan plan,
            _
        ) = await fixture.PublishMaintainAsync(endpointCount: 1);
        EventAddress anchor = plan.CatchUpBoundaries[^1].Address;
        await File.WriteAllTextAsync(
            fixture.BlockPath(anchor, "blocks", plan.RecapBlockId),
            "damaged"
        );
        Assert.IsType<
            PublishedBlockRestoreCapability.InstallFinalCheckpoint
        >(
            (await fixture.InspectAsync(anchor))
                .Blocks[plan.RecapBlockId].Capability
        );
        var maintainer = fixture.CreateMaintainer(plan);

        DerivedRecapRestoreResult result =
            await fixture.CreateExecutor([maintainer])
                .RestoreAsync(anchor, fixture.CurrentHead);

        _ = Assert.IsType<DerivedRecapRestoreResult.Restored>(
            result
        );
        Assert.Equal(0, maintainer.CallCount);
        Assert.Equal(
            "committed",
            await fixture.MaterializedTextAsync(anchor)
        );
    }

    [Fact]
    public async Task EarlierCheckpointRunsOnlyPendingSuffix() {
        using RestoreFixture fixture =
            await RestoreFixture.CreateAsync();
        (
            MaintainRecapBlockPlan plan,
            _
        ) = await fixture.PublishMaintainAsync(endpointCount: 2);
        EventAddress anchor = plan.CatchUpBoundaries[^1].Address;
        await fixture.DamageFinalAsync(plan);
        DerivedRecapBlock checkpoint =
            DerivedRecapCodec.CreateBlock(
                plan,
                plan.CatchUpBoundaries[0].Address,
                "checkpoint"
            );
        await File.WriteAllBytesAsync(
            fixture.BlockPath(anchor, "work", plan.RecapBlockId),
            DerivedRecapCodec.EncodeBlock(checkpoint)
        );
        Assert.IsType<
            PublishedBlockRestoreCapability.ResumeSuffix
        >(
            (await fixture.InspectAsync(anchor))
                .Blocks[plan.RecapBlockId].Capability
        );
        var maintainer = fixture.CreateMaintainer(
            plan,
            static (_, request) => request.OldBlock.Text + "+suffix"
        );

        DerivedRecapRestoreResult result =
            await fixture.CreateExecutor([maintainer])
                .RestoreAsync(anchor, fixture.CurrentHead);

        _ = Assert.IsType<DerivedRecapRestoreResult.Restored>(
            result
        );
        Assert.Equal(1, maintainer.CallCount);
        Assert.Equal(["checkpoint"], maintainer.OldBlocks);
        Assert.Equal(
            "checkpoint+suffix",
            await fixture.MaterializedTextAsync(anchor)
        );
    }

    [Fact]
    public async Task MissingCheckpointReplaysEntireEmptySourceRoute() {
        using RestoreFixture fixture =
            await RestoreFixture.CreateAsync();
        (
            MaintainRecapBlockPlan plan,
            _
        ) = await fixture.PublishMaintainAsync(endpointCount: 2);
        EventAddress anchor = plan.CatchUpBoundaries[^1].Address;
        await fixture.DamageFinalAsync(plan);
        File.Delete(
            fixture.BlockPath(anchor, "work", plan.RecapBlockId)
        );
        Assert.IsType<PublishedBlockRestoreCapability.ReplayBlock>(
            (await fixture.InspectAsync(anchor))
                .Blocks[plan.RecapBlockId].Capability
        );
        var maintainer = fixture.CreateMaintainer(
            plan,
            static (call, request) =>
                request.OldBlock.Text + $"step-{call}"
        );

        DerivedRecapRestoreResult result =
            await fixture.CreateExecutor([maintainer])
                .RestoreAsync(anchor, fixture.CurrentHead);

        _ = Assert.IsType<DerivedRecapRestoreResult.Restored>(
            result
        );
        Assert.Equal(2, maintainer.CallCount);
        Assert.Equal(["", "step-1"], maintainer.OldBlocks);
        Assert.Equal(
            "step-1step-2",
            await fixture.MaterializedTextAsync(anchor)
        );
    }

    [Fact]
    public async Task InheritRestoreCopiesFrozenInputWithoutMaintainer() {
        using RestoreFixture fixture =
            await RestoreFixture.CreateAsync();
        (
            InheritRecapBlockPlan plan,
            EventAddress target
        ) = await fixture.PublishInheritAsync();
        await fixture.DamageFinalAsync(plan, target);

        DerivedRecapRestoreResult result =
            await fixture.CreateExecutor([])
                .RestoreAsync(target, fixture.CurrentHead);

        _ = Assert.IsType<DerivedRecapRestoreResult.Restored>(
            result
        );
        Assert.Equal(
            "source-content",
            await fixture.MaterializedTextAsync(target)
        );
    }

    [Fact]
    public async Task FrozenRosterRestoresWithoutActivePlanningInputs() {
        using RestoreFixture fixture =
            await RestoreFixture.CreateAsync();
        (
            MaintainRecapBlockPlan plan,
            _
        ) = await fixture.PublishMaintainAsync(endpointCount: 1);
        await fixture.DamageFinalAsync(plan);
        File.Delete(
            fixture.BlockPath(
                plan.CatchUpBoundaries[^1].Address,
                "work",
                plan.RecapBlockId
            )
        );
        var maintainer = fixture.CreateMaintainer(plan);
        DerivedRecapRestoreResult result =
            await fixture.CreateExecutor([maintainer])
                .RestoreAsync(
                    plan.CatchUpBoundaries[^1].Address,
                    fixture.CurrentHead
                );

        _ = Assert.IsType<DerivedRecapRestoreResult.Restored>(
            result
        );
        Assert.Equal(1, maintainer.CallCount);
    }

    [Fact]
    public async Task SuffixRestorePreservesFrozenPlanCanonicalBytes() {
        using RestoreFixture fixture =
            await RestoreFixture.CreateAsync();
        SessionHistoryPlanningWindow raw =
            fixture.Engine.ReadHistoryPlanningWindow();
        EventAddress[] route = raw.ReplaySafeBoundaries
            .Select(static boundary => boundary.Address)
            .TakeLast(2)
            .ToArray();
        EventAddress anchor = route[^1];
        var priorSnapshot = new ContextHeaderSnapshot(
            "frozen-system",
            "frozen-observation",
            "frozen-action"
        );
        var plan = new MaintainRecapBlockPlan(
            new RecapBlockId("frozen.rich"),
            new ContextHeaderBlockPath(
                ContextHeaderCarrier.System,
                "frozen.rich"
            ),
            "frozen-rich-maintainer",
            RecapPlannerTestIdentity.CapabilityFingerprint,
            new EmptyRecapMaintainSource(
                raw.StartExclusive,
                raw.StartSetups
            ),
            RecapPlannerWireTestFacts.Boundaries(raw, route),
            new InlineRecapPriorContext(
                raw.StartExclusive,
                priorSnapshot
            ),
            RestoreFixture.MaxContent - 123
        );
        PublishedRecapDescriptor originalDescriptor =
            await fixture.PublishAsync(
                anchor,
                [plan],
                new Dictionary<RecapBlockId, string> {
                    [plan.RecapBlockId] = "committed"
                }
            );
        PublishedRecapSet originalPublication =
            DerivedRecapCodec.DecodePublication(
                await File.ReadAllBytesAsync(
                    fixture.PublicationPath(anchor)
                )
            );
        byte[] originalFrozenPlan =
            DerivedRecapCodec.EncodeManifest(
                originalPublication.FrozenPlanSnapshot
            );
        await fixture.DamageFinalAsync(plan);
        DerivedRecapBlock checkpoint =
            DerivedRecapCodec.CreateBlock(
                plan,
                route[0],
                "checkpoint"
            );
        await File.WriteAllBytesAsync(
            fixture.BlockPath(
                anchor,
                "work",
                plan.RecapBlockId
            ),
            DerivedRecapCodec.EncodeBlock(checkpoint)
        );
        var maintainer = fixture.CreateMaintainer(
            plan,
            static (_, request) => request.OldBlock.Text + "+suffix"
        );
        var restored =
            Assert.IsType<DerivedRecapRestoreResult.Restored>(
                await fixture.CreateExecutor([maintainer])
                    .RestoreAsync(anchor, fixture.CurrentHead)
            );

        Assert.Equal(1, maintainer.CallCount);
        Assert.NotEqual(originalDescriptor, restored.Descriptor);
        PublishedRecapSet restoredPublication =
            DerivedRecapCodec.DecodePublication(
                await File.ReadAllBytesAsync(
                    fixture.PublicationPath(anchor)
                )
            );
        Assert.Equal(
            originalFrozenPlan,
            DerivedRecapCodec.EncodeManifest(
                restoredPublication.FrozenPlanSnapshot
            )
        );
        MaintainRecapBlockPlan restoredPlan =
            Assert.IsType<MaintainRecapBlockPlan>(
                Assert.Single(
                    restoredPublication.FrozenPlanSnapshot.Blocks
                )
            );
        Assert.IsType<EmptyRecapMaintainSource>(
            restoredPlan.Source
        );
        var restoredPrior =
            Assert.IsType<InlineRecapPriorContext>(
                restoredPlan.PriorContext
            );
        Assert.Equal(priorSnapshot, restoredPrior.Snapshot);
        Assert.Equal(
            "checkpoint+suffix",
            await fixture.MaterializedTextAsync(anchor)
        );
    }

    [Fact]
    public async Task MissingNeededMaintainerFailsGlobalPreflight() {
        using RestoreFixture fixture =
            await RestoreFixture.CreateAsync();
        MaintainRecapBlockPlan first = fixture.CreateMaintainPlan(
            "first",
            "first-maintainer",
            endpointCount: 1
        );
        MaintainRecapBlockPlan second = fixture.CreateMaintainPlan(
            "second",
            "second-maintainer",
            endpointCount: 1
        );
        EventAddress anchor = fixture.CurrentHead;
        _ = await fixture.PublishAsync(
            anchor,
            [first, second],
            new Dictionary<RecapBlockId, string> {
                [first.RecapBlockId] = "first-old",
                [second.RecapBlockId] = "second-old"
            }
        );
        foreach (MaintainRecapBlockPlan plan in new[] { first, second }) {
            await fixture.DamageFinalAsync(plan);
            File.Delete(
                fixture.BlockPath(
                    anchor,
                    "work",
                    plan.RecapBlockId
                )
            );
        }
        var available = fixture.CreateMaintainer(first);

        var unavailable =
            Assert.IsType<DerivedRecapRestoreResult.Unavailable>(
                await fixture.CreateExecutor([available])
                    .RestoreAsync(anchor, fixture.CurrentHead)
            );

        Assert.Contains(
            unavailable.Defects,
            static defect =>
                defect.Code
                    == DerivedRecapRestoreDefectCodes
                        .MaintainerUnavailable
        );
        Assert.Equal(0, available.CallCount);
    }

    [Fact]
    public async Task FingerprintDriftPreventsRestoreCall() {
        using RestoreFixture fixture =
            await RestoreFixture.CreateAsync();
        (
            MaintainRecapBlockPlan plan,
            _
        ) = await fixture.PublishMaintainAsync(endpointCount: 1);
        EventAddress anchor = plan.CatchUpBoundaries[^1].Address;
        await fixture.DamageFinalAsync(plan);
        File.Delete(
            fixture.BlockPath(anchor, "work", plan.RecapBlockId)
        );
        var drifted = new ScriptedMaintainer(
            plan.MaintainerId,
            plan.Target,
            static (_, _) => "must-not-run",
            beforeReturn: null,
            capabilityFingerprint:
                "sha256:ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"
        );

        var unavailable = Assert.IsType<
            DerivedRecapRestoreResult.Unavailable
        >(
            await fixture.CreateExecutor([drifted])
                .RestoreAsync(anchor, fixture.CurrentHead)
        );

        Assert.Contains(
            unavailable.Defects,
            defect => defect.Code
                == DerivedRecapRestoreDefectCodes
                    .MaintainerUnavailable
        );
        Assert.Equal(0, drifted.CallCount);
    }

    [Fact]
    public async Task MissingExistingSourceInputIsUnavailableWithoutCall() {
        using RestoreFixture fixture =
            await RestoreFixture.CreateAsync();
        (
            MaintainRecapBlockPlan plan,
            EventAddress target
        ) = await fixture.PublishExistingAsync();
        await fixture.DamageFinalAsync(plan, target);
        File.Delete(
            fixture.BlockPath(target, "work", plan.RecapBlockId)
        );
        File.Delete(
            fixture.BlockPath(target, "inputs", plan.RecapBlockId)
        );
        var maintainer = fixture.CreateMaintainer(plan);

        var unavailable =
            Assert.IsType<DerivedRecapRestoreResult.Unavailable>(
                await fixture.CreateExecutor([maintainer])
                    .RestoreAsync(target, fixture.CurrentHead)
            );

        Assert.Contains(
            unavailable.Defects,
            static defect =>
                defect.Code == "RestoreDependencyMissing"
        );
        Assert.Equal(0, maintainer.CallCount);
    }

    [Theory]
    [InlineData(1, 4000)]
    [InlineData(1000, 1)]
    public async Task RestoreRawLimitsFailBeforeFirstMaintainer(
        int maxRawEventsPerStep,
        int maxRawEventsPerBuild
    ) {
        int componentReadCount = 0;
        int mutationCount = 0;
        using RestoreFixture fixture =
            await RestoreFixture.CreateAsync(
                hooks: new RecapStoreTestHooks(
                    BeforeRestoreComponentRead: () =>
                        componentReadCount++,
                    BeforeAtomicFileReplace: _ => mutationCount++
                )
            );
        (
            MaintainRecapBlockPlan plan,
            _
        ) = await fixture.PublishMaintainAsync(endpointCount: 1);
        EventAddress anchor = plan.CatchUpBoundaries[^1].Address;
        await fixture.DamageFinalAsync(plan);
        File.Delete(
            fixture.BlockPath(anchor, "work", plan.RecapBlockId)
        );
        var maintainer = fixture.CreateMaintainer(plan);
        RecapProtocolHardCaps limited = fixture.CreateHardCaps(
            maxRawEventsPerStep: maxRawEventsPerStep,
            maxRawEventsPerBuild: maxRawEventsPerBuild
        );
        componentReadCount = 0;
        mutationCount = 0;

        DerivedRecapRestoreResult result =
            await fixture.CreateExecutor([maintainer], limited)
                .RestoreAsync(anchor, fixture.CurrentHead);
        if (maxRawEventsPerStep == 1) {
            var beyond = Assert.IsType<
                DerivedRecapRestoreResult.BeyondPrefix
            >(result);
            Assert.Equal(
                DerivedRecapBeyondPrefixStage.RestorePendingWindow,
                beyond.Stage
            );
        }
        else {
            var unavailable = Assert.IsType<
                DerivedRecapRestoreResult.Unavailable
            >(result);
            Assert.Contains(
                unavailable.Defects,
                static defect => defect.Code
                    == DerivedRecapRestoreDefectCodes
                        .ExecutionLimitExceeded
            );
        }
        Assert.Equal(0, componentReadCount);
        Assert.Equal(0, mutationCount);
        Assert.Equal(0, maintainer.CallCount);
    }

    [Fact]
    public async Task PlanSwitchAfterMetadataPreflightIsRetryableBeforeComponents() {
        int componentReadCount = 0;
        int mutationCount = 0;
        bool injectConcurrentPublication = false;
        string publicationPath = string.Empty;
        string finalPath = string.Empty;
        byte[] publicationBytes = [];
        byte[] finalBytes = [];
        using RestoreFixture fixture =
            await RestoreFixture.CreateAsync(
                historyPairs: 259,
                hooks: new RecapStoreTestHooks(
                    BeforeRestorePublicationRead: () => {
                        if (!injectConcurrentPublication) {
                            return;
                        }
                        injectConcurrentPublication = false;
                        File.WriteAllBytes(
                            publicationPath,
                            publicationBytes
                        );
                        File.WriteAllBytes(finalPath, finalBytes);
                    },
                    BeforeRestoreComponentRead: () =>
                        componentReadCount++,
                    BeforeAtomicFileReplace: _ => mutationCount++
                )
            );
        MaintainRecapBlockPlan template = fixture.CreateMaintainPlan(
            "frozen.self",
            "frozen-maintainer",
            endpointCount: 1
        );
        _ = fixture.Engine.AppendRuntimeConfigSetup(
            new SessionRuntimeConfiguration(
                "model-plan-switch",
                "surface-plan-switch",
                SessionJournalDefaults.Schema,
                new(0)
            )
        );
        _ = fixture.Engine.AppendSystemPromptSetup(
            "system-plan-switch"
        );
        EventAddress recentStart = fixture.AppendPair(
            "plan-switch-start"
        );
        EventAddress anchor = fixture.AppendPair(
            "plan-switch-anchor"
        );
        var original = new MaintainRecapBlockPlan(
            template.RecapBlockId,
            template.Target,
            template.MaintainerId,
            template.MaintainerCapabilityFingerprint,
            new EmptyRecapMaintainSource(
                recentStart,
                RecapPlannerWireTestFacts.SetupsAt(
                    fixture.Engine,
                    recentStart
                )
            ),
            [new RecapReplayBoundary(
                anchor,
                RecapPlannerWireTestFacts.SetupsAt(
                    fixture.Engine,
                    anchor
                )
            )],
            template.PriorContext,
            template.MaxContentUtf8Bytes
        );
        _ = await fixture.PublishAsync(
            anchor,
            [original],
            new Dictionary<RecapBlockId, string> {
                [original.RecapBlockId] = "committed"
            }
        );
        EventAddress beyond = fixture.Engine
            .ReadCurrentLineageHeaders().HeadToRoot[
                fixture.Lineage.MaxHeaderCount
            ].Address;
        var mutated = new MaintainRecapBlockPlan(
            original.RecapBlockId,
            original.Target,
            original.MaintainerId,
            original.MaintainerCapabilityFingerprint,
            new EmptyRecapMaintainSource(
                beyond,
                ((EmptyRecapMaintainSource)original.Source)
                    .ReplayStartSetups
            ),
            original.CatchUpBoundaries,
            original.PriorContext,
            original.MaxContentUtf8Bytes
        );
        PublishedPlanSnapshot originalPlan = Assert.IsType<
            PublishedPlanAtAnchorReadResult.Available
        >(
            await fixture.Store.ReadPublishedPlanAtAnchorAsync(anchor)
        ).Snapshot;
        DerivedRecapSetManifest mutatedManifest =
            DerivedRecapCodec.CreateManifest(
                fixture.Engine.BranchRefId,
                anchor,
                originalPlan.FrozenPlan.SetAdmissionAnchorSetups,
                [mutated]
            );
        DerivedRecapBlock mutatedFinal =
            DerivedRecapCodec.CreateBlock(
                mutated,
                anchor,
                "concurrent final"
            );
        publicationBytes = DerivedRecapCodec.EncodePublication(
            DerivedRecapCodec.CreatePublication(
                mutatedManifest,
                [mutatedFinal]
            )
        );
        finalBytes = DerivedRecapCodec.EncodeBlock(mutatedFinal);
        publicationPath = fixture.PublicationPath(anchor);
        finalPath = fixture.BlockPath(
            anchor,
            "blocks",
            original.RecapBlockId
        );
        var maintainer = fixture.CreateMaintainer(original);
        componentReadCount = 0;
        mutationCount = 0;
        injectConcurrentPublication = true;

        DerivedRecapRestoreResult result =
            await fixture.CreateExecutor([maintainer])
                .RestoreAsync(anchor, fixture.CurrentHead);
        Assert.True(
            result is DerivedRecapRestoreResult.Retryable,
            result is DerivedRecapRestoreResult.BeyondPrefix observed
                ? $"Unexpected Beyond stage={observed.Stage}, "
                    + $"required={observed.Evidence.RequiredAnchor}, "
                    + $"next={observed.Evidence.NextAddress}."
                : $"Unexpected result {result.GetType().Name}."
        );
        var retryable = (DerivedRecapRestoreResult.Retryable)result;

        Assert.Equal(
            DerivedRecapRestoreDefectCodes.ConcurrentPublishedChange,
            retryable.Code
        );
        Assert.Equal(0, mutationCount);
        Assert.Equal(0, maintainer.CallCount);
        Assert.Equal(0, componentReadCount);
    }

    [Fact]
    public async Task RestoreCallLimitFailsBeforeFirstMaintainer() {
        using RestoreFixture fixture =
            await RestoreFixture.CreateAsync();
        (
            MaintainRecapBlockPlan plan,
            _
        ) = await fixture.PublishMaintainAsync(endpointCount: 2);
        EventAddress anchor = plan.CatchUpBoundaries[^1].Address;
        await fixture.DamageFinalAsync(plan);
        File.Delete(
            fixture.BlockPath(anchor, "work", plan.RecapBlockId)
        );
        var maintainer = fixture.CreateMaintainer(plan);
        RecapProtocolHardCaps limited = fixture.CreateHardCaps(
            maxMaintainerCalls: 1
        );

        var unavailable =
            Assert.IsType<DerivedRecapRestoreResult.Unavailable>(
                await fixture.CreateExecutor([maintainer], limited)
                    .RestoreAsync(anchor, fixture.CurrentHead)
            );

        Assert.Contains(
            unavailable.Defects,
            static defect =>
                defect.Code
                    == DerivedRecapRestoreDefectCodes
                        .ExecutionLimitExceeded
        );
        Assert.Equal(0, maintainer.CallCount);
    }

    [Fact]
    public async Task MaintainerExceptionReturnsTypedBlockFailure() {
        using RestoreFixture fixture =
            await RestoreFixture.CreateAsync();
        (
            MaintainRecapBlockPlan plan,
            _
        ) = await fixture.PublishMaintainAsync(endpointCount: 1);
        EventAddress anchor = plan.CatchUpBoundaries[^1].Address;
        await fixture.DamageFinalAsync(plan);
        File.Delete(
            fixture.BlockPath(anchor, "work", plan.RecapBlockId)
        );
        var maintainer = fixture.CreateMaintainer(
            plan,
            static (_, _) => throw new InvalidOperationException(
                "injected Maintainer failure"
            )
        );

        var failed =
            Assert.IsType<DerivedRecapRestoreResult.BlockFailed>(
                await fixture.CreateExecutor([maintainer])
                    .RestoreAsync(anchor, fixture.CurrentHead)
            );

        Assert.Equal(
            DerivedRecapRestoreDefectCodes.MaintainerFailed,
            failed.Code
        );
    }

    [Fact]
    public async Task InvalidMaintainerResultReturnsTypedBlockFailure() {
        using RestoreFixture fixture =
            await RestoreFixture.CreateAsync();
        (
            MaintainRecapBlockPlan plan,
            _
        ) = await fixture.PublishMaintainAsync(endpointCount: 1);
        EventAddress anchor = plan.CatchUpBoundaries[^1].Address;
        await fixture.DamageFinalAsync(plan);
        File.Delete(
            fixture.BlockPath(anchor, "work", plan.RecapBlockId)
        );
        var maintainer = new InvalidResultMaintainer(
            plan.MaintainerId,
            plan.Target
        );

        var failed =
            Assert.IsType<DerivedRecapRestoreResult.BlockFailed>(
                await fixture.CreateExecutor([maintainer])
                    .RestoreAsync(anchor, fixture.CurrentHead)
            );

        Assert.Equal(
            DerivedRecapRestoreDefectCodes.MaintainerResultInvalid,
            failed.Code
        );
    }

    [Fact]
    public async Task ComponentStaleReturnsRetryableWithoutLoop() {
        using RestoreFixture fixture =
            await RestoreFixture.CreateAsync();
        (
            MaintainRecapBlockPlan plan,
            _
        ) = await fixture.PublishMaintainAsync(endpointCount: 1);
        EventAddress anchor = plan.CatchUpBoundaries[^1].Address;
        await fixture.DamageFinalAsync(plan);
        string workPath =
            fixture.BlockPath(anchor, "work", plan.RecapBlockId);
        File.Delete(workPath);
        var maintainer = fixture.CreateMaintainer(
            plan,
            static (_, _) => "candidate",
            beforeReturn: () => File.WriteAllText(
                workPath,
                "concurrent damage"
            )
        );

        var retryable =
            Assert.IsType<DerivedRecapRestoreResult.Retryable>(
                await fixture.CreateExecutor([maintainer])
                    .RestoreAsync(anchor, fixture.CurrentHead)
            );

        Assert.Equal(
            DerivedRecapRestoreDefectCodes.ConcurrentPublishedChange,
            retryable.Code
        );
        Assert.Equal(1, maintainer.CallCount);
    }

    [Fact]
    public async Task MiddleAnchorRestoreDoesNotRequireLatest() {
        using RestoreFixture fixture =
            await RestoreFixture.CreateAsync(historyPairs: 3);
        (
            MaintainRecapBlockPlan older,
            _
        ) = await fixture.PublishMaintainAsync(endpointCount: 1);
        EventAddress olderAnchor = older.CatchUpBoundaries[^1].Address;
        _ = fixture.AppendPair("newer");
        (
            MaintainRecapBlockPlan newer,
            _
        ) = await fixture.PublishMaintainAsync(endpointCount: 1);
        Assert.NotEqual(
            olderAnchor,
            newer.CatchUpBoundaries[^1].Address
        );
        await fixture.DamageFinalAsync(older, olderAnchor);
        File.Delete(
            fixture.BlockPath(
                olderAnchor,
                "work",
                older.RecapBlockId
            )
        );
        var maintainer = fixture.CreateMaintainer(older);

        DerivedRecapRestoreResult result =
            await fixture.CreateExecutor([maintainer])
                .RestoreAsync(olderAnchor, fixture.CurrentHead);

        _ = Assert.IsType<DerivedRecapRestoreResult.Restored>(
            result
        );
        Assert.Equal(1, maintainer.CallCount);
    }

    [Fact]
    public async Task RawHeadRaceLeavesReusablePendingReplacement() {
        using RestoreFixture fixture =
            await RestoreFixture.CreateAsync();
        (
            MaintainRecapBlockPlan plan,
            _
        ) = await fixture.PublishMaintainAsync(endpointCount: 1);
        EventAddress anchor = plan.CatchUpBoundaries[^1].Address;
        EventAddress expectedHead = fixture.CurrentHead;
        await fixture.DamageFinalAsync(plan);
        File.Delete(
            fixture.BlockPath(anchor, "work", plan.RecapBlockId)
        );
        var maintainer = fixture.CreateMaintainer(
            plan,
            static (_, _) => "pending-after-race",
            beforeReturn: () => fixture.AppendPair("race")
        );
        DerivedRecapRestoreExecutor executor =
            fixture.CreateExecutor([maintainer]);

        var retryable =
            Assert.IsType<DerivedRecapRestoreResult.Retryable>(
                await executor.RestoreAsync(anchor, expectedHead)
            );
        Assert.Equal(
            DerivedRecapRestoreDefectCodes.RawHeadChanged,
            retryable.Code
        );
        Assert.Equal(1, maintainer.CallCount);

        _ = Assert.IsType<DerivedRecapRestoreResult.Restored>(
            await executor.RestoreAsync(anchor, fixture.CurrentHead)
        );
        Assert.Equal(1, maintainer.CallCount);
        Assert.Equal(
            "pending-after-race",
            await fixture.MaterializedTextAsync(anchor)
        );
    }

    private sealed class ScriptedMaintainer : IRecapBlockMaintainer {
        private readonly Func<
            int,
            RecapBlockMaintenanceRequest,
            string
        > _maintain;
        private readonly Action? _beforeReturn;

        public ScriptedMaintainer(
            string id,
            ContextHeaderBlockPath target,
            Func<int, RecapBlockMaintenanceRequest, string> maintain,
            Action? beforeReturn,
            string capabilityFingerprint =
                RecapPlannerTestIdentity.CapabilityFingerprint
        ) {
            Id = id;
            Target = target;
            CapabilityFingerprint = capabilityFingerprint;
            _maintain = maintain;
            _beforeReturn = beforeReturn;
        }

        public string Id { get; }
        public string CapabilityFingerprint { get; }
        public ContextHeaderBlockPath Target { get; }
        public int CallCount { get; private set; }
        public List<string> OldBlocks { get; } = [];

        public ValueTask<RecapBlockMaintenanceResult> MaintainAsync(
            RecapBlockMaintenanceRequest request,
            CancellationToken ct
        ) {
            ct.ThrowIfCancellationRequested();
            CallCount++;
            OldBlocks.Add(request.OldBlock.Text);
            string content = _maintain(CallCount, request);
            _beforeReturn?.Invoke();
            return ValueTask.FromResult(
                new RecapBlockMaintenanceResult(
                    Id,
                    Target,
                    new ContextHeaderBlock(content)
                )
            );
        }
    }

    private sealed class InvalidResultMaintainer(
        string id,
        ContextHeaderBlockPath target
    ) : IRecapBlockMaintainer {
        public string Id { get; } = id;
        public string CapabilityFingerprint { get; } =
            "sha256:0000000000000000000000000000000000000000000000000000000000000000";
        public ContextHeaderBlockPath Target { get; } = target;

        public ValueTask<RecapBlockMaintenanceResult> MaintainAsync(
            RecapBlockMaintenanceRequest request,
            CancellationToken ct
        ) => ValueTask.FromResult(
            new RecapBlockMaintenanceResult(
                Id + "-wrong",
                Target,
                new ContextHeaderBlock("invalid")
            )
        );
    }

    private sealed class RestoreFixture : IDisposable {
        public const int MaxContent = 4096;

        private RestoreFixture(
            string path,
            SessionJournalEngine engine,
            DerivedRecapStore store
        ) {
            Path = path;
            Engine = engine;
            Store = store;
            Publisher = new DerivedRecapPublisher(store, engine);
        }

        public string Path { get; }
        public SessionJournalEngine Engine { get; }
        public DerivedRecapStore Store { get; }
        public DerivedRecapPublisher Publisher { get; }
        public EventAddress CurrentHead =>
            Engine.ReadCurrentHead()!.Value;
        public SessionCurrentLineagePrefix Lineage =>
            Engine.ReadCurrentLineagePrefix(513);
        public DerivedRecapLineageView LineageView =>
            DerivedRecapLineageView.Capture(Store, Engine);

        public static async ValueTask<RestoreFixture> CreateAsync(
            int historyPairs = 5,
            RecapStoreTestHooks? hooks = null
        ) {
            string path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "atelia-derived-recap-restore-tests",
                Guid.NewGuid().ToString("N")
            );
            SessionJournalEngine engine = SessionJournalEngine.Create(
                path,
                new SessionCreateOptions(
                    "model-a",
                    "system-a",
                    "surface-a"
                )
            );
            DerivedRecapStore store = hooks is null
                ? DerivedRecapStore.Open(
                    path,
                    engine.BranchRefId
                )
                : DerivedRecapStore.OpenForTest(
                    path,
                    engine.BranchRefId,
                    hooks
                );
            await store.CreateAsync();
            var fixture = new RestoreFixture(path, engine, store);
            for (int index = 0; index < historyPairs; index++) {
                fixture.AppendPair(index.ToString());
            }
            return fixture;
        }

        public EventAddress AppendPair(string suffix) {
            Engine.AppendObservation($"observation {suffix}");
            return Engine.AppendImportedAgentAction(
                new ActionMessage([
                    new ActionBlock.Text($"answer {suffix}")
                ]),
                new CompletionDescriptor(
                    "import",
                    "v1",
                    "model-a"
                )
            );
        }

        public MaintainRecapBlockPlan CreateMaintainPlan(
            string blockId,
            string maintainerId,
            int endpointCount
        ) {
            SessionHistoryPlanningWindow window =
                Engine.ReadHistoryPlanningWindow();
            EventAddress[] boundaries = window.ReplaySafeBoundaries
                .Select(static boundary => boundary.Address)
                .ToArray();
            return new MaintainRecapBlockPlan(
                new RecapBlockId(blockId),
                new ContextHeaderBlockPath(
                    ContextHeaderCarrier.System,
                    blockId
                ),
                maintainerId,
                RecapPlannerTestIdentity.CapabilityFingerprint,
                new EmptyRecapMaintainSource(
                    window.StartExclusive,
                    window.StartSetups
                ),
                RecapPlannerWireTestFacts.Boundaries(
                    window,
                    boundaries[^endpointCount..]
                ),
                EmptyRecapPriorContext.Instance,
                MaxContent
            );
        }

        public async ValueTask<(
            MaintainRecapBlockPlan Plan,
            PublishedRecapDescriptor Descriptor
        )> PublishMaintainAsync(int endpointCount) {
            MaintainRecapBlockPlan plan = CreateMaintainPlan(
                "frozen.self",
                "frozen-maintainer",
                endpointCount
            );
            PublishedRecapDescriptor descriptor =
                await PublishAsync(
                    CurrentHead,
                    [plan],
                    new Dictionary<RecapBlockId, string> {
                        [plan.RecapBlockId] = "committed"
                    }
                );
            return (plan, descriptor);
        }

        public async ValueTask<(
            InheritRecapBlockPlan Plan,
            EventAddress Target
        )> PublishInheritAsync() {
            SessionCurrentLineagePrefix lineage = Lineage;
            EventAddress source = lineage.HeadToOldest[4].Address;
            SessionHistoryPlanningWindow window =
                Engine.ReadHistoryPlanningWindowAt(source);
            var sourcePlan = new MaintainRecapBlockPlan(
                new RecapBlockId("frozen.self"),
                new ContextHeaderBlockPath(
                    ContextHeaderCarrier.System,
                    "frozen.self"
                ),
                "frozen-maintainer",
                RecapPlannerTestIdentity.CapabilityFingerprint,
                new EmptyRecapMaintainSource(
                    window.StartExclusive,
                    window.StartSetups
                ),
                [RecapPlannerWireTestFacts.Boundary(window, source)],
                EmptyRecapPriorContext.Instance,
                MaxContent
            );
            PublishedRecapDescriptor sourceDescriptor =
                await PublishAsync(
                    source,
                    [sourcePlan],
                    new Dictionary<RecapBlockId, string> {
                        [sourcePlan.RecapBlockId] = "source-content"
                    }
                );
            DerivedRecapFrozenInput expectedInput =
                DerivedRecapCodec.CreateFrozenInput(
                    sourcePlan.RecapBlockId,
                    sourcePlan.Target,
                    source,
                    RecapPlannerWireTestFacts.SetupsAt(
                        window,
                        source
                    ),
                    "source-content"
                );
            var inherit = new InheritRecapBlockPlan(
                sourcePlan.RecapBlockId,
                sourcePlan.Target,
                source,
                expectedInput.AbsorbedThroughSetups,
                sourceDescriptor.EnvelopeSha256,
                expectedInput.PayloadSha256,
                MaxContent
            );
            EventAddress target = CurrentHead;
            _ = await PublishAsync(
                target,
                [inherit],
                new Dictionary<RecapBlockId, string> {
                    [inherit.RecapBlockId] = "source-content"
                },
                new Dictionary<RecapBlockId, EventAddress> {
                    [inherit.RecapBlockId] = source
                }
            );
            return (inherit, target);
        }

        public async ValueTask<(
            MaintainRecapBlockPlan Plan,
            EventAddress Target
        )> PublishExistingAsync() {
            SessionCurrentLineagePrefix lineage = Lineage;
            EventAddress source = lineage.HeadToOldest[4].Address;
            SessionHistoryPlanningWindow sourceWindow =
                Engine.ReadHistoryPlanningWindowAt(source);
            var sourcePlan = new MaintainRecapBlockPlan(
                new RecapBlockId("frozen.self"),
                new ContextHeaderBlockPath(
                    ContextHeaderCarrier.System,
                    "frozen.self"
                ),
                "frozen-maintainer",
                RecapPlannerTestIdentity.CapabilityFingerprint,
                new EmptyRecapMaintainSource(
                    sourceWindow.StartExclusive,
                    sourceWindow.StartSetups
                ),
                [
                    RecapPlannerWireTestFacts.Boundary(
                        sourceWindow,
                        source
                    )
                ],
                EmptyRecapPriorContext.Instance,
                MaxContent
            );
            PublishedRecapDescriptor sourceDescriptor =
                await PublishAsync(
                    source,
                    [sourcePlan],
                    new Dictionary<RecapBlockId, string> {
                        [sourcePlan.RecapBlockId] = "source-content"
                    }
                );
            DerivedRecapFrozenInput expectedInput =
                DerivedRecapCodec.CreateFrozenInput(
                    sourcePlan.RecapBlockId,
                    sourcePlan.Target,
                    source,
                    RecapPlannerWireTestFacts.SetupsAt(
                        sourceWindow,
                        source
                    ),
                    "source-content"
                );
            EventAddress target = CurrentHead;
            var existing = new MaintainRecapBlockPlan(
                sourcePlan.RecapBlockId,
                sourcePlan.Target,
                sourcePlan.MaintainerId,
                sourcePlan.MaintainerCapabilityFingerprint,
                new ExistingRecapMaintainSource(
                    source,
                    expectedInput.AbsorbedThroughSetups,
                    sourceDescriptor.EnvelopeSha256,
                    expectedInput.PayloadSha256
                ),
                [
                    new RecapReplayBoundary(
                        target,
                        RecapPlannerWireTestFacts.SetupsAt(
                            Engine,
                            target
                        )
                    )
                ],
                EmptyRecapPriorContext.Instance,
                MaxContent
            );
            _ = await PublishAsync(
                target,
                [existing],
                new Dictionary<RecapBlockId, string> {
                    [existing.RecapBlockId] = "target-content"
                }
            );
            return (existing, target);
        }

        public async ValueTask<(
            MaintainRecapBlockPlan Plan,
            EventAddress Target
        )> PublishExistingTwoStepAsync(
            bool useStaleMidBoundarySetup
        ) {
            EventAddress source = CurrentHead;
            SessionHistoryPlanningWindow sourceWindow =
                Engine.ReadHistoryPlanningWindowAt(source);
            SessionContextAnchorSetupReferences sourceSetups =
                RecapPlannerWireTestFacts.SetupsAt(Engine, source);
            var sourcePlan = new MaintainRecapBlockPlan(
                new RecapBlockId("frozen.self"),
                new ContextHeaderBlockPath(
                    ContextHeaderCarrier.System,
                    "frozen.self"
                ),
                "frozen-maintainer",
                RecapPlannerTestIdentity.CapabilityFingerprint,
                new EmptyRecapMaintainSource(
                    sourceWindow.StartExclusive,
                    sourceWindow.StartSetups
                ),
                [new RecapReplayBoundary(source, sourceSetups)],
                EmptyRecapPriorContext.Instance,
                MaxContent
            );
            PublishedRecapDescriptor sourceDescriptor =
                await PublishAsync(
                    source,
                    [sourcePlan],
                    new Dictionary<RecapBlockId, string> {
                        [sourcePlan.RecapBlockId] = "source-content"
                    }
                );
            DerivedRecapFrozenInput expectedInput =
                DerivedRecapCodec.CreateFrozenInput(
                    sourcePlan.RecapBlockId,
                    sourcePlan.Target,
                    source,
                    sourceSetups,
                    "source-content"
                );

            Engine.AppendRuntimeConfigSetup(
                new SessionRuntimeConfiguration(
                    "model-b",
                    "surface-b",
                    SessionJournalDefaults.Schema,
                    new(0)
                )
            );
            Engine.AppendSystemPromptSetup("system-b");
            EventAddress mid = AppendPair("existing-mid");
            EventAddress target = AppendPair("existing-target");
            SessionContextAnchorSetupReferences midSetups =
                useStaleMidBoundarySetup
                    ? sourceSetups
                    : RecapPlannerWireTestFacts.SetupsAt(Engine, mid);
            SessionContextAnchorSetupReferences targetSetups =
                RecapPlannerWireTestFacts.SetupsAt(Engine, target);
            var existing = new MaintainRecapBlockPlan(
                sourcePlan.RecapBlockId,
                sourcePlan.Target,
                sourcePlan.MaintainerId,
                sourcePlan.MaintainerCapabilityFingerprint,
                new ExistingRecapMaintainSource(
                    source,
                    expectedInput.AbsorbedThroughSetups,
                    sourceDescriptor.EnvelopeSha256,
                    expectedInput.PayloadSha256
                ),
                [
                    new RecapReplayBoundary(mid, midSetups),
                    new RecapReplayBoundary(target, targetSetups)
                ],
                EmptyRecapPriorContext.Instance,
                MaxContent
            );
            _ = await PublishAsync(
                target,
                [existing],
                new Dictionary<RecapBlockId, string> {
                    [existing.RecapBlockId] = "target-content"
                },
                admissionSetups: targetSetups
            );
            return (existing, target);
        }

        public async ValueTask<PublishedRecapDescriptor> PublishAsync(
            EventAddress anchor,
            IReadOnlyList<RecapBlockPlan> plans,
            IReadOnlyDictionary<RecapBlockId, string> contents,
            IReadOnlyDictionary<RecapBlockId, EventAddress>? cursors =
                null,
            SessionContextAnchorSetupReferences? admissionSetups = null
        ) {
            DerivedRecapSetManifest manifest =
                DerivedRecapCodec.CreateManifest(
                    Engine.BranchRefId,
                    anchor,
                    admissionSetups
                        ?? RecapPlannerWireTestFacts.SetupsAt(
                            Engine,
                            anchor
                        ),
                    plans
                );
            _ = await Store.CreateBuildingAsync(manifest);
            var available = Assert.IsType<
                BuildingReadResult.Available
            >(await Store.ReadBuildingAsync(anchor));
            foreach (RecapBlockPlan plan in plans) {
                EventAddress cursor = cursors is not null
                    && cursors.TryGetValue(
                        plan.RecapBlockId,
                        out EventAddress exactCursor
                    )
                        ? exactCursor
                        : anchor;
                DerivedRecapBlock final =
                    DerivedRecapCodec.CreateBlock(
                        plan,
                        cursor,
                        contents[plan.RecapBlockId]
                    );
                BuildingBlockInspection inspection =
                    await Store.InspectBuildingBlockAsync(
                        available.Snapshot.Descriptor,
                        plan.RecapBlockId
                    );
                if (plan is MaintainRecapBlockPlan maintain) {
                    for (int index = 0;
                         index < maintain.CatchUpBoundaries.Count;
                         index++) {
                        DerivedRecapBlock checkpoint =
                            index
                                == maintain.CatchUpBoundaries.Count - 1
                                ? final
                                : DerivedRecapCodec.CreateBlock(
                                    maintain,
                                    maintain.CatchUpBoundaries[index]
                                        .Address,
                                    contents[plan.RecapBlockId]
                                );
                        _ = Assert.IsType<
                            CheckpointWriteResult.Updated
                        >(
                            await Store.AdvanceRollingCheckpointAsync(
                                available.Snapshot.Descriptor,
                                plan.RecapBlockId,
                                inspection.Checkpoint.StateToken,
                                checkpoint
                            )
                        );
                        inspection =
                            await Store.InspectBuildingBlockAsync(
                                available.Snapshot.Descriptor,
                                plan.RecapBlockId
                            );
                    }
                }
                FinalBlockWriteResult write =
                    await Store.EnsureFinalBlockAsync(
                        available.Snapshot.Descriptor,
                        plan.RecapBlockId,
                        inspection.Final.StateToken,
                        final
                    );
                Assert.True(
                    write is FinalBlockWriteResult.Installed
                        or FinalBlockWriteResult.ReplacedDamaged
                        or FinalBlockWriteResult.AlreadyHealthy
                );
            }
            return Assert.IsType<PublishRecapResult.Published>(
                await Publisher.PublishAsync(anchor)
            ).Descriptor;
        }

        public ScriptedMaintainer CreateMaintainer(
            MaintainRecapBlockPlan plan,
            Func<
                int,
                RecapBlockMaintenanceRequest,
                string
            >? maintain = null,
            Action? beforeReturn = null
        ) => new(
            plan.MaintainerId,
            plan.Target,
            maintain ?? (static (_, _) => "restored"),
            beforeReturn
        );

        public DerivedRecapRestoreExecutor CreateExecutor(
            IReadOnlyList<IRecapBlockMaintainer> maintainers,
            RecapProtocolHardCaps? hardCaps = null
        ) => new(
            Engine,
            Store,
            new RecapBlockMaintainerRegistry(maintainers),
            hardCaps ?? CreateHardCaps()
        );

        public RecapProtocolHardCaps CreateHardCaps(
            int maxMaintainerCalls = 16,
            int maxRawEventsPerStep = 1000,
            int maxRawEventsPerBuild = 4000
        ) => new(
            maxRawGrowthEventCount: 1000,
            maxRouteEndpointsPerBlock: 8,
            maxMaintainerCallsPerBuild:
                maxMaintainerCalls,
            maxRawEventsPerStep,
            maxRawEventsPerBuild,
            maxContentUtf8Bytes:
                SessionContextContributionContract
                    .MaxContributionUtf8Bytes,
            maxCatalogEntries:
                SessionContextContributionContract.MaxContributionCount
        );

        public async ValueTask<PublishedRestoreInspection>
            InspectAsync(EventAddress anchor)
            => Assert.IsType<
                PublishedRestoreInspectionResult.Available
            >(
                await LineageView
                    .InspectPublishedForOfflineDiagnosticsAsync(anchor)
            ).Inspection;

        public async ValueTask DamageFinalAsync(
            RecapBlockPlan plan,
            EventAddress? anchor = null
        ) => await File.WriteAllTextAsync(
            BlockPath(
                anchor ?? (
                    plan is MaintainRecapBlockPlan maintain
                        ? maintain.CatchUpBoundaries[^1].Address
                        : throw new ArgumentNullException(nameof(anchor))
                ),
                "blocks",
                plan.RecapBlockId
            ),
            "damaged"
        );

        public async ValueTask<string> MaterializedTextAsync(
            EventAddress anchor
        ) {
            var selected =
                Assert.IsType<DerivedRecapSelection.Selected>(
                    await Store.SelectNthPreviousAsync(LineageView, 0)
                );
            if (selected.Descriptor.SetAdmissionAnchor != anchor) {
                var inspection = await InspectAsync(anchor);
                var committed =
                    Assert.IsType<
                        PublishedEnvelopeCommitResult.AlreadyCommitted
                    >(
                        await new DerivedRecapRestorer(Store, Engine)
                            .CommitEnvelopeAsync(
                                Store.IssuePublishedEnvelopeCommitAuthority(
                                    inspection.Handle,
                                    inspection.Blocks.Values
                                        .Select(static block =>
                                            block.WriteAuthority
                                        )
                                        .ToArray()
                                ),
                                CurrentHead
                            )
                    );
                selected = new DerivedRecapSelection.Selected(
                    committed.Descriptor
                );
            }
            DerivedRecapMaterialization materialized =
                await Store.MaterializeAsync(selected.Descriptor);
            return Assert.Single(
                materialized.Contributions
            ).ExactText;
        }

        public string PublicationPath(EventAddress anchor)
            => System.IO.Path.Combine(
                Store.GetPublishedPathForTest(anchor),
                "publication.json"
            );

        public string ManifestPath(EventAddress anchor)
            => System.IO.Path.Combine(
                Store.GetPublishedPathForTest(anchor),
                "manifest.json"
            );

        public string BlockPath(
            EventAddress anchor,
            string directory,
            RecapBlockId blockId
        ) => System.IO.Path.Combine(
            Store.GetPublishedPathForTest(anchor),
            directory,
            $"{blockId.Value}.json"
        );

        public void Dispose() {
            Engine.Dispose();
            try {
                if (Directory.Exists(Path)) {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch {
            }
        }
    }
}
