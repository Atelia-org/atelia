using Atelia.Completion.Abstractions;

namespace Atelia.SessionJournal.Cli;

internal static class MemoryMaintainerHistorySplitPolicy {
    public static int FindHalfContextSplitPoint(
        IReadOnlyList<IHistoryMessage> messages,
        Func<IHistoryMessage, ulong> estimateTokens
    ) {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(estimateTokens);
        if (messages.Count < 2) { return -1; }

        ulong totalTokens = 0;
        for (int i = 0; i < messages.Count; i++) {
            totalTokens += estimateTokens(messages[i]);
        }
        if (totalTokens == 0) { return -1; }

        ulong halfTokens = (totalTokens + 1) / 2;
        ulong cumulativeTokens = 0;
        int lastValidSuffixStart = -1;

        for (int i = 0; i < messages.Count - 1; i++) {
            cumulativeTokens += estimateTokens(messages[i]);
            if (IsObservationLike(messages[i])
                && messages[i + 1].Kind == HistoryMessageKind.Action) {
                int suffixStart = i;
                if (suffixStart == 0) { continue; }

                lastValidSuffixStart = suffixStart;
                if (cumulativeTokens >= halfTokens) { return suffixStart; }
            }
        }

        return lastValidSuffixStart;
    }

    private static bool IsObservationLike(IHistoryMessage message)
        => message.Kind
            is HistoryMessageKind.Observation
            or HistoryMessageKind.ToolResults;
}
