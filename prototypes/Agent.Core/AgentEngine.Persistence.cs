using Atelia.Agent.Core.App;
using Atelia.Agent.Core.History;
using Atelia.Agent.Core.Persistence;
using Atelia.Completion.Tools;
using Atelia.StateJournal;

namespace Atelia.Agent.Core;

public partial class AgentEngine {
    /// <summary>
    /// 导出当前引擎的完整可持久化状态快照。
    /// </summary>
    public AgentEngineStateSnapshot ExportStateSnapshot() {
        var pendingToolResults = _pendingToolResults.Values
            .OrderBy(static result => result.ToolCallId, StringComparer.Ordinal)
            .Select(AgentState.CloneToolCallExecutionResult)
            .ToArray();

        LlmProfileCheckpoint? resolvedProfile = _turnRuntime.ResolvedProfile is null
            ? null
            : LlmProfileCheckpoint.FromProfile(_turnRuntime.ResolvedProfile);

        CompactionCheckpoint? pendingCompaction = _compactionRequest.HasValue
            ? new CompactionCheckpoint(
                _compactionRequest.Value.SplitIndex,
                _compactionRequest.Value.SystemPrompt,
                _compactionRequest.Value.SummarizePrompt
            )
            : null;

        return new AgentEngineStateSnapshot(
            AgentState: _state.ExportSnapshot(),
            PendingToolResults: pendingToolResults,
            ResolvedProfile: resolvedProfile,
            LockedCompactionSplitIndex: _turnRuntime.LockedCompactionSplitIndex,
            PendingCompaction: pendingCompaction,
            ToolSessionExecutionSequence: _toolSession?.LastIssuedExecutionSequence ?? 0
        );
    }

    /// <summary>
    /// 从持久化快照重建一个新的 <see cref="AgentEngine"/>。
    /// </summary>
    /// <param name="snapshot">完整状态快照。</param>
    public static AgentEngine CreateFromStateSnapshot(
        AgentEngineStateSnapshot snapshot,
        LlmProfileRegistry? profileRegistry = null,
        IEnumerable<IApp>? initialApps = null,
        IEnumerable<ITool>? initialTools = null,
        IIdleObservationProvider? idleProvider = null,
        Func<DateTimeOffset>? utcNowProvider = null,
        AutoCompactionOptions? autoCompaction = null
    ) {
        return CreateFromStateSnapshotCore(
            snapshot,
            profileRegistry is null ? null : checkpoint => profileRegistry.ResolveOrNull(checkpoint),
            initialApps,
            initialTools,
            idleProvider,
            utcNowProvider,
            autoCompaction
        );
    }

    /// <summary>
    /// 从持久化快照重建一个新的 <see cref="AgentEngine"/>。
    /// 低层重载保留显式 resolver 逃生口，用于不想引入 <see cref="LlmProfileRegistry"/> 的宿主。
    /// </summary>
    public static AgentEngine CreateFromStateSnapshot(
        AgentEngineStateSnapshot snapshot,
        Func<LlmProfileCheckpoint, LlmProfile?>? resolvedProfileResolver,
        IEnumerable<IApp>? initialApps = null,
        IEnumerable<ITool>? initialTools = null,
        IIdleObservationProvider? idleProvider = null,
        Func<DateTimeOffset>? utcNowProvider = null,
        AutoCompactionOptions? autoCompaction = null
    ) {
        return CreateFromStateSnapshotCore(
            snapshot,
            resolvedProfileResolver,
            initialApps,
            initialTools,
            idleProvider,
            utcNowProvider,
            autoCompaction
        );
    }

    private static AgentEngine CreateFromStateSnapshotCore(
        AgentEngineStateSnapshot snapshot,
        Func<LlmProfileCheckpoint, LlmProfile?>? resolvedProfileResolver,
        IEnumerable<IApp>? initialApps,
        IEnumerable<ITool>? initialTools,
        IIdleObservationProvider? idleProvider,
        Func<DateTimeOffset>? utcNowProvider,
        AutoCompactionOptions? autoCompaction
    ) {
        ArgumentNullException.ThrowIfNull(snapshot);

        var state = AgentState.RestoreSnapshot(snapshot.AgentState);
        var engine = new AgentEngine(
            state: state,
            initialApps: initialApps,
            initialTools: initialTools,
            idleProvider: idleProvider,
            utcNowProvider: utcNowProvider,
            autoCompaction: autoCompaction
        );

        foreach (var pendingToolResult in snapshot.PendingToolResults) {
            engine._pendingToolResults[pendingToolResult.ToolCallId] = AgentState.CloneToolCallExecutionResult(pendingToolResult);
        }

        if (snapshot.ResolvedProfile is not null) {
            if (resolvedProfileResolver is null) {
                throw new InvalidOperationException(
                    "State snapshot contains a resolved LlmProfile checkpoint, but no resolver was supplied."
                );
            }

            var resolvedProfile = resolvedProfileResolver(snapshot.ResolvedProfile)
                ?? throw new InvalidOperationException(
                    $"Resolved profile checkpoint could not be restored: {snapshot.ResolvedProfile.ProviderId}/{snapshot.ResolvedProfile.ApiSpecId}/{snapshot.ResolvedProfile.ModelId}."
                );

            if (!Equals(resolvedProfile.ToCompletionDescriptor(), snapshot.ResolvedProfile.ToCompletionDescriptor())) {
                throw new InvalidOperationException(
                    "Resolved profile checkpoint did not round-trip to the same invocation identity."
                );
            }

            engine._turnRuntime.RememberResolvedProfile(resolvedProfile);
        }

        if (snapshot.LockedCompactionSplitIndex.HasValue) {
            engine._turnRuntime.RememberCompactionSplitIndex(snapshot.LockedCompactionSplitIndex.Value);
        }

        if (snapshot.PendingCompaction is not null) {
            engine._compactionRequest = new CompactionRequest(
                snapshot.PendingCompaction.SplitIndex,
                snapshot.PendingCompaction.SystemPrompt,
                snapshot.PendingCompaction.SummarizePrompt
            );
        }

        if (snapshot.ToolSessionExecutionSequence > 0) {
            engine.EnsureSession().RestoreExecutionSequence(snapshot.ToolSessionExecutionSequence);
        }

        return engine;
    }

    /// <summary>
    /// 从一个 <see cref="AgentEngineStateRoot"/> 的 graph root 重建 <see cref="AgentEngine"/>。
    /// </summary>
    public static AgentEngine CreateFromRoot(
        DurableDict<string> root,
        LlmProfileRegistry? profileRegistry = null,
        IEnumerable<IApp>? initialApps = null,
        IEnumerable<ITool>? initialTools = null,
        IIdleObservationProvider? idleProvider = null,
        Func<DateTimeOffset>? utcNowProvider = null,
        AutoCompactionOptions? autoCompaction = null
    ) {
        ArgumentNullException.ThrowIfNull(root);

        return CreateFromStateSnapshot(
            AgentEngineStateRoot.FromRoot(root).Load(),
            profileRegistry,
            initialApps,
            initialTools,
            idleProvider,
            utcNowProvider,
            autoCompaction
        );
    }

    /// <summary>
    /// 从一个 <see cref="AgentEngineStateRoot"/> 的 graph root 重建 <see cref="AgentEngine"/>。
    /// 低层重载保留显式 resolver 逃生口，用于不想引入 <see cref="LlmProfileRegistry"/> 的宿主。
    /// </summary>
    public static AgentEngine CreateFromRoot(
        DurableDict<string> root,
        Func<LlmProfileCheckpoint, LlmProfile?>? resolvedProfileResolver,
        IEnumerable<IApp>? initialApps = null,
        IEnumerable<ITool>? initialTools = null,
        IIdleObservationProvider? idleProvider = null,
        Func<DateTimeOffset>? utcNowProvider = null,
        AutoCompactionOptions? autoCompaction = null
    ) {
        ArgumentNullException.ThrowIfNull(root);

        return CreateFromStateSnapshot(
            AgentEngineStateRoot.FromRoot(root).Load(),
            resolvedProfileResolver,
            initialApps,
            initialTools,
            idleProvider,
            utcNowProvider,
            autoCompaction
        );
    }

    internal void AttachPersistenceSession(Repository repo, AgentEngineStateRoot stateRoot) {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(stateRoot);

        if (_attachedPersistence is not null) { throw new InvalidOperationException("AgentEngine persistence session is already attached."); }

        _attachedPersistence = new AttachedPersistenceSession(repo, stateRoot);
    }

    internal void DetachPersistenceSession() {
        _attachedPersistence = null;
    }

    internal void PersistStableBoundaryIfAttached() {
        _attachedPersistence?.SaveAndCommit(this);
    }

    private sealed class AttachedPersistenceSession {
        private readonly Repository _repo;
        private readonly AgentEngineStateRoot _stateRoot;

        public AttachedPersistenceSession(Repository repo, AgentEngineStateRoot stateRoot) {
            _repo = repo ?? throw new ArgumentNullException(nameof(repo));
            _stateRoot = stateRoot ?? throw new ArgumentNullException(nameof(stateRoot));
        }

        public void SaveAndCommit(AgentEngine engine) {
            ArgumentNullException.ThrowIfNull(engine);
            _stateRoot.SaveAndCommit(_repo, engine);
        }
    }
}
