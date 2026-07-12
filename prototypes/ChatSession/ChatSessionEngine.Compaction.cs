using Atelia.Completion.Abstractions;
using Atelia.Diagnostics;
using Atelia.StateJournal;

namespace Atelia.ChatSession;

public sealed partial class ChatSessionEngine {
    public async Task<CompactionResult> CompactAsync(
        string summarizeSystemPrompt,
        string summarizePrompt,
        CancellationToken ct = default
    ) {
        ThrowIfDisposed();

        var messages = MessageRecord.ToHistoryMessages(_messages);
        int splitIndex = FindHalfContextSplitPoint(messages);
        DebugUtil.Info(
            "ChatSession.Compaction",
            $"CompactAsync start: head={PersistedHeadAddress}, messages={messages.Count}, splitIndex={splitIndex}, firstKinds={DescribeLeadingKinds(messages)}"
        );
        if (splitIndex < 0) {
            return new CompactionResult(
                Applied: false,
                FailureReason: CompactionFailureReason.NoValidSplitPoint,
                SplitIndex: splitIndex,
                SummaryLength: 0,
                HistoryCountBefore: messages.Count,
                HistoryCountAfter: messages.Count,
                TokensBefore: ChatSessionTokenEstimator.Estimate(messages),
                TokensAfter: ChatSessionTokenEstimator.Estimate(messages)
            );
        }

        if (!IsValidSplitPoint(messages, splitIndex)) {
            return new CompactionResult(
                Applied: false,
                FailureReason: CompactionFailureReason.InvalidSplitPoint,
                SplitIndex: splitIndex,
                SummaryLength: 0,
                HistoryCountBefore: messages.Count,
                HistoryCountAfter: messages.Count,
                TokensBefore: ChatSessionTokenEstimator.Estimate(messages),
                TokensAfter: ChatSessionTokenEstimator.Estimate(messages)
            );
        }

        return await ExecuteCompactionCoreAsync(summarizeSystemPrompt, summarizePrompt, splitIndex, messages, ct)
            .ConfigureAwait(false);
    }

    internal static int FindHalfContextSplitPoint(IReadOnlyList<IHistoryMessage> messages) {
        if (messages.Count < 2) { return -1; }

        ulong totalTokens = ChatSessionTokenEstimator.Estimate(messages);
        if (totalTokens == 0) { return -1; }

        ulong halfTokens = (totalTokens + 1) / 2;
        ulong cumulativeTokens = 0;
        int lastValidSuffixStart = -1;

        for (int i = 0; i < messages.Count - 1; i++) {
            cumulativeTokens += ChatSessionTokenEstimator.Estimate(messages[i]);

            if (IsObservationLike(messages[i]) && messages[i + 1].Kind == HistoryMessageKind.Action) {
                int suffixStart = i;
                if (suffixStart == 0) { continue; }
                if (suffixStart == 1 && messages[0] is RecapMessage) { continue; }

                lastValidSuffixStart = suffixStart;

                if (cumulativeTokens >= halfTokens) { return suffixStart; }
            }
        }

        return lastValidSuffixStart;
    }

    private static bool IsValidSplitPoint(IReadOnlyList<IHistoryMessage> messages, int splitIndex) {
        return splitIndex >= 1
               && splitIndex < messages.Count - 1
               && IsObservationLike(messages[splitIndex])
               && messages[splitIndex + 1].Kind == HistoryMessageKind.Action;
    }

    private static bool IsObservationLike(IHistoryMessage message) {
        return message.Kind switch {
            HistoryMessageKind.Observation => true,
            HistoryMessageKind.ToolResults => true,
            _ => false
        };
    }

    private static List<IHistoryMessage> ProjectForSummarization(
        IReadOnlyList<IHistoryMessage> prefix,
        string summarizePrompt
    ) {
        var messages = new List<IHistoryMessage>(prefix.Count + 1);

        for (int i = 0; i < prefix.Count; i++) {
            var original = prefix[i];
            switch (original.Kind) {
                case HistoryMessageKind.Action:
                    var action = (ActionMessage)original;
                    messages.Add(StripReasoningBlocks(action));
                    break;
                case HistoryMessageKind.Observation:
                case HistoryMessageKind.ToolResults:
                    messages.Add(original);
                    break;
            }
        }

        messages.Add(new ObservationMessage(summarizePrompt));
        return messages;
    }

    private static ActionMessage StripReasoningBlocks(ActionMessage action) {
        var filtered = new List<ActionBlock>(action.Blocks.Count);
        for (int i = 0; i < action.Blocks.Count; i++) {
            switch (action.Blocks[i]) {
                case ActionBlock.Text text:
                    var visibleText = InlineThinkTextFilter.StripInlineThinkBlocks(text.Content);
                    if (!string.IsNullOrEmpty(visibleText)) {
                        filtered.Add(new ActionBlock.Text(visibleText));
                    }
                    break;
                case ActionBlock.ToolCall:
                    filtered.Add(action.Blocks[i]);
                    break;
            }
        }
        return new ActionMessage(filtered);
    }

    private async Task<CompactionResult> ExecuteCompactionCoreAsync(
        string summarizeSystemPrompt,
        string summarizePrompt,
        int splitIndex,
        IReadOnlyList<IHistoryMessage> currentMessages,
        CancellationToken ct
    ) {
        var tokensBefore = ChatSessionTokenEstimator.Estimate(currentMessages);
        var historyCountBefore = currentMessages.Count;

        var prefix = new List<IHistoryMessage>(splitIndex);
        for (int i = 0; i < splitIndex; i++) { prefix.Add(currentMessages[i]); }

        var summarizeMessages = ProjectForSummarization(prefix, summarizePrompt);

        var completionRequest = new CompletionRequest(
            ModelId: _runtime.ModelId,
            SystemPrompt: summarizeSystemPrompt,
            Context: summarizeMessages,
            Tools: System.Collections.Immutable.ImmutableArray<ToolDefinition>.Empty
        );

        var result = await _runtime.CompletionClient.StreamCompletionAsync(completionRequest, null, ct)
            .ConfigureAwait(false);

        var summary = InlineThinkTextFilter.StripInlineThinkBlocks(result.Message.GetFlattenedText()).Trim();
        if (string.IsNullOrEmpty(summary)) {
            return new CompactionResult(
                Applied: false,
                FailureReason: CompactionFailureReason.EmptySummary,
                SplitIndex: splitIndex,
                SummaryLength: 0,
                HistoryCountBefore: historyCountBefore,
                HistoryCountAfter: historyCountBefore,
                TokensBefore: tokensBefore,
                TokensAfter: tokensBefore
            );
        }

        for (int i = 0; i < splitIndex; i++) {
            _messages.PopFront<DurableObject>(out _);
        }

        MessageRecord.PrependRecap(_messages, summary);
        Commit();

        var remaining = MessageRecord.ToHistoryMessages(_messages);
        var tokensAfter = ChatSessionTokenEstimator.Estimate(remaining);
        DebugUtil.Info(
            "ChatSession.Compaction",
            $"CompactAsync applied: head={PersistedHeadAddress}, splitIndex={splitIndex}, before={historyCountBefore}, after={remaining.Count}, leadingKinds={DescribeLeadingKinds(remaining)}"
        );

        return new CompactionResult(
            Applied: true,
            FailureReason: null,
            SplitIndex: splitIndex,
            SummaryLength: summary.Length,
            HistoryCountBefore: historyCountBefore,
            HistoryCountAfter: remaining.Count,
            TokensBefore: tokensBefore,
            TokensAfter: tokensAfter
        );
    }

    private static string DescribeLeadingKinds(IReadOnlyList<IHistoryMessage> messages) {
        if (messages.Count == 0) { return "<empty>"; }
        return string.Join(",", messages.Take(4).Select(x => x.Kind.ToString()));
    }
}
