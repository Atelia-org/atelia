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
}
