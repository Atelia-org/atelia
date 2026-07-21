using Atelia.Completion.Abstractions;

namespace Atelia.ChatSession;

public static class HistoryWindowSplitPolicy {
    public static int FindHalfContextSplitPoint(
        IReadOnlyList<IHistoryMessage> messages,
        Func<IHistoryMessage, ulong> estimateTokens,
        bool allowActionToObservationBoundary = false
    ) {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(estimateTokens);
        if (messages.Count < 2) { return -1; }

        ulong totalTokens = 0;
        for (int i = 0; i < messages.Count; i++) { totalTokens += estimateTokens(messages[i]); }
        if (totalTokens == 0) { return -1; }

        ulong halfTokens = (totalTokens + 1) / 2;
        ulong cumulativeTokens = 0;
        int lastValidSuffixStart = -1;

        for (int i = 0; i < messages.Count - 1; i++) {
            cumulativeTokens += estimateTokens(messages[i]);

            if (IsObservationLike(messages[i]) && messages[i + 1].Kind == HistoryMessageKind.Action) {
                int suffixStart = i;
                if (suffixStart == 0) { continue; }
                if (suffixStart == 1 && messages[0] is RecapMessage) { continue; }

                lastValidSuffixStart = suffixStart;
                if (cumulativeTokens >= halfTokens) { return suffixStart; }
            }

            if (allowActionToObservationBoundary
                && MessageEndsWithAction(messages[i])
                && messages[i + 1].Kind == HistoryMessageKind.Observation) {
                int suffixStart = i + 1;
                lastValidSuffixStart = suffixStart;
                if (cumulativeTokens >= halfTokens) { return suffixStart; }
            }
        }

        return lastValidSuffixStart;
    }

    public static bool IsObservationToActionBoundary(
        IReadOnlyList<IHistoryMessage> messages,
        int splitIndex
    ) => splitIndex >= 1
         && splitIndex < messages.Count - 1
         && IsObservationLike(messages[splitIndex])
         && messages[splitIndex + 1].Kind == HistoryMessageKind.Action;

    private static bool MessageEndsWithAction(IHistoryMessage message)
        => message is ActionMessage || message is ContextHeader { ActionMessage: not null };

    private static bool IsObservationLike(IHistoryMessage message)
        => message.Kind is HistoryMessageKind.Observation or HistoryMessageKind.ToolResults;
}
