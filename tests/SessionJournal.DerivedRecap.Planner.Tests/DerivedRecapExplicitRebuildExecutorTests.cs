using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.SessionJournal.DerivedRecap.Abstractions;
using Atelia.SessionJournal.DerivedRecap.Store;
using Xunit;

namespace Atelia.SessionJournal.DerivedRecap.Planner.Tests;

public sealed class DerivedRecapExplicitRebuildExecutorTests
    : IDisposable {
    private readonly List<string> _paths = [];

    [Fact]
    public async Task HealthySelfHashedEpochWithWrongRawCommitmentIsRejected() {
        string path = NewPath();
        RefId refId;
        EventAddress admission;
        using (var writer = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions(
                "model-a",
                "system-a",
                "surface-a"
            )
        )) {
            refId = writer.BranchRefId;
            _ = writer.AppendObservation("A");
            admission = writer.AppendImportedAgentAction(
                new ActionMessage([new ActionBlock.Text("B")]),
                new CompletionDescriptor("import", "v1", "model-a")
            );
        }
        DerivedRecapEpochStore store = DerivedRecapEpochStore.Open(
            path,
            refId
        );
        await store.CreateAsync();
        using var offline = SessionJournalEngine.OpenReadOnly(path);
        SessionHistoryPlanningSeed start = Assert.IsType<
            SessionCreatedPlanningSeedReadResult.Available
        >(offline.ReadView.ReadSessionCreatedPlanningSeedAtBounded(
            admission,
            16
        )).Seed;
        SessionHistoryPlanningWindow window = Assert.IsType<
            SessionHistoryPlanningWindowReadResult.Available
        >(offline.ReadView.ReadHistoryPlanningWindowAtBounded(
            admission,
            start,
            16
        )).Window;
        DerivedRecapEpochInput forged = DerivedRecapV8Codec
            .CreateEpochInput(
                new RecapEpochBoundary(start.Address, start.Setups),
                new RecapEpochBoundary(admission, window.EndSetups),
                window.RawAddresses.Count,
                new string('f', 64),
                Array.AsReadOnly([
                    .. window.Units.Select(static unit => unit.Message)
                ]),
                RecapEpochPrevious.Empty.Instance
            );
        DerivedRecapEpochManifest manifest = DerivedRecapV8Codec
            .CreateManifest(
                refId,
                admission,
                forged.PayloadSha256,
                Definitions()
            );
        _ = Assert.IsType<InstallRecapEpochBuildingResult.Installed>(
            await store.InstallBuildingAsync(manifest, forged)
        );
        RecapEpochStoreSnapshot building = Assert.IsType<
            RecapEpochStoreReadResult.Available
        >(await store.ReadBuildingAsync(admission)).Snapshot;
        foreach (RecapEpochBlockInspection block in building.Blocks) {
            Assert.IsType<WriteRecapEpochFinalResult.Installed>(
                await store.WriteFinalAsync(
                    block.WriteAuthority!,
                    DerivedRecapV8Codec.CreateFinalBlock(
                        manifest,
                        block.Definition,
                        $"forged-{block.Definition.RecapBlockId.Value}"
                    )
                )
            );
        }
        Assert.IsType<PublishRecapEpochResult.Published>(
            await store.PublishBuildingAsync(building.Descriptor)
        );

        DerivedRecapRebuildSpoolStore spool =
            DerivedRecapRebuildSpoolStore.Open(path, refId);
        string campaignId = Guid.NewGuid().ToString("N");
        await DerivedRecapFullRebuildAuthorityPreparer.BeginAsync(
            offline,
            spool,
            campaignId,
            DerivedRecapRebuildSpoolLimits.Default
        );
        _ = await DerivedRecapFullRebuildAuthorityPreparer.ResumeAsync(
            offline,
            spool,
            campaignId
        );
        RecapEpochBlockDefinition[] definitions = Definitions();
        var self = new RecordingMaintainer(definitions[0]);
        var world = new RecordingMaintainer(definitions[1]);
        DerivedRecapEpochCampaignExecutor executor = Executor(
            offline,
            store,
            [self, world]
        );

        Assert.IsType<DerivedRecapEpochOperationResult.Unavailable>(
            await executor.RunExplicitRebuildAsync(
                offline,
                spool,
                campaignId
            )
        );
        Assert.Empty(self.Inputs);
        Assert.Empty(world.Inputs);
    }

    [Fact]
    public async Task FrozenBuildingPublishesBeforeActiveConfigIsLoaded() {
        string path = NewPath();
        RefId refId;
        EventAddress admission;
        using (var writer = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model-a", "system-a", "surface-a")
        )) {
            refId = writer.BranchRefId;
            _ = writer.AppendObservation("A");
            admission = writer.AppendImportedAgentAction(
                new ActionMessage([new ActionBlock.Text("B")]),
                new CompletionDescriptor("import", "v1", "model-a")
            );
        }
        DerivedRecapEpochStore store = DerivedRecapEpochStore.Open(
            path,
            refId
        );
        await store.CreateAsync();
        using var offline = SessionJournalEngine.OpenReadOnly(path);
        SessionHistoryPlanningSeed start = Assert.IsType<
            SessionCreatedPlanningSeedReadResult.Available
        >(offline.ReadView.ReadSessionCreatedPlanningSeedAtBounded(
            admission,
            16
        )).Seed;
        SessionHistoryPlanningWindow window = Assert.IsType<
            SessionHistoryPlanningWindowReadResult.Available
        >(offline.ReadView.ReadHistoryPlanningWindowAtBounded(
            admission,
            start,
            16
        )).Window;
        DerivedRecapEpochInput input = DerivedRecapV8Codec
            .CreateEpochInput(
                new RecapEpochBoundary(start.Address, start.Setups),
                new RecapEpochBoundary(admission, window.EndSetups),
                window.RawAddresses.Count,
                window.RawRangeSha256,
                Array.AsReadOnly([
                    .. window.Units.Select(static unit => unit.Message)
                ]),
                RecapEpochPrevious.Empty.Instance
            );
        RecapEpochBlockDefinition[] definitions = Definitions();
        DerivedRecapEpochManifest manifest = DerivedRecapV8Codec
            .CreateManifest(
                refId,
                admission,
                input.PayloadSha256,
                definitions
            );
        _ = await store.InstallBuildingAsync(manifest, input);
        RecapEpochStoreSnapshot building = Assert.IsType<
            RecapEpochStoreReadResult.Available
        >(await store.ReadBuildingAsync(admission)).Snapshot;
        Assert.IsType<WriteRecapEpochFinalResult.Installed>(
            await store.WriteFinalAsync(
                building.Blocks[0].WriteAuthority!,
                DerivedRecapV8Codec.CreateFinalBlock(
                    manifest,
                    definitions[0],
                    "already healthy"
                )
            )
        );
        DerivedRecapRebuildSpoolStore spool =
            DerivedRecapRebuildSpoolStore.Open(path, refId);
        string campaign = Guid.NewGuid().ToString("N");
        await DerivedRecapFullRebuildAuthorityPreparer.BeginAsync(
            offline,
            spool,
            campaign,
            DerivedRecapRebuildSpoolLimits.Default
        );
        _ = await DerivedRecapFullRebuildAuthorityPreparer.ResumeAsync(
            offline,
            spool,
            campaign
        );
        var self = new RecordingMaintainer(definitions[0]);
        var world = new RecordingMaintainer(definitions[1]);
        int activeLoads = 0;
        var executor = new DerivedRecapEpochCampaignExecutor(
            offline.ReadView,
            store,
            (Func<RecapEpochActiveConfiguration>)(() => {
                activeLoads++;
                throw new IOException("active config unavailable");
            }),
            new RecapEpochOperationLimits(64, 64, 2, 2, 4, 2, 128),
            new RecapBlockMaintainerRegistry([self, world])
        );

        Assert.IsType<DerivedRecapEpochOperationResult.Unavailable>(
            await executor.RunExplicitRebuildAsync(
                offline,
                spool,
                campaign
            )
        );
        Assert.Equal(1, activeLoads);
        Assert.Empty(self.Inputs);
        Assert.Single(world.Inputs);
        Assert.IsType<RecapEpochBuildingSelectionResult.Empty>(
            await store.SelectBuildingAsync()
        );
        Assert.IsType<RecapEpochStoreReadResult.Available>(
            await store.ReadPublishedForRepairAsync(admission)
        );
    }

    [Fact]
    public async Task OverCapRawRequiresExplicitSpoolAndResumesContiguousEpochs() {
        string path = NewPath();
        RefId refId;
        EventAddress rawHead;
        using (var writer = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions(
                "model-a",
                "system-a",
                "surface-a"
            )
        )) {
            refId = writer.BranchRefId;
            for (int index = 0; index < 256; index++) {
                _ = writer.AppendObservation($"observation-{index}");
                rawHead = writer.AppendImportedAgentAction(
                    new ActionMessage([
                        new ActionBlock.Text($"action-{index}")
                    ]),
                    new CompletionDescriptor(
                        "import",
                        "v1",
                        "model-a"
                    )
                );
            }
            rawHead = writer.ReadView.ReadCurrentHead()!.Value;
        }

        DerivedRecapEpochStore store = DerivedRecapEpochStore.Open(
            path,
            refId
        );
        await store.CreateAsync();
        RecapEpochBlockDefinition[] definitions = Definitions();
        var self = new RecordingMaintainer(definitions[0]);
        var world = new RecordingMaintainer(definitions[1]);
        using (var onlineEngine = SessionJournalEngine.OpenReadOnly(path)) {
            DerivedRecapEpochCampaignExecutor online = Executor(
                onlineEngine,
                store,
                [self, world]
            );
            var required = Assert.IsType<
                DerivedRecapEpochOperationResult.FullRebuildRequired
            >(await online.RunOnlineAsync());
            Assert.Equal(rawHead, required.CapturedRawHead);
            Assert.Empty(self.Inputs);
            Assert.Empty(world.Inputs);
            Assert.False(Directory.Exists(Path.Combine(
                path,
                "derived",
                "recap",
                "rebuild"
            )));
        }

        DerivedRecapRebuildSpoolStore spool =
            DerivedRecapRebuildSpoolStore.Open(path, refId);
        string campaignId = Guid.NewGuid().ToString("N");
        using (var auditEngine = SessionJournalEngine.OpenReadOnly(path)) {
            await DerivedRecapFullRebuildAuthorityPreparer.BeginAsync(
                auditEngine,
                spool,
                campaignId,
                new DerivedRecapRebuildSpoolLimits(
                    PageEventCount: 47,
                    MaximumPageBytes: 512 * 1024,
                    MaximumEventCount: 10_000,
                    MaximumTotalEncodedBytes: 16 * 1024 * 1024
                )
            );
            _ = await DerivedRecapFullRebuildAuthorityPreparer
                .ResumeAsync(auditEngine, spool, campaignId);
        }

        DerivedRecapEpochOperationResult result;
        int operations = 0;
        bool deletedPreviousSource = false;
        do {
            using var rebuildEngine =
                SessionJournalEngine.OpenReadOnly(path);
            DerivedRecapEpochCampaignExecutor executor = Executor(
                rebuildEngine,
                store,
                [self, world]
            );
            result = operations == 0
                ? await executor.ResetAndRunExplicitRebuildAsync(
                    rebuildEngine,
                    spool,
                    campaignId
                )
                : await executor.RunExplicitRebuildAsync(
                    rebuildEngine,
                    spool,
                    campaignId
                );
            operations++;
            Assert.True(operations < 10);
            if (operations == 1) {
                var pending = Assert.IsType<
                    DerivedRecapEpochOperationResult.MoreWorkPending
                >(result);
                EventAddress previousSource = Assert.Single(
                    await store.ListPublishedAnchorsAsync(),
                    anchor => anchor != pending.Latest.AdmissionAnchor
                );
                Directory.Delete(
                    PublishedPath(path, refId, previousSource),
                    recursive: true
                );
                deletedPreviousSource = true;
            }
        } while (result
                 is DerivedRecapEpochOperationResult.MoreWorkPending);

        var fresh = Assert.IsType<
            DerivedRecapEpochOperationResult.Fresh
        >(result);
        Assert.Equal(rawHead, fresh.Latest!.AdmissionAnchor);
        Assert.Equal(8, self.Inputs.Count);
        Assert.Equal(8, world.Inputs.Count);
        Assert.Equal(4, operations);
        for (int index = 0; index < self.Inputs.Count; index++) {
            Assert.Same(self.Inputs[index], world.Inputs[index]);
            Assert.Equal(64, self.Inputs[index].HistoryMessages.Count);
            if (index == 0) {
                Assert.Empty(
                    self.Inputs[index].PriorContext.SystemPromptFragment
                );
                Assert.Empty(
                    self.Inputs[index].PriorContext.ObservationMessage
                );
            }
            else {
                Assert.Contains(
                    $"self-{index}",
                    self.Inputs[index].PriorContext.SystemPromptFragment
                );
                Assert.Contains(
                    $"world-{index}",
                    self.Inputs[index].PriorContext.ObservationMessage
                );
            }
        }

        IReadOnlyList<EventAddress> anchors =
            await store.ListPublishedAnchorsAsync();
        Assert.True(deletedPreviousSource);
        Assert.Equal(7, anchors.Count);
        var snapshots = new Dictionary<
            EventAddress,
            RecapEpochStoreSnapshot
        >();
        foreach (EventAddress anchor in anchors) {
            snapshots.Add(
                anchor,
                Assert.IsType<RecapEpochStoreReadResult.Available>(
                    await store.ReadPublishedForRepairAsync(anchor)
                ).Snapshot
            );
        }
        RecapEpochStoreSnapshot latest = snapshots[rawHead];
        int chainCount = 0;
        bool reachedDeletedSource = false;
        while (true) {
            chainCount++;
            if (latest.EpochInput.Previous
                is not RecapEpochPrevious.Prior prior) {
                break;
            }
            Assert.Equal(
                prior.Pack.Source.AdmissionAnchor,
                latest.EpochInput.StartBoundary.Address
            );
            if (!snapshots.TryGetValue(
                    prior.Pack.Source.AdmissionAnchor,
                    out RecapEpochStoreSnapshot? predecessor
                )) {
                reachedDeletedSource = true;
                break;
            }
            latest = predecessor;
        }
        Assert.True(reachedDeletedSource);
        Assert.Equal(7, chainCount);
    }

    public void Dispose() {
        foreach (string path in _paths) {
            try {
                if (Directory.Exists(path)) {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch {
                // Best-effort test cleanup.
            }
        }
    }

    private static DerivedRecapEpochCampaignExecutor Executor(
        SessionJournalEngine engine,
        DerivedRecapEpochStore store,
        IReadOnlyList<IRecapBlockMaintainer> maintainers
    ) => new(
        engine.ReadView,
        store,
        new RecapEpochPlanningConfiguration(
            [
                .. Definitions().Select(definition =>
                    new RecapBlockCatalogEntry(
                        definition.RecapBlockId,
                        definition.Target,
                        definition.MaintainerId,
                        definition.MaintainerCapabilityFingerprint,
                        definition.MaxContentUtf8Bytes
                    ))
            ],
            new RecapCadenceConfig(
                TestHistoryUnitLoadEstimator.DefaultId,
                new HistoryLoadUnit(0),
                new HistoryLoadUnit(1)
            ),
            new TestHistoryUnitLoadEstimator(),
            new FixedRawChunkPolicy()
        ),
        new RecapEpochOperationLimits(
            maxRawGrowthEventCount: 64,
            maxRawEventsPerEpoch: 64,
            maxMaintainerCallsPerEpoch: 2,
            maxEpochsPerOperation: 2,
            maxMaintainerCallsPerOperation: 4,
            maxRecapBlockCount: 2,
            maxRebuildForwardRangeEventCount: 128
        ),
        new RecapBlockMaintainerRegistry(maintainers)
    );

    private static RecapEpochBlockDefinition[] Definitions() => [
        Definition("self", ContextHeaderCarrier.System, 0),
        Definition("world", ContextHeaderCarrier.Observation, 1)
    ];

    private static RecapEpochBlockDefinition Definition(
        string id,
        ContextHeaderCarrier carrier,
        int ordinal
    ) => new(
        new RecapBlockId(id),
        new ContextHeaderBlockPath(carrier, id),
        id,
        RecapPlannerTestIdentity.CapabilityFingerprint,
        4096,
        ordinal
    );

    private string NewPath() {
        string root = Directory.Exists("/dev/shm")
            ? "/dev/shm"
            : Path.GetTempPath();
        string path = Path.Combine(
            root,
            "atelia-recap-explicit-executor-tests",
            Guid.NewGuid().ToString("N")
        );
        _paths.Add(path);
        return path;
    }

    private static string PublishedPath(
        string repositoryPath,
        RefId refId,
        EventAddress admission
    ) => Path.Combine(
        repositoryPath,
        "derived",
        "recap",
        "v8",
        "refs",
        refId.ToHexString(),
        "published",
        EventAddressFileNameCodec.Format(admission)
    );

    private sealed class FixedRawChunkPolicy : IRecapEpochPlanningPolicy {
        public string Id => "tests.fixed-raw-chunk-policy";

        public RecapEpochPlanningDecision Decide(
            RecapEpochPlanningFacts facts
        ) {
            Dictionary<EventAddress, int> rawPositions = facts.Window
                .RawAddresses
                .Select((address, index) => (address, index))
                .ToDictionary(
                    static item => item.address,
                    static item => item.index
                );
            RecapHistoryLoadBoundary? boundary = facts.HistoryLoad
                .ReplaySafeBoundaries
                .LastOrDefault(candidate =>
                    rawPositions[candidate.Address] + 1
                        <= facts.MaxRawEventsPerEpoch);
            return boundary is null
                ? new RecapEpochPlanningDecision.NoBuild("fresh")
                : new RecapEpochPlanningDecision.Build(boundary.Address);
        }
    }

    private sealed class RecordingMaintainer(
        RecapEpochBlockDefinition definition
    ) : IRecapBlockMaintainer {
        public string Id => definition.MaintainerId;
        public ContextHeaderBlockPath Target => definition.Target;
        public string CapabilityFingerprint =>
            definition.MaintainerCapabilityFingerprint;
        public object RuntimeGroupAffinity => this;
        public List<RecapMaintenanceEpochInput> Inputs { get; } = [];

        public ValueTask<RecapMaintenanceSuccess> MaintainAsync(
            RecapMaintenanceEpochInput request,
            CancellationToken cancellationToken
        ) {
            cancellationToken.ThrowIfCancellationRequested();
            Inputs.Add(request);
            return ValueTask.FromResult<RecapMaintenanceSuccess>(
                new RecapMaintenanceSuccess.Updated(
                    $"{definition.RecapBlockId.Value}-{Inputs.Count}"
                )
            );
        }
    }
}
