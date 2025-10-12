using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Atelia.LiveContextProto.Provider;
using Atelia.LiveContextProto.State.History;
using Xunit;

namespace Atelia.LiveContextProto.Tests.Provider;

public sealed class ModelOutputAccumulatorErrorTests {
    [Fact]
    public async Task AggregateAsync_ExecuteErrorOnly_ProducesToolResultsEntry() {
        var deltas = new List<ModelOutputDelta> {
            ModelOutputDelta.ExecutionError("工具总线异常")
        };

        var invocation = new ModelInvocationDescriptor("stub", "spec/error", "stub-model");

        var aggregate = await ModelOutputAccumulator.AggregateAsync(
            AsAsyncEnumerable(deltas),
            invocation,
            CancellationToken.None
        );

        Assert.NotNull(aggregate.ToolResultsEntry);
        Assert.Equal("工具总线异常", aggregate.ToolResultsEntry!.ExecuteError);
        Assert.Empty(aggregate.ToolResultsEntry!.Results);

        // Output entry still exists with an empty content segment.
        Assert.Single(aggregate.OutputEntry.Contents);
        Assert.Equal(string.Empty, aggregate.OutputEntry.Contents[0]);
    }

    private static async IAsyncEnumerable<ModelOutputDelta> AsAsyncEnumerable(IEnumerable<ModelOutputDelta> deltas) {
        foreach (var delta in deltas) {
            yield return delta;
            await Task.Yield();
        }
    }
}
