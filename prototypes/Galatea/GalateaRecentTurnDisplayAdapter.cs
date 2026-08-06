using System.Text;
using Atelia.Completion.Abstractions;
using Atelia.SessionJournal;

namespace Atelia.Galatea.Server;

internal static class GalateaRecentTurnDisplayAdapter {
    internal static RecentTurnDto Project(
        SessionCompletedTurnProjection source
    ) {
        ArgumentNullException.ThrowIfNull(source);

        var text = new StringBuilder();
        var reasoning = new StringBuilder();
        foreach (ActionBlock block in
                 source.TerminalAction.Message.Blocks) {
            switch (block) {
                case ActionBlock.Text textBlock:
                    text.Append(textBlock.Content);
                    break;
                case ActionBlock.ReasoningBlock reasoningBlock when reasoningBlock.PlainText is not null:
                    reasoning.Append(reasoningBlock.PlainText);
                    break;
            }
        }

        string reasoningText = reasoning.ToString();
        return new RecentTurnDto(
            GalateaUserMessageEnvelope.UnwrapForDisplay(
                source.ObservationContent
            ),
            new AssistantMessageDto(
                InlineThinkTextFilter.StripInlineThinkBlocks(
                    text.ToString()
                ),
                reasoningText.Length == 0 ? null : reasoningText
            )
        );
    }

    internal static RecentTurnDto Project(
        SessionRetractedTurnProjection source
    ) {
        ArgumentNullException.ThrowIfNull(source);
        SessionTerminalActionProjection terminal =
            source.TerminalAction
            ?? throw new ArgumentException(
                "A visible completed-turn rewind requires a terminal Action.",
                nameof(source)
            );
        return Project(
            new SessionCompletedTurnProjection(
                source.ObservationAddress,
                source.ObservationContent,
                terminal
            )
        );
    }
}

internal static class GalateaUserMessageEnvelope {
    private const string Prefix = "玩家角色试图采取如下动作：\n```\n";
    private const string Suffix = "\n```\n";

    internal static string Wrap(string userMessage) {
        ArgumentException.ThrowIfNullOrWhiteSpace(userMessage);
        return Prefix + userMessage + Suffix;
    }

    internal static string UnwrapForDisplay(
        string? storedUserMessage
    ) {
        if (string.IsNullOrEmpty(storedUserMessage)) { return string.Empty; }
        if (!storedUserMessage.StartsWith(
            Prefix,
            StringComparison.Ordinal
        )
            || !storedUserMessage.EndsWith(
                Suffix,
                StringComparison.Ordinal
            )) { return storedUserMessage; }
        return storedUserMessage.Substring(
            Prefix.Length,
            storedUserMessage.Length - Prefix.Length - Suffix.Length
        );
    }
}
