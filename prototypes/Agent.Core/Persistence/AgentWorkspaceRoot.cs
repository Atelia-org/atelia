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
    private const long SchemaVersion = 2L;

    private readonly DurableDict<string> _root;

    private AgentWorkspaceRoot(DurableDict<string> root) {
        _root = root ?? throw new ArgumentNullException(nameof(root));
        ValidateRoot(_root);
    }

    public DurableDict<string> Root => _root;

    public Revision Revision => _root.Revision;

    public static AgentWorkspaceRoot Create(Revision revision) {
        ArgumentNullException.ThrowIfNull(revision);

        var root = revision.CreateDict<string>();
        StampMetadata(root);
        var workspaceRoot = new AgentWorkspaceRoot(root);
        workspaceRoot.InitializeDefaultShape();
        return workspaceRoot;
    }

    public static AgentWorkspaceRoot FromRoot(DurableDict<string> root) => new(root);

    public void SetSystemPrompt(string systemPrompt) {
        ArgumentNullException.ThrowIfNull(systemPrompt);
        _root.Upsert(KeySystemPrompt, systemPrompt);
    }

    public string GetRequiredSystemPrompt() {
        return _root.Get<string>(KeySystemPrompt, out var prompt) == GetIssue.None
            ? prompt!
            : throw new InvalidDataException("Agent state root is missing systemPrompt.");
    }

    public void SetLastSerial(ulong lastSerial) => _root.Upsert(KeyLastSerial, lastSerial);

    public ulong AllocateNextHistorySerial() {
        var nextSerial = checked(GetRequiredLastSerial() + 1);
        SetLastSerial(nextSerial);
        return nextSerial;
    }

    public ulong GetRequiredLastSerial() {
        return _root.Get<ulong>(KeyLastSerial, out var serial) == GetIssue.None
            ? serial
            : throw new InvalidDataException("Agent state root is missing lastSerial.");
    }

    public void SetToolSessionExecutionSequence(long executionSequence) {
        _root.Upsert(KeyToolSessionExecutionSequence, executionSequence);
    }

    public long GetToolSessionExecutionSequenceOrDefault() {
        return _root.Get<long>(KeyToolSessionExecutionSequence, out var executionSequence) == GetIssue.None
            ? executionSequence
            : 0L;
    }

    public void SaveHistory(IReadOnlyList<HistoryEntry> recentHistory) {
        ArgumentNullException.ThrowIfNull(recentHistory);

        var history = Revision.CreateDeque();
        foreach (var entry in recentHistory) {
            history.PushBack<DurableObject>(AgentEngineStateCodec.WriteHistoryEntry(Revision, entry));
        }

        SetHistory(history);
    }

    public IReadOnlyList<HistoryEntry> LoadHistory() {
        var historyContainer = GetRequiredHistory();
        var recentHistory = new List<HistoryEntry>(historyContainer.Count);
        for (int i = 0; i < historyContainer.Count; i++) {
            if (!historyContainer.TryGetAt<DurableDict<string>>(i, out var historyRecord) || historyRecord is null) {
                throw new InvalidDataException($"History record at index {i} is missing or invalid.");
            }

            recentHistory.Add(AgentEngineStateCodec.ReadHistoryEntry(historyRecord));
        }

        return recentHistory;
    }

    public void SavePendingNotifications(IReadOnlyList<string> notifications) {
        ArgumentNullException.ThrowIfNull(notifications);

        var pendingNotifications = Revision.CreateDeque();
        foreach (var notification in notifications) {
            pendingNotifications.PushBack(notification);
        }

        SetPendingNotifications(pendingNotifications);
    }

    public IReadOnlyList<string> LoadPendingNotifications() {
        var notificationsContainer = GetRequiredPendingNotifications();
        var pendingNotifications = new List<string>(notificationsContainer.Count);
        for (int i = 0; i < notificationsContainer.Count; i++) {
            if (!notificationsContainer.TryGetAt<string>(i, out var notification) || notification is null) {
                throw new InvalidDataException($"Pending notification at index {i} is missing or invalid.");
            }

            pendingNotifications.Add(notification);
        }

        return pendingNotifications;
    }

    public void SavePendingToolResults(IReadOnlyList<ToolCallExecutionResult> pendingResults) {
        ArgumentNullException.ThrowIfNull(pendingResults);

        var pendingToolResults = Revision.CreateDict<string>();
        foreach (var pendingResult in pendingResults) {
            pendingToolResults.Upsert(
                pendingResult.ToolCallId,
                AgentEngineStateCodec.WritePendingToolResult(Revision, pendingResult)
            );
        }

        SetPendingToolResults(pendingToolResults);
    }

    public IReadOnlyList<ToolCallExecutionResult> LoadPendingToolResults() {
        var pendingToolResultsContainer = GetRequiredPendingToolResults();
        var pendingToolResults = new List<ToolCallExecutionResult>(pendingToolResultsContainer.Count);
        foreach (var toolCallId in pendingToolResultsContainer.Keys) {
            var resultRecord = pendingToolResultsContainer.GetOrThrow<DurableDict<string>>(toolCallId)
                ?? throw new InvalidDataException($"Pending tool result '{toolCallId}' is missing.");
            pendingToolResults.Add(AgentEngineStateCodec.ReadPendingToolResult(resultRecord));
        }

        return pendingToolResults;
    }

    public void SaveTurnRuntime(LlmProfileCheckpoint? resolvedProfile, int? lockedCompactionSplitIndex) {
        var turnRuntime = Revision.CreateDict<string>();
        AgentEngineStateCodec.WriteTurnRuntime(turnRuntime, resolvedProfile, lockedCompactionSplitIndex);
        SetTurnRuntime(turnRuntime);
    }

    public (LlmProfileCheckpoint? ResolvedProfile, int? LockedCompactionSplitIndex) LoadTurnRuntime() {
        return AgentEngineStateCodec.ReadTurnRuntime(GetRequiredTurnRuntime());
    }

    public void SavePendingCompaction(CompactionCheckpoint? pendingCompaction) {
        SetPendingCompactionRecord(
            pendingCompaction is null
                ? null
                : AgentEngineStateCodec.WriteCompactionCheckpoint(Revision, pendingCompaction)
        );
    }

    public CompactionCheckpoint? LoadPendingCompaction() {
        return GetPendingCompactionRecordOrNull() is { } pendingCompaction
            ? AgentEngineStateCodec.ReadCompactionCheckpoint(pendingCompaction)
            : null;
    }

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

    private void SetPendingCompactionRecord(DurableDict<string>? pendingCompaction) {
        if (pendingCompaction is null) {
            _root.Remove(KeyPendingCompaction);
            return;
        }

        _root.Upsert<DurableObject>(KeyPendingCompaction, pendingCompaction);
    }

    private DurableDict<string>? GetPendingCompactionRecordOrNull() {
        return _root.TryGet<DurableDict<string>>(KeyPendingCompaction, out var pendingCompaction)
            ? pendingCompaction
            : null;
    }

    public void StampMetadata() => StampMetadata(_root);

    private void InitializeDefaultShape() {
        SetLastSerial(0);
        SetToolSessionExecutionSequence(0);
        SaveHistory(Array.Empty<HistoryEntry>());
        SavePendingNotifications(Array.Empty<string>());
        SavePendingToolResults(Array.Empty<ToolCallExecutionResult>());
        SaveTurnRuntime(null, null);
        SavePendingCompaction(null);
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
}
