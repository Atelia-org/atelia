using Atelia.StateJournal.Internal;
using Atelia.StateJournal.Pools;

namespace Atelia.StateJournal;

partial class Revision {
    // ───── Compaction 参数 ─────

    /// <summary>存活对象数低于此值时不触发压缩。</summary>
    private const int CompactionMinThreshold = 64;

    /// <summary>碎片率（%）超过此值才触发压缩。</summary>
    private const int CompactionTriggerPercent = 25;

    /// <summary>每次压缩最多移动的存活对象比例（%）。</summary>
    private const int CompactionMovePercent = 5;

    /// <summary>
    /// 从 GraphRoot 开始 DFS 遍历，同时执行 GcPool 的 BeginMark + TryMarkReachable。
    /// 遍历结束后 mark bitmap 即为最终可达集，后续 Sweep 无需再标记。
    /// 若发现悬空引用则返回失败（mark bitmap 留脏，下次 Commit 的 BeginMark 会清零，无副作用）。
    /// </summary>
    private AteliaResult<List<DurableObject>> WalkAndMark(DurableObject graphRoot) {
        _pool.BeginMark();
        _pool.MarkReachable(new SlotHandle(1, 0)); // slot 0 = ObjectMap，始终可达
        _pool.MarkReachable(new SlotHandle(0, 1)); // slot 1 = SymbolTable，始终可达

        // Symbol Pool 的 mark 与 Object Pool 同步进行，一趟 DFS 完成两种引用的标记
        _symbolPool.BeginMark();

        var liveObjects = new List<DurableObject>();
        var dfsStack = new Stack<DurableObject>();

        // 标记并入队根节点
        _pool.TryMarkReachable(graphRoot.LocalId.ToSlotHandle());
        liveObjects.Add(graphRoot);
        dfsStack.Push(graphRoot);

        while (dfsStack.Count > 0) {
            var current = dfsStack.Pop();
            var visitor = new WalkMarkVisitor(_pool, _symbolPool, liveObjects, dfsStack);
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
        RollbackCompactionChanges(undoToken, symbolUndoToken: null, touchedObjects);
    }

    /// <summary>
    /// 核心回滚逻辑：恢复 pool slot 布局 → 恢复被移动对象的 LocalId → 丢弃 ObjectMap 与 touched 对象的工作态变更。
    /// </summary>
    /// <remarks>
    /// 目标语义下，这里不负责“吞掉”内部不变量破坏。
    /// 若 pool 恢复后的对象布局与 move records 不一致，应直接抛异常 fail-fast。
    /// </remarks>
    private void RollbackCompactionChanges(
        GcPool<DurableObject>.CompactionJournal? objectUndoToken,
        InternPool<string, OrdinalStaticEqualityComparer>.CompactionJournal? symbolUndoToken,
        HashSet<DurableObject> touchedObjects
    ) {
        // 顺序关键：先恢复 pool slot 布局，再恢复 LocalId，最后丢弃工作态变更
        if (objectUndoToken is { } ou) {
            _pool.RollbackCompaction(ou);
            RestoreMovedObjectLocalIds(ou.Records);
        }
        if (symbolUndoToken is { } su) {
            _symbolPool.RollbackCompaction(su);
        }
        _objectMap.DiscardChanges();
        _symbolTable.DiscardChanges();
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
        // ObjectMap.Count = liveObjects.Count + 1 (SymbolTable at key=1)
        int expectedObjectMapCount = liveObjects.Count + 1;
        if (_objectMap.Count != expectedObjectMapCount) {
            return new SjCorruptionError(
                $"ObjectMap count {_objectMap.Count} does not match expected count {expectedObjectMapCount} (live={liveObjects.Count} + 1 SymbolTable).",
                RecoveryHint: "Compaction produced inconsistent ObjectMap/live-object state."
            );
        }

        foreach (var parentObj in liveObjects) {
            var identityResult = ValidateLiveObjectIdentity(parentObj);
            if (identityResult.IsFailure) { return identityResult.Error!; }

            var validator = new ReferenceValidationVisitor(_pool, _symbolPool, parentObj.LocalId);
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
        // ObjectMap.Count = expectedLiveObjectCount + 1 (SymbolTable at key=1)
        int expectedObjectMapCount = expectedLiveObjectCount + 1;
        if (_objectMap.Count != expectedObjectMapCount) {
            return new SjCorruptionError(
                $"ObjectMap count {_objectMap.Count} does not match expected count {expectedObjectMapCount} (live={expectedLiveObjectCount} + 1 SymbolTable).",
                RecoveryHint: "Compaction produced inconsistent ObjectMap/live-object state."
            );
        }

        foreach (var touchedObj in touchedObjects) {
            var identityResult = ValidateLiveObjectIdentity(touchedObj);
            if (identityResult.IsFailure) { return identityResult.Error!; }

            var validator = new ReferenceValidationVisitor(_pool, _symbolPool, touchedObj.LocalId);
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

    #region Visitors

    partial struct CompactRewriter {
        public LocalId Rewrite(LocalId oldId) {
            return objectTable.TryGetValue(oldId.Value, out var newId) ? newId : oldId;
        }
    }

    partial struct WalkMarkVisitor {
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

    partial struct ReferenceValidationVisitor {
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
    #endregion
}
