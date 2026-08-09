using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.SessionJournal.DerivedRecap.Abstractions;
using Atelia.SessionJournal.DerivedRecap.Store;
using Xunit;

namespace Atelia.SessionJournal.DerivedRecap.Planner.Tests;

public sealed class DerivedRecapEpochCampaignExecutorTests {
    [Fact]
    public async Task MultiEpochCampaignUsesContiguousSharedInputsAndBudgetGate() {
        using var fixture = new CampaignFixture();
        EventAddress firstAdmission = fixture.AppendPair("A");
        EventAddress secondAdmission = fixture.AppendPair("B");
        _ = fixture.AppendPair("recent-1");
        _ = fixture.AppendPair("recent-2");
        var policy = new BoundaryPolicy(
            firstAdmission,
            secondAdmission
        );
        RecapEpochBlockDefinition[] definitions = fixture.Definitions();
        var self = new RecordingMaintainer(
            definitions[0],
            static call => new RecapMaintenanceSuccess.Updated(
                $"self-{call}"
            )
        );
        var world = new RecordingMaintainer(
            definitions[1],
            call => call == 1
                ? new RecapMaintenanceSuccess.Updated("world-1")
                : RecapMaintenanceSuccess.KeepUnchanged.Instance
        );
        DerivedRecapEpochCampaignExecutor executor = fixture.Executor(
            policy,
            [self, world],
            maxEpochsPerOperation: 1,
            maxCallsPerOperation: 2
        );

        var pending = Assert.IsType<
            DerivedRecapEpochOperationResult.MoreWorkPending
        >(await executor.RunOnlineAsync());
        Assert.Equal(firstAdmission, pending.Latest.AdmissionAnchor);
        Assert.Equal(1, pending.EpochsPublished);
        Assert.Equal(2, pending.MaintainerCalls);
        Assert.Single(self.Inputs);
        Assert.Single(world.Inputs);
        Assert.Same(self.Inputs[0], world.Inputs[0]);

        var fresh = Assert.IsType<
            DerivedRecapEpochOperationResult.Fresh
        >(await executor.RunOnlineAsync());
        Assert.Equal(secondAdmission, fresh.Latest!.AdmissionAnchor);
        Assert.Equal(1, fresh.EpochsPublished);
        Assert.Equal(2, fresh.MaintainerCalls);
        Assert.Equal(2, self.Inputs.Count);
        Assert.Equal(2, world.Inputs.Count);
        Assert.Same(self.Inputs[1], world.Inputs[1]);
        Assert.NotSame(self.Inputs[0], self.Inputs[1]);
        Assert.Contains(
            "self-1",
            self.Inputs[1].PriorContext.SystemPromptFragment
        );
        Assert.Contains(
            "world-1",
            self.Inputs[1].PriorContext.ObservationMessage
        );
        Assert.DoesNotContain(
            self.Inputs[1].HistoryMessages,
            message => Render(message).Contains(
                "A-observation",
                StringComparison.Ordinal
            )
        );
        Assert.Contains(
            self.Inputs[1].HistoryMessages,
            message => Render(message).Contains(
                "B-observation",
                StringComparison.Ordinal
            )
        );

        RecapEpochStoreSnapshot first = await fixture.Published(
            firstAdmission
        );
        RecapEpochStoreSnapshot second = await fixture.Published(
            secondAdmission
        );
        Assert.Equal(
            first.EpochInput.AdmissionBoundary,
            second.EpochInput.StartBoundary
        );
        var prior = Assert.IsType<RecapEpochPrevious.Prior>(
            second.EpochInput.Previous
        );
        Assert.Equal(firstAdmission, prior.Pack.Source.AdmissionAnchor);
        Assert.Equal(
            ["self-1", "world-1"],
            prior.Pack.Blocks.Select(static block => block.Content)
        );
        var kept = Assert.IsType<RecapEpochFinalHealth.Healthy>(
            second.Blocks[1].Final
        ).Block;
        var old = Assert.IsType<RecapEpochFinalHealth.Healthy>(
            first.Blocks[1].Final
        ).Block;
        Assert.Equal(old.Content, kept.Content);
        Assert.NotEqual(
            old.EpochBlockExecutionSha256,
            kept.EpochBlockExecutionSha256
        );
    }

    [Fact]
    public async Task FirstPendingRosterThatCannotFitIsConfigurationFailure() {
        using var fixture = new CampaignFixture();
        EventAddress admission = fixture.AppendPair("A");
        _ = fixture.AppendPair("recent");
        RecapEpochBlockDefinition[] definitions = fixture.Definitions();
        var self = new RecordingMaintainer(
            definitions[0],
            _ => new RecapMaintenanceSuccess.Updated("self")
        );

        DerivedRecapEpochCampaignExecutor executor = fixture.Executor(
            new BoundaryPolicy(admission, second: null),
            [self],
            maxEpochsPerOperation: 2,
            maxCallsPerOperation: 1
        );

        Assert.IsType<
            DerivedRecapEpochOperationResult.ConfigurationLimit
        >(await executor.RunOnlineAsync());
        Assert.Empty(self.Inputs);
        Assert.IsType<RecapEpochBuildingSelectionResult.Empty>(
            await fixture.Store.SelectBuildingAsync()
        );
    }

    [Fact]
    public async Task BuildingResumeKeepsHealthySiblingAndRetriesOnlyFailure() {
        using var fixture = new CampaignFixture();
        EventAddress admission = fixture.AppendPair("A");
        _ = fixture.AppendPair("recent");
        RecapEpochBlockDefinition[] definitions = fixture.Definitions();
        var self = new RecordingMaintainer(
            definitions[0],
            _ => new RecapMaintenanceSuccess.Updated("self")
        );
        var world = new RecordingMaintainer(
            definitions[1],
            call => {
                if (call == 1) {
                    throw new IOException("first attempt fails");
                }
                if (call == 2) {
                    _ = fixture.AppendPair("drift-during-resume");
                }
                return new RecapMaintenanceSuccess.Updated("world");
            }
        );
        DerivedRecapEpochCampaignExecutor executor = fixture.Executor(
            new BoundaryPolicy(admission, second: null),
            [self, world],
            maxEpochsPerOperation: 2,
            maxCallsPerOperation: 2
        );

        Assert.IsType<DerivedRecapEpochOperationResult.BlockFailed>(
            await executor.RunOnlineAsync()
        );
        RecapEpochStoreSnapshot partial = Assert.IsType<
            RecapEpochBuildingSelectionResult.Selected
        >(await fixture.Store.SelectBuildingAsync()).Snapshot;
        Assert.IsType<RecapEpochFinalHealth.Healthy>(
            partial.Blocks[0].Final
        );
        Assert.IsType<RecapEpochFinalHealth.Missing>(
            partial.Blocks[1].Final
        );

        Assert.IsType<DerivedRecapEpochOperationResult.Unavailable>(
            await executor.RunOnlineAsync()
        );
        Assert.Single(self.Inputs);
        Assert.Equal(2, world.Inputs.Count);
        Assert.IsType<RecapEpochBuildingSelectionResult.Selected>(
            await fixture.Store.SelectBuildingAsync()
        );

        Assert.IsType<DerivedRecapEpochOperationResult.Fresh>(
            await executor.RunOnlineAsync()
        );
        Assert.Single(self.Inputs);
        Assert.Equal(2, world.Inputs.Count);
        Assert.IsType<RecapEpochBuildingSelectionResult.Empty>(
            await fixture.Store.SelectBuildingAsync()
        );
        _ = await fixture.Published(admission);
    }

    [Fact]
    public async Task FrozenBuildingPublishesBeforeUnavailableActiveConfigurationIsLoaded() {
        using var fixture = new CampaignFixture();
        EventAddress admission = fixture.AppendPair("A");
        _ = fixture.AppendPair("recent");
        RecapEpochBlockDefinition[] definitions = fixture.Definitions();
        var self = new RecordingMaintainer(
            definitions[0],
            _ => new RecapMaintenanceSuccess.Updated("self")
        );
        var world = new RecordingMaintainer(
            definitions[1],
            call => call == 1
                ? throw new IOException("first attempt")
                : new RecapMaintenanceSuccess.Updated("world")
        );
        DerivedRecapEpochCampaignExecutor initial = fixture.Executor(
            new BoundaryPolicy(admission, second: null),
            [self, world],
            maxEpochsPerOperation: 2,
            maxCallsPerOperation: 2
        );
        Assert.IsType<DerivedRecapEpochOperationResult.BlockFailed>(
            await initial.RunOnlineAsync()
        );
        int configurationLoads = 0;
        var frozenFirst = new DerivedRecapEpochCampaignExecutor(
            fixture.Engine.ReadView,
            fixture.Store,
            (Func<RecapEpochActiveConfiguration>)(() => {
                configurationLoads++;
                throw new IOException("active config unavailable");
            }),
            new RecapEpochOperationLimits(
                maxRawGrowthEventCount: 512,
                maxRawEventsPerEpoch: 64,
                maxMaintainerCallsPerEpoch: 2,
                maxEpochsPerOperation: 2,
                maxMaintainerCallsPerOperation: 2,
                maxRecapBlockCount: 2
            ),
            new RecapBlockMaintainerRegistry([self, world])
        );

        Assert.IsType<DerivedRecapEpochOperationResult.Unavailable>(
            await frozenFirst.RunOnlineAsync()
        );
        Assert.Equal(1, configurationLoads);
        Assert.Single(self.Inputs);
        Assert.Equal(2, world.Inputs.Count);
        Assert.IsType<RecapEpochBuildingSelectionResult.Empty>(
            await fixture.Store.SelectBuildingAsync()
        );
        _ = await fixture.Published(admission);
    }

    [Fact]
    public async Task PublishedRestoreUsesSameKernelAndRepairsOnlyCommitmentMismatch() {
        using var fixture = new CampaignFixture();
        EventAddress admission = fixture.AppendPair("A");
        _ = fixture.AppendPair("recent");
        RecapEpochBlockDefinition[] definitions = fixture.Definitions();
        var self = new RecordingMaintainer(
            definitions[0],
            call => new RecapMaintenanceSuccess.Updated(
                $"self-{call}"
            )
        );
        var world = new RecordingMaintainer(
            definitions[1],
            _ => new RecapMaintenanceSuccess.Updated("world-1")
        );
        DerivedRecapEpochCampaignExecutor executor = fixture.Executor(
            new BoundaryPolicy(admission, second: null),
            [self, world],
            maxEpochsPerOperation: 2,
            maxCallsPerOperation: 2
        );
        Assert.IsType<DerivedRecapEpochOperationResult.Fresh>(
            await executor.RunOnlineAsync()
        );
        RecapEpochStoreSnapshot before = await fixture.Published(
            admission
        );
        string oldEnvelope = before.Publication!.EnvelopeSha256;
        await File.WriteAllBytesAsync(
            fixture.FinalPath(admission, "self"),
            DerivedRecapV8Codec.EncodeFinalBlock(
                DerivedRecapV8Codec.CreateFinalBlock(
                    before.Manifest,
                    before.Blocks[0].Definition,
                    "valid-but-uncommitted"
                )
            )
        );
        RecapEpochStoreSnapshot damaged = Assert.IsType<
            RecapEpochStoreReadResult.Available
        >(await fixture.Store.ReadPublishedForRepairAsync(admission))
            .Snapshot;
        Assert.IsType<RecapEpochFinalHealth.Damaged>(
            damaged.Blocks[0].Final
        );
        Assert.IsType<RecapEpochFinalHealth.Healthy>(
            damaged.Blocks[1].Final
        );

        Assert.IsType<DerivedRecapEpochOperationResult.Fresh>(
            await executor.RunOnlineAsync()
        );
        Assert.Equal(2, self.Inputs.Count);
        Assert.Single(world.Inputs);
        RecapEpochStoreSnapshot repaired = await fixture.Published(
            admission
        );
        Assert.NotEqual(oldEnvelope, repaired.Publication!.EnvelopeSha256);
        Assert.Equal(
            "self-2",
            Assert.IsType<RecapEpochFinalHealth.Healthy>(
                repaired.Blocks[0].Final
            ).Block.Content
        );
    }

    [Fact]
    public async Task RepairCallsLeaveLaterFullEpochAsMoreWorkPending() {
        using var fixture = new CampaignFixture();
        EventAddress firstAdmission = fixture.AppendPair("A");
        _ = fixture.AppendPair("recent");
        RecapEpochBlockDefinition[] definitions = fixture.Definitions();
        var self = new RecordingMaintainer(
            definitions[0],
            call => new RecapMaintenanceSuccess.Updated(
                $"self-{call}"
            )
        );
        var world = new RecordingMaintainer(
            definitions[1],
            call => new RecapMaintenanceSuccess.Updated(
                $"world-{call}"
            )
        );
        var initialPolicy = new BoundaryPolicy(
            firstAdmission,
            second: null
        );
        DerivedRecapEpochCampaignExecutor initial = fixture.Executor(
            initialPolicy,
            [self, world],
            maxEpochsPerOperation: 2,
            maxCallsPerOperation: 2
        );
        Assert.IsType<DerivedRecapEpochOperationResult.Fresh>(
            await initial.RunOnlineAsync()
        );
        RecapEpochStoreSnapshot first = await fixture.Published(
            firstAdmission
        );
        await File.WriteAllTextAsync(
            fixture.FinalPath(firstAdmission, "self"),
            "damaged"
        );
        EventAddress secondAdmission = fixture.AppendPair("B");
        _ = fixture.AppendPair("recent-2");
        DerivedRecapEpochCampaignExecutor resumed = fixture.Executor(
            new BoundaryPolicy(firstAdmission, secondAdmission),
            [self, world],
            maxEpochsPerOperation: 2,
            maxCallsPerOperation: 2
        );

        var pending = Assert.IsType<
            DerivedRecapEpochOperationResult.MoreWorkPending
        >(await resumed.RunOnlineAsync());
        Assert.Equal(firstAdmission, pending.Latest.AdmissionAnchor);
        Assert.Equal(1, pending.MaintainerCalls);
        Assert.Equal(2, self.Inputs.Count);
        Assert.Single(world.Inputs);
        Assert.IsType<RecapEpochBuildingSelectionResult.Empty>(
            await fixture.Store.SelectBuildingAsync()
        );

        var fresh = Assert.IsType<
            DerivedRecapEpochOperationResult.Fresh
        >(await resumed.RunOnlineAsync());
        Assert.Equal(secondAdmission, fresh.Latest!.AdmissionAnchor);
        Assert.Equal(3, self.Inputs.Count);
        Assert.Equal(2, world.Inputs.Count);
        Assert.Equal(
            first.EpochInput.AdmissionBoundary,
            (await fixture.Published(secondAdmission))
                .EpochInput.StartBoundary
        );
    }

    [Fact]
    public async Task ResumeRejectsValidButNonGoverningFrozenSetupBeforeCalls() {
        using var fixture = new CampaignFixture();
        EventAddress admission = fixture.AppendPair("A");
        _ = fixture.AppendPair("recent");
        EventAddress capturedHead = fixture.Engine.ReadView
            .ReadCurrentHead()!.Value;
        SessionHistoryPlanningSeed start = Assert.IsType<
            SessionCreatedPlanningSeedReadResult.Available
        >(fixture.Engine.ReadView.ReadSessionCreatedPlanningSeedAtBounded(
            capturedHead,
            64
        )).Seed;
        SessionHistoryPlanningWindow slab = Assert.IsType<
            SessionHistoryPlanningWindowReadResult.Available
        >(fixture.Engine.ReadView.ReadHistoryPlanningWindowAtBounded(
            admission,
            start,
            64
        )).Window;
        EventAddress laterSetup = fixture.Engine.AppendSystemPromptSetup(
            "later-system"
        );
        SessionContextAnchorSetupReferences wrongButValid = fixture.Engine
            .ResolveContextAnchorSetupReferences(laterSetup);
        DerivedRecapEpochInput input = DerivedRecapV8Codec.CreateEpochInput(
            new RecapEpochBoundary(start.Address, wrongButValid),
            new RecapEpochBoundary(admission, slab.EndSetups),
            slab.RawAddresses.Count,
            slab.RawRangeSha256,
            Array.AsReadOnly([
                .. slab.Units.Select(static unit => unit.Message)
            ]),
            RecapEpochPrevious.Empty.Instance
        );
        RecapEpochBlockDefinition[] definitions = fixture.Definitions();
        DerivedRecapEpochManifest manifest =
            DerivedRecapV8Codec.CreateManifest(
                fixture.Engine.BranchRefId,
                admission,
                input.PayloadSha256,
                definitions
            );
        Assert.IsType<InstallRecapEpochBuildingResult.Installed>(
            await fixture.Store.InstallBuildingAsync(manifest, input)
        );
        var self = new RecordingMaintainer(
            definitions[0],
            _ => new RecapMaintenanceSuccess.Updated("self")
        );
        var world = new RecordingMaintainer(
            definitions[1],
            _ => new RecapMaintenanceSuccess.Updated("world")
        );
        DerivedRecapEpochCampaignExecutor executor = fixture.Executor(
            new BoundaryPolicy(admission, second: null),
            [self, world],
            maxEpochsPerOperation: 2,
            maxCallsPerOperation: 2
        );

        Assert.IsType<DerivedRecapEpochOperationResult.Unavailable>(
            await executor.RunOnlineAsync()
        );
        Assert.Empty(self.Inputs);
        Assert.Empty(world.Inputs);
        Assert.IsType<RecapEpochBuildingSelectionResult.Selected>(
            await fixture.Store.SelectBuildingAsync()
        );
    }

    [Fact]
    public async Task NoBuildReplansWhenRawHeadChangesDuringDecision() {
        using var fixture = new CampaignFixture();
        _ = fixture.AppendPair("A");
        var policy = new MutatingNoBuildPolicy(fixture);
        RecapEpochBlockDefinition[] definitions = fixture.Definitions();
        var self = new RecordingMaintainer(
            definitions[0],
            _ => new RecapMaintenanceSuccess.Updated("self")
        );
        var world = new RecordingMaintainer(
            definitions[1],
            _ => new RecapMaintenanceSuccess.Updated("world")
        );
        DerivedRecapEpochCampaignExecutor executor = fixture.Executor(
            policy,
            [self, world],
            maxEpochsPerOperation: 2,
            maxCallsPerOperation: 4
        );

        Assert.IsType<DerivedRecapEpochOperationResult.Fresh>(
            await executor.RunOnlineAsync()
        );
        Assert.Equal(2, policy.Calls);
        Assert.Empty(self.Inputs);
        Assert.Empty(world.Inputs);
    }

    private static string Render(IHistoryMessage message) => message switch {
        ObservationMessage observation => observation.Content ?? string.Empty,
        ActionMessage action => action.GetFlattenedText(),
        _ => string.Empty
    };

    private sealed class BoundaryPolicy(
        EventAddress first,
        EventAddress? second
    ) : IRecapEpochPlanningPolicy {
        public string Id => "tests.boundary-policy";

        public RecapEpochPlanningDecision Decide(
            RecapEpochPlanningFacts facts
        ) {
            if (facts.Window.StartExclusive == first) {
                return second is { } next
                    ? new RecapEpochPlanningDecision.Build(next)
                    : new RecapEpochPlanningDecision.NoBuild("fresh");
            }
            if (second is { } latest
                && facts.Window.StartExclusive == latest) {
                return new RecapEpochPlanningDecision.NoBuild("fresh");
            }
            return new RecapEpochPlanningDecision.Build(first);
        }
    }

    private sealed class MutatingNoBuildPolicy(
        CampaignFixture fixture
    ) : IRecapEpochPlanningPolicy {
        public string Id => "tests.mutating-no-build-policy";
        public int Calls { get; private set; }

        public RecapEpochPlanningDecision Decide(
            RecapEpochPlanningFacts facts
        ) {
            _ = facts;
            Calls++;
            if (Calls == 1) {
                _ = fixture.AppendPair("drift");
            }
            return new RecapEpochPlanningDecision.NoBuild("fresh");
        }
    }

    private sealed class RecordingMaintainer(
        RecapEpochBlockDefinition definition,
        Func<int, RecapMaintenanceSuccess> result
    ) : IRecapBlockMaintainer {
        private int _calls;

        public string Id => definition.MaintainerId;
        public ContextHeaderBlockPath Target => definition.Target;
        public string CapabilityFingerprint =>
            definition.MaintainerCapabilityFingerprint;
        public object RuntimeGroupAffinity => this;
        public List<RecapMaintenanceEpochInput> Inputs { get; } = [];

        public IRecapMaintenanceGroupExecution CreateGroupExecution(
            RecapMaintenanceEpochInput input
        ) => new TestGroupExecution(this, input);

        public async ValueTask<RecapMaintenanceSuccess> MaintainAsync(
            IRecapMaintenanceGroupExecution groupExecution,
            IRecapMaintainerCallControl callControl,
            CancellationToken cancellationToken
        ) {
            await callControl.WaitForDispatchPermissionAsync(
                cancellationToken
            );
            callControl.MarkLaneAdmissionRequested();
            callControl.MarkDispatchStarted();
            cancellationToken.ThrowIfCancellationRequested();
            Inputs.Add(groupExecution.Input);
            return result(++_calls);
        }
    }

    private sealed record TestGroupExecution(
        object RuntimeGroupAffinity,
        RecapMaintenanceEpochInput Input
    ) : IRecapMaintenanceGroupExecution;

    private sealed class CampaignFixture : IDisposable {
        public CampaignFixture() {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "atelia-recap-epoch-campaign",
                Guid.NewGuid().ToString("N")
            );
            Engine = SessionJournalEngine.Create(
                Path,
                new SessionCreateOptions(
                    "model-a",
                    "system-a",
                    "surface-a"
                )
            );
            Store = DerivedRecapEpochStore.Open(
                Path,
                Engine.BranchRefId
            );
            Store.CreateAsync().AsTask().GetAwaiter().GetResult();
        }

        public string Path { get; }
        public SessionJournalEngine Engine { get; }
        public DerivedRecapEpochStore Store { get; }

        public EventAddress AppendPair(string name) {
            Engine.AppendObservation($"{name}-observation");
            return Engine.AppendImportedAgentAction(
                new ActionMessage([
                    new ActionBlock.Text($"{name}-action")
                ]),
                new CompletionDescriptor("import", "v1", "model-a")
            );
        }

        public RecapEpochBlockDefinition[] Definitions() => [
            Definition("self", ContextHeaderCarrier.System, 0),
            Definition("world", ContextHeaderCarrier.Observation, 1)
        ];

        public DerivedRecapEpochCampaignExecutor Executor(
            IRecapEpochPlanningPolicy policy,
            IReadOnlyList<IRecapBlockMaintainer> maintainers,
            int maxEpochsPerOperation,
            int maxCallsPerOperation
        ) {
            RecapEpochBlockDefinition[] definitions = Definitions();
            return new DerivedRecapEpochCampaignExecutor(
                Engine.ReadView,
                Store,
                new RecapEpochPlanningConfiguration(
                    [
                        .. definitions.Select(definition =>
                            new RecapBlockCatalogEntry(
                                definition.RecapBlockId,
                                definition.Target,
                                definition.MaintainerId,
                                definition
                                    .MaintainerCapabilityFingerprint,
                                definition.MaxContentUtf8Bytes
                            ))
                    ],
                    new RecapCadenceConfig(
                        TestHistoryUnitLoadEstimator.DefaultId,
                        new HistoryLoadUnit(0),
                        new HistoryLoadUnit(1)
                    ),
                    new TestHistoryUnitLoadEstimator(),
                    policy
                ),
                new RecapEpochOperationLimits(
                    maxRawGrowthEventCount: 512,
                    maxRawEventsPerEpoch: 64,
                    maxMaintainerCallsPerEpoch: 2,
                    maxEpochsPerOperation,
                    maxCallsPerOperation,
                    maxRecapBlockCount: 2
                ),
                new RecapBlockMaintainerRegistry(maintainers)
            );
        }

        public async ValueTask<RecapEpochStoreSnapshot> Published(
            EventAddress admission
        ) => Assert.IsType<RecapEpochStoreReadResult.Available>(
            await Store.ReadPublishedForRepairAsync(admission)
        ).Snapshot;

        public string FinalPath(EventAddress admission, string id)
            => System.IO.Path.Combine(
                Path,
                "derived",
                "recap",
                "v8",
                "refs",
                Engine.BranchRefId.ToHexString(),
                "published",
                EventAddressFileNameCodec.Format(admission),
                "blocks",
                $"{id}.json"
            );

        public void Dispose() {
            Engine.Dispose();
            try {
                if (Directory.Exists(Path)) {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (IOException) {
            }
        }

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
    }
}
