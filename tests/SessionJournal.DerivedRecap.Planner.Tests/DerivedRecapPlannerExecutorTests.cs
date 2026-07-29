using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.SessionJournal.DerivedRecap.Store;
using Xunit;

namespace Atelia.SessionJournal.DerivedRecap.Planner.Tests;

public sealed class DerivedRecapPlannerExecutorTests {
    [Fact]
    public async Task BelowTriggerHasNoPolicyOrMaintainerCalls() {
        using TestFixture fixture = await TestFixture.CreateAsync(
            historyPairs: 1
        );
        var maintainer = new ScriptedMaintainer(
            "self-maintainer",
            fixture.SelfTarget,
            static (_, _) => "unused"
        );
        var policy = new DelegatePolicy(_ =>
            throw new InvalidOperationException("must not run"));
        DerivedRecapPlannerExecutor executor = fixture.CreateExecutor(
            policy,
            [maintainer],
            rawGrowthTrigger: 100
        );

        DerivedRecapExecutionResult result =
            await executor.RunAsync();

        Assert.IsType<DerivedRecapExecutionResult.NoBuild>(result);
        Assert.Equal(0, policy.CallCount);
        Assert.Equal(0, maintainer.CallCount);
    }

    [Fact]
    public async Task MaintainMayKeepContentWhileAdvancingCursor() {
        using TestFixture fixture = await TestFixture.CreateAsync(
            historyPairs: 2
        );
        EventAddress start = fixture.ReplayStart();
        EventAddress admission = fixture.Engine.ReadCurrentHead()!.Value;
        var maintainer = new ScriptedMaintainer(
            "self-maintainer",
            fixture.SelfTarget,
            static (_, request) =>
                request.OldBlock.Text.Length == 0
                    ? "stable"
                    : request.OldBlock.Text
        );
        var policy = new DelegatePolicy(context =>
            new RecapPlanningPolicyDecision.Build(
                admission,
                [
                    new RecapBlockPlanningDecision.Maintain(
                        fixture.SelfId,
                        new RecapPlanningMaintainSource.Empty(start),
                        [admission],
                        EmptyRecapPriorContext.Instance
                    )
                ]
            ));

        DerivedRecapExecutionResult result =
            await fixture.CreateExecutor(policy, [maintainer])
                .RunAsync();

        var published =
            Assert.IsType<DerivedRecapExecutionResult.Published>(
                result
            );
        PublishedRecapSourceSnapshot source =
            await fixture.ReadSourceAsync(
                published.Descriptor,
                [fixture.SelfId]
            );
        DerivedRecapFrozenInput input =
            Assert.Single(source.FrozenInputs);
        Assert.Equal("stable", input.Content);
        Assert.Equal(admission, input.AbsorbedThrough);
        Assert.Equal(1, maintainer.CallCount);
    }

    [Fact]
    public async Task InheritCopiesExactSourceWithoutMaintainerCall() {
        using TestFixture fixture = await TestFixture.CreateAsync(
            historyPairs: 1
        );
        var maintainer = new ScriptedMaintainer(
            "self-maintainer",
            fixture.SelfTarget,
            static (_, _) => "source-content"
        );
        EventAddress firstAdmission =
            fixture.Engine.ReadCurrentHead()!.Value;
        EventAddress start = fixture.ReplayStart();
        var initialPolicy = new DelegatePolicy(_ =>
            new RecapPlanningPolicyDecision.Build(
                firstAdmission,
                [
                    new RecapBlockPlanningDecision.Maintain(
                        fixture.SelfId,
                        new RecapPlanningMaintainSource.Empty(start),
                        [firstAdmission],
                        EmptyRecapPriorContext.Instance
                    )
                ]
            ));
        var first = Assert.IsType<DerivedRecapExecutionResult.Published>(
            await fixture.CreateExecutor(initialPolicy, [maintainer])
                .RunAsync()
        );
        maintainer.Reset();

        EventAddress secondAdmission = fixture.AppendPair("next");
        var inheritPolicy = new DelegatePolicy(context => {
            RecapSourceIntent source =
                Assert.Single(context.PolicyFacts.AvailableSources)
                    .Source;
            return new RecapPlanningPolicyDecision.Build(
                secondAdmission,
                [
                    new RecapBlockPlanningDecision.Inherit(
                        fixture.SelfId,
                        source
                    )
                ]
            );
        });
        var second =
            Assert.IsType<DerivedRecapExecutionResult.Published>(
                await fixture.CreateExecutor(
                        inheritPolicy,
                        [maintainer]
                    )
                    .RunAsync()
            );

        PublishedRecapSourceSnapshot source =
            await fixture.ReadSourceAsync(
                second.Descriptor,
                [fixture.SelfId]
            );
        DerivedRecapFrozenInput input =
            Assert.Single(source.FrozenInputs);
        Assert.Equal("source-content", input.Content);
        Assert.Equal(firstAdmission, input.AbsorbedThrough);
        Assert.Equal(0, maintainer.CallCount);
        Assert.NotEqual(first.Descriptor, second.Descriptor);
    }

    [Fact]
    public async Task ResumeContinuesAfterHealthyCheckpointSuffix() {
        using TestFixture fixture = await TestFixture.CreateAsync(
            historyPairs: 2
        );
        (EventAddress start, EventAddress mid, EventAddress admission) =
            fixture.TwoStepRoute();
        MaintainRecapBlockPlan plan = fixture.CreateEmptyPlan(
            fixture.SelfId,
            fixture.SelfTarget,
            "self-maintainer",
            start,
            [mid, admission]
        );
        await fixture.CreateBuildingAsync(admission, [plan]);
        var maintainer = new ScriptedMaintainer(
            "self-maintainer",
            fixture.SelfTarget,
            static (call, request) => call switch {
                1 => "checkpoint-one",
                2 => throw new InvalidOperationException("zeta failed"),
                _ => request.OldBlock.Text + "+checkpoint-two"
            }
        );
        DerivedRecapPlannerExecutor executor = fixture.CreateExecutor(
            new DelegatePolicy(static _ =>
                new RecapPlanningPolicyDecision.NoBuild("unused")),
            [maintainer],
            maxRouteEndpointsPerBlock: 2
        );

        var failed =
            Assert.IsType<DerivedRecapExecutionResult.BlockFailed>(
                await executor.ResumeAsync(admission)
            );
        Assert.Equal(fixture.SelfId, failed.RecapBlockId);
        var published =
            Assert.IsType<DerivedRecapExecutionResult.Published>(
                await executor.ResumeAsync(admission)
            );

        Assert.Equal(
            ["", "checkpoint-one", "checkpoint-one"],
            maintainer.OldBlocks
        );
        PublishedRecapSourceSnapshot source =
            await fixture.ReadSourceAsync(
                published.Descriptor,
                [fixture.SelfId]
            );
        Assert.Equal(
            admission,
            Assert.Single(source.FrozenInputs).AbsorbedThrough
        );
    }

    [Fact]
    public async Task FinalCheckpointInstallsWithoutMaintainerCall() {
        using TestFixture fixture = await TestFixture.CreateAsync(
            historyPairs: 1
        );
        EventAddress admission = fixture.Engine.ReadCurrentHead()!.Value;
        MaintainRecapBlockPlan plan = fixture.CreateEmptyPlan(
            fixture.SelfId,
            fixture.SelfTarget,
            "self-maintainer",
            fixture.ReplayStart(),
            [admission]
        );
        BuildingSnapshot building =
            await fixture.CreateBuildingAsync(admission, [plan]);
        BuildingBlockInspection inspection =
            await fixture.Store.InspectBuildingBlockAsync(
                building.Descriptor,
                fixture.SelfId
            );
        _ = Assert.IsType<CheckpointWriteResult.Updated>(
            await fixture.Store.AdvanceRollingCheckpointAsync(
                building.Descriptor,
                fixture.SelfId,
                inspection.Checkpoint.StateToken,
                DerivedRecapCodec.CreateBlock(
                    plan,
                    admission,
                    "already-finished"
                )
            )
        );
        var maintainer = new ScriptedMaintainer(
            "self-maintainer",
            fixture.SelfTarget,
            static (_, _) =>
                throw new InvalidOperationException("must not run")
        );

        DerivedRecapExecutionResult result =
            await fixture.CreateExecutor(
                    new DelegatePolicy(static _ =>
                        new RecapPlanningPolicyDecision.NoBuild("unused")),
                    [maintainer]
                )
                .ResumeAsync(admission);

        Assert.IsType<DerivedRecapExecutionResult.Published>(result);
        Assert.Equal(0, maintainer.CallCount);
    }

    [Fact]
    public async Task LaterBlockFailureResumeSkipsHealthyEarlierBlock() {
        using TestFixture fixture = await TestFixture.CreateAsync(
            historyPairs: 1
        );
        EventAddress admission = fixture.Engine.ReadCurrentHead()!.Value;
        EventAddress start = fixture.ReplayStart();
        RecapBlockId alphaId = new("alpha");
        RecapBlockId zetaId = new("zeta");
        ContextHeaderBlockPath alphaTarget = new(
            ContextHeaderCarrier.System,
            "alpha"
        );
        ContextHeaderBlockPath zetaTarget = new(
            ContextHeaderCarrier.System,
            "zeta"
        );
        MaintainRecapBlockPlan alphaPlan = fixture.CreateEmptyPlan(
            alphaId,
            alphaTarget,
            "alpha-maintainer",
            start,
            [admission]
        );
        MaintainRecapBlockPlan zetaPlan = fixture.CreateEmptyPlan(
            zetaId,
            zetaTarget,
            "zeta-maintainer",
            start,
            [admission]
        );
        await fixture.CreateBuildingAsync(
            admission,
            [alphaPlan, zetaPlan]
        );
        var alpha = new ScriptedMaintainer(
            "alpha-maintainer",
            alphaTarget,
            static (_, _) => "alpha-ready"
        );
        var zeta = new ScriptedMaintainer(
            "zeta-maintainer",
            zetaTarget,
            static (call, _) => call == 1
                ? throw new InvalidOperationException("zeta failed")
                : "zeta-ready"
        );
        DerivedRecapPlannerExecutor executor = fixture.CreateExecutor(
            new DelegatePolicy(static _ =>
                new RecapPlanningPolicyDecision.NoBuild("unused")),
            [alpha, zeta],
            catalog: [
                new RecapBlockCatalogEntry(
                    alphaId,
                    alphaTarget,
                    alpha.Id,
                    TestFixture.MaxContent
                ),
                new RecapBlockCatalogEntry(
                    zetaId,
                    zetaTarget,
                    zeta.Id,
                    TestFixture.MaxContent
                )
            ],
            maxMaintainerCallsPerBuild: 2
        );

        Assert.IsType<DerivedRecapExecutionResult.BlockFailed>(
            await executor.ResumeAsync(admission)
        );
        Assert.IsType<DerivedRecapExecutionResult.Published>(
            await executor.ResumeAsync(admission)
        );

        Assert.Equal(1, alpha.CallCount);
        Assert.Equal(2, zeta.CallCount);
    }

    [Fact]
    public async Task ResumeLimitFailureHasNoMaintainerCalls() {
        using TestFixture fixture = await TestFixture.CreateAsync(
            historyPairs: 2
        );
        (EventAddress start, EventAddress mid, EventAddress admission) =
            fixture.TwoStepRoute();
        MaintainRecapBlockPlan plan = fixture.CreateEmptyPlan(
            fixture.SelfId,
            fixture.SelfTarget,
            "self-maintainer",
            start,
            [mid, admission]
        );
        await fixture.CreateBuildingAsync(admission, [plan]);
        var maintainer = new ScriptedMaintainer(
            "self-maintainer",
            fixture.SelfTarget,
            static (_, _) => "must-not-run"
        );

        DerivedRecapExecutionResult result =
            await fixture.CreateExecutor(
                    new DelegatePolicy(static _ =>
                        new RecapPlanningPolicyDecision.NoBuild("unused")),
                    [maintainer],
                    maxRouteEndpointsPerBlock: 1
                )
                .ResumeAsync(admission);

        Assert.IsType<DerivedRecapExecutionResult.Unavailable>(result);
        Assert.Equal(0, maintainer.CallCount);
    }

    [Fact]
    public async Task PolicyRawMutationReturnsTypedRetryableFailure() {
        using TestFixture fixture = await TestFixture.CreateAsync(
            historyPairs: 1
        );
        EventAddress captured = fixture.Engine.ReadCurrentHead()!.Value;
        EventAddress start = fixture.ReplayStart();
        var maintainer = new ScriptedMaintainer(
            "self-maintainer",
            fixture.SelfTarget,
            static (_, _) => "must-not-run"
        );
        var policy = new DelegatePolicy(_ => {
            fixture.Engine.AppendObservation("concurrent raw write");
            return new RecapPlanningPolicyDecision.Build(
                captured,
                [
                    new RecapBlockPlanningDecision.Maintain(
                        fixture.SelfId,
                        new RecapPlanningMaintainSource.Empty(start),
                        [captured],
                        EmptyRecapPriorContext.Instance
                    )
                ]
            );
        });

        var result =
            Assert.IsType<DerivedRecapExecutionResult.Retryable>(
                await fixture.CreateExecutor(policy, [maintainer])
                    .RunAsync()
            );

        Assert.Equal(
            DerivedRecapExecutionDefectCodes.RawHeadChanged,
            result.Code
        );
        Assert.Equal(0, maintainer.CallCount);
    }

    [Fact]
    public async Task InstallerRawMutationIsRetryableAndLeavesNoBuilding() {
        TestFixture? fixture = null;
        var hooks = new RecapStoreTestHooks(
            BeforeBuildingRawHeadRecheck: () =>
                fixture!.Engine.AppendObservation("installer race")
        );
        using (fixture =
               await TestFixture.CreateAsync(
                   historyPairs: 1,
                   hooks
               )) {
            EventAddress admission =
                fixture.Engine.ReadCurrentHead()!.Value;
            EventAddress start = fixture.ReplayStart();
            var maintainer = new ScriptedMaintainer(
                "self-maintainer",
                fixture.SelfTarget,
                static (_, _) => "must-not-run"
            );
            var policy = new DelegatePolicy(_ =>
                new RecapPlanningPolicyDecision.Build(
                    admission,
                    [
                        new RecapBlockPlanningDecision.Maintain(
                            fixture.SelfId,
                            new RecapPlanningMaintainSource.Empty(start),
                            [admission],
                            EmptyRecapPriorContext.Instance
                        )
                    ]
                ));

            var result =
                Assert.IsType<DerivedRecapExecutionResult.Retryable>(
                    await fixture.CreateExecutor(policy, [maintainer])
                        .RunAsync()
                );

            Assert.Equal(
                DerivedRecapExecutionDefectCodes.RawHeadChanged,
                result.Code
            );
            Assert.Equal(0, maintainer.CallCount);
            Assert.IsType<BuildingReadResult.Missing>(
                await fixture.Store.ReadBuildingAsync(admission)
            );
        }
    }

    [Fact]
    public async Task ResumeRejectsIncompleteRouteBeforeMaintainer() {
        using TestFixture fixture = await TestFixture.CreateAsync(
            historyPairs: 2
        );
        (EventAddress start, EventAddress mid, EventAddress admission) =
            fixture.TwoStepRoute();
        MaintainRecapBlockPlan plan = fixture.CreateEmptyPlan(
            fixture.SelfId,
            fixture.SelfTarget,
            "self-maintainer",
            start,
            [mid]
        );
        await fixture.CreateBuildingAsync(admission, [plan]);
        var maintainer = new ScriptedMaintainer(
            "self-maintainer",
            fixture.SelfTarget,
            static (_, _) => "must-not-run"
        );

        DerivedRecapExecutionResult result =
            await fixture.CreateExecutor(
                    new DelegatePolicy(static _ =>
                        new RecapPlanningPolicyDecision.NoBuild("unused")),
                    [maintainer]
                )
                .ResumeAsync(admission);

        Assert.IsType<DerivedRecapExecutionResult.Unavailable>(result);
        Assert.Equal(0, maintainer.CallCount);
    }

    [Fact]
    public async Task ResumeRejectsNonAncestorInlinePriorBeforeMaintainer() {
        using TestFixture fixture = await TestFixture.CreateAsync(
            historyPairs: 2
        );
        EventAddress start = fixture.ReplayStart();
        EventAddress admission = fixture.Engine.ReadCurrentHead()!.Value;
        var plan = new MaintainRecapBlockPlan(
            fixture.SelfId,
            fixture.SelfTarget,
            "self-maintainer",
            new EmptyRecapMaintainSource(start),
            [admission],
            new InlineRecapPriorContext(
                admission,
                ContextHeaderSnapshot.Empty
            ),
            TestFixture.MaxContent
        );
        await fixture.CreateBuildingAsync(admission, [plan]);
        var maintainer = new ScriptedMaintainer(
            "self-maintainer",
            fixture.SelfTarget,
            static (_, _) => "must-not-run"
        );

        DerivedRecapExecutionResult result =
            await fixture.CreateExecutor(
                    new DelegatePolicy(static _ =>
                        new RecapPlanningPolicyDecision.NoBuild("unused")),
                    [maintainer]
                )
                .ResumeAsync(admission);

        Assert.IsType<DerivedRecapExecutionResult.Unavailable>(result);
        Assert.Equal(0, maintainer.CallCount);
    }

    [Fact]
    public async Task StoreWriteFailureReturnsTypedUnavailable() {
        var hooks = new RecapStoreTestHooks(
            BeforeAtomicFileReplace: _ =>
                throw new IOException("injected checkpoint failure")
        );
        using TestFixture fixture = await TestFixture.CreateAsync(
            historyPairs: 1,
            hooks
        );
        EventAddress admission = fixture.Engine.ReadCurrentHead()!.Value;
        MaintainRecapBlockPlan plan = fixture.CreateEmptyPlan(
            fixture.SelfId,
            fixture.SelfTarget,
            "self-maintainer",
            fixture.ReplayStart(),
            [admission]
        );
        await fixture.CreateBuildingAsync(admission, [plan]);
        var maintainer = new ScriptedMaintainer(
            "self-maintainer",
            fixture.SelfTarget,
            static (_, _) => "candidate"
        );

        var result =
            Assert.IsType<DerivedRecapExecutionResult.Unavailable>(
                await fixture.CreateExecutor(
                        new DelegatePolicy(static _ =>
                            new RecapPlanningPolicyDecision.NoBuild("unused")),
                        [maintainer]
                    )
                    .ResumeAsync(admission)
            );

        Assert.Contains(
            result.Defects,
            defect => defect.Code
                == DerivedRecapExecutionDefectCodes.BuildingInvalid
        );
    }

    [Fact]
    public async Task ResumeRejectsSourceSetNotOlderThanTarget() {
        using TestFixture fixture = await TestFixture.CreateAsync(
            historyPairs: 2
        );
        SessionHistoryPlanningWindow raw =
            fixture.Engine.ReadHistoryPlanningWindow();
        EventAddress olderTarget =
            raw.ReplaySafeBoundaries[^2].Address;
        EventAddress sourceAdmission =
            raw.ReplaySafeBoundaries[^1].Address;
        var maintainer = new ScriptedMaintainer(
            "self-maintainer",
            fixture.SelfTarget,
            static (_, _) => "source-content"
        );
        var sourcePolicy = new DelegatePolicy(_ =>
            new RecapPlanningPolicyDecision.Build(
                sourceAdmission,
                [
                    new RecapBlockPlanningDecision.Maintain(
                        fixture.SelfId,
                        new RecapPlanningMaintainSource.Empty(
                            raw.StartExclusive
                        ),
                        [sourceAdmission],
                        EmptyRecapPriorContext.Instance
                    )
                ]
            ));
        var published =
            Assert.IsType<DerivedRecapExecutionResult.Published>(
                await fixture.CreateExecutor(
                        sourcePolicy,
                        [maintainer]
                    )
                    .RunAsync()
            );
        PublishedRecapSourceSnapshot source =
            await fixture.ReadSourceAsync(
                published.Descriptor,
                [fixture.SelfId]
            );
        DerivedRecapFrozenInput input =
            Assert.Single(source.FrozenInputs);
        var inherit = new InheritRecapBlockPlan(
            fixture.SelfId,
            fixture.SelfTarget,
            sourceAdmission,
            published.Descriptor.EnvelopeSha256,
            input.PayloadSha256,
            TestFixture.MaxContent
        );
        await fixture.CreateBuildingAsync(olderTarget, [inherit]);
        maintainer.Reset();

        DerivedRecapExecutionResult result =
            await fixture.CreateExecutor(
                    new DelegatePolicy(static _ =>
                        new RecapPlanningPolicyDecision.NoBuild("unused")),
                    [maintainer]
                )
                .ResumeAsync(olderTarget);

        Assert.IsType<DerivedRecapExecutionResult.Unavailable>(result);
        Assert.Equal(0, maintainer.CallCount);
    }

    [Fact]
    public async Task ResumeRejectsOversizedInheritedContent() {
        using TestFixture fixture = await TestFixture.CreateAsync(
            historyPairs: 1
        );
        EventAddress sourceAdmission =
            fixture.Engine.ReadCurrentHead()!.Value;
        var maintainer = new ScriptedMaintainer(
            "self-maintainer",
            fixture.SelfTarget,
            static (_, _) => "0123456789"
        );
        var sourcePolicy = new DelegatePolicy(_ =>
            new RecapPlanningPolicyDecision.Build(
                sourceAdmission,
                [
                    new RecapBlockPlanningDecision.Maintain(
                        fixture.SelfId,
                        new RecapPlanningMaintainSource.Empty(
                            fixture.ReplayStart()
                        ),
                        [sourceAdmission],
                        EmptyRecapPriorContext.Instance
                    )
                ]
            ));
        var published =
            Assert.IsType<DerivedRecapExecutionResult.Published>(
                await fixture.CreateExecutor(
                        sourcePolicy,
                        [maintainer]
                    )
                    .RunAsync()
            );
        PublishedRecapSourceSnapshot source =
            await fixture.ReadSourceAsync(
                published.Descriptor,
                [fixture.SelfId]
            );
        DerivedRecapFrozenInput input =
            Assert.Single(source.FrozenInputs);
        EventAddress target = fixture.AppendPair("target");
        const int smallerLimit = 5;
        var inherit = new InheritRecapBlockPlan(
            fixture.SelfId,
            fixture.SelfTarget,
            sourceAdmission,
            published.Descriptor.EnvelopeSha256,
            input.PayloadSha256,
            smallerLimit
        );
        await fixture.CreateBuildingAsync(target, [inherit]);
        maintainer.Reset();
        RecapBlockCatalogEntry[] catalog = [
            new(
                fixture.SelfId,
                fixture.SelfTarget,
                maintainer.Id,
                smallerLimit
            )
        ];

        DerivedRecapExecutionResult result =
            await fixture.CreateExecutor(
                    new DelegatePolicy(static _ =>
                        new RecapPlanningPolicyDecision.NoBuild("unused")),
                    [maintainer],
                    catalog: catalog
                )
                .ResumeAsync(target);

        Assert.IsType<DerivedRecapExecutionResult.Unavailable>(result);
        Assert.Equal(0, maintainer.CallCount);
    }

    private sealed class DelegatePolicy : IRecapPlanningPolicy {
        private readonly Func<
            RecapPlanningPolicyContext,
            RecapPlanningPolicyDecision
        > _decide;

        public DelegatePolicy(
            Func<
                RecapPlanningPolicyContext,
                RecapPlanningPolicyDecision
            > decide
        ) {
            _decide = decide;
        }

        public int CallCount { get; private set; }

        public RecapPlanningPolicyDecision Decide(
            RecapPlanningPolicyContext context
        ) {
            CallCount++;
            return _decide(context);
        }
    }

    private sealed class ScriptedMaintainer : IRecapBlockMaintainer {
        private readonly Func<
            int,
            RecapBlockMaintenanceRequest,
            string
        > _maintain;

        public ScriptedMaintainer(
            string id,
            ContextHeaderBlockPath target,
            Func<int, RecapBlockMaintenanceRequest, string> maintain
        ) {
            Id = id;
            Target = target;
            _maintain = maintain;
        }

        public string Id { get; }
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
            return ValueTask.FromResult(
                new RecapBlockMaintenanceResult(
                    Id,
                    Target,
                    new ContextHeaderBlock(content)
                )
            );
        }

        public void Reset() {
            CallCount = 0;
            OldBlocks.Clear();
        }
    }

    private sealed class TestFixture : IDisposable {
        public const int MaxContent = 4096;

        private TestFixture(
            string path,
            SessionJournalEngine engine,
            DerivedRecapStore store
        ) {
            Path = path;
            Engine = engine;
            Store = store;
        }

        public string Path { get; }
        public SessionJournalEngine Engine { get; }
        public DerivedRecapStore Store { get; }
        public RecapBlockId SelfId { get; } = new("self");
        public ContextHeaderBlockPath SelfTarget { get; } = new(
            ContextHeaderCarrier.System,
            "self"
        );

        public static async ValueTask<TestFixture> CreateAsync(
            int historyPairs,
            RecapStoreTestHooks? hooks = null
        ) {
            string path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "atelia-derived-recap-planner-tests",
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
                ? DerivedRecapStore.Open(path, engine.BranchRefId)
                : DerivedRecapStore.OpenForTest(
                    path,
                    engine.BranchRefId,
                    hooks
                );
            var fixture = new TestFixture(
                path,
                engine,
                store
            );
            await fixture.Store.CreateAsync();
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

        public EventAddress ReplayStart()
            => Engine.ReadHistoryPlanningWindow().StartExclusive;

        public (
            EventAddress Start,
            EventAddress Mid,
            EventAddress Admission
        ) TwoStepRoute() {
            SessionHistoryPlanningWindow window =
                Engine.ReadHistoryPlanningWindow();
            EventAddress[] boundaries = window.ReplaySafeBoundaries
                .Select(static item => item.Address)
                .ToArray();
            Assert.True(boundaries.Length >= 2);
            return (
                window.StartExclusive,
                boundaries[^2],
                boundaries[^1]
            );
        }

        public MaintainRecapBlockPlan CreateEmptyPlan(
            RecapBlockId id,
            ContextHeaderBlockPath target,
            string maintainerId,
            EventAddress start,
            IReadOnlyList<EventAddress> route
        ) => new(
            id,
            target,
            maintainerId,
            new EmptyRecapMaintainSource(start),
            route,
            EmptyRecapPriorContext.Instance,
            MaxContent
        );

        public async ValueTask<BuildingSnapshot> CreateBuildingAsync(
            EventAddress admission,
            IReadOnlyList<RecapBlockPlan> plans
        ) {
            var created = Assert.IsType<CreateBuildingResult.Created>(
                await Store.CreateBuildingAsync(
                    DerivedRecapCodec.CreateManifest(
                        Engine.BranchRefId,
                        admission,
                        plans
                    )
                )
            );
            var read = Assert.IsType<BuildingReadResult.Available>(
                await Store.ReadBuildingAsync(admission)
            );
            Assert.Equal(created.Descriptor, read.Snapshot.Descriptor);
            return read.Snapshot;
        }

        public DerivedRecapPlannerExecutor CreateExecutor(
            IRecapPlanningPolicy policy,
            IReadOnlyList<IRecapBlockMaintainer> maintainers,
            int rawGrowthTrigger = 0,
            int maxRouteEndpointsPerBlock = 4,
            int maxMaintainerCallsPerBuild = 8,
            IReadOnlyList<RecapBlockCatalogEntry>? catalog = null
        ) => new(
            Engine,
            Store,
            new RecapPlannerConfig(
                catalog ?? [
                    new RecapBlockCatalogEntry(
                        SelfId,
                        SelfTarget,
                        "self-maintainer",
                        MaxContent
                    )
                ],
                rawGrowthTrigger,
                rawGrowthHardLimit: 1000,
                maxRouteEndpointsPerBlock,
                maxMaintainerCallsPerBuild,
                maxRawEventsPerStep: 1000,
                maxRawEventsPerBuild: 4000
            ),
            policy,
            new RecapBlockMaintainerRegistry(maintainers)
        );

        public async ValueTask<PublishedRecapSourceSnapshot>
            ReadSourceAsync(
            PublishedRecapDescriptor descriptor,
            IReadOnlyList<RecapBlockId> blockIds
        ) {
            var available =
                Assert.IsType<
                    PublishedRecapSourceReadResult.Available
                >(
                    await Store.ReadPublishedSourceAsync(
                        descriptor,
                        blockIds
                    )
                );
            return available.Snapshot;
        }

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
