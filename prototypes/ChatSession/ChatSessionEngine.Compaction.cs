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

    public async Task<MemoryMaintenanceResult> RunMemoryMaintainersAsync(
        MemoryMaintenanceRequest request,
        CancellationToken ct = default
    ) {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Maintainers);

        var maintainers = request.Maintainers.ToArray();
        if (maintainers.Length == 0) { throw new ArgumentException("At least one memory maintainer is required.", nameof(request)); }
        MemoryMaintenanceOrchestrator.ValidateMaintainers(maintainers);

        var messages = MessageRecord.ToHistoryMessages(_messages);
        var tokensBefore = ChatSessionTokenEstimator.Estimate(messages);
        int splitIndex = FindHalfContextSplitPoint(messages, request.AllowActionToObservationBoundary);
        DebugUtil.Info(
            "ChatSession.MemoryMaintenance",
            $"RunMemoryMaintainersAsync start: head={PersistedHeadAddress}, messages={messages.Count}, splitIndex={splitIndex}, maintainers={maintainers.Length}, firstKinds={DescribeLeadingKinds(messages)}"
        );
        if (splitIndex < 0) {
            return new MemoryMaintenanceResult(
                Completed: false,
                FailureReason: CompactionFailureReason.NoValidSplitPoint,
                SplitIndex: splitIndex,
                MaintainerResults: Array.Empty<MemoryBlockMaintenanceResult>(),
                HistoryCountBefore: messages.Count,
                TokensBefore: tokensBefore,
                UpdatedMemoryPack: null
            );
        }

        var fragment = CreateHistorySlice(messages, 0, splitIndex);
        var recentHistory = new RecentHistorySlice(
            PriorContext: ContextHeaderSnapshot.FromRenderedMemoryPack(request.MemoryPack.Render()),
            Messages: fragment,
            SourceId: PersistedHeadAddress?.ToString(),
            EstimatedTokens: ChatSessionTokenEstimator.Estimate(fragment)
        );

        var batch = await MemoryMaintenanceOrchestrator.RunAsync(
            request.MemoryPack,
            recentHistory,
            maintainers,
            ct
        ).ConfigureAwait(false);
        DebugUtil.Info(
            "ChatSession.MemoryMaintenance",
            $"RunMemoryMaintainersAsync completed: head={PersistedHeadAddress}, splitIndex={splitIndex}, results={batch.Results.Count}"
        );

        return new MemoryMaintenanceResult(
            Completed: true,
            FailureReason: null,
            SplitIndex: splitIndex,
            MaintainerResults: batch.Results,
            HistoryCountBefore: messages.Count,
            TokensBefore: tokensBefore,
            UpdatedMemoryPack: batch.UpdatedMemoryPack
        );
    }

    internal static int FindHalfContextSplitPoint(
        IReadOnlyList<IHistoryMessage> messages,
        bool allowActionToObservationBoundary = false
    ) => HistoryWindowSplitPolicy.FindHalfContextSplitPoint(
        messages,
        ChatSessionTokenEstimator.Estimate,
        allowActionToObservationBoundary
    );

    private static bool IsValidSplitPoint(IReadOnlyList<IHistoryMessage> messages, int splitIndex)
        => HistoryWindowSplitPolicy.IsObservationToActionBoundary(messages, splitIndex);

    private static IReadOnlyList<IHistoryMessage> CreateHistorySlice(
        IReadOnlyList<IHistoryMessage> messages,
        int startIndex,
        int count
    ) {
        var result = new IHistoryMessage[count];
        for (int i = 0; i < count; i++) { result[i] = messages[startIndex + i]; }
        return result;
    }

    private static List<IHistoryMessage> ProjectForSummarization(
        IReadOnlyList<IHistoryMessage> prefix,
        string summarizePrompt
    ) {
        var messages = new List<IHistoryMessage>(prefix.Count + 1);

        for (int i = 0; i < prefix.Count; i++) {
            var original = prefix[i];
            switch (original.Kind) {
                case HistoryMessageKind.ContextHeader:
                    var header = (ContextHeader)original;
                    if (!string.IsNullOrWhiteSpace(header.SystemPromptFragment)) { messages.Add(new ObservationMessage(header.SystemPromptFragment)); }
                    if (!string.IsNullOrWhiteSpace(header.ObservationMessage)) { messages.Add(new ObservationMessage(header.ObservationMessage)); }
                    if (header.ActionMessage is not null) { messages.Add(StripReasoningBlocks(header.ActionMessage)); }
                    break;
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
        var sourceHeadBeforeCompaction = PersistedHeadAddress;

        var prefix = new List<IHistoryMessage>(splitIndex);
        for (int i = 0; i < splitIndex; i++) { prefix.Add(currentMessages[i]); }
        var protectedHeaders = prefix.OfType<ContextHeader>().ToArray();

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

        var sourceAnchor = sourceHeadBeforeCompaction is { } head
            ? new RecapSourceAnchor(
                SourceHeadBeforeCompaction: head.ToString(),
                SourceBranchName: _branchName,
                SourceStartIndex: 0,
                SourceEndExclusive: splitIndex,
                SourceMessageCountBefore: historyCountBefore,
                CompactionKind: MessageRecord.CompactionKindPrefixSummary
            )
            : null;

        MessageRecord.PrependRecap(_messages, summary, sourceAnchor);
        for (int i = protectedHeaders.Length - 1; i >= 0; i--) {
            MessageRecord.PrependContextHeader(_messages, protectedHeaders[i]);
        }
        Commit(ChatSessionCommitKind.Compaction, "applied prefix summary compaction");

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
