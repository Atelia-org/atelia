using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Atelia.LiveContextProto.State.History;
using Atelia.LiveContextProto.Tools;
using Xunit;

namespace Atelia.LiveContextProto.Tests.Tools;

public sealed class ToolExecutorTests {
    [Fact]
    public async Task ExecuteBatchAsync_AppendsElapsedMetadata() {
        var handler = new SucceedingHandler();
        var executor = new ToolExecutor(new IToolHandler[] { handler });

        var request = new ToolCallRequest(
            "memory.search",
            "test-call-1",
            "{\"query\":\"metadata\"}",
            new Dictionary<string, string> { { "query", "metadata" } },
            null
        );

        var records = await executor.ExecuteBatchAsync(new[] { request }, CancellationToken.None);
        var record = Assert.Single(records);

        Assert.Equal(ToolExecutionStatus.Success, record.CallResult.Status);
        Assert.True(record.Metadata.ContainsKey("elapsed_ms"));
        Assert.True(record.Metadata.ContainsKey("query"));

        var elapsed = Assert.IsType<double>(record.Metadata["elapsed_ms"]);
        Assert.True(elapsed >= 0);

        Assert.Equal(1, handler.InvocationCount);
    }

    private sealed class SucceedingHandler : IToolHandler {
        public int InvocationCount { get; private set; }
        public string ToolName => "memory.search";

        public ValueTask<ToolHandlerResult> ExecuteAsync(ToolCallRequest request, CancellationToken cancellationToken) {
            InvocationCount++;
            var query = "(missing)";
            if (request.Arguments is { } args && args.TryGetValue("query", out var parsed)) {
                query = parsed;
            }

            var metadata = ImmutableDictionary<string, object?>.Empty
                .SetItem("query", query);

            return ValueTask.FromResult(
                new ToolHandlerResult(
                    ToolExecutionStatus.Success,
                    $"Handled {request.ToolCallId}",
                    metadata
                )
            );
        }
    }
}
