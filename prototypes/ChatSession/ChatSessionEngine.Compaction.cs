using Atelia.Completion.Abstractions;
using Atelia.Diagnostics;
using Atelia.StateJournal;

namespace Atelia.ChatSession;

public sealed partial class ChatSessionEngine {
    private const int MaxMemoryMaintainerToolLoopIterations = 16;

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
        for (int i = 0; i < maintainers.Length; i++) {
            ValidateMemoryMaintainer(maintainers[i]);
        }
        EnsureUniqueMemoryMaintainers(maintainers);

        var messages = MessageRecord.ToHistoryMessages(_messages);
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
                MaintainerResults: Array.Empty<MemoryMaintainerResult>(),
                HistoryCountBefore: messages.Count,
                TokensBefore: ChatSessionTokenEstimator.Estimate(messages)
            );
        }

        var fragment = CreateHistorySlice(messages, splitIndex, messages.Count - splitIndex);
        var tasks = new Task<MemoryMaintainerResult>[maintainers.Length];
        for (int i = 0; i < maintainers.Length; i++) {
            tasks[i] = RunMemoryMaintainerAsync(maintainers[i], fragment, ct);
        }

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        DebugUtil.Info(
            "ChatSession.MemoryMaintenance",
            $"RunMemoryMaintainersAsync completed: head={PersistedHeadAddress}, splitIndex={splitIndex}, results={results.Length}"
        );

        return new MemoryMaintenanceResult(
            Completed: true,
            FailureReason: null,
            SplitIndex: splitIndex,
            MaintainerResults: results,
            HistoryCountBefore: messages.Count,
            TokensBefore: ChatSessionTokenEstimator.Estimate(messages)
        );
    }

    internal static int FindHalfContextSplitPoint(
        IReadOnlyList<IHistoryMessage> messages,
        bool allowActionToObservationBoundary = false
    ) {
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

            if (allowActionToObservationBoundary
                && MessageEndsWithAction(messages[i])
                && messages[i + 1].Kind == HistoryMessageKind.Observation) {
                int suffixStart = i + 1;
                if (suffixStart == 0) { continue; }

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

    private static bool MessageEndsWithAction(IHistoryMessage message) {
        return message switch {
            ActionMessage => true,
            ContextHeader header => header.AssistantMessage is not null,
            _ => false
        };
    }

    private static bool IsObservationLike(IHistoryMessage message) {
        return message.Kind switch {
            HistoryMessageKind.Observation => true,
            HistoryMessageKind.ToolResults => true,
            _ => false
        };
    }

    private static IReadOnlyList<IHistoryMessage> CreateHistorySlice(
        IReadOnlyList<IHistoryMessage> messages,
        int startIndex,
        int count
    ) {
        var result = new IHistoryMessage[count];
        for (int i = 0; i < count; i++) { result[i] = messages[startIndex + i]; }
        return result;
    }

    private static void ValidateMemoryMaintainer(IMemoryMaintainerAgent maintainer) {
        ArgumentNullException.ThrowIfNull(maintainer);
        if (string.IsNullOrWhiteSpace(maintainer.Id)) { throw new ArgumentException("Memory maintainer id cannot be empty.", nameof(maintainer)); }
        if (string.IsNullOrWhiteSpace(maintainer.TargetBlockKey)) { throw new ArgumentException("Memory maintainer target block key cannot be empty.", nameof(maintainer)); }
        ArgumentNullException.ThrowIfNull(maintainer.SystemPrompt);
        ArgumentNullException.ThrowIfNull(maintainer.UserPrompt);
        ArgumentNullException.ThrowIfNull(maintainer.ToolSession);
    }

    private static void EnsureUniqueMemoryMaintainers(IReadOnlyList<IMemoryMaintainerAgent> maintainers) {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var targetBlockKeys = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < maintainers.Count; i++) {
            if (!ids.Add(maintainers[i].Id)) { throw new ArgumentException($"Duplicate memory maintainer id: {maintainers[i].Id}", nameof(maintainers)); }

            if (!targetBlockKeys.Add(maintainers[i].TargetBlockKey)) { throw new ArgumentException($"Duplicate memory maintainer target block key: {maintainers[i].TargetBlockKey}", nameof(maintainers)); }
        }
    }

    private async Task<MemoryMaintainerResult> RunMemoryMaintainerAsync(
        IMemoryMaintainerAgent maintainer,
        IReadOnlyList<IHistoryMessage> recentHistoryFragment,
        CancellationToken ct
    ) {
        var session = maintainer.ToolSession;
        var workingContext = new List<IHistoryMessage>(recentHistoryFragment.Count + 8);
        workingContext.AddRange(recentHistoryFragment);
        workingContext.Add(new ObservationMessage(maintainer.UserPrompt));

        ActionMessage? finalMessage = null;
        CompletionDescriptor? invocation = null;
        List<string>? errors = null;
        int totalToolCallsExecuted = 0;

        for (int iteration = 0; iteration < MaxMemoryMaintainerToolLoopIterations; iteration++) {
            ct.ThrowIfCancellationRequested();

            var completionRequest = new CompletionRequest(
                ModelId: _runtime.ModelId,
                SystemPrompt: maintainer.SystemPrompt,
                Context: workingContext,
                Tools: session.VisibleDefinitions
            );

            var result = await _runtime.CompletionClient.StreamCompletionAsync(completionRequest, null, ct)
                .ConfigureAwait(false);

            invocation = result.Invocation;
            if (result.Errors is { Count: > 0 }) {
                errors ??= new List<string>();
                errors.AddRange(result.Errors);
            }

            if (!result.Termination.IsSuccess) {
                throw new ChatSessionTurnAbortedException(
                    BuildTurnAbortMessage(result.Termination),
                    result.Termination,
                    result.Errors
                );
            }

            finalMessage = StripReasoningBlocks(result.Message);
            workingContext.Add(finalMessage);

            var toolCalls = finalMessage.ToolCalls;
            if (toolCalls.Count == 0) { break; }

            var toolResults = new ToolResult[toolCalls.Count];
            int executed = 0;
            for (int i = 0; i < toolCalls.Count; i++) {
                ct.ThrowIfCancellationRequested();
                var callResult = await session.ExecuteAsync(toolCalls[i], ct).ConfigureAwait(false);
                toolResults[i] = callResult.ToToolResult();
                executed++;
            }

            totalToolCallsExecuted += executed;
            workingContext.Add(new ToolResultsMessage(content: null, results: toolResults));
        }

        if (finalMessage is not null && finalMessage.ToolCalls.Count > 0) {
            throw new InvalidOperationException(
                $"Memory maintainer '{maintainer.Id}' tool loop exceeded the hard limit of {MaxMemoryMaintainerToolLoopIterations} iterations."
            );
        }

        var updatedText = InlineThinkTextFilter.StripInlineThinkBlocks(finalMessage?.GetFlattenedText() ?? string.Empty).Trim();
        return new MemoryMaintainerResult(
            MaintainerId: maintainer.Id,
            TargetBlockKey: maintainer.TargetBlockKey,
            UpdatedText: updatedText,
            Invocation: invocation ?? new CompletionDescriptor("none", "none", _runtime.ModelId),
            Errors: errors?.AsReadOnly(),
            ToolCallsExecuted: totalToolCallsExecuted
        );
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
                    if (!string.IsNullOrWhiteSpace(header.UserMessage)) { messages.Add(new ObservationMessage(header.UserMessage)); }
                    if (header.AssistantMessage is not null) { messages.Add(StripReasoningBlocks(header.AssistantMessage)); }
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

        MessageRecord.PrependRecap(_messages, summary);
        for (int i = protectedHeaders.Length - 1; i >= 0; i--) {
            MessageRecord.PrependContextHeader(_messages, protectedHeaders[i]);
        }
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
