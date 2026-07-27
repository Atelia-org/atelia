using System.Text.RegularExpressions;
using Atelia.Completion.Abstractions;
using Atelia.SessionJournal;

namespace Atelia.SessionJournal.Cli;

internal static partial class MemoryMaintainerTextUtil {
    public static string FlattenMessageText(IHistoryMessage message)
        => message switch {
            SessionContextHeader contextHeader => string.Join('\n',
                new[] {
                    contextHeader.SystemPromptFragment,
                    contextHeader.ObservationMessage,
                    contextHeader.ActionMessage?.GetFlattenedText()
                }.Where(static text => !string.IsNullOrEmpty(text))
            ),
            ToolResultsMessage toolResults => toolResults.Content ?? string.Empty,
            ObservationMessage observation => observation.Content ?? string.Empty,
            ActionMessage action => action.GetFlattenedText(),
            _ => message.ToString() ?? string.Empty
        };

    public static int EstimateTokens(IReadOnlyList<IHistoryMessage> messages)
        => Math.Max(1, messages.Sum(EstimateTokens));

    public static int EstimateTokens(IHistoryMessage message)
        => Math.Max(1, FlattenMessageText(message).Length / 3);

    public static string NormalizeWhitespace(string text)
        => WhitespacePattern().Replace(text, " ").Trim();

    [GeneratedRegex("\\s+")]
    private static partial Regex WhitespacePattern();
}

internal static class MemoryMaintainerOutputUtil {
    public static MemoryBlockTextPreview? CreateBlockPreview(string? text, int tailPreviewChars = 600) {
        if (text is null) { return null; }
        var tailPreview = text.Length <= tailPreviewChars
            ? text
            : text[^tailPreviewChars..];
        return new MemoryBlockTextPreview(text.Length, tailPreview);
    }
}

internal sealed record MemoryBlockTextPreview(int Length, string TailPreview);
