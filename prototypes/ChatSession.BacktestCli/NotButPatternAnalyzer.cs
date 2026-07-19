using System.Text;
using System.Text.RegularExpressions;
using Atelia.ChatSession;
using Atelia.Completion.Abstractions;

namespace ChatSessionBacktestCli;

internal static partial class NotButPatternAnalyzer {
    public const string MaintainerId = "pattern-count.not-but";
    public const string BlockId = "galatea.pattern.not-but-count";

    public static PatternAnalysis Analyze(IReadOnlyList<IHistoryMessage> messages) {
        var matches = new List<string>();
        foreach (var text in messages.Select(BacktestTextUtil.FlattenMessageText)) {
            foreach (Match match in NotButPattern().Matches(text)) {
                matches.Add(BacktestTextUtil.NormalizeWhitespace(match.Value));
            }
        }

        return new PatternAnalysis(matches.Count, matches);
    }

    public static string RenderBlock(PatternAnalysis analysis) {
        var builder = new StringBuilder();
        builder.AppendLine($"totalCount: {analysis.Count}");
        if (analysis.Matches.Count > 0) {
            builder.AppendLine();
            builder.AppendLine("matches:");
            foreach (var match in analysis.Matches) { builder.AppendLine($"- {match}"); }
        }

        return builder.ToString().TrimEnd();
    }

    public static int ExtractCount(string? blockContent) {
        if (string.IsNullOrWhiteSpace(blockContent)) { return 0; }
        var match = CountPattern().Match(blockContent);
        return match.Success && int.TryParse(match.Groups[1].Value, out var count) ? count : 0;
    }

    public static PatternReplayRecord CreateReplayRecord(
        ChatSessionLegacyReplayStep step,
        int estimatedTokens,
        string? oldBlockText,
        string newBlockText,
        PatternAnalysis analysis,
        int oldCount
    )
        => new(
            EventOrdinal: step.Event.Ordinal,
            EventCommit: step.Event.Commit,
            HistoryMessageCount: step.MessageCount,
            EstimatedTokens: estimatedTokens,
            MaintainerId: MaintainerId,
            TargetCarrier: MemoryPackCarrier.Action.ToString(),
            TargetBlockId: BlockId,
            OldBlock: BacktestOutputUtil.CreateBlockPreview(oldBlockText),
            NewBlock: BacktestOutputUtil.CreateBlockPreview(newBlockText)!,
            Count: analysis.Count,
            DeltaCount: analysis.Count - oldCount,
            DeltaMatches: analysis.Count >= oldCount
                ? analysis.Matches.Skip(oldCount).ToArray()
                : Array.Empty<string>()
        );

    [GeneratedRegex("不是[\\s\\S]{0,80}?而是[\\s\\S]{0,80}?(?=$|[。！？!?；;\\n])")]
    private static partial Regex NotButPattern();

    [GeneratedRegex("(?:pattern count|totalCount): (\\d+)")]
    private static partial Regex CountPattern();
}

internal sealed record PatternAnalysis(int Count, IReadOnlyList<string> Matches);

internal sealed record PatternReplayRecord(
    int EventOrdinal,
    string? EventCommit,
    int HistoryMessageCount,
    int EstimatedTokens,
    string MaintainerId,
    string TargetCarrier,
    string TargetBlockId,
    MemoryBlockPreview? OldBlock,
    MemoryBlockPreview NewBlock,
    int Count,
    int DeltaCount,
    IReadOnlyList<string> DeltaMatches
);
