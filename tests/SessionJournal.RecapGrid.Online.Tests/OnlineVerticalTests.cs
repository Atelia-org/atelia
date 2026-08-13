using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid;
using Atelia.SessionJournal.RecapGrid.Cadence;
using Atelia.SessionJournal.RecapGrid.Control;
using Atelia.SessionJournal.RecapGrid.Getter;
using Atelia.SessionJournal.RecapGrid.Manager;
using Atelia.SessionJournal.RecapGrid.Store;
using Microsoft.Data.Sqlite;
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
        var executor = new FillingExecutor();
        await using RecapGridOnlineContextHandle online = Assert.IsType<
            RecapGridOnlineOpenResult.Opened>(
            RecapGridOnlineFactory.Open(
                writer,
                executor,
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

        Assert.IsType<RecapGridStoreCreateResult.Created>(
            RecapGridStoreFactory.Create(path));
        EventAddress secondBoundary = writer.ReadCurrentHead()!.Value;
        _ = await writer.SendAsync(secondBoundary, "second observation");

        Assert.Equal(2, agent.CallCount);
        Assert.Equal(1, ReadTimelineHead(writer).Generation);
        Assert.Equal(1, executor.CallCount);
    }

    [Fact]
    public async Task ActiveUnfulfilledRecipeBuildsOnceThenIsIdempotent() {
        string path = NewPath();
        using SessionJournalEngine writer = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model", "system", "online"));
        for (int index = 0; index < 12; index++) {
            _ = writer.AppendObservation(
                $"The behavior of X-{index} is suspicious.");
            _ = writer.AppendImportedAgentAction(
                new ActionMessage([
                    new ActionBlock.Text($"Investigate X-{index}.")
                ]),
                new CompletionDescriptor("import", "v1", "model"));
        }
        ProvisionTimelineAndControl(
            writer,
            maxRawEvents: 3,
            minimumRecentHistoryLoad: 30);
        var executor = new FillingExecutor();
        await using RecapGridOnlineContextHandle online = Assert.IsType<
            RecapGridOnlineOpenResult.Opened>(
            RecapGridOnlineFactory.Open(writer, executor,
                RecapGridOnlineLimits.Production, _estimator)
        ).Handle;
        EventAddress boundary = writer.ReadCurrentHead()!.Value;
        var agent = new CountingTextCompletionClient();
        writer.UseRuntime(Runtime(online, agent));
        boundary = await SendThroughMaintenanceAsync(
            writer, boundary, "seal the offline Timeline");
        Assert.Equal(0, executor.CallCount);

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
        SessionCurrentLineagePrefix prefix = writer.ReadView
            .ReadLineagePrefixAt(
                boundary,
                HistoryRecentReserveOperationLimits.MaximumRawEvents);
        Assert.True(Assert.IsType<
            SessionCurrentLineageAnchorLookup.Found>(
                prefix.Lookup(row.Descriptor.EndInclusive)).Index > 3);
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

        boundary = await SendThroughMaintenanceAsync(
            writer,
            boundary,
            "build recap from the offline-sealed Timeline");
        int firstBuildCalls = executor.CallCount;
        Assert.True(firstBuildCalls > 0);
        Assert.IsType<RecapGridOnlinePassResult.Ready>(
            await online.PreparePassAsync(
                writer.ReadView,
                new SessionContextLifecycleRequest(
                    new SessionContextSelectionRequest(boundary, 0),
                    SessionExecutionPhase.AwaitingAgentAction,
                    SessionContextLifecycleTrigger.ObservationAccepted)));
        Assert.Equal(firstBuildCalls, executor.CallCount);
        using RecapGridContextHandle getter = Assert.IsType<
            RecapGridContextOpenResult.Opened>(
                RecapGridContextFactory.Open(
                    writer.ReadView,
                    _estimator)).Handle;
        Assert.IsType<RecapGridContextResolveResult.Selected>(
            getter.Resolve(boundary, nthPrevious: 0));
    }

    [Theory]
    [InlineData(SessionContextLifecycleTrigger.ObservationAccepted)]
    [InlineData(SessionContextLifecycleTrigger.ToolResultObserved)]
    public async Task ReadinessTriggersBuildOneWithoutSealingAndDisposeDrains(
        SessionContextLifecycleTrigger trigger
    ) {
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

        TimelineHeadRef beforeMaintenance = ReadTimelineHead(writer);
        Task<RecapGridOnlinePassResult> operation = online.PreparePassAsync(
            writer.ReadView,
            new SessionContextLifecycleRequest(
                new SessionContextSelectionRequest(boundary, 0),
                SessionExecutionPhase.AwaitingAgentAction,
                trigger)).AsTask();
        await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsType<RecapGridStorePrepareResetResult.Busy>(
            RecapGridStoreMaintenance.PrepareReset(path));
        Task disposing = online.DisposeAsync().AsTask();
        await Task.Delay(20);
        Assert.False(disposing.IsCompleted);

        executor.Release.TrySetResult();
        RecapGridOnlinePassResult.MaintenanceContinuation maintained =
            Assert.IsType<RecapGridOnlinePassResult.MaintenanceContinuation>(
                await operation.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(maintained.Evidence.EntryDebt);
        Assert.Equal(0, maintained.Evidence.TimelineRowsCommitted);
        Assert.Equal(1, maintained.Evidence.RowViewsCommitted);
        Assert.Equal(0, maintained.Evidence.TimelineRowsCommitted);
        Assert.Equal(beforeMaintenance, ReadTimelineHead(writer));
        await disposing.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsType<RecapGridOnlinePassResult.Disposed>(
            await online.PreparePassAsync(
                writer.ReadView, IdleRequest(boundary)));
        await online.DisposeAsync();

        await using (RecapGridOnlineContextHandle replacement = Assert.IsType<
                         RecapGridOnlineOpenResult.Opened>(
                         RecapGridOnlineFactory.Open(
                             writer,
                             new RejectingExecutor(),
                             RecapGridOnlineLimits.Production,
                             _estimator)).Handle) {
            Assert.IsType<RecapGridOnlinePassResult.Ready>(
                await replacement.PreparePassAsync(
                    writer.ReadView,
                    new SessionContextLifecycleRequest(
                        new SessionContextSelectionRequest(boundary, 0),
                        SessionExecutionPhase.AwaitingAgentAction,
                        trigger)));
        }
        Assert.IsType<RecapGridStorePrepareResetResult.Prepared>(
            RecapGridStoreMaintenance.PrepareReset(path));
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
            RecapGridOnlineLimits.Production.MaximumNewCalls,
            RecapGridOnlineLimits.Production.SoftMaximumElapsed);
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
            RecapGridOnlineLimits.Production.MaximumNewCalls,
            RecapGridOnlineLimits.Production.SoftMaximumElapsed);
        await using (RecapGridOnlineContextHandle exact = Assert.IsType<
                         RecapGridOnlineOpenResult.Opened>(
                         RecapGridOnlineFactory.Open(
                             writer,
                             new RejectingExecutor(),
                             exactLimits,
                             _estimator)
                     ).Handle) {
            writer.UseRuntime(Runtime(exact));
            _ = await SendThroughMaintenanceAsync(
                writer, before, "exact cap can finish the audit");
            Assert.NotNull(ReadTimelineHead(writer).HeadRowId);
            Assert.False(File.Exists(Path.Combine(
                path, "derived", "recap-grid", "v1", "grid.sqlite")));
        }
    }

    [Fact]
    public async Task OnlineSealsAtMostOneTimelineRowPerPass() {
        string path = NewPath();
        CreateRawHistory(path, turns: 2);
        using SessionJournalEngine writer = SessionJournalEngine.Open(path);
        ProvisionTimelineAndControl(writer, maxRawEvents: 64);
        var executor = new RejectingExecutor();
        await using RecapGridOnlineContextHandle online = Assert.IsType<
            RecapGridOnlineOpenResult.Opened>(
            RecapGridOnlineFactory.Open(
                writer,
                executor,
                RecapGridOnlineLimits.Production,
                _estimator)).Handle;
        EventAddress boundary = writer.ReadCurrentHead()!.Value;
        long previousGeneration = ReadTimelineHead(writer).Generation;

        RecapGridOnlinePassResult result;
        do {
            result = await online.PreparePassAsync(
                writer.ReadView, IdleRequest(boundary));
            long generation = ReadTimelineHead(writer).Generation;
            Assert.InRange(generation - previousGeneration, 0, 1);
            previousGeneration = generation;
            if (result is RecapGridOnlinePassResult.MaintenanceContinuation
                    maintenance) {
                Assert.False(maintenance.Evidence.EntryDebt);
                Assert.Equal(1,
                    maintenance.Evidence.TimelineRowsCommitted);
                Assert.Equal(0,
                    maintenance.Evidence.RowViewsCommitted);
            }
        } while (result is RecapGridOnlinePassResult.MaintenanceContinuation);

        Assert.IsType<RecapGridOnlinePassResult.RawHistoryAuthorized>(result);
        Assert.Equal(boundary, writer.ReadCurrentHead());
        Assert.Equal(0, executor.CallCount);
    }

    [Fact]
    public async Task CatchUpFreezesTotalNewCallBudgetAcrossRecipeRows() {
        await using ActiveOnlineFixture fixture = await CreateActiveFixtureAsync(
            turns: 3,
            zeroColumns: false,
            maximumNewCalls: 1,
            maximumElapsed: TimeSpan.FromMinutes(1));

        RecapGridOnlinePassResult.MaintenanceContinuation exhausted =
            Assert.IsType<RecapGridOnlinePassResult.MaintenanceContinuation>(
                await fixture.Online.CatchUpMaintenanceAsync("pending"));

        Assert.Equal("CatchUpNewCallBudgetExhausted", exhausted.Code);
        Assert.Equal(1, fixture.Executor.CallCount);
        Assert.Equal(1, exhausted.Evidence.NewCalls);
        Assert.Equal(1, exhausted.Evidence.RowViewsCommitted);
        Assert.True(exhausted.Evidence.Passes >= 2);
        Assert.NotNull(exhausted.Evidence.LastAttemptedAuthority);
        Assert.NotNull(exhausted.Evidence.NextAuthority);
    }

    [Fact]
    public async Task CatchUpFreezesAbsoluteElapsedBudgetWithoutOverrun() {
        var clock = new ManualTimeProvider();
        await using ActiveOnlineFixture fixture = await CreateActiveFixtureAsync(
            turns: 3,
            zeroColumns: false,
            maximumNewCalls: RecapGridLimits.MaximumColumnCount,
            maximumElapsed: TimeSpan.FromSeconds(1));
        fixture.Online.TimeProviderForTest = clock;
        fixture.Online.OperationHooksForTest = new(
            AfterBuildResult: () => clock.Advance(TimeSpan.FromSeconds(2)));

        RecapGridOnlinePassResult.MaintenanceContinuation exhausted =
            Assert.IsType<RecapGridOnlinePassResult.MaintenanceContinuation>(
                await fixture.Online.CatchUpMaintenanceAsync("pending"));

        Assert.Equal("CatchUpElapsedBudgetExhausted", exhausted.Code);
        Assert.Equal(1, fixture.Executor.CallCount);
        Assert.Equal(1, exhausted.Evidence.NewCalls);
        Assert.Equal(1, exhausted.Evidence.RowViewsCommitted);
    }

    [Fact]
    public async Task CatchUpPassCapStopsBeforeRecipeRow257() {
        string path = NewPath();
        using SessionJournalEngine writer = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model", "system", "online"));
        ProvisionTimelineAndControl(writer);
        await using RecapGridOnlineContextHandle online = Assert.IsType<
            RecapGridOnlineOpenResult.Opened>(RecapGridOnlineFactory.Open(
                writer,
                new RejectingExecutor(),
                RecapGridOnlineLimits.Production,
                _estimator)).Handle;
        int attempted = 0;
        online.OperationHooksForTest = new(
            PreparePassOverride: () => {
                int ordinal = checked(++attempted);
                string digest = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(
                        BitConverter.GetBytes(ordinal))).ToLowerInvariant();
                var coordinate = new RecapGridRecipeRowCoordinate(
                    new HistoryRowId(digest),
                    new GridBuildRecipeDigest(digest));
                return new RecapGridOnlinePassResult.MaintenanceContinuation(
                    RecapGridOnlineComponent.Manager,
                    "GridDebtRemaining",
                    "test-only pass-bound probe",
                    new RecapGridOnlineMaintenanceEvidence(
                        1, true, 0, coordinate, null,
                        1, 1, 0, 0, coordinate, null,
                        RecapGridOnlineContinuationKind.GridDebtRemaining));
            });

        RecapGridOnlinePassResult.MaintenanceContinuation exhausted =
            Assert.IsType<RecapGridOnlinePassResult.MaintenanceContinuation>(
                await online.CatchUpMaintenanceAsync("pending"));

        Assert.Equal("CatchUpPassBudgetExhausted", exhausted.Code);
        Assert.Equal(RecapGridOnlineCatchUpLimits.MaximumPasses,
            exhausted.Evidence.Passes);
        Assert.Equal(RecapGridOnlineCatchUpLimits.MaximumPasses,
            exhausted.Evidence.RecipeRowSteps);
        Assert.Equal(RecapGridOnlineCatchUpLimits.MaximumPasses,
            exhausted.Evidence.RowViewsCommitted);
        Assert.Equal(0, exhausted.Evidence.NewCalls);
        Assert.NotNull(exhausted.Evidence.NextRecipeRow);
        Assert.Equal(RecapGridOnlineCatchUpLimits.MaximumPasses, attempted);
    }

    [Fact]
    public async Task TimelineCommitThenProbeCancellationCarriesMutationEvidence() {
        string path = NewPath();
        using SessionJournalEngine writer = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model", "system", "online"));
        _ = writer.AppendObservation("observation");
        _ = writer.AppendImportedAgentAction(
            new ActionMessage([new ActionBlock.Text("answer")]),
            new CompletionDescriptor("import", "v1", "model"));
        ProvisionTimelineAndControl(writer);
        await using RecapGridOnlineContextHandle online = Assert.IsType<
            RecapGridOnlineOpenResult.Opened>(RecapGridOnlineFactory.Open(
                writer,
                new RejectingExecutor(),
                RecapGridOnlineLimits.Production,
                _estimator)).Handle;
        using var cancelled = new CancellationTokenSource();
        online.OperationHooksForTest = new(
            AfterTimelineCommit: cancelled.Cancel);

        RecapGridOnlinePassResult.Backpressure result = Assert.IsType<
            RecapGridOnlinePassResult.Backpressure>(
                await online.PreparePassAsync(
                    writer.ReadView,
                    IdleRequest(writer.ReadCurrentHead()!.Value),
                    cancelled.Token));

        Assert.Equal("PostMutationCancelled", result.Code);
        Assert.Equal(1, result.MaintenanceEvidence?.TimelineRowsCommitted);
    }

    [Fact]
    public async Task BuildThenInspectCancellationCarriesCommittedMetrics() {
        await using ActiveOnlineFixture fixture = await CreateActiveFixtureAsync(
            turns: 1,
            zeroColumns: false,
            maximumNewCalls: RecapGridLimits.MaximumColumnCount,
            maximumElapsed: TimeSpan.FromMinutes(1));
        using var cancelled = new CancellationTokenSource();
        fixture.Online.OperationHooksForTest = new(
            AfterBuildResult: cancelled.Cancel);

        RecapGridOnlinePassResult.Backpressure result = Assert.IsType<
            RecapGridOnlinePassResult.Backpressure>(
                await fixture.Online.PreparePassAsync(
                    fixture.Writer.ReadView,
                    new SessionContextLifecycleRequest(
                        new SessionContextSelectionRequest(
                            fixture.Writer.ReadCurrentHead()!.Value, 0),
                        SessionExecutionPhase.AwaitingAgentAction,
                        SessionContextLifecycleTrigger.ObservationAccepted),
                    cancelled.Token));

        Assert.Equal("PostMutationCancelled", result.Code);
        Assert.Equal(1, result.MaintenanceEvidence?.RowViewsCommitted);
        Assert.Equal(1, result.MaintenanceEvidence?.NewCalls);
    }

    [Fact]
    public async Task PartialCellsRetryOnlyMissingWorkBeforePublishingView() {
        var executor = new PartialThenFillingExecutor();
        await using CustomActiveOnlineFixture fixture =
            await CreateCustomActiveFixtureAsync(
                turns: 1,
                columnCount: 2,
                nestedOverlays: false,
                executor);
        EventAddress boundary = fixture.Writer.ReadCurrentHead()!.Value;

        RecapGridOnlinePassResult.MaintenanceContinuation partial =
            Assert.IsType<RecapGridOnlinePassResult.MaintenanceContinuation>(
                await fixture.Online.PreparePassAsync(
                    fixture.Writer.ReadView, IdleRequest(boundary)));
        RecapGridOnlineMaintenanceEvidence first = partial.Evidence;
        Assert.Equal([2], executor.OrderedMissingCounts);
        Assert.Equal(2, first.NewCalls);
        Assert.Equal(1, first.CellsCommitted);
        Assert.Equal(0, first.RowViewsCommitted);
        Assert.Equal(0, first.RecipeRowSteps);
        Assert.NotNull(first.LastAttemptedRecipeRow);
        Assert.NotNull(first.NextRecipeRow);

        RecapGridOnlinePassResult.MaintenanceContinuation completed =
            Assert.IsType<RecapGridOnlinePassResult.MaintenanceContinuation>(
                await fixture.Online.PreparePassAsync(
                    fixture.Writer.ReadView, IdleRequest(boundary)));
        Assert.Equal([2, 1], executor.OrderedMissingCounts);
        Assert.Equal(1, completed.Evidence.NewCalls);
        Assert.Equal(1, completed.Evidence.CellsCommitted);
        Assert.Equal(1, completed.Evidence.RowViewsCommitted);
        Assert.Equal(1, completed.Evidence.RecipeRowSteps);
        Assert.NotNull(completed.Evidence.LastAttemptedRecipeRow);
        Assert.Null(completed.Evidence.NextRecipeRow);
        Assert.Equal(
            RecapGridOnlineContinuationKind.GridDebtCleared,
            completed.Evidence.ContinuationKind);
    }

    [Fact]
    public async Task NestedOverlayBuildsExactlyOneAssignmentPerLifecyclePass() {
        var executor = new RecordingFillingExecutor();
        await using CustomActiveOnlineFixture fixture =
            await CreateCustomActiveFixtureAsync(
                turns: 1,
                columnCount: 3,
                nestedOverlays: true,
                executor);
        EventAddress boundary = fixture.Writer.ReadCurrentHead()!.Value;
        var attempted = new List<GridBuildRecipeDigest>();

        for (int ordinal = 0; ordinal < 3; ordinal++) {
            RecapGridOnlinePassResult.MaintenanceContinuation pass =
                Assert.IsType<RecapGridOnlinePassResult
                    .MaintenanceContinuation>(
                        await fixture.Online.PreparePassAsync(
                            fixture.Writer.ReadView,
                            IdleRequest(boundary)));
            Assert.Equal(1, pass.Evidence.NewCalls);
            Assert.Equal(1, pass.Evidence.CellsCommitted);
            Assert.Equal(1, pass.Evidence.RowViewsCommitted);
            Assert.Equal(1, pass.Evidence.RecipeRowSteps);
            attempted.Add(Assert.IsType<RecapGridRecipeRowCoordinate>(
                pass.Evidence.LastAttemptedRecipeRow).RecipeDigest);
        }

        Assert.Equal(fixture.OrderedRecipeDigests, attempted);
        Assert.Equal([1, 1, 1], executor.OrderedMissingCounts);
        Assert.IsType<RecapGridOnlinePassResult.Ready>(
            await fixture.Online.PreparePassAsync(
                fixture.Writer.ReadView, IdleRequest(boundary)));
        Assert.Equal(boundary, fixture.Writer.ReadCurrentHead());
        Assert.Equal(3, executor.CallCount);
    }

    [Fact]
    public async Task FulfilledOnlyDebtPublishesMappingWithoutBuildOrProviderWork() {
        ActiveOnlineFixture fixture = await CreateActiveFixtureAsync(
            turns: 1,
            zeroColumns: false,
            maximumNewCalls: RecapGridLimits.MaximumColumnCount,
            maximumElapsed: TimeSpan.FromMinutes(1));
        string path = fixture.Writer.Path;
        ICountingExecutor executor = fixture.Executor;
        EventAddress boundary;
        TimelineHeadRef timelineBefore;
        FulfilledViewKey key;
        try {
            boundary = fixture.Writer.ReadCurrentHead()!.Value;
            RecapGridOnlinePassResult.MaintenanceContinuation built =
                Assert.IsType<RecapGridOnlinePassResult
                    .MaintenanceContinuation>(
                        await fixture.Online.PreparePassAsync(
                            fixture.Writer.ReadView, IdleRequest(boundary)));
            Assert.Equal(1, built.Evidence.RowViewsCommitted);
            Assert.Equal(1, built.Evidence.NewCalls);

            timelineBefore = ReadTimelineHead(fixture.Writer);
            HistoryTimelineSelectedRow row;
            using (HistoryTimelineHandle timeline = Assert.IsType<
                       HistoryTimelineOpenResult.Opened>(
                       HistoryTimelineFactory.Open(
                           fixture.Writer.ReadView, _estimator)).Handle) {
                row = Assert.IsType<HistoryTimelineReaderRowResult.Selected>(
                    timeline.Reader.ReadSelectedRow(
                        timelineBefore,
                        Assert.IsType<HistoryRowId>(timelineBefore.HeadRowId)))
                    .Row;
            }
            key = FulfilledViewKey.Create(
                fixture.Writer.BranchRefId,
                timelineBefore,
                row.Descriptor.DescriptorDigest,
                fixture.Recipe);
            RemoveFulfillmentForTest(path);
            using RecapGridStoreReaderHandle missingReader = Assert.IsType<
                RecapGridStoreReaderOpenResult.Opened>(
                    RecapGridStoreFactory.OpenReader(path)).Handle;
            Assert.IsType<RecapGridStoreReadResult<
                RecapGridFulfilledView>.Missing>(
                    missingReader.Reader.ReadFulfilled(key));
        }
        finally {
            await fixture.DisposeAsync();
        }

        Dictionary<string, byte[]> rawBefore = SnapshotRawAuthority(path);
        Dictionary<string, byte[]> timelineBytesBefore = SnapshotDirectory(
            Path.Combine(path, "derived", "history-timeline"));
        int providerCallsBefore = executor.CallCount;
        using (SessionJournalEngine writer = SessionJournalEngine.Open(path))
        await using (RecapGridOnlineContextHandle online = Assert.IsType<
                         RecapGridOnlineOpenResult.Opened>(
                         RecapGridOnlineFactory.Open(
                             writer,
                             executor,
                             RecapGridOnlineLimits.Production,
                             _estimator)).Handle) {
            RecapGridOnlinePassResult.MaintenanceContinuation repaired =
                Assert.IsType<RecapGridOnlinePassResult
                    .MaintenanceContinuation>(
                        await online.PreparePassAsync(
                            writer.ReadView, IdleRequest(boundary)));

            Assert.Equal("GridDebtCleared", repaired.Code);
            Assert.Equal(0, repaired.Evidence.NewCalls);
            Assert.Equal(0, repaired.Evidence.CellsCommitted);
            Assert.Equal(0, repaired.Evidence.RowViewsCommitted);
            Assert.Equal(0, repaired.Evidence.RecipeRowSteps);
            Assert.Equal(providerCallsBefore, executor.CallCount);
            Assert.Equal(boundary, writer.ReadCurrentHead());
            Assert.Equal(timelineBefore, ReadTimelineHead(writer));
            using RecapGridStoreReaderHandle repairedReader = Assert.IsType<
                RecapGridStoreReaderOpenResult.Opened>(
                    RecapGridStoreFactory.OpenReader(path)).Handle;
            Assert.IsType<RecapGridStoreReadResult<
                RecapGridFulfilledView>.Found>(
                    repairedReader.Reader.ReadFulfilled(key));
        }
        Assert.Equal(rawBefore, SnapshotRawAuthority(path));
        Assert.Equal(
            timelineBytesBefore,
            SnapshotDirectory(Path.Combine(
                path, "derived", "history-timeline")));
    }

    [Fact]
    public async Task RewindOfflineReconcileAndOneSealShareOneAuditSnapshot() {
        string path = NewPath();
        EventAddress siblingHead;
        using (SessionJournalEngine writer = SessionJournalEngine.Create(
                   path,
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
                EventAddress boundary = writer.ReadCurrentHead()!.Value;
                _ = await SendThroughMaintenanceAsync(
                    writer, boundary, "prime the non-empty Timeline");
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

        int captures = 0;
        using SessionJournalEngine reopened = SessionJournalEngine.OpenForTest(
            path,
            new SessionRuntime(
                new CountingTextCompletionClient(),
                CompletionTarget: CompletionTarget(),
                ContextCandidateSource: new EmptySource()),
            new SessionJournalTestHooks(
                AfterLifecycleAuditExpectedHeadCaptured: _ =>
                    Interlocked.Increment(ref captures)));
        TimelineHeadRef before = ReadTimelineHead(reopened);
        await using RecapGridOnlineContextHandle online = Assert.IsType<
            RecapGridOnlineOpenResult.Opened>(
            RecapGridOnlineFactory.Open(
                reopened,
                new RejectingExecutor(),
                RecapGridOnlineLimits.Production,
                _estimator)).Handle;
        reopened.UseRuntime(Runtime(online));

        SessionJournalNotReadyException first =
            await Assert.ThrowsAsync<SessionJournalNotReadyException>(
                () => reopened.SendAsync(
                    siblingHead, "continue after rewind"));

        Assert.Equal(SessionJournalNotReadyReason
            .RecapMaintenanceBackpressure, first.Reason);
        Assert.Equal(1, captures);
        TimelineHeadRef after = ReadTimelineHead(reopened);
        Assert.InRange(after.Generation - before.Generation, 1, 2);
        Assert.Equal(siblingHead, reopened.ReadCurrentHead());
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
        int maxRawEvents = 64,
        int minimumRecentHistoryLoad = 1
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
        Assert.IsType<RecapGridCadenceCreateResult.Created>(
            RecapGridCadenceFactory.Create(
                writer,
                new RecapGridCadencePolicySpec(
                    minimumRecentHistoryLoad,
                    HistoryPartitionAlgorithms
                        .FirstReplaySafeBoundaryAtTargetV1,
                    O200kBaseHistoryUnitLoadEstimator.EstimatorId,
                    targetHistoryLoad: 1,
                    maxRawEvents,
                    maxRenderedBytes: 1024 * 1024)));
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

    private static async ValueTask<RecapGridOnlinePassResult>
        DrainMaintenanceAsync(
        RecapGridOnlineContextHandle online,
        SessionJournalEngine writer,
        EventAddress boundary
    ) {
        for (int pass = 0; pass < 128; pass++) {
            RecapGridOnlinePassResult result = await online.PreparePassAsync(
                writer.ReadView, IdleRequest(boundary));
            if (result is not RecapGridOnlinePassResult
                    .MaintenanceContinuation maintenance) {
                return result;
            }
            Assert.InRange(maintenance.Evidence.TimelineRowsCommitted, 0, 1);
            Assert.InRange(maintenance.Evidence.RowViewsCommitted, 0, 1);
            Assert.Equal(boundary, writer.ReadCurrentHead());
        }
        throw new Xunit.Sdk.XunitException(
            "Online maintenance did not reach a terminal probe in 128 passes.");
    }

    private async ValueTask<ActiveOnlineFixture> CreateActiveFixtureAsync(
        int turns,
        bool zeroColumns,
        int maximumNewCalls,
        TimeSpan maximumElapsed,
        ICountingExecutor? executorOverride = null
    ) {
        string path = NewPath();
        SessionJournalEngine writer = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model", "system", "online"));
        try {
            for (int index = 0; index < turns; index++) {
                _ = writer.AppendObservation($"observation-{index}");
                _ = writer.AppendImportedAgentAction(
                    new ActionMessage([
                        new ActionBlock.Text($"answer-{index}")
                    ]),
                    new CompletionDescriptor("import", "v1", "model"));
            }
            ProvisionTimelineAndControl(writer, maxRawEvents: 64);
            ICountingExecutor executor = executorOverride
                ?? new FillingExecutor();
            RecapGridOnlineContextHandle online = Assert.IsType<
                RecapGridOnlineOpenResult.Opened>(RecapGridOnlineFactory.Open(
                    writer,
                    executor,
                    new RecapGridOnlineLimits(
                        HistoryRecentReserveOperationLimits.MaximumRawEvents,
                        maximumNewCalls,
                        maximumElapsed),
                    _estimator)).Handle;
            try {
                EventAddress boundary = writer.ReadCurrentHead()!.Value;
                RecapGridOnlinePassResult prime =
                    new RecapGridOnlinePassResult.MaintenanceContinuation(
                        RecapGridOnlineComponent.Timeline,
                        "Prime",
                        "Prime",
                        new RecapGridOnlineMaintenanceEvidence(
                            0, false, 0, null, null, 0, 0, 0, 0,
                            null, null,
                            RecapGridOnlineContinuationKind
                                .TimelineDebtRemaining));
                for (int operation = 0;
                     operation < 4
                        && prime is RecapGridOnlinePassResult
                            .MaintenanceContinuation;
                     operation++) {
                    prime = await online.CatchUpMaintenanceAsync("pending");
                }
                if (prime is not RecapGridOnlinePassResult
                        .RawHistoryAuthorized) {
                    throw new Xunit.Sdk.XunitException(
                        $"Prime maintenance ended with {prime}.");
                }

                TimelineHeadRef timelineHead = ReadTimelineHead(writer);
                using HistoryTimelineHandle timeline = Assert.IsType<
                    HistoryTimelineOpenResult.Opened>(
                    HistoryTimelineFactory.Open(
                        writer.ReadView, _estimator)).Handle;
                var rows = new List<HistoryTimelineSelectedRow>();
                HistoryTimelinePathCursor? cursor = null;
                do {
                    HistoryTimelinePathPage page = Assert.IsType<
                        HistoryTimelinePathPageResult.Page>(
                        timeline.Reader.ReadSelectedPathPage(
                            timelineHead, cursor)).Value;
                    rows.AddRange(page.Rows);
                    cursor = page.Next;
                } while (cursor is not null);
                Assert.True(rows.Count >= turns);
                HistoryTimelineSelectedRow bootstrap = rows[^1];
                Assert.IsType<RecapGridStoreCreateResult.Created>(
                    RecapGridStoreFactory.Create(path));
                (FamilyDefinition family,
                    MaintainerDefinitionRevision definition) = BuildValues();
                BuildTarget target = zeroColumns
                    ? BuildTarget.Create([])
                    : BuildTarget.Create([new BuildTargetColumn(
                        definition.LogicalColumnId,
                        definition.Digest)]);
                GridBuildRecipe recipe = GridBuildRecipe.CreateFull(
                    timelineHead.TimelineId,
                    bootstrap.Descriptor.RowId,
                    target);
                var admission = new RecapGridControlAdmission(
                    RecapGridControlPermission.All,
                    zeroColumns ? [] : [family.Digest],
                    zeroColumns
                        ? []
                        : [definition.Capability.CapabilityFingerprint],
                    zeroColumns ? [] : [ContextHeaderCarrier.System],
                    ["case."],
                    maximumBootstrapRows: 1024,
                    maximumProjectedCalls: 1024);
                using RecapGridControlHandle control = Assert.IsType<
                    RecapGridControlOpenResult.Opened>(
                    RecapGridControlFactory.Open(
                        path, writer.BranchRefId, admission)).Handle;
                ControlHeadRef head = Assert.IsType<
                    RecapGridControlSnapshotResult.Available>(
                    control.Reader.ReadSnapshot()).Snapshot.Head;
                if (!zeroColumns) {
                    head = Assert.IsType<RecapGridControlPutResult.Stored>(
                        control.Coordinator.PutFamilyDefinition(
                            head, family)).Head;
                    head = Assert.IsType<RecapGridControlPutResult.Stored>(
                        control.Coordinator.PutMaintainerDefinition(
                            head, definition)).Head;
                }
                head = Assert.IsType<RecapGridControlPutResult.Stored>(
                    control.Coordinator.PutBuildRecipe(
                        head,
                        timelineHead,
                        recipe,
                        bootstrap.Witness)).Head;
                Assert.IsType<RecapGridControlActivateResult.Applied>(
                    control.Coordinator.CompareExchangeActiveRecipe(
                        head,
                        timelineHead,
                        recipe.Digest,
                        RecapGridControlActivationPurpose.Direct));
                return new ActiveOnlineFixture(
                    writer,
                    online,
                    executor,
                    family,
                    definition,
                    recipe);
            }
            catch {
                await online.DisposeAsync();
                throw;
            }
        }
        catch {
            writer.Dispose();
            throw;
        }
    }

    private async ValueTask<CustomActiveOnlineFixture>
        CreateCustomActiveFixtureAsync(
        int turns,
        int columnCount,
        bool nestedOverlays,
        ICountingExecutor executor
    ) {
        if (columnCount is < 2 or > 3) {
            throw new ArgumentOutOfRangeException(nameof(columnCount));
        }
        ActiveOnlineFixture inner = await CreateActiveFixtureAsync(
            turns,
            zeroColumns: false,
            maximumNewCalls: RecapGridLimits.MaximumColumnCount,
            maximumElapsed: TimeSpan.FromMinutes(1),
            executor);
        try {
            var admission = new RecapGridControlAdmission(
                RecapGridControlPermission.All,
                [inner.Family.Digest],
                [inner.Definition.Capability.CapabilityFingerprint],
                [ContextHeaderCarrier.System],
                ["case."],
                maximumBootstrapRows: 1024,
                maximumProjectedCalls: 1024);
            using RecapGridControlHandle control = Assert.IsType<
                RecapGridControlOpenResult.Opened>(
                    RecapGridControlFactory.Open(
                        inner.Writer.Path,
                        inner.Writer.BranchRefId,
                        admission)).Handle;
            RecapGridControlSnapshot snapshot = Assert.IsType<
                RecapGridControlSnapshotResult.Available>(
                    control.Reader.ReadSnapshot()).Snapshot;
            RegisteredGridRecipe active = Assert.IsType<RegisteredGridRecipe>(
                snapshot.ActiveRecipe);
            GridBuildRecipe baseRecipe = active.Recipe;
            FamilyDefinition family = Assert.Single(snapshot.Families);
            var definitions = new List<MaintainerDefinitionRevision> {
                Assert.Single(snapshot.Definitions)
            };
            ControlHeadRef head = snapshot.Head;
            for (int ordinal = 1; ordinal < columnCount; ordinal++) {
                MaintainerDefinitionRevision definition =
                    CreateDefinition(family, ordinal);
                definitions.Add(definition);
                head = Assert.IsType<RecapGridControlPutResult.Stored>(
                    control.Coordinator.PutMaintainerDefinition(
                        head, definition)).Head;
            }

            using HistoryTimelineHandle timeline = Assert.IsType<
                HistoryTimelineOpenResult.Opened>(HistoryTimelineFactory.Open(
                    inner.Writer.ReadView, _estimator)).Handle;
            HistoryTimelineSelectedRow bootstrap = Assert.IsType<
                HistoryTimelineReaderRowResult.Selected>(
                    timeline.Reader.ReadSelectedRow(
                        active.Bootstrap.TimelineHead,
                        Assert.IsType<HistoryRowId>(active.Bootstrap.RowId)))
                .Row;
            var orderedRecipes = new List<GridBuildRecipe> { baseRecipe };
            GridBuildRecipe finalRecipe;
            if (!nestedOverlays) {
                finalRecipe = GridBuildRecipe.CreateFull(
                    baseRecipe.TimelineId,
                    baseRecipe.BootstrapThroughRowId,
                    BuildTarget.Create(definitions.Select(static definition =>
                        new BuildTargetColumn(
                            definition.LogicalColumnId,
                            definition.Digest))));
                head = Assert.IsType<RecapGridControlPutResult.Stored>(
                    control.Coordinator.PutBuildRecipe(
                        head,
                        active.Bootstrap.TimelineHead,
                        finalRecipe,
                        bootstrap.Witness)).Head;
                orderedRecipes.Clear();
                orderedRecipes.Add(finalRecipe);
            }
            else {
                finalRecipe = baseRecipe;
                for (int ordinal = 1; ordinal < columnCount; ordinal++) {
                    MaintainerDefinitionRevision definition =
                        definitions[ordinal];
                    finalRecipe = GridBuildRecipe.CreateOverlay(
                        finalRecipe,
                        baseRecipe.BootstrapThroughRowId,
                        BuildTarget.Create([
                            .. definitions.Take(ordinal + 1).Select(
                                static value => new BuildTargetColumn(
                                    value.LogicalColumnId,
                                    value.Digest))
                        ]),
                        [definition.LogicalColumnId]);
                    head = Assert.IsType<RecapGridControlPutResult.Stored>(
                        control.Coordinator.PutBuildRecipe(
                            head,
                            active.Bootstrap.TimelineHead,
                            finalRecipe,
                            bootstrap.Witness)).Head;
                    orderedRecipes.Add(finalRecipe);
                }
            }
            Assert.IsType<RecapGridControlActivateResult.Applied>(
                control.Coordinator.CompareExchangeActiveRecipe(
                    head,
                    active.Bootstrap.TimelineHead,
                    finalRecipe.Digest,
                    RecapGridControlActivationPurpose.Direct));
            return new CustomActiveOnlineFixture(
                inner,
                orderedRecipes.Select(static recipe => recipe.Digest)
                    .ToArray());
        }
        catch {
            await inner.DisposeAsync();
            throw;
        }
    }

    private static async ValueTask<EventAddress>
        SendThroughMaintenanceAsync(
        SessionJournalEngine writer,
        EventAddress boundary,
        string observation
    ) {
        for (int pass = 0; pass < 128; pass++) {
            try {
                _ = await writer.SendAsync(boundary, observation);
                return writer.ReadCurrentHead()!.Value;
            }
            catch (SessionJournalNotReadyException exception)
                when (exception.Reason
                    == SessionJournalNotReadyReason
                        .RecapMaintenanceBackpressure) {
                Assert.Equal(boundary, writer.ReadCurrentHead());
            }
        }
        throw new Xunit.Sdk.XunitException(
            "Lifecycle maintenance did not accept the observation in 128 passes.");
    }

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

    private static MaintainerDefinitionRevision CreateDefinition(
        FamilyDefinition family,
        int ordinal
    ) => MaintainerDefinitionRevision.Create(
        new LogicalColumnId($"case.column-{ordinal}"),
        family.Digest,
        new ContextHeaderBlockPath(
            ContextHeaderCarrier.System, $"column-{ordinal}"),
        new MaintainerCapabilitySpec(
            "runtime-v1",
            MaintainerReadableScope
                .FullPriorBuildTargetAndCurrentHistorySegmentV1),
        new MaintainerDeclarativeSpec(
            $"Question {ordinal}?",
            $"Maintain column {ordinal}."),
        maxContentUtf8Bytes: 16 * 1024);

    private string NewPath() {
        string path = Path.Combine(
            Directory.Exists("/dev/shm") ? "/dev/shm" : Path.GetTempPath(),
            "atelia-recap-grid-online-tests",
            Guid.NewGuid().ToString("N"));
        _paths.Add(path);
        return path;
    }

    private static void RemoveFulfillmentForTest(string repository) {
        string database = Path.Combine(
            repository,
            "derived",
            "recap-grid",
            "v1",
            "grid.sqlite");
        var builder = new SqliteConnectionStringBuilder {
            DataSource = database,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM fulfilled_view_ref;";
        Assert.Equal(1, delete.ExecuteNonQuery());
        using SqliteCommand count = connection.CreateCommand();
        count.Transaction = transaction;
        count.CommandText = "UPDATE store_metadata SET "
            + "fulfilled_view_count = fulfilled_view_count - 1;";
        Assert.Equal(1, count.ExecuteNonQuery());
        transaction.Commit();
    }

    private static Dictionary<string, byte[]> SnapshotRawAuthority(
        string repository
    ) {
        var snapshot = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(
                     repository,
                     "*",
                     SearchOption.AllDirectories)) {
            string relative = Path.GetRelativePath(repository, file)
                .Replace(Path.DirectorySeparatorChar, '/');
            if (relative.StartsWith("derived/", StringComparison.Ordinal)
                || relative.StartsWith("control/", StringComparison.Ordinal)) {
                continue;
            }
            snapshot.Add(relative, File.ReadAllBytes(file));
        }
        return snapshot;
    }

    private static Dictionary<string, byte[]> SnapshotDirectory(string root) {
        var snapshot = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(
                     root,
                     "*",
                     SearchOption.AllDirectories)) {
            snapshot.Add(
                Path.GetRelativePath(root, file)
                    .Replace(Path.DirectorySeparatorChar, '/'),
                File.ReadAllBytes(file));
        }
        return snapshot;
    }

    private static void CopyDirectory(string source, string destination) {
        Directory.CreateDirectory(destination);
        if (!OperatingSystem.IsWindows()) {
            File.SetUnixFileMode(destination, File.GetUnixFileMode(source));
        }
        foreach (string directory in Directory.EnumerateDirectories(
                     source, "*", SearchOption.AllDirectories)) {
            string copied = Path.Combine(
                destination,
                Path.GetRelativePath(source, directory));
            Directory.CreateDirectory(copied);
            if (!OperatingSystem.IsWindows()) {
                File.SetUnixFileMode(copied, File.GetUnixFileMode(directory));
            }
        }
        foreach (string file in Directory.EnumerateFiles(
                     source, "*", SearchOption.AllDirectories)) {
            string copied = Path.Combine(
                destination,
                Path.GetRelativePath(source, file));
            File.Copy(file, copied);
            if (!OperatingSystem.IsWindows()) {
                File.SetUnixFileMode(copied, File.GetUnixFileMode(file));
            }
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

    private interface ICountingExecutor : IRecapCellBatchExecutor {
        int CallCount { get; }
    }

    private sealed class FillingExecutor : ICountingExecutor {
        public int CallCount { get; private set; }
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

    private class RecordingFillingExecutor : ICountingExecutor {
        internal List<int> OrderedMissingCounts { get; } = [];
        public int CallCount { get; protected set; }

        public virtual ValueTask<RecapCellBatchExecutionResult> ExecuteAsync(
            FrozenRowBatch batch,
            CancellationToken cancellationToken
        ) {
            CallCount++;
            OrderedMissingCounts.Add(batch.OrderedMissingWork.Count);
            return ValueTask.FromResult<RecapCellBatchExecutionResult>(
                new RecapCellBatchExecutionResult.Completed([
                    .. batch.OrderedMissingWork.Select(work =>
                        new RecapCellExecutionOutcome.Updated(
                            work.EvaluationKey.Digest,
                            $"settled-{CallCount}-{work.Ordinal}"))
                ]));
        }
    }

    private sealed class PartialThenFillingExecutor
        : RecordingFillingExecutor {
        public override ValueTask<RecapCellBatchExecutionResult> ExecuteAsync(
            FrozenRowBatch batch,
            CancellationToken cancellationToken
        ) {
            if (CallCount != 0) {
                return base.ExecuteAsync(batch, cancellationToken);
            }
            _ = base.ExecuteAsync(batch, cancellationToken);
            Assert.Equal(2, batch.OrderedMissingWork.Count);
            return ValueTask.FromResult<RecapCellBatchExecutionResult>(
                new RecapCellBatchExecutionResult.Completed([
                    new RecapCellExecutionOutcome.Updated(
                        batch.OrderedMissingWork[0].EvaluationKey.Digest,
                        "settled-first"),
                    new RecapCellExecutionOutcome.Failed(
                        batch.OrderedMissingWork[1].EvaluationKey.Digest,
                        "fixture-failure",
                        "retry only this cell")
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

    private sealed class ActiveOnlineFixture(
        SessionJournalEngine writer,
        RecapGridOnlineContextHandle online,
        ICountingExecutor executor,
        FamilyDefinition family,
        MaintainerDefinitionRevision definition,
        GridBuildRecipe recipe
    ) : IAsyncDisposable {
        internal SessionJournalEngine Writer { get; } = writer;
        internal RecapGridOnlineContextHandle Online { get; } = online;
        internal ICountingExecutor Executor { get; } = executor;
        internal FamilyDefinition Family { get; } = family;
        internal MaintainerDefinitionRevision Definition { get; } = definition;
        internal GridBuildRecipe Recipe { get; } = recipe;

        public async ValueTask DisposeAsync() {
            try {
                await Online.DisposeAsync();
            }
            finally {
                Writer.Dispose();
            }
        }
    }

    private sealed class CustomActiveOnlineFixture(
        ActiveOnlineFixture inner,
        IReadOnlyList<GridBuildRecipeDigest> orderedRecipeDigests
    ) : IAsyncDisposable {
        internal SessionJournalEngine Writer => inner.Writer;
        internal RecapGridOnlineContextHandle Online => inner.Online;
        internal IReadOnlyList<GridBuildRecipeDigest> OrderedRecipeDigests {
            get;
        } = orderedRecipeDigests;

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class ManualTimeProvider : TimeProvider {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;

        internal void Advance(TimeSpan elapsed) =>
            _timestamp += elapsed.Ticks;
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
