using System.Text.RegularExpressions;
using Atelia.ChatSession;
using Atelia.Completion.Abstractions;

namespace ChatSessionBacktestCli;

internal static partial class BacktestTextUtil {
    public static string FlattenMessageText(IHistoryMessage message)
        => message switch {
            ContextHeader contextHeader => string.Join('\n',
                new[] {
                    contextHeader.SystemPromptFragment,
                    contextHeader.ObservationMessage,
                    contextHeader.ActionMessage?.GetFlattenedText()
                }.Where(static text => !string.IsNullOrEmpty(text))
            ),
            RecapMessage recap => recap.Content ?? string.Empty,
            ToolResultsMessage toolResults => toolResults.Content ?? string.Empty,
            ObservationMessage observation => observation.Content ?? string.Empty,
            ActionMessage action => action.GetFlattenedText(),
            _ => message.ToString() ?? string.Empty
        };

    public static int EstimateTokens(IReadOnlyList<IHistoryMessage> messages)
        => Math.Max(1, messages.Sum(message => FlattenMessageText(message).Length) / 3);

    public static string NormalizeWhitespace(string text)
        => WhitespacePattern().Replace(text, " ").Trim();

    [GeneratedRegex("\\s+")]
    private static partial Regex WhitespacePattern();
}

internal static class BacktestOutputUtil {
    public static MemoryBlockPreview? CreateBlockPreview(string? text, int tailPreviewChars = 600) {
        if (text is null) { return null; }
        var tailPreview = text.Length <= tailPreviewChars
            ? text
            : text[^tailPreviewChars..];
        return new MemoryBlockPreview(text.Length, tailPreview);
    }
}

internal sealed record MemoryBlockPreview(int Length, string TailPreview);
