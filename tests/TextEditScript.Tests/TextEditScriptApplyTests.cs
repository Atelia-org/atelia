using Atelia.TextEditScript;
using Xunit;

namespace Atelia.Tests;

public class TextEditScriptApplyTests {
    [Fact]
    public void ApplyTo_ShouldInsertAfterTail_WhenSnapshotIsEmpty() {
        var script = new TextEditScriptDocument([
            new InsertTextEdit(TextInsertSide.AfterAnchor, TextAnchor.Tail, "第一条。"),
        ]);

        var result = script.ApplyTo(TextBlockSnapshotDocument.Empty);

        Assert.True(result.IsSuccess);
        var block = Assert.Single(result.Value.Blocks);
        Assert.Equal((uint)1, block.BlockId);
        Assert.Equal("第一条。", block.Content);
    }

    [Fact]
    public void ApplyTo_ShouldRejectInsertAfterHead_WhenSnapshotIsEmpty() {
        var script = new TextEditScriptDocument([
            new InsertTextEdit(TextInsertSide.AfterAnchor, TextAnchor.Head, "无效。"),
        ]);

        var result = script.ApplyTo(TextBlockSnapshotDocument.Empty);

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
        Assert.Contains("empty snapshot", result.Error.Message);
    }

    [Fact]
    public void ApplyTo_ShouldSupportHeadAndTailForReplaceAndDelete() {
        var before = new TextBlockSnapshotDocument([
            new TextBlockSnapshot(7, "旧首条"),
            new TextBlockSnapshot(9, "旧末条"),
        ]);
        var script = new TextEditScriptDocument([
            new ReplaceTextEdit(TextAnchor.Tail, "新末条"),
            new DeleteTextEdit(TextAnchor.Head),
        ]);

        var result = script.ApplyTo(before);

        Assert.True(result.IsSuccess);
        var block = Assert.Single(result.Value.Blocks);
        Assert.Equal((uint)9, block.BlockId);
        Assert.Equal("新末条", block.Content);
    }

    [Fact]
    public void ApplyTo_ShouldApplySequentiallyAgainstUpdatedSnapshot() {
        var before = new TextBlockSnapshotDocument([
            new TextBlockSnapshot(10, "A"),
            new TextBlockSnapshot(20, "B"),
        ]);
        var script = new TextEditScriptDocument([
            new InsertTextEdit(TextInsertSide.BeforeAnchor, TextAnchor.Head, "X"),
            new DeleteTextEdit(TextAnchor.Head),
            new ReplaceTextEdit(TextAnchor.Head, "A2"),
        ]);

        var result = script.ApplyTo(before);

        Assert.True(result.IsSuccess);
        Assert.Collection(
            result.Value.Blocks,
            block => {
                Assert.Equal((uint)10, block.BlockId);
                Assert.Equal("A2", block.Content);
            },
            block => {
                Assert.Equal((uint)20, block.BlockId);
                Assert.Equal("B", block.Content);
            });
    }

    [Fact]
    public void ApplyTo_ShouldAllocateInsertedBlockIdsSequentiallyFromConfiguredSeed() {
        var before = new TextBlockSnapshotDocument([
            new TextBlockSnapshot(2, "A"),
            new TextBlockSnapshot(4, "B"),
        ]);
        var script = new TextEditScriptDocument([
            new InsertTextEdit(TextInsertSide.AfterAnchor, TextAnchor.Tail, "C"),
            new InsertTextEdit(TextInsertSide.AfterAnchor, TextAnchor.Tail, "D"),
        ]);

        var result = script.ApplyTo(before, new TextEditScriptApplyOptions { FirstInsertedBlockId = 3 });

        Assert.True(result.IsSuccess);
        Assert.Collection(
            result.Value.Blocks,
            block => Assert.Equal((uint)2, block.BlockId),
            block => Assert.Equal((uint)4, block.BlockId),
            block => Assert.Equal((uint)3, block.BlockId),
            block => Assert.Equal((uint)5, block.BlockId));
    }

    [Fact]
    public void ApplyTo_ShouldRejectDuplicateBlockIdsInSnapshot() {
        var before = new TextBlockSnapshotDocument([
            new TextBlockSnapshot(7, "A"),
            new TextBlockSnapshot(7, "B"),
        ]);
        var script = new TextEditScriptDocument([
            new DeleteTextEdit(TextAnchor.Head),
        ]);

        var result = script.ApplyTo(before);

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
        Assert.Contains("duplicate block id", result.Error.Message);
    }
}
