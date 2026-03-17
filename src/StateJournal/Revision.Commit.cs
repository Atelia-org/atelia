using System.Buffers.Binary;
using Atelia.Diagnostics;
using Atelia.Rbf;
using Atelia.StateJournal.Internal;
using Atelia.StateJournal.Pools;

namespace Atelia.StateJournal;

partial class Revision {
    internal readonly record struct PrimaryCommitArtifacts(
        CommitId CommitId,
        List<DurableObject> LiveObjects
    );

    internal partial AteliaResult<CommitOutcome> Commit(DurableObject graphRoot) {
        ArgumentNullException.ThrowIfNull(graphRoot);

        string graphRootLabel = graphRoot.LocalId.Value.ToString();
        DebugUtil.Trace(
            "StateJournal.Commit",
            $"Start: graphRoot={graphRootLabel}, head={HeadId.Ticket.Serialize()}",
            eventKind: DebugEventKind.Start
        );

        var primaryCommit = RunPrimaryCommit(graphRoot);
        if (primaryCommit.IsFailure) {
            DebugUtil.Warning(
                "StateJournal.Commit",
                $"Primary failed: graphRoot={graphRootLabel}, error={primaryCommit.Error!.ErrorCode}",
                eventKind: DebugEventKind.Failure
            );
            return primaryCommit.Error!;
        }

        var primaryArtifacts = primaryCommit.Value;
        CommitId primaryCommitId = primaryArtifacts.CommitId;
        DebugUtil.Info(
            "StateJournal.Commit",
            $"Primary succeeded: primary={primaryCommitId.Ticket.Serialize()}",
            eventKind: DebugEventKind.Success
        );
        var compactionSession = RevisionCompactionSession.TryApply(this, primaryCommitId, primaryArtifacts.LiveObjects);
        if (compactionSession is null) { return LogAndReturnOutcome(CommitOutcome.PrimaryOnly(primaryCommitId)); }

        var compactionCommit = PersistCompactionFollowup(graphRoot, primaryArtifacts.LiveObjects);
        if (compactionCommit.IsFailure) { return LogAndReturnOutcome(compactionSession.RollbackAfterFollowupPersistFailure(compactionCommit.Error!)); }

        return LogAndReturnOutcome(CommitOutcome.Compacted(primaryCommitId, compactionCommit.Value));
    }

    internal partial AteliaResult<CommitId> ExportTo(DurableObject graphRoot, IRbfFile targetFile) {
        ArgumentNullException.ThrowIfNull(graphRoot);
        ArgumentNullException.ThrowIfNull(targetFile);

        var liveObjectsResult = PrepareLiveObjects(graphRoot);
        if (liveObjectsResult.IsFailure) { return liveObjectsResult.Error!; }

        try {
            var persistResult = PersistCurrentSnapshot(
                graphRoot, liveObjectsResult.Value!,
                removeUnreachableObjectMapKeys: true, FrameSource.CrossFileSnapshot,
                targetFile, forceAll: true
            );
            // ExportTo 不改变当前 Revision 状态，无论成败都回滚 _objectMap 的未提交变更
            _objectMap.DiscardChanges();
            if (persistResult.IsFailure) { return persistResult.Error!; }
            // 不调用 FinalizePrimaryCommit：不 Complete PendingSave、不 Sweep、不更新 _head
            return persistResult.Value.Id;
        }
        catch (Exception ex) when (IsExternalCommitException(ex)) {
            _objectMap.DiscardChanges();
            return BuildStateError("ExportTo persistence failed", ex);
        }
    }

    internal partial AteliaResult<CommitOutcome> SaveAs(DurableObject graphRoot, IRbfFile targetFile) {
        ArgumentNullException.ThrowIfNull(graphRoot);
        ArgumentNullException.ThrowIfNull(targetFile);

        var liveObjectsResult = PrepareLiveObjects(graphRoot);
        if (liveObjectsResult.IsFailure) { return liveObjectsResult.Error!; }

        AteliaResult<(List<PendingSave> PendingSaves, CommitId Id)> persistResult;
        try {
            persistResult = PersistCurrentSnapshot(
                graphRoot, liveObjectsResult.Value!,
                removeUnreachableObjectMapKeys: true, FrameSource.CrossFileSnapshot,
                targetFile, forceAll: true
            );
        }
        catch (Exception ex) when (IsExternalCommitException(ex)) {
            _objectMap.DiscardChanges();
            return BuildStateError("SaveAs persistence failed", ex);
        }
        if (persistResult.IsFailure) {
            _objectMap.DiscardChanges();
            return persistResult.Error!;
        }

        var (pendingSaves, newCommitId) = persistResult.Value;

        // Finalize: Complete all PendingSave (HeadTicket 指向新文件), Sweep GC, 更新 _head
        FinalizePrimaryCommit(graphRoot, pendingSaves, newCommitId);
        // 切换到新文件
        _file = targetFile;
        // 跳过 Compaction（刚全量 rebase，无 delta 碎片）
        return CommitOutcome.PrimaryOnly(newCommitId);
    }

    private static AteliaResult<CommitOutcome> LogAndReturnOutcome(CommitOutcome outcome) {
        var msg = $"Completed: head={outcome.HeadCommitId.Ticket.Serialize()}, kind={outcome.Completion}";
        if (outcome.CompactionIssue is not null) { msg += $", issue={outcome.CompactionIssue.ErrorCode}"; }
        if (outcome.IsCompacted) { msg += $", primary={outcome.PrimaryCommitId.Ticket.Serialize()}"; }
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
    private AteliaResult<PrimaryCommitArtifacts> RunPrimaryCommit(DurableObject graphRoot) {
        var liveObjectsResult = PrepareLiveObjects(graphRoot);
        if (liveObjectsResult.IsFailure) { return liveObjectsResult.Error!; }

        AteliaResult<(List<PendingSave> PendingSaves, CommitId Id)> persistResult;
        try {
            persistResult = PersistCurrentSnapshot(
                graphRoot, liveObjectsResult.Value!,
                removeUnreachableObjectMapKeys: true, FrameSource.PrimaryCommit
            );
        }
        catch (Exception ex) when (IsExternalCommitException(ex)) {
            _objectMap.DiscardChanges();
            return BuildStateError("Persistence failed", ex);
        }
        if (persistResult.IsFailure) {
            _objectMap.DiscardChanges();
            return persistResult.Error!;
        }

        var (pendingSaves, newCommitId) = persistResult.Value;

        // Phase 3: Finalize（全部落盘成功，统一应用内存状态变更）
        FinalizePrimaryCommit(graphRoot, pendingSaves, newCommitId);
        return new PrimaryCommitArtifacts(newCommitId, liveObjectsResult.Value!);
    }

    /// <summary>
    /// durable 化 compaction apply 引入的脏变更。
    /// </summary>
    /// <remarks>
    /// 这里复用 primary commit 产出的 live objects，不再重新 WalkAndMark/Sweep。
    /// 因为 compaction 只重排 slot / LocalId / 子引用与 ObjectMap key，不改变对象可达性。
    /// </remarks>
    private AteliaResult<CommitId> PersistCompactionFollowup(DurableObject graphRoot, IReadOnlyList<DurableObject> liveObjects) {
        AteliaResult<(List<PendingSave> PendingSaves, CommitId Id)> persistResult;
        try {
            persistResult = PersistCurrentSnapshot(
                graphRoot, liveObjects,
                removeUnreachableObjectMapKeys: false, FrameSource.Compaction
            );
        }
        catch (Exception ex) when (IsExternalCommitException(ex)) {
            return BuildStateError("Compaction follow-up persistence failed", ex);
        }
        if (persistResult.IsFailure) { return persistResult.Error!; }

        var (pendingSaves, newCommitId) = persistResult.Value;
        FinalizeFollowupPersist(graphRoot, pendingSaves, newCommitId);
        return newCommitId;
    }

    /// <summary>
    /// 从 GraphRoot 开始 DFS 遍历，同时执行 GcPool 的 BeginMark + TryMarkReachable。
    /// 遍历结束后 mark bitmap 即为最终可达集，后续 Sweep 无需再标记。
    /// 若发现悬空引用则返回失败（mark bitmap 留脏，下次 Commit 的 BeginMark 会清零，无副作用）。
    /// </summary>
    private AteliaResult<List<DurableObject>> WalkAndMark(DurableObject graphRoot) {
        _pool.BeginMark();
        _pool.MarkReachable(new SlotHandle(0, 0)); // slot 0 = ObjectMap，始终可达

        var liveObjects = new List<DurableObject>();
        var dfsStack = new Stack<DurableObject>();

        // 标记并入队根节点
        _pool.TryMarkReachable(graphRoot.LocalId.ToSlotHandle());
        liveObjects.Add(graphRoot);
        dfsStack.Push(graphRoot);

        while (dfsStack.Count > 0) {
            var current = dfsStack.Pop();
            var visitor = new WalkMarkVisitor(_pool, liveObjects, dfsStack);
            current.AcceptChildRefVisitor(ref visitor);
            if (visitor.Error is not null) { return visitor.Error; }
        }

        return liveObjects;
    }

    private AteliaResult<List<DurableObject>> PrepareLiveObjects(DurableObject graphRoot) {
        try {
            EnsureCanReference(graphRoot);
        }
        catch (InvalidOperationException ex) {
            return BuildStateError("EnsureCanReference failed", ex);
        }

        return WalkAndMark(graphRoot);
    }

    private static SjStateError BuildStateError(string messagePrefix, Exception ex) {
        return new SjStateError(
            $"{messagePrefix}: {ex.GetType().Name}: {ex.Message}",
            RecoveryHint: "Revision state was not modified. Fix the issue and retry."
        );
    }

    private AteliaResult<(List<PendingSave> PendingSaves, CommitId Id)> PersistCurrentSnapshot(
        DurableObject graphRoot,
        IReadOnlyList<DurableObject> liveObjects,
        bool removeUnreachableObjectMapKeys,
        FrameSource frameSource,
        IRbfFile? targetFile = null,
        bool forceAll = false
    ) {
        var file = targetFile ?? _file;
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
            var writeResult = VersionChain.Write(obj, file, userContext);
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

        Span<byte> rootMeta = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(rootMeta, graphRoot.LocalId.Value);
        DiffWriteContext mapContext = new(FrameUsage.ObjectMap, frameSource) {
            // ObjectMap 也保留逻辑祖先 commit 的 ticket。
            // 因此 Open(targetCommit, targetFile) 能读取当前快照，同时 HeadParentId 继续暴露这条跨文件祖先信息。
            ForceRebase = forceAll,
            ForceSave = true,
        };
        var mapWriteResult = VersionChain.Write(_objectMap, file, mapContext, tailMeta: rootMeta);
        if (mapWriteResult.IsFailure) { return mapWriteResult.Error!; }
        pendingSaves.Add(mapWriteResult.Value);

        return (pendingSaves, new CommitId(mapWriteResult.Value.Ticket));
    }

    private void FinalizePrimaryCommit(DurableObject graphRoot, List<PendingSave> pendingSaves, CommitId newCommitId) {
        foreach (var pending in pendingSaves) { pending.Complete(); }

        // Mark bitmap 已在 Phase 1 (WalkAndMark) 中完成，直接 Sweep
        _pool.Sweep<DetachOnSweepCollectHandler>();

        _head = new CommitSnapshot(
            newCommitId,
            _head?.Id ?? default,
            _objectMap,
            graphRoot
        );
    }

    private void FinalizeFollowupPersist(DurableObject graphRoot, List<PendingSave> pendingSaves, CommitId newCommitId) {
        foreach (var pending in pendingSaves) { pending.Complete(); }
        _pool.TrimExcessCapacity();

        _head = new CommitSnapshot(
            newCommitId,
            _head?.Id ?? default,
            _objectMap,
            graphRoot
        );
    }

    /// <summary>
    /// 测试专用入口：从 move records 推断 touched objects 后调用核心回滚。
    /// </summary>
    internal void RollbackCompactionChanges(GcPool<DurableObject>.CompactionJournal undoToken) {
        var touchedObjects = new HashSet<DurableObject>();
        foreach (var record in undoToken.Records) {
            if (_pool.TryGetValue(record.NewHandle, out var movedObj)) {
                touchedObjects.Add(movedObj);
            }
            else if (_pool.TryGetValue(record.OldHandle, out var originalObj)) {
                touchedObjects.Add(originalObj);
            }
        }
        RollbackCompactionChanges(undoToken, touchedObjects);
    }

    /// <summary>
    /// 核心回滚逻辑：恢复 pool slot 布局 → 恢复被移动对象的 LocalId → 丢弃 ObjectMap 与 touched 对象的工作态变更。
    /// </summary>
    /// <remarks>
    /// 目标语义下，这里不负责“吞掉”内部不变量破坏。
    /// 若 pool 恢复后的对象布局与 move records 不一致，应直接抛异常 fail-fast。
    /// </remarks>
    private void RollbackCompactionChanges(
        GcPool<DurableObject>.CompactionJournal undoToken,
        HashSet<DurableObject> touchedObjects
    ) {
        // 顺序关键：先恢复 pool slot 布局，再恢复 LocalId，最后丢弃工作态变更
        _pool.RollbackCompaction(undoToken);
        RestoreMovedObjectLocalIds(undoToken.Records);
        _objectMap.DiscardChanges();
        foreach (var obj in touchedObjects) {
            obj.DiscardChanges();
        }
    }

    /// <summary>
    /// pool 恢复后，对象已回到原始 slot，但其 LocalId 仍指向 compaction 后的新位置。
    /// 此方法将每个被移动对象的 LocalId Rebind 回 compaction 前的原始 handle。
    /// </summary>
    private void RestoreMovedObjectLocalIds(IReadOnlyList<SlotPool<DurableObject>.MoveRecord> records) {
        foreach (var record in records) {
            var oldHandle = record.OldHandle;
            if (!_pool.TryGetValue(oldHandle, out var obj)) {
                throw new InvalidOperationException(
                    $"Compaction rollback restored slot {oldHandle}, but the moved object is missing at its original handle."
                );
            }
            obj.Rebind(LocalId.FromSlotHandle(oldHandle));
        }
    }

    private static SjCompactionPersistError BuildCompactionFollowupPersistFailureError(CommitId primaryCommitId, AteliaError cause) {
        var details = new Dictionary<string, string> {
            ["PrimaryCommitTicket"] = primaryCommitId.Ticket.Serialize().ToString(),
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

    private bool ShouldCompact() {
        int liveCount = _pool.Count;
        int capacity = _pool.Capacity;
        if (liveCount < CompactionMinThreshold || capacity == 0) { return false; }

        int holeCount = capacity - liveCount;
        return holeCount * 100 > capacity * CompactionTriggerPercent;
    }

    private int GetCompactionMaxMoves() {
        int liveCount = _pool.Count;
        return Math.Max(1, (int)((long)liveCount * CompactionMovePercent / 100));
    }

    // ───── Compaction 参数 ─────

    /// <summary>存活对象数低于此值时不触发压缩。</summary>
    private const int CompactionMinThreshold = 64;

    /// <summary>碎片率（%）超过此值才触发压缩。</summary>
    private const int CompactionTriggerPercent = 25;

    /// <summary>每次压缩最多移动的存活对象比例（%）。</summary>
    private const int CompactionMovePercent = 5;

    /// <summary>Compaction 用的引用重写器：查翻译表，命中则返回新 LocalId，否则原样返回。</summary>
    private ref struct CompactRewriter(Dictionary<uint, LocalId> table) : IChildRefRewriter {
        public LocalId Rewrite(LocalId oldId) {
            return table.TryGetValue(oldId.Value, out var newId) ? newId : oldId;
        }
    }

    /// <summary>
    /// 根据当前 compaction 校验模式，在 apply 完成后做一致性校验。
    /// </summary>
    private AteliaResult<bool> ValidateCompactionApply(
        IReadOnlyList<DurableObject> liveObjects,
        IReadOnlyCollection<DurableObject> touchedObjects,
        IReadOnlyDictionary<uint, LocalId> translationTable
    ) {
        return GetCompactionValidationMode() switch {
            CompactionValidationMode.Strict => ValidateAllReferences(liveObjects),
            CompactionValidationMode.HotPath => ValidateCompactionHotPath(touchedObjects, translationTable, liveObjects.Count),
            _ => throw new InvalidOperationException("Unknown compaction validation mode.")
        };
    }

    /// <summary>
    /// 校验给定 live objects 与当前 ObjectMap / pool / 子引用的一致性。
    /// 主要用于 compaction apply 后的工作集级验证，避免再次从 ObjectMap 反查对象。
    /// </summary>
    private AteliaResult<bool> ValidateAllReferences(IReadOnlyList<DurableObject> liveObjects) {
        if (_objectMap.Count != liveObjects.Count) {
            return new SjCorruptionError(
                $"ObjectMap count {_objectMap.Count} does not match live object count {liveObjects.Count}.",
                RecoveryHint: "Compaction produced inconsistent ObjectMap/live-object state."
            );
        }

        foreach (var parentObj in liveObjects) {
            var identityResult = ValidateLiveObjectIdentity(parentObj);
            if (identityResult.IsFailure) { return identityResult.Error!; }

            var validator = new ReferenceValidationVisitor(_pool, parentObj.LocalId);
            parentObj.AcceptChildRefVisitor(ref validator);
            if (validator.Error is not null) { return validator.Error!; }
        }
        return true;
    }

    /// <summary>
    /// 热路径模式：不再扫描整个 live object 图，而是只校验本次 compaction 明确触达的对象和键空间重映射。
    /// </summary>
    private AteliaResult<bool> ValidateCompactionHotPath(
        IReadOnlyCollection<DurableObject> touchedObjects,
        IReadOnlyDictionary<uint, LocalId> translationTable,
        int expectedLiveObjectCount
    ) {
        if (_objectMap.Count != expectedLiveObjectCount) {
            return new SjCorruptionError(
                $"ObjectMap count {_objectMap.Count} does not match live object count {expectedLiveObjectCount}.",
                RecoveryHint: "Compaction produced inconsistent ObjectMap/live-object state."
            );
        }

        foreach (var touchedObj in touchedObjects) {
            var identityResult = ValidateLiveObjectIdentity(touchedObj);
            if (identityResult.IsFailure) { return identityResult.Error!; }

            var validator = new ReferenceValidationVisitor(_pool, touchedObj.LocalId);
            touchedObj.AcceptChildRefVisitor(ref validator);
            if (validator.Error is not null) { return validator.Error!; }
        }

        foreach (uint oldIdValue in translationTable.Keys) {
            if (_objectMap.Get(oldIdValue, out _) == GetIssue.None) {
                return new SjCorruptionError(
                    $"Moved object old LocalId {oldIdValue} still exists in ObjectMap after compaction.",
                    RecoveryHint: "Compaction produced inconsistent ObjectMap key remapping."
                );
            }
        }

        return true;
    }

    private AteliaResult<bool> ValidateLiveObjectIdentity(DurableObject parentObj) {
        if (parentObj.IsDetached) {
            return new SjCorruptionError(
                $"Live object LocalId {parentObj.LocalId.Value} is unexpectedly detached.",
                RecoveryHint: "Compaction produced inconsistent object state."
            );
        }

        var parentHandle = parentObj.LocalId.ToSlotHandle();
        if (!_pool.TryGetValue(parentHandle, out var pooledParent) || !ReferenceEquals(parentObj, pooledParent)) {
            return new SjCorruptionError(
                $"Live object LocalId {parentObj.LocalId.Value} is missing or mismatched in pool.",
                RecoveryHint: "Compaction produced inconsistent pool/object identity state."
            );
        }

        if (_objectMap.Get(parentObj.LocalId.Value, out _) != GetIssue.None) {
            return new SjCorruptionError(
                $"Live object LocalId {parentObj.LocalId.Value} is missing from ObjectMap.",
                RecoveryHint: "Compaction produced inconsistent ObjectMap state."
            );
        }

        return true;
    }

    private readonly struct DetachOnSweepCollectHandler : ISweepCollectHandler<DurableObject> {
        public static void OnCollect(DurableObject value) => value.DetachByGc();
    }

    /// <summary>DFS 遍历中的 visitor：验证引用有效性 + 首访标记去重 + 收集存活 handle + 压入 DFS 栈。</summary>
    private ref struct WalkMarkVisitor(
            GcPool<DurableObject> pool,
            List<DurableObject> liveObjects,
            Stack<DurableObject> dfsStack) : IChildRefVisitor {
        public AteliaError? Error { get; private set; }

        public void Visit(LocalId childId) {
            if (Error is not null || childId.IsNull) { return; }
            SlotHandle handle = childId.ToSlotHandle();
            if (!pool.TryGetValueAndMarkFirstReachable(handle, out var child, out bool firstVisit)) {
                Error = new SjCorruptionError(
                    $"Dangling reference detected during commit: Graph contains missing LocalId {childId.Value}.",
                    RecoveryHint: "Fix object graph references before commit."
                );
                return;
            }
            if (!firstVisit) { return; } // 已访问
            liveObjects.Add(child);
            dfsStack.Push(child);
        }
    }

    private ref struct ReferenceValidationVisitor(GcPool<DurableObject> pool, LocalId parentId) : IChildRefVisitor {
        public AteliaError? Error { get; private set; }

        public void Visit(LocalId childId) {
            if (Error is not null || childId.IsNull) { return; }
            SlotHandle handle = childId.ToSlotHandle();
            if (!pool.TryGetValue(handle, out _)) {
                Error = new SjCorruptionError(
                    $"Dangling reference detected: parent LocalId {parentId.Value} points to missing LocalId {childId.Value}.",
                    RecoveryHint: "Data corruption detected in persisted object references."
                );
            }
        }
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
