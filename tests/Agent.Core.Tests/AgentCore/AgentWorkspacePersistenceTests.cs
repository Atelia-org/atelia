using Atelia.Agent.Core;
using Atelia.Agent.Core.History;
using Atelia.Agent.Core.Persistence;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;
using Atelia.StateJournal;
using Xunit;

namespace Agent.Core.Tests;

public sealed class AgentWorkspacePersistenceTests {
    [Fact]
    public void WorkspaceRoot_Create_SeedsMinimalShapeAndCanSelfRead() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-workspace-shape-{Guid.NewGuid():N}");

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var workspaceRoot = AgentWorkspaceRoot.Create(revision);

            Assert.Equal(GetIssue.None, workspaceRoot.Root.Get<string>("kind", out var kind));
            Assert.Equal("agent-engine-state", kind);
            Assert.Equal(GetIssue.None, workspaceRoot.Root.Get<long>("schemaVersion", out var schemaVersion));
            Assert.Equal(2L, schemaVersion);

            Assert.Empty(workspaceRoot.LoadHistory());
            Assert.Empty(workspaceRoot.LoadPendingNotifications());
            Assert.Empty(workspaceRoot.LoadPendingToolResults());
            Assert.Equal((ResolvedProfile: null, LockedCompactionSplitIndex: null), workspaceRoot.LoadTurnRuntime());
            Assert.Null(workspaceRoot.LoadPendingCompaction());
            Assert.Equal(0UL, workspaceRoot.GetRequiredLastSerial());
            Assert.Equal(0L, workspaceRoot.GetToolSessionExecutionSequenceOrDefault());
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public void StateRoot_SaveThenLoad_RoundTripsAllSnapshotFields() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-state-root-{Guid.NewGuid():N}");

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var stateRoot = AgentEngineStateRoot.Create(revision, "bootstrap-system");
            var expected = CreateSnapshotFixture();

            stateRoot.Save(expected);

            var actual = stateRoot.Load();

            Assert.Equal(expected.AgentState.SystemPrompt, actual.AgentState.SystemPrompt);
            Assert.Equal(expected.AgentState.LastSerial, actual.AgentState.LastSerial);
            Assert.Equal(expected.AgentState.PendingNotifications, actual.AgentState.PendingNotifications);
            Assert.Equal(expected.PendingToolResults.Count, actual.PendingToolResults.Count);
            Assert.Equal(expected.ResolvedProfile, actual.ResolvedProfile);
            Assert.Equal(expected.LockedCompactionSplitIndex, actual.LockedCompactionSplitIndex);
            Assert.Equal(expected.PendingCompaction, actual.PendingCompaction);
            Assert.Equal(expected.ToolSessionExecutionSequence, actual.ToolSessionExecutionSequence);

            Assert.Equal(expected.AgentState.RecentHistory.Count, actual.AgentState.RecentHistory.Count);
            for (int i = 0; i < expected.AgentState.RecentHistory.Count; i++) {
                AssertHistoryEntry(expected.AgentState.RecentHistory[i], actual.AgentState.RecentHistory[i]);
            }

            AssertToolCallExecutionResult(expected.PendingToolResults[0], actual.PendingToolResults[0]);
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public void WorkspaceBornState_RestoreAndWriteThroughsNotificationDrainAndRecap() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-workspace-born-{Guid.NewGuid():N}");

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var workspaceRoot = AgentWorkspaceRoot.Create(revision);
            workspaceRoot.SetSystemPrompt("workspace-born-system");

            var state = AgentState.RestoreFromWorkspaceRoot(workspaceRoot);
            state.AttachWorkspaceRoot(workspaceRoot, syncExistingState: false);

            state.SetSystemPrompt("updated-system");
            state.AppendNotification("queued-notification");
            var observation = state.AppendObservation(new ObservationEntry(), "recent-events");
            state.AppendAction(
                new ActionEntry(
                    new ActionMessage([new ActionBlock.Text("assistant-turn")]),
                    new CompletionDescriptor("provider-a", "spec-a", "model-a")
                )
            );
            state.ReplacePrefixWithRecap(1, "summary-text");

            Assert.Equal("updated-system", workspaceRoot.GetRequiredSystemPrompt());
            Assert.Empty(workspaceRoot.LoadPendingNotifications());
            Assert.Equal("queued-notification\nrecent-events", observation.Notifications);

            var history = workspaceRoot.LoadHistory();
            Assert.Equal(2, history.Count);
            var recap = Assert.IsType<RecapEntry>(history[0]);
            Assert.Equal("summary-text", recap.Content);
            Assert.Equal(1UL, recap.InsteadSerial);
            Assert.IsType<ActionEntry>(history[1]);
            Assert.Equal(3UL, workspaceRoot.GetRequiredLastSerial());
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public void Host_StateCoreMutationsUpdateWorkspaceBeforeCommit() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-host-working-state-{Guid.NewGuid():N}");

        try {
            using var host = AgentEngineHost.CreateNew(repoDir);
            host.Engine.State.SetSystemPrompt("updated-before-commit");
            host.Engine.AppendNotification("queued-notification");
            host.Engine.State.AppendObservation(new ObservationEntry(), "recent-events");

            var snapshot = host.StateRoot.Load();

            Assert.Equal("updated-before-commit", snapshot.AgentState.SystemPrompt);
            Assert.Empty(snapshot.AgentState.PendingNotifications);
            var observation = Assert.IsType<ObservationEntry>(Assert.Single(snapshot.AgentState.RecentHistory));
            Assert.Equal("queued-notification\nrecent-events", observation.Notifications);
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public void PublicCreateFromRoot_RemainsSnapshotBasedAndDoesNotLiveWriteThrough() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-public-root-{Guid.NewGuid():N}");

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var stateRoot = AgentEngineStateRoot.Create(revision, "public-root-system");

            var engine = AgentEngine.CreateFromRoot(stateRoot.Root);
            engine.State.SetSystemPrompt("updated-in-memory-only");
            engine.AppendNotification("queued-notification");
            engine.State.AppendObservation(new ObservationEntry(), "recent-events");
            engine.State.AppendAction(
                new ActionEntry(
                    new ActionMessage([new ActionBlock.Text("assistant-turn")]),
                    new CompletionDescriptor("provider-a", "spec-a", "model-a")
                )
            );
            Assert.True(engine.RequestCompaction("compact-system", "compact-now"));

            var persisted = stateRoot.Load();

            Assert.Equal("public-root-system", persisted.AgentState.SystemPrompt);
            Assert.Empty(persisted.AgentState.RecentHistory);
            Assert.Empty(persisted.AgentState.PendingNotifications);
            Assert.Null(persisted.PendingCompaction);
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public void Host_CreateNewThenOpenExisting_RestoresPersistedStateThroughExistingPath() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-host-open-{Guid.NewGuid():N}");

        try {
            using (var host = AgentEngineHost.CreateNew(
                       repoDir,
                       new AgentEngineHostCreateOptions(SystemPrompt: "host-system"))) {
                var invocation = new CompletionDescriptor("provider-a", "spec-a", "model-a");

                host.Engine.State.AppendObservation(new ObservationEntry(), "recent-events");
                host.Engine.State.AppendAction(
                    new ActionEntry(
                        new ActionMessage([new ActionBlock.Text("assistant-turn")]),
                        invocation
                    )
                );
                host.Engine.InjectActionContent(
                    new ActionInjectionRequest(
                        "tail",
                        new InjectionSource(InjectionSourceKind.HostOverride),
                        InjectedActionContentMode.Text
                    )
                );
                host.Engine.AppendNotification("queued-notification");

                host.SaveAndCommit();
            }

            using var reopened = AgentEngineHost.OpenExisting(repoDir);
            var snapshot = reopened.StateRoot.Load();

            Assert.Equal("host-system", reopened.Engine.SystemPrompt);
            Assert.Equal(3, snapshot.AgentState.RecentHistory.Count);
            Assert.Collection(
                snapshot.AgentState.RecentHistory,
                entry => Assert.IsType<ObservationEntry>(entry),
                entry => Assert.IsType<ActionEntry>(entry),
                entry => Assert.IsType<InjectionEntry>(entry)
            );
            Assert.Equal(["queued-notification"], snapshot.AgentState.PendingNotifications);
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public void Host_NotificationDrain_PersistsAcrossReopen() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-host-drain-{Guid.NewGuid():N}");

        try {
            using (var host = AgentEngineHost.CreateNew(repoDir)) {
                host.Engine.AppendNotification("queued-notification");
                host.Engine.State.AppendObservation(new ObservationEntry(), "recent-events");
                host.SaveAndCommit();
            }

            using var reopened = AgentEngineHost.OpenExisting(repoDir);
            var snapshot = reopened.StateRoot.Load();

            Assert.Empty(snapshot.AgentState.PendingNotifications);
            var observation = Assert.IsType<ObservationEntry>(Assert.Single(snapshot.AgentState.RecentHistory));
            Assert.Equal("queued-notification\nrecent-events", observation.Notifications);
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public void Host_Recap_PersistsAcrossReopen() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-host-recap-{Guid.NewGuid():N}");

        try {
            using (var host = AgentEngineHost.CreateNew(repoDir)) {
                host.Engine.State.AppendObservation(new ObservationEntry(), "recent-events");
                host.Engine.State.AppendAction(
                    new ActionEntry(
                        new ActionMessage([new ActionBlock.Text("assistant-turn")]),
                        new CompletionDescriptor("provider-a", "spec-a", "model-a")
                    )
                );
                host.Engine.State.ReplacePrefixWithRecap(1, "summary-text");
                host.SaveAndCommit();
            }

            using var reopened = AgentEngineHost.OpenExisting(repoDir);
            var snapshot = reopened.StateRoot.Load();

            Assert.Equal(2, snapshot.AgentState.RecentHistory.Count);
            var recap = Assert.IsType<RecapEntry>(snapshot.AgentState.RecentHistory[0]);
            Assert.Equal("summary-text", recap.Content);
            Assert.Equal(1UL, recap.InsteadSerial);
            Assert.IsType<ActionEntry>(snapshot.AgentState.RecentHistory[1]);
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public void Host_OpenExisting_RestoresRuntimeOnlyFieldsFromSnapshotCompatibilityLayer() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-host-runtime-reopen-{Guid.NewGuid():N}");

        try {
            var expected = CreateSnapshotFixture();
            var restoredProfile = new LlmProfile(
                new NoopCompletionClient("provider-b", "spec-b"),
                "model-b",
                "profile-b",
                8192,
                CapabilityProfile.FullFeature
            );

            using (var repo = Repository.Create(repoDir).Unwrap()) {
                var revision = repo.CreateBranch("main").Unwrap();
                var stateRoot = AgentEngineStateRoot.Create(revision, "seed-system");
                stateRoot.SaveAndCommit(repo, expected);
            }

            using var reopened = AgentEngineHost.OpenExisting(
                repoDir,
                new AgentEngineHostRuntime(profileRegistry: new LlmProfileRegistry([restoredProfile]))
            );
            var actual = reopened.Engine.ExportStateSnapshot();

            Assert.Equal(expected.AgentState.SystemPrompt, actual.AgentState.SystemPrompt);
            Assert.Equal(expected.AgentState.RecentHistory.Count, actual.AgentState.RecentHistory.Count);
            Assert.Equal(expected.PendingToolResults.Count, actual.PendingToolResults.Count);
            AssertToolCallExecutionResult(expected.PendingToolResults[0], actual.PendingToolResults[0]);
            Assert.Equal(expected.ResolvedProfile, actual.ResolvedProfile);
            Assert.Equal(expected.LockedCompactionSplitIndex, actual.LockedCompactionSplitIndex);
            Assert.Equal(expected.PendingCompaction, actual.PendingCompaction);
            Assert.Equal(expected.ToolSessionExecutionSequence, actual.ToolSessionExecutionSequence);
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Host_RuntimeFieldsWriteThroughDuringStepWithoutSnapshotSaveAtBoundary() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-host-runtime-live-{Guid.NewGuid():N}");

        try {
            var client = new QueueCompletionClient(
                new ActionMessage([
                    new ActionBlock.ToolCall(new RawToolCall("alpha", "call-1", "{}"))
                ])
            );
            var profile = CreateFullFeatureProfile(client, "model-live");
            long? sequenceSeenInsideTool = null;
            AgentEngineHost? liveHost = null;
            RecordingTool? alphaTool = null;

            using (var host = AgentEngineHost.CreateNew(
                       repoDir,
                       runtime: new AgentEngineHostRuntime(initialTools: [
                           alphaTool = new RecordingTool("alpha", context => {
                               sequenceSeenInsideTool = liveHost!.StateRoot.Load().ToolSessionExecutionSequence;
                               Assert.True(context.Session.TryGetTool("alpha", out var sessionTool));
                               Assert.Same(alphaTool, sessionTool);
                           })
                       ]))) {
                liveHost = host;
                host.Engine.State.AppendObservation(new ObservationEntry(), "seed-observation");
                host.Engine.State.AppendAction(
                    new ActionEntry(
                        new ActionMessage([new ActionBlock.Text("seed-action")]),
                        profile.ToCompletionDescriptor()
                    )
                );
                host.Engine.WaitingInput += static (_, args) => {
                    args.ShouldContinue = true;
                    args.Observation = IncomingObservation.FromRecentEvents("fresh-observation");
                };

                await host.StepAsync(profile);
                var afterInitialTurnStart = host.StateRoot.Load();
                Assert.Null(afterInitialTurnStart.ResolvedProfile);
                Assert.Null(afterInitialTurnStart.LockedCompactionSplitIndex);

                await host.StepAsync(profile);
                var afterModelOutput = host.StateRoot.Load();
                Assert.Empty(afterModelOutput.PendingToolResults);
                Assert.Equal(LlmProfileCheckpoint.FromProfile(profile), afterModelOutput.ResolvedProfile);
                Assert.Equal(1, afterModelOutput.LockedCompactionSplitIndex);

                await host.StepAsync(profile);
                Assert.Equal(1L, sequenceSeenInsideTool);
                var afterToolExecution = host.StateRoot.Load();
                var pendingResult = Assert.Single(afterToolExecution.PendingToolResults);
                Assert.Equal("call-1", pendingResult.ToolCallId);
                Assert.Equal(1L, afterToolExecution.ToolSessionExecutionSequence);
            }

            using (var reopened = AgentEngineHost.OpenExisting(
                       repoDir,
                       new AgentEngineHostRuntime(profileRegistry: new LlmProfileRegistry([profile])))) {
                var reopenedAfterToolExecution = reopened.StateRoot.Load();
                var reopenedPendingResult = Assert.Single(reopenedAfterToolExecution.PendingToolResults);
                Assert.Equal("call-1", reopenedPendingResult.ToolCallId);
                Assert.Equal(1L, reopenedAfterToolExecution.ToolSessionExecutionSequence);

                reopened.Engine.WaitingInput += static (_, args) => {
                    args.ShouldContinue = true;
                    args.Observation = IncomingObservation.FromRecentEvents("next-turn-observation");
                };

                await reopened.StepAsync(profile);
                var afterToolResults = reopened.StateRoot.Load();
                Assert.Empty(afterToolResults.PendingToolResults);
                Assert.IsType<ToolResultsEntry>(afterToolResults.AgentState.RecentHistory[^1]);
                Assert.Equal(1L, afterToolResults.ToolSessionExecutionSequence);

                await reopened.StepAsync(profile);
                var afterFinalModelOutput = reopened.StateRoot.Load();
                Assert.Equal(LlmProfileCheckpoint.FromProfile(profile), afterFinalModelOutput.ResolvedProfile);

                await reopened.StepAsync(profile);
                var afterNewTurn = reopened.StateRoot.Load();
                Assert.Null(afterNewTurn.ResolvedProfile);
                Assert.Null(afterNewTurn.LockedCompactionSplitIndex);
            }
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Host_PendingCompactionWriteThroughsRequestAndClear() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-host-compaction-live-{Guid.NewGuid():N}");

        try {
            using var host = AgentEngineHost.CreateNew(repoDir);
            var client = new QueueCompletionClient(
                new ActionMessage([new ActionBlock.Text("summary from live compaction")])
            );
            var profile = CreateFullFeatureProfile(client, "model-compact");

            host.Engine.State.AppendObservation(new ObservationEntry(), "seed-observation");
            host.Engine.State.AppendAction(
                new ActionEntry(
                    new ActionMessage([new ActionBlock.Text("seed-action")]),
                    profile.ToCompletionDescriptor()
                )
            );

            Assert.True(host.Engine.RequestCompaction("compact-system", "compact-now"));
            var requested = host.StateRoot.Load();
            Assert.Equal(new CompactionCheckpoint(1, "compact-system", "compact-now"), requested.PendingCompaction);

            await host.StepAsync(profile);
            var afterCompaction = host.StateRoot.Load();
            Assert.Null(afterCompaction.PendingCompaction);
            var recap = Assert.IsType<RecapEntry>(afterCompaction.AgentState.RecentHistory[0]);
            Assert.Equal("summary from live compaction", recap.Content);
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    private static AgentEngineStateSnapshot CreateSnapshotFixture() {
        var invocation = new CompletionDescriptor("provider-a", "spec-a", "model-a");
        var state = AgentState.CreateDefault("roundtrip-system");
        state.AppendObservation(new ObservationEntry(), "notice-1");
        state.AppendAction(
            new ActionEntry(
                new ActionMessage([
                    new ActionBlock.Text("assistant-hello")
                ]),
                invocation
            )
        );
        state.InjectActionContent(
            new ActionInjectionRequest(
                "continue-here",
                new InjectionSource(InjectionSourceKind.Wizard, "wiz-1", "notes"),
                InjectedActionContentMode.Text
            )
        );
        state.AppendNotification("pending-1");
        state.AppendNotification("pending-2");
        var stateSnapshot = new AgentEngine(state: state).ExportStateSnapshot();

        return new AgentEngineStateSnapshot(
            AgentState: stateSnapshot.AgentState,
            PendingToolResults: [
                new ToolCallExecutionResult(
                    new RawToolCall("gamma", "call-c", "{}"),
                    ToolExecuteResult.FromText(ToolExecutionStatus.Skipped, "pending-tool"),
                    TimeSpan.FromSeconds(3)
                )
            ],
            ResolvedProfile: new LlmProfileCheckpoint("provider-b", "spec-b", "model-b", "profile-b", 8192),
            LockedCompactionSplitIndex: 3,
            PendingCompaction: new CompactionCheckpoint(2, "compact-system", "compact-now"),
            ToolSessionExecutionSequence: 42
        );
    }

    private static void AssertHistoryEntry(HistoryEntry expected, HistoryEntry actual) {
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.Equal(expected.Timestamp, actual.Timestamp);
        Assert.Equal(expected.Serial, actual.Serial);
        Assert.Equal(expected.TokenEstimate, actual.TokenEstimate);

        switch (expected, actual) {
            case (ObservationEntry expectedObservation, ObservationEntry actualObservation)
                when expected is not ToolResultsEntry:
                Assert.Equal(expectedObservation.Notifications, actualObservation.Notifications);
                break;
            case (ActionEntry expectedAction, ActionEntry actualAction):
                Assert.Equal(expectedAction.Invocation, actualAction.Invocation);
                Assert.Equal(
                    ActionMessageSerialization.SerializeBlocks(expectedAction.Message.Blocks),
                    ActionMessageSerialization.SerializeBlocks(actualAction.Message.Blocks)
                );
                break;
            case (InjectionEntry expectedInjection, InjectionEntry actualInjection):
                Assert.Equal(expectedInjection.Content, actualInjection.Content);
                Assert.Equal(expectedInjection.BlockKind, actualInjection.BlockKind);
                Assert.Equal(expectedInjection.Source, actualInjection.Source);
                break;
            case (ToolResultsEntry expectedResults, ToolResultsEntry actualResults):
                Assert.Equal(expectedResults.Notifications, actualResults.Notifications);
                Assert.Equal(expectedResults.Results.Count, actualResults.Results.Count);
                AssertToolCallExecutionResult(expectedResults.Results[0], actualResults.Results[0]);
                break;
            case (RecapEntry expectedRecap, RecapEntry actualRecap):
                Assert.Equal(expectedRecap.Content, actualRecap.Content);
                Assert.Equal(expectedRecap.InsteadSerial, actualRecap.InsteadSerial);
                break;
            default:
                throw new Xunit.Sdk.XunitException($"Unhandled history entry pair: {expected.GetType().Name} vs {actual.GetType().Name}");
        }
    }

    private static void AssertToolCallExecutionResult(ToolCallExecutionResult expected, ToolCallExecutionResult actual) {
        Assert.Equal(expected.RawToolCall, actual.RawToolCall);
        Assert.Equal(expected.ExecuteResult.Status, actual.ExecuteResult.Status);
        Assert.Equal(expected.ExecuteResult.GetFlattenedText(), actual.ExecuteResult.GetFlattenedText());
        Assert.Equal(expected.Elapsed, actual.Elapsed);
    }

    private static LlmProfile CreateFullFeatureProfile(ICompletionClient client, string modelId) {
        return new LlmProfile(client, modelId, $"{modelId}-profile", 4096, CapabilityProfile.FullFeature);
    }

    private sealed class RecordingTool : ITool {
        private readonly Action<ToolExecutionContext>? _onExecute;

        public RecordingTool(string name, Action<ToolExecutionContext>? onExecute = null) {
            _onExecute = onExecute;
            Definition = new ToolDefinition(name, $"Tool {name}.", new ToolSchema.Object());
        }

        public ToolDefinition Definition { get; }

        public ValueTask<ToolExecuteResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken) {
            _ = cancellationToken;
            _onExecute?.Invoke(context);
            return ValueTask.FromResult(ToolExecuteResult.FromText(ToolExecutionStatus.Success, "ok"));
        }
    }

    private sealed class QueueCompletionClient : ICompletionClient {
        private readonly Queue<ActionMessage> _messages;

        public QueueCompletionClient(params ActionMessage[] messages) {
            _messages = new Queue<ActionMessage>(messages);
        }

        public string Name => "test-provider";

        public string ApiSpecId => "test-spec";

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            _ = observer;
            _ = cancellationToken;

            var message = _messages.Count > 0
                ? _messages.Dequeue()
                : new ActionMessage([new ActionBlock.Text("default-output")]);

            return Task.FromResult(
                new CompletionResult(
                    message,
                    new CompletionDescriptor(Name, ApiSpecId, request.ModelId)
                )
            );
        }
    }

    private sealed class NoopCompletionClient : ICompletionClient {
        public NoopCompletionClient(string name, string apiSpecId) {
            Name = name;
            ApiSpecId = apiSpecId;
        }

        public string Name { get; }

        public string ApiSpecId { get; }

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            _ = request;
            _ = observer;
            _ = cancellationToken;
            throw new NotSupportedException("This test client should not be invoked.");
        }
    }
}
