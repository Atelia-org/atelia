using Atelia.Completion.Abstractions;
using Atelia.SessionJournal.Cli;
using Xunit;

namespace Atelia.SessionJournal.Cli.Tests;

public sealed class MemoryMaintainerHistorySplitPolicyTests {
    [Fact]
    public void FindHalfContextSplitPoint_PreservesWholeObservationActionPairs() {
        IHistoryMessage[] messages = [
            new ObservationMessage("observation-1"),
            new ActionMessage([new ActionBlock.Text("action-1")]),
            new ObservationMessage("observation-2"),
            new ActionMessage([new ActionBlock.Text("action-2")])
        ];

        int splitIndex =
            MemoryMaintainerHistorySplitPolicy.FindHalfContextSplitPoint(
                messages,
                static _ => 1
            );

        Assert.Equal(2, splitIndex);
    }

    [Fact]
    public void FindHalfContextSplitPoint_AcceptsToolResultsToActionBoundary() {
        IHistoryMessage[] messages = [
            new ObservationMessage("observation"),
            new ActionMessage([new ActionBlock.Text("tool call")]),
            new ToolResultsMessage("tool result", []),
            new ActionMessage([new ActionBlock.Text("final answer")])
        ];

        int splitIndex =
            MemoryMaintainerHistorySplitPolicy.FindHalfContextSplitPoint(
                messages,
                static _ => 1
            );

        Assert.Equal(2, splitIndex);
    }
}
