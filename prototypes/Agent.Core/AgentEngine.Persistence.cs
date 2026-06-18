using Atelia.Agent.Core.App;
using Atelia.Agent.Core.History;
using Atelia.Agent.Core.Persistence;
using Atelia.Completion.Tools;
using Atelia.StateJournal;

namespace Atelia.Agent.Core;

public partial class AgentEngine {
    internal AgentEngineRuntimeStateSnapshot ExportRuntimeStateSnapshot() {
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

        return new AgentEngineRuntimeStateSnapshot(
            PendingToolResults: pendingToolResults,
            ResolvedProfile: resolvedProfile,
            LockedCompactionSplitIndex: _turnRuntime.LockedCompactionSplitIndex,
            PendingCompaction: pendingCompaction,
            ToolSessionExecutionSequence: _toolSession?.LastIssuedExecutionSequence ?? 0
        );
    }

    /// <summary>
    /// 导出当前引擎的完整状态快照。
    /// 该 public snapshot 主要用于 compatibility、diagnostic 和 import/export；repo-backed live durable host 不是靠它驱动主持久化。
    /// </summary>
    public AgentEngineStateSnapshot ExportStateSnapshot() {
        var runtimeSnapshot = ExportRuntimeStateSnapshot();

        return new AgentEngineStateSnapshot(
            AgentState: _state.ExportSnapshot(),
            PendingToolResults: runtimeSnapshot.PendingToolResults,
            ResolvedProfile: runtimeSnapshot.ResolvedProfile,
            LockedCompactionSplitIndex: runtimeSnapshot.LockedCompactionSplitIndex,
            PendingCompaction: runtimeSnapshot.PendingCompaction,
            ToolSessionExecutionSequence: runtimeSnapshot.ToolSessionExecutionSequence
        );
    }

    /// <summary>
    /// 从状态快照重建一个新的、未绑定 live durable host 的 <see cref="AgentEngine"/>。
    /// 该入口保留给 compatibility/import-export/public non-live 场景。
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
    /// 从状态快照重建一个新的、未绑定 live durable host 的 <see cref="AgentEngine"/>。
    /// 低层重载保留显式 resolver 逃生口，用于 compatibility/import-export/public non-live 宿主不想引入 <see cref="LlmProfileRegistry"/> 的场景。
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

        return CreateFromPersistedStateCore(
            AgentState.RestoreSnapshot(snapshot.AgentState),
            new AgentEngineRuntimeStateSnapshot(
                PendingToolResults: snapshot.PendingToolResults,
                ResolvedProfile: snapshot.ResolvedProfile,
                LockedCompactionSplitIndex: snapshot.LockedCompactionSplitIndex,
                PendingCompaction: snapshot.PendingCompaction,
                ToolSessionExecutionSequence: snapshot.ToolSessionExecutionSequence
            ),
            resolvedProfileResolver,
            initialApps,
            initialTools,
            idleProvider,
            utcNowProvider,
            autoCompaction
        );
    }

    private static AgentEngine CreateFromPersistedStateCore(
        AgentState state,
        AgentEngineRuntimeStateSnapshot runtimeState,
        Func<LlmProfileCheckpoint, LlmProfile?>? resolvedProfileResolver,
        IEnumerable<IApp>? initialApps,
        IEnumerable<ITool>? initialTools,
        IIdleObservationProvider? idleProvider,
        Func<DateTimeOffset>? utcNowProvider,
        AutoCompactionOptions? autoCompaction,
        AgentWorkspaceSession? workspaceSession = null
    ) {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(runtimeState);

        var engine = new AgentEngine(
            state: state,
            initialApps: initialApps,
            initialTools: initialTools,
            idleProvider: idleProvider,
            utcNowProvider: utcNowProvider,
            autoCompaction: autoCompaction,
            workspaceSession: workspaceSession
        );

        foreach (var pendingToolResult in runtimeState.PendingToolResults) {
            engine._pendingToolResults[pendingToolResult.ToolCallId] = AgentState.CloneToolCallExecutionResult(pendingToolResult);
        }

        if (runtimeState.ResolvedProfile is not null) {
            if (resolvedProfileResolver is null) {
                throw new InvalidOperationException(
                    "State snapshot contains a resolved LlmProfile checkpoint, but no resolver was supplied."
                );
            }

            var resolvedProfile = resolvedProfileResolver(runtimeState.ResolvedProfile)
                ?? throw new InvalidOperationException(
                    $"Resolved profile checkpoint could not be restored: {runtimeState.ResolvedProfile.ProviderId}/{runtimeState.ResolvedProfile.ApiSpecId}/{runtimeState.ResolvedProfile.ModelId}."
                );

            if (!Equals(resolvedProfile.ToCompletionDescriptor(), runtimeState.ResolvedProfile.ToCompletionDescriptor())) {
                throw new InvalidOperationException(
                    "Resolved profile checkpoint did not round-trip to the same invocation identity."
                );
            }

            engine._turnRuntime.RememberResolvedProfile(resolvedProfile);
        }

        if (runtimeState.LockedCompactionSplitIndex.HasValue) {
            engine._turnRuntime.RememberCompactionSplitIndex(runtimeState.LockedCompactionSplitIndex.Value);
        }

        if (runtimeState.PendingCompaction is not null) {
            engine._compactionRequest = new CompactionRequest(
                runtimeState.PendingCompaction.SplitIndex,
                runtimeState.PendingCompaction.SystemPrompt,
                runtimeState.PendingCompaction.SummarizePrompt
            );
        }

        if (runtimeState.ToolSessionExecutionSequence > 0) {
            engine.EnsureSession().RestoreExecutionSequence(runtimeState.ToolSessionExecutionSequence);
        }

        return engine;
    }

    internal static AgentEngine CreateForRepository(
        Repository repo,
        AgentEngineStateRoot stateRoot,
        LlmProfileRegistry? profileRegistry = null,
        IEnumerable<IApp>? initialApps = null,
        IEnumerable<ITool>? initialTools = null,
        IIdleObservationProvider? idleProvider = null,
        Func<DateTimeOffset>? utcNowProvider = null,
        AutoCompactionOptions? autoCompaction = null
    ) {
        return CreateForRepository(
            repo,
            stateRoot,
            profileRegistry is null ? null : checkpoint => profileRegistry.ResolveOrNull(checkpoint),
            initialApps,
            initialTools,
            idleProvider,
            utcNowProvider,
            autoCompaction
        );
    }

    internal static AgentEngine CreateForRepository(
        Repository repo,
        AgentEngineStateRoot stateRoot,
        Func<LlmProfileCheckpoint, LlmProfile?>? resolvedProfileResolver,
        IEnumerable<IApp>? initialApps = null,
        IEnumerable<ITool>? initialTools = null,
        IIdleObservationProvider? idleProvider = null,
        Func<DateTimeOffset>? utcNowProvider = null,
        AutoCompactionOptions? autoCompaction = null
    ) {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(stateRoot);

        var workspaceSession = stateRoot.OpenSession(repo);

        return CreateFromPersistedStateCore(
            AgentState.RestoreFromWorkspaceSession(workspaceSession),
            workspaceSession.LoadRuntimeState(),
            resolvedProfileResolver,
            initialApps,
            initialTools,
            idleProvider,
            utcNowProvider,
            autoCompaction,
            workspaceSession
        );
    }

    /// <summary>
    /// 从一个 <see cref="AgentEngineStateRoot"/> 的 graph root 重建 <see cref="AgentEngine"/>。
    /// public 入口会先 materialize snapshot 再恢复，因此属于 compatibility/import-export/public non-live 路径，而非 internal live workspace host path。
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

        var workspaceRoot = AgentWorkspaceRoot.FromRoot(root);
        return CreateFromStateSnapshot(
            AgentEngineStateRoot.FromWorkspaceRoot(workspaceRoot).Load(),
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
    /// public 入口会先 materialize snapshot 再恢复；低层重载保留显式 resolver 逃生口，用于 compatibility/import-export/public non-live 宿主不想引入 <see cref="LlmProfileRegistry"/> 的场景。
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

        var workspaceRoot = AgentWorkspaceRoot.FromRoot(root);
        return CreateFromStateSnapshot(
            AgentEngineStateRoot.FromWorkspaceRoot(workspaceRoot).Load(),
            resolvedProfileResolver,
            initialApps,
            initialTools,
            idleProvider,
            utcNowProvider,
            autoCompaction
        );
    }

    internal void CloseWorkspaceSession() {
        _state.CloseWorkspaceSession();
        if (_toolSession is not null) {
            _toolSession.ExecutionSequenceAllocated = null;
        }
        _workspaceSession?.Close();
    }

    internal void CommitStableBoundary() {
        _workspaceSession?.Commit();
    }

    private void PersistPendingToolResults() {
        _workspaceSession?.ReplacePendingToolResults(_pendingToolResults.Values
            .OrderBy(static result => result.ToolCallId, StringComparer.Ordinal)
            .Select(AgentState.CloneToolCallExecutionResult)
            .ToArray());
    }

    private void UpsertPendingToolResult(ToolCallExecutionResult pendingResult) {
        if (_workspaceSession is null) { return; }

        _workspaceSession.UpsertPendingToolResult(pendingResult);
    }

    private void PersistTurnRuntime() {
        if (_workspaceSession is null) { return; }

        _workspaceSession.UpdateTurnRuntime(
            _turnRuntime.ResolvedProfile is null
                ? null
                : LlmProfileCheckpoint.FromProfile(_turnRuntime.ResolvedProfile),
            _turnRuntime.LockedCompactionSplitIndex
        );
    }

    private void PersistPendingCompaction() {
        if (_workspaceSession is null) { return; }

        if (_compactionRequest.HasValue) {
            _workspaceSession.SetPendingCompaction(
                new CompactionCheckpoint(
                    _compactionRequest.Value.SplitIndex,
                    _compactionRequest.Value.SystemPrompt,
                    _compactionRequest.Value.SummarizePrompt
                )
            );
            return;
        }

        _workspaceSession.ClearPendingCompaction();
    }

    private void PersistToolSessionExecutionSequence() {
        _workspaceSession?.SetToolSessionExecutionSequence(_toolSession?.LastIssuedExecutionSequence ?? 0);
    }

    private void PersistToolSessionExecutionSequence(long executionSequence) {
        _workspaceSession?.SetToolSessionExecutionSequence(executionSequence);
    }
}
