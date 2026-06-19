using Atelia.Agent.Core.App;
using Atelia.Agent.Core.History;
using Atelia.Agent.Core.Persistence;
using Atelia.Completion.Tools;

namespace Atelia.Agent.Core;

public partial class AgentEngine {
    internal (LlmProfileCheckpoint? ResolvedProfile, int? LockedCompactionSplitIndex) ExportTurnRuntimeState() {
        return (
            ResolvedProfile: _turnRuntime.ResolvedProfile is null
                ? null
                : LlmProfileCheckpoint.FromProfile(_turnRuntime.ResolvedProfile),
            LockedCompactionSplitIndex: _turnRuntime.LockedCompactionSplitIndex
        );
    }

    private IReadOnlyList<ToolCallExecutionResult> ExportPendingToolResultsSnapshot() {
        var pendingToolResults = _pendingToolResults.Values
            .OrderBy(static result => result.ToolCallId, StringComparer.Ordinal)
            .Select(AgentState.CloneToolCallExecutionResult)
            .ToArray();

        return pendingToolResults;
    }

    private CompactionCheckpoint? ExportPendingCompactionSnapshot() {
        return _compactionRequest.HasValue
            ? new CompactionCheckpoint(
                _compactionRequest.Value.SplitIndex,
                _compactionRequest.Value.SystemPrompt,
                _compactionRequest.Value.SummarizePrompt
            )
            : null;
    }

    /// <summary>
    /// 导出当前引擎的完整状态快照。
    /// 该 public snapshot 主要用于 compatibility、diagnostic 和 import/export；repo-backed live durable host 不是靠它驱动主持久化。
    /// </summary>
    public AgentEngineStateSnapshot ExportStateSnapshot() {
        var turnRuntimeState = ExportTurnRuntimeState();

        return new AgentEngineStateSnapshot(
            AgentState: _state.ExportSnapshot(),
            PendingToolResults: ExportPendingToolResultsSnapshot(),
            ResolvedProfile: turnRuntimeState.ResolvedProfile,
            LockedCompactionSplitIndex: turnRuntimeState.LockedCompactionSplitIndex,
            PendingCompaction: ExportPendingCompactionSnapshot(),
            ToolSessionExecutionSequence: _toolSession?.LastIssuedExecutionSequence ?? 0
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
            profileRegistry,
            initialApps,
            initialTools,
            idleProvider,
            utcNowProvider,
            autoCompaction
        );
    }

    private static AgentEngine CreateFromStateSnapshotCore(
        AgentEngineStateSnapshot snapshot,
        LlmProfileRegistry? profileRegistry,
        IEnumerable<IApp>? initialApps,
        IEnumerable<ITool>? initialTools,
        IIdleObservationProvider? idleProvider,
        Func<DateTimeOffset>? utcNowProvider,
        AutoCompactionOptions? autoCompaction
    ) {
        ArgumentNullException.ThrowIfNull(snapshot);

        return CreateFromPersistedStateCore(
            AgentState.RestoreSnapshot(snapshot.AgentState),
            snapshot.PendingToolResults,
            snapshot.ResolvedProfile,
            snapshot.LockedCompactionSplitIndex,
            snapshot.PendingCompaction,
            snapshot.ToolSessionExecutionSequence,
            profileRegistry,
            initialApps,
            initialTools,
            idleProvider,
            utcNowProvider,
            autoCompaction
        );
    }

    private static AgentEngine CreateFromPersistedStateCore(
        AgentState state,
        LlmProfileRegistry? profileRegistry,
        IEnumerable<IApp>? initialApps,
        IEnumerable<ITool>? initialTools,
        IIdleObservationProvider? idleProvider,
        Func<DateTimeOffset>? utcNowProvider,
        AutoCompactionOptions? autoCompaction,
        AgentWorkspaceSession? workspaceSession = null
    ) {
        ArgumentNullException.ThrowIfNull(state);
        return new AgentEngine(
            state: state,
            initialApps: initialApps,
            initialTools: initialTools,
            idleProvider: idleProvider,
            utcNowProvider: utcNowProvider,
            autoCompaction: autoCompaction,
            profileRegistry: profileRegistry,
            workspaceSession: workspaceSession
        );
    }

    private static AgentEngine CreateFromPersistedStateCore(
        AgentState state,
        IReadOnlyList<ToolCallExecutionResult> pendingToolResults,
        LlmProfileCheckpoint? resolvedProfile,
        int? lockedCompactionSplitIndex,
        CompactionCheckpoint? pendingCompaction,
        long toolSessionExecutionSequence,
        LlmProfileRegistry? profileRegistry,
        IEnumerable<IApp>? initialApps,
        IEnumerable<ITool>? initialTools,
        IIdleObservationProvider? idleProvider,
        Func<DateTimeOffset>? utcNowProvider,
        AutoCompactionOptions? autoCompaction,
        AgentWorkspaceSession? workspaceSession = null
    ) {
        ArgumentNullException.ThrowIfNull(pendingToolResults);

        var engine = CreateFromPersistedStateCore(
            state,
            profileRegistry,
            initialApps,
            initialTools,
            idleProvider,
            utcNowProvider,
            autoCompaction,
            workspaceSession
        );

        engine.ApplyRuntimeState(
            pendingToolResults,
            resolvedProfile,
            lockedCompactionSplitIndex,
            pendingCompaction,
            toolSessionExecutionSequence
        );
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
        ArgumentNullException.ThrowIfNull(workspaceSession);

        var engine = CreateFromPersistedStateCore(
            workspaceSession.RestoreState(),
            profileRegistry,
            initialApps,
            initialTools,
            idleProvider,
            utcNowProvider,
            autoCompaction,
            workspaceSession
        );

        engine.RestoreRuntimeStateFromWorkspaceSession(workspaceSession);
        return engine;
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

    private void ApplyRuntimeState(
        IReadOnlyList<ToolCallExecutionResult> pendingToolResults,
        LlmProfileCheckpoint? resolvedProfile,
        int? lockedCompactionSplitIndex,
        CompactionCheckpoint? pendingCompaction,
        long toolSessionExecutionSequence
    ) {
        ArgumentNullException.ThrowIfNull(pendingToolResults);

        ApplyPendingToolResultsSnapshot(pendingToolResults);
        ApplyTurnRuntimeState(
            resolvedProfile,
            lockedCompactionSplitIndex
        );
        ApplyPendingCompactionSnapshot(pendingCompaction);
        ApplyToolSessionExecutionSequence(toolSessionExecutionSequence);
    }

    private void RestoreRuntimeStateFromWorkspaceSession(AgentWorkspaceSession workspaceSession) {
        ArgumentNullException.ThrowIfNull(workspaceSession);

        ApplyPendingToolResultsSnapshot(workspaceSession.LoadPendingToolResults());
        var (resolvedProfile, lockedCompactionSplitIndex) = workspaceSession.LoadTurnRuntimeState();
        ApplyTurnRuntimeState(resolvedProfile, lockedCompactionSplitIndex);
        ApplyPendingCompactionSnapshot(workspaceSession.LoadPendingCompaction());
        ApplyToolSessionExecutionSequence(workspaceSession.LoadToolSessionExecutionSequence());
    }

    private void ApplyToolSessionExecutionSequence(long toolSessionExecutionSequence) {
        if (toolSessionExecutionSequence > 0) {
            EnsureSession().RestoreExecutionSequence(toolSessionExecutionSequence);
        }
    }

    private void ApplyTurnRuntimeState(
        LlmProfileCheckpoint? resolvedProfileCheckpoint,
        int? lockedCompactionSplitIndex,
        LlmProfile? preferredResolvedProfile = null
    ) {
        var resolvedProfile = resolvedProfileCheckpoint is null
            ? null
            : ResolveResolvedProfileCheckpoint(resolvedProfileCheckpoint, preferredResolvedProfile);

        _turnRuntime.ReplacePersistedTurnState(resolvedProfile, lockedCompactionSplitIndex);
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
                "State snapshot contains a resolved LlmProfile checkpoint, but no compatible registered or remembered profile was available to restore it. " +
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
            var turnRuntimeState = ExportTurnRuntimeState();
            var updatedTurnRuntimeState = _workspaceSession.UpdateTurnRuntime(
                turnRuntimeState.ResolvedProfile,
                turnRuntimeState.LockedCompactionSplitIndex
            );
            ApplyTurnRuntimeState(
                updatedTurnRuntimeState.ResolvedProfile,
                updatedTurnRuntimeState.LockedCompactionSplitIndex,
                preferredResolvedProfile
            );
        }
        catch {
            var reloadedTurnRuntimeState = _workspaceSession.LoadTurnRuntimeState();
            ApplyTurnRuntimeState(
                reloadedTurnRuntimeState.ResolvedProfile,
                reloadedTurnRuntimeState.LockedCompactionSplitIndex,
                preferredResolvedProfile
            );
            throw;
        }
    }

    private void PersistPendingCompaction() {
        if (_workspaceSession is null) { return; }

        var pendingCompaction = _compactionRequest.HasValue
            ? new CompactionCheckpoint(
                _compactionRequest.Value.SplitIndex,
                _compactionRequest.Value.SystemPrompt,
                _compactionRequest.Value.SummarizePrompt
            )
            : null;

        try {
            ApplyPendingCompactionSnapshot(_workspaceSession.UpdatePendingCompaction(pendingCompaction));
        }
        catch {
            ApplyPendingCompactionSnapshot(_workspaceSession.LoadPendingCompaction());
            throw;
        }
    }

    private void ApplyPendingCompactionSnapshot(CompactionCheckpoint? pendingCompaction) {
        _compactionRequest = pendingCompaction is null
            ? null
            : new CompactionRequest(
                pendingCompaction.SplitIndex,
                pendingCompaction.SystemPrompt,
                pendingCompaction.SummarizePrompt
            );
    }
}
