using Atelia.StateJournal.Internal;
using Atelia.StateJournal.Pools;

namespace Atelia.StateJournal;

partial class Revision {
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

    private readonly struct DetachOnSweepCollectHandler : ISweepCollectHandler<DurableObject> {
        public static void OnCollect(DurableObject value) => value.DetachByGc();
    }

    #region Visitors

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
