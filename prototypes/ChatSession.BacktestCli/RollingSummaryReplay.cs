using Atelia.ChatSession;
using Atelia.ChatSession.Memory;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;

namespace ChatSessionBacktestCli;

internal static class RollingSummaryReplayDefaults {
    public const string PresetName = "rolling-summary";

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
    private readonly ReplayMemoryMaintainerProfile _profile;
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
        ReplayMemoryMaintainerProfile profile,
        ToolRegistry toolRegistry,
        string callLogDir,
        int thresholdTokens,
        int maxEpochs
    ) {
        _eventSource = eventSource ?? throw new ArgumentNullException(nameof(eventSource));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
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
            var oldBlock = _memoryPack.TryGetBlock(_profile.Target, out var found) ? found : new MemoryPackBlock(string.Empty);
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
                    MaintainerId: _profile.MaintainerId,
                    TargetCarrier: MemoryPackCarrierTokens.ToStorageToken(_profile.Target.Carrier),
                    TargetBlockId: _profile.Target.BlockKey
                )
            );
            var maintainer = _profile.CreateMaintainer(loggingClient, _connection.ModelId, _toolRegistry.CreateSession());

            MemoryBlockMaintenanceResult? result = null;
            string? newBlockText = null;
            Exception? exception = null;
            try {
                result = await maintainer.MaintainAsync(
                    new MemoryBlockMaintenanceRequest(recentHistory, _profile.Target, oldBlock),
                    ct
                ).ConfigureAwait(false);
                newBlockText = MemoryBlockTextNormalizer.NormalizeBlockText(result.NewBlock.Text);
                var draft = new MemoryPackDraft(_memoryPack);
                draft.UpsertBlock(_profile.Target, newBlockText);
                _memoryPack = draft.Build();
                _activeHistory.RemoveRange(0, splitIndex);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ChatSessionTurnAbortedException or HttpRequestException or TaskCanceledException) {
                HadFailure = true;
                exception = ex;
            }

            int afterMaxCallId = RollingSummaryCallLogUtil.GetMaxCallId(_callLogDir);
            var callLogPaths = RollingSummaryCallLogUtil.BuildCallLogPaths(
                _callLogDir,
                beforeMaxCallId,
                afterMaxCallId
            );

            yield return RollingSummaryReplayRecord.Create(
                epochIndex,
                replayEvent,
                _thresholdTokens,
                estimatedTokens,
                splitIndex,
                _activeHistory.Count,
                _profile,
                oldBlock.Text,
                newBlockText,
                callLogPath,
                callLogPaths,
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

internal sealed record ReplayMemoryMaintainerProfile(
    string PresetName,
    string MaintainerId,
    MemoryPackBlockPath Target,
    Func<ICompletionClient, string, ToolSession, IMemoryBlockMaintainer> CreateMaintainer
);

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

    public static IReadOnlyList<string> BuildCallLogPaths(
        string callLogDir,
        int beforeMaxCallId,
        int afterMaxCallId
    ) => Enumerable.Range(
        beforeMaxCallId + 1,
        Math.Max(0, afterMaxCallId - beforeMaxCallId)
    ).Select(id => Path.Combine(Path.GetFullPath(callLogDir), $"{id:0000}.json")).ToArray();
}

internal sealed record RollingSummaryReplayRecord(
    string Schema,
    string PresetName,
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
    IReadOnlyList<string> CallLogPaths,
    string Status,
    string? ExceptionType,
    string? ExceptionMessage,
    int ToolCallsExecuted,
    IReadOnlyList<string>? Errors,
    IReadOnlyList<MemoryMaintenanceStageBacktestRecord>? Stages,
    IReadOnlyList<MemoryMaintenanceNotice>? Notices,
    IReadOnlyList<string>? Diagnostics
) {
    public static RollingSummaryReplayRecord Create(
        int epochIndex,
        ChatSessionLegacyReplayEvent replayEvent,
        int thresholdTokens,
        int estimatedTokens,
        int splitIndex,
        int remainingActiveMessageCount,
        ReplayMemoryMaintainerProfile profile,
        string? oldBlockText,
        string? newBlockText,
        string callLogPath,
        IReadOnlyList<string> callLogPaths,
        MemoryBlockMaintenanceResult? result,
        Exception? exception
    ) {
        var loopFailure = exception as MemoryDocumentAgentLoopException;
        return new(
            Schema: "atelia.chat-session.rolling-summary-backtest.v1",
            PresetName: profile.PresetName,
            EpochIndex: epochIndex,
            EventOrdinal: replayEvent.Ordinal,
            EventCommit: replayEvent.Commit,
            ReplayMode: "ignore-original-compaction.synthetic-sliding-prefix",
            ThresholdTokens: thresholdTokens,
            EstimatedTokens: estimatedTokens,
            SplitIndex: splitIndex,
            SlidingOutMessageCount: splitIndex,
            RemainingActiveMessageCount: remainingActiveMessageCount,
            TargetCarrier: MemoryPackCarrierTokens.ToStorageToken(profile.Target.Carrier),
            TargetBlockId: profile.Target.BlockKey,
            OldBlock: BacktestOutputUtil.CreateBlockPreview(oldBlockText),
            NewBlock: BacktestOutputUtil.CreateBlockPreview(newBlockText),
            CallLogPath: callLogPath,
            CallLogPaths: callLogPaths,
            Status: exception is null ? "succeeded" : "failed",
            ExceptionType: exception?.GetType().FullName,
            ExceptionMessage: exception?.Message,
            ToolCallsExecuted: result?.ToolCallsExecuted ?? loopFailure?.ToolCallsExecuted ?? 0,
            Errors: result?.Errors ?? loopFailure?.Errors,
            Stages: result?.Stages?.Select(MemoryMaintenanceStageBacktestRecord.From).ToArray()
                ?? (loopFailure is null ? null : [MemoryMaintenanceStageBacktestRecord.FromFailure(loopFailure)]),
            Notices: result?.Notices,
            Diagnostics: result?.Diagnostics
        );
    }
}

internal sealed record MemoryMaintenanceStageBacktestRecord(
    string Stage,
    string Status,
    int BeforeTokens,
    int? AfterTokens,
    int? TargetTokens,
    bool? TargetReached,
    CompletionDescriptor? Invocation,
    IReadOnlyList<string>? Errors,
    int ToolCallsExecuted,
    string? FailureType,
    string? FailureMessage
) {
    public static MemoryMaintenanceStageBacktestRecord From(MemoryBlockMaintenanceStageResult stage)
        => new(
            stage.Stage,
            stage.Status.ToString().ToLowerInvariant(),
            stage.BeforeTokens,
            stage.AfterTokens,
            stage.TargetTokens,
            stage.TargetReached,
            stage.Invocation,
            stage.Errors,
            stage.ToolCallsExecuted,
            stage.FailureType,
            stage.FailureMessage
        );

    public static MemoryMaintenanceStageBacktestRecord FromFailure(MemoryDocumentAgentLoopException failure)
        => new(
            failure.Stage,
            "failed",
            failure.BeforeTokens,
            failure.WorkingTokens,
            failure.TargetTokens,
            null,
            failure.Invocation,
            failure.Errors,
            failure.ToolCallsExecuted,
            failure.GetType().FullName,
            failure.Message
        );
}
