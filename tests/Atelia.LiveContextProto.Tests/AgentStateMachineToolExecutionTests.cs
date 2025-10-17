using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Atelia.LiveContextProto.Agent;
using Atelia.LiveContextProto.Context;
using Atelia.LiveContextProto.Provider;
using Atelia.LiveContextProto.Profile;
using Atelia.LiveContextProto.State;
using Atelia.LiveContextProto.State.History;
using Atelia.LiveContextProto.Tools;
using Xunit;

namespace Atelia.LiveContextProto.Tests;

public sealed class AgentStateMachineToolExecutionTests {
    private const string StrategyId = "test-strategy";
    private const string ProviderId = "test-provider";
    private const string Specification = "spec";
    private const string Model = "model";

    [Fact]
    public async Task DoStepAsync_CompletesToolCallLifecycle() {
        var state = AgentState.CreateDefault(timestampProvider: () => DateTimeOffset.UnixEpoch);

        var toolInvocations = new List<ToolExecutionContext>();
        var echoTool = new DelegateTool(
            "echo",
            context => {
                toolInvocations.Add(context);
                var metadata = ImmutableDictionary<string, object?>.Empty.SetItem("source", "delegate");
                return new ToolHandlerResult(
                    ToolExecutionStatus.Success,
                    UniformContent("tool-output"),
                    metadata
                );
            }
        );

        var catalog = CreateCatalog(state, echoTool);
        var executor = new ToolExecutor(catalog.CreateHandlers());

        var firstResponse = CreateDeltaSequence(
            ModelOutputDelta.Content("calling tool", endSegment: true),
            ModelOutputDelta.ToolCall(
                CreateToolCallRequest(
                    "echo",
                    "call-1",
                    "{\"payload\":\"value\"}",
                    new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["payload"] = "value" }
                )
            )
        );

        var secondResponse = CreateDeltaSequence(
            ModelOutputDelta.Content("tool complete", endSegment: true)
        );

        var provider = new FakeProviderClient(new[] { firstResponse, secondResponse });
        var router = CreateRouter(provider);
        var options = StrategyId;
        var agent = new LlmAgent(state, router, executor, catalog, options);

        agent.EnqueueUserInput("hello world", options);

        var step1 = await agent.DoStepAsync();
        Assert.True(step1.ProgressMade);
        Assert.NotNull(step1.Input);
        Assert.Equal(AgentRunState.WaitingInput, step1.StateBefore);
        Assert.Equal(AgentRunState.PendingInput, step1.StateAfter);

        var step2 = await agent.DoStepAsync();
        Assert.True(step2.ProgressMade);
        Assert.NotNull(step2.Output);
        Assert.Equal(AgentRunState.PendingInput, step2.StateBefore);
        Assert.Equal(AgentRunState.WaitingToolResults, step2.StateAfter);
        Assert.Single(step2.Output!.ToolCalls);

        var step3 = await agent.DoStepAsync();
        Assert.True(step3.ProgressMade);
        Assert.Null(step3.Output);
        Assert.Equal(AgentRunState.WaitingToolResults, step3.StateBefore);
        Assert.Equal(AgentRunState.ToolResultsReady, step3.StateAfter);

        var step4 = await agent.DoStepAsync();
        Assert.True(step4.ProgressMade);
        Assert.NotNull(step4.ToolResults);
        Assert.Equal(AgentRunState.ToolResultsReady, step4.StateBefore);
        Assert.Equal(AgentRunState.PendingToolResults, step4.StateAfter);

        var toolResults = step4.ToolResults!;
        Assert.Single(toolResults.Results);
        Assert.Null(toolResults.ExecuteError);

        var historyResult = toolResults.Results[0];
        Assert.Equal(ToolExecutionStatus.Success, historyResult.Status);
        var plainText = LevelOfDetailSections.ToPlainText(historyResult.Result.Live);
        Assert.Contains("tool-output", plainText, StringComparison.Ordinal);

        Assert.Single(toolInvocations);
        Assert.Equal("call-1", toolInvocations[0].Request.ToolCallId);
        Assert.Equal("echo", toolInvocations[0].Request.ToolName);

        var metadata = toolResults.Metadata;
        Assert.True(metadata.TryGetValue("tool_call_count", out var callCountValue));
        Assert.Equal(1, Assert.IsType<int>(callCountValue));
        Assert.True(metadata.TryGetValue("tool_failed_count", out var failedCountValue));
        Assert.Equal(0, Assert.IsType<int>(failedCountValue));
        Assert.True(metadata.TryGetValue("per_call_metadata", out var perCallMetadata));
        var perCall = Assert.IsType<ImmutableDictionary<string, object?>>(perCallMetadata);
        Assert.True(perCall.ContainsKey("call-1"));

        var step5 = await agent.DoStepAsync();
        Assert.True(step5.ProgressMade);
        Assert.NotNull(step5.Output);
        Assert.Equal(AgentRunState.PendingToolResults, step5.StateBefore);
        Assert.Equal(AgentRunState.WaitingInput, step5.StateAfter);

        Assert.Equal(4, state.History.Count);
        Assert.IsType<ModelInputEntry>(state.History[0]);
        Assert.IsType<ModelOutputEntry>(state.History[1]);
        Assert.IsType<ToolResultsEntry>(state.History[2]);
        Assert.IsType<ModelOutputEntry>(state.History[3]);
    }

    [Fact]
    public async Task DoStepAsync_ToolFailureProducesExecuteError() {
        var state = AgentState.CreateDefault(timestampProvider: () => DateTimeOffset.UnixEpoch);

        var failingTool = new DelegateTool(
            "broken",
            _ => new ToolHandlerResult(
                ToolExecutionStatus.Failed,
                UniformContent("tool failed"),
                ImmutableDictionary<string, object?>.Empty.SetItem("error", "simulated_failure")
            )
        );

        var catalog = CreateCatalog(state, failingTool);
        var executor = new ToolExecutor(catalog.CreateHandlers());

        var provider = new FakeProviderClient(
            new[] {
                CreateDeltaSequence(
                    ModelOutputDelta.Content("call broken", endSegment: true),
                    ModelOutputDelta.ToolCall(CreateToolCallRequest("broken", "fail-1", "{}"))
                )
        }
        );

        var router = CreateRouter(provider);
        var options = StrategyId;
        var agent = new LlmAgent(state, router, executor, catalog, options);

        agent.EnqueueUserInput("trigger failure", options);

        await agent.DoStepAsync(); // WaitingInput -> PendingInput
        await agent.DoStepAsync(); // PendingInput -> WaitingToolResults
        await agent.DoStepAsync(); // WaitingToolResults -> ToolResultsReady

        var step4 = await agent.DoStepAsync();
        Assert.True(step4.ProgressMade);
        Assert.NotNull(step4.ToolResults);
        Assert.Equal(AgentRunState.ToolResultsReady, step4.StateBefore);
        Assert.Equal(AgentRunState.PendingToolResults, step4.StateAfter);

        var toolResults = step4.ToolResults!;
        Assert.Equal("tool failed", toolResults.ExecuteError);
        Assert.Equal(ToolExecutionStatus.Failed, toolResults.Results.Single().Status);

        var metadata = toolResults.Metadata;
        Assert.True(metadata.TryGetValue("tool_failed_count", out var failedCountValue));
        Assert.Equal(1, Assert.IsType<int>(failedCountValue));
    }

    private static ProviderRouter CreateRouter(IProviderClient provider) {
        var profile = new LlmProfile(Client: provider, ModelId: Model, Name: StrategyId);
        return new ProviderRouter(new[] { profile });
    }

    private static ToolCatalog CreateCatalog(AgentState state, params ITool[] extraTools) {
        var tools = state.EnumerateWidgetTools();
        return ToolCatalog.Create(tools.Concat(extraTools));
    }

    private static ToolCallRequest CreateToolCallRequest(
        string toolName,
        string callId,
        string rawArguments,
        IReadOnlyDictionary<string, object?>? arguments = null
    ) => new(toolName, callId, rawArguments, arguments, null, null);

    private static async IAsyncEnumerable<ModelOutputDelta> CreateDeltaSequence(params ModelOutputDelta[] deltas) {
        foreach (var delta in deltas) {
            yield return delta;
            await Task.Yield();
        }
    }

    private static LevelOfDetailContent UniformContent(string text)
        => new(text, text, text);

    private sealed class FakeProviderClient : IProviderClient {
        private readonly Queue<IAsyncEnumerable<ModelOutputDelta>> _responses;

        public string Name => "test-provider";
        public string Specification => "test-spec";

        public FakeProviderClient(IEnumerable<IAsyncEnumerable<ModelOutputDelta>> responses) {
            _responses = new Queue<IAsyncEnumerable<ModelOutputDelta>>(responses ?? throw new ArgumentNullException(nameof(responses)));
        }

        public IAsyncEnumerable<ModelOutputDelta> CallModelAsync(LlmRequest request, CancellationToken cancellationToken) {
            if (_responses.Count == 0) { throw new InvalidOperationException("No provider responses configured."); }

            return _responses.Dequeue();
        }
    }

    private sealed class DelegateTool : ITool {
        private readonly Func<ToolExecutionContext, ToolHandlerResult> _execute;

        public DelegateTool(string name, Func<ToolExecutionContext, ToolHandlerResult> execute) {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        }

        public string Name { get; }

        public string Description => "delegate-tool";

        public IReadOnlyList<ToolParameter> Parameters { get; } = Array.Empty<ToolParameter>();

        public ValueTask<ToolHandlerResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken)
            => new(_execute(context));
    }
}
