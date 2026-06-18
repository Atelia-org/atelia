using Atelia.Agent.Core.History;
using Atelia.Completion.Tools;
using Atelia.StateJournal;

namespace Atelia.Agent.Core.Persistence;

/// <summary>
/// Agent.Core durable workspace 的 graph root façade。
/// </summary>
internal sealed class AgentWorkspaceRoot {
    private const string KeyKind = "kind";
    private const string KeySchemaVersion = "schemaVersion";
    private const string KeySystemPrompt = "systemPrompt";
    private const string KeyLastSerial = "lastSerial";
    private const string KeyHistory = "history";
    private const string KeyPendingNotifications = "pendingNotifications";
    private const string KeyPendingToolResults = "pendingToolResults";
    private const string KeyTurnRuntime = "turnRuntime";
    private const string KeyPendingCompaction = "pendingCompaction";
    private const string KeyToolSessionExecutionSequence = "toolSessionExecutionSequence";

    private const string KindValue = "agent-engine-state";
    private const long SchemaVersion = 3L;

    private readonly DurableDict<string> _root;

    private AgentWorkspaceRoot(DurableDict<string> root) {
        _root = root ?? throw new ArgumentNullException(nameof(root));
        ValidateRoot(_root);
        Meta = new MetaBlock(this);
        History = new HistoryBlock(this);
        RuntimeState = new RuntimeStateBlock(this);
    }

    public DurableDict<string> Root => _root;

    public Revision Revision => _root.Revision;

    public MetaBlock Meta { get; }

    public HistoryBlock History { get; }

    public RuntimeStateBlock RuntimeState { get; }

    public static AgentWorkspaceRoot Create(Revision revision) {
        ArgumentNullException.ThrowIfNull(revision);

        var root = revision.CreateDict<string>();
        StampMetadata(root);
        var workspaceRoot = new AgentWorkspaceRoot(root);
        workspaceRoot.InitializeDefaultShape();
        return workspaceRoot;
    }

    public static AgentWorkspaceRoot FromRoot(DurableDict<string> root) => new(root);

    private void SetHistory(DurableDeque history) {
        ArgumentNullException.ThrowIfNull(history);
        _root.Upsert<DurableObject>(KeyHistory, history);
    }

    private DurableDeque GetRequiredHistory() {
        return _root.GetOrThrow<DurableDeque>(KeyHistory)
               ?? throw new InvalidDataException("Agent state root is missing history deque.");
    }

    private void SetPendingNotifications(DurableDeque notifications) {
        ArgumentNullException.ThrowIfNull(notifications);
        _root.Upsert<DurableObject>(KeyPendingNotifications, notifications);
    }

    private DurableDeque GetRequiredPendingNotifications() {
        return _root.GetOrThrow<DurableDeque>(KeyPendingNotifications)
               ?? throw new InvalidDataException("Agent state root is missing pendingNotifications deque.");
    }

    private void SetPendingToolResults(DurableDict<string> pendingToolResults) {
        ArgumentNullException.ThrowIfNull(pendingToolResults);
        _root.Upsert<DurableObject>(KeyPendingToolResults, pendingToolResults);
    }

    private DurableDict<string> GetRequiredPendingToolResults() {
        return _root.GetOrThrow<DurableDict<string>>(KeyPendingToolResults)
               ?? throw new InvalidDataException("Agent state root is missing pendingToolResults map.");
    }

    private void SetTurnRuntime(DurableDict<string> turnRuntime) {
        ArgumentNullException.ThrowIfNull(turnRuntime);
        _root.Upsert<DurableObject>(KeyTurnRuntime, turnRuntime);
    }

    private DurableDict<string> GetRequiredTurnRuntime() {
        return _root.GetOrThrow<DurableDict<string>>(KeyTurnRuntime)
               ?? throw new InvalidDataException("Agent state root is missing turnRuntime record.");
    }

    private void SetPendingCompactionRecord(DurableDict<string> pendingCompaction) {
        ArgumentNullException.ThrowIfNull(pendingCompaction);
        _root.Upsert<DurableObject>(KeyPendingCompaction, pendingCompaction);
    }

    private DurableDict<string> GetRequiredPendingCompactionRecord() {
        return _root.TryGet<DurableDict<string>>(KeyPendingCompaction, out var pendingCompaction)
               && pendingCompaction is not null
            ? pendingCompaction
            : throw new InvalidDataException("Agent state root is missing pendingCompaction record.");
    }

    private void InitializeDefaultShape() {
        History.SetLastSerial(0);
        RuntimeState.SetToolSessionExecutionSequence(0);
        History.ReplaceRecent(Array.Empty<HistoryEntry>());
        History.ReplacePendingNotifications(Array.Empty<string>());
        RuntimeState.ReplacePendingToolResults(Array.Empty<ToolCallExecutionResult>());
        RuntimeState.ReplaceTurnRuntime(null, null);
        RuntimeState.ReplacePendingCompaction(null);
    }

    private static void StampMetadata(DurableDict<string> root) {
        root.Upsert(KeyKind, KindValue);
        root.Upsert(KeySchemaVersion, SchemaVersion);
    }

    private static void ValidateRoot(DurableDict<string> root) {
        if (root.Get<string>(KeyKind, out var kind) != GetIssue.None || kind != KindValue) {
            throw new InvalidDataException("Root is not an agent-engine-state.");
        }

        if (root.Get<long>(KeySchemaVersion, out var schemaVersion) != GetIssue.None
            || schemaVersion != SchemaVersion) {
            throw new InvalidDataException(
                $"Unsupported agent-engine-state schema version. Expected {SchemaVersion}."
            );
        }
    }

    public sealed class MetaBlock {
        private readonly AgentWorkspaceRoot _workspaceRoot;

        internal MetaBlock(AgentWorkspaceRoot workspaceRoot) {
            _workspaceRoot = workspaceRoot;
        }

        public void Stamp() => StampMetadata(_workspaceRoot._root);

        public void SetSystemPrompt(string systemPrompt) {
            ArgumentNullException.ThrowIfNull(systemPrompt);
            _workspaceRoot._root.Upsert(KeySystemPrompt, systemPrompt);
        }

        public string GetRequiredSystemPrompt() {
            return _workspaceRoot._root.Get<string>(KeySystemPrompt, out var prompt) == GetIssue.None
                ? prompt!
                : throw new InvalidDataException("Agent state root is missing systemPrompt.");
        }

    }

    public sealed class HistoryBlock {
        private readonly AgentWorkspaceRoot _workspaceRoot;

        internal HistoryBlock(AgentWorkspaceRoot workspaceRoot) {
            _workspaceRoot = workspaceRoot;
        }

        public void SetLastSerial(ulong lastSerial) => _workspaceRoot._root.Upsert(KeyLastSerial, lastSerial);

        public ulong AllocateNextSerial() {
            var nextSerial = checked(GetRequiredLastSerial() + 1);
            SetLastSerial(nextSerial);
            return nextSerial;
        }

        public ulong GetRequiredLastSerial() {
            return _workspaceRoot._root.Get<ulong>(KeyLastSerial, out var serial) == GetIssue.None
                ? serial
                : throw new InvalidDataException("Agent state root is missing lastSerial.");
        }

        public void ReplaceRecent(IReadOnlyList<HistoryEntry> recentHistory) {
            ArgumentNullException.ThrowIfNull(recentHistory);

            var history = _workspaceRoot.Revision.CreateDeque();
            foreach (var entry in recentHistory) {
                history.PushBack<DurableObject>(
                    AgentEngineStateCodec.WriteHistoryEntry(_workspaceRoot.Revision, entry)
                );
            }

            _workspaceRoot.SetHistory(history);
        }

        public void AppendRecent(HistoryEntry entry) {
            ArgumentNullException.ThrowIfNull(entry);

            _workspaceRoot.GetRequiredHistory().PushBack<DurableObject>(
                AgentEngineStateCodec.WriteHistoryEntry(_workspaceRoot.Revision, entry)
            );
        }

        public IReadOnlyList<HistoryEntry> LoadRecent() {
            var historyContainer = _workspaceRoot.GetRequiredHistory();
            var recentHistory = new List<HistoryEntry>(historyContainer.Count);
            for (int i = 0; i < historyContainer.Count; i++) {
                if (!historyContainer.TryGetAt<DurableDict<string>>(i, out var historyRecord) || historyRecord is null) {
                    throw new InvalidDataException($"History record at index {i} is missing or invalid.");
                }

                recentHistory.Add(AgentEngineStateCodec.ReadHistoryEntry(historyRecord));
            }

            return recentHistory;
        }

        public void ReplacePendingNotifications(IReadOnlyList<string> notifications) {
            ArgumentNullException.ThrowIfNull(notifications);

            var pendingNotifications = _workspaceRoot.Revision.CreateDeque();
            foreach (var notification in notifications) {
                pendingNotifications.PushBack(notification);
            }

            _workspaceRoot.SetPendingNotifications(pendingNotifications);
        }

        public void AppendPendingNotification(string notification) {
            ArgumentNullException.ThrowIfNull(notification);

            _workspaceRoot.GetRequiredPendingNotifications().PushBack(notification);
        }

        public IReadOnlyList<string> LoadPendingNotifications() {
            var notificationsContainer = _workspaceRoot.GetRequiredPendingNotifications();
            var pendingNotifications = new List<string>(notificationsContainer.Count);
            for (int i = 0; i < notificationsContainer.Count; i++) {
                if (!notificationsContainer.TryGetAt<string>(i, out var notification) || notification is null) {
                    throw new InvalidDataException($"Pending notification at index {i} is missing or invalid.");
                }

                pendingNotifications.Add(notification);
            }

            return pendingNotifications;
        }
    }

    public sealed class RuntimeStateBlock {
        private readonly AgentWorkspaceRoot _workspaceRoot;

        internal RuntimeStateBlock(AgentWorkspaceRoot workspaceRoot) {
            _workspaceRoot = workspaceRoot;
        }

        public void SetToolSessionExecutionSequence(long executionSequence) {
            _workspaceRoot._root.Upsert(KeyToolSessionExecutionSequence, executionSequence);
        }

        public long GetToolSessionExecutionSequenceOrDefault() {
            return _workspaceRoot._root.Get<long>(KeyToolSessionExecutionSequence, out var executionSequence) == GetIssue.None
                ? executionSequence
                : 0L;
        }

        public void ReplacePendingToolResults(IReadOnlyList<ToolCallExecutionResult> pendingResults) {
            ArgumentNullException.ThrowIfNull(pendingResults);

            var pendingToolResults = _workspaceRoot.Revision.CreateDict<string>();
            foreach (var pendingResult in pendingResults) {
                pendingToolResults.Upsert(
                    pendingResult.ToolCallId,
                    AgentEngineStateCodec.WritePendingToolResult(_workspaceRoot.Revision, pendingResult)
                );
            }

            _workspaceRoot.SetPendingToolResults(pendingToolResults);
        }

        public void UpsertPendingToolResult(ToolCallExecutionResult pendingResult) {
            ArgumentNullException.ThrowIfNull(pendingResult);

            _workspaceRoot.GetRequiredPendingToolResults().Upsert(
                pendingResult.ToolCallId,
                AgentEngineStateCodec.WritePendingToolResult(_workspaceRoot.Revision, pendingResult)
            );
        }

        public IReadOnlyList<ToolCallExecutionResult> LoadPendingToolResults() {
            var pendingToolResultsContainer = _workspaceRoot.GetRequiredPendingToolResults();
            var pendingToolResults = new List<ToolCallExecutionResult>(pendingToolResultsContainer.Count);
            foreach (var toolCallId in pendingToolResultsContainer.Keys) {
                var resultRecord = pendingToolResultsContainer.GetOrThrow<DurableDict<string>>(toolCallId)
                    ?? throw new InvalidDataException($"Pending tool result '{toolCallId}' is missing.");
                pendingToolResults.Add(AgentEngineStateCodec.ReadPendingToolResult(resultRecord));
            }

            return pendingToolResults;
        }

        public void ReplaceTurnRuntime(LlmProfileCheckpoint? resolvedProfile, int? lockedCompactionSplitIndex) {
            var turnRuntime = _workspaceRoot.Revision.CreateDict<string>();
            AgentEngineStateCodec.WriteTurnRuntime(turnRuntime, resolvedProfile, lockedCompactionSplitIndex);
            _workspaceRoot.SetTurnRuntime(turnRuntime);
        }

        public void UpdateTurnRuntime(LlmProfileCheckpoint? resolvedProfile, int? lockedCompactionSplitIndex) {
            var turnRuntime = _workspaceRoot.GetRequiredTurnRuntime();
            if (resolvedProfile is null) {
                AgentEngineStateCodec.ClearResolvedProfile(turnRuntime);
            }
            else {
                AgentEngineStateCodec.WriteResolvedProfile(turnRuntime, resolvedProfile);
            }

            if (lockedCompactionSplitIndex.HasValue) {
                AgentEngineStateCodec.WriteLockedCompactionSplitIndex(turnRuntime, lockedCompactionSplitIndex.Value);
            }
            else {
                AgentEngineStateCodec.ClearLockedCompactionSplitIndex(turnRuntime);
            }
        }

        public void SetResolvedProfile(LlmProfileCheckpoint resolvedProfile) {
            ArgumentNullException.ThrowIfNull(resolvedProfile);
            AgentEngineStateCodec.WriteResolvedProfile(_workspaceRoot.GetRequiredTurnRuntime(), resolvedProfile);
        }

        public void ClearResolvedProfile() {
            AgentEngineStateCodec.ClearResolvedProfile(_workspaceRoot.GetRequiredTurnRuntime());
        }

        public void SetLockedCompactionSplitIndex(int lockedCompactionSplitIndex) {
            AgentEngineStateCodec.WriteLockedCompactionSplitIndex(
                _workspaceRoot.GetRequiredTurnRuntime(),
                lockedCompactionSplitIndex
            );
        }

        public void ClearLockedCompactionSplitIndex() {
            AgentEngineStateCodec.ClearLockedCompactionSplitIndex(_workspaceRoot.GetRequiredTurnRuntime());
        }

        public (LlmProfileCheckpoint? ResolvedProfile, int? LockedCompactionSplitIndex) LoadTurnRuntime() {
            return AgentEngineStateCodec.ReadTurnRuntime(_workspaceRoot.GetRequiredTurnRuntime());
        }

        public void ReplacePendingCompaction(CompactionCheckpoint? pendingCompaction) {
            var pendingCompactionRecord = _workspaceRoot.Revision.CreateDict<string>();
            if (pendingCompaction is not null) {
                AgentEngineStateCodec.WriteCompactionCheckpointFields(pendingCompactionRecord, pendingCompaction);
            }

            _workspaceRoot.SetPendingCompactionRecord(pendingCompactionRecord);
        }

        public void SetPendingCompaction(CompactionCheckpoint pendingCompaction) {
            ArgumentNullException.ThrowIfNull(pendingCompaction);
            AgentEngineStateCodec.WriteCompactionCheckpointFields(
                _workspaceRoot.GetRequiredPendingCompactionRecord(),
                pendingCompaction
            );
        }

        public void ClearPendingCompaction() {
            AgentEngineStateCodec.ClearCompactionCheckpointFields(
                _workspaceRoot.GetRequiredPendingCompactionRecord()
            );
        }

        public CompactionCheckpoint? LoadPendingCompaction() {
            return AgentEngineStateCodec.ReadCompactionCheckpointOrNull(
                _workspaceRoot.GetRequiredPendingCompactionRecord()
            );
        }
    }
}
