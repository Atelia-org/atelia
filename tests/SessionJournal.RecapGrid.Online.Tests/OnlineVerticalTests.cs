using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid;
using Atelia.SessionJournal.RecapGrid.Control;
using Atelia.SessionJournal.RecapGrid.Manager;
using Atelia.SessionJournal.RecapGrid.Store;
using Xunit;

namespace Atelia.SessionJournal.RecapGrid.Online.Tests;

public sealed class OnlineVerticalTests : IDisposable {
    private readonly List<string> _paths = [];
    private readonly O200kBaseHistoryUnitLoadEstimator _estimator = new();

    [Fact]
    public async Task MutableOwnerCapturesAuditOnlyInsideLifecycleScope() {
        string path = NewPath();
        using SessionJournalEngine writer = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model", "system", "online")
        );
        var lifecycle = new CapturingLifecycle(writer);
        writer.UseRuntime(new SessionRuntime(
            new TextCompletionClient(),
            CompletionTarget: new SessionCompletionTargetIdentity(
                "online-tests", "test", "online-tests-v1", "adapter-v1"),
            ContextCandidateSource: new EmptySource(),
            ContextLifecycle: lifecycle
        ));

        _ = await writer.SendAsync(
            writer.ReadCurrentHead()!.Value,
            "capture"
        );

        Assert.Equal(2, lifecycle.CaptureCount);
        Assert.All(lifecycle.CursorHeads,
            head => Assert.NotEqual(default, head));
        Assert.Throws<InvalidOperationException>(() =>
            writer.CaptureSelectedLineageAuditSnapshot(100));
        Assert.Throws<InvalidOperationException>(() =>
            writer.BeginSelectedLineageAudit());
    }

    [Fact]
    public async Task LifecycleAuditIsSingleCaptureAndRejectsConcurrentMutation() {
        string path = NewPath();
        using var captureEntered = new ManualResetEventSlim();
        using var releaseCapture = new ManualResetEventSlim();
        int hookCalls = 0;
        SessionJournalEngine? owner = null;
        var lifecycle = new ConcurrentAuditLifecycle(
            () => owner!,
            captureEntered,
            releaseCapture);
        var runtime = new SessionRuntime(
            new TextCompletionClient(),
            CompletionTarget: new SessionCompletionTargetIdentity(
                "online-tests", "test", "online-tests-v1", "adapter-v1"),
            ContextCandidateSource: new EmptySource(),
            ContextLifecycle: lifecycle);
        owner = SessionJournalEngine.CreateForTest(
            path,
            new SessionCreateOptions("model", "system", "online"),
            runtime,
            new SessionJournalTestHooks(
                AfterLifecycleAuditExpectedHeadCaptured: _ => {
                    if (Interlocked.Increment(ref hookCalls) == 1) {
                        captureEntered.Set();
                        Assert.True(releaseCapture.Wait(TimeSpan.FromSeconds(10)));
                    }
                }));
        using (owner) {
            _ = await owner.SendAsync(
                owner.ReadCurrentHead()!.Value,
                "capture concurrently");

            Assert.True(lifecycle.ObservedBusy);
            Assert.True(lifecycle.ConcurrentMutationRejected);
            SessionSelectedLineageAuditSnapshot snapshot = Assert.IsType<
                SessionSelectedLineageAuditSnapshot>(
                lifecycle.SnapshotAfterScope);
            SessionSelectedLineageForwardCursor cursor = Assert.IsType<
                SessionSelectedLineageForwardCursor>(
                lifecycle.CursorAfterScope);
            Assert.Throws<InvalidOperationException>(() =>
                snapshot.OpenForwardCursor());
            Assert.Throws<InvalidOperationException>(() =>
                cursor.ReadCurrentHead());
            cursor.Dispose();
            snapshot.Dispose();
            Assert.Throws<ObjectDisposedException>(() =>
                snapshot.OpenForwardCursor());
        }
    }

    [Fact]
    public async Task LifecycleAuditCapIsExactAndCancellationFailsClosed() {
        string path = NewPath();
        SessionJournalEngine? owner = null;
        var lifecycle = new AuditBoundsLifecycle(() => owner!);
        var runtime = new SessionRuntime(
            new TextCompletionClient(),
            CompletionTarget: new SessionCompletionTargetIdentity(
                "online-tests", "test", "online-tests-v1", "adapter-v1"),
            ContextCandidateSource: new EmptySource(),
            ContextLifecycle: lifecycle);
        owner = SessionJournalEngine.CreateForTest(
            path,
            new SessionCreateOptions("model", "system", "online"),
            runtime,
            new SessionJournalTestHooks());
        using (owner) {
            _ = await owner.SendAsync(
                owner.ReadCurrentHead()!.Value,
                "bounded capture");
        }

        Assert.True(lifecycle.ExactCapAvailable);
        Assert.True(lifecycle.CapMinusOneLimited);
        Assert.True(lifecycle.CancelledBeforeAuthority);
        Assert.True(lifecycle.CaptureAfterCancellationAvailable);
    }

    [Fact]
    public async Task EmptyTimelineWithoutActiveRecipeAuthorizesRawWithoutStore() {
        string path = NewPath();
        using SessionJournalEngine writer = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model", "system", "online")
        );
        ProvisionTimelineAndControl(writer);
        var executor = new RejectingExecutor();
        RecapGridOnlineOpenResult.Opened opened = Assert.IsType<
            RecapGridOnlineOpenResult.Opened>(
            RecapGridOnlineFactory.Open(
                writer,
                executor,
                RecapGridOnlineLimits.Production,
                _estimator)
        );
        await using RecapGridOnlineContextHandle online = opened.Handle;
        EventAddress boundary = writer.ReadCurrentHead()!.Value;

        RecapGridOnlinePassResult result = await online.PreparePassAsync(
            writer.ReadView,
            new SessionContextLifecycleRequest(
                new SessionContextSelectionRequest(boundary, 0),
                SessionExecutionPhase.Idle,
                SessionContextLifecycleTrigger.PreObservation,
                "pending"
            )
        );

        Assert.IsType<RecapGridOnlinePassResult.RawHistoryAuthorized>(result);
        Assert.Equal(0, executor.CallCount);
        Assert.False(File.Exists(Path.Combine(
            path, "derived", "recap-grid", "v1", "grid.sqlite")));
        Assert.Same(online.CandidateSource,
            Assert.IsAssignableFrom<ICoherentContextCandidateSource>(
                online.CandidateSource));
        Assert.Same(online, online.Lifecycle);
    }

    [Fact]
    public async Task NonEmptyTimelineWithoutActiveRecipeStillUsesRawOnly() {
        string path = NewPath();
        using SessionJournalEngine writer = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model", "system", "online"));
        _ = writer.AppendObservation("observation");
        _ = writer.AppendImportedAgentAction(
            new ActionMessage([new ActionBlock.Text("answer")]),
            new CompletionDescriptor("import", "v1", "model"));
        ProvisionTimelineAndControl(writer);
        var executor = new RejectingExecutor();
        await using RecapGridOnlineContextHandle online = Assert.IsType<
            RecapGridOnlineOpenResult.Opened>(
            RecapGridOnlineFactory.Open(writer, executor,
                RecapGridOnlineLimits.Production, _estimator)
        ).Handle;
        EventAddress boundary = writer.ReadCurrentHead()!.Value;

        RecapGridOnlinePassResult result = await online.PreparePassAsync(
            writer.ReadView,
            IdleRequest(boundary));

        Assert.IsType<RecapGridOnlinePassResult.RawHistoryAuthorized>(result);
        Assert.NotNull(ReadTimelineHead(writer).HeadRowId);
        Assert.Equal(0, executor.CallCount);
        Assert.False(File.Exists(Path.Combine(
            path, "derived", "recap-grid", "v1", "grid.sqlite")));
    }

    [Fact]
    public async Task ToolResultReadinessLeavesCurrentRawTailUnsealed() {
        string path = NewPath();
        using SessionJournalEngine writer = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model", "system", "online")
        );
        _ = writer.AppendObservation("observation");
        _ = writer.AppendImportedAgentAction(
            new ActionMessage([new ActionBlock.Text("answer")]),
            new CompletionDescriptor("import", "v1", "model")
        );
        ProvisionTimelineAndControl(writer);
        TimelineHeadRef before = ReadTimelineHead(writer);
        Assert.Null(before.HeadRowId);
        var executor = new RejectingExecutor();
        await using RecapGridOnlineContextHandle online = Assert.IsType<
            RecapGridOnlineOpenResult.Opened
        >(RecapGridOnlineFactory.Open(
            writer,
            executor,
            RecapGridOnlineLimits.Production,
            _estimator
        )).Handle;
        EventAddress boundary = writer.ReadCurrentHead()!.Value;

        RecapGridOnlinePassResult result = await online.PreparePassAsync(
            writer.ReadView,
            new SessionContextLifecycleRequest(
                new SessionContextSelectionRequest(boundary, 0),
                SessionExecutionPhase.AwaitingAgentAction,
                SessionContextLifecycleTrigger.ToolResultObserved
            )
        );

        Assert.IsType<RecapGridOnlinePassResult.RawHistoryAuthorized>(result);
        Assert.Equal(before, ReadTimelineHead(writer));
        Assert.Equal(0, executor.CallCount);
    }

    [Fact]
    public async Task EmptyTimelineWithActiveRecipeKeepsFirstRowRawOnly() {
        string path = NewPath();
        var agent = new CountingTextCompletionClient();
        using SessionJournalEngine writer = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model", "system", "online"));
        ProvisionTimelineAndControl(writer);
        TimelineHeadRef timelineHead = ReadTimelineHead(writer);
        Assert.Null(timelineHead.HeadRowId);
        (FamilyDefinition family, MaintainerDefinitionRevision definition) =
            BuildValues();
        GridBuildRecipe recipe = GridBuildRecipe.CreateFull(
            timelineHead.TimelineId,
            bootstrapThroughRowId: null,
            BuildTarget.Create([new BuildTargetColumn(
                definition.LogicalColumnId,
                definition.Digest)]));
        var admission = new RecapGridControlAdmission(
            RecapGridControlPermission.All,
            [family.Digest],
            [definition.Capability.CapabilityFingerprint],
            [ContextHeaderCarrier.System],
            ["case."],
            64,
            1024);
        using (RecapGridControlHandle control = Assert.IsType<
                   RecapGridControlOpenResult.Opened>(
                   RecapGridControlFactory.Open(
                       path, writer.BranchRefId, admission)
               ).Handle) {
            ControlHeadRef head = Assert.IsType<
                RecapGridControlSnapshotResult.Available>(
                control.Reader.ReadSnapshot()).Snapshot.Head;
            head = Assert.IsType<RecapGridControlPutResult.Stored>(
                control.Coordinator.PutFamilyDefinition(head, family)).Head;
            head = Assert.IsType<RecapGridControlPutResult.Stored>(
                control.Coordinator.PutMaintainerDefinition(
                    head, definition)).Head;
            head = Assert.IsType<RecapGridControlPutResult.Stored>(
                control.Coordinator.PutBuildRecipe(
                    head,
                    timelineHead,
                    recipe,
                    bootstrapWitness: null)).Head;
            Assert.IsType<RecapGridControlActivateResult.Applied>(
                control.Coordinator.CompareExchangeActiveRecipe(
                    head,
                    timelineHead,
                    recipe.Digest,
                    RecapGridControlActivationPurpose.Direct));
        }
        await using RecapGridOnlineContextHandle online = Assert.IsType<
            RecapGridOnlineOpenResult.Opened>(
            RecapGridOnlineFactory.Open(
                writer,
                new RejectingExecutor(),
                RecapGridOnlineLimits.Production,
                _estimator)
        ).Handle;
        writer.UseRuntime(Runtime(online, agent));
        EventAddress initial = writer.ReadCurrentHead()!.Value;

        _ = await writer.SendAsync(initial, "first observation");

        Assert.Equal(1, agent.CallCount);
        Assert.Null(ReadTimelineHead(writer).HeadRowId);
        Assert.False(File.Exists(Path.Combine(
            path, "derived", "recap-grid", "v1", "grid.sqlite")));
    }

    [Fact]
    public async Task ActiveUnfulfilledRecipeBuildsOnceThenIsIdempotent() {
        string path = NewPath();
        using SessionJournalEngine writer = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model", "system", "online"));
        _ = writer.AppendObservation("The behavior of X is suspicious.");
        _ = writer.AppendImportedAgentAction(
            new ActionMessage([new ActionBlock.Text("Investigate X.")]),
            new CompletionDescriptor("import", "v1", "model"));
        ProvisionTimelineAndControl(writer);
        var executor = new FillingExecutor();
        await using RecapGridOnlineContextHandle online = Assert.IsType<
            RecapGridOnlineOpenResult.Opened>(
            RecapGridOnlineFactory.Open(writer, executor,
                RecapGridOnlineLimits.Production, _estimator)
        ).Handle;
        EventAddress boundary = writer.ReadCurrentHead()!.Value;
        Assert.IsType<RecapGridOnlinePassResult.RawHistoryAuthorized>(
            await online.PreparePassAsync(
                writer.ReadView, IdleRequest(boundary)));

        TimelineHeadRef timelineHead = ReadTimelineHead(writer);
        using HistoryTimelineHandle timeline = Assert.IsType<
            HistoryTimelineOpenResult.Opened>(
            HistoryTimelineFactory.Open(writer.ReadView, _estimator)
        ).Handle;
        HistoryTimelineSelectedRow row = Assert.IsType<
            HistoryTimelineReaderRowResult.Selected>(
            timeline.Reader.ReadSelectedRow(
                timelineHead,
                timelineHead.HeadRowId!.Value)
        ).Row;
        Assert.IsType<RecapGridStoreCreateResult.Created>(
            RecapGridStoreFactory.Create(path));
        (FamilyDefinition family, MaintainerDefinitionRevision definition) =
            BuildValues();
        GridBuildRecipe recipe = GridBuildRecipe.CreateFull(
            timelineHead.TimelineId,
            row.Descriptor.RowId,
            BuildTarget.Create([new BuildTargetColumn(
                definition.LogicalColumnId,
                definition.Digest)]));
        var admission = new RecapGridControlAdmission(
            RecapGridControlPermission.All,
            [family.Digest],
            [definition.Capability.CapabilityFingerprint],
            [ContextHeaderCarrier.System],
            ["case."],
            maximumBootstrapRows: 64,
            maximumProjectedCalls: 1024);
        using (RecapGridControlHandle control = Assert.IsType<
                   RecapGridControlOpenResult.Opened>(
                   RecapGridControlFactory.Open(
                       path, writer.BranchRefId, admission)
               ).Handle) {
            ControlHeadRef head = Assert.IsType<
                RecapGridControlSnapshotResult.Available>(
                control.Reader.ReadSnapshot()).Snapshot.Head;
            head = Assert.IsType<RecapGridControlPutResult.Stored>(
                control.Coordinator.PutFamilyDefinition(head, family)).Head;
            head = Assert.IsType<RecapGridControlPutResult.Stored>(
                control.Coordinator.PutMaintainerDefinition(
                    head, definition)).Head;
            head = Assert.IsType<RecapGridControlPutResult.Stored>(
                control.Coordinator.PutBuildRecipe(
                    head, timelineHead, recipe, row.Witness)).Head;
            Assert.IsType<RecapGridControlActivateResult.Applied>(
                control.Coordinator.CompareExchangeActiveRecipe(
                    head,
                    timelineHead,
                    recipe.Digest,
                    RecapGridControlActivationPurpose.Direct));
        }

        Assert.IsType<RecapGridOnlinePassResult.Ready>(
            await online.PreparePassAsync(
                writer.ReadView, IdleRequest(boundary)));
        int firstBuildCalls = executor.CallCount;
        Assert.True(firstBuildCalls > 0);
        Assert.IsType<RecapGridOnlinePassResult.Ready>(
            await online.PreparePassAsync(
                writer.ReadView, IdleRequest(boundary)));
        Assert.Equal(firstBuildCalls, executor.CallCount);
    }

    [Fact]
    public async Task DisposeDrainsActiveBuildAndBlocksGridResetUntilReleased() {
        string path = NewPath();
        using SessionJournalEngine writer = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model", "system", "online"));
        _ = writer.AppendObservation("The behavior of X is suspicious.");
        _ = writer.AppendImportedAgentAction(
            new ActionMessage([new ActionBlock.Text("Investigate X.")]),
            new CompletionDescriptor("import", "v1", "model"));
        ProvisionTimelineAndControl(writer);
        var executor = new BlockingFillingExecutor();
        RecapGridOnlineContextHandle online = Assert.IsType<
            RecapGridOnlineOpenResult.Opened>(
            RecapGridOnlineFactory.Open(
                writer,
                executor,
                RecapGridOnlineLimits.Production,
                _estimator)).Handle;
        EventAddress boundary = writer.ReadCurrentHead()!.Value;
        Assert.IsType<RecapGridOnlinePassResult.RawHistoryAuthorized>(
            await online.PreparePassAsync(
                writer.ReadView, IdleRequest(boundary)));

        TimelineHeadRef timelineHead = ReadTimelineHead(writer);
        using HistoryTimelineHandle timeline = Assert.IsType<
            HistoryTimelineOpenResult.Opened>(
            HistoryTimelineFactory.Open(writer.ReadView, _estimator)).Handle;
        HistoryTimelineSelectedRow row = Assert.IsType<
            HistoryTimelineReaderRowResult.Selected>(
            timeline.Reader.ReadSelectedRow(
                timelineHead,
                timelineHead.HeadRowId!.Value)).Row;
        Assert.IsType<RecapGridStoreCreateResult.Created>(
            RecapGridStoreFactory.Create(path));
        (FamilyDefinition family, MaintainerDefinitionRevision definition) =
            BuildValues();
        GridBuildRecipe recipe = GridBuildRecipe.CreateFull(
            timelineHead.TimelineId,
            row.Descriptor.RowId,
            BuildTarget.Create([new BuildTargetColumn(
                definition.LogicalColumnId,
                definition.Digest)]));
        var admission = new RecapGridControlAdmission(
            RecapGridControlPermission.All,
            [family.Digest],
            [definition.Capability.CapabilityFingerprint],
            [ContextHeaderCarrier.System],
            ["case."],
            maximumBootstrapRows: 64,
            maximumProjectedCalls: 1024);
        using (RecapGridControlHandle control = Assert.IsType<
                   RecapGridControlOpenResult.Opened>(
                   RecapGridControlFactory.Open(
                       path, writer.BranchRefId, admission)).Handle) {
            ControlHeadRef head = Assert.IsType<
                RecapGridControlSnapshotResult.Available>(
                control.Reader.ReadSnapshot()).Snapshot.Head;
            head = Assert.IsType<RecapGridControlPutResult.Stored>(
                control.Coordinator.PutFamilyDefinition(
                    head, family)).Head;
            head = Assert.IsType<RecapGridControlPutResult.Stored>(
                control.Coordinator.PutMaintainerDefinition(
                    head, definition)).Head;
            head = Assert.IsType<RecapGridControlPutResult.Stored>(
                control.Coordinator.PutBuildRecipe(
                    head, timelineHead, recipe, row.Witness)).Head;
            Assert.IsType<RecapGridControlActivateResult.Applied>(
                control.Coordinator.CompareExchangeActiveRecipe(
                    head,
                    timelineHead,
                    recipe.Digest,
                    RecapGridControlActivationPurpose.Direct));
        }

        Task<RecapGridOnlinePassResult> operation = online.PreparePassAsync(
            writer.ReadView, IdleRequest(boundary)).AsTask();
        await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsType<RecapGridStorePrepareResetResult.Busy>(
            RecapGridStoreMaintenance.PrepareReset(path));
        Task disposing = online.DisposeAsync().AsTask();
        await Task.Delay(20);
        Assert.False(disposing.IsCompleted);

        executor.Release.TrySetResult();
        Assert.IsType<RecapGridOnlinePassResult.Ready>(
            await operation.WaitAsync(TimeSpan.FromSeconds(10)));
        await disposing.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsType<RecapGridStorePrepareResetResult.Prepared>(
            RecapGridStoreMaintenance.PrepareReset(path));
        Assert.IsType<RecapGridOnlinePassResult.Disposed>(
            await online.PreparePassAsync(
                writer.ReadView, IdleRequest(boundary)));
        await online.DisposeAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposeOwnedContinuesAndAggregatesNonFatalCleanup(
        bool asynchronous
    ) {
        string path = NewPath();
        using SessionJournalEngine writer = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model", "system", "online"));
        ProvisionTimelineAndControl(writer);
        RecapGridOnlineContextHandle online = Assert.IsType<
            RecapGridOnlineOpenResult.Opened>(
            RecapGridOnlineFactory.Open(
                writer,
                new RejectingExecutor(),
                RecapGridOnlineLimits.Production,
                _estimator)).Handle;
        int manager = 0;
        int getter = 0;
        int timeline = 0;
        online.CleanupHooksForTest = new(
            AfterManagerDisposed: () => {
                manager++;
                throw new InvalidOperationException("manager-cleanup");
            },
            AfterGetterDisposed: () => {
                getter++;
                throw new IOException("getter-cleanup");
            },
            AfterTimelineDisposed: () => timeline++);

        Exception failure = asynchronous
            ? await Assert.ThrowsAsync<AggregateException>(
                () => online.DisposeAsync().AsTask())
            : Assert.Throws<AggregateException>(() => online.Dispose());

        Assert.Equal(2, Assert.IsType<AggregateException>(failure)
            .InnerExceptions.Count);
        Assert.Equal((1, 1, 1), (manager, getter, timeline));
        if (asynchronous) {
            await Assert.ThrowsAsync<AggregateException>(
                () => online.DisposeAsync().AsTask());
        }
        else {
            Assert.Throws<AggregateException>(() => online.Dispose());
        }
        Assert.Equal((1, 1, 1), (manager, getter, timeline));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposeOwnedCleanupCanReenterWithoutSelfDeadlock(
        bool asynchronousReentry
    ) {
        string path = NewPath();
        using SessionJournalEngine writer = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model", "system", "online"));
        ProvisionTimelineAndControl(writer);
        RecapGridOnlineContextHandle online = Assert.IsType<
            RecapGridOnlineOpenResult.Opened>(
            RecapGridOnlineFactory.Open(
                writer,
                new RejectingExecutor(),
                RecapGridOnlineLimits.Production,
                _estimator)).Handle;
        int getter = 0;
        int timeline = 0;
        online.CleanupHooksForTest = new(
            AfterManagerDisposed: () => {
                if (asynchronousReentry) {
                    _ = online.DisposeAsync();
                }
                else {
                    online.Dispose();
                }
            },
            AfterGetterDisposed: () => getter++,
            AfterTimelineDisposed: () => timeline++);

        await online.DisposeAsync().AsTask()
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal((1, 1), (getter, timeline));
        await online.DisposeAsync();
        online.Dispose();
        Assert.Equal((1, 1), (getter, timeline));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposeOwnedFatalStopsImmediatelyAndRemainsObservable(
        bool asynchronous
    ) {
        string path = NewPath();
        using SessionJournalEngine writer = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model", "system", "online"));
        ProvisionTimelineAndControl(writer);
        RecapGridOnlineContextHandle online = Assert.IsType<
            RecapGridOnlineOpenResult.Opened>(
            RecapGridOnlineFactory.Open(
                writer,
                new RejectingExecutor(),
                RecapGridOnlineLimits.Production,
                _estimator)).Handle;
        int fatal = 0;
        int notReached = 0;
        online.CleanupHooksForTest = new(
            AfterGetterDisposed: () => {
                fatal++;
                throw new OutOfMemoryException("fatal-cleanup");
            },
            AfterTimelineDisposed: () => notReached++);

        if (asynchronous) {
            await Assert.ThrowsAsync<OutOfMemoryException>(
                () => online.DisposeAsync().AsTask());
            await Assert.ThrowsAsync<OutOfMemoryException>(
                () => online.DisposeAsync().AsTask());
        }
        else {
            Assert.Throws<OutOfMemoryException>(() => online.Dispose());
            Assert.Throws<OutOfMemoryException>(() => online.Dispose());
        }
        Assert.Equal(1, fatal);
        Assert.Equal(0, notReached);
    }

    [Fact]
    public void ReentrantUnawaitedDrainFaultIsObservedByLaterDispose() {
        var fatal = new OutOfMemoryException("fatal-drain");
        var lifetime = new OnlineLifetime(
            () => ValueTask.FromException(fatal));
        Assert.True(lifetime.TryEnter(
            out OnlineLifetime.OperationLease? lease));
        using (lifetime.EnterOperationScope()) {
            _ = lifetime.DisposeAndDrainAsync();
        }

        lease!.Dispose();

        OutOfMemoryException observed = Assert.Throws<OutOfMemoryException>(
            () => lifetime.DisposeAndDrain());
        Assert.Same(fatal, observed);
    }

    [Fact]
    public async Task OfflineAuditCapAndCapPlusOneAreTypedAndRecoverable() {
        string path = NewPath();
        CreateRawHistory(path, turns: 12);
        int eventCount = CountAuditEvents(path);
        using SessionJournalEngine writer = SessionJournalEngine.Open(path);
        ProvisionTimelineAndControl(writer, maxRawEvents: 3);
        EventAddress before = writer.ReadCurrentHead()!.Value;

        var lowLimits = new RecapGridOnlineLimits(
            eventCount - 1,
            maximumTimelineRows: 64,
            RecapGridOnlineLimits.Production.BuildBudget);
        await using (RecapGridOnlineContextHandle low = Assert.IsType<
                         RecapGridOnlineOpenResult.Opened>(
                         RecapGridOnlineFactory.Open(
                             writer,
                             new RejectingExecutor(),
                             lowLimits,
                             _estimator)
                     ).Handle) {
            writer.UseRuntime(Runtime(low));
            SessionJournalNotReadyException limited =
                await Assert.ThrowsAsync<SessionJournalNotReadyException>(
                    () => writer.SendAsync(
                        before,
                        "cap must stop before authority"));
            Assert.Equal(
                SessionJournalNotReadyReason.RecapMaintenanceBackpressure,
                limited.Reason);
            Assert.Contains("OfflineAuditEventLimitExceeded", limited.Message);
            Assert.Equal(before, writer.ReadCurrentHead());
        }

        var exactLimits = new RecapGridOnlineLimits(
            eventCount,
            maximumTimelineRows: 64,
            RecapGridOnlineLimits.Production.BuildBudget);
        await using (RecapGridOnlineContextHandle exact = Assert.IsType<
                         RecapGridOnlineOpenResult.Opened>(
                         RecapGridOnlineFactory.Open(
                             writer,
                             new RejectingExecutor(),
                             exactLimits,
                             _estimator)
                     ).Handle) {
            writer.UseRuntime(Runtime(exact));
            _ = await writer.SendAsync(
                before,
                "exact cap can finish the audit");
            Assert.NotNull(ReadTimelineHead(writer).HeadRowId);
            Assert.False(File.Exists(Path.Combine(
                path, "derived", "recap-grid", "v1", "grid.sqlite")));
        }
    }

    [Fact]
    public async Task OnlineTimelineRowLimitExactHitSucceedsAndNextRowBackpressures() {
        string baselinePath = NewPath();
        CreateRawHistory(baselinePath, turns: 2);
        string exactPath = NewPath();
        string overflowPath = NewPath();
        CopyDirectory(baselinePath, exactPath);
        CopyDirectory(baselinePath, overflowPath);

        async Task<(TimelineHeadRef Head, int AgentCalls)> RunAsync(
            string path,
            int maximumTimelineRows,
            bool expectBackpressure
        ) {
            using SessionJournalEngine writer = SessionJournalEngine.Open(path);
            ProvisionTimelineAndControl(writer, maxRawEvents: 64);
            var agent = new CountingTextCompletionClient();
            var limits = new RecapGridOnlineLimits(
                maximumAuditEvents: 1_024,
                maximumTimelineRows,
                RecapGridOnlineLimits.Production.BuildBudget);
            await using RecapGridOnlineContextHandle online = Assert.IsType<
                RecapGridOnlineOpenResult.Opened>(
                RecapGridOnlineFactory.Open(
                    writer,
                    new RejectingExecutor(),
                    limits,
                    _estimator)).Handle;
            writer.UseRuntime(Runtime(online, agent));
            EventAddress before = writer.ReadCurrentHead()!.Value;

            if (expectBackpressure) {
                SessionJournalNotReadyException limited =
                    await Assert.ThrowsAsync<SessionJournalNotReadyException>(
                        () => writer.SendAsync(before, "must not dispatch"));
                Assert.Equal(
                    SessionJournalNotReadyReason.RecapMaintenanceBackpressure,
                    limited.Reason);
                Assert.Contains("TimelineRowLimitExceeded", limited.Message);
                Assert.Equal(before, writer.ReadCurrentHead());
            }
            else {
                _ = await writer.SendAsync(before, "exact terminal probe");
            }
            return (ReadTimelineHead(writer), agent.CallCount);
        }

        (TimelineHeadRef baseline, _) = await RunAsync(
            baselinePath,
            maximumTimelineRows: 64,
            expectBackpressure: false);
        int exactRowCount = checked((int)baseline.Generation);
        Assert.True(exactRowCount > 1);

        (TimelineHeadRef exact, int exactCalls) = await RunAsync(
            exactPath,
            maximumTimelineRows: exactRowCount,
            expectBackpressure: false);
        Assert.NotNull(exact.HeadRowId);
        Assert.Equal(1, exactCalls);
        Assert.Equal(exactRowCount, exact.Generation);

        (TimelineHeadRef overflow, int overflowCalls) = await RunAsync(
            overflowPath,
            maximumTimelineRows: exactRowCount - 1,
            expectBackpressure: true);
        Assert.NotNull(overflow.HeadRowId);
        Assert.Equal(0, overflowCalls);
        Assert.Equal(exactRowCount - 1, overflow.Generation);
    }

    [Fact]
    public async Task RewindOfflineReconcileAndSuffixBuildShareOneAuditSnapshotAndProbeLimit() {
        string basePath = NewPath();
        EventAddress siblingHead;
        using (SessionJournalEngine writer = SessionJournalEngine.Create(
                   basePath,
                   new SessionCreateOptions("model", "system", "online"))) {
            for (int index = 0; index < 12; index++) {
                _ = writer.AppendObservation(
                    $"original-observation-{index}");
                _ = writer.AppendImportedAgentAction(
                    new ActionMessage([
                        new ActionBlock.Text($"original-answer-{index}")
                    ]),
                    new CompletionDescriptor("import", "v1", "model"));
            }
            ProvisionTimelineAndControl(writer, maxRawEvents: 3);
            var primeAgent = new CountingTextCompletionClient();
            await using (RecapGridOnlineContextHandle prime = Assert.IsType<
                             RecapGridOnlineOpenResult.Opened>(
                             RecapGridOnlineFactory.Open(
                                 writer,
                                 new RejectingExecutor(),
                                 RecapGridOnlineLimits.Production,
                                 _estimator)).Handle) {
                writer.UseRuntime(Runtime(prime, primeAgent));
                _ = await writer.SendAsync(
                    writer.ReadCurrentHead()!.Value,
                    "prime the non-empty Timeline");
            }
            Assert.IsType<SessionTurnRetractionResult.Moved>(
                writer.RewindLatestCompletedTurn(
                    writer.ReadCurrentHead()!.Value));
            for (int index = 0; index < 6; index++) {
                Assert.IsType<SessionTurnRetractionResult.Moved>(
                    writer.RewindLatestCompletedTurn(
                        writer.ReadCurrentHead()!.Value));
            }
            for (int index = 0; index < 8; index++) {
                _ = writer.AppendObservation(
                    $"sibling-observation-{index}");
                _ = writer.AppendImportedAgentAction(
                    new ActionMessage([
                        new ActionBlock.Text($"sibling-answer-{index}")
                    ]),
                    new CompletionDescriptor("import", "v1", "model"));
            }
            siblingHead = writer.ReadCurrentHead()!.Value;
        }

        string exactPath = NewPath();
        string overflowPath = NewPath();
        CopyDirectory(basePath, exactPath);
        CopyDirectory(basePath, overflowPath);

        async Task<(TimelineHeadRef Before, TimelineHeadRef After)>
            RunAsync(
            string path,
            int maximumRows,
            bool expectBackpressure
        ) {
            int captures = 0;
            var agent = new CountingTextCompletionClient();
            using SessionJournalEngine writer =
                SessionJournalEngine.OpenForTest(
                    path,
                    new SessionRuntime(
                        agent,
                        CompletionTarget: CompletionTarget(),
                        ContextCandidateSource: new EmptySource()),
                    new SessionJournalTestHooks(
                        AfterLifecycleAuditExpectedHeadCaptured: _ =>
                            Interlocked.Increment(ref captures)));
            TimelineHeadRef before = ReadTimelineHead(writer);
            var limits = new RecapGridOnlineLimits(
                RecapGridOnlineLimits.Production.MaximumAuditEvents,
                maximumRows,
                RecapGridOnlineLimits.Production.BuildBudget);
            await using RecapGridOnlineContextHandle online = Assert.IsType<
                RecapGridOnlineOpenResult.Opened>(
                RecapGridOnlineFactory.Open(
                    writer,
                    new RejectingExecutor(),
                    limits,
                    _estimator)).Handle;
            writer.UseRuntime(Runtime(online, agent));
            EventAddress rawBefore = writer.ReadCurrentHead()!.Value;
            if (expectBackpressure) {
                SessionJournalNotReadyException limited =
                    await Assert.ThrowsAsync<SessionJournalNotReadyException>(
                        () => writer.SendAsync(
                            rawBefore,
                            "offline row cap must stop"));
                Assert.Equal(
                    SessionJournalNotReadyReason.RecapMaintenanceBackpressure,
                    limited.Reason);
                Assert.Contains("TimelineRowLimitExceeded", limited.Message);
                Assert.Equal(rawBefore, writer.ReadCurrentHead());
                Assert.Equal(0, agent.CallCount);
            }
            else {
                _ = await writer.SendAsync(
                    rawBefore,
                    "offline reconcile and build");
                Assert.Equal(1, agent.CallCount);
            }
            Assert.Equal(1, captures);
            TimelineHeadRef after = ReadTimelineHead(writer);
            if (!expectBackpressure) {
                using HistoryTimelineHandle timeline = Assert.IsType<
                    HistoryTimelineOpenResult.Opened>(
                    HistoryTimelineFactory.Open(
                        writer.ReadView, _estimator)).Handle;
                HistoryTimelineSelectedRow selected = Assert.IsType<
                    HistoryTimelineReaderRowResult.Selected>(
                    timeline.Reader.ReadSelectedRow(
                        after,
                        after.HeadRowId!.Value)).Row;
                Assert.Equal(
                    siblingHead,
                    selected.Descriptor.EndInclusive);
            }
            return (before, after);
        }

        (TimelineHeadRef before, TimelineHeadRef baseline) = await RunAsync(
            basePath,
            maximumRows: 64,
            expectBackpressure: false);
        int committedRows = checked((int)(
            baseline.Generation - before.Generation - 1));
        Assert.True(committedRows > 1);

        (_, TimelineHeadRef exact) = await RunAsync(
            exactPath,
            maximumRows: committedRows,
            expectBackpressure: false);
        Assert.Equal(baseline.Generation, exact.Generation);

        (_, TimelineHeadRef overflow) = await RunAsync(
            overflowPath,
            maximumRows: committedRows - 1,
            expectBackpressure: true);
        Assert.Equal(baseline.Generation - 1, overflow.Generation);
    }

    [Fact]
    public async Task OfflineCursorRawFenceDriftBecomesFinalAuthorityMismatchBeforeAgentDispatch() {
        string path = NewPath();
        var agent = new CountingTextCompletionClient();
        SessionJournalEngine? writer = null;
        EventAddress? rewindTo = null;
        EventAddress? expectedAtCapture = null;
        int moved = 0;
        writer = SessionJournalEngine.CreateForTest(
            path,
            new SessionCreateOptions("model", "system", "online"),
            new SessionRuntime(
                agent,
                CompletionTarget: CompletionTarget(),
                ContextCandidateSource: new EmptySource()),
            new SessionJournalTestHooks(
                AfterLifecycleAuditExpectedHeadCaptured: journal => {
                    if (Interlocked.Exchange(ref moved, 1) == 0) {
                        Assert.True(journal.MoveRef(
                            writer!.BranchRefId,
                            expectedAtCapture,
                            rewindTo).Unwrap());
                    }
                }),
            new EventJournalOptions());
        using (writer) {
            for (int index = 0; index < 12; index++) {
                rewindTo = writer.AppendObservation($"observation-{index}");
                expectedAtCapture = writer.AppendImportedAgentAction(
                    new ActionMessage([
                        new ActionBlock.Text($"answer-{index}")
                    ]),
                    new CompletionDescriptor("import", "v1", "model"));
            }
            ProvisionTimelineAndControl(writer, maxRawEvents: 3);
            await using RecapGridOnlineContextHandle online = Assert.IsType<
                RecapGridOnlineOpenResult.Opened>(
                RecapGridOnlineFactory.Open(
                    writer,
                    new RejectingExecutor(),
                    RecapGridOnlineLimits.Production,
                    _estimator)
            ).Handle;
            writer.UseRuntime(Runtime(online, agent));
            EventAddress boundary = writer.ReadCurrentHead()!.Value;

            InvalidOperationException stale =
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => writer.SendAsync(boundary, "must not dispatch"));

            Assert.Contains("stale", stale.Message,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, agent.CallCount);
            Assert.Equal(rewindTo, writer.ReadCurrentHead());
        }
    }

    [Fact]
    public async Task InvalidPhaseAndForeignReadViewFailBeforeMutation() {
        string path = NewPath();
        using SessionJournalEngine writer = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model", "system", "online")
        );
        ProvisionTimelineAndControl(writer);
        var executor = new RejectingExecutor();
        await using RecapGridOnlineContextHandle online = Assert.IsType<
            RecapGridOnlineOpenResult.Opened>(
            RecapGridOnlineFactory.Open(writer, executor,
                RecapGridOnlineLimits.Production, _estimator)
        ).Handle;
        TimelineHeadRef before = ReadTimelineHead(writer);
        EventAddress boundary = writer.ReadCurrentHead()!.Value;

        Assert.Throws<ArgumentException>(() =>
            new SessionContextLifecycleRequest(
                new SessionContextSelectionRequest(boundary, 0),
                SessionExecutionPhase.AwaitingCompletionDispatch,
                SessionContextLifecycleTrigger.PreObservation));

        string otherPath = NewPath();
        using SessionJournalEngine other = SessionJournalEngine.Create(
            otherPath,
            new SessionCreateOptions("model", "system", "other")
        );
        RecapGridOnlinePassResult foreign = await online.PreparePassAsync(
            other.ReadView,
            new SessionContextLifecycleRequest(
                new SessionContextSelectionRequest(boundary, 0),
                SessionExecutionPhase.Idle,
                SessionContextLifecycleTrigger.PreObservation,
                "pending")
        );
        Assert.Equal("RawAuthorityOwnerMismatch", Assert.IsType<
            RecapGridOnlinePassResult.Unavailable>(foreign).Code);
        Assert.Equal(before, ReadTimelineHead(writer));
        Assert.Equal(0, executor.CallCount);
    }

    [Fact]
    public void FactoryRejectsReadOnlyOwnerBeforeOpeningDerivedState() {
        string path = NewPath();
        using (SessionJournalEngine writer = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model", "system", "online"))) { }
        using SessionJournalEngine reader = SessionJournalEngine.OpenReadOnly(path);

        RecapGridOnlineOpenResult.Invalid invalid = Assert.IsType<
            RecapGridOnlineOpenResult.Invalid>(
            RecapGridOnlineFactory.Open(
                reader,
                new RejectingExecutor(),
                RecapGridOnlineLimits.Production,
                _estimator)
        );
        Assert.Equal("MutableSessionJournalRequired", invalid.Code);
    }

    private void ProvisionTimelineAndControl(
        SessionJournalEngine writer,
        int maxRawEvents = 64
    ) {
        Assert.IsType<HistoryTimelineCreateResult.Created>(
            HistoryTimelineFactory.Create(
                writer.ReadView,
                new HistoryTimelineInitialPolicySpec(
                    HistoryPartitionAlgorithms
                        .FirstReplaySafeBoundaryAtTargetV1,
                    O200kBaseHistoryUnitLoadEstimator.EstimatorId,
                    new HistoryLoadUnit(1),
                    maxRawEvents,
                    maxRenderedBytes: 1024 * 1024),
                _estimator)
        );
        var admission = new RecapGridControlAdmission(
            RecapGridControlPermission.Create,
            Array.Empty<FamilyDefinitionDigest>(),
            Array.Empty<string>(),
            Array.Empty<ContextHeaderCarrier>(),
            ["online."],
            maximumBootstrapRows: 64,
            maximumProjectedCalls: 1024
        );
        Assert.IsType<RecapGridControlCreateResult.Created>(
            RecapGridControlFactory.Create(
                writer.Path, writer.BranchRefId, admission)
        );
    }

    private static SessionRuntime Runtime(
        RecapGridOnlineContextHandle online,
        ICompletionClient? client = null
    ) => new(
        client ?? new TextCompletionClient(),
        CompletionTarget: CompletionTarget(),
        ContextCandidateSource: online.CandidateSource,
        ContextLifecycle: online.Lifecycle);

    private static SessionCompletionTargetIdentity CompletionTarget()
        => new("online-tests", "test", "online-tests-v1", "adapter-v1");

    private static void CreateRawHistory(string path, int turns) {
        using SessionJournalEngine writer = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model", "system", "online"));
        for (int index = 0; index < turns; index++) {
            _ = writer.AppendObservation($"observation-{index}");
            _ = writer.AppendImportedAgentAction(
                new ActionMessage([
                    new ActionBlock.Text($"answer-{index}")
                ]),
                new CompletionDescriptor("import", "v1", "model"));
        }
    }

    private static int CountAuditEvents(string path) {
        using SessionJournalEngine reader =
            SessionJournalEngine.OpenReadOnly(path);
        SessionSelectedLineageAuditSession audit =
            reader.BeginSelectedLineageAudit();
        while (!audit.IsCaptureComplete) {
            _ = audit.ReadNextPage(
                SessionSelectedLineageAuditLimits.MaximumPageEventCount);
        }
        return checked((int)audit.Complete().EventCount);
    }

    private TimelineHeadRef ReadTimelineHead(SessionJournalEngine writer) {
        using HistoryTimelineHandle timeline = Assert.IsType<
            HistoryTimelineOpenResult.Opened>(
            HistoryTimelineFactory.Open(writer.ReadView, _estimator)
        ).Handle;
        return Assert.IsType<HistoryTimelineSnapshotResult.Available>(
            timeline.Reader.ReadSnapshot()).Head;
    }

    private static SessionContextLifecycleRequest IdleRequest(
        EventAddress boundary
    ) => new(
        new SessionContextSelectionRequest(boundary, 0),
        SessionExecutionPhase.Idle,
        SessionContextLifecycleTrigger.PreObservation,
        "pending");

    private static (FamilyDefinition, MaintainerDefinitionRevision)
        BuildValues() {
        FamilyDefinition family = FamilyDefinition.Create(
            "Maintain one line of inquiry.",
            [new FamilyToolDefinition(
                "submit",
                "Submit the recap.",
                new FamilyObjectInputSchema([
                    new FamilyToolProperty(
                        "content",
                        new FamilyScalarInputSchema(FamilyScalarType.String),
                        required: true)
                ]))],
            new FamilyOutputProtocol(
                "output-v1",
                "submit",
                FamilyToolChoice.Required,
                allowParallel: false),
            new FamilyInputRenderingProtocol(
                "input-v1", "prior-v1", "history-v1"));
        MaintainerDefinitionRevision definition =
            MaintainerDefinitionRevision.Create(
                new LogicalColumnId("case.culprit"),
                family.Digest,
                new ContextHeaderBlockPath(
                    ContextHeaderCarrier.System, "culprit"),
                new MaintainerCapabilitySpec(
                    "runtime-v1",
                    MaintainerReadableScope
                        .FullPriorBuildTargetAndCurrentHistorySegmentV1),
                new MaintainerDeclarativeSpec(
                    "Who is the culprit?",
                    "Maintain the culprit hypothesis."),
                maxContentUtf8Bytes: 16 * 1024);
        return (family, definition);
    }

    private string NewPath() {
        string path = Path.Combine(
            Directory.Exists("/dev/shm") ? "/dev/shm" : Path.GetTempPath(),
            "atelia-recap-grid-online-tests",
            Guid.NewGuid().ToString("N"));
        _paths.Add(path);
        return path;
    }

    private static void CopyDirectory(string source, string destination) {
        Directory.CreateDirectory(destination);
        foreach (string directory in Directory.EnumerateDirectories(
                     source, "*", SearchOption.AllDirectories)) {
            Directory.CreateDirectory(Path.Combine(
                destination,
                Path.GetRelativePath(source, directory)));
        }
        foreach (string file in Directory.EnumerateFiles(
                     source, "*", SearchOption.AllDirectories)) {
            File.Copy(file, Path.Combine(
                destination,
                Path.GetRelativePath(source, file)));
        }
    }

    public void Dispose() {
        foreach (string path in _paths) {
            if (Directory.Exists(path)) {
                Directory.Delete(path, recursive: true);
            }
        }
    }

    private sealed class RejectingExecutor : IRecapCellBatchExecutor {
        internal int CallCount { get; private set; }
        public ValueTask<RecapCellBatchExecutionResult> ExecuteAsync(
            FrozenRowBatch batch,
            CancellationToken cancellationToken
        ) {
            CallCount++;
            return ValueTask.FromResult<RecapCellBatchExecutionResult>(
                new RecapCellBatchExecutionResult.RejectedBeforeDispatch(
                    "UnexpectedExecution", "No work was expected."));
        }
    }

    private sealed class FillingExecutor : IRecapCellBatchExecutor {
        internal int CallCount { get; private set; }
        public ValueTask<RecapCellBatchExecutionResult> ExecuteAsync(
            FrozenRowBatch batch,
            CancellationToken cancellationToken
        ) {
            CallCount++;
            return ValueTask.FromResult<RecapCellBatchExecutionResult>(
                new RecapCellBatchExecutionResult.Completed([
                    .. batch.OrderedMissingWork.Select(work =>
                        new RecapCellExecutionOutcome.Updated(
                            work.EvaluationKey.Digest,
                            "原来如此，那些疑点就都对得上了。"))
                ]));
        }
    }

    private sealed class BlockingFillingExecutor : IRecapCellBatchExecutor {
        internal TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<RecapCellBatchExecutionResult> ExecuteAsync(
            FrozenRowBatch batch,
            CancellationToken cancellationToken
        ) {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new RecapCellBatchExecutionResult.Completed([
                .. batch.OrderedMissingWork.Select(work =>
                    new RecapCellExecutionOutcome.Updated(
                        work.EvaluationKey.Digest,
                        "原来如此，那些疑点就都对得上了。"))
            ]);
        }
    }

    private sealed class CapturingLifecycle(SessionJournalEngine owner)
        : ISessionContextLifecycleCoordinator {
        internal int CaptureCount { get; private set; }
        internal List<EventAddress> CursorHeads { get; } = [];

        public ValueTask<SessionContextLifecycleResult> PrepareAsync(
            SessionJournalReadView readView,
            SessionContextLifecycleRequest request,
            CancellationToken cancellationToken
        ) {
            var available = Assert.IsType<
                SessionSelectedLineageAuditSnapshotCaptureResult.Available>(
                owner.CaptureSelectedLineageAuditSnapshot(
                    100, cancellationToken)
            );
            using (available.Snapshot)
            using (SessionSelectedLineageForwardCursor cursor =
                   available.Snapshot.OpenForwardCursor(cancellationToken)) {
                Assert.True(cursor.IsBoundTo(
                    owner.Path, owner.BranchRefId, request.Boundary));
                CursorHeads.Add(cursor.ReadCurrentHead()!.Value);
            }
            CaptureCount++;
            return ValueTask.FromResult(
                SessionContextLifecycleResult.RawHistoryAuthorized);
        }
    }

    private sealed class ConcurrentAuditLifecycle(
        Func<SessionJournalEngine> owner,
        ManualResetEventSlim captureEntered,
        ManualResetEventSlim releaseCapture
    ) : ISessionContextLifecycleCoordinator {
        private int _calls;
        internal bool ObservedBusy { get; private set; }
        internal bool ConcurrentMutationRejected { get; private set; }
        internal SessionSelectedLineageAuditSnapshot? SnapshotAfterScope {
            get;
            private set;
        }
        internal SessionSelectedLineageForwardCursor? CursorAfterScope {
            get;
            private set;
        }

        public async ValueTask<SessionContextLifecycleResult> PrepareAsync(
            SessionJournalReadView readView,
            SessionContextLifecycleRequest request,
            CancellationToken cancellationToken
        ) {
            if (Interlocked.Increment(ref _calls) != 1) {
                return SessionContextLifecycleResult.RawHistoryAuthorized;
            }
            Task<SessionSelectedLineageAuditSnapshotCaptureResult> first =
                Task.Run(() => owner().CaptureSelectedLineageAuditSnapshot(
                    100, cancellationToken));
            Assert.True(captureEntered.Wait(TimeSpan.FromSeconds(10)));
            ObservedBusy = owner().CaptureSelectedLineageAuditSnapshot(100)
                is SessionSelectedLineageAuditSnapshotCaptureResult.Busy;
            ConcurrentMutationRejected = await Task.Run(() => {
                try {
                    _ = owner().AppendSystemPromptSetup("must not mutate");
                    return false;
                }
                catch (SessionJournalConcurrentMutationException) {
                    return true;
                }
            });
            releaseCapture.Set();
            var available = Assert.IsType<
                SessionSelectedLineageAuditSnapshotCaptureResult.Available>(
                await first);
            SnapshotAfterScope = available.Snapshot;
            CursorAfterScope = available.Snapshot.OpenForwardCursor(
                cancellationToken);
            Assert.Equal(request.Boundary,
                CursorAfterScope.ReadCurrentHead());
            return SessionContextLifecycleResult.RawHistoryAuthorized;
        }
    }

    private sealed class AuditBoundsLifecycle(
        Func<SessionJournalEngine> owner
    ) : ISessionContextLifecycleCoordinator {
        internal bool ExactCapAvailable { get; private set; }
        internal bool CapMinusOneLimited { get; private set; }
        internal bool CancelledBeforeAuthority { get; private set; }
        internal bool CaptureAfterCancellationAvailable { get; private set; }

        public ValueTask<SessionContextLifecycleResult> PrepareAsync(
            SessionJournalReadView readView,
            SessionContextLifecycleRequest request,
            CancellationToken cancellationToken
        ) {
            var measured = Assert.IsType<
                SessionSelectedLineageAuditSnapshotCaptureResult.Available>(
                owner().CaptureSelectedLineageAuditSnapshot(
                    100, cancellationToken));
            int count = checked((int)((ISessionSelectedLineageAuditPageSnapshot)
                measured.Snapshot).ReadHeadToOldestPages()
                .Sum(static page => page.HeadToOldest.Count));
            measured.Snapshot.Dispose();
            var exact = Assert.IsType<
                SessionSelectedLineageAuditSnapshotCaptureResult.Available>(
                owner().CaptureSelectedLineageAuditSnapshot(
                    count, cancellationToken));
            ExactCapAvailable = true;
            exact.Snapshot.Dispose();
            CapMinusOneLimited = owner()
                .CaptureSelectedLineageAuditSnapshot(count - 1,
                    cancellationToken)
                is SessionSelectedLineageAuditSnapshotCaptureResult
                    .LimitExceeded;
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            try {
                _ = owner().CaptureSelectedLineageAuditSnapshot(
                    count, cancelled.Token);
            }
            catch (OperationCanceledException) {
                CancelledBeforeAuthority = true;
            }
            var afterCancellation = Assert.IsType<
                SessionSelectedLineageAuditSnapshotCaptureResult.Available>(
                owner().CaptureSelectedLineageAuditSnapshot(
                    count, cancellationToken));
            CaptureAfterCancellationAvailable = true;
            afterCancellation.Snapshot.Dispose();
            return ValueTask.FromResult(
                SessionContextLifecycleResult.RawHistoryAuthorized);
        }
    }

    private sealed class TextCompletionClient : ICompletionClient {
        public string Name => "online-tests";
        public string ApiSpecId => "online-tests-v1";
        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(new CompletionResult(
            new ActionMessage([new ActionBlock.Text("answer")]),
            new CompletionDescriptor(Name, ApiSpecId, request.ModelId)
        ));
    }

    private sealed class CountingTextCompletionClient : ICompletionClient {
        internal int CallCount { get; private set; }
        public string Name => "online-tests";
        public string ApiSpecId => "online-tests-v1";
        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            CallCount++;
            return Task.FromResult(new CompletionResult(
                new ActionMessage([new ActionBlock.Text("answer")]),
                new CompletionDescriptor(Name, ApiSpecId, request.ModelId)));
        }
    }

    private sealed class EmptySource : ICoherentContextCandidateSource {
        public ValueTask<SessionContextCandidateSelection> SelectAsync(
            SessionContextSelectionRequest request,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult(new SessionContextCandidateSelection(
            SessionContextCandidateSelectionStatus.EmptyLineage,
            null
        ));

        public ValueTask<SessionContextCandidateMaterializationResult>
            MaterializeAsync(
            SessionContextCandidateDescriptor descriptor,
            CancellationToken cancellationToken
        ) => throw new InvalidOperationException();
    }
}
