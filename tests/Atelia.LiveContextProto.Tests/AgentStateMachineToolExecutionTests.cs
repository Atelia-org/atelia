using Atelia.Agent;
using Atelia.Agent.Core;
using Atelia.Agent.Core.History;
using Atelia.Agent.Core.Tool;
using Atelia.LiveContextProto;
using Atelia.Completion.Abstractions;
using System.Collections.Immutable;
using Xunit;

namespace Atelia.LiveContextProto.Tests;

public sealed class AgentStateMachineToolExecutionTests {
    private const string StrategyId = "test-strategy";
    private const string ProviderId = "test-provider";
    private const string Specification = "spec";
    private const string Model = "model";

    [Fact]
    public async Task DoStepAsync_CompletesToolCallLifecycle() {
        var state = AgentState.CreateDefault();

        var toolInvocations = new List<IReadOnlyDictionary<string, object?>?>();
        var echoTool = new DelegateTool(
            "echo",
            arguments => {
                toolInvocations.Add(arguments);
                return LodToolExecuteResult.FromContent(
                    ToolExecutionStatus.Success,
                    UniformContent("tool-output")
                );
            }
        );

        var firstResponse = CreateDeltaSequence(
            CompletionChunk.FromContent("calling tool"),
            CompletionChunk.FromToolCall(
                CreateToolCallRequest(
                    "echo",
                    "call-1",
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["payload"] = "value" },
                    new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["payload"] = "value" }
                )
            )
        );

        var secondResponse = CreateDeltaSequence(
            CompletionChunk.FromContent("tool complete")
        );

        var provider = new FakeProviderClient(new[] { firstResponse, secondResponse });
        var profile = new LlmProfile(Client: provider, ModelId: Model, Name: StrategyId);
        var agent = new CharacterAgent(state, new[] { echoTool });

        agent.AppendNotification("hello world");

        var step1 = await agent.DoStepAsync(profile);
        Assert.True(step1.ProgressMade);
        Assert.NotNull(step1.Input);
        Assert.Equal(AgentRunState.WaitingInput, step1.StateBefore);
        Assert.Equal(AgentRunState.PendingInput, step1.StateAfter);

        var step2 = await agent.DoStepAsync(profile);
        Assert.True(step2.ProgressMade);
        Assert.NotNull(step2.Output);
        Assert.Equal(AgentRunState.PendingInput, step2.StateBefore);
        Assert.Equal(AgentRunState.WaitingToolResults, step2.StateAfter);
        Assert.Single(step2.Output!.ToolCalls);

        var step3 = await agent.DoStepAsync(profile);
        Assert.True(step3.ProgressMade);
        Assert.Null(step3.Output);
        Assert.Equal(AgentRunState.WaitingToolResults, step3.StateBefore);
        Assert.Equal(AgentRunState.ToolResultsReady, step3.StateAfter);

        var step4 = await agent.DoStepAsync(profile);
        Assert.True(step4.ProgressMade);
        Assert.NotNull(step4.ToolResults);
        Assert.Equal(AgentRunState.ToolResultsReady, step4.StateBefore);
        Assert.Equal(AgentRunState.PendingToolResults, step4.StateAfter);

        var toolResults = step4.ToolResults!;
        Assert.Single(toolResults.Results);
        Assert.Null(toolResults.ExecuteError);

        var historyResult = toolResults.Results[0];
        Assert.Equal(ToolExecutionStatus.Success, historyResult.Status);
        var plainText = historyResult.Result.Basic;
        Assert.Contains("tool-output", plainText, StringComparison.Ordinal);

        Assert.Single(toolInvocations);
        var capturedArguments = toolInvocations[0];
        Assert.NotNull(capturedArguments);
        Assert.Equal("value", capturedArguments!["payload"]);

        Assert.Equal("call-1", historyResult.ToolCallId);
        Assert.Equal("echo", historyResult.ToolName);

        var step5 = await agent.DoStepAsync(profile);
        Assert.True(step5.ProgressMade);
        Assert.NotNull(step5.Output);
        Assert.Equal(AgentRunState.PendingToolResults, step5.StateBefore);
        Assert.Equal(AgentRunState.WaitingInput, step5.StateAfter);

        Assert.Equal(4, state.History.Count);
        Assert.IsType<ObservationEntry>(state.History[0]);
        Assert.IsType<ActionEntry>(state.History[1]);
        Assert.IsType<ToolEntry>(state.History[2]);
        Assert.IsType<ActionEntry>(state.History[3]);
    }

    [Fact]
    public async Task DoStepAsync_ToolFailureProducesExecuteError() {
        var state = AgentState.CreateDefault();

        var failingTool = new DelegateTool(
            "broken",
            _ => LodToolExecuteResult.FromContent(
                ToolExecutionStatus.Failed,
                UniformContent("tool failed")
            )
        );

        var provider = new FakeProviderClient(
            new[] {
                CreateDeltaSequence(
                    CompletionChunk.FromContent("call broken"),
                    CompletionChunk.FromToolCall(CreateToolCallRequest("broken", "fail-1", ImmutableDictionary<string, string>.Empty))
                )
            }
        );

        var profile = new LlmProfile(Client: provider, ModelId: Model, Name: StrategyId);
        var agent = new CharacterAgent(state, new[] { failingTool });

        agent.AppendNotification("trigger failure");

        await agent.DoStepAsync(profile); // WaitingInput -> PendingInput
        await agent.DoStepAsync(profile); // PendingInput -> WaitingToolResults
        await agent.DoStepAsync(profile); // WaitingToolResults -> ToolResultsReady

        var step4 = await agent.DoStepAsync(profile);
        Assert.True(step4.ProgressMade);
        Assert.NotNull(step4.ToolResults);
        Assert.Equal(AgentRunState.ToolResultsReady, step4.StateBefore);
        Assert.Equal(AgentRunState.PendingToolResults, step4.StateAfter);

        var toolResults = step4.ToolResults!;
        Assert.Equal("tool failed", toolResults.ExecuteError);
        Assert.Equal(ToolExecutionStatus.Failed, toolResults.Results.Single().Status);
    }

    private static ParsedToolCall CreateToolCallRequest(
        string toolName,
        string callId,
        IReadOnlyDictionary<string, string>? rawArguments,
        IReadOnlyDictionary<string, object?>? arguments = null
    ) {
        var materializedArguments = arguments ?? ImmutableDictionary<string, object?>.Empty;
        return new(toolName, callId, rawArguments, materializedArguments, null, null);
    }

    private static async IAsyncEnumerable<CompletionChunk> CreateDeltaSequence(params CompletionChunk[] deltas) {
        foreach (var delta in deltas) {
            yield return delta;
            await Task.Yield();
        }
    }

    private static LevelOfDetailContent UniformContent(string text)
        => new(text, text);

    private sealed class FakeProviderClient : ICompletionClient {
        private readonly Queue<IAsyncEnumerable<CompletionChunk>> _responses;

        public string Name => "test-provider";
        public string ApiSpecId => "test-spec";

        public FakeProviderClient(IEnumerable<IAsyncEnumerable<CompletionChunk>> responses) {
            _responses = new Queue<IAsyncEnumerable<CompletionChunk>>(responses ?? throw new ArgumentNullException(nameof(responses)));
        }

        public IAsyncEnumerable<CompletionChunk> StreamCompletionAsync(CompletionRequest request, CancellationToken cancellationToken) {
            if (_responses.Count == 0) { throw new InvalidOperationException("No provider responses configured."); }

            return _responses.Dequeue();
        }
    }

    private sealed class DelegateTool : ITool {
        private readonly Func<IReadOnlyDictionary<string, object?>?, LodToolExecuteResult> _execute;

        public DelegateTool(string name, Func<IReadOnlyDictionary<string, object?>?, LodToolExecuteResult> execute) {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        }

        public string Name { get; }

        public string Description => "delegate-tool";

        public IReadOnlyList<ToolParamSpec> Parameters { get; } = Array.Empty<ToolParamSpec>();

        public ValueTask<LodToolExecuteResult> ExecuteAsync(IReadOnlyDictionary<string, object?>? arguments, CancellationToken cancellationToken)
            => new(_execute(arguments));
    }
}
