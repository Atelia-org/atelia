using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Atelia.LiveContextProto.Agent;
using Atelia.LiveContextProto.Provider;
using Atelia.LiveContextProto.State;
using Atelia.LiveContextProto.State.History;
using Atelia.LiveContextProto.Tools;
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

        var toolExecutor = new ToolExecutor(Array.Empty<IToolHandler>());
        var orchestrator = new AgentOrchestrator(state, router, toolExecutor);

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

    [Fact]
    public async Task InvokeAsync_PassesLiveScreenDecoratedMessagesToProvider() {
        var timestamps = new Queue<DateTimeOffset>(
            new[] {
                DateTimeOffset.Parse("2025-10-13T02:00:00Z"),
                DateTimeOffset.Parse("2025-10-13T02:05:00Z"),
                DateTimeOffset.Parse("2025-10-13T02:06:00Z"),
                DateTimeOffset.Parse("2025-10-13T02:07:00Z")
            }
        );

        var state = AgentState.CreateDefault(timestampProvider: () => timestamps.Dequeue());
        state.UpdateMemoryNotebook("- Notebook snapshot");
        state.UpdateLiveInfoSection("Planner Summary", "- Phase 3 cross-provider");

        state.AppendModelInput(
            new ModelInputEntry(
                new[] {
                    new KeyValuePair<string, string>("default", "检查 LiveScreen")
                }
            )
        );

        var provider = new LiveScreenAwareProvider();
        var router = new ProviderRouter(
            new[] {
                new ProviderRouteDefinition(
                    ProviderRouter.DefaultStubStrategy,
                    "stub-test",
                    "spec/liveinfo",
                    "stub-model",
                    provider,
                    default
                )
            }
        );

        var toolExecutor = new ToolExecutor(Array.Empty<IToolHandler>());
        var orchestrator = new AgentOrchestrator(state, router, toolExecutor);

        await orchestrator.InvokeAsync(
            new ProviderInvocationOptions(ProviderRouter.DefaultStubStrategy),
            CancellationToken.None
        );

        Assert.True(provider.SawLiveScreen);
        Assert.True(provider.InnerMessageWasModelInput);
        Assert.NotNull(provider.LastRequest);

        var liveScreen = provider.ObservedLiveScreen;
        Assert.False(string.IsNullOrWhiteSpace(liveScreen));
        Assert.Contains("Planner Summary", liveScreen!, StringComparison.Ordinal);
        Assert.Contains("Memory Notebook", liveScreen!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeAsync_ExecutesToolCallsWhenProviderOmitsResults() {
        var timestamps = new Queue<DateTimeOffset>(
            new[] {
                DateTimeOffset.Parse("2025-10-13T03:00:00Z"),
                DateTimeOffset.Parse("2025-10-13T03:01:00Z"),
                DateTimeOffset.Parse("2025-10-13T03:02:00Z"),
                DateTimeOffset.Parse("2025-10-13T03:03:00Z")
            }
        );

        var state = AgentState.CreateDefault(timestampProvider: () => timestamps.Dequeue());
        state.AppendModelInput(
            new ModelInputEntry(
                new[] {
                    new KeyValuePair<string, string>("default", "触发工具执行")
                }
            )
        );

        var provider = new ToolDeclarationOnlyProvider();
        var handler = new RecordingToolHandler();
        var toolExecutor = new ToolExecutor(new IToolHandler[] { handler });
        var router = new ProviderRouter(
            new[] {
                new ProviderRouteDefinition(
                    ProviderRouter.DefaultStubStrategy,
                    "stub-tools",
                    "spec/tools",
                    "stub-model",
                    provider,
                    default
                )
            }
        );

        var orchestrator = new AgentOrchestrator(state, router, toolExecutor);

        var result = await orchestrator.InvokeAsync(
            new ProviderInvocationOptions(ProviderRouter.DefaultStubStrategy),
            CancellationToken.None
        );

        Assert.Equal(1, handler.InvocationCount);

        Assert.NotNull(result.ToolResults);
        var toolResults = result.ToolResults!;
        Assert.Single(toolResults.Results);
        var executed = toolResults.Results[0];
        Assert.Equal("memory.search", executed.ToolName);
        Assert.Equal(ToolExecutionStatus.Success, executed.Status);
        Assert.Contains("RecordingToolHandler", executed.Result, StringComparison.Ordinal);

        Assert.True(toolResults.Metadata.ContainsKey("tool_call_count"));
        Assert.Equal(1, toolResults.Metadata["tool_call_count"]);
        Assert.True(toolResults.Metadata.ContainsKey("per_call_metadata"));

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

    private sealed class LiveScreenAwareProvider : IProviderClient {
        public bool SawLiveScreen { get; private set; }
        public bool InnerMessageWasModelInput { get; private set; }
        public string? ObservedLiveScreen { get; private set; }
        public ProviderRequest? LastRequest { get; private set; }

        public IAsyncEnumerable<ModelOutputDelta> CallModelAsync(ProviderRequest request, CancellationToken cancellationToken) {
            LastRequest = request;

            foreach (var message in request.Context) {
                if (message is ILiveScreenCarrier carrier) {
                    SawLiveScreen = !string.IsNullOrWhiteSpace(carrier.LiveScreen);
                    ObservedLiveScreen = carrier.LiveScreen;
                    if (carrier.InnerMessage is IModelInputMessage) {
                        InnerMessageWasModelInput = true;
                    }
                }
            }

            return ProduceAsync();
        }

        private static async IAsyncEnumerable<ModelOutputDelta> ProduceAsync() {
            await Task.Yield();
            yield return ModelOutputDelta.Content("ack", endSegment: true);
            yield return ModelOutputDelta.Usage(new TokenUsage(10, 5));
        }
    }

    private sealed class ToolDeclarationOnlyProvider : IProviderClient {
        public IAsyncEnumerable<ModelOutputDelta> CallModelAsync(ProviderRequest request, CancellationToken cancellationToken)
            => ProduceAsync();

        private static async IAsyncEnumerable<ModelOutputDelta> ProduceAsync() {
            await Task.Yield();
            yield return ModelOutputDelta.Content("Tool invocation planned", endSegment: true);
            yield return ModelOutputDelta.ToolCall(
                new ToolCallRequest(
                    "memory.search",
                    "tool-exec-1",
                    "{\"query\":\"phase 4 diagnostics\"}",
                    new Dictionary<string, string> { { "query", "phase 4 diagnostics" } },
                    null
                )
            );
        }
    }

    private sealed class RecordingToolHandler : IToolHandler {
        public int InvocationCount { get; private set; }
        public string ToolName => "memory.search";

        public ValueTask<ToolHandlerResult> ExecuteAsync(ToolCallRequest request, CancellationToken cancellationToken) {
            InvocationCount++;
            var metadata = ImmutableDictionary<string, object?>.Empty
                .SetItem("invocation", InvocationCount);
            var message = $"RecordingToolHandler 执行 {request.ToolCallId}";
            return ValueTask.FromResult(new ToolHandlerResult(ToolExecutionStatus.Success, message, metadata));
        }
    }
}
