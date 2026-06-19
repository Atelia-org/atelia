using Atelia.Agent.Core.App;
using Atelia.Agent.Core.History;
using Atelia.Agent.Core.Persistence;
using Atelia.Completion.Tools;
using Atelia.StateJournal;

namespace Atelia.Agent.Core;

public partial class AgentEngine {
    internal AgentTurnRuntimeStateSnapshot ExportTurnRuntimeStateSnapshot() {
        return new AgentTurnRuntimeStateSnapshot(
            ResolvedProfile: _turnRuntime.ResolvedProfile is null
                ? null
                : LlmProfileCheckpoint.FromProfile(_turnRuntime.ResolvedProfile),
            LockedCompactionSplitIndex: _turnRuntime.LockedCompactionSplitIndex
        );
    }

    internal AgentEngineRuntimeStateSnapshot ExportRuntimeStateSnapshot() {
        var pendingToolResults = _pendingToolResults.Values
            .OrderBy(static result => result.ToolCallId, StringComparer.Ordinal)
            .Select(AgentState.CloneToolCallExecutionResult)
            .ToArray();

        var turnRuntimeSnapshot = ExportTurnRuntimeStateSnapshot();

        CompactionCheckpoint? pendingCompaction = _compactionRequest.HasValue
            ? new CompactionCheckpoint(
                _compactionRequest.Value.SplitIndex,
                _compactionRequest.Value.SystemPrompt,
                _compactionRequest.Value.SummarizePrompt
            )
            : null;

        return new AgentEngineRuntimeStateSnapshot(
            PendingToolResults: pendingToolResults,
            ResolvedProfile: turnRuntimeSnapshot.ResolvedProfile,
            LockedCompactionSplitIndex: turnRuntimeSnapshot.LockedCompactionSplitIndex,
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
            resolvedProfileResolver: resolvedProfileResolver,
            workspaceSession: workspaceSession
        );

        engine.ApplyRuntimeStateSnapshot(runtimeState);
        return engine;
    }

    internal static AgentEngine CreateFromWorkspaceSession(
        AgentWorkspaceSession workspaceSession,
        LlmProfileRegistry? profileRegistry = null,
        IEnumerable<IApp>? initialApps = null,
        IEnumerable<ITool>? initialTools = null,
        IIdleObservationProvider? idleProvider = null,
        Func<DateTimeOffset>? utcNowProvider = null,
        AutoCompactionOptions? autoCompaction = null
    ) {
        return CreateFromWorkspaceSession(
            workspaceSession,
            profileRegistry is null ? null : checkpoint => profileRegistry.ResolveOrNull(checkpoint),
            initialApps,
            initialTools,
            idleProvider,
            utcNowProvider,
            autoCompaction
        );
    }

    internal static AgentEngine CreateFromWorkspaceSession(
        AgentWorkspaceSession workspaceSession,
        Func<LlmProfileCheckpoint, LlmProfile?>? resolvedProfileResolver,
        IEnumerable<IApp>? initialApps = null,
        IEnumerable<ITool>? initialTools = null,
        IIdleObservationProvider? idleProvider = null,
        Func<DateTimeOffset>? utcNowProvider = null,
        AutoCompactionOptions? autoCompaction = null
    ) {
        ArgumentNullException.ThrowIfNull(workspaceSession);

        return CreateFromPersistedStateCore(
            workspaceSession.RestoreState(),
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

    internal void CommitStableBoundary() {
        _workspaceSession?.Commit();
    }

    private void PersistPendingToolResults() {
        if (_workspaceSession is null) { return; }

        try {
            ApplyPendingToolResultsSnapshot(_workspaceSession.ReplacePendingToolResults(_pendingToolResults.Values
                .OrderBy(static result => result.ToolCallId, StringComparer.Ordinal)
                .Select(AgentState.CloneToolCallExecutionResult)
                .ToArray()));
        }
        catch {
            ApplyPendingToolResultsSnapshot(_workspaceSession.LoadPendingToolResults());
            throw;
        }
    }

    private void UpsertPendingToolResult(ToolCallExecutionResult pendingResult) {
        if (_workspaceSession is null) {
            _pendingToolResults[pendingResult.ToolCallId] = pendingResult;
            return;
        }

        try {
            ApplyPendingToolResultsSnapshot(_workspaceSession.UpsertPendingToolResult(pendingResult));
        }
        catch {
            ApplyPendingToolResultsSnapshot(_workspaceSession.LoadPendingToolResults());
            throw;
        }
    }

    private void ApplyPendingToolResultsSnapshot(IReadOnlyList<ToolCallExecutionResult> pendingResults) {
        ArgumentNullException.ThrowIfNull(pendingResults);

        _pendingToolResults.Clear();
        foreach (var pendingResult in pendingResults) {
            _pendingToolResults[pendingResult.ToolCallId] = AgentState.CloneToolCallExecutionResult(pendingResult);
        }
    }

    private void ApplyRuntimeStateSnapshot(AgentEngineRuntimeStateSnapshot runtimeState) {
        ArgumentNullException.ThrowIfNull(runtimeState);

        ApplyPendingToolResultsSnapshot(runtimeState.PendingToolResults);
        ApplyTurnRuntimeSnapshot(
            new AgentTurnRuntimeStateSnapshot(
                runtimeState.ResolvedProfile,
                runtimeState.LockedCompactionSplitIndex
            )
        );
        _compactionRequest = runtimeState.PendingCompaction is null
            ? null
            : new CompactionRequest(
                runtimeState.PendingCompaction.SplitIndex,
                runtimeState.PendingCompaction.SystemPrompt,
                runtimeState.PendingCompaction.SummarizePrompt
            );

        if (runtimeState.ToolSessionExecutionSequence > 0) {
            EnsureSession().RestoreExecutionSequence(runtimeState.ToolSessionExecutionSequence);
        }
    }

    private void ApplyTurnRuntimeSnapshot(
        AgentTurnRuntimeStateSnapshot turnRuntimeSnapshot,
        LlmProfile? preferredResolvedProfile = null
    ) {
        ArgumentNullException.ThrowIfNull(turnRuntimeSnapshot);

        var resolvedProfile = turnRuntimeSnapshot.ResolvedProfile is null
            ? null
            : ResolveResolvedProfileCheckpoint(turnRuntimeSnapshot.ResolvedProfile, preferredResolvedProfile);

        _turnRuntime.ReplacePersistedTurnState(resolvedProfile, turnRuntimeSnapshot.LockedCompactionSplitIndex);
    }

    private LlmProfile ResolveResolvedProfileCheckpoint(
        LlmProfileCheckpoint checkpoint,
        LlmProfile? preferredResolvedProfile = null
    ) {
        ArgumentNullException.ThrowIfNull(checkpoint);

        if (preferredResolvedProfile is not null && ProfileMatchesCheckpoint(preferredResolvedProfile, checkpoint)) {
            _resolvedProfileRestore.Remember(preferredResolvedProfile);
            EnsureProfileSupportsAgentCoreFullFeatures(preferredResolvedProfile, source: "Resolved profile checkpoint restore");
            return preferredResolvedProfile;
        }

        var resolvedProfile = _resolvedProfileRestore.ResolveOrNull(checkpoint)
            ?? throw new InvalidOperationException(
                "State snapshot contains a resolved LlmProfile checkpoint, but no reusable resolver/restore seam was able to restore it. " +
                $"Missing checkpoint: {checkpoint.ProviderId}/{checkpoint.ApiSpecId}/{checkpoint.ModelId}."
            );

        if (!ProfileMatchesCheckpoint(resolvedProfile, checkpoint)) {
            throw new InvalidOperationException(
                "Resolved profile checkpoint did not round-trip to the same invocation identity and soft-context budget."
            );
        }

        EnsureProfileSupportsAgentCoreFullFeatures(resolvedProfile, source: "Resolved profile checkpoint restore");
        _resolvedProfileRestore.Remember(resolvedProfile);
        return resolvedProfile;
    }

    private static bool ProfileMatchesCheckpoint(LlmProfile profile, LlmProfileCheckpoint checkpoint) {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(checkpoint);

        return Equals(profile.ToCompletionDescriptor(), checkpoint.ToCompletionDescriptor())
            && profile.SoftContextTokenCap == checkpoint.SoftContextTokenCap;
    }

    private void PersistTurnRuntime(LlmProfile? preferredResolvedProfile = null) {
        if (_workspaceSession is null) { return; }

        if (_turnRuntime.ResolvedProfile is not null) {
            _resolvedProfileRestore.Remember(_turnRuntime.ResolvedProfile);
        }

        try {
            var turnRuntimeSnapshot = ExportTurnRuntimeStateSnapshot();
            ApplyTurnRuntimeSnapshot(_workspaceSession.UpdateTurnRuntime(
                turnRuntimeSnapshot.ResolvedProfile,
                turnRuntimeSnapshot.LockedCompactionSplitIndex
            ), preferredResolvedProfile);
        }
        catch {
            ApplyTurnRuntimeSnapshot(_workspaceSession.LoadTurnRuntimeState(), preferredResolvedProfile);
            throw;
        }
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
