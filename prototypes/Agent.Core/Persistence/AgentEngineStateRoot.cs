using Atelia.Agent.Core.History;
using Atelia.Completion.Tools;
using Atelia.StateJournal;

namespace Atelia.Agent.Core.Persistence;

/// <summary>
/// 以 <see cref="DurableDict{TKey}"/> 为 graph root 的 AgentEngine 持久化 façade。
/// </summary>
public sealed class AgentEngineStateRoot {
    private readonly DurableDict<string> _root;

    private AgentEngineStateRoot(DurableDict<string> root) {
        _root = root ?? throw new ArgumentNullException(nameof(root));
        ValidateRoot(_root);
    }

    public DurableDict<string> Root => _root;

    public Revision Revision => _root.Revision;

    public static AgentEngineStateRoot Create(Revision revision, string? systemPrompt = null) {
        ArgumentNullException.ThrowIfNull(revision);

        var root = revision.CreateDict<string>();
        root.Upsert(AgentEngineStateCodec.KeyKind, AgentEngineStateCodec.KindValue);
        root.Upsert(AgentEngineStateCodec.KeySchemaVersion, AgentEngineStateCodec.SchemaVersion);

        var stateRoot = new AgentEngineStateRoot(root);
        var defaultState = AgentState.CreateDefault(systemPrompt);
        stateRoot.Save(
            new AgentEngineStateSnapshot(
                AgentState: defaultState.ExportSnapshot(),
                PendingToolResults: Array.Empty<ToolCallExecutionResult>(),
                ResolvedProfile: null,
                LockedCompactionSplitIndex: null,
                PendingCompaction: null,
                ToolSessionExecutionSequence: 0
            )
        );

        return stateRoot;
    }

    public static AgentEngineStateRoot Create(Revision revision, AgentEngine engine) {
        ArgumentNullException.ThrowIfNull(engine);

        var stateRoot = Create(revision, engine.SystemPrompt);
        stateRoot.Save(engine);
        return stateRoot;
    }

    public static AgentEngineStateRoot FromRoot(DurableDict<string> root) => new(root);

    public void Save(AgentEngine engine) {
        ArgumentNullException.ThrowIfNull(engine);
        Save(engine.ExportStateSnapshot());
    }

    public void SaveAndCommit(Repository repo, AgentEngine engine) {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(engine);

        Save(engine);
        repo.Commit(_root).Unwrap();
    }

    public void SaveAndCommit(Repository repo, AgentEngineStateSnapshot snapshot) {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(snapshot);

        Save(snapshot);
        repo.Commit(_root).Unwrap();
    }

    public void Save(AgentEngineStateSnapshot snapshot) {
        ArgumentNullException.ThrowIfNull(snapshot);

        _root.Upsert(AgentEngineStateCodec.KeyKind, AgentEngineStateCodec.KindValue);
        _root.Upsert(AgentEngineStateCodec.KeySchemaVersion, AgentEngineStateCodec.SchemaVersion);
        _root.Upsert(AgentEngineStateCodec.KeySystemPrompt, snapshot.AgentState.SystemPrompt);
        _root.Upsert(AgentEngineStateCodec.KeyLastSerial, snapshot.AgentState.LastSerial);
        _root.Upsert(AgentEngineStateCodec.KeyToolSessionExecutionSequence, snapshot.ToolSessionExecutionSequence);

        var history = Revision.CreateDeque();
        foreach (var entry in snapshot.AgentState.RecentHistory) {
            history.PushBack<DurableObject>(AgentEngineStateCodec.WriteHistoryEntry(Revision, entry));
        }
        _root.Upsert<DurableObject>(AgentEngineStateCodec.KeyHistory, history);

        var pendingNotifications = Revision.CreateDeque();
        foreach (var notification in snapshot.AgentState.PendingNotifications) {
            pendingNotifications.PushBack(notification);
        }
        _root.Upsert<DurableObject>(AgentEngineStateCodec.KeyPendingNotifications, pendingNotifications);

        var pendingToolResults = Revision.CreateDict<string>();
        foreach (var pendingResult in snapshot.PendingToolResults) {
            pendingToolResults.Upsert(
                pendingResult.ToolCallId,
                AgentEngineStateCodec.WritePendingToolResult(Revision, pendingResult)
            );
        }
        _root.Upsert<DurableObject>(AgentEngineStateCodec.KeyPendingToolResults, pendingToolResults);

        var turnRuntime = Revision.CreateDict<string>();
        AgentEngineStateCodec.WriteTurnRuntime(
            turnRuntime,
            snapshot.ResolvedProfile,
            snapshot.LockedCompactionSplitIndex
        );
        _root.Upsert<DurableObject>(AgentEngineStateCodec.KeyTurnRuntime, turnRuntime);

        if (snapshot.PendingCompaction is not null) {
            _root.Upsert<DurableObject>(
                AgentEngineStateCodec.KeyPendingCompaction,
                AgentEngineStateCodec.WriteCompactionCheckpoint(Revision, snapshot.PendingCompaction)
            );
        }
        else {
            _root.Remove(AgentEngineStateCodec.KeyPendingCompaction);
        }
    }

    public AgentEngineStateSnapshot Load() {
        string systemPrompt = _root.Get<string>(AgentEngineStateCodec.KeySystemPrompt, out var prompt) == GetIssue.None
            ? prompt!
            : throw new InvalidDataException("Agent state root is missing systemPrompt.");
        ulong lastSerial = _root.Get<ulong>(AgentEngineStateCodec.KeyLastSerial, out var serial) == GetIssue.None
            ? serial
            : throw new InvalidDataException("Agent state root is missing lastSerial.");
        long toolSessionExecutionSequence = _root.Get<long>(AgentEngineStateCodec.KeyToolSessionExecutionSequence, out var executionSequence) == GetIssue.None
            ? executionSequence
            : 0L;

        var historyContainer = _root.GetOrThrow<DurableDeque>(AgentEngineStateCodec.KeyHistory)
            ?? throw new InvalidDataException("Agent state root is missing history deque.");
        var recentHistory = new List<HistoryEntry>(historyContainer.Count);
        for (int i = 0; i < historyContainer.Count; i++) {
            if (!historyContainer.TryGetAt<DurableDict<string>>(i, out var historyRecord) || historyRecord is null) { throw new InvalidDataException($"History record at index {i} is missing or invalid."); }

            recentHistory.Add(AgentEngineStateCodec.ReadHistoryEntry(historyRecord));
        }

        var notificationsContainer = _root.GetOrThrow<DurableDeque>(AgentEngineStateCodec.KeyPendingNotifications)
            ?? throw new InvalidDataException("Agent state root is missing pendingNotifications deque.");
        var pendingNotifications = new List<string>(notificationsContainer.Count);
        for (int i = 0; i < notificationsContainer.Count; i++) {
            if (!notificationsContainer.TryGetAt<string>(i, out var notification) || notification is null) { throw new InvalidDataException($"Pending notification at index {i} is missing or invalid."); }

            pendingNotifications.Add(notification);
        }

        var pendingToolResultsContainer = _root.GetOrThrow<DurableDict<string>>(AgentEngineStateCodec.KeyPendingToolResults)
            ?? throw new InvalidDataException("Agent state root is missing pendingToolResults map.");
        var pendingToolResults = new List<ToolCallExecutionResult>(pendingToolResultsContainer.Count);
        foreach (var toolCallId in pendingToolResultsContainer.Keys) {
            var resultRecord = pendingToolResultsContainer.GetOrThrow<DurableDict<string>>(toolCallId)
                ?? throw new InvalidDataException($"Pending tool result '{toolCallId}' is missing.");
            pendingToolResults.Add(AgentEngineStateCodec.ReadPendingToolResult(resultRecord));
        }

        var turnRuntime = _root.GetOrThrow<DurableDict<string>>(AgentEngineStateCodec.KeyTurnRuntime)
            ?? throw new InvalidDataException("Agent state root is missing turnRuntime record.");
        var (resolvedProfile, lockedCompactionSplitIndex) = AgentEngineStateCodec.ReadTurnRuntime(turnRuntime);

        CompactionCheckpoint? pendingCompaction = null;
        if (_root.TryGet<DurableDict<string>>(AgentEngineStateCodec.KeyPendingCompaction, out var compactionRecord)
            && compactionRecord is not null) {
            pendingCompaction = AgentEngineStateCodec.ReadCompactionCheckpoint(compactionRecord);
        }

        return new AgentEngineStateSnapshot(
            AgentState: new AgentStateSnapshot(
                SystemPrompt: systemPrompt,
                RecentHistory: recentHistory,
                PendingNotifications: pendingNotifications,
                LastSerial: lastSerial
            ),
            PendingToolResults: pendingToolResults,
            ResolvedProfile: resolvedProfile,
            LockedCompactionSplitIndex: lockedCompactionSplitIndex,
            PendingCompaction: pendingCompaction,
            ToolSessionExecutionSequence: toolSessionExecutionSequence
        );
    }

    private static void ValidateRoot(DurableDict<string> root) {
        if (root.Get<string>(AgentEngineStateCodec.KeyKind, out var kind) != GetIssue.None
            || kind != AgentEngineStateCodec.KindValue) { throw new InvalidDataException("Root is not an agent-engine-state."); }

        if (root.Get<long>(AgentEngineStateCodec.KeySchemaVersion, out var schemaVersion) != GetIssue.None
            || schemaVersion != AgentEngineStateCodec.SchemaVersion) {
            throw new InvalidDataException(
                $"Unsupported agent-engine-state schema version. Expected {AgentEngineStateCodec.SchemaVersion}."
            );
        }
    }
}
