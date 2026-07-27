using Atelia.ChatSession;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.SessionJournal;
using Atelia.SessionJournal.Derived;
using Atelia.SessionJournal.Maintainers;
using SJ = Atelia.SessionJournal;

namespace ChatSessionBacktestCli;

internal interface IRollingSummaryReplaySource {
    string SourceKind { get; }

    IAsyncEnumerable<RollingSummaryReplayStep> ReadStepsAsync(CancellationToken ct);
}

internal interface IRollingSummaryRepositoryBound {
    string RepositoryPath { get; }
}

internal sealed record RollingSummaryReplayMessage {
    public RollingSummaryReplayMessage(
        IHistoryMessage message,
        EventAddress? sourceStartInclusive = null,
        EventAddress? sourceEndInclusive = null
    ) {
        ArgumentNullException.ThrowIfNull(message);
        if (sourceStartInclusive.HasValue != sourceEndInclusive.HasValue) {
            throw new ArgumentException("Source start and end addresses must either both be present or both be absent.");
        }

        Message = message;
        SourceStartInclusive = sourceStartInclusive;
        SourceEndInclusive = sourceEndInclusive;
    }

    public IHistoryMessage Message { get; }
    public EventAddress? SourceStartInclusive { get; }
    public EventAddress? SourceEndInclusive { get; }
}

internal sealed record RollingSummaryReplayStep(
    RollingSummaryReplaySourceCursor TriggerCursor,
    IReadOnlyList<RollingSummaryReplayMessage> AppendedEntries,
    bool IsTriggerBoundary,
    bool ResetActiveHistory = false
);

internal sealed record RollingSummaryReplaySourceCursor(
    string SourceKind,
    string SourceId,
    long? EventOrdinal = null,
    string? EventCommit = null,
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
                        ToReplayMessages(replayEvent.Messages),
                        IsTriggerBoundary: false,
                        ResetActiveHistory: true
                    );
                    break;
                case ChatSessionLegacyEventKinds.ModelTurn:
                    yield return new RollingSummaryReplayStep(
                        cursor,
                        ToReplayMessages(replayEvent.AppendedMessages),
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

    private static IReadOnlyList<RollingSummaryReplayMessage> ToReplayMessages(IReadOnlyList<ChatSessionLegacyMessageDto>? messages)
        => messages is null || messages.Count == 0
            ? Array.AsReadOnly(Array.Empty<RollingSummaryReplayMessage>())
            : Array.AsReadOnly(messages
                .Select(ChatSessionLegacyEventSourceProjection.ToHistoryMessage)
                .Select(static message => new RollingSummaryReplayMessage(message))
                .ToArray());
}

internal sealed class SessionJournalRollingSummaryReplaySource
    : IRollingSummaryReplaySource, IRollingSummaryRepositoryBound {
    private readonly string _repoPath;

    private SessionJournalRollingSummaryReplaySource(string repoPath)
        => _repoPath = repoPath;

    public string SourceKind => RollingSummaryReplaySourceKinds.SessionJournal;
    public string RepositoryPath => _repoPath;

    public static SessionJournalRollingSummaryReplaySource Open(string sessionJournalRepoPath) {
        if (string.IsNullOrWhiteSpace(sessionJournalRepoPath)) {
            throw new ArgumentException("SessionJournal repo path cannot be empty.", nameof(sessionJournalRepoPath));
        }

        return new SessionJournalRollingSummaryReplaySource(Path.GetFullPath(sessionJournalRepoPath));
    }

    public async IAsyncEnumerable<RollingSummaryReplayStep> ReadStepsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct) {
        SJ.SessionHistoryReplay replay;
        using (var engine = SJ.SessionJournalEngine.Open(_repoPath)) {
            replay = engine.ReplayHistory(ct);
        }

        foreach (SJ.AddressedSessionHistoryMessage addressed in replay.Messages) {
            ct.ThrowIfCancellationRequested();
            var cursor = new RollingSummaryReplaySourceCursor(
                SourceKind: RollingSummaryReplaySourceKinds.SessionJournal,
                SourceId: EventAddressTextCodec.Format(addressed.SourceEndInclusive),
                SourceRawHead: replay.SourceRawHead
            );
            yield return new RollingSummaryReplayStep(
                cursor,
                Array.AsReadOnly([
                    new RollingSummaryReplayMessage(
                        addressed.Message,
                        addressed.SourceStartInclusive,
                        addressed.SourceEndInclusive
                    )
                ]),
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
    private readonly IRollingSummaryArtifactWriter? _artifactWriter;
    private readonly string _command;
    private readonly string _callLogDir;
    private readonly int _thresholdTokens;
    private readonly int _maxEpochs;
    private readonly List<RollingSummaryReplayMessage> _activeHistory = [];
    private SJ.MemoryPack _memoryPack = new();

    public RollingSummaryReplayRunner(
        IRollingSummaryReplaySource source,
        ICompletionClient client,
        CompletionConnectionConfig connection,
        ReplayMemoryMaintainerProfile profile,
        string callLogDir,
        int thresholdTokens,
        int maxEpochs,
        IRollingSummaryArtifactWriter? artifactWriter = null,
        string command = "replay-rolling-summary"
    ) {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        if (artifactWriter is not null &&
            !string.Equals(artifactWriter.RequiredSourceKind, source.SourceKind, StringComparison.Ordinal)) {
            throw new ArgumentException(
                $"Artifact writer requires source kind '{artifactWriter.RequiredSourceKind}', but replay source kind is '{source.SourceKind}'.",
                nameof(artifactWriter)
            );
        }
        if (artifactWriter is IRollingSummaryRepositoryBound repositoryBoundWriter) {
            if (source is not IRollingSummaryRepositoryBound repositoryBoundSource ||
                !PathsEqual(repositoryBoundSource.RepositoryPath, repositoryBoundWriter.RepositoryPath)) {
                throw new ArgumentException(
                    "Repository-bound replay source and artifact writer must target the same SessionJournal repository.",
                    nameof(artifactWriter)
                );
            }
        }
        _artifactWriter = artifactWriter;
        _command = string.IsNullOrWhiteSpace(command)
            ? throw new ArgumentException("Replay command cannot be empty.", nameof(command))
            : command;
        _callLogDir = string.IsNullOrWhiteSpace(callLogDir) ? throw new ArgumentException("Call log directory cannot be empty.", nameof(callLogDir)) : callLogDir;
        _thresholdTokens = thresholdTokens;
        _maxEpochs = maxEpochs;
    }

    public bool HadFailure { get; private set; }

    public async IAsyncEnumerable<RollingSummaryReplayRecord> RunAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct) {
        int epochIndex = 0;
        bool hasObservedSourceRawHead = false;
        EventAddress? expectedSourceRawHead = null;
        bool artifactWriterPrepared = false;

        await foreach (var step in _source.ReadStepsAsync(ct).ConfigureAwait(false)) {
            ct.ThrowIfCancellationRequested();
            ValidateStepSource(step, ref hasObservedSourceRawHead, ref expectedSourceRawHead);
            if (_artifactWriter is not null && !artifactWriterPrepared) {
                EventAddress sourceRawHead = step.TriggerCursor.SourceRawHead
                    ?? throw new InvalidDataException("Artifact-producing replay requires a source raw head.");
                await _artifactWriter.PrepareAsync(sourceRawHead, ct).ConfigureAwait(false);
                artifactWriterPrepared = true;
            }
            if (step.ResetActiveHistory) { _activeHistory.Clear(); }
            if (step.AppendedEntries.Count > 0) { _activeHistory.AddRange(step.AppendedEntries); }
            if (!step.IsTriggerBoundary) { continue; }
            if (epochIndex >= _maxEpochs) { yield break; }

            var activeMessages = _activeHistory.Select(static entry => entry.Message).ToArray();
            int estimatedTokens = BacktestTextUtil.EstimateTokens(activeMessages);
            if (estimatedTokens < _thresholdTokens) { continue; }

            int splitIndex = HistoryWindowSplitPolicy.FindHalfContextSplitPoint(
                activeMessages,
                static message => (ulong)BacktestTextUtil.EstimateTokens(message)
            );
            if (splitIndex < 0) { continue; }

            var fragmentEntries = _activeHistory.Take(splitIndex).ToArray();
            (EventAddress? fragmentSourceStartInclusive, EventAddress? fragmentSourceEndInclusive)
                = GetFragmentSourceRange(fragmentEntries, step.TriggerCursor);
            var fragment = fragmentEntries.Select(static entry => entry.Message).ToArray();
            int beforeMaxCallId = RollingSummaryCallLogUtil.GetMaxCallId(_callLogDir);
            string callLogPath = Path.Combine(Path.GetFullPath(_callLogDir), $"{beforeMaxCallId + 1:0000}.json");
            var oldBlock = _memoryPack.TryGetBlock(_profile.Target, out var found) ? found : new SJ.MemoryPackBlock(string.Empty);
            var recentHistory = new SJ.RecentHistorySlice(
                SJ.ContextHeaderSnapshot.FromRenderedMemoryPack(_memoryPack.Render()),
                fragment,
                SourceId: step.TriggerCursor.SourceId,
                EstimatedTokens: (ulong)BacktestTextUtil.EstimateTokens(fragment)
            );

            var loggingClient = new LoggingCompletionClient(
                _client,
                _connection,
                _callLogDir,
                new CompletionCallLogContext(
                    Command: _command,
                    EpochIndex: epochIndex,
                    EventOrdinal: step.TriggerCursor.EventOrdinal,
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
            SJ.MemoryMaintenanceBatchResult? batch = null;
            string? newBlockText = null;
            Exception? exception = null;
            try {
                batch = await SJ.MemoryMaintenanceOrchestrator.RunAsync(
                    _memoryPack,
                    recentHistory,
                    [maintainer],
                    ct
                ).ConfigureAwait(false);
                result = batch.Results[0];
                newBlockText = result.NewBlock.Text;
            }
            catch (Exception ex) when (
                ex is InvalidOperationException or SJ.SessionJournalTurnAbortedException or HttpRequestException ||
                ex is TaskCanceledException && !ct.IsCancellationRequested
            ) {
                HadFailure = true;
                exception = ex;
            }

            IReadOnlyList<string> callLogPaths = loggingClient.WrittenCallLogPaths;
            if (callLogPaths.Count > 0) {
                callLogPath = callLogPaths[0];
            }

            RollingSummaryArtifactLink? artifactLink = null;
            if (exception is null && _artifactWriter is not null) {
                if (step.TriggerCursor.SourceRawHead is null ||
                    fragmentSourceStartInclusive is null ||
                    fragmentSourceEndInclusive is not { } sourceEndInclusive) {
                    throw new InvalidDataException(
                        "Artifact-producing replay requires source raw head and an addressed fragment range."
                    );
                }

                try {
                    artifactLink = await _artifactWriter.WriteProducedAsync(
                        new RollingSummaryArtifactCandidate(
                            sourceEndInclusive,
                            batch!.UpdatedMemoryPack,
                            result!,
                            callLogPaths
                        ),
                        ct
                    ).ConfigureAwait(false);
                }
                catch (RollingSummaryArtifactWriteException ex) {
                    HadFailure = true;
                    exception = ex;
                }
            }

            if (exception is null) {
                _memoryPack = batch!.UpdatedMemoryPack;
                _activeHistory.RemoveRange(0, splitIndex);
            }

            yield return RollingSummaryReplayRecord.Create(
                epochIndex,
                step.TriggerCursor,
                fragmentSourceStartInclusive,
                fragmentSourceEndInclusive,
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
                exception,
                artifactLink
            );

            epochIndex++;
            if (exception is not null) { yield break; }
        }
    }

    private void ValidateStepSource(
        RollingSummaryReplayStep step,
        ref bool hasObservedSourceRawHead,
        ref EventAddress? expectedSourceRawHead
    ) {
        if (!string.Equals(_source.SourceKind, step.TriggerCursor.SourceKind, StringComparison.Ordinal)) {
            throw new InvalidDataException(
                $"Replay source kind '{_source.SourceKind}' does not match step source kind '{step.TriggerCursor.SourceKind}'."
            );
        }

        if (!hasObservedSourceRawHead) {
            expectedSourceRawHead = step.TriggerCursor.SourceRawHead;
            hasObservedSourceRawHead = true;
        }
        else if (expectedSourceRawHead != step.TriggerCursor.SourceRawHead) {
            throw new InvalidDataException("Replay source raw head changed while reading one replay snapshot.");
        }
    }

    private static (EventAddress? StartInclusive, EventAddress? EndInclusive) GetFragmentSourceRange(
        IReadOnlyList<RollingSummaryReplayMessage> fragmentEntries,
        RollingSummaryReplaySourceCursor triggerCursor
    ) {
        if (fragmentEntries.Count == 0) {
            throw new InvalidDataException("Rolling summary split produced an empty fragment.");
        }

        bool isAddressed = fragmentEntries[0].SourceStartInclusive.HasValue;
        if (fragmentEntries.Any(entry => entry.SourceStartInclusive.HasValue != isAddressed)) {
            throw new InvalidDataException("Rolling summary fragment cannot mix addressed and unaddressed messages.");
        }

        if (isAddressed != triggerCursor.SourceRawHead.HasValue) {
            throw new InvalidDataException(
                "Rolling summary fragment address state must match the trigger cursor source raw head state."
            );
        }

        return (
            fragmentEntries[0].SourceStartInclusive,
            fragmentEntries[^1].SourceEndInclusive
        );
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal
        );
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
    string? ArtifactId,
    string? ArtifactPath,
    string? AnchorRawEvent,
    string? PreviousArtifact,
    string Status,
    string? ExceptionType,
    string? ExceptionMessage,
    CompletionDescriptor? Invocation,
    IReadOnlyList<string>? Errors
) {
    public static RollingSummaryReplayRecord Create(
        int epochIndex,
        RollingSummaryReplaySourceCursor cursor,
        EventAddress? sourceStartInclusive,
        EventAddress? sourceEndInclusive,
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
        Exception? exception,
        RollingSummaryArtifactLink? artifactLink
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
            SourceStartInclusive: FormatAddress(sourceStartInclusive),
            SourceEndInclusive: FormatAddress(sourceEndInclusive),
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
            ArtifactId: artifactLink?.ArtifactId,
            ArtifactPath: artifactLink?.ArtifactPath,
            AnchorRawEvent: FormatAddress(artifactLink?.AnchorRawEvent),
            PreviousArtifact: artifactLink?.PreviousArtifact,
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
