using System.Text;
using Atelia.Completion.Abstractions;
using Atelia.SessionJournal;

namespace Atelia.Galatea.Server;

internal static class GalateaRecentTurnDisplayAdapter {
    internal static RecentTurnDto Project(
        SessionCompletedTurnProjection source
    ) {
        ArgumentNullException.ThrowIfNull(source);

        var reasoning = new StringBuilder();
        foreach (ActionBlock block in
                 source.TerminalAction.Message.Blocks) {
            switch (block) {
                case ActionBlock.ReasoningBlock reasoningBlock when reasoningBlock.PlainText is not null:
                    reasoning.Append(reasoningBlock.PlainText);
                    break;
            }
        }

        string reasoningText = reasoning.ToString();
        return new RecentTurnDto(
            GalateaObservationDisplay.Project(source.ObservationContent),
            new AssistantMessageDto(
                GalateaVisibleActionTextRenderer.Render(
                    source.TerminalAction.Message
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

    internal static bool TryUnwrapForDisplay(
        string? stored,
        out string display
    ) {
        if (stored is not null
            && stored.StartsWith(Prefix, StringComparison.Ordinal)
            && stored.EndsWith(Suffix, StringComparison.Ordinal)) {
            display = stored.Substring(
                Prefix.Length,
                stored.Length - Prefix.Length - Suffix.Length
            );
            return true;
        }
        display = stored ?? string.Empty;
        return false;
    }
}

internal static class GalateaObservationDisplay {
    internal static string Project(string? stored) {
        if (GalateaMailboxObservationEnvelope.TryUnwrap(
                stored,
                out MailboxMessage mail)) {
            return GalateaMailboxObservationEnvelope.FormatForDisplay(mail);
        }
        return PlayerTurnObservationClassifier.TryProject(
            stored,
            out _,
            out string display
        ) ? display : stored ?? string.Empty;
    }
}
