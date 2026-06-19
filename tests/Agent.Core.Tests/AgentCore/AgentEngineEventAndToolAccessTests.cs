using Atelia.Agent.Core;
using Atelia.Agent.Core.History;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;
using Xunit;

namespace Agent.Core.Tests;

public sealed class AgentEngineEventAndToolAccessTests {
    [Fact]
    public async Task ActionProduced_SeesDurableStateAlreadyCommitted_WhenUsingAgentEngineHost() {
        var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-agent-engine-host-{Guid.NewGuid():N}");

        try {
            using var host = AgentEngineHost.CreateNew(repoDir);
            var profile = new LlmProfile(
                new RecordingCompletionClient(new ActionMessage([new ActionBlock.Text("assistant-output")])),
                "model-a",
                "test-profile",
                4096,
                CapabilityProfile.FullFeature
            );

            host.Engine.WaitingInput += static (_, args) => {
                args.ShouldContinue = true;
                args.Observation = IncomingObservation.FromRecentEvents("recent-events");
            };

            var observedCountAtEvent = -1;
            HistoryEntryKind? observedLastKind = null;
            host.Engine.ActionProduced += (_, _) => {
                var snapshot = host.LoadSnapshot();
                observedCountAtEvent = snapshot.AgentState.RecentHistory.Count;
                observedLastKind = snapshot.AgentState.RecentHistory[^1].Kind;
            };

            await host.StepAsync(profile);
            await host.StepAsync(profile);

            Assert.Equal(2, observedCountAtEvent);
            Assert.Equal(HistoryEntryKind.Action, observedLastKind);
        }
        finally {
            if (Directory.Exists(repoDir)) {
                Directory.Delete(repoDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task StepAsync_AppToolAccessSnapshotsComposeByIntersection() {
        var client = new RecordingCompletionClient(new ActionMessage([new ActionBlock.Text("assistant-output")]));
        var profile = new LlmProfile(client, "model-a", "test-profile", 4096, CapabilityProfile.FullFeature);
        var engine = new AgentEngine(
            initialApps: [
                new StaticProjectionApp("allow-only-app", ToolAccessSnapshot.AllowOnly(["alpha", "beta"])),
                new StaticProjectionApp("hide-app", ToolAccessSnapshot.Hide(["beta"]))
            ],
            initialTools: [new RecordingTool("alpha"), new RecordingTool("beta"), new RecordingTool("gamma")]
        );

        engine.WaitingInput += static (_, args) => {
            args.ShouldContinue = true;
            args.Observation = IncomingObservation.FromRecentEvents("recent-events");
        };

        await engine.StepAsync(profile);
        await engine.StepAsync(profile);

        var request = Assert.Single(client.Requests);
        var visibleTool = Assert.Single(request.Tools);
        Assert.Equal("alpha", visibleTool.Name);
    }

    [Fact]
    public async Task StepAsync_PrepareInvocationToolAccessOverride_CannotWidenAppProjection() {
        var client = new RecordingCompletionClient(new ActionMessage([new ActionBlock.Text("assistant-output")]));
        var profile = new LlmProfile(client, "model-a", "test-profile", 4096, CapabilityProfile.FullFeature);
        var engine = new AgentEngine(
            initialApps: [
                new StaticProjectionApp("hide-beta-app", ToolAccessSnapshot.Hide(["beta"]))
            ],
            initialTools: [new RecordingTool("beta"), new RecordingTool("gamma")]
        );

        engine.WaitingInput += static (_, args) => {
            args.ShouldContinue = true;
            args.Observation = IncomingObservation.FromRecentEvents("recent-events");
        };
        engine.PrepareInvocationAsync = static (args, _) => {
            args.ToolAccessOverride = ToolAccessSnapshot.AllowOnly(["beta", "gamma"]);
            return Task.CompletedTask;
        };

        await engine.StepAsync(profile);
        await engine.StepAsync(profile);

        var request = Assert.Single(client.Requests);
        var visibleTool = Assert.Single(request.Tools);
        Assert.Equal("gamma", visibleTool.Name);
    }

    private sealed class StaticProjectionApp : IApp {
        private readonly string _name;
        private readonly ToolAccessSnapshot _toolAccessSnapshot;

        public StaticProjectionApp(string name, ToolAccessSnapshot toolAccessSnapshot) {
            _name = name;
            _toolAccessSnapshot = toolAccessSnapshot;
        }

        public string Name => _name;

        public string Description => "Test app";

        public IReadOnlyList<ITool> Tools => Array.Empty<ITool>();

        public AppProjection Render(AppRenderContext context) {
            _ = context;
            return new AppProjection(Window: null, ToolAccessSnapshot: _toolAccessSnapshot);
        }
    }

    private sealed class RecordingTool : ITool {
        public RecordingTool(string name) {
            Definition = new ToolDefinition(name, $"Tool {name}.", new ToolSchema.Object());
        }

        public ToolDefinition Definition { get; }

        public ValueTask<ToolExecuteResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken) {
            _ = context;
            _ = cancellationToken;
            return ValueTask.FromResult(ToolExecuteResult.FromText(ToolExecutionStatus.Success, "ok"));
        }
    }

    private sealed class RecordingCompletionClient : ICompletionClient {
        private readonly ActionMessage _message;

        public RecordingCompletionClient(ActionMessage message) {
            _message = message;
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
            return Task.FromResult(
                new CompletionResult(
                    _message,
                    new CompletionDescriptor(Name, ApiSpecId, request.ModelId)
                )
            );
        }
    }
}
