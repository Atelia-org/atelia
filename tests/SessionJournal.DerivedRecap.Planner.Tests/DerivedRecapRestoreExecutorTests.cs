using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.SessionJournal.DerivedRecap.Store;
using Xunit;

namespace Atelia.SessionJournal.DerivedRecap.Planner.Tests;

public sealed class DerivedRecapRestoreExecutorTests {
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
                    plan.CatchUpThrough[^1],
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
        EventAddress anchor = plan.CatchUpThrough[^1];
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

        var restored =
            Assert.IsType<DerivedRecapRestoreResult.Restored>(
                result
            );
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
                    fixture.Lineage,
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
        EventAddress anchor = plan.CatchUpThrough[^1];
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
        EventAddress anchor = plan.CatchUpThrough[^1];
        await fixture.DamageFinalAsync(plan);
        DerivedRecapBlock checkpoint =
            DerivedRecapCodec.CreateBlock(
                plan,
                plan.CatchUpThrough[0],
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
        EventAddress anchor = plan.CatchUpThrough[^1];
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
                plan.CatchUpThrough[^1],
                "work",
                plan.RecapBlockId
            )
        );
        var maintainer = fixture.CreateMaintainer(plan);
        DerivedRecapRestoreResult result =
            await fixture.CreateExecutor([maintainer])
                .RestoreAsync(
                    plan.CatchUpThrough[^1],
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
            new EmptyRecapMaintainSource(raw.StartExclusive),
            route,
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
        EventAddress anchor = plan.CatchUpThrough[^1];
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
        using RestoreFixture fixture =
            await RestoreFixture.CreateAsync();
        (
            MaintainRecapBlockPlan plan,
            _
        ) = await fixture.PublishMaintainAsync(endpointCount: 1);
        EventAddress anchor = plan.CatchUpThrough[^1];
        await fixture.DamageFinalAsync(plan);
        File.Delete(
            fixture.BlockPath(anchor, "work", plan.RecapBlockId)
        );
        var maintainer = fixture.CreateMaintainer(plan);
        RecapProtocolHardCaps limited = fixture.CreateHardCaps(
            maxRawEventsPerStep: maxRawEventsPerStep,
            maxRawEventsPerBuild: maxRawEventsPerBuild
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
    public async Task RestoreCallLimitFailsBeforeFirstMaintainer() {
        using RestoreFixture fixture =
            await RestoreFixture.CreateAsync();
        (
            MaintainRecapBlockPlan plan,
            _
        ) = await fixture.PublishMaintainAsync(endpointCount: 2);
        EventAddress anchor = plan.CatchUpThrough[^1];
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
        EventAddress anchor = plan.CatchUpThrough[^1];
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
        EventAddress anchor = plan.CatchUpThrough[^1];
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
        EventAddress anchor = plan.CatchUpThrough[^1];
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
        EventAddress olderAnchor = older.CatchUpThrough[^1];
        _ = fixture.AppendPair("newer");
        (
            MaintainRecapBlockPlan newer,
            _
        ) = await fixture.PublishMaintainAsync(endpointCount: 1);
        Assert.NotEqual(
            olderAnchor,
            newer.CatchUpThrough[^1]
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
        EventAddress anchor = plan.CatchUpThrough[^1];
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
        public SessionCurrentLineageSnapshot Lineage =>
            Engine.ReadCurrentLineageHeaders();

        public static async ValueTask<RestoreFixture> CreateAsync(
            int historyPairs = 5
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
            var store = DerivedRecapStore.Open(
                path,
                engine.BranchRefId
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
                    window.StartExclusive
                ),
                boundaries[^endpointCount..],
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
            SessionCurrentLineageSnapshot lineage = Lineage;
            EventAddress source = lineage.HeadToRoot[4].Address;
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
                    window.StartExclusive
                ),
                [source],
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
                    "source-content"
                );
            var inherit = new InheritRecapBlockPlan(
                sourcePlan.RecapBlockId,
                sourcePlan.Target,
                source,
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
            SessionCurrentLineageSnapshot lineage = Lineage;
            EventAddress source = lineage.HeadToRoot[4].Address;
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
                    sourceWindow.StartExclusive
                ),
                [source],
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
                    sourceDescriptor.EnvelopeSha256,
                    expectedInput.PayloadSha256
                ),
                [target],
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

        public async ValueTask<PublishedRecapDescriptor> PublishAsync(
            EventAddress anchor,
            IReadOnlyList<RecapBlockPlan> plans,
            IReadOnlyDictionary<RecapBlockId, string> contents,
            IReadOnlyDictionary<RecapBlockId, EventAddress>? cursors =
                null
        ) {
            DerivedRecapSetManifest manifest =
                DerivedRecapCodec.CreateManifest(
                    Engine.BranchRefId,
                    anchor,
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
                         index < maintain.CatchUpThrough.Count;
                         index++) {
                        DerivedRecapBlock checkpoint =
                            index
                                == maintain.CatchUpThrough.Count - 1
                                ? final
                                : DerivedRecapCodec.CreateBlock(
                                    maintain,
                                    maintain.CatchUpThrough[index],
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
            return await Publisher.PublishAsync(anchor);
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
                await Store.InspectPublishedForRestoreAsync(
                    anchor,
                    Lineage
                )
            ).Inspection;

        public async ValueTask DamageFinalAsync(
            RecapBlockPlan plan,
            EventAddress? anchor = null
        ) => await File.WriteAllTextAsync(
            BlockPath(
                anchor ?? (
                    plan is MaintainRecapBlockPlan maintain
                        ? maintain.CatchUpThrough[^1]
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
                    await Store.SelectNthPreviousAsync(Lineage, 0)
                );
            if (selected.Descriptor.SetAdmissionAnchor != anchor) {
                var inspection = await InspectAsync(anchor);
                var committed =
                    Assert.IsType<
                        PublishedEnvelopeCommitResult.AlreadyCommitted
                    >(
                        await new DerivedRecapRestorer(Store, Engine)
                            .CommitEnvelopeAsync(
                                inspection.Handle,
                                inspection.Blocks.ToDictionary(
                                    static item => item.Key,
                                    static item =>
                                        item.Value.Final.StateToken
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
