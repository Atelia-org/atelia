using Atelia.ChatSession;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;

namespace ChatSessionBacktestCli;

internal static class RollingSummaryReplayDefaults {
    public const string MaintainerId = "rolling-summary.memory-block";

    public const string SystemPrompt = """
        You maintain one durable rolling summary block for a long-running chat session.
        Return only the complete replacement text for the target memory block.
        Do not include prefaces, analysis labels, Markdown code fences, or explanations outside the block.
        The first character of your response must be the first character of the memory block itself.
        """;

    public const string UserPrompt = """
        从“即将滑出上下文窗口”的片段中提炼后续仍有用的信息，并把它们合并进当前 rolling summary。

        目标：
        - 保留长期有用的事实、决策、未完成任务、用户偏好、当前实现状态、重要路径和验证结果。
        - 删除已经过时、被后文否定、纯寒暄、临时操作细节、低价值逐字流水账。
        - 如果新片段修正了旧 summary，直接更新为当前可信版本。
        - 输出完整新版 block，不要只输出 delta。
        - 默认使用简体中文；代码标识符、路径、命令、模型名、专有名词保持原文。
        - 结构要便于后续 maintainer 再维护，优先短小分组和项目符号。
        """;
}

internal sealed class RollingSummaryReplayRunner {
    private readonly ChatSessionLegacyEventSource _eventSource;
    private readonly ICompletionClient _client;
    private readonly CompletionConnectionConfig _connection;
    private readonly MemoryPackBlockPath _target;
    private readonly string _systemPrompt;
    private readonly string _userPrompt;
    private readonly ToolRegistry _toolRegistry;
    private readonly string _callLogDir;
    private readonly int _thresholdTokens;
    private readonly int _maxEpochs;
    private readonly List<IHistoryMessage> _activeHistory = [];
    private MemoryPack _memoryPack = new();

    public RollingSummaryReplayRunner(
        ChatSessionLegacyEventSource eventSource,
        ICompletionClient client,
        CompletionConnectionConfig connection,
        MemoryPackBlockPath target,
        string systemPrompt,
        string userPrompt,
        ToolRegistry toolRegistry,
        string callLogDir,
        int thresholdTokens,
        int maxEpochs
    ) {
        _eventSource = eventSource ?? throw new ArgumentNullException(nameof(eventSource));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _systemPrompt = systemPrompt ?? throw new ArgumentNullException(nameof(systemPrompt));
        _userPrompt = userPrompt ?? throw new ArgumentNullException(nameof(userPrompt));
        _toolRegistry = toolRegistry ?? throw new ArgumentNullException(nameof(toolRegistry));
        _callLogDir = string.IsNullOrWhiteSpace(callLogDir) ? throw new ArgumentException("Call log directory cannot be empty.", nameof(callLogDir)) : callLogDir;
        _thresholdTokens = thresholdTokens;
        _maxEpochs = maxEpochs;
    }

    public bool HadFailure { get; private set; }

    public async IAsyncEnumerable<RollingSummaryReplayRecord> RunAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct) {
        int epochIndex = 0;

        foreach (var replayEvent in _eventSource.Events) {
            ct.ThrowIfCancellationRequested();
            if (replayEvent.Ordinal < 0) { throw new InvalidDataException("Replay event ordinal cannot be negative."); }

            bool appendedModelTurn = ApplyEvent(replayEvent);
            if (!appendedModelTurn) { continue; }
            if (epochIndex >= _maxEpochs) { yield break; }

            int estimatedTokens = BacktestTextUtil.EstimateTokens(_activeHistory);
            if (estimatedTokens < _thresholdTokens) { continue; }

            int splitIndex = RollingSummarySplitPolicy.FindHalfContextSplitPoint(_activeHistory);
            if (splitIndex < 0) { continue; }

            int beforeMaxCallId = RollingSummaryCallLogUtil.GetMaxCallId(_callLogDir);
            string callLogPath = Path.Combine(Path.GetFullPath(_callLogDir), $"{beforeMaxCallId + 1:0000}.json");
            var oldBlock = _memoryPack.TryGetBlock(_target, out var found) ? found : new MemoryPackBlock(string.Empty);
            var fragment = _activeHistory.Take(splitIndex).ToArray();
            var recentHistory = new RecentHistorySlice(
                ContextHeaderSnapshot.FromRenderedMemoryPack(_memoryPack.Render()),
                fragment,
                SourceId: replayEvent.Commit ?? replayEvent.Ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture),
                EstimatedTokens: (ulong)BacktestTextUtil.EstimateTokens(fragment)
            );

            var loggingClient = new LoggingCompletionClient(
                _client,
                _connection,
                _callLogDir,
                new CompletionCallLogContext(
                    Command: "replay-rolling-summary",
                    EpochIndex: epochIndex,
                    EventOrdinal: replayEvent.Ordinal,
                    MaintainerId: RollingSummaryReplayDefaults.MaintainerId,
                    TargetCarrier: MemoryPackCarrierTokens.ToStorageToken(_target.Carrier),
                    TargetBlockId: _target.BlockKey
                )
            );
            var maintainer = new CompletionMemoryBlockMaintainer(
                RollingSummaryReplayDefaults.MaintainerId,
                _target,
                loggingClient,
                _connection.ModelId,
                _systemPrompt,
                _userPrompt,
                _toolRegistry.CreateSession()
            );

            MemoryBlockMaintenanceResult? result = null;
            string? newBlockText = null;
            Exception? exception = null;
            try {
                result = await maintainer.MaintainAsync(
                    new MemoryBlockMaintenanceRequest(recentHistory, _target, oldBlock),
                    ct
                ).ConfigureAwait(false);
                newBlockText = RollingSummaryTextUtil.NormalizeBlockText(result.NewBlock.Text);
                var draft = new MemoryPackDraft(_memoryPack);
                draft.UpsertBlock(_target, newBlockText);
                _memoryPack = draft.Build();
                _activeHistory.RemoveRange(0, splitIndex);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ChatSessionTurnAbortedException or HttpRequestException or TaskCanceledException) {
                HadFailure = true;
                exception = ex;
            }

            yield return RollingSummaryReplayRecord.Create(
                epochIndex,
                replayEvent,
                _thresholdTokens,
                estimatedTokens,
                splitIndex,
                _activeHistory.Count,
                _target,
                oldBlock.Text,
                newBlockText,
                callLogPath,
                result,
                exception
            );

            epochIndex++;
            if (exception is not null) { yield break; }
        }
    }

    private bool ApplyEvent(ChatSessionLegacyReplayEvent replayEvent) {
        switch (replayEvent.Kind) {
            case ChatSessionLegacyEventKinds.InitialState:
                _activeHistory.Clear();
                _activeHistory.AddRange(
                    (replayEvent.Messages ?? Array.Empty<ChatSessionLegacyMessageDto>())
                    .Select(ChatSessionLegacyEventSourceProjection.ToHistoryMessage)
                );
                return false;
            case ChatSessionLegacyEventKinds.ModelTurn:
                _activeHistory.AddRange(
                    (replayEvent.AppendedMessages ?? Array.Empty<ChatSessionLegacyMessageDto>())
                    .Select(ChatSessionLegacyEventSourceProjection.ToHistoryMessage)
                );
                return true;
            case ChatSessionLegacyEventKinds.UpdateSystemPrompt:
            case ChatSessionLegacyEventKinds.Compaction:
            case ChatSessionLegacyEventKinds.RedundantSave:
                return false;
            default:
                throw new NotSupportedException($"Event kind '{replayEvent.Kind}' is not supported by rolling summary replay.");
        }
    }
}

internal static class RollingSummaryTextUtil {
    public static string NormalizeBlockText(string text) {
        var trimmed = (text ?? string.Empty).Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal)) { return trimmed; }

        int firstLineEnd = trimmed.IndexOf('\n', StringComparison.Ordinal);
        if (firstLineEnd < 0) { return trimmed; }

        string openingFence = trimmed[..firstLineEnd].Trim();
        if (!openingFence.StartsWith("```", StringComparison.Ordinal)) { return trimmed; }

        int closingFenceStart = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        if (closingFenceStart <= firstLineEnd) { return trimmed; }

        string trailing = trimmed[(closingFenceStart + 3)..].Trim();
        if (trailing.Length > 0) { return trimmed; }

        return trimmed[(firstLineEnd + 1)..closingFenceStart].Trim();
    }
}

internal static class RollingSummarySplitPolicy {
    public static int FindHalfContextSplitPoint(IReadOnlyList<IHistoryMessage> messages) {
        if (messages.Count < 2) { return -1; }

        int totalTokens = BacktestTextUtil.EstimateTokens(messages);
        int halfTokens = (totalTokens + 1) / 2;
        int cumulativeTokens = 0;
        int lastValidSuffixStart = -1;

        for (int i = 0; i < messages.Count - 1; i++) {
            cumulativeTokens += BacktestTextUtil.EstimateTokens(messages[i]);
            if (!IsObservationLike(messages[i]) || messages[i + 1].Kind != HistoryMessageKind.Action) { continue; }

            int suffixStart = i;
            if (suffixStart == 0) { continue; }
            if (suffixStart == 1 && messages[0] is RecapMessage) { continue; }

            lastValidSuffixStart = suffixStart;
            if (cumulativeTokens >= halfTokens) { return suffixStart; }
        }

        return lastValidSuffixStart;
    }

    private static bool IsObservationLike(IHistoryMessage message)
        => message.Kind is HistoryMessageKind.Observation or HistoryMessageKind.ToolResults;
}

internal static class RollingSummaryCallLogUtil {
    public static int GetMaxCallId(string callLogDir) {
        if (!Directory.Exists(callLogDir)) { return 0; }

        int max = 0;
        foreach (var path in Directory.EnumerateFiles(callLogDir, "*.json")) {
            if (int.TryParse(Path.GetFileNameWithoutExtension(path), out int callId)) {
                max = Math.Max(max, callId);
            }
        }

        return max;
    }
}

internal sealed record RollingSummaryReplayRecord(
    string Schema,
    int EpochIndex,
    int EventOrdinal,
    string? EventCommit,
    string ReplayMode,
    int ThresholdTokens,
    int EstimatedTokens,
    int SplitIndex,
    int SlidingOutMessageCount,
    int RemainingActiveMessageCount,
    string TargetCarrier,
    string TargetBlockId,
    MemoryBlockPreview? OldBlock,
    MemoryBlockPreview? NewBlock,
    string CallLogPath,
    string Status,
    string? ExceptionType,
    string? ExceptionMessage,
    int ToolCallsExecuted,
    IReadOnlyList<string>? Errors
) {
    public static RollingSummaryReplayRecord Create(
        int epochIndex,
        ChatSessionLegacyReplayEvent replayEvent,
        int thresholdTokens,
        int estimatedTokens,
        int splitIndex,
        int remainingActiveMessageCount,
        MemoryPackBlockPath target,
        string? oldBlockText,
        string? newBlockText,
        string callLogPath,
        MemoryBlockMaintenanceResult? result,
        Exception? exception
    )
        => new(
            Schema: "atelia.chat-session.rolling-summary-backtest.v1",
            EpochIndex: epochIndex,
            EventOrdinal: replayEvent.Ordinal,
            EventCommit: replayEvent.Commit,
            ReplayMode: "ignore-original-compaction.synthetic-sliding-prefix",
            ThresholdTokens: thresholdTokens,
            EstimatedTokens: estimatedTokens,
            SplitIndex: splitIndex,
            SlidingOutMessageCount: splitIndex,
            RemainingActiveMessageCount: remainingActiveMessageCount,
            TargetCarrier: MemoryPackCarrierTokens.ToStorageToken(target.Carrier),
            TargetBlockId: target.BlockKey,
            OldBlock: BacktestOutputUtil.CreateBlockPreview(oldBlockText),
            NewBlock: BacktestOutputUtil.CreateBlockPreview(newBlockText),
            CallLogPath: callLogPath,
            Status: exception is null ? "succeeded" : "failed",
            ExceptionType: exception?.GetType().FullName,
            ExceptionMessage: exception?.Message,
            ToolCallsExecuted: result?.ToolCallsExecuted ?? 0,
            Errors: result?.Errors
        );
}
