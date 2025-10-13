using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Atelia.LiveContextProto.Provider;
using Atelia.LiveContextProto.State.History;
using Xunit;

namespace Atelia.LiveContextProto.Tests.Provider;

public sealed class ModelOutputAccumulatorTests {
    [Fact]
    public async Task AggregateAsync_ComposesContentToolCallsAndResults() {
        var deltas = new List<ModelOutputDelta> {
            ModelOutputDelta.Content("Hello "),
            ModelOutputDelta.Content("world", endSegment: true),
            ModelOutputDelta.ToolCall(
                new ToolCallRequest(
                    "memory.search",
                    "call-1",
                    "{\"query\":\"status\"}",
                    new Dictionary<string, object?> { { "query", "status" } },
                    null,
                    null
                )
            ),
            ModelOutputDelta.ToolResult(
                new ToolCallResult(
                    "memory.search",
                    "call-1",
                    ToolExecutionStatus.Success,
                    "找到 1 条结果",
                    TimeSpan.FromMilliseconds(150)
                )
            ),
            ModelOutputDelta.Usage(new TokenUsage(200, 42, 15))
        };

        var invocation = new ModelInvocationDescriptor("stub", "spec/demo", "stub-model");

        var aggregate = await ModelOutputAccumulator.AggregateAsync(
            AsAsyncEnumerable(deltas),
            invocation,
            CancellationToken.None
        );

        Assert.Single(aggregate.OutputEntry.Contents);
        Assert.Equal("Hello world", aggregate.OutputEntry.Contents[0]);

        Assert.Single(aggregate.OutputEntry.ToolCalls);
        Assert.Equal("memory.search", aggregate.OutputEntry.ToolCalls[0].ToolName);

        var usage = Assert.IsType<TokenUsage>(aggregate.OutputEntry.Metadata["token_usage"]);
        Assert.Equal(200, usage.PromptTokens);
        Assert.Equal(42, usage.CompletionTokens);
        Assert.Equal(15, usage.CachedPromptTokens);

        Assert.NotNull(aggregate.ToolResultsEntry);
        var toolResults = aggregate.ToolResultsEntry!;
        Assert.Single(toolResults.Results);
        Assert.Equal("call-1", toolResults.Results[0].ToolCallId);

        var toolUsage = Assert.IsType<TokenUsage>(toolResults.Metadata["token_usage"]);
        Assert.Equal(42, toolUsage.CompletionTokens);
    }

    private static async IAsyncEnumerable<ModelOutputDelta> AsAsyncEnumerable(IEnumerable<ModelOutputDelta> deltas) {
        foreach (var delta in deltas) {
            yield return delta;
            await Task.Yield();
        }
    }
}
