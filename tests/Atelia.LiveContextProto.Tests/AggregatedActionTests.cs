using System.Collections.Generic;
using System.Linq;
using Atelia.Completion.Abstractions;
using Xunit;

namespace Atelia.LiveContextProto.Tests;

public sealed class AggregatedActionTests {
    private static readonly CompletionDescriptor Descriptor = new("provider", "spec", "model");

    [Fact]
    public void Constructor_CopiesIncomingLists_ToPreserveSnapshotSemantics() {
        var sourceBlocks = new List<ActionBlock> {
            new ActionBlock.Text("alpha"),
            new ActionBlock.ToolCall(new ParsedToolCall("tool.a", "call-1", null, new Dictionary<string, object?>(), null, null))
        };
        var sourceErrors = new List<string> { "boom-1" };

        var action = new AggregatedAction(sourceBlocks, Descriptor, new TokenUsage(3, 2), sourceErrors);

        sourceBlocks.Add(new ActionBlock.Text("omega"));
        sourceErrors.Add("boom-2");

        Assert.Collection(
            action.Blocks,
            block => Assert.Equal("alpha", Assert.IsType<ActionBlock.Text>(block).Content),
            block => Assert.Equal("call-1", Assert.IsType<ActionBlock.ToolCall>(block).Call.ToolCallId)
        );
        Assert.Equal("alpha", action.Content);
        Assert.Collection(action.ToolCalls, call => Assert.Equal("call-1", call.ToolCallId));
        Assert.Equal(new[] { "boom-1" }, action.Errors);
    }

    [Fact]
    public void WithExpression_AlsoFreezesAssignedLists() {
        var baseAction = new AggregatedAction(
            blocks: new[] { new ActionBlock.Text("seed") },
            invocation: Descriptor
        );
        var replacementBlocks = new List<ActionBlock> { new ActionBlock.Text("beta") };
        var replacementErrors = new List<string> { "warn-1" };

        var cloned = baseAction with {
            Blocks = replacementBlocks,
            Errors = replacementErrors,
        };

        replacementBlocks.Add(new ActionBlock.Text("gamma"));
        replacementErrors.Add("warn-2");

        Assert.Collection(
            cloned.Blocks,
            block => Assert.Equal("beta", Assert.IsType<ActionBlock.Text>(block).Content)
        );
        Assert.Equal("beta", cloned.Content);
        Assert.Equal(new[] { "warn-1" }, cloned.Errors);
    }
}
