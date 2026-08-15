using Atelia.Agent.Core;
using Atelia.Agent.Core.History;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;
using Xunit;

namespace Agent.Core.Tests;

public sealed class AgentEngineCapabilityGateTests {
    [Fact]
    public async Task StepAsync_Throws_WhenInputProfileIsNotFullFeature() {
        var client = new QueueCompletionClient(
            new ActionMessage([new ActionBlock.Text("assistant-output")])
        );
        var profile = CreateProfile(client, "model-a", CapabilityProfile.BasicExecutionOnly);
        var engine = new AgentEngine();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.StepAsync(profile));

        Assert.Contains("SupportsAgentCoreFullFeatures == true", ex.Message, StringComparison.Ordinal);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task StepAsync_Throws_WhenResolveProfileProducesNonFullFeatureProfile() {
        var client = new QueueCompletionClient(
            new ActionMessage([new ActionBlock.Text("should-not-be-used")])
        );
        var inputProfile = CreateProfile(client, "model-a", CapabilityProfile.FullFeature);
        var resolvedProfile = CreateProfile(client, "model-b", CapabilityProfile.BasicExecutionOnly);
        var engine = new AgentEngine();

        engine.WaitingInput += static (_, args) => {
            args.ShouldContinue = true;
            args.Observation = IncomingObservation.FromRecentEvents("recent-events");
        };
        engine.ResolveProfile += (_, args) => args.Profile = resolvedProfile;

        await engine.StepAsync(inputProfile);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.StepAsync(inputProfile));

        Assert.Contains("ResolveProfile result", ex.Message, StringComparison.Ordinal);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task StepAsync_TurnBoundaryCanSwitchBetweenFullFeatureProfiles() {
        var firstClient = new QueueCompletionClient(
            new ActionMessage([new ActionBlock.Text("first-turn-output")])
        );
        var secondClient = new QueueCompletionClient(
            new ActionMessage([new ActionBlock.Text("second-turn-output")])
        );
        var firstProfile = CreateProfile(firstClient, "model-a", CapabilityProfile.FullFeature);
        var secondProfile = CreateProfile(secondClient, "model-b", CapabilityProfile.FullFeature);
        var engine = new AgentEngine();

        engine.WaitingInput += static (_, args) => {
            args.ShouldContinue = true;
            args.Observation = IncomingObservation.FromRecentEvents("recent-events");
        };

        await engine.StepAsync(firstProfile);
        Assert.Null(engine.CurrentTurnFullFeatureEnabled);

        await engine.StepAsync(firstProfile);
        Assert.True(engine.CurrentTurnFullFeatureEnabled);

        await engine.StepAsync(secondProfile);
        Assert.Null(engine.CurrentTurnFullFeatureEnabled);

        await engine.StepAsync(secondProfile);
        Assert.True(engine.CurrentTurnFullFeatureEnabled);
    }

    [Fact]
    public async Task StepAsync_SameTurnCapabilitySwitch_ThrowsEvenWhenDescriptorMatches() {
        var client = new QueueCompletionClient(
            new ActionMessage([
                new ActionBlock.ToolCall(new RawToolCall("alpha", "call-1", "{}"))
            ]),
            new ActionMessage([new ActionBlock.Text("second-call-output")])
        );
        var fullProfile = CreateProfile(client, "model-a", CapabilityProfile.FullFeature);
        var alternateFullProfile = CreateProfile(
            client,
            "model-a",
            new CapabilityProfile(
                ThinkingIsVisibleAsText: true,
                ThinkingReplayWithinTurnIsSupported: true,
                RuntimeAuthoredReasoningReplayIsSupported: true,
                RuntimeMayEditActorContinuation: true,
                AssistantPrefixContinuationIsStable: true,
                Notes: "alternate-full-feature-shape"
            )
        );
        var engine = new AgentEngine(
            initialTools: [new RecordingTool("alpha")]
        );

        engine.WaitingInput += static (_, args) => {
            args.ShouldContinue = true;
            args.Observation = IncomingObservation.FromRecentEvents("recent-events");
        };

        await engine.StepAsync(fullProfile);
        await engine.StepAsync(fullProfile);
        await engine.StepAsync(alternateFullProfile);
        await engine.StepAsync(alternateFullProfile);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.StepAsync(alternateFullProfile));

        Assert.Contains("capability switch", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(client.Requests);
    }

    private static LlmProfile CreateProfile(ICompletionClient client, string modelId, CapabilityProfile capabilities) {
        return new LlmProfile(client, modelId, $"{modelId}-profile", 4096, capabilities);
    }

    private static ActionEntry CreateAction(string content, LlmProfile profile) {
        return new ActionEntry(
            new ActionMessage([new ActionBlock.Text(content)]),
            new CompletionDescriptor(profile.Client.Name, profile.Client.ApiSpecId, profile.ModelId)
        );
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

    private sealed class QueueCompletionClient : ICompletionClient {
        private readonly Queue<ActionMessage> _messages;

        public QueueCompletionClient(params ActionMessage[] messages) {
            _messages = new Queue<ActionMessage>(messages);
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
}
