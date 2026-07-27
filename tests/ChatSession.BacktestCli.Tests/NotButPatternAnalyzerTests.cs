using Atelia.ChatSession;
using Xunit;

namespace ChatSessionBacktestCli.Tests;

public sealed class NotButPatternAnalyzerTests {
    [Fact]
    public void RenderedBlock_CanServeAsNextEpochState() {
        var firstBlock = NotButPatternAnalyzer.RenderBlock(
            new PatternAnalysis(1, ["不是甲，而是乙。"])
        );
        var oldCount = NotButPatternAnalyzer.ExtractCount(firstBlock);
        var nextAnalysis = new PatternAnalysis(
            3,
            ["不是甲，而是乙。", "不是丙，而是丁。", "不是戊，而是己。"]
        );
        var nextBlock = NotButPatternAnalyzer.RenderBlock(nextAnalysis);
        var step = CreateStep();

        var record = NotButPatternAnalyzer.CreateReplayRecord(
            step,
            estimatedTokens: 24,
            oldBlockText: firstBlock,
            newBlockText: nextBlock,
            nextAnalysis,
            oldCount
        );

        Assert.Equal(1, oldCount);
        Assert.Equal(3, record.Count);
        Assert.Equal(2, record.DeltaCount);
        Assert.Equal(["不是丙，而是丁。", "不是戊，而是己。"], record.DeltaMatches);
    }

    [Fact]
    public void CreateReplayRecord_PreservesLegacyTargetIdentityWithoutMemoryPack() {
        var step = CreateStep();
        var analysis = new PatternAnalysis(1, ["不是甲，而是乙。"]);

        var record = NotButPatternAnalyzer.CreateReplayRecord(
            step,
            estimatedTokens: 12,
            oldBlockText: null,
            newBlockText: "totalCount: 1",
            analysis,
            oldCount: 0
        );

        Assert.Equal("pattern-count.not-but", record.MaintainerId);
        Assert.Equal("Action", record.TargetCarrier);
        Assert.Equal("galatea.pattern.not-but-count", record.TargetBlockId);
        Assert.Equal(1, record.DeltaCount);
    }

    private static ChatSessionLegacyReplayStep CreateStep()
        => new(
            Event: new ChatSessionLegacyReplayEvent {
                Ordinal = 7,
                Commit = "commit-7",
                Kind = ChatSessionLegacyEventKinds.ModelTurn,
            },
            Applied: true,
            MessageCount: 1
        );
}
