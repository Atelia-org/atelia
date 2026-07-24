using Atelia.ChatSession;
using Atelia.ChatSession.Memory;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.SessionJournal.Derived;
using SJ = Atelia.SessionJournal;

namespace ChatSessionBacktestCli;

internal interface IRollingSummaryReplaySource {
    string SourceKind { get; }

    IAsyncEnumerable<RollingSummaryReplayStep> ReadStepsAsync(CancellationToken ct);
}

internal sealed record RollingSummaryReplayStep(
    RollingSummaryReplaySourceCursor Cursor,
    IReadOnlyList<IHistoryMessage> AppendedMessages,
    bool IsTriggerBoundary,
    bool ResetActiveHistory = false
);

internal sealed record RollingSummaryReplaySourceCursor(
    string SourceKind,
    string SourceId,
    long? EventOrdinal = null,
    string? EventCommit = null,
    EventAddress? SourceStartInclusive = null,
    EventAddress? SourceEndInclusive = null,
    EventAddress? SourceRawHead = null
);

internal static class RollingSummaryReplaySourceKinds {
    public const string LegacyChatSessionExport = "legacy-chat-session-export";
    public const string SessionJournal = "session-journal";
}

internal sealed class LegacyRollingSummaryReplaySource : IRollingSummaryReplaySource {
    private readonly ChatSessionLegacyEventSource _eventSource;

    public LegacyRollingSummaryReplaySource(ChatSessionLegacyEventSource eventSource)
        => _eventSource = eventSource ?? throw new ArgumentNullException(nameof(eventSource));

    public string SourceKind => RollingSummaryReplaySourceKinds.LegacyChatSessionExport;

    public async IAsyncEnumerable<RollingSummaryReplayStep> ReadStepsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct) {
        _ = Task.CompletedTask;
        for (int position = 0; position < _eventSource.Events.Count; position++) {
            ct.ThrowIfCancellationRequested();
            var replayEvent = _eventSource.Events[position];
            if (replayEvent.Ordinal != position) { throw new InvalidDataException($"Event ordinal mismatch at index {position}: {replayEvent.Ordinal}."); }
            if (replayEvent.Ordinal < 0) { throw new InvalidDataException("Replay event ordinal cannot be negative."); }

            var cursor = CreateCursor(replayEvent);
            switch (replayEvent.Kind) {
                case ChatSessionLegacyEventKinds.InitialState:
                    yield return new RollingSummaryReplayStep(
                        cursor,
                        ToHistoryMessages(replayEvent.Messages),
                        IsTriggerBoundary: false,
                        ResetActiveHistory: true
                    );
                    break;
                case ChatSessionLegacyEventKinds.ModelTurn:
                    yield return new RollingSummaryReplayStep(
                        cursor,
                        ToHistoryMessages(replayEvent.AppendedMessages),
                        IsTriggerBoundary: true
                    );
                    break;
                case ChatSessionLegacyEventKinds.UpdateSystemPrompt:
                case ChatSessionLegacyEventKinds.Compaction:
                case ChatSessionLegacyEventKinds.RedundantSave:
                    break;
                default:
                    throw new NotSupportedException($"Event kind '{replayEvent.Kind}' is not supported by rolling summary replay.");
            }
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static RollingSummaryReplaySourceCursor CreateCursor(ChatSessionLegacyReplayEvent replayEvent)
        => new(
            SourceKind: RollingSummaryReplaySourceKinds.LegacyChatSessionExport,
            SourceId: replayEvent.Commit ?? replayEvent.Ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture),
            EventOrdinal: replayEvent.Ordinal,
            EventCommit: replayEvent.Commit
        );

    private static IReadOnlyList<IHistoryMessage> ToHistoryMessages(IReadOnlyList<ChatSessionLegacyMessageDto>? messages)
        => messages is null || messages.Count == 0
            ? Array.AsReadOnly(Array.Empty<IHistoryMessage>())
            : Array.AsReadOnly(messages.Select(ChatSessionLegacyEventSourceProjection.ToHistoryMessage).ToArray());
}

internal sealed class SessionJournalRollingSummaryReplaySource : IRollingSummaryReplaySource {
    private readonly string _repoPath;

    private SessionJournalRollingSummaryReplaySource(string repoPath)
        => _repoPath = repoPath;

    public string SourceKind => RollingSummaryReplaySourceKinds.SessionJournal;

    public static SessionJournalRollingSummaryReplaySource Open(string sessionJournalRepoPath) {
        if (string.IsNullOrWhiteSpace(sessionJournalRepoPath)) {
            throw new ArgumentException("SessionJournal repo path cannot be empty.", nameof(sessionJournalRepoPath));
        }

        return new SessionJournalRollingSummaryReplaySource(Path.GetFullPath(sessionJournalRepoPath));
    }

    public async IAsyncEnumerable<RollingSummaryReplayStep> ReadStepsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct) {
        using var engine = SJ.SessionJournalEngine.Open(_repoPath);
        SJ.SessionHistoryReplay replay = engine.ReplayHistory(ct);
        foreach (SJ.AddressedSessionHistoryMessage addressed in replay.Messages) {
            ct.ThrowIfCancellationRequested();
            var cursor = new RollingSummaryReplaySourceCursor(
                SourceKind: RollingSummaryReplaySourceKinds.SessionJournal,
                SourceId: EventAddressTextCodec.Format(addressed.SourceEndInclusive),
                SourceStartInclusive: addressed.SourceStartInclusive,
                SourceEndInclusive: addressed.SourceEndInclusive,
                SourceRawHead: replay.SourceRawHead
            );
            yield return new RollingSummaryReplayStep(
                cursor,
                Array.AsReadOnly(new[] { addressed.Message }),
                IsTriggerBoundary: addressed.Message.Kind is HistoryMessageKind.Action or HistoryMessageKind.ToolResults
            );
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }
}

internal sealed class RollingSummaryReplayRunner {
    private readonly IRollingSummaryReplaySource _source;
    private readonly ICompletionClient _client;
    private readonly CompletionConnectionConfig _connection;
    private readonly ReplayMemoryMaintainerProfile _profile;
    private readonly string _callLogDir;
    private readonly int _thresholdTokens;
    private readonly int _maxEpochs;
    private readonly List<IHistoryMessage> _activeHistory = [];
    private SJ.MemoryPack _memoryPack = new();

    public RollingSummaryReplayRunner(
        IRollingSummaryReplaySource source,
        ICompletionClient client,
        CompletionConnectionConfig connection,
        ReplayMemoryMaintainerProfile profile,
        string callLogDir,
        int thresholdTokens,
        int maxEpochs
    ) {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _callLogDir = string.IsNullOrWhiteSpace(callLogDir) ? throw new ArgumentException("Call log directory cannot be empty.", nameof(callLogDir)) : callLogDir;
        _thresholdTokens = thresholdTokens;
        _maxEpochs = maxEpochs;
    }

    public bool HadFailure { get; private set; }

    public async IAsyncEnumerable<RollingSummaryReplayRecord> RunAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct) {
        int epochIndex = 0;

        await foreach (var step in _source.ReadStepsAsync(ct).ConfigureAwait(false)) {
            ct.ThrowIfCancellationRequested();
            if (step.ResetActiveHistory) { _activeHistory.Clear(); }
            if (step.AppendedMessages.Count > 0) { _activeHistory.AddRange(step.AppendedMessages); }
            if (!step.IsTriggerBoundary) { continue; }
            if (epochIndex >= _maxEpochs) { yield break; }

            int estimatedTokens = BacktestTextUtil.EstimateTokens(_activeHistory);
            if (estimatedTokens < _thresholdTokens) { continue; }

            int splitIndex = HistoryWindowSplitPolicy.FindHalfContextSplitPoint(
                _activeHistory,
                static message => (ulong)BacktestTextUtil.EstimateTokens(message)
            );
            if (splitIndex < 0) { continue; }

            int beforeMaxCallId = RollingSummaryCallLogUtil.GetMaxCallId(_callLogDir);
            string callLogPath = Path.Combine(Path.GetFullPath(_callLogDir), $"{beforeMaxCallId + 1:0000}.json");
            var oldBlock = _memoryPack.TryGetBlock(_profile.Target, out var found) ? found : new SJ.MemoryPackBlock(string.Empty);
            var fragment = _activeHistory.Take(splitIndex).ToArray();
            var recentHistory = new SJ.RecentHistorySlice(
                SJ.ContextHeaderSnapshot.FromRenderedMemoryPack(_memoryPack.Render()),
                fragment,
                SourceId: step.Cursor.SourceId,
                EstimatedTokens: (ulong)BacktestTextUtil.EstimateTokens(fragment)
            );

            var loggingClient = new LoggingCompletionClient(
                _client,
                _connection,
                _callLogDir,
                new CompletionCallLogContext(
                    Command: "replay-rolling-summary",
                    EpochIndex: epochIndex,
                    EventOrdinal: step.Cursor.EventOrdinal,
                    MaintainerId: _profile.MaintainerId,
                    TargetCarrier: SJ.MemoryPackCarrierTokens.ToStorageToken(_profile.Target.Carrier),
                    TargetBlockId: _profile.Target.BlockKey
                )
            );
            var maintainer = new SJ.RewriteMemoryBlockMaintainer(
                _profile.RewriteProfile,
                loggingClient,
                _connection.ModelId
            );

            SJ.MemoryBlockMaintenanceResult? result = null;
            string? newBlockText = null;
            Exception? exception = null;
            try {
                var batch = await SJ.MemoryMaintenanceOrchestrator.RunAsync(
                    _memoryPack,
                    recentHistory,
                    [maintainer],
                    ct
                ).ConfigureAwait(false);
                result = batch.Results[0];
                newBlockText = result.NewBlock.Text;
                _memoryPack = batch.UpdatedMemoryPack;
                _activeHistory.RemoveRange(0, splitIndex);
            }
            catch (Exception ex) when (ex is InvalidOperationException or SJ.SessionJournalTurnAbortedException or HttpRequestException or TaskCanceledException) {
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
                step.Cursor,
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
}

internal sealed record ReplayMemoryMaintainerProfile(
    string PresetName,
    SJ.MemoryRewriteProfile RewriteProfile
) {
    public string MaintainerId => RewriteProfile.Id;
    public SJ.MemoryPackBlockPath Target => RewriteProfile.Target;
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
    string SourceKind,
    string SourceId,
    long? EventOrdinal,
    string? EventCommit,
    string? SourceRawHead,
    string? SourceStartInclusive,
    string? SourceEndInclusive,
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
    CompletionDescriptor? Invocation,
    IReadOnlyList<string>? Errors
) {
    public static RollingSummaryReplayRecord Create(
        int epochIndex,
        RollingSummaryReplaySourceCursor cursor,
        int thresholdTokens,
        int estimatedTokens,
        int splitIndex,
        int remainingActiveMessageCount,
        ReplayMemoryMaintainerProfile profile,
        string? oldBlockText,
        string? newBlockText,
        string callLogPath,
        IReadOnlyList<string> callLogPaths,
        SJ.MemoryBlockMaintenanceResult? result,
        Exception? exception
    ) {
        return new(
            Schema: "atelia.chat-session.memory-maintainer-backtest.v2",
            PresetName: profile.PresetName,
            EpochIndex: epochIndex,
            SourceKind: cursor.SourceKind,
            SourceId: cursor.SourceId,
            EventOrdinal: cursor.EventOrdinal,
            EventCommit: cursor.EventCommit,
            SourceRawHead: FormatAddress(cursor.SourceRawHead),
            SourceStartInclusive: FormatAddress(cursor.SourceStartInclusive),
            SourceEndInclusive: FormatAddress(cursor.SourceEndInclusive),
            ReplayMode: "ignore-original-compaction.synthetic-sliding-prefix",
            ThresholdTokens: thresholdTokens,
            EstimatedTokens: estimatedTokens,
            SplitIndex: splitIndex,
            SlidingOutMessageCount: splitIndex,
            RemainingActiveMessageCount: remainingActiveMessageCount,
            TargetCarrier: SJ.MemoryPackCarrierTokens.ToStorageToken(profile.Target.Carrier),
            TargetBlockId: profile.Target.BlockKey,
            OldBlock: BacktestOutputUtil.CreateBlockPreview(oldBlockText),
            NewBlock: BacktestOutputUtil.CreateBlockPreview(newBlockText),
            CallLogPath: callLogPath,
            CallLogPaths: callLogPaths,
            Status: exception is null ? "succeeded" : "failed",
            ExceptionType: exception?.GetType().FullName,
            ExceptionMessage: exception?.Message,
            Invocation: result?.Invocation,
            Errors: result?.Errors
        );
    }

    private static string? FormatAddress(Atelia.EventJournal.EventAddress? address)
        => address is null ? null : EventAddressTextCodec.Format(address.Value);
}
