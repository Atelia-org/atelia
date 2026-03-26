using System.Buffers.Binary;
using Atelia.Diagnostics;
using Atelia.Rbf;
using Atelia.StateJournal.Internal;
using Atelia.StateJournal.Pools;

namespace Atelia.StateJournal;

partial class Revision {
    internal readonly record struct PrimaryCommitArtifacts(
        CommitTicket CommitTicket,
        List<DurableObject> LiveObjects
    );

    internal partial AteliaResult<CommitOutcome> Commit(DurableObject graphRoot, IRbfFile targetFile) {
        ArgumentNullException.ThrowIfNull(graphRoot);
        ArgumentNullException.ThrowIfNull(targetFile);

        string graphRootLabel = graphRoot.LocalId.Value.ToString();
        DebugUtil.Trace(
            "StateJournal.Commit",
            $"Start: graphRoot={graphRootLabel}, head={HeadId.Ticket.Serialize()}",
            eventKind: DebugEventKind.Start
        );

        var primaryCommit = RunPrimaryCommit(graphRoot, targetFile);
        if (primaryCommit.IsFailure) {
            DebugUtil.Warning(
                "StateJournal.Commit",
                $"Primary failed: graphRoot={graphRootLabel}, error={primaryCommit.Error!.ErrorCode}",
                eventKind: DebugEventKind.Failure
            );
            return primaryCommit.Error!;
        }

        var primaryArtifacts = primaryCommit.Value;
        CommitTicket primaryCommitTicket = primaryArtifacts.CommitTicket;
        DebugUtil.Info(
            "StateJournal.Commit",
            $"Primary succeeded: primary={primaryCommitTicket.Ticket.Serialize()}",
            eventKind: DebugEventKind.Success
        );
        var compactionSession = RevisionCompactionSession.TryApply(this, primaryCommitTicket, primaryArtifacts.LiveObjects);
        if (compactionSession is null) { return LogAndReturnOutcome(CommitOutcome.PrimaryOnly(primaryCommitTicket)); }

        var compactionCommit = PersistCompactionFollowup(
            graphRoot,
            primaryArtifacts.LiveObjects,
            compactionSession.SymbolMirrorUpdatePlan,
            targetFile
        );
        if (compactionCommit.IsFailure) { return LogAndReturnOutcome(compactionSession.RollbackAfterFollowupPersistFailure(compactionCommit.Error!)); }

        return LogAndReturnOutcome(CommitOutcome.Compacted(primaryCommitTicket, compactionCommit.Value));
    }

    internal partial AteliaResult<CommitTicket> ExportTo(DurableObject graphRoot, IRbfFile targetFile) {
        ArgumentNullException.ThrowIfNull(graphRoot);
        ArgumentNullException.ThrowIfNull(targetFile);

        var liveObjectsResult = PrepareLiveObjects(graphRoot);
        if (liveObjectsResult.IsFailure) { return liveObjectsResult.Error!; }

        try {
            var persistResult = PersistCrossFileSnapshot(graphRoot, liveObjectsResult.Value!, targetFile);
            // ExportTo 不改变当前 Revision 状态，无论成败都回滚镜像层的未提交变更
            _objectMap.DiscardChanges();
            _symbolTable.DiscardChanges();
            if (persistResult.IsFailure) { return persistResult.Error!; }
            // 不调用 FinalizePrimaryCommit：不 Complete PendingSave、不 Sweep、不更新 _head
            return persistResult.Value.Id;
        }
        catch (Exception ex) when (IsExternalCommitException(ex)) {
            _objectMap.DiscardChanges();
            _symbolTable.DiscardChanges();
            return BuildStateError("ExportTo persistence failed", ex);
        }
    }

    internal partial AteliaResult<CommitOutcome> SaveAs(DurableObject graphRoot, IRbfFile targetFile) {
        ArgumentNullException.ThrowIfNull(graphRoot);
        ArgumentNullException.ThrowIfNull(targetFile);

        var liveObjectsResult = PrepareLiveObjects(graphRoot);
        if (liveObjectsResult.IsFailure) { return liveObjectsResult.Error!; }

        AteliaResult<(List<PendingSave> PendingSaves, CommitTicket Id)> persistResult;
        try {
            persistResult = PersistCrossFileSnapshot(graphRoot, liveObjectsResult.Value!, targetFile);
        }
        catch (Exception ex) when (IsExternalCommitException(ex)) {
            _objectMap.DiscardChanges();
            _symbolTable.DiscardChanges();
            return BuildStateError("SaveAs persistence failed", ex);
        }
        if (persistResult.IsFailure) {
            _objectMap.DiscardChanges();
            _symbolTable.DiscardChanges();
            return persistResult.Error!;
        }

        var (pendingSaves, newCommitTicket) = persistResult.Value;

        // Finalize: Complete all PendingSave (HeadTicket 指向新文件), Sweep GC, 更新 _head
        FinalizePrimaryCommit(graphRoot, pendingSaves, newCommitTicket);
        // 跳过 Compaction（刚全量 rebase，无 delta 碎片）
        return CommitOutcome.PrimaryOnly(newCommitTicket);
    }

    private static AteliaResult<CommitOutcome> LogAndReturnOutcome(CommitOutcome outcome) {
        var msg = $"Completed: head={outcome.HeadCommitTicket.Ticket.Serialize()}, kind={outcome.Completion}";
        if (outcome.CompactionIssue is not null) { msg += $", issue={outcome.CompactionIssue.ErrorCode}"; }
        if (outcome.IsCompacted) { msg += $", primary={outcome.PrimaryCommitTicket.Ticket.Serialize()}"; }
        if (outcome.CompactionIssue is not null) {
            DebugUtil.Warning("StateJournal.Commit", msg, eventKind: DebugEventKind.Failure);
        }
        else {
            DebugUtil.Info("StateJournal.Commit", msg, eventKind: DebugEventKind.Success);
        }
        return outcome;
    }

    /// <summary>
    /// 执行 primary commit。
    /// </summary>
    /// <remarks>
    /// 此方法只应把“可归因于图状态 / 宿主环境 / I/O”的失败折叠为 <see cref="AteliaError"/>。
    /// 若内部持久化协议本身违反不变量，应让异常直接传播，以便 fail-fast 暴露实现 bug。
    /// </remarks>
    private AteliaResult<PrimaryCommitArtifacts> RunPrimaryCommit(DurableObject graphRoot, IRbfFile targetFile) {
        var liveObjectsResult = PrepareLiveObjects(graphRoot);
        if (liveObjectsResult.IsFailure) { return liveObjectsResult.Error!; }

        AteliaResult<(List<PendingSave> PendingSaves, CommitTicket Id)> persistResult;
        try {
            persistResult = PersistPrimarySnapshot(graphRoot, liveObjectsResult.Value!, targetFile);
        }
        catch (Exception ex) when (IsExternalCommitException(ex)) {
            _objectMap.DiscardChanges();
            _symbolTable.DiscardChanges();
            return BuildStateError("Persistence failed", ex);
        }
        if (persistResult.IsFailure) {
            _objectMap.DiscardChanges();
            _symbolTable.DiscardChanges();
            return persistResult.Error!;
        }

        var (pendingSaves, newCommitTicket) = persistResult.Value;

        // Phase 3: Finalize（全部落盘成功，统一应用内存状态变更）
        FinalizePrimaryCommit(graphRoot, pendingSaves, newCommitTicket);
        return new PrimaryCommitArtifacts(newCommitTicket, liveObjectsResult.Value!);
    }

    /// <summary>
    /// durable 化 compaction apply 引入的脏变更。
    /// </summary>
    /// <remarks>
    /// 这里复用 primary commit 产出的 live objects，不再重新 WalkAndMark/Sweep。
    /// 因为 compaction 只重排 slot / LocalId / 子引用与 ObjectMap key，不改变对象可达性。
    /// </remarks>
    private AteliaResult<CommitTicket> PersistCompactionFollowup(
        DurableObject graphRoot,
        IReadOnlyList<DurableObject> liveObjects,
        SymbolMirrorUpdatePlan symbolMirrorUpdatePlan,
        IRbfFile targetFile
    ) {
        AteliaResult<(List<PendingSave> PendingSaves, CommitTicket Id)> persistResult;
        try {
            persistResult = PersistCompactionFollowupSnapshot(graphRoot, liveObjects, symbolMirrorUpdatePlan, targetFile);
        }
        catch (Exception ex) when (IsExternalCommitException(ex)) {
            return BuildStateError("Compaction follow-up persistence failed", ex);
        }
        if (persistResult.IsFailure) { return persistResult.Error!; }

        var (pendingSaves, newCommitTicket) = persistResult.Value;
        FinalizeFollowupPersist(graphRoot, pendingSaves, newCommitTicket);
        return newCommitTicket;
    }

    private static SjStateError BuildStateError(string messagePrefix, Exception ex) {
        return new SjStateError(
            $"{messagePrefix}: {ex.GetType().Name}: {ex.Message}",
            RecoveryHint: "Revision state was not modified. Fix the issue and retry."
        );
    }

    private AteliaResult<(List<PendingSave> PendingSaves, CommitTicket Id)> PersistPrimarySnapshot(
        DurableObject graphRoot,
        IReadOnlyList<DurableObject> liveObjects,
        IRbfFile targetFile
    ) {
        return PersistSnapshotCore(
            graphRoot,
            liveObjects,
            frameSource: FrameSource.PrimaryCommit,
            targetFile,
            symbolMirrorUpdatePlan: SymbolMirrorUpdatePlan.ReachableScan(),
            removeUnreachableObjectMapKeys: true,
            forceAll: false
        );
    }

    private AteliaResult<(List<PendingSave> PendingSaves, CommitTicket Id)> PersistCrossFileSnapshot(
        DurableObject graphRoot,
        IReadOnlyList<DurableObject> liveObjects,
        IRbfFile targetFile
    ) {
        return PersistSnapshotCore(
            graphRoot,
            liveObjects,
            frameSource: FrameSource.CrossFileSnapshot,
            targetFile,
            symbolMirrorUpdatePlan: SymbolMirrorUpdatePlan.ReachableScan(),
            removeUnreachableObjectMapKeys: true,
            forceAll: true
        );
    }

    private AteliaResult<(List<PendingSave> PendingSaves, CommitTicket Id)> PersistCompactionFollowupSnapshot(
        DurableObject graphRoot,
        IReadOnlyList<DurableObject> liveObjects,
        SymbolMirrorUpdatePlan symbolMirrorUpdatePlan,
        IRbfFile targetFile
    ) {
        return PersistSnapshotCore(
            graphRoot,
            liveObjects,
            frameSource: FrameSource.Compaction,
            targetFile,
            symbolMirrorUpdatePlan: symbolMirrorUpdatePlan,
            removeUnreachableObjectMapKeys: false,
            forceAll: false
        );
    }

    private AteliaResult<(List<PendingSave> PendingSaves, CommitTicket Id)> PersistSnapshotCore(
        DurableObject graphRoot,
        IReadOnlyList<DurableObject> liveObjects,
        FrameSource frameSource,
        IRbfFile targetFile,
        SymbolMirrorUpdatePlan symbolMirrorUpdatePlan,
        bool removeUnreachableObjectMapKeys,
        bool forceAll = false
    ) {
        var pendingSaves = new List<PendingSave>();
        var userContext = new DiffWriteContext(FrameUsage.UserPayload, frameSource) {
            // forceAll + targetFile 用于 ExportTo/SaveAs 的 full snapshot：
            // 我们强制当前可达对象全部写成 rebase 帧，但不抹掉对象现有的 HeadTicket。
            // 这样目标文件里的头帧会保留“逻辑祖先”信息；读取时因为头帧本身是 rebase，
            // Load 不会继续跟进这个 parent 去目标文件中追帧。
            ForceRebase = forceAll,
            ForceSave = forceAll,
        };
        foreach (var obj in liveObjects) {
            if (!forceAll && obj.IsTracked && !obj.HasChanges) { continue; }
            var writeResult = VersionChain.Write(obj, targetFile, userContext);
            if (writeResult.IsFailure) { return writeResult.Error!; }
            pendingSaves.Add(writeResult.Value);
            _objectMap.Upsert(obj.LocalId.Value, writeResult.Value.Ticket.Serialize());
        }

        if (removeUnreachableObjectMapKeys) {
            // 从 ObjectMap 中移除不可达对象的 key（利用 Phase 1 的 mark bitmap 判定）
            // 迭代 CommittedKeys（上次 commit 的快照，本轮 Upsert/Remove 不影响）以避免临时集合分配
            foreach (uint key in _objectMap.CommittedKeys) {
                if (!_pool.IsMarkedReachable(new SlotHandle(key))) { _objectMap.Remove(key); }
            }
        }

        var symbolMirrorResult = UpdateSymbolMirror(symbolMirrorUpdatePlan);
        if (symbolMirrorResult.IsFailure) { return symbolMirrorResult.Error!; }

        // 写入 SymbolTable（它的 VersionChain 自动处理增量 diff）
        var stContext = new DiffWriteContext(FrameUsage.UserPayload, frameSource) {
            ForceRebase = forceAll,
            ForceSave = forceAll,
        };
        var stWriteResult = VersionChain.Write(_symbolTable, targetFile, stContext);
        if (stWriteResult.IsFailure) { return stWriteResult.Error!; }
        var stPendingSave = stWriteResult.Value;
        pendingSaves.Add(stPendingSave);
        // SymbolTable 的 ticket 存入 ObjectMap，key = packed slot handle（slot 1）
        _objectMap.Upsert(new SlotHandle(0, 1).Packed, stPendingSave.Ticket.Serialize());

        // ── TailMeta: [0..3] GraphRoot LocalId, [4..7] SymbolTable LocalId ──
        Span<byte> rootMeta = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(rootMeta, graphRoot.LocalId.Value);
        BinaryPrimitives.WriteUInt32LittleEndian(rootMeta[4..], new SlotHandle(0, 1).Packed);

        DiffWriteContext mapContext = new(FrameUsage.ObjectMap, frameSource) {
            // ObjectMap 也保留逻辑祖先 commit 的 ticket。
            // 因此 Open(targetCommit, targetFile) 能读取当前快照，同时 HeadParentId 继续暴露这条跨文件祖先信息。
            ForceRebase = forceAll,
            ForceSave = true,
        };
        var mapWriteResult = VersionChain.Write(_objectMap, targetFile, mapContext, tailMeta: rootMeta);
        if (mapWriteResult.IsFailure) { return mapWriteResult.Error!; }
        pendingSaves.Add(mapWriteResult.Value);

        return (pendingSaves, new CommitTicket(mapWriteResult.Value.Ticket));
    }

    private void FinalizePrimaryCommit(DurableObject graphRoot, List<PendingSave> pendingSaves, CommitTicket newCommitTicket) {
        foreach (var pending in pendingSaves) { pending.Complete(); }

        // Mark bitmap 已在 Phase 1 (WalkAndMark) 中完成，直接 Sweep
        _pool.Sweep<DetachOnSweepCollectHandler>();
        _symbolPool.Sweep();

        _head = new CommitSnapshot(
            newCommitTicket,
            _head?.Id ?? default,
            _objectMap,
            graphRoot
        );
    }

    private void FinalizeFollowupPersist(DurableObject graphRoot, List<PendingSave> pendingSaves, CommitTicket newCommitTicket) {
        foreach (var pending in pendingSaves) { pending.Complete(); }
        _pool.TrimExcessCapacity();

        _head = new CommitSnapshot(
            newCommitTicket,
            _head?.Id ?? default,
            _objectMap,
            graphRoot
        );
    }

    private static SjCompactionPersistError BuildCompactionFollowupPersistFailureError(CommitTicket primaryCommitTicket, AteliaError cause) {
        var details = new Dictionary<string, string> {
            ["PrimaryCommitTicket"] = primaryCommitTicket.Ticket.Serialize().ToString(),
            ["CompactionStage"] = "FollowupPersist",
            ["FollowupErrorCode"] = cause.ErrorCode,
        };

        return new SjCompactionPersistError(
            "Commit primary snapshot succeeded, but compaction follow-up persistence failed.",
            RecoveryHint: "Primary snapshot remains current Head. In-memory compaction changes were rolled back. Fix runtime/I/O issue and retry Commit to persist compaction changes.",
            Details: details,
            Cause: cause
        );
    }

    /// <summary>Compaction 用的引用重写器：查翻译表，命中则返回新 id，否则原样返回。同时覆盖 Object (LocalId) 和 Symbol (SymbolId)。</summary>
    private ref partial struct CompactRewriter(
            Dictionary<uint, LocalId> objectTable,
            Dictionary<uint, SymbolId>? symbolTable) : IChildRefRewriter {
    }

    /// <summary>DFS 遍历中的 visitor：验证引用有效性 + 首访标记去重 + 收集存活 handle + 压入 DFS 栈。
    /// 同时标记 symbol pool 中的可达 SymbolId，一趟 DFS 完成两种 pool 的 mark。</summary>
    private ref partial struct WalkMarkVisitor(
            GcPool<DurableObject> pool,
            StringPool symbolPool,
            List<DurableObject> liveObjects,
            Stack<DurableObject> dfsStack) : IChildRefVisitor {
        public AteliaError? Error { get; private set; }
    }

    private ref partial struct ReferenceValidationVisitor(GcPool<DurableObject> pool, StringPool symbolPool, LocalId parentId) : IChildRefVisitor {
        public AteliaError? Error { get; private set; }
    }

    #region Test Helper
    internal enum CompactionFaultPoint {
        None = 0,
        AfterFirstMoveApplied = 1,
    }

    /// <summary>
    /// compaction apply 结束后的引用一致性校验模式。
    /// </summary>
    internal enum CompactionValidationMode {
        /// <summary>
        /// 热路径模式：只校验 touched objects、moved keys 与必要的 ObjectMap/pool 对齐。
        /// 成本更低，但自检覆盖面小于全量校验。
        /// </summary>
        HotPath = 0,

        /// <summary>
        /// 严格模式：对整个 live object 工作集做全量引用完整性校验。
        /// 成本更高，但最利于 fail-fast 暴露遗漏重写一类内部 bug。
        /// </summary>
        Strict = 1,
    }
    private static readonly AsyncLocal<CompactionFaultInjection?> s_compactionFaultInjection = new();
    private static readonly AsyncLocal<CompactionValidationMode?> s_compactionValidationModeOverride = new();
    private static readonly CompactionValidationMode s_defaultCompactionValidationMode = GetDefaultCompactionValidationMode();

    private static CompactionValidationMode GetCompactionValidationMode() {
        return s_compactionValidationModeOverride.Value ?? s_defaultCompactionValidationMode;
    }

    private static CompactionValidationMode GetDefaultCompactionValidationMode() {
        string? raw = Environment.GetEnvironmentVariable("ATELIA_SJ_COMPACTION_VALIDATE");
        if (!string.IsNullOrWhiteSpace(raw)) {
            if (string.Equals(raw, "STRICT", StringComparison.OrdinalIgnoreCase)) { return CompactionValidationMode.Strict; }
            if (string.Equals(raw, "HOT", StringComparison.OrdinalIgnoreCase)
                || string.Equals(raw, "HOTPATH", StringComparison.OrdinalIgnoreCase)) { return CompactionValidationMode.HotPath; }
        }
#if DEBUG
        // Debug / test 默认更偏向 fail-fast 暴露内部遗漏重写 bug。
        return CompactionValidationMode.Strict;
#else
        // 发布默认优先热路径成本；需要更强自检时可显式切到 STRICT。
        return CompactionValidationMode.HotPath;
#endif
    }

    private static void ThrowIfCompactionFaultInjected(CompactionFaultPoint point) {
        var injection = s_compactionFaultInjection.Value;
        if (injection is null || !injection.Armed || injection.Point != point) { return; }
        injection.Armed = false;
        throw injection.ExceptionFactory();
    }

    private sealed class CompactionFaultInjection(
        CompactionFaultPoint point,
        Func<Exception> exceptionFactory
    ) {
        public CompactionFaultPoint Point { get; } = point;
        public Func<Exception> ExceptionFactory { get; } = exceptionFactory;
        public bool Armed { get; set; } = true;
    }

    private sealed class CompactionFaultScope(CompactionFaultInjection? previous) : IDisposable {
        private bool _disposed;

        public void Dispose() {
            if (_disposed) { return; }
            s_compactionFaultInjection.Value = previous;
            _disposed = true;
        }
    }

    private sealed class CompactionValidationModeScope(CompactionValidationMode? previous) : IDisposable {
        private bool _disposed;

        public void Dispose() {
            if (_disposed) { return; }
            s_compactionValidationModeOverride.Value = previous;
            _disposed = true;
        }
    }

    private static bool IsExternalCommitException(Exception ex) {
        return ex is IOException
            or UnauthorizedAccessException
            or ObjectDisposedException;
    }

    internal static IDisposable InjectCompactionFaultScope(
        CompactionFaultPoint point,
        Func<Exception> exceptionFactory
    ) {
        ArgumentNullException.ThrowIfNull(exceptionFactory);
        if (point == CompactionFaultPoint.None) { throw new ArgumentOutOfRangeException(nameof(point)); }

        var previous = s_compactionFaultInjection.Value;
        s_compactionFaultInjection.Value = new CompactionFaultInjection(point, exceptionFactory);
        return new CompactionFaultScope(previous);
    }

    /// <summary>
    /// 临时覆盖当前 async flow 下的 compaction 校验模式。
    /// 主要供测试、benchmark 与显式诊断场景使用。
    /// </summary>
    internal static IDisposable OverrideCompactionValidationModeScope(CompactionValidationMode mode) {
        var previous = s_compactionValidationModeOverride.Value;
        s_compactionValidationModeOverride.Value = mode;
        return new CompactionValidationModeScope(previous);
    }

    #endregion
}
