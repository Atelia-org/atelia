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
    /// 导出当前引擎的完整可持久化状态快照。
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
        AutoCompactionOptions? autoCompaction
    ) {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(runtimeState);

        var engine = new AgentEngine(
            state: state,
            initialApps: initialApps,
            initialTools: initialTools,
            idleProvider: idleProvider,
            utcNowProvider: utcNowProvider,
            autoCompaction: autoCompaction
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

    internal static AgentEngine CreateFromWorkspaceRoot(
        AgentWorkspaceRoot workspaceRoot,
        LlmProfileRegistry? profileRegistry = null,
        IEnumerable<IApp>? initialApps = null,
        IEnumerable<ITool>? initialTools = null,
        IIdleObservationProvider? idleProvider = null,
        Func<DateTimeOffset>? utcNowProvider = null,
        AutoCompactionOptions? autoCompaction = null
    ) {
        return CreateFromWorkspaceRoot(
            workspaceRoot,
            profileRegistry is null ? null : checkpoint => profileRegistry.ResolveOrNull(checkpoint),
            initialApps,
            initialTools,
            idleProvider,
            utcNowProvider,
            autoCompaction
        );
    }

    internal static AgentEngine CreateFromWorkspaceRoot(
        AgentWorkspaceRoot workspaceRoot,
        Func<LlmProfileCheckpoint, LlmProfile?>? resolvedProfileResolver,
        IEnumerable<IApp>? initialApps = null,
        IEnumerable<ITool>? initialTools = null,
        IIdleObservationProvider? idleProvider = null,
        Func<DateTimeOffset>? utcNowProvider = null,
        AutoCompactionOptions? autoCompaction = null
    ) {
        ArgumentNullException.ThrowIfNull(workspaceRoot);

        var state = AgentState.RestoreFromWorkspaceRoot(workspaceRoot);
        state.AttachWorkspaceRoot(workspaceRoot, syncExistingState: false);

        return CreateFromPersistedStateCore(
            state,
            AgentEngineStateRoot.FromWorkspaceRoot(workspaceRoot).LoadRuntimeState(),
            resolvedProfileResolver,
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

        var engine = CreateFromWorkspaceRoot(
            stateRoot.WorkspaceRoot,
            resolvedProfileResolver,
            initialApps,
            initialTools,
            idleProvider,
            utcNowProvider,
            autoCompaction
        );
        engine.AttachRepositoryPersistence(repo, stateRoot);
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

    private void AttachRepositoryPersistence(Repository repo, AgentEngineStateRoot stateRoot) {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(stateRoot);

        if (_repositoryPersistence is not null) { throw new InvalidOperationException("AgentEngine repository persistence is already bound."); }

        if (!_state.IsAttachedToWorkspaceRoot(stateRoot.WorkspaceRoot)) {
            _state.AttachWorkspaceRoot(stateRoot.WorkspaceRoot);
        }

        _repositoryPersistence = new RepositoryPersistenceBinding(repo, stateRoot);
        _toolsDirty = true;
    }

    internal void DetachPersistenceSession() {
        _state.DetachWorkspaceRoot();
        if (_toolSession is not null) {
            _toolSession.ExecutionSequenceAllocated = null;
        }
        _repositoryPersistence = null;
    }

    internal void PersistStableBoundaryIfAttached() {
        _repositoryPersistence?.CommitRoot();
    }

    private void PersistPendingToolResultsIfAttached() {
        _repositoryPersistence?.SavePendingToolResults(_pendingToolResults.Values
            .OrderBy(static result => result.ToolCallId, StringComparer.Ordinal)
            .Select(AgentState.CloneToolCallExecutionResult)
            .ToArray());
    }

    private void UpsertPendingToolResultIfAttached(ToolCallExecutionResult pendingResult) {
        if (_repositoryPersistence is null) { return; }

        _repositoryPersistence.UpsertPendingToolResult(pendingResult);
    }

    private void PersistTurnRuntimeIfAttached() {
        var resolvedProfile = _turnRuntime.ResolvedProfile is null
            ? null
            : LlmProfileCheckpoint.FromProfile(_turnRuntime.ResolvedProfile);

        _repositoryPersistence?.SaveTurnRuntime(resolvedProfile, _turnRuntime.LockedCompactionSplitIndex);
    }

    private void PersistPendingCompactionIfAttached() {
        var pendingCompaction = _compactionRequest.HasValue
            ? new CompactionCheckpoint(
                _compactionRequest.Value.SplitIndex,
                _compactionRequest.Value.SystemPrompt,
                _compactionRequest.Value.SummarizePrompt
            )
            : null;

        _repositoryPersistence?.SavePendingCompaction(pendingCompaction);
    }

    private void PersistToolSessionExecutionSequenceIfAttached() {
        _repositoryPersistence?.SetToolSessionExecutionSequence(_toolSession?.LastIssuedExecutionSequence ?? 0);
    }

    private void PersistToolSessionExecutionSequenceIfAttached(long executionSequence) {
        _repositoryPersistence?.SetToolSessionExecutionSequence(executionSequence);
    }

    private sealed class RepositoryPersistenceBinding {
        private readonly Repository _repo;
        private readonly AgentEngineStateRoot _stateRoot;

        public RepositoryPersistenceBinding(Repository repo, AgentEngineStateRoot stateRoot) {
            _repo = repo ?? throw new ArgumentNullException(nameof(repo));
            _stateRoot = stateRoot ?? throw new ArgumentNullException(nameof(stateRoot));
        }

        public void CommitRoot() => _stateRoot.Commit(_repo);

        public void SavePendingToolResults(IReadOnlyList<ToolCallExecutionResult> pendingResults)
            => _stateRoot.SavePendingToolResults(pendingResults);

        public void UpsertPendingToolResult(ToolCallExecutionResult pendingResult)
            => _stateRoot.UpsertPendingToolResult(pendingResult);

        public void SaveTurnRuntime(LlmProfileCheckpoint? resolvedProfile, int? lockedCompactionSplitIndex)
            => _stateRoot.SaveTurnRuntime(resolvedProfile, lockedCompactionSplitIndex);

        public void SavePendingCompaction(CompactionCheckpoint? pendingCompaction)
            => _stateRoot.SavePendingCompaction(pendingCompaction);

        public void SetToolSessionExecutionSequence(long executionSequence)
            => _stateRoot.SetToolSessionExecutionSequence(executionSequence);
    }
}
