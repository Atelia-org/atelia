using System.Collections.Concurrent;
using System.Reflection;
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
    public void WorkspaceRoot_Create_SeedsMinimalShapeDuringCreationAndCanSelfRead() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-workspace-shape-{Guid.NewGuid():N}");

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var workspaceRoot = AgentWorkspaceRoot.Create(revision, "shape-system");

            Assert.Equal(GetIssue.None, workspaceRoot.Root.Get<string>("kind", out var kind));
            Assert.Equal("agent-engine-state", kind);
            Assert.Equal(GetIssue.None, workspaceRoot.Root.Get<long>("schemaVersion", out var schemaVersion));
            Assert.Equal(3L, schemaVersion);

            Assert.Equal("shape-system", workspaceRoot.Meta.GetRequiredSystemPrompt());
            Assert.Empty(workspaceRoot.History.LoadRecent());
            Assert.Empty(workspaceRoot.History.LoadPendingNotifications());
            Assert.Empty(workspaceRoot.RuntimeState.LoadPendingToolResults());
            Assert.Equal((ResolvedProfile: null, LockedCompactionSplitIndex: null), workspaceRoot.RuntimeState.LoadTurnRuntime());
            Assert.Null(workspaceRoot.RuntimeState.LoadPendingCompaction());
            Assert.Equal(0UL, workspaceRoot.History.GetRequiredLastSerial());
            Assert.Equal(0L, workspaceRoot.RuntimeState.GetToolSessionExecutionSequenceOrDefault());
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public void SnapshotHelper_SaveThenLoad_RoundTripsAllSnapshotFields() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-workspace-snapshot-helper-{Guid.NewGuid():N}");

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var workspaceRoot = AgentWorkspaceRoot.Create(revision, "bootstrap-system");
            var expected = CreateSnapshotFixture();

            AgentEngineWorkspaceSnapshotHelper.SaveSnapshot(workspaceRoot, expected);

            var actual = AgentEngineWorkspaceSnapshotHelper.LoadSnapshot(workspaceRoot);

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
            workspaceRoot.Meta.SetSystemPrompt("workspace-born-system");
            var initialPendingNotificationsDeque = GetPendingNotificationsDeque(workspaceRoot);

            using var session = AgentWorkspaceSession.Open(workspaceRoot);
            var state = session.RestoreState();

            state.SetSystemPrompt("updated-system");
            state.AppendNotification("queued-notification");
            Assert.Same(initialPendingNotificationsDeque, GetPendingNotificationsDeque(workspaceRoot));
            Assert.Equal(["queued-notification"], workspaceRoot.History.LoadPendingNotifications());

            var observation = state.AppendObservation(new ObservationEntry(), "recent-events");
            state.AppendAction(
                new ActionEntry(
                    new ActionMessage([new ActionBlock.Text("assistant-turn")]),
                    new CompletionDescriptor("provider-a", "spec-a", "model-a")
                )
            );
            state.ReplacePrefixWithRecap(1, "summary-text");

            Assert.Equal("updated-system", workspaceRoot.Meta.GetRequiredSystemPrompt());
            Assert.NotSame(initialPendingNotificationsDeque, GetPendingNotificationsDeque(workspaceRoot));
            Assert.Empty(workspaceRoot.History.LoadPendingNotifications());
            Assert.Equal("queued-notification\nrecent-events", observation.Notifications);

            var history = workspaceRoot.History.LoadRecent();
            Assert.Equal(2, history.Count);
            var recap = Assert.IsType<RecapEntry>(history[0]);
            Assert.Equal("summary-text", recap.Content);
            Assert.Equal(1UL, recap.InsteadSerial);
            Assert.IsType<ActionEntry>(history[1]);
            Assert.Equal(3UL, workspaceRoot.History.GetRequiredLastSerial());
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public void WorkspaceLiveAppend_AppendsToExistingDurableHistoryDeque() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-workspace-append-{Guid.NewGuid():N}");

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var workspaceRoot = AgentWorkspaceRoot.Create(revision);
            workspaceRoot.Meta.SetSystemPrompt("workspace-append-system");
            var initialHistoryDeque = GetHistoryDeque(workspaceRoot);

            using var session = AgentWorkspaceSession.Open(workspaceRoot);
            var state = session.RestoreState();

            state.AppendObservation(new ObservationEntry(), "recent-events");
            var afterObservationDeque = GetHistoryDeque(workspaceRoot);
            state.AppendAction(
                new ActionEntry(
                    new ActionMessage([new ActionBlock.Text("assistant-turn")]),
                    new CompletionDescriptor("provider-a", "spec-a", "model-a")
                )
            );
            var afterActionDeque = GetHistoryDeque(workspaceRoot);

            Assert.Same(initialHistoryDeque, afterObservationDeque);
            Assert.Same(initialHistoryDeque, afterActionDeque);
            Assert.Collection(
                workspaceRoot.History.LoadRecent(),
                entry => {
                    var observation = Assert.IsType<ObservationEntry>(entry);
                    Assert.Equal("recent-events", observation.Notifications);
                },
                entry => Assert.IsType<ActionEntry>(entry)
            );
            Assert.Equal(2UL, workspaceRoot.History.GetRequiredLastSerial());

            state.ReplacePrefixWithRecap(1, "summary-text");

            Assert.Same(initialHistoryDeque, GetHistoryDeque(workspaceRoot));
            var history = workspaceRoot.History.LoadRecent();
            var recap = Assert.IsType<RecapEntry>(history[0]);
            Assert.Equal("summary-text", recap.Content);
            Assert.IsType<ActionEntry>(history[1]);
            Assert.Equal(3UL, workspaceRoot.History.GetRequiredLastSerial());
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public void WorkspaceLiveAppendAction_UsesDurableRecentHistoryAsAuthoritativeSource() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-workspace-append-action-authoritative-{Guid.NewGuid():N}");

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var workspaceRoot = AgentWorkspaceRoot.Create(revision);

            using var session = AgentWorkspaceSession.Open(workspaceRoot);
            var state = session.RestoreState();
            state.AppendObservation(new ObservationEntry(), "durable-observation");

            ReplaceCachedRecentHistory(
                state,
                CreateAssignedObservationEntry(999UL),
                CreateAssignedActionEntry(1000UL, "stale-cached-action")
            );
            ReplaceCachedLastSerial(state, 1000UL);

            var appended = state.AppendAction(
                new ActionEntry(
                    new ActionMessage([new ActionBlock.Text("durable-action")]),
                    new CompletionDescriptor("provider-a", "spec-a", "model-a")
                )
            );
            var durableHistory = workspaceRoot.History.LoadRecent();

            Assert.Equal(2UL, appended.Serial);
            Assert.Collection(
                state.RecentHistory,
                entry => {
                    var observation = Assert.IsType<ObservationEntry>(entry);
                    Assert.Equal(1UL, observation.Serial);
                    Assert.Equal("durable-observation", observation.Notifications);
                },
                entry => {
                    var action = Assert.IsType<ActionEntry>(entry);
                    Assert.Equal(2UL, action.Serial);
                    Assert.Equal("durable-action", action.Message.GetFlattenedText());
                }
            );
            Assert.Collection(
                durableHistory,
                entry => {
                    var observation = Assert.IsType<ObservationEntry>(entry);
                    Assert.Equal(1UL, observation.Serial);
                    Assert.Equal("durable-observation", observation.Notifications);
                },
                entry => {
                    var action = Assert.IsType<ActionEntry>(entry);
                    Assert.Equal(2UL, action.Serial);
                    Assert.Equal("durable-action", action.Message.GetFlattenedText());
                }
            );
            Assert.Equal(2UL, GetWorkingSet(state).LastSerial);
            Assert.Equal(2UL, workspaceRoot.History.GetRequiredLastSerial());
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public void WorkspaceLiveAppendAction_FailureRefreshesRecentHistoryCacheFromDurableWorkspace() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-workspace-append-action-failure-refresh-{Guid.NewGuid():N}");

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var workspaceRoot = AgentWorkspaceRoot.Create(revision);

            using var session = AgentWorkspaceSession.Open(workspaceRoot);
            var state = session.RestoreState();
            state.AppendObservation(new ObservationEntry(), "durable-observation");
            state.AppendAction(
                new ActionEntry(
                    new ActionMessage([new ActionBlock.Text("durable-action")]),
                    new CompletionDescriptor("provider-a", "spec-a", "model-a")
                )
            );

            ReplaceCachedRecentHistory(state, CreateAssignedObservationEntry(999UL));
            ReplaceCachedLastSerial(state, 999UL);

            var exception = Assert.Throws<InvalidOperationException>(
                () => state.AppendAction(
                    new ActionEntry(
                        new ActionMessage([new ActionBlock.Text("should-fail")]),
                        new CompletionDescriptor("provider-a", "spec-a", "model-a")
                    )
                )
            );

            Assert.Equal("Illegal history transition. Last=Action, Next=Action", exception.Message);
            Assert.Collection(
                state.RecentHistory,
                entry => {
                    var observation = Assert.IsType<ObservationEntry>(entry);
                    Assert.Equal(1UL, observation.Serial);
                    Assert.Equal("durable-observation", observation.Notifications);
                },
                entry => {
                    var action = Assert.IsType<ActionEntry>(entry);
                    Assert.Equal(2UL, action.Serial);
                    Assert.Equal("durable-action", action.Message.GetFlattenedText());
                }
            );
            Assert.Equal(2UL, GetWorkingSet(state).LastSerial);
            Assert.Equal(2UL, workspaceRoot.History.GetRequiredLastSerial());
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public void WorkspaceSessionAppendAction_ReturnsAuthoritativePreRecentHistoryForDeltaBackfill() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-workspace-session-append-action-snapshot-{Guid.NewGuid():N}");

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var workspaceRoot = AgentWorkspaceRoot.Create(revision);

            using var session = AgentWorkspaceSession.Open(workspaceRoot);
            var state = session.RestoreState();
            state.AppendObservation(new ObservationEntry(), "durable-observation");

            ReplaceCachedRecentHistory(
                state,
                CreateAssignedObservationEntry(999UL),
                CreateAssignedActionEntry(1000UL, "stale-cached-action")
            );
            ReplaceCachedLastSerial(state, 1000UL);

            var appended = new ActionEntry(
                new ActionMessage([new ActionBlock.Text("durable-action")]),
                new CompletionDescriptor("provider-a", "spec-a", "model-a")
            );
            var mutation = session.AppendAction(appended);

            Assert.Equal(2UL, appended.Serial);
            Assert.True(appended.TokenEstimate > 0);
            Assert.Equal(2UL, mutation.LastSerial);
            Assert.Same(appended, mutation.AppendedEntry);
            Assert.Collection(
                mutation.AuthoritativePreRecentHistory,
                entry => {
                    var observation = Assert.IsType<ObservationEntry>(entry);
                    Assert.Equal(1UL, observation.Serial);
                    Assert.Equal("durable-observation", observation.Notifications);
                }
            );
            Assert.Collection(
                workspaceRoot.History.LoadRecent(),
                entry => {
                    var observation = Assert.IsType<ObservationEntry>(entry);
                    Assert.Equal(1UL, observation.Serial);
                    Assert.Equal("durable-observation", observation.Notifications);
                },
                entry => {
                    var action = Assert.IsType<ActionEntry>(entry);
                    Assert.Equal(2UL, action.Serial);
                    Assert.Equal("durable-action", action.Message.GetFlattenedText());
                }
            );
            Assert.Equal(2UL, workspaceRoot.History.GetRequiredLastSerial());
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public void WorkspaceSessionAppendObservation_ReturnsAuthoritativePreStateForDeltaBackfill() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-workspace-session-append-observation-working-set-{Guid.NewGuid():N}");

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var workspaceRoot = AgentWorkspaceRoot.Create(revision);

            using var session = AgentWorkspaceSession.Open(workspaceRoot);
            var state = session.RestoreState();
            state.AppendObservation(new ObservationEntry(), "durable-observation");
            state.AppendAction(
                new ActionEntry(
                    new ActionMessage([new ActionBlock.Text("durable-action")]),
                    new CompletionDescriptor("provider-a", "spec-a", "model-a")
                )
            );
            state.AppendNotification("durable-notification");

            ReplaceCachedRecentHistory(
                state,
                CreateAssignedObservationEntry(999UL),
                CreateAssignedObservationEntry(1000UL)
            );
            ReplaceCachedLastSerial(state, 1000UL);
            ReplaceCachedPendingNotifications(state, "stale-cached-notification");

            var appended = new ObservationEntry();
            var mutation = session.AppendObservation(appended, "follow-up-events");
            var expectedRecentHistory = workspaceRoot.History.LoadRecent();
            var expectedPendingNotifications = workspaceRoot.History.LoadPendingNotifications();
            var expectedLastSerial = workspaceRoot.History.GetRequiredLastSerial();

            Assert.Equal(3UL, appended.Serial);
            Assert.True(appended.TokenEstimate > 0);
            Assert.Equal(expectedLastSerial, mutation.LastSerial);
            Assert.Same(appended, mutation.AppendedEntry);
            Assert.Equal(["durable-notification"], mutation.AuthoritativePrePendingNotifications);
            AssertObservationActionHistory(
                mutation.AuthoritativePreRecentHistory,
                "durable-observation",
                "durable-action"
            );
            AssertObservationActionObservationHistory(
                expectedRecentHistory,
                "durable-observation",
                "durable-action",
                "durable-notification\nfollow-up-events"
            );
            Assert.Empty(workspaceRoot.History.LoadPendingNotifications());
            Assert.Equal(3UL, workspaceRoot.History.GetRequiredLastSerial());
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public void WorkspaceLiveAppendObservation_UsesDurableRecentHistoryForAppendOrderValidation() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-workspace-append-observation-order-{Guid.NewGuid():N}");

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var workspaceRoot = AgentWorkspaceRoot.Create(revision);

            using var session = AgentWorkspaceSession.Open(workspaceRoot);
            var state = session.RestoreState();
            state.AppendObservation(new ObservationEntry(), "durable-observation");
            state.AppendAction(
                new ActionEntry(
                    new ActionMessage([new ActionBlock.Text("durable-action")]),
                    new CompletionDescriptor("provider-a", "spec-a", "model-a")
                )
            );

            ReplaceCachedRecentHistory(
                state,
                CreateAssignedObservationEntry(999UL),
                CreateAssignedObservationEntry(1000UL)
            );
            ReplaceCachedLastSerial(state, 1000UL);

            var appended = state.AppendObservation(new ObservationEntry(), "follow-up-events");

            AssertObservationActionObservationHistory(
                state.RecentHistory,
                "durable-observation",
                "durable-action",
                "follow-up-events"
            );
            AssertObservationActionObservationHistory(
                workspaceRoot.History.LoadRecent(),
                "durable-observation",
                "durable-action",
                "follow-up-events"
            );
            Assert.Equal(3UL, appended.Serial);
            Assert.Equal(3UL, GetWorkingSet(state).LastSerial);
            Assert.Equal(3UL, workspaceRoot.History.GetRequiredLastSerial());
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public void WorkspaceLiveAppendObservation_FailureRefreshesWorkingSetFromDurableWorkspace() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-workspace-append-observation-failure-refresh-{Guid.NewGuid():N}");

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var workspaceRoot = AgentWorkspaceRoot.Create(revision);

            using var session = AgentWorkspaceSession.Open(workspaceRoot);
            var state = session.RestoreState();
            state.AppendObservation(new ObservationEntry(), "durable-observation");
            state.AppendAction(
                new ActionEntry(
                    new ActionMessage([new ActionBlock.Text("durable-action")]),
                    new CompletionDescriptor("provider-a", "spec-a", "model-a")
                )
            );
            state.InjectActionContent(
                new ActionInjectionRequest(
                    "durable-injection",
                    new InjectionSource(InjectionSourceKind.HostOverride),
                    InjectedActionContentMode.Text
                )
            );
            state.AppendNotification("durable-notification");

            ReplaceCachedRecentHistory(
                state,
                CreateAssignedObservationEntry(999UL),
                CreateAssignedActionEntry(1000UL, "stale-cache-action")
            );
            ReplaceCachedLastSerial(state, 1000UL);
            ReplaceCachedPendingNotifications(state, "stale-cached-notification");

            var exception = Assert.Throws<InvalidOperationException>(
                () => state.AppendObservation(new ObservationEntry(), "follow-up-events")
            );

            Assert.Equal("Cannot append observation while a pending action continuation is open.", exception.Message);
            Assert.Collection(
                state.RecentHistory,
                entry => {
                    var observation = Assert.IsType<ObservationEntry>(entry);
                    Assert.Equal(1UL, observation.Serial);
                    Assert.Equal("durable-observation", observation.Notifications);
                },
                entry => {
                    var action = Assert.IsType<ActionEntry>(entry);
                    Assert.Equal(2UL, action.Serial);
                    Assert.Equal("durable-action", action.Message.GetFlattenedText());
                },
                entry => {
                    var injection = Assert.IsType<InjectionEntry>(entry);
                    Assert.Equal(3UL, injection.Serial);
                    Assert.Equal("durable-injection", injection.Content);
                }
            );
            Assert.Equal(["durable-notification"], GetCachedPendingNotifications(state));
            Assert.Equal(3UL, GetWorkingSet(state).LastSerial);
            Assert.Equal(3UL, workspaceRoot.History.GetRequiredLastSerial());
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public void WorkspaceLiveAppendObservation_UsesDurableRecentHistoryForPendingActionGuard() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-workspace-append-observation-guard-{Guid.NewGuid():N}");

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var workspaceRoot = AgentWorkspaceRoot.Create(revision);

            using var session = AgentWorkspaceSession.Open(workspaceRoot);
            var state = session.RestoreState();
            state.AppendObservation(new ObservationEntry(), "durable-observation");
            state.AppendAction(
                new ActionEntry(
                    new ActionMessage([new ActionBlock.Text("durable-action")]),
                    new CompletionDescriptor("provider-a", "spec-a", "model-a")
                )
            );

            ReplaceCachedRecentHistory(
                state,
                CreateAssignedObservationEntry(999UL),
                CreateAssignedInjectionEntry(1000UL, "stale-injection")
            );
            ReplaceCachedLastSerial(state, 1000UL);

            var appended = state.AppendObservation(new ObservationEntry(), "follow-up-events");

            AssertObservationActionObservationHistory(
                state.RecentHistory,
                "durable-observation",
                "durable-action",
                "follow-up-events"
            );
            AssertObservationActionObservationHistory(
                workspaceRoot.History.LoadRecent(),
                "durable-observation",
                "durable-action",
                "follow-up-events"
            );
            Assert.Equal(3UL, appended.Serial);
            Assert.Equal(3UL, GetWorkingSet(state).LastSerial);
            Assert.Equal(3UL, workspaceRoot.History.GetRequiredLastSerial());
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public void WorkspaceSessionAppendToolResults_ReturnsAuthoritativePreStateForDeltaBackfill() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-workspace-session-append-tool-results-working-set-{Guid.NewGuid():N}");

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var workspaceRoot = AgentWorkspaceRoot.Create(revision);

            using var session = AgentWorkspaceSession.Open(workspaceRoot);
            var state = session.RestoreState();
            state.AppendObservation(new ObservationEntry(), "durable-observation");
            state.AppendAction(
                new ActionEntry(
                    new ActionMessage([new ActionBlock.Text("durable-action")]),
                    new CompletionDescriptor("provider-a", "spec-a", "model-a")
                )
            );
            state.AppendNotification("durable-notification");

            ReplaceCachedRecentHistory(
                state,
                CreateAssignedObservationEntry(999UL),
                CreateAssignedInjectionEntry(1000UL, "stale-injection")
            );
            ReplaceCachedLastSerial(state, 1000UL);
            ReplaceCachedPendingNotifications(state, "stale-cached-notification");

            var appended = new ToolResultsEntry([
                CreateToolCallExecutionResult("tool-alpha", "call-1", "tool-output")
            ]);
            var mutation = session.AppendToolResults(appended);
            var expectedRecentHistory = workspaceRoot.History.LoadRecent();
            var expectedLastSerial = workspaceRoot.History.GetRequiredLastSerial();

            Assert.Equal(3UL, appended.Serial);
            Assert.True(appended.TokenEstimate > 0);
            Assert.Equal(expectedLastSerial, mutation.LastSerial);
            Assert.Same(appended, mutation.AppendedEntry);
            Assert.Equal(["durable-notification"], mutation.AuthoritativePrePendingNotifications);
            AssertObservationActionHistory(
                mutation.AuthoritativePreRecentHistory,
                "durable-observation",
                "durable-action"
            );
            AssertObservationActionToolResultsHistory(
                expectedRecentHistory,
                "durable-observation",
                "durable-action",
                "durable-notification",
                "call-1"
            );
            Assert.Empty(workspaceRoot.History.LoadPendingNotifications());
            Assert.Equal(3UL, workspaceRoot.History.GetRequiredLastSerial());
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public void WorkspaceLiveAppendToolResults_FailureRefreshesWorkingSetFromDurableWorkspace() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-workspace-append-tool-results-failure-refresh-{Guid.NewGuid():N}");

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var workspaceRoot = AgentWorkspaceRoot.Create(revision);

            using var session = AgentWorkspaceSession.Open(workspaceRoot);
            var state = session.RestoreState();
            state.AppendObservation(new ObservationEntry(), "durable-observation");
            state.AppendAction(
                new ActionEntry(
                    new ActionMessage([new ActionBlock.Text("durable-action")]),
                    new CompletionDescriptor("provider-a", "spec-a", "model-a")
                )
            );
            state.InjectActionContent(
                new ActionInjectionRequest(
                    "durable-injection",
                    new InjectionSource(InjectionSourceKind.HostOverride),
                    InjectedActionContentMode.Text
                )
            );
            state.AppendNotification("durable-notification");

            ReplaceCachedRecentHistory(
                state,
                CreateAssignedObservationEntry(999UL),
                CreateAssignedActionEntry(1000UL, "stale-cache-action")
            );
            ReplaceCachedLastSerial(state, 1000UL);
            ReplaceCachedPendingNotifications(state, "stale-cached-notification");

            var exception = Assert.Throws<InvalidOperationException>(
                () => state.AppendToolResults(
                    new ToolResultsEntry([
                        CreateToolCallExecutionResult("tool-alpha", "call-1", "tool-output")
                    ])
                )
            );

            Assert.Equal("Cannot append tool results while a pending action continuation is open.", exception.Message);
            Assert.Collection(
                state.RecentHistory,
                entry => {
                    var observation = Assert.IsType<ObservationEntry>(entry);
                    Assert.Equal(1UL, observation.Serial);
                    Assert.Equal("durable-observation", observation.Notifications);
                },
                entry => {
                    var action = Assert.IsType<ActionEntry>(entry);
                    Assert.Equal(2UL, action.Serial);
                    Assert.Equal("durable-action", action.Message.GetFlattenedText());
                },
                entry => {
                    var injection = Assert.IsType<InjectionEntry>(entry);
                    Assert.Equal(3UL, injection.Serial);
                    Assert.Equal("durable-injection", injection.Content);
                }
            );
            Assert.Equal(["durable-notification"], GetCachedPendingNotifications(state));
            Assert.Equal(3UL, GetWorkingSet(state).LastSerial);
            Assert.Equal(3UL, workspaceRoot.History.GetRequiredLastSerial());
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public void WorkspaceLiveAppendToolResults_UsesDurableRecentHistoryAndPendingNotificationsAsAuthoritativeSource() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-workspace-append-tool-results-authoritative-{Guid.NewGuid():N}");

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var workspaceRoot = AgentWorkspaceRoot.Create(revision);

            using var session = AgentWorkspaceSession.Open(workspaceRoot);
            var state = session.RestoreState();
            state.AppendObservation(new ObservationEntry(), "durable-observation");
            state.AppendAction(
                new ActionEntry(
                    new ActionMessage([new ActionBlock.Text("durable-action")]),
                    new CompletionDescriptor("provider-a", "spec-a", "model-a")
                )
            );
            state.AppendNotification("durable-notification");

            ReplaceCachedRecentHistory(
                state,
                CreateAssignedObservationEntry(999UL),
                CreateAssignedInjectionEntry(1000UL, "stale-injection")
            );
            ReplaceCachedLastSerial(state, 1000UL);
            ReplaceCachedPendingNotifications(state, "stale-cached-notification");

            var appended = state.AppendToolResults(
                new ToolResultsEntry([
                    CreateToolCallExecutionResult("tool-alpha", "call-1", "tool-output")
                ])
            );

            AssertObservationActionToolResultsHistory(
                state.RecentHistory,
                "durable-observation",
                "durable-action",
                "durable-notification",
                "call-1"
            );
            AssertObservationActionToolResultsHistory(
                workspaceRoot.History.LoadRecent(),
                "durable-observation",
                "durable-action",
                "durable-notification",
                "call-1"
            );
            Assert.Equal(3UL, appended.Serial);
            Assert.Equal(3UL, GetWorkingSet(state).LastSerial);
            Assert.Equal(3UL, workspaceRoot.History.GetRequiredLastSerial());
            Assert.Empty(workspaceRoot.History.LoadPendingNotifications());
            Assert.Empty(GetCachedPendingNotifications(state));
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public void WorkspaceLiveInjectActionContent_UsesDurableRecentHistoryForPriorActionLookup() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-workspace-inject-prior-action-{Guid.NewGuid():N}");

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var workspaceRoot = AgentWorkspaceRoot.Create(revision);

            using var session = AgentWorkspaceSession.Open(workspaceRoot);
            var state = session.RestoreState();
            state.AppendObservation(new ObservationEntry(), "durable-observation");
            state.AppendAction(
                new ActionEntry(
                    new ActionMessage([new ActionBlock.Text("durable-action")]),
                    new CompletionDescriptor("provider-a", "spec-a", "model-a")
                )
            );

            ReplaceCachedRecentHistory(state);
            ReplaceCachedLastSerial(state, 1000UL);

            var injected = state.InjectActionContent(
                new ActionInjectionRequest(
                    "injected-text",
                    new InjectionSource(InjectionSourceKind.HostOverride),
                    InjectedActionContentMode.Text
                )
            );

            Assert.Equal(3UL, injected.InjectedEntrySerial);
            Assert.Equal(ActionBlockKind.Text, injected.InjectedBlockKind);
            AssertObservationActionInjectionHistory(
                state.RecentHistory,
                "durable-observation",
                "durable-action",
                "injected-text",
                ActionBlockKind.Text
            );
            AssertObservationActionInjectionHistory(
                workspaceRoot.History.LoadRecent(),
                "durable-observation",
                "durable-action",
                "injected-text",
                ActionBlockKind.Text
            );
            Assert.Equal(3UL, GetWorkingSet(state).LastSerial);
            Assert.Equal(3UL, workspaceRoot.History.GetRequiredLastSerial());
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public void WorkspaceLiveInjectActionContent_MatchRecentActionTail_UsesDurableLatestActionTail() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-workspace-inject-tail-kind-{Guid.NewGuid():N}");

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var workspaceRoot = AgentWorkspaceRoot.Create(revision);

            using var session = AgentWorkspaceSession.Open(workspaceRoot);
            var state = session.RestoreState();
            state.AppendObservation(new ObservationEntry(), "durable-observation");
            state.AppendAction(CreateActionEntryWithReasoningTail("durable-action", "durable-thinking"));

            ReplaceCachedRecentHistory(
                state,
                CreateAssignedObservationEntry(999UL),
                CreateAssignedActionEntry(1000UL, "cache-text-tail")
            );
            ReplaceCachedLastSerial(state, 1000UL);

            var injected = state.InjectActionContent(
                new ActionInjectionRequest(
                    "injected-thinking",
                    new InjectionSource(InjectionSourceKind.HostOverride),
                    InjectedActionContentMode.MatchRecentActionTail
                )
            );

            Assert.Equal(3UL, injected.InjectedEntrySerial);
            Assert.Equal(ActionBlockKind.Thinking, injected.InjectedBlockKind);
            AssertObservationActionInjectionHistory(
                state.RecentHistory,
                "durable-observation",
                "durable-action",
                "injected-thinking",
                ActionBlockKind.Thinking
            );
            AssertObservationActionInjectionHistory(
                workspaceRoot.History.LoadRecent(),
                "durable-observation",
                "durable-action",
                "injected-thinking",
                ActionBlockKind.Thinking
            );
            Assert.Equal(3UL, GetWorkingSet(state).LastSerial);
            Assert.Equal(3UL, workspaceRoot.History.GetRequiredLastSerial());
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public void WorkspaceLiveInjectActionContent_UsesDurableTailActionForToolCallGuard() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-workspace-inject-tail-guard-{Guid.NewGuid():N}");

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var workspaceRoot = AgentWorkspaceRoot.Create(revision);

            using var session = AgentWorkspaceSession.Open(workspaceRoot);
            var state = session.RestoreState();
            state.AppendObservation(new ObservationEntry(), "durable-observation");
            state.AppendAction(
                new ActionEntry(
                    new ActionMessage([new ActionBlock.Text("durable-action")]),
                    new CompletionDescriptor("provider-a", "spec-a", "model-a")
                )
            );

            ReplaceCachedRecentHistory(
                state,
                CreateAssignedObservationEntry(999UL),
                CreateAssignedActionEntryWithToolCall(1000UL, "stale-call")
            );
            ReplaceCachedLastSerial(state, 1000UL);

            var injected = state.InjectActionContent(
                new ActionInjectionRequest(
                    "injected-text",
                    new InjectionSource(InjectionSourceKind.HostOverride),
                    InjectedActionContentMode.Text
                )
            );

            Assert.Equal(3UL, injected.InjectedEntrySerial);
            Assert.Equal(ActionBlockKind.Text, injected.InjectedBlockKind);
            AssertObservationActionInjectionHistory(
                state.RecentHistory,
                "durable-observation",
                "durable-action",
                "injected-text",
                ActionBlockKind.Text
            );
            AssertObservationActionInjectionHistory(
                workspaceRoot.History.LoadRecent(),
                "durable-observation",
                "durable-action",
                "injected-text",
                ActionBlockKind.Text
            );
            Assert.Equal(3UL, GetWorkingSet(state).LastSerial);
            Assert.Equal(3UL, workspaceRoot.History.GetRequiredLastSerial());
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public void WorkspaceSessionInjectActionContent_ReturnsAuthoritativePreRecentHistoryForDeltaBackfill() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-workspace-session-inject-state-{Guid.NewGuid():N}");

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var workspaceRoot = AgentWorkspaceRoot.Create(revision);

            using var session = AgentWorkspaceSession.Open(workspaceRoot);
            var state = session.RestoreState();
            state.AppendObservation(new ObservationEntry(), "durable-observation");
            state.AppendAction(CreateActionEntryWithReasoningTail("durable-action", "durable-thinking"));

            ReplaceCachedRecentHistory(
                state,
                CreateAssignedObservationEntry(999UL),
                CreateAssignedActionEntry(1000UL, "stale-cache-action")
            );
            ReplaceCachedLastSerial(state, 1000UL);

            var mutation = session.InjectActionContent(
                new ActionInjectionRequest(
                    "injected-thinking",
                    new InjectionSource(InjectionSourceKind.HostOverride),
                    InjectedActionContentMode.MatchRecentActionTail
                )
            );

            Assert.Equal(3UL, mutation.Result.InjectedEntrySerial);
            Assert.Equal(ActionBlockKind.Thinking, mutation.Result.InjectedBlockKind);
            Assert.Equal(3UL, mutation.LastSerial);
            Assert.Equal("injected-thinking", mutation.AppendedEntry.Content);
            Assert.Equal(ActionBlockKind.Thinking, mutation.AppendedEntry.BlockKind);
            AssertObservationActionHistory(
                mutation.AuthoritativePreRecentHistory,
                "durable-observation",
                "durable-action"
            );
            AssertObservationActionInjectionHistory(
                workspaceRoot.History.LoadRecent(),
                "durable-observation",
                "durable-action",
                "injected-thinking",
                ActionBlockKind.Thinking
            );
            Assert.Equal(3UL, workspaceRoot.History.GetRequiredLastSerial());
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public void WorkspaceLiveAppendAction_AlignedCacheBackfillsDeltaWithoutReplacingPrefixReferences() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-workspace-append-action-delta-{Guid.NewGuid():N}");

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var workspaceRoot = AgentWorkspaceRoot.Create(revision);

            using var session = AgentWorkspaceSession.Open(workspaceRoot);
            var state = session.RestoreState();
            state.AppendObservation(new ObservationEntry(), "durable-observation");

            var originalObservation = Assert.IsType<ObservationEntry>(Assert.Single(state.RecentHistory));
            var appended = state.AppendAction(
                new ActionEntry(
                    new ActionMessage([new ActionBlock.Text("durable-action")]),
                    new CompletionDescriptor("provider-a", "spec-a", "model-a")
                )
            );

            Assert.Same(originalObservation, state.RecentHistory[0]);
            Assert.Same(appended, state.RecentHistory[1]);
            AssertObservationActionHistory(state.RecentHistory, "durable-observation", "durable-action");
            Assert.Equal(2UL, GetWorkingSet(state).LastSerial);
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public void WorkspaceLiveInjectActionContent_AlignedCacheBackfillsDeltaWithoutReplacingPrefixReferences() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-workspace-inject-delta-{Guid.NewGuid():N}");

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var workspaceRoot = AgentWorkspaceRoot.Create(revision);

            using var session = AgentWorkspaceSession.Open(workspaceRoot);
            var state = session.RestoreState();
            state.AppendObservation(new ObservationEntry(), "durable-observation");
            state.AppendAction(CreateActionEntryWithReasoningTail("durable-action", "durable-thinking"));

            var originalObservation = Assert.IsType<ObservationEntry>(state.RecentHistory[0]);
            var originalAction = Assert.IsType<ActionEntry>(state.RecentHistory[1]);
            var injected = state.InjectActionContent(
                new ActionInjectionRequest(
                    "injected-thinking",
                    new InjectionSource(InjectionSourceKind.HostOverride),
                    InjectedActionContentMode.MatchRecentActionTail
                )
            );

            Assert.Same(originalObservation, state.RecentHistory[0]);
            Assert.Same(originalAction, state.RecentHistory[1]);
            var injection = Assert.IsType<InjectionEntry>(state.RecentHistory[2]);
            Assert.Equal(injected.InjectedEntrySerial, injection.Serial);
            Assert.Equal(ActionBlockKind.Thinking, injection.BlockKind);
            Assert.Equal("injected-thinking", injection.Content);
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public void WorkspaceLiveAppendObservation_AlignedCacheBackfillsDeltaWithoutReplacingPrefixReferences() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-workspace-append-observation-delta-{Guid.NewGuid():N}");

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var workspaceRoot = AgentWorkspaceRoot.Create(revision);

            using var session = AgentWorkspaceSession.Open(workspaceRoot);
            var state = session.RestoreState();
            state.AppendObservation(new ObservationEntry(), "durable-observation");
            state.AppendAction(
                new ActionEntry(
                    new ActionMessage([new ActionBlock.Text("durable-action")]),
                    new CompletionDescriptor("provider-a", "spec-a", "model-a")
                )
            );
            state.AppendNotification("durable-notification");

            var originalObservation = Assert.IsType<ObservationEntry>(state.RecentHistory[0]);
            var originalAction = Assert.IsType<ActionEntry>(state.RecentHistory[1]);
            var appended = state.AppendObservation(new ObservationEntry(), "follow-up-events");

            Assert.Same(originalObservation, state.RecentHistory[0]);
            Assert.Same(originalAction, state.RecentHistory[1]);
            Assert.Same(appended, state.RecentHistory[2]);
            Assert.Equal("durable-notification\nfollow-up-events", appended.Notifications);
            Assert.Empty(GetCachedPendingNotifications(state));
            Assert.Equal(3UL, GetWorkingSet(state).LastSerial);
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public void WorkspaceLiveAppendToolResults_AlignedCacheBackfillsDeltaWithoutReplacingPrefixReferences() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-workspace-append-tool-results-delta-{Guid.NewGuid():N}");

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var workspaceRoot = AgentWorkspaceRoot.Create(revision);

            using var session = AgentWorkspaceSession.Open(workspaceRoot);
            var state = session.RestoreState();
            state.AppendObservation(new ObservationEntry(), "durable-observation");
            state.AppendAction(
                new ActionEntry(
                    new ActionMessage([new ActionBlock.Text("durable-action")]),
                    new CompletionDescriptor("provider-a", "spec-a", "model-a")
                )
            );
            state.AppendNotification("durable-notification");

            var originalObservation = Assert.IsType<ObservationEntry>(state.RecentHistory[0]);
            var originalAction = Assert.IsType<ActionEntry>(state.RecentHistory[1]);
            var appended = state.AppendToolResults(
                new ToolResultsEntry([
                    CreateToolCallExecutionResult("tool-alpha", "call-1", "tool-output")
                ])
            );

            Assert.Same(originalObservation, state.RecentHistory[0]);
            Assert.Same(originalAction, state.RecentHistory[1]);
            Assert.Same(appended, state.RecentHistory[2]);
            Assert.Empty(GetCachedPendingNotifications(state));
            AssertObservationActionToolResultsHistory(
                state.RecentHistory,
                "durable-observation",
                "durable-action",
                "durable-notification",
                "call-1"
            );
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public void WorkspaceLiveAppendAction_SoftHistoryDriftFallsBackToWholeReload() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-workspace-append-action-soft-drift-{Guid.NewGuid():N}");

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var workspaceRoot = AgentWorkspaceRoot.Create(revision);

            using var session = AgentWorkspaceSession.Open(workspaceRoot);
            var state = session.RestoreState();
            state.AppendObservation(new ObservationEntry(), "durable-observation");

            var driftedObservation = CreateAssignedObservationEntry(1UL, "drifted-observation");
            ReplaceCachedRecentHistory(state, driftedObservation);
            ReplaceCachedLastSerial(state, 1UL);

            state.AppendAction(
                new ActionEntry(
                    new ActionMessage([new ActionBlock.Text("durable-action")]),
                    new CompletionDescriptor("provider-a", "spec-a", "model-a")
                )
            );

            Assert.NotSame(driftedObservation, state.RecentHistory[0]);
            AssertObservationActionHistory(state.RecentHistory, "durable-observation", "durable-action");
            Assert.Equal(2UL, GetWorkingSet(state).LastSerial);
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public void WorkspaceLiveInjectActionContent_SoftHistoryDriftFallsBackToWholeReload() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-workspace-inject-soft-drift-{Guid.NewGuid():N}");

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var workspaceRoot = AgentWorkspaceRoot.Create(revision);

            using var session = AgentWorkspaceSession.Open(workspaceRoot);
            var state = session.RestoreState();
            state.AppendObservation(new ObservationEntry(), "durable-observation");
            state.AppendAction(CreateActionEntryWithReasoningTail("durable-action", "durable-thinking"));

            var driftedObservation = CreateAssignedObservationEntry(1UL, "drifted-observation");
            var driftedAction = CreateAssignedActionEntry(2UL, "drifted-action");
            ReplaceCachedRecentHistory(state, driftedObservation, driftedAction);
            ReplaceCachedLastSerial(state, 2UL);

            var injected = state.InjectActionContent(
                new ActionInjectionRequest(
                    "injected-thinking",
                    new InjectionSource(InjectionSourceKind.HostOverride),
                    InjectedActionContentMode.MatchRecentActionTail
                )
            );

            Assert.NotSame(driftedObservation, state.RecentHistory[0]);
            Assert.NotSame(driftedAction, state.RecentHistory[1]);
            Assert.Equal(3UL, injected.InjectedEntrySerial);
            Assert.Equal(ActionBlockKind.Thinking, injected.InjectedBlockKind);
            AssertObservationActionInjectionHistory(
                state.RecentHistory,
                "durable-observation",
                "durable-action",
                "injected-thinking",
                ActionBlockKind.Thinking
            );
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public void WorkspaceLiveAppendObservation_SoftPendingNotificationsDriftFallsBackToWholeReload() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-workspace-append-observation-soft-pending-drift-{Guid.NewGuid():N}");

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var workspaceRoot = AgentWorkspaceRoot.Create(revision);

            using var session = AgentWorkspaceSession.Open(workspaceRoot);
            var state = session.RestoreState();
            state.AppendObservation(new ObservationEntry(), "durable-observation");
            state.AppendAction(
                new ActionEntry(
                    new ActionMessage([new ActionBlock.Text("durable-action")]),
                    new CompletionDescriptor("provider-a", "spec-a", "model-a")
                )
            );
            state.AppendNotification("durable-notification");

            var originalObservation = Assert.IsType<ObservationEntry>(state.RecentHistory[0]);
            var originalAction = Assert.IsType<ActionEntry>(state.RecentHistory[1]);
            ReplaceCachedPendingNotifications(state, "drifted-notification");

            var appended = state.AppendObservation(new ObservationEntry(), "follow-up-events");

            Assert.NotSame(originalObservation, state.RecentHistory[0]);
            Assert.NotSame(originalAction, state.RecentHistory[1]);
            Assert.Equal("durable-notification\nfollow-up-events", appended.Notifications);
            Assert.Empty(GetCachedPendingNotifications(state));
            AssertObservationActionObservationHistory(
                state.RecentHistory,
                "durable-observation",
                "durable-action",
                "durable-notification\nfollow-up-events"
            );
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public void WorkspaceSessionInjectActionContent_RejectsWhitespaceContent() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-workspace-session-inject-whitespace-{Guid.NewGuid():N}");

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var workspaceRoot = AgentWorkspaceRoot.Create(revision);

            using var session = AgentWorkspaceSession.Open(workspaceRoot);
            var state = session.RestoreState();
            state.AppendObservation(new ObservationEntry(), "durable-observation");
            state.AppendAction(
                new ActionEntry(
                    new ActionMessage([new ActionBlock.Text("durable-action")]),
                    new CompletionDescriptor("provider-a", "spec-a", "model-a")
                )
            );

            var exception = Assert.Throws<ArgumentException>(
                () => session.InjectActionContent(
                    new ActionInjectionRequest(
                        "   ",
                        new InjectionSource(InjectionSourceKind.HostOverride),
                        InjectedActionContentMode.Text
                    )
                )
            );

            Assert.Equal("request", exception.ParamName);
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public void WorkspaceLiveInjectActionContent_SupportsConsecutiveInjectionsFromDurableRecentHistory() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-workspace-consecutive-injection-{Guid.NewGuid():N}");

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var workspaceRoot = AgentWorkspaceRoot.Create(revision);

            using var session = AgentWorkspaceSession.Open(workspaceRoot);
            var state = session.RestoreState();
            state.AppendObservation(new ObservationEntry(), "durable-observation");
            state.AppendAction(CreateActionEntryWithReasoningTail("durable-action", "durable-thinking"));

            var firstInjection = state.InjectActionContent(
                new ActionInjectionRequest(
                    "first-injection",
                    new InjectionSource(InjectionSourceKind.HostOverride),
                    InjectedActionContentMode.MatchRecentActionTail
                )
            );

            ReplaceCachedRecentHistory(
                state,
                CreateAssignedObservationEntry(999UL),
                CreateAssignedActionEntry(1000UL, "stale-cache-action"),
                CreateAssignedInjectionEntry(1001UL, "stale-cache-injection")
            );
            ReplaceCachedLastSerial(state, 1001UL);

            var secondInjection = state.InjectActionContent(
                new ActionInjectionRequest(
                    "second-injection",
                    new InjectionSource(InjectionSourceKind.HostOverride),
                    InjectedActionContentMode.MatchRecentActionTail
                )
            );

            Assert.Equal(ActionBlockKind.Thinking, firstInjection.InjectedBlockKind);
            Assert.Equal(ActionBlockKind.Thinking, secondInjection.InjectedBlockKind);
            Assert.Equal(3UL, firstInjection.InjectedEntrySerial);
            Assert.Equal(4UL, secondInjection.InjectedEntrySerial);
            Assert.Collection(
                state.RecentHistory,
                entry => {
                    var observation = Assert.IsType<ObservationEntry>(entry);
                    Assert.Equal(1UL, observation.Serial);
                    Assert.Equal("durable-observation", observation.Notifications);
                },
                entry => {
                    var action = Assert.IsType<ActionEntry>(entry);
                    Assert.Equal(2UL, action.Serial);
                    Assert.Equal("durable-action", action.Message.GetFlattenedText());
                },
                entry => {
                    var injection = Assert.IsType<InjectionEntry>(entry);
                    Assert.Equal(3UL, injection.Serial);
                    Assert.Equal("first-injection", injection.Content);
                    Assert.Equal(ActionBlockKind.Thinking, injection.BlockKind);
                },
                entry => {
                    var injection = Assert.IsType<InjectionEntry>(entry);
                    Assert.Equal(4UL, injection.Serial);
                    Assert.Equal("second-injection", injection.Content);
                    Assert.Equal(ActionBlockKind.Thinking, injection.BlockKind);
                }
            );
            Assert.Equal(4UL, GetWorkingSet(state).LastSerial);
            Assert.Equal(4UL, workspaceRoot.History.GetRequiredLastSerial());
            Assert.Collection(
                workspaceRoot.History.LoadRecent(),
                entry => Assert.IsType<ObservationEntry>(entry),
                entry => Assert.IsType<ActionEntry>(entry),
                entry => Assert.IsType<InjectionEntry>(entry),
                entry => Assert.IsType<InjectionEntry>(entry)
            );
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public void WorkspaceLiveInjectActionContent_FailureRefreshesRecentHistoryCacheFromDurableWorkspace() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-workspace-injection-failure-refresh-{Guid.NewGuid():N}");

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var workspaceRoot = AgentWorkspaceRoot.Create(revision);

            using var session = AgentWorkspaceSession.Open(workspaceRoot);
            var state = session.RestoreState();
            state.AppendObservation(new ObservationEntry(), "durable-observation");
            state.AppendAction(
                new ActionEntry(
                    new ActionMessage([
                        new ActionBlock.ToolCall(new RawToolCall("tool-durable", "durable-tool-call", "{}"))
                    ]),
                    new CompletionDescriptor("provider-a", "spec-a", "model-a")
                )
            );

            ReplaceCachedRecentHistory(
                state,
                CreateAssignedObservationEntry(999UL),
                CreateAssignedActionEntry(1000UL, "stale-cache-action")
            );
            ReplaceCachedLastSerial(state, 1000UL);

            var exception = Assert.Throws<InvalidOperationException>(
                () => state.InjectActionContent(
                    new ActionInjectionRequest(
                        "should-fail",
                        new InjectionSource(InjectionSourceKind.HostOverride),
                        InjectedActionContentMode.Text
                    )
                )
            );

            Assert.Equal(
                "Cannot inject after trailing action because the trailing ActionEntry already contains tool calls. Injection only supports assistant content before tool execution begins.",
                exception.Message
            );
            Assert.Equal(2UL, GetWorkingSet(state).LastSerial);
            Assert.Equal(2UL, workspaceRoot.History.GetRequiredLastSerial());
            Assert.Collection(
                state.RecentHistory,
                entry => {
                    var observation = Assert.IsType<ObservationEntry>(entry);
                    Assert.Equal(1UL, observation.Serial);
                    Assert.Equal("durable-observation", observation.Notifications);
                },
                entry => {
                    var action = Assert.IsType<ActionEntry>(entry);
                    Assert.Equal(2UL, action.Serial);
                    Assert.Equal("durable-tool-call", Assert.Single(action.Message.ToolCalls).ToolCallId);
                }
            );
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public void WorkspaceLiveInjectActionContent_AllowsExplicitModeAfterRecapInjectionShape() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-workspace-recap-injection-{Guid.NewGuid():N}");

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var workspaceRoot = AgentWorkspaceRoot.Create(revision);

            using var session = AgentWorkspaceSession.Open(workspaceRoot);
            var state = session.RestoreState();
            state.AppendObservation(new ObservationEntry(), "first-observation");
            state.AppendAction(
                new ActionEntry(
                    new ActionMessage([new ActionBlock.Text("first-action")]),
                    new CompletionDescriptor("provider-a", "spec-a", "model-a")
                )
            );
            state.AppendObservation(new ObservationEntry(), "second-observation");
            state.InjectActionContent(
                new ActionInjectionRequest(
                    "seed-injection",
                    new InjectionSource(InjectionSourceKind.HostOverride),
                    InjectedActionContentMode.Text
                )
            );
            state.ReplacePrefixWithRecap(3, "summary-text");

            var injected = state.InjectActionContent(
                new ActionInjectionRequest(
                    "after-recap",
                    new InjectionSource(InjectionSourceKind.HostOverride),
                    InjectedActionContentMode.Thinking
                )
            );

            Assert.Equal(6UL, injected.InjectedEntrySerial);
            Assert.Equal(ActionBlockKind.Thinking, injected.InjectedBlockKind);
            Assert.True(state.HasPendingActionContinuation);
            Assert.Equal(6UL, GetWorkingSet(state).LastSerial);
            Assert.Equal(6UL, workspaceRoot.History.GetRequiredLastSerial());
            Assert.Collection(
                state.RecentHistory,
                entry => {
                    var recap = Assert.IsType<RecapEntry>(entry);
                    Assert.Equal(5UL, recap.Serial);
                    Assert.Equal("summary-text", recap.Content);
                    Assert.Equal(3UL, recap.InsteadSerial);
                },
                entry => {
                    var injection = Assert.IsType<InjectionEntry>(entry);
                    Assert.Equal(4UL, injection.Serial);
                    Assert.Equal("seed-injection", injection.Content);
                    Assert.Equal(ActionBlockKind.Text, injection.BlockKind);
                },
                entry => {
                    var injection = Assert.IsType<InjectionEntry>(entry);
                    Assert.Equal(6UL, injection.Serial);
                    Assert.Equal("after-recap", injection.Content);
                    Assert.Equal(ActionBlockKind.Thinking, injection.BlockKind);
                }
            );
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public void WorkspaceLiveInjectActionContent_MatchRecentActionTail_RejectsRecapInjectionShapeWithoutActionEntry() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-workspace-recap-injection-match-tail-{Guid.NewGuid():N}");

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var workspaceRoot = AgentWorkspaceRoot.Create(revision);

            using var session = AgentWorkspaceSession.Open(workspaceRoot);
            var state = session.RestoreState();
            state.AppendObservation(new ObservationEntry(), "first-observation");
            state.AppendAction(
                new ActionEntry(
                    new ActionMessage([new ActionBlock.Text("first-action")]),
                    new CompletionDescriptor("provider-a", "spec-a", "model-a")
                )
            );
            state.AppendObservation(new ObservationEntry(), "second-observation");
            state.InjectActionContent(
                new ActionInjectionRequest(
                    "seed-injection",
                    new InjectionSource(InjectionSourceKind.HostOverride),
                    InjectedActionContentMode.Text
                )
            );
            state.ReplacePrefixWithRecap(3, "summary-text");

            var exception = Assert.Throws<InvalidOperationException>(
                () => state.InjectActionContent(
                    new ActionInjectionRequest(
                        "should-fail",
                        new InjectionSource(InjectionSourceKind.HostOverride),
                        InjectedActionContentMode.MatchRecentActionTail
                    )
                )
            );

            Assert.Equal("Cannot inject action content because no prior ActionEntry exists in history.", exception.Message);
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public void WorkspaceLiveNotificationDrain_UsesDurableWorkspaceAsAuthoritativeSource() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-workspace-drain-authoritative-{Guid.NewGuid():N}");

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var workspaceRoot = AgentWorkspaceRoot.Create(revision);

            using var session = AgentWorkspaceSession.Open(workspaceRoot);
            var state = session.RestoreState();
            state.AppendNotification("durable-notification");
            Assert.Equal(["durable-notification"], workspaceRoot.History.LoadPendingNotifications());

            ReplaceCachedPendingNotifications(state, "stale-cached-notification");

            var observation = state.AppendObservation(new ObservationEntry(), "recent-events");

            Assert.Equal("durable-notification\nrecent-events", observation.Notifications);
            Assert.Empty(workspaceRoot.History.LoadPendingNotifications());
            Assert.Empty(GetCachedPendingNotifications(state));
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public void WorkspaceSessionAppendPendingNotification_ReturnsUpdatedPendingNotificationSnapshotForTargetedRefresh() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-workspace-session-append-notification-snapshot-{Guid.NewGuid():N}");

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var workspaceRoot = AgentWorkspaceRoot.Create(revision);

            using var session = AgentWorkspaceSession.Open(workspaceRoot);
            var state = session.RestoreState();
            state.AppendNotification("durable-notification-1");

            ReplaceCachedPendingNotifications(state, "stale-cached-notification");

            var pendingNotifications = session.AppendPendingNotification("durable-notification-2");
            var freshPendingNotifications = session.LoadPendingNotifications();

            Assert.Equal(freshPendingNotifications, pendingNotifications);
            Assert.Equal(["durable-notification-1", "durable-notification-2"], pendingNotifications);
            Assert.Equal(["durable-notification-1", "durable-notification-2"], workspaceRoot.History.LoadPendingNotifications());
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public void WorkspaceSessionSetSystemPrompt_ReturnsUpdatedDurablePrompt() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-workspace-session-set-prompt-{Guid.NewGuid():N}");

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var workspaceRoot = AgentWorkspaceRoot.Create(revision, "initial-system");

            using var session = AgentWorkspaceSession.Open(workspaceRoot);
            var state = session.RestoreState();

            var updatedPrompt = session.SetSystemPrompt("updated-system");
            state.SetSystemPrompt("state-updated-system");

            Assert.Equal("updated-system", updatedPrompt);
            Assert.Equal("state-updated-system", state.SystemPrompt);
            Assert.Equal("state-updated-system", workspaceRoot.Meta.GetRequiredSystemPrompt());
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public void Host_AppendNotification_RefreshesPendingNotificationCacheFromDurableWorkspace() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-host-append-notification-authoritative-{Guid.NewGuid():N}");

        try {
            using var host = AgentEngineHost.CreateNew(repoDir);
            host.Engine.AppendNotification("durable-notification-1");

            ReplaceCachedPendingNotifications(host.Engine.State);
            Assert.False(host.Engine.State.HasPendingNotification);
            Assert.Equal(["durable-notification-1"], host.WorkspaceRoot.History.LoadPendingNotifications());

            host.Engine.AppendNotification("durable-notification-2");

            var expectedNotifications = new[] { "durable-notification-1", "durable-notification-2" };
            var durablePendingNotifications = host.LoadDurablePendingNotifications();

            Assert.True(host.Engine.State.HasPendingNotification);
            Assert.Equal(expectedNotifications, GetCachedPendingNotifications(host.Engine.State));
            Assert.Equal(expectedNotifications, host.WorkspaceRoot.History.LoadPendingNotifications());
            Assert.Equal(expectedNotifications, durablePendingNotifications);
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public void WorkspaceLiveRecap_AlignedCacheUsesTargetedDeltaBackfill() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-workspace-recap-delta-backfill-{Guid.NewGuid():N}");

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var workspaceRoot = AgentWorkspaceRoot.Create(revision);
            var initialHistoryDeque = GetHistoryDeque(workspaceRoot);

            using var session = AgentWorkspaceSession.Open(workspaceRoot);
            var state = session.RestoreState();
            state.AppendObservation(new ObservationEntry(), "durable-observation");
            var action = state.AppendAction(
                new ActionEntry(
                    new ActionMessage([new ActionBlock.Text("durable-action")]),
                    new CompletionDescriptor("provider-a", "spec-a", "model-a")
                )
            );

            var recap = state.ReplacePrefixWithRecap(1, "summary-text");
            var history = workspaceRoot.History.LoadRecent();

            Assert.Same(initialHistoryDeque, GetHistoryDeque(workspaceRoot));
            Assert.Equal(1UL, recap.InsteadSerial);
            Assert.Equal(3UL, recap.Serial);
            Assert.Same(recap, state.RecentHistory[0]);
            Assert.Same(action, state.RecentHistory[1]);
            Assert.Collection(
                history,
                entry => {
                    var persistedRecap = Assert.IsType<RecapEntry>(entry);
                    Assert.Equal("summary-text", persistedRecap.Content);
                    Assert.Equal(1UL, persistedRecap.InsteadSerial);
                    Assert.Equal(3UL, persistedRecap.Serial);
                },
                entry => {
                    var action = Assert.IsType<ActionEntry>(entry);
                    Assert.Equal(2UL, action.Serial);
                    Assert.Equal("durable-action", action.Message.GetFlattenedText());
                }
            );
            Assert.Equal(3UL, workspaceRoot.History.GetRequiredLastSerial());
            Assert.Equal(3UL, GetWorkingSet(state).LastSerial);
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public void WorkspaceLiveRecap_SoftHistoryDriftFallsBackToWholeReload() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-workspace-recap-soft-drift-{Guid.NewGuid():N}");

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var workspaceRoot = AgentWorkspaceRoot.Create(revision);

            using var session = AgentWorkspaceSession.Open(workspaceRoot);
            var state = session.RestoreState();
            state.AppendObservation(new ObservationEntry(), "durable-observation");
            state.AppendAction(
                new ActionEntry(
                    new ActionMessage([new ActionBlock.Text("durable-action")]),
                    new CompletionDescriptor("provider-a", "spec-a", "model-a")
                )
            );

            var driftedObservation = CreateAssignedObservationEntry(1UL, "drifted-observation");
            var driftedAction = CreateAssignedActionEntry(2UL, "drifted-action");
            ReplaceCachedRecentHistory(state, driftedObservation, driftedAction);
            ReplaceCachedLastSerial(state, 2UL);

            var recap = state.ReplacePrefixWithRecap(1, "summary-text");

            Assert.NotSame(recap, state.RecentHistory[0]);
            Assert.NotSame(driftedAction, state.RecentHistory[1]);
            AssertRecapActionHistory(state.RecentHistory, "summary-text", 1UL, "durable-action");
            var reloadedRecap = Assert.IsType<RecapEntry>(state.RecentHistory[0]);
            Assert.Equal(1UL, reloadedRecap.InsteadSerial);
            Assert.Equal(3UL, reloadedRecap.Serial);
            Assert.Equal(3UL, GetWorkingSet(state).LastSerial);
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public void WorkspaceLiveRecap_FaultReloadsRecentHistoryCacheFromDurableWorkspace() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-workspace-recap-fault-reload-{Guid.NewGuid():N}");

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var workspaceRoot = AgentWorkspaceRoot.Create(revision);

            using var session = AgentWorkspaceSession.Open(workspaceRoot);
            var state = session.RestoreState();
            state.AppendObservation(new ObservationEntry(), "durable-observation");
            state.AppendAction(
                new ActionEntry(
                    new ActionMessage([new ActionBlock.Text("durable-action")]),
                    new CompletionDescriptor("provider-a", "spec-a", "model-a")
                )
            );

            ConfigureSessionFaultToThrowOnce(
                session,
                AgentWorkspaceSessionFaultPoint.AfterReplacePrefixWithRecapMutation,
                "Injected fault after recap rewrite.",
                beforeThrow: () => {
                    ReplaceCachedRecentHistory(
                        state,
                        CreateAssignedObservationEntry(999UL, "ghost-observation"),
                        CreateAssignedActionEntry(1000UL, "ghost-action")
                    );
                    ReplaceCachedLastSerial(state, 1000UL);
                }
            );

            var exception = Assert.Throws<InvalidOperationException>(() => state.ReplacePrefixWithRecap(1, "summary-text"));

            Assert.Equal("Injected fault after recap rewrite.", exception.Message);
            AssertRecapActionHistory(workspaceRoot.History.LoadRecent(), "summary-text", 1UL, "durable-action");
            AssertRecapActionHistory(state.RecentHistory, "summary-text", 1UL, "durable-action");
            var localRecap = Assert.IsType<RecapEntry>(state.RecentHistory[0]);
            Assert.Equal(1UL, localRecap.InsteadSerial);
            Assert.Equal(3UL, localRecap.Serial);
            Assert.Equal(3UL, GetWorkingSet(state).LastSerial);
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public void WorkspaceLiveRecap_MidRewriteFaultRollsBackDurableAndLocalRecentHistory() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-workspace-recap-mid-rewrite-rollback-{Guid.NewGuid():N}");

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var workspaceRoot = AgentWorkspaceRoot.Create(revision);

            using var session = AgentWorkspaceSession.Open(workspaceRoot);
            var state = session.RestoreState();
            state.AppendObservation(new ObservationEntry(), "first-observation");
            state.AppendAction(
                new ActionEntry(
                    new ActionMessage([new ActionBlock.Text("first-action")]),
                    new CompletionDescriptor("provider-a", "spec-a", "model-a")
                )
            );
            state.AppendObservation(new ObservationEntry(), "second-observation");
            state.InjectActionContent(
                new ActionInjectionRequest(
                    "seed-injection",
                    new InjectionSource(InjectionSourceKind.HostOverride),
                    InjectedActionContentMode.Text
                )
            );

            ConfigureSessionFaultToThrowOnce(
                session,
                AgentWorkspaceSessionFaultPoint.AfterReplacePrefixWithRecapFrontPopMutation,
                "Injected fault during recap front-pop rewrite.",
                beforeThrow: () => {
                    ReplaceCachedRecentHistory(
                        state,
                        CreateAssignedObservationEntry(999UL, "ghost-observation"),
                        CreateAssignedActionEntry(1000UL, "ghost-action")
                    );
                    ReplaceCachedLastSerial(state, 1000UL);
                }
            );

            var exception = Assert.Throws<InvalidOperationException>(() => state.ReplacePrefixWithRecap(3, "summary-text"));

            Assert.Equal("Injected fault during recap front-pop rewrite.", exception.Message);
            Assert.Collection(
                workspaceRoot.History.LoadRecent(),
                entry => {
                    var observation = Assert.IsType<ObservationEntry>(entry);
                    Assert.Equal(1UL, observation.Serial);
                    Assert.Equal("first-observation", observation.Notifications);
                },
                entry => {
                    var action = Assert.IsType<ActionEntry>(entry);
                    Assert.Equal(2UL, action.Serial);
                    Assert.Equal("first-action", action.Message.GetFlattenedText());
                },
                entry => {
                    var observation = Assert.IsType<ObservationEntry>(entry);
                    Assert.Equal(3UL, observation.Serial);
                    Assert.Equal("second-observation", observation.Notifications);
                },
                entry => {
                    var injection = Assert.IsType<InjectionEntry>(entry);
                    Assert.Equal(4UL, injection.Serial);
                    Assert.Equal("seed-injection", injection.Content);
                    Assert.Equal(ActionBlockKind.Text, injection.BlockKind);
                }
            );
            Assert.Collection(
                state.RecentHistory,
                entry => {
                    var observation = Assert.IsType<ObservationEntry>(entry);
                    Assert.Equal(1UL, observation.Serial);
                    Assert.Equal("first-observation", observation.Notifications);
                },
                entry => {
                    var action = Assert.IsType<ActionEntry>(entry);
                    Assert.Equal(2UL, action.Serial);
                    Assert.Equal("first-action", action.Message.GetFlattenedText());
                },
                entry => {
                    var observation = Assert.IsType<ObservationEntry>(entry);
                    Assert.Equal(3UL, observation.Serial);
                    Assert.Equal("second-observation", observation.Notifications);
                },
                entry => {
                    var injection = Assert.IsType<InjectionEntry>(entry);
                    Assert.Equal(4UL, injection.Serial);
                    Assert.Equal("seed-injection", injection.Content);
                    Assert.Equal(ActionBlockKind.Text, injection.BlockKind);
                }
            );
            Assert.Equal(4UL, workspaceRoot.History.GetRequiredLastSerial());
            Assert.Equal(4UL, GetWorkingSet(state).LastSerial);
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public void WorkspaceLocalTailRewrite_TruncateLeavesSerialGapAndNextAppendStaysMonotonic() {
        var state = AgentState.CreateDefault("tail-rewrite-local");
        state.AppendObservation(new ObservationEntry(), "first-observation");
        state.AppendAction(
            new ActionEntry(
                new ActionMessage([new ActionBlock.Text("first-action")]),
                new CompletionDescriptor("provider-a", "spec-a", "model-a")
            )
        );
        state.AppendObservation(new ObservationEntry(), "second-observation");
        state.AppendAction(
            new ActionEntry(
                new ActionMessage([new ActionBlock.Text("second-action")]),
                new CompletionDescriptor("provider-a", "spec-a", "model-a")
            )
        );

        InvokeRewriteRecentHistoryTail(state, 2UL);

        AssertObservationActionHistory(state.RecentHistory, "first-observation", "first-action");
        Assert.Equal(4UL, GetWorkingSet(state).LastSerial);

        var appended = state.AppendObservation(new ObservationEntry(), "after-truncate");

        Assert.Equal(5UL, appended.Serial);
        Assert.Collection(
            state.RecentHistory,
            entry => {
                var observation = Assert.IsType<ObservationEntry>(entry);
                Assert.Equal(1UL, observation.Serial);
                Assert.Equal("first-observation", observation.Notifications);
            },
            entry => {
                var action = Assert.IsType<ActionEntry>(entry);
                Assert.Equal(2UL, action.Serial);
                Assert.Equal("first-action", action.Message.GetFlattenedText());
            },
            entry => {
                var observation = Assert.IsType<ObservationEntry>(entry);
                Assert.Equal(5UL, observation.Serial);
                Assert.Equal("after-truncate", observation.Notifications);
            }
        );
        Assert.Equal(5UL, GetWorkingSet(state).LastSerial);
    }

    [Fact]
    public void WorkspaceLiveTailRewrite_AlignedCacheUsesTargetedDeltaBackfill() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-workspace-tail-rewrite-delta-backfill-{Guid.NewGuid():N}");

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var workspaceRoot = AgentWorkspaceRoot.Create(revision);
            var initialHistoryDeque = GetHistoryDeque(workspaceRoot);

            using var session = AgentWorkspaceSession.Open(workspaceRoot);
            var state = session.RestoreState();
            state.AppendObservation(new ObservationEntry(), "first-observation");
            state.AppendAction(
                new ActionEntry(
                    new ActionMessage([new ActionBlock.Text("first-action")]),
                    new CompletionDescriptor("provider-a", "spec-a", "model-a")
                )
            );
            state.AppendObservation(new ObservationEntry(), "second-observation");
            state.AppendAction(
                new ActionEntry(
                    new ActionMessage([new ActionBlock.Text("second-action")]),
                    new CompletionDescriptor("provider-a", "spec-a", "model-a")
                )
            );

            var replacementObservation = new ObservationEntry();
            replacementObservation.AssignNotifications("rewritten-observation");
            var replacementAction = new ActionEntry(
                new ActionMessage([new ActionBlock.Text("rewritten-action")]),
                new CompletionDescriptor("provider-a", "spec-a", "model-a")
            );

            InvokeRewriteRecentHistoryTail(state, 2UL, replacementObservation, replacementAction);

            Assert.Same(initialHistoryDeque, GetHistoryDeque(workspaceRoot));
            Assert.Same(replacementObservation, state.RecentHistory[2]);
            Assert.Same(replacementAction, state.RecentHistory[3]);
            AssertObservationActionObservationActionHistory(
                workspaceRoot.History.LoadRecent(),
                "first-observation",
                "first-action",
                5UL,
                "rewritten-observation",
                6UL,
                "rewritten-action"
            );
            AssertObservationActionObservationActionHistory(
                state.RecentHistory,
                "first-observation",
                "first-action",
                5UL,
                "rewritten-observation",
                6UL,
                "rewritten-action"
            );
            Assert.Equal(6UL, GetWorkingSet(state).LastSerial);
            Assert.Equal(6UL, workspaceRoot.History.GetRequiredLastSerial());
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public void WorkspaceLiveTailRewrite_SoftHistoryDriftFallsBackToWholeReload() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-workspace-tail-rewrite-soft-drift-{Guid.NewGuid():N}");

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var workspaceRoot = AgentWorkspaceRoot.Create(revision);

            using var session = AgentWorkspaceSession.Open(workspaceRoot);
            var state = session.RestoreState();
            state.AppendObservation(new ObservationEntry(), "first-observation");
            state.AppendAction(
                new ActionEntry(
                    new ActionMessage([new ActionBlock.Text("first-action")]),
                    new CompletionDescriptor("provider-a", "spec-a", "model-a")
                )
            );
            state.AppendObservation(new ObservationEntry(), "second-observation");
            state.AppendAction(
                new ActionEntry(
                    new ActionMessage([new ActionBlock.Text("second-action")]),
                    new CompletionDescriptor("provider-a", "spec-a", "model-a")
                )
            );

            ReplaceCachedRecentHistory(
                state,
                CreateAssignedObservationEntry(1UL, "drifted-first-observation"),
                CreateAssignedActionEntry(2UL, "drifted-first-action"),
                CreateAssignedObservationEntry(3UL, "drifted-second-observation"),
                CreateAssignedActionEntry(4UL, "drifted-second-action")
            );
            ReplaceCachedLastSerial(state, 4UL);

            var replacementObservation = new ObservationEntry();
            replacementObservation.AssignNotifications("rewritten-observation");
            var replacementAction = new ActionEntry(
                new ActionMessage([new ActionBlock.Text("rewritten-action")]),
                new CompletionDescriptor("provider-a", "spec-a", "model-a")
            );

            InvokeRewriteRecentHistoryTail(state, 2UL, replacementObservation, replacementAction);

            Assert.NotSame(replacementObservation, state.RecentHistory[2]);
            Assert.NotSame(replacementAction, state.RecentHistory[3]);
            AssertObservationActionObservationActionHistory(
                state.RecentHistory,
                "first-observation",
                "first-action",
                5UL,
                "rewritten-observation",
                6UL,
                "rewritten-action"
            );
            Assert.Equal(6UL, GetWorkingSet(state).LastSerial);
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public void WorkspaceLiveTailRewrite_MidRewriteFaultRollsBackDurableAndLocalRecentHistory() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-workspace-tail-rewrite-mid-rollback-{Guid.NewGuid():N}");

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var workspaceRoot = AgentWorkspaceRoot.Create(revision);

            using var session = AgentWorkspaceSession.Open(workspaceRoot);
            var state = session.RestoreState();
            state.AppendObservation(new ObservationEntry(), "first-observation");
            state.AppendAction(
                new ActionEntry(
                    new ActionMessage([new ActionBlock.Text("first-action")]),
                    new CompletionDescriptor("provider-a", "spec-a", "model-a")
                )
            );
            state.AppendObservation(new ObservationEntry(), "second-observation");
            state.AppendAction(
                new ActionEntry(
                    new ActionMessage([new ActionBlock.Text("second-action")]),
                    new CompletionDescriptor("provider-a", "spec-a", "model-a")
                )
            );

            ConfigureSessionFaultToThrowOnce(
                session,
                AgentWorkspaceSessionFaultPoint.AfterRewriteRecentHistoryTailStepMutation,
                "Injected fault during tail rewrite step.",
                beforeThrow: () => {
                    ReplaceCachedRecentHistory(
                        state,
                        CreateAssignedObservationEntry(999UL, "ghost-observation"),
                        CreateAssignedActionEntry(1000UL, "ghost-action")
                    );
                    ReplaceCachedLastSerial(state, 1000UL);
                }
            );

            var replacementObservation = new ObservationEntry();
            replacementObservation.AssignNotifications("rewritten-observation");
            var replacementAction = new ActionEntry(
                new ActionMessage([new ActionBlock.Text("rewritten-action")]),
                new CompletionDescriptor("provider-a", "spec-a", "model-a")
            );

            var exception = Assert.Throws<TargetInvocationException>(
                () => InvokeRewriteRecentHistoryTail(state, 2UL, replacementObservation, replacementAction)
            );

            var inner = Assert.IsType<InvalidOperationException>(exception.InnerException);
            Assert.Equal("Injected fault during tail rewrite step.", inner.Message);
            AssertObservationActionObservationActionHistory(
                workspaceRoot.History.LoadRecent(),
                "first-observation",
                "first-action",
                3UL,
                "second-observation",
                4UL,
                "second-action"
            );
            AssertObservationActionObservationActionHistory(
                state.RecentHistory,
                "first-observation",
                "first-action",
                3UL,
                "second-observation",
                4UL,
                "second-action"
            );
            Assert.Equal(4UL, GetWorkingSet(state).LastSerial);
            Assert.Equal(4UL, workspaceRoot.History.GetRequiredLastSerial());
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public void WorkspaceLiveTailRewrite_PostMutationFaultReloadsDurableTruth() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-workspace-tail-rewrite-post-fault-reload-{Guid.NewGuid():N}");

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var workspaceRoot = AgentWorkspaceRoot.Create(revision);

            using var session = AgentWorkspaceSession.Open(workspaceRoot);
            var state = session.RestoreState();
            state.AppendObservation(new ObservationEntry(), "first-observation");
            state.AppendAction(
                new ActionEntry(
                    new ActionMessage([new ActionBlock.Text("first-action")]),
                    new CompletionDescriptor("provider-a", "spec-a", "model-a")
                )
            );
            state.AppendObservation(new ObservationEntry(), "second-observation");
            state.AppendAction(
                new ActionEntry(
                    new ActionMessage([new ActionBlock.Text("second-action")]),
                    new CompletionDescriptor("provider-a", "spec-a", "model-a")
                )
            );

            var replacementObservation = new ObservationEntry();
            replacementObservation.AssignNotifications("rewritten-observation");
            var replacementAction = new ActionEntry(
                new ActionMessage([new ActionBlock.Text("rewritten-action")]),
                new CompletionDescriptor("provider-a", "spec-a", "model-a")
            );

            ConfigureSessionFaultToThrowOnce(
                session,
                AgentWorkspaceSessionFaultPoint.AfterRewriteRecentHistoryTailMutation,
                "Injected fault after tail rewrite mutation.",
                beforeThrow: () => {
                    ReplaceCachedRecentHistory(
                        state,
                        CreateAssignedObservationEntry(999UL, "ghost-observation"),
                        CreateAssignedActionEntry(1000UL, "ghost-action")
                    );
                    ReplaceCachedLastSerial(state, 1000UL);
                }
            );

            var exception = Assert.Throws<TargetInvocationException>(
                () => InvokeRewriteRecentHistoryTail(state, 2UL, replacementObservation, replacementAction)
            );

            var inner = Assert.IsType<InvalidOperationException>(exception.InnerException);
            Assert.Equal("Injected fault after tail rewrite mutation.", inner.Message);
            AssertObservationActionObservationActionHistory(
                workspaceRoot.History.LoadRecent(),
                "first-observation",
                "first-action",
                5UL,
                "rewritten-observation",
                6UL,
                "rewritten-action"
            );
            AssertObservationActionObservationActionHistory(
                state.RecentHistory,
                "first-observation",
                "first-action",
                5UL,
                "rewritten-observation",
                6UL,
                "rewritten-action"
            );
            Assert.NotSame(replacementObservation, state.RecentHistory[2]);
            Assert.NotSame(replacementAction, state.RecentHistory[3]);
            Assert.Equal(6UL, GetWorkingSet(state).LastSerial);
            Assert.Equal(6UL, workspaceRoot.History.GetRequiredLastSerial());
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public void SnapshotHelper_SaveSnapshot_ReplacesDurableHistoryDequeAfterLiveAppend() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-workspace-snapshot-replace-{Guid.NewGuid():N}");

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var workspaceRoot = AgentWorkspaceRoot.Create(revision);
            workspaceRoot.Meta.SetSystemPrompt("workspace-append-system");
            using var session = AgentWorkspaceSession.Open(workspaceRoot);
            var state = session.RestoreState();
            state.AppendObservation(new ObservationEntry(), "live-append");
            var liveAppendDeque = GetHistoryDeque(workspaceRoot);
            state.AppendNotification("live-pending");
            var livePendingNotificationsDeque = GetPendingNotificationsDeque(workspaceRoot);
            var snapshot = CreateSnapshotFixture();
            AgentEngineWorkspaceSnapshotHelper.SaveSnapshot(workspaceRoot, snapshot);

            Assert.NotSame(liveAppendDeque, GetHistoryDeque(workspaceRoot));
            var actual = AgentEngineWorkspaceSnapshotHelper.LoadSnapshot(workspaceRoot);
            Assert.Equal(snapshot.AgentState.RecentHistory.Count, actual.AgentState.RecentHistory.Count);
            Assert.Equal(snapshot.AgentState.LastSerial, actual.AgentState.LastSerial);
            for (int i = 0; i < snapshot.AgentState.RecentHistory.Count; i++) {
                AssertHistoryEntry(snapshot.AgentState.RecentHistory[i], actual.AgentState.RecentHistory[i]);
            }

            Assert.NotSame(livePendingNotificationsDeque, GetPendingNotificationsDeque(workspaceRoot));
            Assert.Equal(snapshot.AgentState.PendingNotifications, workspaceRoot.History.LoadPendingNotifications());
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public void SnapshotHelper_SaveSnapshot_ReplacesTurnRuntimeDurableDictAfterLiveMutation() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-turn-runtime-snapshot-replace-{Guid.NewGuid():N}");

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var workspaceRoot = AgentWorkspaceRoot.Create(revision);
            using var session = AgentWorkspaceSession.Open(workspaceRoot);
            var liveTurnRuntime = GetTurnRuntimeMap(workspaceRoot);

            session.UpdateTurnRuntime(new LlmProfileCheckpoint("provider-live", "spec-live", "model-live", "profile-live", 4096), null);
            session.UpdateTurnRuntime(new LlmProfileCheckpoint("provider-live", "spec-live", "model-live", "profile-live", 4096), 7);

            Assert.Same(liveTurnRuntime, GetTurnRuntimeMap(workspaceRoot));

            AgentEngineWorkspaceSnapshotHelper.SaveSnapshot(workspaceRoot, CreateSnapshotFixture());

            Assert.NotSame(liveTurnRuntime, GetTurnRuntimeMap(workspaceRoot));
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public void WorkspaceRoot_LiveTurnRuntimeFieldUpdates_KeepDurableDictIdentity() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-turn-runtime-live-update-{Guid.NewGuid():N}");

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var workspaceRoot = AgentWorkspaceRoot.Create(revision);
            using var session = AgentWorkspaceSession.Open(workspaceRoot);
            var turnRuntime = GetTurnRuntimeMap(workspaceRoot);
            var firstProfile = new LlmProfileCheckpoint("provider-a", "spec-a", "model-a", "profile-a", 4096);
            var secondProfile = new LlmProfileCheckpoint("provider-b", "spec-b", "model-b", "profile-b", 8192);

            session.UpdateTurnRuntime(firstProfile, null);
            Assert.Same(turnRuntime, GetTurnRuntimeMap(workspaceRoot));

            session.UpdateTurnRuntime(firstProfile, 3);
            Assert.Same(turnRuntime, GetTurnRuntimeMap(workspaceRoot));

            session.UpdateTurnRuntime(secondProfile, 3);
            Assert.Same(turnRuntime, GetTurnRuntimeMap(workspaceRoot));

            session.UpdateTurnRuntime(secondProfile, 5);
            Assert.Same(turnRuntime, GetTurnRuntimeMap(workspaceRoot));

            var updatedTurnRuntime = session.LoadTurnRuntimeState();
            Assert.Equal(secondProfile, updatedTurnRuntime.ResolvedProfile);
            Assert.Equal(5, updatedTurnRuntime.LockedCompactionSplitIndex);
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public void WorkspaceRoot_LiveTurnRuntimeFieldClears_KeepDurableDictIdentity() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-turn-runtime-live-clear-{Guid.NewGuid():N}");

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var workspaceRoot = AgentWorkspaceRoot.Create(revision);
            using var session = AgentWorkspaceSession.Open(workspaceRoot);
            var turnRuntime = GetTurnRuntimeMap(workspaceRoot);

            session.UpdateTurnRuntime(new LlmProfileCheckpoint("provider-a", "spec-a", "model-a", "profile-a", 4096), 3);
            Assert.Same(turnRuntime, GetTurnRuntimeMap(workspaceRoot));

            session.UpdateTurnRuntime(null, 3);
            Assert.Same(turnRuntime, GetTurnRuntimeMap(workspaceRoot));

            session.UpdateTurnRuntime(null, null);
            Assert.Same(turnRuntime, GetTurnRuntimeMap(workspaceRoot));

            var clearedTurnRuntime = session.LoadTurnRuntimeState();
            Assert.Null(clearedTurnRuntime.ResolvedProfile);
            Assert.Null(clearedTurnRuntime.LockedCompactionSplitIndex);
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public void WorkspaceSessionUpdateTurnRuntime_ReturnsUpdatedTurnRuntimeState() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-turn-runtime-session-update-{Guid.NewGuid():N}");

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var workspaceRoot = AgentWorkspaceRoot.Create(revision);
            using var session = AgentWorkspaceSession.Open(workspaceRoot);
            var expectedProfile = new LlmProfileCheckpoint("provider-turn", "spec-turn", "model-turn", "profile-turn", 4096);

            var updatedTurnRuntime = session.UpdateTurnRuntime(expectedProfile, 7);

            Assert.Equal(expectedProfile, updatedTurnRuntime.ResolvedProfile);
            Assert.Equal(7, updatedTurnRuntime.LockedCompactionSplitIndex);
            Assert.Equal(updatedTurnRuntime, session.LoadTurnRuntimeState());
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public void WorkspaceSessionUpdatePendingCompaction_ReturnsUpdatedSnapshot() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-pending-compaction-session-update-{Guid.NewGuid():N}");

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var workspaceRoot = AgentWorkspaceRoot.Create(revision);
            using var session = AgentWorkspaceSession.Open(workspaceRoot);
            var pendingCompactionRecord = GetPendingCompactionRecord(workspaceRoot);
            var expected = new CompactionCheckpoint(3, "live-system", "live-prompt");

            var updated = session.UpdatePendingCompaction(expected);

            Assert.Same(pendingCompactionRecord, GetPendingCompactionRecord(workspaceRoot));
            Assert.Equal(expected, updated);
            Assert.Equal(expected, session.LoadPendingCompaction());

            var cleared = session.UpdatePendingCompaction(null);

            Assert.Same(pendingCompactionRecord, GetPendingCompactionRecord(workspaceRoot));
            Assert.Null(cleared);
            Assert.Null(session.LoadPendingCompaction());
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public void WorkspaceSessionAllocateToolSessionExecutionSequence_ReturnsAuthoritativeSequence() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-tool-sequence-session-allocate-{Guid.NewGuid():N}");

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var workspaceRoot = AgentWorkspaceRoot.Create(revision);
            using var session = AgentWorkspaceSession.Open(workspaceRoot);

            var first = session.AllocateToolSessionExecutionSequence();
            var second = session.AllocateToolSessionExecutionSequence();

            Assert.Equal(1L, first);
            Assert.Equal(2L, second);
            Assert.Equal(2L, session.LoadToolSessionExecutionSequence());
            Assert.Equal(2L, workspaceRoot.RuntimeState.GetToolSessionExecutionSequenceOrDefault());
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public void WorkspaceSessionReplacePendingToolResults_ReturnsUpdatedPendingResultsSnapshot() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-pending-results-session-replace-{Guid.NewGuid():N}");

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var workspaceRoot = AgentWorkspaceRoot.Create(revision);
            using var session = AgentWorkspaceSession.Open(workspaceRoot);
            var initialPendingMap = GetPendingToolResultsMap(workspaceRoot);

            var pendingResults = new[] {
                CreateToolCallExecutionResult("tool-alpha", "call-1", "output-1"),
                CreateToolCallExecutionResult("tool-beta", "call-2", "output-2")
            };

            var updatedPendingResults = session.ReplacePendingToolResults(pendingResults);
            var persistedPendingResults = session.LoadPendingToolResults();

            Assert.NotSame(initialPendingMap, GetPendingToolResultsMap(workspaceRoot));
            Assert.Equal(pendingResults.Length, updatedPendingResults.Count);
            for (int i = 0; i < pendingResults.Length; i++) {
                AssertToolCallExecutionResult(pendingResults[i], updatedPendingResults[i]);
                AssertToolCallExecutionResult(pendingResults[i], persistedPendingResults[i]);
            }
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public void WorkspaceSessionUpsertPendingToolResult_ReturnsUpdatedPendingResultsSnapshot() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-pending-results-session-upsert-{Guid.NewGuid():N}");

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var workspaceRoot = AgentWorkspaceRoot.Create(revision);
            using var session = AgentWorkspaceSession.Open(workspaceRoot);

            session.ReplacePendingToolResults([
                CreateToolCallExecutionResult("tool-alpha", "call-1", "output-1")
            ]);
            var upsertedResult = CreateToolCallExecutionResult("tool-beta", "call-2", "output-2");

            var updatedPendingResults = session.UpsertPendingToolResult(upsertedResult);
            var freshPendingResults = session.LoadPendingToolResults();

            Assert.Equal(freshPendingResults.Count, updatedPendingResults.Count);
            for (int i = 0; i < freshPendingResults.Count; i++) {
                AssertToolCallExecutionResult(freshPendingResults[i], updatedPendingResults[i]);
            }
            Assert.Equal(["call-1", "call-2"], updatedPendingResults
                .Select(static result => result.ToolCallId)
                .OrderBy(static toolCallId => toolCallId, StringComparer.Ordinal));
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public void SnapshotHelper_ReplaceRuntimeState_ReplacesPendingCompactionDurableRecordAfterLiveMutation() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-pending-compaction-runtime-replace-{Guid.NewGuid():N}");

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var workspaceRoot = AgentWorkspaceRoot.Create(revision);
            using var session = AgentWorkspaceSession.Open(workspaceRoot);
            var livePendingCompaction = GetPendingCompactionRecord(workspaceRoot);

            session.UpdatePendingCompaction(new CompactionCheckpoint(2, "live-system", "live-prompt"));
            Assert.Same(livePendingCompaction, GetPendingCompactionRecord(workspaceRoot));

            AgentEngineWorkspaceSnapshotHelper.SaveSnapshot(
                workspaceRoot,
                new AgentEngineStateSnapshot(
                    AgentState: new AgentStateSnapshot(
                        SystemPrompt: workspaceRoot.Meta.GetRequiredSystemPrompt(),
                        RecentHistory: workspaceRoot.History.LoadRecent(),
                        PendingNotifications: workspaceRoot.History.LoadPendingNotifications(),
                        LastSerial: workspaceRoot.History.GetRequiredLastSerial()
                    ),
                    PendingToolResults: Array.Empty<ToolCallExecutionResult>(),
                    ResolvedProfile: null,
                    LockedCompactionSplitIndex: null,
                    PendingCompaction: new CompactionCheckpoint(4, "snapshot-system", "snapshot-prompt"),
                    ToolSessionExecutionSequence: 0
                )
            );

            Assert.NotSame(livePendingCompaction, GetPendingCompactionRecord(workspaceRoot));
            Assert.Equal(
                new CompactionCheckpoint(4, "snapshot-system", "snapshot-prompt"),
                AgentEngineWorkspaceSnapshotHelper.LoadSnapshot(workspaceRoot).PendingCompaction
            );
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public void WorkspaceRoot_PendingCompactionLoad_RequiresSeededRecord() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-pending-compaction-required-{Guid.NewGuid():N}");

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var workspaceRoot = AgentWorkspaceRoot.Create(revision);

            workspaceRoot.Root.Remove("pendingCompaction");

            var exception = Assert.Throws<InvalidDataException>(() => workspaceRoot.RuntimeState.LoadPendingCompaction());
            Assert.Equal("Agent state root is missing pendingCompaction record.", exception.Message);
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

            var durablePrompt = host.LoadDurableSystemPrompt();
            var durablePendingNotifications = host.LoadDurablePendingNotifications();
            var durableRecentHistory = host.LoadDurableRecentHistory();

            Assert.Equal("updated-before-commit", durablePrompt);
            Assert.Empty(durablePendingNotifications);
            var observation = Assert.IsType<ObservationEntry>(Assert.Single(durableRecentHistory));
            Assert.Equal("queued-notification\nrecent-events", observation.Notifications);
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public void PublicLoadThenCreateFromStateSnapshot_RemainsNonLiveAndDoesNotWriteThrough() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-public-root-{Guid.NewGuid():N}");

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var workspaceRoot = AgentWorkspaceRoot.Create(revision, "public-root-system");

            var snapshot = AgentEngineWorkspaceSnapshotHelper.LoadSnapshot(workspaceRoot);
            var engine = AgentEngine.CreateFromStateSnapshot(snapshot);
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

            var persisted = AgentEngineWorkspaceSnapshotHelper.LoadSnapshot(workspaceRoot);

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
    public void PublicCreateFromStateSnapshot_RemainsNonLiveAndDoesNotWriteThrough() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-public-snapshot-{Guid.NewGuid():N}");

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var workspaceRoot = AgentWorkspaceRoot.Create(revision, "public-snapshot-system");
            var snapshot = AgentEngineWorkspaceSnapshotHelper.LoadSnapshot(workspaceRoot);

            var engine = AgentEngine.CreateFromStateSnapshot(snapshot);
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

            var persisted = AgentEngineWorkspaceSnapshotHelper.LoadSnapshot(workspaceRoot);

            Assert.Equal("public-snapshot-system", persisted.AgentState.SystemPrompt);
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
    public void PublicCreateFromStateSnapshot_LocalHistoryMutationStaysInWorkingSetOnly() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-public-snapshot-local-history-{Guid.NewGuid():N}");

        try {
            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var workspaceRoot = AgentWorkspaceRoot.Create(revision, "public-snapshot-system");
            var snapshot = AgentEngineWorkspaceSnapshotHelper.LoadSnapshot(workspaceRoot);

            var engine = AgentEngine.CreateFromStateSnapshot(snapshot);
            engine.AppendNotification("queued-notification");
            var observation = engine.State.AppendObservation(new ObservationEntry(), "recent-events");
            engine.State.AppendAction(
                new ActionEntry(
                    new ActionMessage([new ActionBlock.Text("assistant-turn")]),
                    new CompletionDescriptor("provider-a", "spec-a", "model-a")
                )
            );
            engine.InjectActionContent(
                new ActionInjectionRequest(
                    "continue-here",
                    new InjectionSource(InjectionSourceKind.HostOverride),
                    InjectedActionContentMode.Text
                )
            );
            var recap = engine.State.ReplacePrefixWithRecap(1, "summary-text");

            Assert.Equal("queued-notification\nrecent-events", observation.Notifications);
            Assert.False(engine.State.HasPendingNotification);
            Assert.Equal(1UL, recap.InsteadSerial);
            Assert.Equal(4UL, recap.Serial);
            Assert.Collection(
                engine.State.RecentHistory,
                entry => {
                    var recapEntry = Assert.IsType<RecapEntry>(entry);
                    Assert.Equal(4UL, recapEntry.Serial);
                    Assert.Equal("summary-text", recapEntry.Content);
                    Assert.Equal(1UL, recapEntry.InsteadSerial);
                },
                entry => {
                    var action = Assert.IsType<ActionEntry>(entry);
                    Assert.Equal(2UL, action.Serial);
                    Assert.Equal("assistant-turn", action.Message.GetFlattenedText());
                },
                entry => {
                    var injection = Assert.IsType<InjectionEntry>(entry);
                    Assert.Equal(3UL, injection.Serial);
                    Assert.Equal("continue-here", injection.Content);
                    Assert.Equal(ActionBlockKind.Text, injection.BlockKind);
                }
            );

            var persisted = AgentEngineWorkspaceSnapshotHelper.LoadSnapshot(workspaceRoot);

            Assert.Equal("public-snapshot-system", persisted.AgentState.SystemPrompt);
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
            var durableRecentHistory = reopened.LoadDurableRecentHistory();
            var durablePendingNotifications = reopened.LoadDurablePendingNotifications();

            Assert.Equal("host-system", reopened.Engine.SystemPrompt);
            Assert.Equal(3, durableRecentHistory.Count);
            Assert.Collection(
                durableRecentHistory,
                entry => Assert.IsType<ObservationEntry>(entry),
                entry => Assert.IsType<ActionEntry>(entry),
                entry => Assert.IsType<InjectionEntry>(entry)
            );
            Assert.Equal(["queued-notification"], durablePendingNotifications);
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
            var durablePendingNotifications = reopened.LoadDurablePendingNotifications();
            var durableRecentHistory = reopened.LoadDurableRecentHistory();

            Assert.Empty(durablePendingNotifications);
            var observation = Assert.IsType<ObservationEntry>(Assert.Single(durableRecentHistory));
            Assert.Equal("queued-notification\nrecent-events", observation.Notifications);
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Host_PendingInputLateNotification_FoldsIntoCurrentObservationBeforeModelRequest() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-host-pending-input-late-notification-{Guid.NewGuid():N}");

        try {
            var client = new RecordingQueueCompletionClient(
                new ActionMessage([new ActionBlock.Text("assistant-output")])
            );
            var profile = CreateFullFeatureProfile(client, "model-pending-input-late");

            using var host = AgentEngineHost.CreateNew(repoDir);
            var initialHistoryDeque = GetHistoryDeque(host.WorkspaceRoot);
            host.Engine.WaitingInput += static (_, args) => {
                args.ShouldContinue = true;
                args.Observation = IncomingObservation.FromRecentEvents("current-input");
            };

            await host.StepAsync(profile);
            host.Engine.AppendNotification("late-notification");

            await host.StepAsync(profile);

            var request = Assert.Single(client.Requests);
            AssertSingleObservationRequestContent(request, "current-input\nlate-notification");
            Assert.Empty(host.LoadDurablePendingNotifications());
            Assert.Same(initialHistoryDeque, GetHistoryDeque(host.WorkspaceRoot));

            var durableObservation = Assert.IsType<ObservationEntry>(host.LoadDurableRecentHistory()[0]);
            Assert.Equal("current-input\nlate-notification", durableObservation.Notifications);
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Host_PendingInputLateNotification_PrepareInvocationSeesFoldedCurrentObservation() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-host-pending-input-prepare-folded-observation-{Guid.NewGuid():N}");

        try {
            var client = new RecordingQueueCompletionClient(
                new ActionMessage([new ActionBlock.Text("assistant-output")])
            );
            var profile = CreateFullFeatureProfile(client, "model-pending-input-prepare");

            using var host = AgentEngineHost.CreateNew(repoDir);
            host.Engine.WaitingInput += static (_, args) => {
                args.ShouldContinue = true;
                args.Observation = IncomingObservation.FromRecentEvents("current-input");
            };

            await host.StepAsync(profile);
            host.Engine.AppendNotification("late-notification");

            string? notificationsSeenInPrepare = null;
            ulong? estimatedTokensSeenInPrepare = null;
            ulong? liveEstimateSeenInPrepare = null;
            host.Engine.PrepareInvocationAsync = (args, _) => {
                notificationsSeenInPrepare = Assert.IsType<ObservationEntry>(host.Engine.State.RecentHistory[^1]).Notifications;
                estimatedTokensSeenInPrepare = args.EstimatedContextTokens;
                liveEstimateSeenInPrepare = host.Engine.EstimateCurrentContextTokens();
                return Task.CompletedTask;
            };

            await host.StepAsync(profile);

            Assert.Equal("current-input\nlate-notification", notificationsSeenInPrepare);
            Assert.Equal(liveEstimateSeenInPrepare, estimatedTokensSeenInPrepare);
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Host_PendingInputLateNotification_SoftCapGateUsesFoldedCurrentObservation() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-host-pending-input-soft-cap-folded-observation-{Guid.NewGuid():N}");

        try {
            const string currentInput = "current-input";
            var lateNotification = new string('n', 512);
            var seedInvocation = new CompletionDescriptor("provider-a", "spec-a", "model-a");

            using var host = AgentEngineHost.CreateNew(
                repoDir,
                runtime: new AgentEngineHostRuntime(
                    autoCompaction: new AutoCompactionOptions("compact-system", "compact-now")
                )
            );
            host.Engine.State.AppendObservation(new ObservationEntry(), "seed-observation-1");
            host.Engine.State.AppendAction(
                new ActionEntry(
                    new ActionMessage([new ActionBlock.Text("seed-action-1")]),
                    seedInvocation
                )
            );
            host.Engine.State.AppendObservation(new ObservationEntry(), "seed-observation-2");
            host.Engine.State.AppendAction(
                new ActionEntry(
                    new ActionMessage([new ActionBlock.Text("seed-action-2")]),
                    seedInvocation
                )
            );
            host.Engine.WaitingInput += static (_, args) => {
                args.ShouldContinue = true;
                args.Observation = IncomingObservation.FromRecentEvents(currentInput);
            };

            await host.StepAsync(CreateFullFeatureProfile(new NoopCompletionClient("provider-a", "spec-a"), "model-seed"));
            host.Engine.AppendNotification(lateNotification);

            var preFoldEstimate = host.Engine.EstimateCurrentContextTokens();
            var expectedFoldedState = AgentState.CreateDefault(host.Engine.SystemPrompt);
            expectedFoldedState.AppendObservation(new ObservationEntry(), "seed-observation-1");
            expectedFoldedState.AppendAction(
                new ActionEntry(
                    new ActionMessage([new ActionBlock.Text("seed-action-1")]),
                    seedInvocation
                )
            );
            expectedFoldedState.AppendObservation(new ObservationEntry(), "seed-observation-2");
            expectedFoldedState.AppendAction(
                new ActionEntry(
                    new ActionMessage([new ActionBlock.Text("seed-action-2")]),
                    seedInvocation
                )
            );
            expectedFoldedState.AppendObservation(new ObservationEntry(), $"{currentInput}\n{lateNotification}");
            var postFoldEstimate = new AgentEngine(state: expectedFoldedState).EstimateCurrentContextTokens();
            Assert.True(postFoldEstimate > preFoldEstimate);

            var client = new RecordingQueueCompletionClient(
                new ActionMessage([new ActionBlock.Text("assistant-output")])
            );
            var softCapProfile = CreateFullFeatureProfile(client, "model-soft-cap-folded", (int)postFoldEstimate);

            await host.StepAsync(softCapProfile);

            Assert.Empty(client.Requests);
            Assert.True(host.Engine.HasPendingCompaction);
            Assert.NotNull(host.LoadDurablePendingCompaction());
            Assert.Equal(
                $"{currentInput}\n{lateNotification}",
                Assert.IsType<ObservationEntry>(host.LoadDurableRecentHistory()[^1]).Notifications
            );
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Host_PendingInputRetry_LateNotificationAppearsInRetryRequest() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-host-pending-input-retry-late-notification-{Guid.NewGuid():N}");

        try {
            var client = new RecordingQueueCompletionClient(
                new InvalidOperationException("simulated provider failure"),
                new ActionMessage([new ActionBlock.Text("assistant-output")])
            );
            var profile = CreateFullFeatureProfile(client, "model-pending-input-retry");

            using var host = AgentEngineHost.CreateNew(repoDir);
            host.Engine.WaitingInput += static (_, args) => {
                args.ShouldContinue = true;
                args.Observation = IncomingObservation.FromRecentEvents("current-input");
            };

            await host.StepAsync(profile);
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.StepAsync(profile));

            host.Engine.AppendNotification("late-after-failure");

            await host.StepAsync(profile);

            Assert.Equal(2, client.Requests.Count);
            AssertSingleObservationRequestContent(client.Requests[0], "current-input");
            AssertSingleObservationRequestContent(client.Requests[1], "current-input\nlate-after-failure");
            Assert.Empty(host.LoadDurablePendingNotifications());

            var durableObservation = Assert.IsType<ObservationEntry>(host.LoadDurableRecentHistory()[0]);
            Assert.Equal("current-input\nlate-after-failure", durableObservation.Notifications);
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Host_OpenExisting_PendingInputRetry_FoldsLateNotificationBeforeRetryRequest() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-host-pending-input-reopen-retry-late-notification-{Guid.NewGuid():N}");

        try {
            var failingClient = new RecordingQueueCompletionClient(
                new InvalidOperationException("simulated provider failure")
            );
            var failingProfile = CreateFullFeatureProfile(failingClient, "model-pending-input-reopen-retry");

            using (var host = AgentEngineHost.CreateNew(repoDir)) {
                host.Engine.WaitingInput += static (_, args) => {
                    args.ShouldContinue = true;
                    args.Observation = IncomingObservation.FromRecentEvents("current-input");
                };

                await host.StepAsync(failingProfile);
                await Assert.ThrowsAsync<InvalidOperationException>(() => host.StepAsync(failingProfile));

                host.Engine.AppendNotification("late-after-reopen");
                host.SaveAndCommit();
            }

            var retryClient = new RecordingQueueCompletionClient(
                new ActionMessage([new ActionBlock.Text("assistant-output")])
            );
            var retryProfile = CreateFullFeatureProfile(retryClient, "model-pending-input-reopen-retry");

            using var reopened = AgentEngineHost.OpenExisting(repoDir);
            await reopened.StepAsync(retryProfile);

            var request = Assert.Single(retryClient.Requests);
            AssertSingleObservationRequestContent(request, "current-input\nlate-after-reopen");
            Assert.Empty(reopened.LoadDurablePendingNotifications());

            var durableObservation = Assert.IsType<ObservationEntry>(reopened.LoadDurableRecentHistory()[0]);
            Assert.Equal("current-input\nlate-after-reopen", durableObservation.Notifications);
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Host_PendingToolResultsRetry_LateNotificationAppearsInRetryRequestWithoutLosingToolResultsType() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-host-pending-tool-results-retry-late-notification-{Guid.NewGuid():N}");

        try {
            var client = new RecordingQueueCompletionClient(
                new InvalidOperationException("simulated provider failure"),
                new ActionMessage([new ActionBlock.Text("assistant-output")])
            );
            const string modelId = "model-pending-tool-results-retry";
            var profile = CreateFullFeatureProfile(client, modelId);
            var invocation = profile.ToCompletionDescriptor();
            var toolResult = CreateToolCallExecutionResult("alpha", "call-1", "tool-output");

            using var host = AgentEngineHost.CreateNew(repoDir);
            AppendObservationActionToolResultsTurn(host.Engine.State, invocation, "seed-observation", toolResult);

            await Assert.ThrowsAsync<InvalidOperationException>(() => host.StepAsync(profile));

            host.Engine.AppendNotification("late-after-failure");

            string? notificationsSeenInPrepare = null;
            string[]? resultIdsSeenInPrepare = null;
            ulong? estimatedTokensSeenInPrepare = null;
            ulong? liveEstimateSeenInPrepare = null;
            host.Engine.PrepareInvocationAsync = (args, _) => {
                var tail = Assert.IsType<ToolResultsEntry>(host.Engine.State.RecentHistory[^1]);
                var durableTail = Assert.IsType<ToolResultsEntry>(host.LoadDurableRecentHistory()[^1]);
                notificationsSeenInPrepare = tail.Notifications;
                resultIdsSeenInPrepare = tail.Results.Select(static result => result.ToolCallId).ToArray();
                Assert.Equal("late-after-failure", durableTail.Notifications);
                Assert.Equal(["call-1"], durableTail.Results.Select(static result => result.ToolCallId).ToArray());
                estimatedTokensSeenInPrepare = args.EstimatedContextTokens;
                liveEstimateSeenInPrepare = host.Engine.EstimateCurrentContextTokens();
                return Task.CompletedTask;
            };

            await host.StepAsync(profile);

            Assert.Equal("late-after-failure", notificationsSeenInPrepare);
            Assert.Equal(["call-1"], Assert.IsType<string[]>(resultIdsSeenInPrepare));
            Assert.Equal(liveEstimateSeenInPrepare, estimatedTokensSeenInPrepare);
            AssertLastToolResultsRequestContentAndResults(client.Requests[1], "late-after-failure", "call-1");
            Assert.Empty(host.LoadDurablePendingNotifications());

            var foldedEntry = Assert.IsType<ToolResultsEntry>(host.LoadDurableRecentHistory()[^2]);
            Assert.Equal("late-after-failure", foldedEntry.Notifications);
            var durableResult = Assert.Single(foldedEntry.Results);
            Assert.Equal("call-1", durableResult.ToolCallId);
            Assert.Equal("tool-output", durableResult.ExecuteResult.GetFlattenedText());
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Host_OpenExisting_PendingToolResultsRetry_FoldsLateNotificationWithoutLosingToolResultsType() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-host-pending-tool-results-reopen-retry-late-notification-{Guid.NewGuid():N}");

        try {
            const string modelId = "model-pending-tool-results-reopen-retry";
            var failingClient = new RecordingQueueCompletionClient(
                new InvalidOperationException("simulated provider failure")
            );
            var failingProfile = CreateFullFeatureProfile(failingClient, modelId);
            var invocation = failingProfile.ToCompletionDescriptor();
            var toolResult = CreateToolCallExecutionResult("alpha", "call-1", "tool-output");

            using (var host = AgentEngineHost.CreateNew(repoDir)) {
                AppendObservationActionToolResultsTurn(host.Engine.State, invocation, "seed-observation", toolResult);

                await Assert.ThrowsAsync<InvalidOperationException>(() => host.StepAsync(failingProfile));

                host.Engine.AppendNotification("late-after-reopen");
                host.SaveAndCommit();
            }

            var retryClient = new RecordingQueueCompletionClient(
                new ActionMessage([new ActionBlock.Text("assistant-output")])
            );
            var retryProfile = CreateFullFeatureProfile(retryClient, modelId);

            using var reopened = AgentEngineHost.OpenExisting(repoDir);
            string? notificationsSeenInPrepare = null;
            string[]? resultIdsSeenInPrepare = null;
            reopened.Engine.PrepareInvocationAsync = (_, _) => {
                var tail = Assert.IsType<ToolResultsEntry>(reopened.Engine.State.RecentHistory[^1]);
                var durableTail = Assert.IsType<ToolResultsEntry>(reopened.LoadDurableRecentHistory()[^1]);
                notificationsSeenInPrepare = tail.Notifications;
                resultIdsSeenInPrepare = tail.Results.Select(static result => result.ToolCallId).ToArray();
                Assert.Equal("late-after-reopen", durableTail.Notifications);
                Assert.Equal(["call-1"], durableTail.Results.Select(static result => result.ToolCallId).ToArray());
                return Task.CompletedTask;
            };
            await reopened.StepAsync(retryProfile);

            Assert.Equal("late-after-reopen", notificationsSeenInPrepare);
            Assert.Equal(["call-1"], Assert.IsType<string[]>(resultIdsSeenInPrepare));
            AssertLastToolResultsRequestContentAndResults(retryClient.Requests[0], "late-after-reopen", "call-1");
            Assert.Empty(reopened.LoadDurablePendingNotifications());

            var foldedEntry = Assert.IsType<ToolResultsEntry>(reopened.LoadDurableRecentHistory()[^2]);
            Assert.Equal("late-after-reopen", foldedEntry.Notifications);
            var durableResult = Assert.Single(foldedEntry.Results);
            Assert.Equal("call-1", durableResult.ToolCallId);
            Assert.Equal("tool-output", durableResult.ExecuteResult.GetFlattenedText());
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Host_PendingToolResultsLateNotification_SoftCapGateUsesFoldedToolResultsInput() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-host-pending-tool-results-soft-cap-folded-observation-{Guid.NewGuid():N}");

        try {
            const string modelId = "model-soft-cap-folded-tool-results";
            var seedProfile = CreateFullFeatureProfile(new NoopCompletionClient("test-provider", "test-spec"), modelId);
            var invocation = seedProfile.ToCompletionDescriptor();
            var currentTurnResult = CreateToolCallExecutionResult("beta", "call-2", "current-tool-output");
            var lateNotification = new string('n', 512);

            using var host = AgentEngineHost.CreateNew(
                repoDir,
                runtime: new AgentEngineHostRuntime(
                    autoCompaction: new AutoCompactionOptions("compact-system", "compact-now")
                )
            );
            host.Engine.State.AppendObservation(new ObservationEntry(), "seed-observation-1");
            host.Engine.State.AppendAction(
                new ActionEntry(
                    new ActionMessage([new ActionBlock.Text("seed-action-1")]),
                    invocation
                )
            );
            host.Engine.State.AppendObservation(new ObservationEntry(), "seed-observation-2");
            host.Engine.State.AppendAction(
                new ActionEntry(
                    new ActionMessage([
                        new ActionBlock.ToolCall(currentTurnResult.RawToolCall)
                    ]),
                    invocation
                )
            );
            host.Engine.State.AppendToolResults(new ToolResultsEntry([currentTurnResult]));
            host.Engine.AppendNotification(lateNotification);

            var preFoldEstimate = host.Engine.EstimateCurrentContextTokens();
            var expectedFoldedState = AgentState.CreateDefault(host.Engine.SystemPrompt);
            expectedFoldedState.AppendObservation(new ObservationEntry(), "seed-observation-1");
            expectedFoldedState.AppendAction(
                new ActionEntry(
                    new ActionMessage([new ActionBlock.Text("seed-action-1")]),
                    invocation
                )
            );
            var expectedCurrentResult = CreateToolCallExecutionResult("beta", "call-2", "current-tool-output");
            expectedFoldedState.AppendObservation(new ObservationEntry(), "seed-observation-2");
            expectedFoldedState.AppendAction(
                new ActionEntry(
                    new ActionMessage([
                        new ActionBlock.ToolCall(expectedCurrentResult.RawToolCall)
                    ]),
                    invocation
                )
            );
            expectedFoldedState.AppendToolResults(new ToolResultsEntry([expectedCurrentResult]));
            expectedFoldedState.AppendNotification(lateNotification);
            _ = InvokeRequiredInstanceMethod(expectedFoldedState, "FoldPendingNotificationsIntoCurrentToolResultsForPendingModelCall");
            var postFoldEstimate = new AgentEngine(state: expectedFoldedState).EstimateCurrentContextTokens();
            Assert.True(postFoldEstimate > preFoldEstimate);

            var client = new RecordingQueueCompletionClient(
                new ActionMessage([new ActionBlock.Text("assistant-output")])
            );
            var softCapProfile = CreateFullFeatureProfile(client, modelId, (int)postFoldEstimate);

            await host.StepAsync(softCapProfile);

            Assert.Empty(client.Requests);
            Assert.True(host.Engine.HasPendingCompaction);
            Assert.NotNull(host.LoadDurablePendingCompaction());
            Assert.Empty(host.LoadDurablePendingNotifications());

            var durableTail = Assert.IsType<ToolResultsEntry>(host.LoadDurableRecentHistory()[^1]);
            Assert.Equal(lateNotification, durableTail.Notifications);
            var durableResult = Assert.Single(durableTail.Results);
            Assert.Equal("call-2", durableResult.ToolCallId);
            Assert.Equal("current-tool-output", durableResult.ExecuteResult.GetFlattenedText());
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public void WorkspaceSessionFoldPendingNotificationsIntoCurrentToolResults_RejectsObservationTailWithoutMutatingDurableState() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-tool-results-fold-tail-guard-{Guid.NewGuid():N}");

        try {
            using var host = AgentEngineHost.CreateNew(repoDir);
            host.Engine.State.AppendObservation(new ObservationEntry(), "current-input");
            host.Engine.AppendNotification("late-notification");

            var session = GetWorkspaceSession(host);
            var exception = Assert.Throws<InvalidOperationException>(() => session.FoldPendingNotificationsIntoCurrentToolResults());

            Assert.Equal("Cannot fold pending notifications because the durable recent-history tail is not a ToolResultsEntry.", exception.Message);
            Assert.Equal(["late-notification"], host.LoadDurablePendingNotifications());

            var durableTail = Assert.IsType<ObservationEntry>(Assert.Single(host.LoadDurableRecentHistory()));
            Assert.Equal("current-input", durableTail.Notifications);
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Host_PendingInputNotificationFold_FailureReloadsAuthoritativeWorkingSet() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-host-pending-input-fold-fault-reload-{Guid.NewGuid():N}");

        try {
            var profile = CreateFullFeatureProfile(
                new NoopCompletionClient("provider-fold-fault", "spec-fold-fault"),
                "model-pending-input-fold-fault"
            );

            using var host = AgentEngineHost.CreateNew(repoDir);
            host.Engine.WaitingInput += static (_, args) => {
                args.ShouldContinue = true;
                args.Observation = IncomingObservation.FromRecentEvents("current-input");
            };

            await host.StepAsync(profile);
            host.Engine.AppendNotification("late-notification");

            ConfigureSessionFaultToThrowOnce(
                GetWorkspaceSession(host),
                AgentWorkspaceSessionFaultPoint.AfterFoldPendingNotificationsIntoCurrentObservationMutation,
                "Injected fault after pending-input notification fold.",
                beforeThrow: () => {
                    ReplaceCachedRecentHistory(host.Engine.State, CreateAssignedObservationEntry(999UL, "ghost-observation"));
                    ReplaceCachedPendingNotifications(host.Engine.State, "ghost-pending");
                    ReplaceCachedLastSerial(host.Engine.State, 999UL);
                }
            );

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StepAsync(profile));

            Assert.Equal("Injected fault after pending-input notification fold.", exception.Message);
            var durableObservation = Assert.IsType<ObservationEntry>(Assert.Single(host.LoadDurableRecentHistory()));
            Assert.Equal("current-input\nlate-notification", durableObservation.Notifications);
            Assert.Equal(1UL, durableObservation.Serial);
            Assert.Empty(host.LoadDurablePendingNotifications());

            var localObservation = Assert.IsType<ObservationEntry>(Assert.Single(host.Engine.State.RecentHistory));
            Assert.Equal("current-input\nlate-notification", localObservation.Notifications);
            Assert.Equal(1UL, localObservation.Serial);
            Assert.Empty(GetCachedPendingNotifications(host.Engine.State));
            Assert.False(host.Engine.State.HasPendingNotification);
            Assert.Equal(1UL, GetWorkingSet(host.Engine.State).LastSerial);
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Host_PendingToolResultsNotificationFold_FailureReloadsAuthoritativeWorkingSet() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-host-pending-tool-results-fold-fault-reload-{Guid.NewGuid():N}");

        try {
            var profile = CreateFullFeatureProfile(
                new NoopCompletionClient("provider-toolresults-fold-fault", "spec-toolresults-fold-fault"),
                "model-pending-tool-results-fold-fault"
            );
            var toolResult = CreateToolCallExecutionResult("alpha", "call-1", "tool-output");

            using var host = AgentEngineHost.CreateNew(repoDir);
            AppendObservationActionToolResultsTurn(host.Engine.State, profile.ToCompletionDescriptor(), "seed-observation", toolResult);
            host.Engine.AppendNotification("late-notification");

            ConfigureSessionFaultToThrowOnce(
                GetWorkspaceSession(host),
                AgentWorkspaceSessionFaultPoint.AfterFoldPendingNotificationsIntoCurrentObservationMutation,
                "Injected fault after pending-tool-results notification fold.",
                beforeThrow: () => {
                    ReplaceCachedRecentHistory(host.Engine.State, CreateAssignedObservationEntry(999UL, "ghost-observation"));
                    ReplaceCachedPendingNotifications(host.Engine.State, "ghost-pending");
                    ReplaceCachedLastSerial(host.Engine.State, 999UL);
                }
            );

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StepAsync(profile));

            Assert.Equal("Injected fault after pending-tool-results notification fold.", exception.Message);
            Assert.Empty(host.LoadDurablePendingNotifications());
            AssertObservationActionToolResultsHistory(
                host.LoadDurableRecentHistory(),
                "seed-observation",
                "",
                "late-notification",
                "call-1"
            );

            Assert.Empty(GetCachedPendingNotifications(host.Engine.State));
            Assert.False(host.Engine.State.HasPendingNotification);
            AssertObservationActionToolResultsHistory(
                host.Engine.State.RecentHistory,
                "seed-observation",
                "",
                "late-notification",
                "call-1"
            );
            var localTail = Assert.IsType<ToolResultsEntry>(host.Engine.State.RecentHistory[^1]);
            var localResult = Assert.Single(localTail.Results);
            Assert.Equal("tool-output", localResult.ExecuteResult.GetFlattenedText());
            Assert.Equal(3UL, GetWorkingSet(host.Engine.State).LastSerial);
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
            var durableRecentHistory = reopened.LoadDurableRecentHistory();

            Assert.Equal(2, durableRecentHistory.Count);
            var recap = Assert.IsType<RecapEntry>(durableRecentHistory[0]);
            Assert.Equal("summary-text", recap.Content);
            Assert.Equal(1UL, recap.InsteadSerial);
            Assert.IsType<ActionEntry>(durableRecentHistory[1]);
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task PublicCreateFromStateSnapshot_RestoresResolvedProfileOverlayForToolExecutionWithoutWriteThrough() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-public-snapshot-tool-restore-{Guid.NewGuid():N}");

        try {
            var resolvedProfile = new LlmProfile(
                new NoopCompletionClient("provider-tool", "spec-tool"),
                "model-tool",
                "resolved-profile",
                4096,
                CapabilityProfile.FullFeature
            );
            var nominalProfile = new LlmProfile(
                new NoopCompletionClient("provider-tool", "spec-tool"),
                "model-tool",
                "nominal-profile",
                4096,
                CapabilityProfile.FullFeature
            );
            var expected = CreateWaitingToolResultsSnapshotFixture(resolvedProfile);

            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var workspaceRoot = AgentWorkspaceRoot.Create(revision, "public-snapshot-tool-restore");
            AgentEngineWorkspaceSnapshotHelper.SaveSnapshot(workspaceRoot, expected);

            var engine = AgentEngine.CreateFromStateSnapshot(
                AgentEngineWorkspaceSnapshotHelper.LoadSnapshot(workspaceRoot),
                new LlmProfileRegistry([resolvedProfile]),
                initialTools: [new RecordingTool("alpha")]
            );
            LlmProfile? activeProfile = null;
            engine.ToolExecutionCompleted += (_, args) => activeProfile = args.Profile;

            var step = await engine.StepAsync(nominalProfile);
            var pendingResult = Assert.Single(engine.ExportStateSnapshot().PendingToolResults);
            var persisted = AgentEngineWorkspaceSnapshotHelper.LoadSnapshot(workspaceRoot);

            Assert.True(step.ProgressMade);
            Assert.Same(resolvedProfile, activeProfile);
            Assert.Equal("call-1", pendingResult.ToolCallId);
            Assert.Empty(persisted.PendingToolResults);
            Assert.Equal(expected.ResolvedProfile, persisted.ResolvedProfile);
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task PublicLoadThenCreateFromStateSnapshot_RestoresResolvedProfileOverlayForToolExecutionWithoutWriteThrough() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-public-root-tool-restore-{Guid.NewGuid():N}");

        try {
            var resolvedProfile = new LlmProfile(
                new NoopCompletionClient("provider-tool", "spec-tool"),
                "model-tool",
                "resolved-profile",
                4096,
                CapabilityProfile.FullFeature
            );
            var nominalProfile = new LlmProfile(
                new NoopCompletionClient("provider-tool", "spec-tool"),
                "model-tool",
                "nominal-profile",
                4096,
                CapabilityProfile.FullFeature
            );
            var expected = CreateWaitingToolResultsSnapshotFixture(resolvedProfile);

            using var repo = Repository.Create(repoDir).Unwrap();
            var revision = repo.CreateBranch("main").Unwrap();
            var workspaceRoot = AgentWorkspaceRoot.Create(revision, "public-root-tool-restore");
            AgentEngineWorkspaceSnapshotHelper.SaveSnapshot(workspaceRoot, expected);

            var snapshot = AgentEngineWorkspaceSnapshotHelper.LoadSnapshot(workspaceRoot);
            var engine = AgentEngine.CreateFromStateSnapshot(
                snapshot,
                new LlmProfileRegistry([resolvedProfile]),
                initialTools: [new RecordingTool("alpha")]
            );
            LlmProfile? activeProfile = null;
            engine.ToolExecutionCompleted += (_, args) => activeProfile = args.Profile;

            var step = await engine.StepAsync(nominalProfile);
            var pendingResult = Assert.Single(engine.ExportStateSnapshot().PendingToolResults);
            var persisted = AgentEngineWorkspaceSnapshotHelper.LoadSnapshot(workspaceRoot);

            Assert.True(step.ProgressMade);
            Assert.Same(resolvedProfile, activeProfile);
            Assert.Equal("call-1", pendingResult.ToolCallId);
            Assert.Empty(persisted.PendingToolResults);
            Assert.Equal(expected.ResolvedProfile, persisted.ResolvedProfile);
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public void Host_OpenExisting_TypedDurableQueriesAndRuntimeOverlayRestoreFieldsSeededBySnapshotCompatibilityLayer() {
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
                var workspaceRoot = AgentWorkspaceRoot.Create(revision, "seed-system");
                AgentEngineWorkspaceSnapshotHelper.SaveSnapshot(workspaceRoot, expected);
                repo.Commit(workspaceRoot.Root).Unwrap();
            }

            using var reopened = AgentEngineHost.OpenExisting(
                repoDir,
                new AgentEngineHostRuntime(profileRegistry: new LlmProfileRegistry([restoredProfile]))
            );
            var durableSystemPrompt = reopened.LoadDurableSystemPrompt();
            var durableRecentHistory = reopened.LoadDurableRecentHistory();
            var durablePendingNotifications = reopened.LoadDurablePendingNotifications();
            var durablePendingToolResults = reopened.LoadDurablePendingToolResults();
            var durableTurnRuntime = reopened.LoadDurableTurnRuntime();
            var durablePendingCompaction = reopened.LoadDurablePendingCompaction();
            var durableToolSessionExecutionSequence = reopened.LoadDurableToolSessionExecutionSequence();

            Assert.Equal(expected.AgentState.SystemPrompt, durableSystemPrompt);
            Assert.Equal(expected.AgentState.PendingNotifications, durablePendingNotifications);
            Assert.Equal(expected.AgentState.RecentHistory.Count, durableRecentHistory.Count);
            for (int i = 0; i < expected.AgentState.RecentHistory.Count; i++) {
                AssertHistoryEntry(expected.AgentState.RecentHistory[i], durableRecentHistory[i]);
            }

            Assert.Equal(expected.PendingToolResults.Count, durablePendingToolResults.Count);
            AssertToolCallExecutionResult(expected.PendingToolResults[0], durablePendingToolResults[0]);
            Assert.Equal(expected.ResolvedProfile, durableTurnRuntime.ResolvedProfile);
            Assert.Equal(expected.LockedCompactionSplitIndex, durableTurnRuntime.LockedCompactionSplitIndex);
            Assert.Equal(expected.PendingCompaction, durablePendingCompaction);
            Assert.Equal(expected.ToolSessionExecutionSequence, durableToolSessionExecutionSequence);
            Assert.True(reopened.Engine.CurrentTurnFullFeatureEnabled);
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public void Host_OpenExisting_RejectsResolvedProfileCheckpointWithoutCompatibleRegistry() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-host-runtime-reopen-missing-profile-{Guid.NewGuid():N}");

        try {
            using (var repo = Repository.Create(repoDir).Unwrap()) {
                var revision = repo.CreateBranch("main").Unwrap();
                var workspaceRoot = AgentWorkspaceRoot.Create(revision, "seed-system");
                AgentEngineWorkspaceSnapshotHelper.SaveSnapshot(workspaceRoot, CreateSnapshotFixture());
                repo.Commit(workspaceRoot.Root).Unwrap();
            }

            var ex = Assert.Throws<InvalidOperationException>(
                () => AgentEngineHost.OpenExisting(repoDir)
            );

            Assert.Contains("Persisted agent state contains a resolved LlmProfile checkpoint", ex.Message, StringComparison.Ordinal);
            Assert.Contains("Missing checkpoint", ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("State snapshot contains", ex.Message, StringComparison.Ordinal);
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public void Host_OpenExisting_RejectsResolvedProfileCheckpointWhenRegistrySoftCapDoesNotMatch() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-host-runtime-reopen-soft-cap-mismatch-{Guid.NewGuid():N}");

        try {
            SeedLiveWorkspaceResolvedProfileCheckpoint(
                repoDir,
                new LlmProfileCheckpoint("provider-b", "spec-b", "model-b", "checkpoint-name", 8192)
            );

            var mismatchedProfile = new LlmProfile(
                new NoopCompletionClient("provider-b", "spec-b"),
                "model-b",
                "profile-b-mismatched",
                4096,
                CapabilityProfile.FullFeature
            );

            var ex = Assert.Throws<InvalidOperationException>(
                () => AgentEngineHost.OpenExisting(
                    repoDir,
                    new AgentEngineHostRuntime(profileRegistry: new LlmProfileRegistry([mismatchedProfile]))
                )
            );

            Assert.Contains("Persisted agent state contains a resolved LlmProfile checkpoint", ex.Message, StringComparison.Ordinal);
            Assert.Contains("Missing checkpoint", ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("State snapshot contains", ex.Message, StringComparison.Ordinal);
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public void Host_OpenExisting_RestoresResolvedProfileCheckpointWhenOnlyNameDiffers() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-host-runtime-reopen-name-only-differs-{Guid.NewGuid():N}");

        try {
            var checkpoint = new LlmProfileCheckpoint("provider-b", "spec-b", "model-b", "checkpoint-name", 8192);
            SeedLiveWorkspaceResolvedProfileCheckpoint(repoDir, checkpoint);

            var restoredProfile = new LlmProfile(
                new NoopCompletionClient("provider-b", "spec-b"),
                "model-b",
                "registry-name-different",
                8192,
                CapabilityProfile.FullFeature
            );

            using var reopened = AgentEngineHost.OpenExisting(
                repoDir,
                new AgentEngineHostRuntime(profileRegistry: new LlmProfileRegistry([restoredProfile]))
            );
            var durableTurnRuntime = reopened.LoadDurableTurnRuntime();

            Assert.Equal(checkpoint, durableTurnRuntime.ResolvedProfile);
            Assert.Null(durableTurnRuntime.LockedCompactionSplitIndex);
            Assert.True(reopened.Engine.CurrentTurnFullFeatureEnabled);
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public void CreateFromStateSnapshot_RejectsRegistryWithoutCompatibleSoftCap() {
        var snapshot = CreateSnapshotFixture();
        var mismatchedProfile = new LlmProfile(
            new NoopCompletionClient("provider-b", "spec-b"),
            "model-b",
            "profile-b-mismatched",
            4096,
            CapabilityProfile.FullFeature
        );

        var ex = Assert.Throws<InvalidOperationException>(
            () => AgentEngine.CreateFromStateSnapshot(
                snapshot,
                new LlmProfileRegistry([mismatchedProfile])
            )
        );

        Assert.Contains("no compatible registered or remembered profile", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Persisted agent state contains a resolved LlmProfile checkpoint", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Missing checkpoint", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Host_LiveTurnRuntimeBackfill_ReappliesResolvedProfileOverlayWithoutProfileRegistry() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-host-turn-runtime-backfill-{Guid.NewGuid():N}");

        try {
            var profile = CreateFullFeatureProfile(
                new QueueCompletionClient(new ActionMessage([new ActionBlock.Text("assistant-output")])),
                "model-runtime-backfill"
            );

            using var host = AgentEngineHost.CreateNew(repoDir);
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
            await host.StepAsync(profile);

            Assert.True(host.Engine.CurrentTurnFullFeatureEnabled);

            var turnRuntime = GetRequiredPrivateField<object>(host.Engine, "_turnRuntime");
            _ = InvokeRequiredInstanceMethod(turnRuntime, "RememberCurrentTurnFullFeatureEnabled", false);
            Assert.False(host.Engine.CurrentTurnFullFeatureEnabled);

            _ = InvokeRequiredInstanceMethod(host.Engine, "PersistTurnRuntime", new object?[] { null });

            Assert.True(host.Engine.CurrentTurnFullFeatureEnabled);
            var durableTurnRuntime = host.LoadDurableTurnRuntime();
            Assert.Equal(LlmProfileCheckpoint.FromProfile(profile), durableTurnRuntime.ResolvedProfile);
            Assert.NotNull(durableTurnRuntime.LockedCompactionSplitIndex);
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public void Host_PersistTurnRuntime_FailureReloadsAuthoritativeTurnRuntimeAndLocalOverlay() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-host-turn-runtime-fault-reload-{Guid.NewGuid():N}");

        try {
            var profile = CreateFullFeatureProfile(
                new NoopCompletionClient("provider-turn-fault", "spec-turn-fault"),
                "model-turn-fault"
            );

            using var host = AgentEngineHost.CreateNew(
                repoDir,
                runtime: new AgentEngineHostRuntime(profileRegistry: new LlmProfileRegistry([profile]))
            );

            host.WorkspaceRoot.RuntimeState.UpdateTurnRuntime(LlmProfileCheckpoint.FromProfile(profile), 7);

            var turnRuntime = GetRequiredPrivateField<object>(host.Engine, "_turnRuntime");
            _ = InvokeRequiredInstanceMethod(turnRuntime, "RememberResolvedProfile", profile);
            _ = InvokeRequiredInstanceMethod(turnRuntime, "RememberCompactionSplitIndex", 3);
            _ = InvokeRequiredInstanceMethod(turnRuntime, "RememberCurrentTurnFullFeatureEnabled", false);
            Assert.False(host.Engine.CurrentTurnFullFeatureEnabled);
            Assert.Equal(3, ExportLiveTurnRuntimeOverlay(host.Engine).LockedCompactionSplitIndex);

            ConfigureSessionFaultToThrowOnce(
                GetWorkspaceSession(host),
                AgentWorkspaceSessionFaultPoint.AfterUpdateTurnRuntimeMutation,
                "Injected fault after turn runtime mutation."
            );

            var exception = Assert.Throws<TargetInvocationException>(
                () => InvokeRequiredInstanceMethod(host.Engine, "PersistTurnRuntime", new object?[] { null })
            );

            var inner = Assert.IsType<InvalidOperationException>(exception.InnerException);
            Assert.Equal("Injected fault after turn runtime mutation.", inner.Message);
            var durableTurnRuntime = host.LoadDurableTurnRuntime();
            var localTurnRuntimeOverlay = ExportLiveTurnRuntimeOverlay(host.Engine);

            Assert.Equal(LlmProfileCheckpoint.FromProfile(profile), durableTurnRuntime.ResolvedProfile);
            Assert.Equal(3, durableTurnRuntime.LockedCompactionSplitIndex);
            Assert.True(host.Engine.CurrentTurnFullFeatureEnabled);
            Assert.Equal(LlmProfileCheckpoint.FromProfile(profile), localTurnRuntimeOverlay.ResolvedProfile);
            Assert.Equal(3, localTurnRuntimeOverlay.LockedCompactionSplitIndex);
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Host_OpenExisting_RestoresResolvedProfileOverlayForWaitingToolResults() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-host-resolved-profile-overlay-{Guid.NewGuid():N}");

        try {
            var nominalProfile = CreateFullFeatureProfile(
                new NoopCompletionClient("provider-requested", "spec-requested"),
                "model-requested"
            );
            var resolvedProfile = CreateFullFeatureProfile(
                new QueueCompletionClient(
                    new ActionMessage([
                        new ActionBlock.ToolCall(new RawToolCall("alpha", "call-1", "{}"))
                    ])
                ),
                "model-resolved"
            );

            using (var host = AgentEngineHost.CreateNew(
                       repoDir,
                       runtime: new AgentEngineHostRuntime(initialTools: [
                           new RecordingTool("alpha")
                       ]))) {
                host.Engine.State.AppendObservation(new ObservationEntry(), "seed-observation");
                host.Engine.State.AppendAction(
                    new ActionEntry(
                        new ActionMessage([new ActionBlock.Text("seed-action")]),
                        nominalProfile.ToCompletionDescriptor()
                    )
                );
                host.Engine.WaitingInput += static (_, args) => {
                    args.ShouldContinue = true;
                    args.Observation = IncomingObservation.FromRecentEvents("fresh-observation");
                };
                host.Engine.ResolveProfile += (_, args) => {
                    args.Profile = resolvedProfile;
                };

                await host.StepAsync(nominalProfile);
                await host.StepAsync(nominalProfile);

                var durableTurnRuntime = host.LoadDurableTurnRuntime();
                var durableRecentHistory = host.LoadDurableRecentHistory();
                Assert.Equal(LlmProfileCheckpoint.FromProfile(resolvedProfile), durableTurnRuntime.ResolvedProfile);
                var outputEntry = Assert.IsType<ActionEntry>(durableRecentHistory[^1]);
                Assert.Single(outputEntry.Message.ToolCalls);
            }

            using var reopened = AgentEngineHost.OpenExisting(
                repoDir,
                new AgentEngineHostRuntime(
                    profileRegistry: new LlmProfileRegistry([resolvedProfile]),
                    initialTools: [new RecordingTool("alpha")]
                )
            );

            var toolStep = await reopened.StepAsync(nominalProfile);
            var pendingResult = Assert.Single(reopened.LoadDurablePendingToolResults());

            Assert.True(toolStep.ProgressMade);
            Assert.Equal("call-1", pendingResult.ToolCallId);
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
                    new ActionBlock.ToolCall(new RawToolCall("alpha", "call-1", "{}")),
                    new ActionBlock.ToolCall(new RawToolCall("beta", "call-2", "{}"))
                ])
            );
            var profile = CreateFullFeatureProfile(client, "model-live");
            var sequencesSeenInsideTools = new List<long?>();
            AgentEngineHost? liveHost = null;
            RecordingTool? alphaTool = null;
            RecordingTool? betaTool = null;

            using (var host = AgentEngineHost.CreateNew(
                       repoDir,
                       runtime: new AgentEngineHostRuntime(initialTools: [
                           alphaTool = new RecordingTool("alpha", context => {
                               sequencesSeenInsideTools.Add(liveHost!.LoadDurableToolSessionExecutionSequence());
                               Assert.True(context.Session.TryGetTool("alpha", out var sessionTool));
                               Assert.Same(alphaTool, sessionTool);
                           }),
                           betaTool = new RecordingTool("beta", context => {
                               sequencesSeenInsideTools.Add(liveHost!.LoadDurableToolSessionExecutionSequence());
                               Assert.True(context.Session.TryGetTool("beta", out var sessionTool));
                               Assert.Same(betaTool, sessionTool);
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
                var afterInitialTurnStart = host.LoadDurableTurnRuntime();
                Assert.Null(afterInitialTurnStart.ResolvedProfile);
                Assert.Null(afterInitialTurnStart.LockedCompactionSplitIndex);

                await host.StepAsync(profile);
                var afterModelOutputTurnRuntime = host.LoadDurableTurnRuntime();
                var afterModelOutputPendingMap = GetPendingToolResultsMap(host.WorkspaceRoot);
                Assert.Empty(host.LoadDurablePendingToolResults());
                Assert.Equal(LlmProfileCheckpoint.FromProfile(profile), afterModelOutputTurnRuntime.ResolvedProfile);
                Assert.Equal(1, afterModelOutputTurnRuntime.LockedCompactionSplitIndex);

                await host.StepAsync(profile);
                Assert.Equal([1L], sequencesSeenInsideTools);
                Assert.Same(afterModelOutputPendingMap, GetPendingToolResultsMap(host.WorkspaceRoot));
                var firstPendingResult = Assert.Single(host.LoadDurablePendingToolResults());
                Assert.Equal("call-1", firstPendingResult.ToolCallId);
                Assert.Equal(1L, host.LoadDurableToolSessionExecutionSequence());

                await host.StepAsync(profile);
                Assert.Equal([1L, 2L], sequencesSeenInsideTools);
                Assert.Same(afterModelOutputPendingMap, GetPendingToolResultsMap(host.WorkspaceRoot));
                Assert.Equal(["call-1", "call-2"], host.LoadDurablePendingToolResults()
                    .Select(static result => result.ToolCallId)
                    .OrderBy(static toolCallId => toolCallId, StringComparer.Ordinal));
                Assert.Equal(2L, host.LoadDurableToolSessionExecutionSequence());
            }

            using (var reopened = AgentEngineHost.OpenExisting(
                       repoDir,
                       new AgentEngineHostRuntime(profileRegistry: new LlmProfileRegistry([profile])))) {
                var reopenedPendingToolResults = reopened.LoadDurablePendingToolResults();
                var pendingMapBeforeClear = GetPendingToolResultsMap(reopened.WorkspaceRoot);
                Assert.Equal(["call-1", "call-2"], reopenedPendingToolResults
                    .Select(static result => result.ToolCallId)
                    .OrderBy(static toolCallId => toolCallId, StringComparer.Ordinal));
                Assert.Equal(2L, reopened.LoadDurableToolSessionExecutionSequence());

                reopened.Engine.WaitingInput += static (_, args) => {
                    args.ShouldContinue = true;
                    args.Observation = IncomingObservation.FromRecentEvents("next-turn-observation");
                };

                await reopened.StepAsync(profile);
                var afterToolResultsHistory = reopened.LoadDurableRecentHistory();
                Assert.NotSame(pendingMapBeforeClear, GetPendingToolResultsMap(reopened.WorkspaceRoot));
                Assert.Empty(reopened.LoadDurablePendingToolResults());
                Assert.IsType<ToolResultsEntry>(afterToolResultsHistory[^1]);
                Assert.Equal(2L, reopened.LoadDurableToolSessionExecutionSequence());

                await reopened.StepAsync(profile);
                var afterFinalModelOutput = reopened.LoadDurableTurnRuntime();
                Assert.Equal(LlmProfileCheckpoint.FromProfile(profile), afterFinalModelOutput.ResolvedProfile);

                await reopened.StepAsync(profile);
                var afterNewTurn = reopened.LoadDurableTurnRuntime();
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
    public async Task Host_ToolExecution_RefreshesPendingToolResultsCacheFromDurableWorkspace() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-host-pending-results-refresh-{Guid.NewGuid():N}");

        try {
            var client = new QueueCompletionClient(
                new ActionMessage([
                    new ActionBlock.ToolCall(new RawToolCall("alpha", "call-1", "{}"))
                ])
            );
            var profile = CreateFullFeatureProfile(client, "model-pending-refresh");

            using var host = AgentEngineHost.CreateNew(
                repoDir,
                runtime: new AgentEngineHostRuntime(initialTools: [
                    new RecordingTool("alpha")
                ]));

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
            await host.StepAsync(profile);

            var pendingBeforeToolExecution = GetPendingToolResults(host.Engine);
            pendingBeforeToolExecution["ghost-call"] = CreateToolCallExecutionResult("tool-ghost", "ghost-call", "ghost-output");

            await host.StepAsync(profile);

            var localPendingResults = GetPendingToolResults(host.Engine);
            var durablePendingResult = Assert.Single(host.LoadDurablePendingToolResults());

            Assert.Single(localPendingResults);
            Assert.True(localPendingResults.ContainsKey("call-1"));
            Assert.False(localPendingResults.ContainsKey("ghost-call"));
            Assert.Equal("call-1", durablePendingResult.ToolCallId);
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public void Host_PersistPendingToolResults_FailureReloadsAuthoritativePendingResults() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-host-pending-results-fault-reload-{Guid.NewGuid():N}");

        try {
            using var host = AgentEngineHost.CreateNew(repoDir);

            var durableResult = CreateToolCallExecutionResult("tool-alpha", "call-1", "durable-output");
            var ghostResult = CreateToolCallExecutionResult("tool-ghost", "ghost-call", "ghost-output");

            var localPendingResults = GetPendingToolResults(host.Engine);
            localPendingResults.Clear();
            localPendingResults[durableResult.ToolCallId] = durableResult;
            Assert.Equal("call-1", Assert.Single(localPendingResults).Key);

            ConfigureSessionFaultToThrowOnce(
                GetWorkspaceSession(host),
                AgentWorkspaceSessionFaultPoint.AfterReplacePendingToolResultsMutation,
                "Injected fault after pending tool results mutation.",
                beforeThrow: () => {
                    localPendingResults.Clear();
                    localPendingResults[ghostResult.ToolCallId] = ghostResult;
                }
            );

            var exception = Assert.Throws<TargetInvocationException>(
                () => InvokeRequiredInstanceMethod(host.Engine, "PersistPendingToolResults")
            );

            var inner = Assert.IsType<InvalidOperationException>(exception.InnerException);
            Assert.Equal("Injected fault after pending tool results mutation.", inner.Message);

            var reloadedResult = Assert.Single(localPendingResults).Value;
            Assert.False(localPendingResults.ContainsKey(ghostResult.ToolCallId));
            Assert.NotSame(durableResult, reloadedResult);
            AssertToolCallExecutionResult(durableResult, reloadedResult);
            AssertToolCallExecutionResult(durableResult, Assert.Single(host.LoadDurablePendingToolResults()));
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public void Host_UpsertPendingToolResult_FailureReloadsAuthoritativePendingResults() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-host-pending-results-upsert-fault-reload-{Guid.NewGuid():N}");

        try {
            using var host = AgentEngineHost.CreateNew(repoDir);

            var existingResult = CreateToolCallExecutionResult("tool-alpha", "call-1", "output-1");
            var upsertedResult = CreateToolCallExecutionResult("tool-beta", "call-2", "output-2");
            var ghostResult = CreateToolCallExecutionResult("tool-ghost", "ghost-call", "ghost-output");
            host.WorkspaceRoot.RuntimeState.ReplacePendingToolResults([existingResult]);

            var localPendingResults = GetPendingToolResults(host.Engine);
            localPendingResults.Clear();
            localPendingResults[existingResult.ToolCallId] = existingResult;

            ConfigureSessionFaultToThrowOnce(
                GetWorkspaceSession(host),
                AgentWorkspaceSessionFaultPoint.AfterUpsertPendingToolResultMutation,
                "Injected fault after pending tool result upsert.",
                beforeThrow: () => {
                    localPendingResults.Clear();
                    localPendingResults[ghostResult.ToolCallId] = ghostResult;
                }
            );

            var exception = Assert.Throws<TargetInvocationException>(
                () => InvokeRequiredInstanceMethod(host.Engine, "UpsertPendingToolResult", upsertedResult)
            );

            var inner = Assert.IsType<InvalidOperationException>(exception.InnerException);
            Assert.Equal("Injected fault after pending tool result upsert.", inner.Message);

            Assert.False(localPendingResults.ContainsKey(ghostResult.ToolCallId));
            Assert.Equal(["call-1", "call-2"], localPendingResults.Keys.OrderBy(static key => key, StringComparer.Ordinal));
            AssertToolCallExecutionResult(existingResult, localPendingResults["call-1"]);
            AssertToolCallExecutionResult(upsertedResult, localPendingResults["call-2"]);
            Assert.Equal(["call-1", "call-2"], host.LoadDurablePendingToolResults()
                .Select(static result => result.ToolCallId)
                .OrderBy(static toolCallId => toolCallId, StringComparer.Ordinal));
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Host_BeginNewTurn_ClearsTurnRuntimeWithoutReplacingDurableDict() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-host-turn-runtime-identity-{Guid.NewGuid():N}");

        try {
            var client = new QueueCompletionClient(
                new ActionMessage([new ActionBlock.Text("assistant-output")])
            );
            var profile = CreateFullFeatureProfile(client, "model-live-identity");
            var incomingObservationIndex = 0;

            using var host = AgentEngineHost.CreateNew(repoDir);
            host.Engine.State.AppendObservation(new ObservationEntry(), "seed-observation");
            host.Engine.State.AppendAction(
                new ActionEntry(
                    new ActionMessage([new ActionBlock.Text("seed-action")]),
                    profile.ToCompletionDescriptor()
                )
            );
            host.Engine.WaitingInput += (_, args) => {
                args.ShouldContinue = true;
                args.Observation = IncomingObservation.FromRecentEvents($"fresh-observation-{++incomingObservationIndex}");
            };

            var initialTurnRuntime = GetTurnRuntimeMap(host.WorkspaceRoot);

            await host.StepAsync(profile);
            var afterTurnStart = host.LoadDurableTurnRuntime();
            Assert.Same(initialTurnRuntime, GetTurnRuntimeMap(host.WorkspaceRoot));
            Assert.Null(afterTurnStart.ResolvedProfile);
            Assert.Null(afterTurnStart.LockedCompactionSplitIndex);

            await host.StepAsync(profile);
            var afterModelOutput = host.LoadDurableTurnRuntime();
            Assert.Same(initialTurnRuntime, GetTurnRuntimeMap(host.WorkspaceRoot));
            Assert.Equal(LlmProfileCheckpoint.FromProfile(profile), afterModelOutput.ResolvedProfile);
            Assert.NotNull(afterModelOutput.LockedCompactionSplitIndex);

            await host.StepAsync(profile);
            var afterNextTurnStart = host.LoadDurableTurnRuntime();
            Assert.Same(initialTurnRuntime, GetTurnRuntimeMap(host.WorkspaceRoot));
            Assert.Null(afterNextTurnStart.ResolvedProfile);
            Assert.Null(afterNextTurnStart.LockedCompactionSplitIndex);
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Host_PendingCompactionLiveMutations_KeepDurableRecordIdentity() {
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

            var pendingCompactionRecord = GetPendingCompactionRecord(host.WorkspaceRoot);

            Assert.True(host.Engine.RequestCompaction("compact-system", "compact-now"));
            var requested = host.LoadDurablePendingCompaction();
            Assert.Same(pendingCompactionRecord, GetPendingCompactionRecord(host.WorkspaceRoot));
            Assert.Equal(new CompactionCheckpoint(1, "compact-system", "compact-now"), requested);

            Assert.True(host.Engine.RequestCompaction("compact-system-updated", "compact-now-updated"));
            var updated = host.LoadDurablePendingCompaction();
            Assert.Same(pendingCompactionRecord, GetPendingCompactionRecord(host.WorkspaceRoot));
            Assert.Equal(
                new CompactionCheckpoint(1, "compact-system-updated", "compact-now-updated"),
                updated
            );

            await host.StepAsync(profile);
            var afterCompaction = host.LoadDurableRecentHistory();
            Assert.Same(pendingCompactionRecord, GetPendingCompactionRecord(host.WorkspaceRoot));
            Assert.Null(host.LoadDurablePendingCompaction());
            var recap = Assert.IsType<RecapEntry>(afterCompaction[0]);
            Assert.Equal("summary from live compaction", recap.Content);
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public void Host_PersistPendingCompaction_FailureReloadsAuthoritativePendingCompactionAndLocalOverlay() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-host-compaction-fault-reload-{Guid.NewGuid():N}");

        try {
            using var host = AgentEngineHost.CreateNew(repoDir);

            var durablePendingCompaction = new CompactionCheckpoint(5, "durable-system", "durable-prompt");
            var localPendingCompaction = new CompactionCheckpoint(1, "local-system", "local-prompt");
            host.WorkspaceRoot.RuntimeState.SetPendingCompaction(durablePendingCompaction);
            _ = InvokeRequiredInstanceMethod(host.Engine, "ApplyPendingCompactionSnapshot", localPendingCompaction);
            Assert.Equal(localPendingCompaction, ExportLivePendingCompactionOverlay(host.Engine));

            ConfigureSessionFaultToThrowOnce(
                GetWorkspaceSession(host),
                AgentWorkspaceSessionFaultPoint.AfterUpdatePendingCompactionMutation,
                "Injected fault after pending compaction mutation.",
                beforeThrow: () => _ = InvokeRequiredInstanceMethod(
                    host.Engine,
                    "ApplyPendingCompactionSnapshot",
                    new CompactionCheckpoint(9, "ghost-system", "ghost-prompt")
                )
            );

            var exception = Assert.Throws<TargetInvocationException>(
                () => InvokeRequiredInstanceMethod(host.Engine, "PersistPendingCompaction")
            );

            var inner = Assert.IsType<InvalidOperationException>(exception.InnerException);
            Assert.Equal("Injected fault after pending compaction mutation.", inner.Message);
            var reloadedDurablePendingCompaction = host.LoadDurablePendingCompaction();

            Assert.Equal(localPendingCompaction, reloadedDurablePendingCompaction);
            Assert.Equal(localPendingCompaction, ExportLivePendingCompactionOverlay(host.Engine));
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public void Host_PersistPendingCompactionClear_FailureReloadsAuthoritativePendingCompactionAndLocalOverlay() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-host-compaction-clear-fault-reload-{Guid.NewGuid():N}");

        try {
            using var host = AgentEngineHost.CreateNew(repoDir);

            var seededPendingCompaction = new CompactionCheckpoint(5, "seed-system", "seed-prompt");
            host.WorkspaceRoot.RuntimeState.SetPendingCompaction(seededPendingCompaction);
            _ = InvokeRequiredInstanceMethod(host.Engine, "ApplyPendingCompactionSnapshot", new object?[] { null });
            Assert.Null(ExportLivePendingCompactionOverlay(host.Engine));

            ConfigureSessionFaultToThrowOnce(
                GetWorkspaceSession(host),
                AgentWorkspaceSessionFaultPoint.AfterUpdatePendingCompactionMutation,
                "Injected fault after pending compaction clear.",
                beforeThrow: () => _ = InvokeRequiredInstanceMethod(
                    host.Engine,
                    "ApplyPendingCompactionSnapshot",
                    new CompactionCheckpoint(9, "ghost-system", "ghost-prompt")
                )
            );

            var exception = Assert.Throws<TargetInvocationException>(
                () => InvokeRequiredInstanceMethod(host.Engine, "PersistPendingCompaction")
            );

            var inner = Assert.IsType<InvalidOperationException>(exception.InnerException);
            Assert.Equal("Injected fault after pending compaction clear.", inner.Message);
            var durablePendingCompaction = host.LoadDurablePendingCompaction();

            Assert.Null(durablePendingCompaction);
            Assert.Null(ExportLivePendingCompactionOverlay(host.Engine));
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Host_Dispose_ClosesRetainedEngineSessionInsteadOfSilentlyDetaching() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-host-dispose-session-{Guid.NewGuid():N}");

        try {
            var host = AgentEngineHost.CreateNew(repoDir);
            var engine = host.Engine;
            var disposableApp = new StaticApp("late-app", new RecordingTool("late-app.tool"));
            var disposableTool = new RecordingTool("late-tool");
            var profile = CreateFullFeatureProfile(
                new QueueCompletionClient(new ActionMessage([new ActionBlock.Text("unused")])),
                "model-closed"
            );

            host.Dispose();

            var stateException = Assert.Throws<InvalidOperationException>(() => engine.State.SetSystemPrompt("should-fail"));
            Assert.Equal("AgentState workspace session has been closed.", stateException.Message);

            var engineException = Assert.Throws<InvalidOperationException>(() => engine.AppendNotification("should-fail"));
            Assert.Equal("AgentEngine workspace session has been closed.", engineException.Message);

            var registerAppException = Assert.Throws<InvalidOperationException>(() => engine.RegisterApp(disposableApp));
            Assert.Equal("AgentEngine workspace session has been closed.", registerAppException.Message);

            var removeAppException = Assert.Throws<InvalidOperationException>(() => engine.RemoveApp(disposableApp.Name));
            Assert.Equal("AgentEngine workspace session has been closed.", removeAppException.Message);

            var registerToolException = Assert.Throws<InvalidOperationException>(() => engine.RegisterTool(disposableTool));
            Assert.Equal("AgentEngine workspace session has been closed.", registerToolException.Message);

            var removeToolException = Assert.Throws<InvalidOperationException>(() => engine.RemoveTool(disposableTool.Definition.Name));
            Assert.Equal("AgentEngine workspace session has been closed.", removeToolException.Message);

            var injectException = Assert.Throws<InvalidOperationException>(
                () => engine.InjectActionContent(
                    new ActionInjectionRequest(
                        "should-fail",
                        new InjectionSource(InjectionSourceKind.HostOverride),
                        InjectedActionContentMode.Text
                    )
                )
            );
            Assert.Equal("AgentEngine workspace session has been closed.", injectException.Message);

            var requestCompactionException = Assert.Throws<InvalidOperationException>(
                () => engine.RequestCompaction("compact-system", "compact-now")
            );
            Assert.Equal("AgentEngine workspace session has been closed.", requestCompactionException.Message);

            var stepException = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.StepAsync(profile));
            Assert.Equal("AgentEngine workspace session has been closed.", stepException.Message);
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public void Host_PublicSurface_DoesNotExposeWritableWorkspaceSnapshotHelperSurface() {
        Assert.Null(typeof(AgentEngineHost).GetProperty("StateRoot", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public));
        Assert.DoesNotContain(
            typeof(AgentEngineHost).GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public),
            static method => method.ReturnType == typeof(AgentEngineWorkspaceSnapshotHelper)
        );
    }

    [Fact]
    public void Host_PublicSurface_DoesNotExposeLoadSnapshotQuery() {
        Assert.DoesNotContain(
            typeof(AgentEngineHost).GetMethods(BindingFlags.Instance | BindingFlags.Public),
            static method => method.Name == "LoadSnapshot"
                && method.GetParameters().Length == 0
        );
    }

    [Fact]
    public void AgentEngine_PublicSurface_DoesNotExposeRootBasedRestoreHelper() {
        Assert.DoesNotContain(
            typeof(AgentEngine).GetMethods(BindingFlags.Static | BindingFlags.Public),
            static method => method.Name == "CreateFromRoot"
        );
    }

    [Fact]
    public void AgentEngine_PublicSurface_DoesNotExposeResolverBasedSnapshotRestoreOverload() {
        Assert.DoesNotContain(
            typeof(AgentEngine).GetMethods(BindingFlags.Static | BindingFlags.Public),
            static method => method.Name == "CreateFromStateSnapshot"
                && method.GetParameters() is [
                    { ParameterType: var first },
                    { ParameterType: var second },
                    ..
                ]
                && first == typeof(AgentEngineStateSnapshot)
                && second == typeof(Func<LlmProfileCheckpoint, LlmProfile?>)
        );
    }

    [Fact]
    public void AgentCore_PublicSurface_DoesNotExportWorkspaceSnapshotHelper() {
        Assert.DoesNotContain(
            typeof(AgentEngine).Assembly.GetExportedTypes(),
            static type => type == typeof(AgentEngineWorkspaceSnapshotHelper)
        );
    }

    [Fact]
    public void SnapshotHelper_DoesNotAcceptLiveAgentEngineInput() {
        Assert.DoesNotContain(
            typeof(AgentEngineWorkspaceSnapshotHelper).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic),
            static method => method.Name == "SaveSnapshot"
                && method.GetParameters() is [
                    { ParameterType: var first },
                    { ParameterType: var second }
                ]
                && first == typeof(AgentWorkspaceRoot)
                && second == typeof(AgentEngine)
        );
    }

    [Fact]
    public void AgentState_WorkspaceSessionBinding_IsBornBoundAtRestoreBoundary() {
        Assert.DoesNotContain(
            typeof(AgentState).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            static method => method.Name == "BindWorkspaceSession" && !method.IsPrivate
        );
        Assert.DoesNotContain(
            typeof(AgentState).GetMethods(BindingFlags.Static | BindingFlags.NonPublic),
            static method => method.Name == "RestoreSnapshot"
                && method.GetParameters() is [
                    { ParameterType: var first },
                    { ParameterType: var second }
                ]
                && first == typeof(AgentStateSnapshot)
                && second == typeof(AgentWorkspaceSession)
        );
        Assert.Contains(
            typeof(AgentState).GetMethods(BindingFlags.Static | BindingFlags.NonPublic),
            static method => method.ReturnType == typeof(AgentState)
                && method.GetParameters() is [
                    { ParameterType: var first },
                    ..
                ]
                && first == typeof(AgentWorkspaceSession)
        );
    }

    [Fact]
    public void AgentWorkspaceSession_DoesNotExposeWholeRuntimeSnapshotLoader() {
        Assert.DoesNotContain(
            typeof(AgentWorkspaceSession).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            static method => method.Name == "LoadRuntimeState"
        );
    }

    [Fact]
    public void AgentWorkspaceSession_DoesNotExposeWholeSnapshotLoader() {
        Assert.DoesNotContain(
            typeof(AgentWorkspaceSession).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            static method => method.Name == "LoadSnapshot"
        );
    }

    [Fact]
    public void AgentCore_InternalSurface_DoesNotContainTurnRuntimeSnapshotType() {
        Assert.Null(typeof(AgentEngineStateSnapshot).Assembly.GetType("Atelia.Agent.Core.Persistence.AgentTurnRuntimeStateSnapshot", throwOnError: false));
    }

    [Fact]
    public void AgentCore_InternalSurface_DoesNotContainEngineRuntimeSnapshotType() {
        Assert.Null(typeof(AgentEngineStateSnapshot).Assembly.GetType("Atelia.Agent.Core.Persistence.AgentEngineRuntimeStateSnapshot", throwOnError: false));
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

    private static void SeedLiveWorkspaceResolvedProfileCheckpoint(
        string repoDir,
        LlmProfileCheckpoint checkpoint
    ) {
        using var repo = Repository.Create(repoDir).Unwrap();
        var revision = repo.CreateBranch("main").Unwrap();
        var workspaceRoot = AgentWorkspaceRoot.Create(revision, "seed-system");
        workspaceRoot.RuntimeState.SetResolvedProfile(checkpoint);
        repo.Commit(workspaceRoot.Root).Unwrap();
    }

    private static AgentEngineStateSnapshot CreateWaitingToolResultsSnapshotFixture(LlmProfile resolvedProfile) {
        ArgumentNullException.ThrowIfNull(resolvedProfile);

        var state = AgentState.CreateDefault("tool-restore-system");
        state.AppendObservation(new ObservationEntry(), "seed-observation");
        state.AppendAction(
            new ActionEntry(
                new ActionMessage([
                    new ActionBlock.ToolCall(new RawToolCall("alpha", "call-1", "{}"))
                ]),
                resolvedProfile.ToCompletionDescriptor()
            )
        );

        var snapshot = new AgentEngine(state: state).ExportStateSnapshot();
        return snapshot with {
            ResolvedProfile = LlmProfileCheckpoint.FromProfile(resolvedProfile),
            LockedCompactionSplitIndex = 1
        };
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

    private static void AssertObservationActionObservationHistory(
        IReadOnlyList<HistoryEntry> history,
        string firstObservationNotifications,
        string actionText,
        string secondObservationNotifications
    ) {
        Assert.Collection(
            history,
            entry => {
                var observation = Assert.IsType<ObservationEntry>(entry);
                Assert.Equal(1UL, observation.Serial);
                Assert.Equal(firstObservationNotifications, observation.Notifications);
            },
            entry => {
                var action = Assert.IsType<ActionEntry>(entry);
                Assert.Equal(2UL, action.Serial);
                Assert.Equal(actionText, action.Message.GetFlattenedText());
            },
            entry => {
                var observation = Assert.IsType<ObservationEntry>(entry);
                Assert.Equal(3UL, observation.Serial);
                Assert.Equal(secondObservationNotifications, observation.Notifications);
            }
        );
    }

    private static void AssertObservationActionObservationActionHistory(
        IReadOnlyList<HistoryEntry> history,
        string firstObservationNotifications,
        string firstActionText,
        ulong secondObservationSerial,
        string secondObservationNotifications,
        ulong secondActionSerial,
        string secondActionText
    ) {
        Assert.Collection(
            history,
            entry => {
                var observation = Assert.IsType<ObservationEntry>(entry);
                Assert.Equal(1UL, observation.Serial);
                Assert.Equal(firstObservationNotifications, observation.Notifications);
            },
            entry => {
                var action = Assert.IsType<ActionEntry>(entry);
                Assert.Equal(2UL, action.Serial);
                Assert.Equal(firstActionText, action.Message.GetFlattenedText());
            },
            entry => {
                var observation = Assert.IsType<ObservationEntry>(entry);
                Assert.Equal(secondObservationSerial, observation.Serial);
                Assert.Equal(secondObservationNotifications, observation.Notifications);
            },
            entry => {
                var action = Assert.IsType<ActionEntry>(entry);
                Assert.Equal(secondActionSerial, action.Serial);
                Assert.Equal(secondActionText, action.Message.GetFlattenedText());
            }
        );
    }

    private static void AssertObservationActionToolResultsHistory(
        IReadOnlyList<HistoryEntry> history,
        string observationNotifications,
        string actionText,
        string toolResultsNotifications,
        string toolCallId
    ) {
        Assert.Collection(
            history,
            entry => {
                var observation = Assert.IsType<ObservationEntry>(entry);
                Assert.Equal(1UL, observation.Serial);
                Assert.Equal(observationNotifications, observation.Notifications);
            },
            entry => {
                var action = Assert.IsType<ActionEntry>(entry);
                Assert.Equal(2UL, action.Serial);
                Assert.Equal(actionText, action.Message.GetFlattenedText());
            },
            entry => {
                var toolResults = Assert.IsType<ToolResultsEntry>(entry);
                Assert.Equal(3UL, toolResults.Serial);
                Assert.Equal(toolResultsNotifications, toolResults.Notifications);
                var result = Assert.Single(toolResults.Results);
                Assert.Equal(toolCallId, result.ToolCallId);
            }
        );
    }

    private static void AssertObservationActionHistory(
        IReadOnlyList<HistoryEntry> history,
        string observationNotifications,
        string actionText
    ) {
        Assert.Collection(
            history,
            entry => {
                var observation = Assert.IsType<ObservationEntry>(entry);
                Assert.Equal(1UL, observation.Serial);
                Assert.Equal(observationNotifications, observation.Notifications);
            },
            entry => {
                var action = Assert.IsType<ActionEntry>(entry);
                Assert.Equal(2UL, action.Serial);
                Assert.Equal(actionText, action.Message.GetFlattenedText());
            }
        );
    }

    private static void AssertRecapActionHistory(
        IReadOnlyList<HistoryEntry> history,
        string recapContent,
        ulong insteadSerial,
        string actionText
    ) {
        Assert.Collection(
            history,
            entry => {
                var recap = Assert.IsType<RecapEntry>(entry);
                Assert.Equal(3UL, recap.Serial);
                Assert.Equal(recapContent, recap.Content);
                Assert.Equal(insteadSerial, recap.InsteadSerial);
            },
            entry => {
                var action = Assert.IsType<ActionEntry>(entry);
                Assert.Equal(2UL, action.Serial);
                Assert.Equal(actionText, action.Message.GetFlattenedText());
            }
        );
    }

    private static void AssertSingleObservationRequestContent(CompletionRequest request, string expectedContent) {
        var observation = Assert.IsType<ObservationMessage>(Assert.Single(request.Context));
        Assert.Equal(expectedContent, observation.Content);
    }

    private static void AssertLastToolResultsRequestContentAndResults(
        CompletionRequest request,
        string expectedContent,
        params string[] expectedToolCallIds
    ) {
        var toolResults = Assert.IsType<ToolResultsMessage>(request.Context[^1]);
        Assert.Equal(expectedContent, toolResults.Content);
        Assert.Equal(expectedToolCallIds, toolResults.Results.Select(static result => result.ToolCallId));
    }

    private static void AssertObservationActionInjectionHistory(
        IReadOnlyList<HistoryEntry> history,
        string observationNotifications,
        string actionText,
        string injectionContent,
        ActionBlockKind injectionBlockKind
    ) {
        Assert.Collection(
            history,
            entry => {
                var observation = Assert.IsType<ObservationEntry>(entry);
                Assert.Equal(1UL, observation.Serial);
                Assert.Equal(observationNotifications, observation.Notifications);
            },
            entry => {
                var action = Assert.IsType<ActionEntry>(entry);
                Assert.Equal(2UL, action.Serial);
                Assert.Equal(actionText, action.Message.GetFlattenedText());
            },
            entry => {
                var injection = Assert.IsType<InjectionEntry>(entry);
                Assert.Equal(3UL, injection.Serial);
                Assert.Equal(injectionContent, injection.Content);
                Assert.Equal(injectionBlockKind, injection.BlockKind);
            }
        );
    }

    private static LlmProfile CreateFullFeatureProfile(ICompletionClient client, string modelId) {
        return CreateFullFeatureProfile(client, modelId, 4096);
    }

    private static LlmProfile CreateFullFeatureProfile(ICompletionClient client, string modelId, int softContextTokenCap) {
        return new LlmProfile(client, modelId, $"{modelId}-profile", checked((uint)softContextTokenCap), CapabilityProfile.FullFeature);
    }

    private static ToolResultsEntry AppendObservationActionToolResultsTurn(
        AgentState state,
        CompletionDescriptor invocation,
        string observationNotifications,
        ToolCallExecutionResult result
    ) {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(result);

        state.AppendObservation(new ObservationEntry(), observationNotifications);
        state.AppendAction(
            new ActionEntry(
                new ActionMessage([
                    new ActionBlock.ToolCall(result.RawToolCall)
                ]),
                invocation
            )
        );
        return state.AppendToolResults(new ToolResultsEntry([result]));
    }

    private static DurableDeque GetHistoryDeque(AgentWorkspaceRoot workspaceRoot) {
        return workspaceRoot.Root.Get<DurableDeque>("history", out var history) == GetIssue.None
            ? history!
            : throw new InvalidOperationException("Workspace root is missing history deque.");
    }

    private static DurableDeque GetPendingNotificationsDeque(AgentWorkspaceRoot workspaceRoot) {
        return workspaceRoot.Root.Get<DurableDeque>("pendingNotifications", out var pendingNotifications) == GetIssue.None
            ? pendingNotifications!
            : throw new InvalidOperationException("Workspace root is missing pendingNotifications deque.");
    }

    private static DurableDict<string> GetPendingToolResultsMap(AgentWorkspaceRoot workspaceRoot) {
        return workspaceRoot.Root.Get<DurableDict<string>>("pendingToolResults", out var pendingToolResults) == GetIssue.None
            ? pendingToolResults!
            : throw new InvalidOperationException("Workspace root is missing pendingToolResults map.");
    }

    private static DurableDict<string> GetTurnRuntimeMap(AgentWorkspaceRoot workspaceRoot) {
        return workspaceRoot.Root.Get<DurableDict<string>>("turnRuntime", out var turnRuntime) == GetIssue.None
            ? turnRuntime!
            : throw new InvalidOperationException("Workspace root is missing turnRuntime map.");
    }

    private static DurableDict<string> GetPendingCompactionRecord(AgentWorkspaceRoot workspaceRoot) {
        return workspaceRoot.Root.Get<DurableDict<string>>("pendingCompaction", out var pendingCompaction) == GetIssue.None
            ? pendingCompaction!
            : throw new InvalidOperationException("Workspace root is missing pendingCompaction record.");
    }

    private static AgentWorkspaceSession GetWorkspaceSession(AgentEngineHost host) {
        return GetRequiredPrivateField<AgentWorkspaceSession>(host, "_workspaceSession");
    }

    private static void ConfigureSessionFaultToThrowOnce(
        AgentWorkspaceSession session,
        AgentWorkspaceSessionFaultPoint targetPoint,
        string message,
        Action? beforeThrow = null
    ) {
        var fired = false;
        session.FaultInjectionForTesting = point => {
            if (point != targetPoint || fired) {
                return null;
            }

            fired = true;
            beforeThrow?.Invoke();
            return new InvalidOperationException(message);
        };
    }

    private static string[] GetCachedPendingNotifications(AgentState state) {
        return GetRequiredPrivateField<ConcurrentQueue<string>>(GetWorkingSet(state), "_pendingNotifications").ToArray();
    }

    private static Dictionary<string, ToolCallExecutionResult> GetPendingToolResults(AgentEngine engine) {
        return GetRequiredPrivateField<Dictionary<string, ToolCallExecutionResult>>(engine, "_pendingToolResults");
    }

    private static (LlmProfileCheckpoint? ResolvedProfile, int? LockedCompactionSplitIndex) ExportLiveTurnRuntimeOverlay(AgentEngine engine) {
        return ((LlmProfileCheckpoint? ResolvedProfile, int? LockedCompactionSplitIndex))
            (InvokeRequiredInstanceMethod(engine, "ExportTurnRuntimeState")
             ?? throw new InvalidOperationException("ExportTurnRuntimeState returned null."));
    }

    private static CompactionCheckpoint? ExportLivePendingCompactionOverlay(AgentEngine engine) {
        return (CompactionCheckpoint?)InvokeRequiredInstanceMethod(engine, "ExportPendingCompactionSnapshot");
    }

    private static void InvokeRewriteRecentHistoryTail(
        AgentState state,
        ulong anchorSerial,
        params HistoryEntry[] replacementEntries
    ) {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(replacementEntries);
        _ = InvokeRequiredInstanceMethod(state, "RewriteRecentHistoryTail", anchorSerial, replacementEntries);
    }

    private static void ReplaceCachedPendingNotifications(AgentState state, params string[] notifications) {
        var queue = GetRequiredPrivateField<ConcurrentQueue<string>>(GetWorkingSet(state), "_pendingNotifications");
        while (queue.TryDequeue(out _)) { }

        foreach (var notification in notifications) {
            queue.Enqueue(notification);
        }
    }

    private static void ReplaceCachedRecentHistory(AgentState state, params HistoryEntry[] entries) {
        var recentHistory = GetRequiredPrivateField<List<HistoryEntry>>(GetWorkingSet(state), "_recentHistory");
        recentHistory.Clear();
        recentHistory.AddRange(entries);
    }

    private static void ReplaceCachedLastSerial(AgentState state, ulong lastSerial) {
        GetWorkingSet(state).RememberAllocatedSerial(lastSerial);
    }

    private static AgentStateWorkingSet GetWorkingSet(AgentState state) {
        return GetRequiredPrivateField<AgentStateWorkingSet>(state, "_workingSet");
    }

    private static T GetRequiredPrivateField<T>(object instance, string fieldName) where T : class {
        var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Field '{fieldName}' was not found on '{instance.GetType().FullName}'.");

        return field.GetValue(instance) as T
            ?? throw new InvalidOperationException($"Field '{fieldName}' on '{instance.GetType().FullName}' was null or not a '{typeof(T).FullName}'.");
    }

    private static object? InvokeRequiredInstanceMethod(object instance, string methodName, params object?[]? arguments) {
        arguments ??= [];
        var method = instance.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .FirstOrDefault(candidate =>
                candidate.Name == methodName
                && ParametersMatch(candidate.GetParameters(), arguments))
            ?? throw new InvalidOperationException(
                $"Method '{methodName}' with {arguments.Length} argument(s) was not found on '{instance.GetType().FullName}'."
            );

        return method.Invoke(instance, arguments);
    }

    private static bool ParametersMatch(ParameterInfo[] parameters, object?[] arguments) {
        if (parameters.Length != arguments.Length) {
            return false;
        }

        for (var index = 0; index < parameters.Length; index++) {
            var argument = arguments[index];
            if (argument is null) {
                if (parameters[index].ParameterType.IsValueType
                    && Nullable.GetUnderlyingType(parameters[index].ParameterType) is null) {
                    return false;
                }

                continue;
            }

            if (!parameters[index].ParameterType.IsInstanceOfType(argument)) {
                return false;
            }
        }

        return true;
    }

    private static ObservationEntry CreateAssignedObservationEntry(ulong serial, string? notifications = null) {
        var entry = new ObservationEntry();
        if (notifications is not null) {
            entry.AssignNotifications(notifications);
        }
        entry.AssignTokenEstimate(1);
        entry.AssignSerial(serial);
        return entry;
    }

    private static ActionEntry CreateAssignedActionEntry(ulong serial, string content) {
        var entry = new ActionEntry(
            new ActionMessage([new ActionBlock.Text(content)]),
            new CompletionDescriptor("provider-stale", "spec-stale", "model-stale")
        );
        entry.AssignTokenEstimate(1);
        entry.AssignSerial(serial);
        return entry;
    }

    private static ActionEntry CreateAssignedActionEntryWithToolCall(ulong serial, string toolCallId) {
        var entry = new ActionEntry(
            new ActionMessage([
                new ActionBlock.ToolCall(new RawToolCall("tool-stale", toolCallId, "{}"))
            ]),
            new CompletionDescriptor("provider-stale", "spec-stale", "model-stale")
        );
        entry.AssignTokenEstimate(1);
        entry.AssignSerial(serial);
        return entry;
    }

    private static ActionEntry CreateActionEntryWithReasoningTail(string textContent, string reasoningContent) {
        var invocation = new CompletionDescriptor("provider-a", "spec-a", "model-a");
        return new ActionEntry(
            new ActionMessage([
                new ActionBlock.Text(textContent),
                new ActionBlock.TextReasoningBlock(reasoningContent, invocation)
            ]),
            invocation
        );
    }

    private static InjectionEntry CreateAssignedInjectionEntry(ulong serial, string content) {
        var entry = new InjectionEntry(
            content,
            ActionBlockKind.Text,
            new InjectionSource(InjectionSourceKind.HostOverride)
        );
        entry.AssignTokenEstimate(1);
        entry.AssignSerial(serial);
        return entry;
    }

    private static ToolCallExecutionResult CreateToolCallExecutionResult(string toolName, string toolCallId, string outputText) {
        return new ToolCallExecutionResult(
            new RawToolCall(toolName, toolCallId, "{}"),
            ToolExecuteResult.FromText(ToolExecutionStatus.Success, outputText),
            TimeSpan.FromMilliseconds(1)
        );
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

    private sealed class StaticApp : IApp {
        public StaticApp(string name, params ITool[] tools) {
            Name = name;
            Description = $"App {name}.";
            Tools = tools;
        }

        public string Name { get; }

        public string Description { get; }

        public IReadOnlyList<ITool> Tools { get; }

        public AppProjection Render(AppRenderContext context) {
            _ = context;
            return new AppProjection(Window: null);
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

    private sealed class RecordingQueueCompletionClient : ICompletionClient {
        private readonly Queue<object> _outcomes;

        public RecordingQueueCompletionClient(params object[] outcomes) {
            _outcomes = new Queue<object>(outcomes);
        }

        public string Name => "test-provider";

        public string ApiSpecId => "test-spec";

        public List<CompletionRequest> Requests { get; } = [];

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            _ = observer;
            _ = cancellationToken;
            Requests.Add(request);

            var outcome = _outcomes.Count > 0
                ? _outcomes.Dequeue()
                : new ActionMessage([new ActionBlock.Text("default-output")]);

            return outcome switch {
                Exception exception => Task.FromException<CompletionResult>(exception),
                ActionMessage message => Task.FromResult(
                    new CompletionResult(
                        message,
                        new CompletionDescriptor(Name, ApiSpecId, request.ModelId)
                    )
                ),
                _ => throw new InvalidOperationException($"Unsupported scripted completion outcome type: {outcome.GetType().FullName}")
            };
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
