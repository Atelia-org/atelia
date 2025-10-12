using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Atelia.LiveContextProto.Agent;
using Atelia.LiveContextProto.Provider;
using Atelia.LiveContextProto.State;
using Atelia.LiveContextProto.State.History;
using Xunit;

namespace Atelia.LiveContextProto.Tests.Agent;

public sealed class AgentOrchestratorTests {
    [Fact]
    public async Task InvokeAsync_AppendsModelOutputAndToolResults() {
        var timestamps = new Queue<DateTimeOffset>(
            new[] {
                DateTimeOffset.Parse("2025-10-13T01:00:00Z"),
                DateTimeOffset.Parse("2025-10-13T01:05:00Z"),
                DateTimeOffset.Parse("2025-10-13T01:06:00Z"),
                DateTimeOffset.Parse("2025-10-13T01:07:00Z")
        }
        );

        var state = AgentState.CreateDefault(timestampProvider: () => timestamps.Dequeue());

        state.AppendModelInput(
            new ModelInputEntry(
                new[] {
                    new KeyValuePair<string, string>("default", "测试 provider stub")
                }
            )
        );

        var provider = new TestProviderClient();
        var router = new ProviderRouter(
            new[] {
                new ProviderRouteDefinition(
                    ProviderRouter.DefaultStubStrategy,
                    "stub-test",
                    "spec/test",
                    "stub-model",
                    provider,
                    default
                )
        }
        );

        var orchestrator = new AgentOrchestrator(state, router);

        var result = await orchestrator.InvokeAsync(
            new ProviderInvocationOptions(ProviderRouter.DefaultStubStrategy),
            CancellationToken.None
        );

        Assert.Single(provider.Requests);
        var request = provider.Requests[0];
        Assert.Equal("stub-test", request.Invocation.ProviderId);
        Assert.Equal("spec/test", request.Invocation.Specification);
        Assert.Equal("stub-model", request.Invocation.Model);
        Assert.Equal(ProviderRouter.DefaultStubStrategy, request.StrategyId);
        Assert.True(request.Context.OfType<IModelInputMessage>().Any());

        Assert.Single(result.Output.Contents);
        Assert.Equal("分析完成", result.Output.Contents[0]);
        Assert.NotNull(result.ToolResults);
        Assert.Single(result.ToolResults!.Results);
        Assert.Equal("call-42", result.ToolResults!.Results[0].ToolCallId);

        var usage = Assert.IsType<TokenUsage>(result.Output.Metadata["token_usage"]);
        Assert.Equal(50, usage.PromptTokens);
        Assert.Equal(10, usage.CompletionTokens);

        Assert.Equal(3, state.History.Count);
        Assert.IsType<ModelInputEntry>(state.History[0]);
        Assert.IsType<ModelOutputEntry>(state.History[1]);
        Assert.IsType<ToolResultsEntry>(state.History[2]);
    }

    private sealed class TestProviderClient : IProviderClient {
        public List<ProviderRequest> Requests { get; } = new();

        public IAsyncEnumerable<ModelOutputDelta> CallModelAsync(ProviderRequest request, CancellationToken cancellationToken) {
            Requests.Add(request);
            return ProduceAsync();
        }

        private static async IAsyncEnumerable<ModelOutputDelta> ProduceAsync() {
            await Task.Yield();
            yield return ModelOutputDelta.Content("分析", endSegment: false);
            yield return ModelOutputDelta.Content("完成", endSegment: true);
            yield return ModelOutputDelta.ToolCall(
                new ToolCallRequest(
                    "memory.search",
                    "call-42",
                    "{\"query\":\"phase\"}",
                    new Dictionary<string, string> { { "query", "phase" } },
                    null
                )
            );
            yield return ModelOutputDelta.ToolResult(
                new ToolCallResult(
                    "memory.search",
                    "call-42",
                    ToolExecutionStatus.Success,
                    "模拟工具响应",
                    TimeSpan.FromMilliseconds(90)
                )
            );
            yield return ModelOutputDelta.Usage(new TokenUsage(50, 10));
        }
    }
}
