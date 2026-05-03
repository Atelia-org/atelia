using Atelia.Agent.Core.History;
using Atelia.Completion.Abstractions;
using Atelia.Diagnostics;

namespace Atelia.Agent.Core;

public partial class AgentEngine {
    /// <summary>
    /// 捆绑一次上下文压缩请求所需的全部参数：切分点与 LLM 调用所需的两个 prompt。
    /// </summary>
    /// <remarks>
    /// prompt 文本不由引擎内置，而是由 <see cref="RequestCompaction"/> 的调用者注入，
    /// 便于在不同实验台项目中进行提示词工程。
    /// </remarks>
    /// <param name="SplitIndex">suffix 起始索引（由 <see cref="ContextSplitter.FindHalfContextSplitPoint"/> 返回）。</param>
    /// <param name="SystemPrompt">摘要 LLM 的系统提示词。</param>
    /// <param name="SummarizePrompt">追加在待摘要历史末尾的请求消息。</param>
    private readonly record struct CompactionRequest(int SplitIndex, string SystemPrompt, string SummarizePrompt);

    internal enum CompactionFailureReason {
        NoValidSplitPoint,
        InvalidSplitPoint,
        EmptySummary
    }

    internal readonly record struct CompactionExecutionResult(
        bool Applied,
        CompactionFailureReason? FailureReason,
        int SplitIndex,
        int SummaryLength,
        int HistoryCountBefore,
        int HistoryCountAfter,
        ulong TokensBefore,
        ulong TokensAfter,
        ulong SoftContextTokenCap
    ) {
        public ulong TokensReleased => TokensBefore > TokensAfter ? TokensBefore - TokensAfter : 0;

        public double BeforeCapacityRatio => SoftContextTokenCap == 0 ? 0.0 : (double)TokensBefore / SoftContextTokenCap;

        public double AfterCapacityRatio => SoftContextTokenCap == 0 ? 0.0 : (double)TokensAfter / SoftContextTokenCap;

        public double ReleasedCapacityRatio => SoftContextTokenCap == 0 ? 0.0 : (double)TokensReleased / SoftContextTokenCap;

        public static CompactionExecutionResult Failed(
            CompactionFailureReason failureReason,
            int splitIndex,
            int historyCountBefore,
            ulong tokensBefore,
            ulong softContextTokenCap
        ) {
            return new CompactionExecutionResult(
                Applied: false,
                FailureReason: failureReason,
                SplitIndex: splitIndex,
                SummaryLength: 0,
                HistoryCountBefore: historyCountBefore,
                HistoryCountAfter: historyCountBefore,
                TokensBefore: tokensBefore,
                TokensAfter: tokensBefore,
                SoftContextTokenCap: softContextTokenCap
            );
        }

        public static CompactionExecutionResult Succeeded(
            int splitIndex,
            int summaryLength,
            int historyCountBefore,
            int historyCountAfter,
            ulong tokensBefore,
            ulong tokensAfter,
            ulong softContextTokenCap
        ) {
            return new CompactionExecutionResult(
                Applied: true,
                FailureReason: null,
                SplitIndex: splitIndex,
                SummaryLength: summaryLength,
                HistoryCountBefore: historyCountBefore,
                HistoryCountAfter: historyCountAfter,
                TokensBefore: tokensBefore,
                TokensAfter: tokensAfter,
                SoftContextTokenCap: softContextTokenCap
            );
        }
    }

    /// <summary>
    /// 待执行的上下文压缩请求。
    /// <c>null</c> 表示无待处理的压缩请求；非 <c>null</c> 时，<see cref="DetermineState"/> 返回 <see cref="AgentRunState.Compacting"/>。
    /// </summary>
    /// <remarks>
    /// 在 <see cref="RequestCompaction"/> 中计算并写入。
    /// 清除时机：
    /// <list type="bullet">
    /// <item><see cref="ProcessCompactingAsync"/> 成功调用 <see cref="AgentState.ReplacePrefixWithRecap"/> 后清除（正常完成）；</item>
    /// <item>stale 校验失败（splitIndex 越界或不再满足 Observation→Action 边界不变式）时清除；</item>
    /// <item>LLM 摘要返回空字符串时清除。</item>
    /// </list>
    /// LLM 调用抛出异常或 cancellation 时<em>不</em>清除，允许下一次 <see cref="StepAsync"/> 自动重试。
    /// 这替代了原先的 <c>bool _compactionPending</c>，使得状态标志同时携带切分点与 prompt 信息，避免 flag 与各参数分离导致的一致性问题。
    /// </remarks>
    private CompactionRequest? _compactionRequest;

    /// <summary>
    /// 请求下一次 <see cref="StepAsync"/> 调用时执行上下文压缩。
    /// 主要用于测试目的；自动触发策略留待后续设计。
    /// </summary>
    /// <param name="systemPrompt">摘要 LLM 的系统提示词（由调用方注入，便于提示词工程）。</param>
    /// <param name="summarizePrompt">追加在待摘要历史末尾的摘要请求消息（由调用方注入）。</param>
    /// <returns>
    /// <c>true</c> 表示找到合法切分点并已记录到 <see cref="_compactionRequest"/>；
    /// <c>false</c> 表示当前历史没有可用的切分点（如条目数不足 2），不会进入 <see cref="AgentRunState.Compacting"/> 状态。
    /// </returns>
    /// <remarks>
    /// 若已有待处理的压缩请求（<see cref="_compactionRequest"/> 非 <c>null</c>），
    /// 本次调用会刷新切分点与 prompt（重新采样当前历史快照）。重复调用是幂等的：只要历史量足够，
    /// 始终返回 <c>true</c>。
    /// <para>
    /// Compaction 是一个高优先级内部状态：一旦 <see cref="_compactionRequest"/> 被设置，
    /// <see cref="DetermineState"/> 会优先返回 <see cref="AgentRunState.Compacting"/>，
    /// 插队到 <c>PendingInput</c> / <c>PendingToolResults</c> 等状态之前。
    /// 由于 suffix 部分原样保留，此插队不会破坏 <see cref="_pendingToolResults"/> 与末尾 <see cref="ActionEntry"/> 的对应关系。
    /// </para>
    /// </remarks>
    public bool RequestCompaction(string systemPrompt, string summarizePrompt) {
        if (systemPrompt is null) { throw new ArgumentNullException(nameof(systemPrompt)); }
        if (summarizePrompt is null) { throw new ArgumentNullException(nameof(summarizePrompt)); }

        var snapshot = _state.RecentHistory;
        int splitIndex = ContextSplitter.FindHalfContextSplitPoint(snapshot);
        if (splitIndex < 0) {
            DebugUtil.Trace(StateMachineDebugCategory, "[Compacting] RequestCompaction: no valid split point; skipping.");
            return false;
        }

        _compactionRequest = new CompactionRequest(splitIndex, systemPrompt, summarizePrompt);
        DebugUtil.Trace(StateMachineDebugCategory, $"[Compacting] RequestCompaction: splitIndex={splitIndex} historyCount={snapshot.Count}");
        return true;
    }

    /// <summary>
    /// 获取是否有待处理的上下文压缩请求。
    /// </summary>
    /// <remarks>
    /// 工具 App（如 <see cref="App.EnginePanelApp"/>）可通过此属性判断是否已有压缩请求排队，
    /// 避免重复发起。
    /// </remarks>
    public bool HasPendingCompaction => _compactionRequest.HasValue;

    internal CompactionPreview? BuildCompactionPreview() {
        var snapshot = _state.RecentHistory;
        int splitIndex = ContextSplitter.FindHalfContextSplitPoint(snapshot);
        if (splitIndex < 1 || splitIndex >= snapshot.Count) { return null; }

        ulong totalHistoryTokens = 0;
        ulong prefixTokens = 0;
        for (int i = 0; i < snapshot.Count; i++) {
            ulong entryTokens = snapshot[i].TokenEstimate;
            totalHistoryTokens += entryTokens;
            if (i < splitIndex) {
                prefixTokens += entryTokens;
            }
        }

        return new CompactionPreview(
            SplitIndex: splitIndex,
            PrefixEntryCount: splitIndex,
            PrefixTokenEstimate: prefixTokens,
            TotalHistoryTokenEstimate: totalHistoryTokens,
            PrefixEndPreview: BuildHistoryEntryPreview(snapshot[splitIndex - 1]),
            SuffixStartPreview: BuildHistoryEntryPreview(snapshot[splitIndex])
        );
    }

    internal async Task<CompactionExecutionResult> ExecuteCompactionImmediateAsync(
        string systemPrompt,
        string summarizePrompt,
        CancellationToken cancellationToken
    ) {
        if (systemPrompt is null) { throw new ArgumentNullException(nameof(systemPrompt)); }
        if (summarizePrompt is null) { throw new ArgumentNullException(nameof(summarizePrompt)); }

        if (_turnRuntime.ActiveToolExecutionProfile is null) {
            throw new InvalidOperationException("Immediate compaction requires an active tool-execution LlmProfile.");
        }

        var profile = _turnRuntime.ActiveToolExecutionProfile;
        EnsureProfileMatchesCurrentTurnLock(profile);

        var snapshot = _state.RecentHistory;
        var softContextTokenCap = (ulong)profile.SoftContextTokenCap;
        var tokensBefore = EstimateCurrentContextTokens();
        var lockedSplitIndex = _turnRuntime.LockedCompactionSplitIndex;
        int splitIndex = lockedSplitIndex ?? ContextSplitter.FindHalfContextSplitPoint(snapshot);
        if (splitIndex < 0) {
            DebugUtil.Trace(StateMachineDebugCategory, "[Compacting] Immediate compaction skipped: no valid split point.");
            return CompactionExecutionResult.Failed(
                failureReason: CompactionFailureReason.NoValidSplitPoint,
                splitIndex: splitIndex,
                historyCountBefore: snapshot.Count,
                tokensBefore: tokensBefore,
                softContextTokenCap: softContextTokenCap
            );
        }

        if (lockedSplitIndex.HasValue) {
            DebugUtil.Info(
                StateMachineDebugCategory,
                $"[Compacting] Immediate compaction requested from tool path. Using action-locked splitIndex={splitIndex}."
            );
        }
        else {
            DebugUtil.Info(StateMachineDebugCategory, $"[Compacting] Immediate compaction requested from tool path. splitIndex={splitIndex}");
        }

        return await ExecuteCompactionCoreAsync(
            profile,
            new CompactionRequest(splitIndex, systemPrompt, summarizePrompt),
            cancellationToken
        ).ConfigureAwait(false);
    }

    /// <summary>
    /// 估算当前上下文的 token 信息量，用于与 <see cref="LlmProfile.SoftContextTokenCap"/> 比较以决定是否触发自动压缩。
    /// </summary>
    /// <remarks>
    /// 此值为近似估算，不包含 App Windows 注入、tool definitions、Detail/Basic 级差等投影变形。
    /// 作为软上限触发条件已足够准确——其目的是在明显超过上限时提前介入，而非精确到个位数。
    /// 估算基准与 <see cref="ContextSplitter.FindHalfContextSplitPoint"/> 一致（均基于 <see cref="HistoryEntry.TokenEstimate"/>），
    /// 保证压缩决策与切分算法共享同一 token 坐标系。
    /// </remarks>
    public ulong EstimateCurrentContextTokens() {
        ulong total = TokenEstimateHelper.GetDefault().EstimateString(SystemPrompt);
        foreach (var entry in _state.RecentHistory) {
            total += entry.TokenEstimate;
        }

        return total;
    }

    /// <summary>
    /// 尝试发起一次自动上下文压缩请求，仅在已配置 <see cref="AutoCompactionOptions"/> 且无待处理的手动请求时生效。
    /// </summary>
    /// <returns>
    /// <c>true</c> 表示成功写入 <see cref="_compactionRequest"/>；
    /// <c>false</c> 表示跳过（未配置或已存在手动请求或历史量不足以进行有效压缩）。
    /// </returns>
    /// <remarks>
    /// <list type="bullet">
    /// <item>手动 <see cref="RequestCompaction"/> 优先：若 <see cref="_compactionRequest"/> 已存在，不会覆盖。</item>
    /// <item><b>防抖守卫</b>：要求历史至少 4 条（约 2 个完整 Turn）才允许自动触发，
    /// 避免压缩后剩余条目仍超 cap 导致立即再次触发（因为压缩后的 Recap→Action 边界仍可通过切分校验，
    /// 但再次压缩仅会"摘要掉刚刚产生的 Recap"，毫无收益）。</item>
    /// </list>
    /// </remarks>
    private bool TryRequestAutoCompaction() {
        if (_autoCompactionOptions is null) {
            DebugUtil.Trace(StateMachineDebugCategory, "[Compacting] Auto compaction skipped: no AutoCompactionOptions configured.");
            return false;
        }

        if (_compactionRequest.HasValue) {
            DebugUtil.Trace(StateMachineDebugCategory, "[Compacting] Auto compaction skipped: compaction request already pending.");
            return false;
        }

        if (_state.RecentHistory.Count < 4) {
            DebugUtil.Trace(
                StateMachineDebugCategory,
                $"[Compacting] Auto compaction skipped: history too short (count={_state.RecentHistory.Count}, need >= 4)."
            );
            return false;
        }

        return RequestCompaction(_autoCompactionOptions.SystemPrompt, _autoCompactionOptions.SummarizePrompt);
    }

    /// <summary>
    /// 将历史条目投影为 <see cref="IHistoryMessage"/> 列表，末尾追加摘要请求消息。
    /// </summary>
    /// <remarks>
    /// 这是原 <c>ContextSummarizer.ProjectToMessages</c> 的迁移版本。
    /// 使用 Detail 级别、不注入 windows、不处理 Turn 切分——
    /// 摘要场景下 LLM 只需看到内容文本，无需完整投影管线。
    /// </remarks>
    private static List<IHistoryMessage> ProjectForSummarization(
        IReadOnlyList<HistoryEntry> entries,
        string summarizePrompt
    ) {
        var messages = new List<IHistoryMessage>(entries.Count + 1);

        foreach (var entry in entries) {
            switch (entry) {
                case ActionEntry action:
                    messages.Add(action.Message);
                    break;
                case ObservationEntry observation:
                    messages.Add(observation.GetMessage(LevelOfDetail.Detail, windows: null));
                    break;
                case RecapEntry recap:
                    messages.Add(new ObservationMessage(recap.Content));
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unsupported HistoryEntry type: {entry.GetType().Name} (Kind={entry.Kind})"
                    );
            }
        }

        messages.Add(new ObservationMessage(summarizePrompt));
        return messages;
    }

    private static string BuildHistoryEntryPreview(HistoryEntry entry) {
        string label = entry.Kind switch {
            HistoryEntryKind.Observation => "Observation",
            HistoryEntryKind.Action => "Action",
            HistoryEntryKind.ToolResults => "ToolResults",
            HistoryEntryKind.Recap => "Recap",
            _ => entry.Kind.ToString()
        };

        string raw = entry switch {
            ActionEntry action => BuildActionPreview(action),
            ToolResultsEntry toolResults => BuildToolResultsPreview(toolResults),
            ObservationEntry observation => observation.GetMessage(LevelOfDetail.Basic, windows: null).Content ?? string.Empty,
            RecapEntry recap => recap.Content,
            _ => string.Empty
        };

        string preview = TrimForPreview(raw, 96);
        return string.IsNullOrWhiteSpace(preview)
            ? label
            : $"{label}: {preview}";
    }

    private static string BuildActionPreview(ActionEntry action) {
        var flattened = action.Message.GetFlattenedText();
        if (!string.IsNullOrWhiteSpace(flattened)) { return flattened; }

        var toolCalls = action.Message.ToolCalls;
        if (toolCalls.Count == 0) { return string.Empty; }

        var names = new List<string>(capacity: Math.Min(toolCalls.Count, 3));
        for (int i = 0; i < toolCalls.Count && names.Count < 3; i++) {
            var name = toolCalls[i].ToolName;
            if (string.IsNullOrWhiteSpace(name) || ContainsIgnoreCase(names, name)) { continue; }
            names.Add(name);
        }

        return names.Count == 0
            ? "工具调用"
            : "工具调用: " + string.Join(", ", names);
    }

    private static string BuildToolResultsPreview(ToolResultsEntry toolResults) {
        if (!string.IsNullOrWhiteSpace(toolResults.ExecuteError)) {
            return "工具执行失败: " + toolResults.ExecuteError;
        }

        if (toolResults.Results.Count > 0) {
            var names = new List<string>(capacity: Math.Min(toolResults.Results.Count, 3));
            for (int i = 0; i < toolResults.Results.Count && names.Count < 3; i++) {
                var name = toolResults.Results[i].ToolName;
                if (string.IsNullOrWhiteSpace(name) || ContainsIgnoreCase(names, name)) { continue; }
                names.Add(name);
            }

            if (names.Count > 0) {
                return "工具结果: " + string.Join(", ", names);
            }
        }

        return toolResults.GetMessage(LevelOfDetail.Basic, windows: null).Content ?? string.Empty;
    }

    private static bool ContainsIgnoreCase(List<string> values, string candidate) {
        for (int i = 0; i < values.Count; i++) {
            if (string.Equals(values[i], candidate, StringComparison.OrdinalIgnoreCase)) {
                return true;
            }
        }

        return false;
    }

    private static string TrimForPreview(string? value, int maxLength) {
        if (string.IsNullOrWhiteSpace(value) || maxLength <= 0) { return string.Empty; }

        var chars = new List<char>(capacity: Math.Min(value.Length, maxLength));
        bool pendingWhitespace = false;
        int lastConsumedSourceIndex = -1;
        bool truncated = false;

        for (int i = 0; i < value.Length; i++) {
            var ch = value[i];
            if (char.IsWhiteSpace(ch)) {
                pendingWhitespace = chars.Count > 0;
                continue;
            }

            if (pendingWhitespace) {
                if (chars.Count >= maxLength) {
                    truncated = true;
                    break;
                }
                chars.Add(' ');
            }

            pendingWhitespace = false;

            if (chars.Count >= maxLength) {
                truncated = true;
                break;
            }
            chars.Add(ch);
            lastConsumedSourceIndex = i;
        }

        if (chars.Count == 0) { return string.Empty; }

        if (!truncated) {
            for (int i = lastConsumedSourceIndex + 1; i < value.Length; i++) {
                if (!char.IsWhiteSpace(value[i])) {
                    truncated = true;
                    break;
                }
            }
        }

        return truncated
            ? new string(chars.ToArray()).TrimEnd() + "..."
            : new string(chars.ToArray()).TrimEnd();
    }

    private async Task<CompactionExecutionResult> ExecuteCompactionCoreAsync(
        LlmProfile profile,
        CompactionRequest request,
        CancellationToken cancellationToken
    ) {
        var snapshot = _state.RecentHistory;
        var softContextTokenCap = (ulong)profile.SoftContextTokenCap;
        var historyCountBefore = snapshot.Count;
        var tokensBefore = EstimateCurrentContextTokens();
        int splitIndex = request.SplitIndex;

        if (splitIndex < 1 || splitIndex >= snapshot.Count
            || !snapshot[splitIndex - 1].IsObservationLike
            || snapshot[splitIndex] is not ActionEntry) {
            DebugUtil.Warning(
                StateMachineDebugCategory,
                $"[Compacting] splitIndex={splitIndex} no longer valid for current history (count={snapshot.Count}); aborting."
            );
            return CompactionExecutionResult.Failed(
                failureReason: CompactionFailureReason.InvalidSplitPoint,
                splitIndex: splitIndex,
                historyCountBefore: historyCountBefore,
                tokensBefore: tokensBefore,
                softContextTokenCap: softContextTokenCap
            );
        }

        var prefix = new List<HistoryEntry>(splitIndex);
        for (int i = 0; i < splitIndex; i++) {
            prefix.Add(snapshot[i]);
        }

        var messages = ProjectForSummarization(prefix, request.SummarizePrompt);
        var summary = await ContextSummarizer.SummarizeAsync(
            profile,
            messages,
            request.SystemPrompt,
            cancellationToken
        ).ConfigureAwait(false);

        if (string.IsNullOrEmpty(summary)) {
            DebugUtil.Warning(StateMachineDebugCategory, "[Compacting] Summarization returned empty; skipping replacement.");
            return CompactionExecutionResult.Failed(
                failureReason: CompactionFailureReason.EmptySummary,
                splitIndex: splitIndex,
                historyCountBefore: historyCountBefore,
                tokensBefore: tokensBefore,
                softContextTokenCap: softContextTokenCap
            );
        }

        _state.ReplacePrefixWithRecap(splitIndex, summary);
        var historyCountAfter = _state.RecentHistory.Count;
        var tokensAfter = EstimateCurrentContextTokens();

        return CompactionExecutionResult.Succeeded(
            splitIndex: splitIndex,
            summaryLength: summary.Length,
            historyCountBefore: historyCountBefore,
            historyCountAfter: historyCountAfter,
            tokensBefore: tokensBefore,
            tokensAfter: tokensAfter,
            softContextTokenCap: softContextTokenCap
        );
    }

    private async Task<StepOutcome> ProcessCompactingAsync(LlmProfile profile, CancellationToken cancellationToken) {
        if (!_compactionRequest.HasValue) {
            DebugUtil.Warning(StateMachineDebugCategory, "[Compacting] Entered without valid compaction request; aborting.");
            return StepOutcome.NoProgress;
        }

        var request = _compactionRequest.Value;
        int splitIndex = request.SplitIndex;
        DebugUtil.Info(StateMachineDebugCategory, $"[Compacting] Starting half-context compaction. splitIndex={splitIndex}");

        var outcome = await ExecuteCompactionCoreAsync(profile, request, cancellationToken).ConfigureAwait(false);
        if (!outcome.Applied) {
            _compactionRequest = null;
            DebugUtil.Warning(StateMachineDebugCategory, $"[Compacting] Compaction aborted reason={outcome.FailureReason?.ToString() ?? "unknown"}.");
            return StepOutcome.NoProgress;
        }

        _compactionRequest = null;
        DebugUtil.Info(
            StateMachineDebugCategory,
            $"[Compacting] Done. splitIndex={outcome.SplitIndex} summaryLen={outcome.SummaryLength} remaining={outcome.HistoryCountAfter} releasedTokens={outcome.TokensReleased}"
        );
        return StepOutcome.FromStateMutation();
    }
}
