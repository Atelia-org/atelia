using System.Buffers.Binary;
using Atelia.Data;
using Atelia.Rbf;
using Atelia.StateJournal.Internal;
using Atelia.StateJournal.Pools;

namespace Atelia.StateJournal;

/// <summary>
/// 一个内存对象图状态快照，类似 git commit。
/// 每次 Commit 产生一个新的落盘快照，由 <see cref="CommitId"/> 标识。
/// <see cref="_head"/> 持有当前已提交的快照状态（Id / ParentId / ObjectMap / GraphRoot），首次 Commit 前为 null。
/// </summary>
public class Revision {
    private readonly IRbfFile _file;
    /// <summary>
    /// 当前活跃的 ObjectMap，始终占据 pool slot 0。
    /// 首次 Commit 前为空 dict；Commit 后与 <see cref="CommitSnapshot.ObjectMap"/> 是同一实例。
    /// </summary>
    private DurableDict<uint, ulong> _objectMap;
    private GcPool<DurableObject> _pool;
    /// <summary>最近一次成功 Commit 的快照。首次 Commit 前为 null。</summary>
    private CommitSnapshot? _head;

    public CommitId Head => _head?.Id ?? default;
    public CommitId HeadParent => _head?.ParentId ?? default;
    /// <summary>最近一次 Commit 时使用的 GraphRoot。首次 Commit 前为 null。Open 后自动从 TailMeta 恢复。</summary>
    public DurableObject? GraphRoot => _head?.GraphRoot;

    /// <summary>从头创建一个新的 root commit（未 Commit 前 _head 为 null）。</summary>
    internal Revision(IRbfFile file) {
        _file = file;
        _objectMap = Durable.Dict<uint, ulong>();
        _pool = new GcPool<DurableObject>();
        _pool.Store(_objectMap); // slot 0 = ObjectMap
    }

    /// <summary>从持久化数据全量加载 commit（内部构造函数，_head 由 Open 设置）。</summary>
    private Revision(IRbfFile file, DurableDict<uint, ulong> objectMap, GcPool<DurableObject> pool) {
        _file = file;
        _objectMap = objectMap;
        _pool = pool;
    }

    /// <summary>从 RBF 文件打开指定 CommitId 对应的 commit，全量加载所有对象到 GcPool。</summary>
    /// <param name="id">commit 的标识（内含 ObjectMap 帧的 SizedPtr）。</param>
    /// <param name="file">RBF 文件。</param>
    internal static AteliaResult<Revision> Open(CommitId id, IRbfFile file) {
        var loadResult = VersionChain.LoadFull(
            file, id.Ticket,
            expectUsage: UsageKind.ObjectMap,
            expectObject: DurableObjectKind.TypedDict
        );
        if (loadResult.IsFailure) { return loadResult.Error!; }

        var result = loadResult.Value;
        if (result.Object is not DurableDict<uint, ulong> objectMap) {
            return new SjCorruptionError(
                $"ObjectMap frame resolved to unexpected type: {result.Object.GetType()}.",
                RecoveryHint: "Expected DurableDict<uint, ulong>."
            );
        }

        CommitId parentId = new CommitId(result.HeadParentTicket);
        byte[] tailMeta = result.HeadTailMeta;

        // 全量加载：遍历 ObjectMap 所有 entries，从 RBF 加载对象，
        // 连同 ObjectMap 自身（slot 0）一起 Rebuild 进 GcPool。
        var entries = new (SlotHandle, DurableObject)[objectMap.Count + 1];
        entries[0] = (new SlotHandle(0, 0), objectMap); // slot 0 = ObjectMap

        int i = 1;
        foreach (uint key in objectMap.Keys) {
            var handle = new SlotHandle(key); // LocalId.Value == SlotHandle.Packed
            if (objectMap.Get(key, out ulong serializedPtr) != GetIssue.None) {
                return new SjCorruptionError(
                    $"ObjectMap key {key} could not be read.",
                    RecoveryHint: "Data corruption in ObjectMap."
                );
            }
            SizedPtr ticket = SizedPtr.Deserialize(serializedPtr);
            var objLoad = VersionChain.Load(file, ticket);
            if (objLoad.IsFailure) { return objLoad.Error!; }
            entries[i++] = (handle, objLoad.Value!);
        }

        var pool = GcPool<DurableObject>.Rebuild(entries);

        var revision = new Revision(file, objectMap, pool);

        // 绑定所有用户对象到 Revision（ObjectMap 在 slot 0，不 Bind）
        foreach (uint key in objectMap.Keys) {
            var handle = new SlotHandle(key);
            var localId = LocalId.FromSlotHandle(handle);
            var obj = pool[handle];
            obj.Bind(revision, localId);
        }

        // 从 TailMeta 恢复 GraphRoot（4 字节 = GraphRoot 的 LocalId.Value）
        DurableObject? graphRoot = null;
        if (tailMeta.Length >= 4) {
            uint rootIdValue = BinaryPrimitives.ReadUInt32LittleEndian(tailMeta);
            if (rootIdValue == 0) {
                return new SjCorruptionError(
                    "Invalid GraphRoot LocalId 0 in TailMeta.",
                    RecoveryHint: "Data corruption in TailMeta."
                );
            }
            var rootHandle = new SlotHandle(rootIdValue);
            if (!pool.TryGetValue(rootHandle, out var rootObj)) {
                return new SjCorruptionError(
                    $"Unknown GraphRoot LocalId {rootIdValue}.",
                    RecoveryHint: "Data corruption in TailMeta."
                );
            }
            graphRoot = rootObj;
        }

        revision._head = new CommitSnapshot(id, parentId, objectMap, graphRoot);

        var validateResult = revision.ValidateAllReferences();
        if (validateResult.IsFailure) { return validateResult.Error!; }

        return revision;
    }

    /// <summary>查找 RBF 文件中最新的 CommitId（逆向扫描最新的 ObjectMap 帧）。</summary>
    internal static AteliaResult<CommitId> FindLatestCommitId(IRbfFile file) {
        foreach (var info in file.ScanReverse()) {
            FrameTag tag = new(info.Tag);
            if (tag.UsageKind == UsageKind.ObjectMap) { return new CommitId(info.Ticket); }
        }
        return new SjCorruptionError(
            "No ObjectMap frame found in RBF file.",
            RecoveryHint: "The file may be empty or not contain any committed data."
        );
    }

    /// <summary>获取指定 LocalId 的 DurableObject（全量加载模式下直接从 pool 读取）。</summary>
    public AteliaResult<DurableObject> Load(LocalId id) {
        if (id.IsNull) { return new SjCorruptionError("Cannot load null LocalId.", RecoveryHint: "Use a valid LocalId."); }
        var handle = id.ToSlotHandle();
        if (!_pool.TryGetValue(handle, out var obj)) {
            return new SjCorruptionError(
                $"LocalId {id.Value} not found in pool.",
                RecoveryHint: "The object may not exist in this commit."
            );
        }
        return obj;
    }

    #region Object Factory

    /// <summary>创建 TypedDict 并绑定到当前 Revision。</summary>
    public DurableDict<TKey, TValue> CreateDict<TKey, TValue>() where TKey : notnull where TValue : notnull {
        var obj = Durable.Dict<TKey, TValue>();
        BindNewObject(obj);
        return obj;
    }

    /// <summary>创建 MixedDict 并绑定到当前 Revision。</summary>
    public DurableDict<TKey> CreateDict<TKey>() where TKey : notnull {
        var obj = Durable.Dict<TKey>();
        BindNewObject(obj);
        return obj;
    }

    /// <summary>创建 TypedList 并绑定到当前 Revision。</summary>
    public DurableList<T> CreateList<T>() where T : notnull {
        var obj = Durable.List<T>();
        BindNewObject(obj);
        return obj;
    }

    /// <summary>创建 MixedList 并绑定到当前 Revision。</summary>
    public DurableList CreateList() {
        var obj = Durable.List();
        BindNewObject(obj);
        return obj;
    }

    private void BindNewObject(DurableObject obj) {
        var handle = _pool.Store(obj);
        var id = LocalId.FromSlotHandle(handle);
        obj.Bind(this, id, DurableState.TransientDirty);
    }

    /// <summary>校验 DurableObject 可作为当前 Revision 的图引用被持有/写入。</summary>
    internal void EnsureCanReference(DurableObject obj) {
        ArgumentNullException.ThrowIfNull(obj);
        if (!obj.IsBoundTo(this)) {
            throw new InvalidOperationException(
                $"Cannot store a DurableObject from a different Revision or an unbound object. Object LocalId={obj.LocalId}, this Revision={Head}."
            );
        }
        if (obj.IsDetached) { throw new InvalidOperationException($"Cannot store a detached DurableObject (LocalId={obj.LocalId.Value})."); }
        SlotHandle handle = obj.LocalId.ToSlotHandle();
        if (!_pool.Validate(handle)) {
            throw new InvalidOperationException(
                $"Cannot store DurableObject LocalId={obj.LocalId.Value}: object is no longer tracked by this Revision (possibly GC-collected)."
            );
        }
    }

    #endregion

    /// <summary>
    /// 提交所有脏对象，保存 ObjectMap 帧。
    /// 在保存前执行 Mark-Sweep GC，从 <paramref name="graphRoot"/> 出发标记可达对象，回收不可达对象。
    /// GraphRoot 的 LocalId 会序列化到 ObjectMap 帧的 TailMeta 中，Open 时自动恢复。
    /// </summary>
    /// <param name="graphRoot">对象图的根节点，必须属于当前 Revision。</param>
    /// <returns>
    /// 若无 compaction 或 compaction 后续持久化成功，返回最新 commit 的 <see cref="CommitId"/>（ObjectMap 帧 ticket）。
    /// 若 primary commit 已成功但 compaction 阶段失败，则返回失败；此时 <see cref="Head"/> 可能已经前进（部分成功可见）。
    /// </returns>
    /// <remarks>
    /// 提交流程分为两段：
    /// A) Primary Commit（三阶段）：
    /// 1) WalkAndMark — 从 graphRoot DFS 遍历对象图，同时执行 GcPool Mark（用 mark bitmap 替代 HashSet 去重）；
    /// 2) Persist — 仅追加写盘，不改对象内存状态；
    /// 3) Finalize — 持久化成功后执行 Sweep GC 和状态更新。
    /// B) Compaction Follow-up（可选）：
    /// - 若触发 compaction，先在内存执行 MoveSlot/Rebind/引用重写；
    /// - 然后执行一次内部 CommitCore 持久化 compaction 造成的脏变更。
    /// 因此 Commit 具备“部分成功可见”语义：primary commit 成功后，即使后续 compaction 失败，Head 也可能已更新到 primary commit。
    /// </remarks>
    internal AteliaResult<CommitId> Commit(DurableObject graphRoot) {
        var userCommit = CommitCore(graphRoot, rollbackObjectMapOnFailure: true);
        if (userCommit.IsFailure) { return userCommit.Error!; }

        CommitId firstCommitId = userCommit.Value;
        var compactionApply = ApplyCompactionIfNeeded(firstCommitId);
        if (compactionApply.IsFailure) { return compactionApply.Error!; }
        if (!compactionApply.Value.Compacted) { return firstCommitId; }

        var compactionCommit = CommitCore(graphRoot, rollbackObjectMapOnFailure: false);
        if (compactionCommit.IsFailure) {
            return BuildCompactionFollowupPersistFailureError(firstCommitId, compactionCommit.Error!);
        }

        return compactionCommit.Value;
    }

    private AteliaResult<CommitId> CommitCore(DurableObject graphRoot, bool rollbackObjectMapOnFailure) {
        List<DurableObject> liveObjects;
        List<PendingSave> pendingSaves;
        CommitId newCommitId;
        try {
            EnsureCanReference(graphRoot);

            // Phase 1: WalkAndMark（DFS 遍历 + GcPool Mark，合并去重与标记为单次遍历）
            var walkResult = WalkAndMark(graphRoot);
            if (walkResult.IsFailure) { return walkResult.Error!; }
            liveObjects = walkResult.Value!;

            // Phase 2: Persist（写盘但不改对象内存，失败时按策略决定是否回滚 _objectMap）
            var persistResult = PersistCommit(graphRoot, liveObjects);
            if (persistResult.IsFailure) {
                if (rollbackObjectMapOnFailure) { _objectMap.DiscardChanges(); }
                return persistResult.Error!;
            }
            (pendingSaves, newCommitId) = persistResult.Value;
        }
        catch (Exception ex) when (IsExpectedCommitException(ex)) {
            if (rollbackObjectMapOnFailure) { _objectMap.DiscardChanges(); }
            var details = new Dictionary<string, string> {
                ["ExceptionType"] = ex.GetType().FullName ?? ex.GetType().Name,
            };
            if (graphRoot is not null) {
                details["GraphRootLocalId"] = graphRoot.LocalId.Value.ToString();
            }

            return new SjStateError(
                $"Commit failed due to runtime state or I/O exception: {ex.GetType().Name}: {ex.Message}",
                RecoveryHint: "Fix the runtime/file state and retry commit. No GC detachment has been applied before ObjectMap persistence succeeds.",
                Details: details
            );
        }

        // Phase 3: Finalize（全部落盘成功，统一应用内存状态变更）
        FinalizeCommit(graphRoot, pendingSaves, newCommitId);
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

    private AteliaResult<(List<PendingSave> PendingSaves, CommitId Id)> PersistCommit(
        DurableObject graphRoot, List<DurableObject> liveObjects
    ) {
        var pendingSaves = new List<PendingSave>();
        foreach (var obj in liveObjects) {
            if (obj.IsTracked && !obj.HasChanges) { continue; }
            var writeResult = VersionChain.Write(obj, _file);
            if (writeResult.IsFailure) { return writeResult.Error!; }
            pendingSaves.Add(writeResult.Value);
            _objectMap.Upsert(obj.LocalId.Value, writeResult.Value.Ticket.Serialize());
        }

        // 从 ObjectMap 中移除不可达对象的 key（利用 Phase 1 的 mark bitmap 判定）
        // 迭代 CommittedKeys（上次 commit 的快照，本轮 Upsert/Remove 不影响）以避免临时集合分配
        foreach (uint key in _objectMap.CommittedKeys) {
            if (!_pool.IsMarkedReachable(new SlotHandle(key))) { _objectMap.Remove(key); }
        }

        Span<byte> rootMeta = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(rootMeta, graphRoot.LocalId.Value);
        DiffWriteContext mapContext = new() {
            UsageKindOverride = UsageKind.ObjectMap,
            ForceSave = true,
        };
        var mapWriteResult = VersionChain.Write(_objectMap, _file, mapContext, tailMeta: rootMeta);
        if (mapWriteResult.IsFailure) { return mapWriteResult.Error!; }
        pendingSaves.Add(mapWriteResult.Value);

        return (pendingSaves, new CommitId(mapWriteResult.Value.Ticket));
    }

    private void FinalizeCommit(DurableObject graphRoot, List<PendingSave> pendingSaves, CommitId newCommitId) {
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

    /// <summary>
    /// 碎片率超阈值时触发渐进压缩。
    /// 在 Sweep 之后调用，利用 FreeBitmap 的空洞分布执行有限次数的 MoveSlot，
    /// 然后遍历 ObjectMap 覆盖的存活对象重写所有受影响的子引用。
    /// 被改动的对象会变脏，并在同一次外层 Commit 的 follow-up CommitCore 中持久化。
    /// 若 apply 失败则尝试回滚（当前 _pool 回滚实现仍是占位）。
    /// </summary>
    private AteliaResult<CompactionApplyContext> ApplyCompactionIfNeeded(CommitId primaryCommitId) {
        int liveCount = _pool.Count;
        int capacity = _pool.Capacity;
        if (liveCount < CompactionMinThreshold || capacity == 0) { return CompactionApplyContext.NotCompacted; }

        // fragmentationRatio = (capacity - liveCount) / capacity
        // 等价于 (capacity - liveCount) * 100 > capacity * triggerPercent 避免浮点
        int holeCount = capacity - liveCount;
        if (holeCount * 100 <= capacity * CompactionTriggerPercent) { return CompactionApplyContext.NotCompacted; }

        int maxMoves = Math.Max(1, (int)((long)liveCount * CompactionMovePercent / 100));
        GcPool<DurableObject>.CompactionApplyResult applyResult = _pool.CompactWithUndo(maxMoves);
        if (applyResult.Moves.Count == 0) { return CompactionApplyContext.NotCompacted; }
        try {
            // 构建翻译表
            var translationTable = new Dictionary<uint, LocalId>(applyResult.Moves.Count);
            foreach (var (oldHandle, newHandle) in applyResult.Moves) {
                var oldId = LocalId.FromSlotHandle(oldHandle);
                var newId = LocalId.FromSlotHandle(newHandle);

                // Rebind 被移动对象自身的 LocalId
                var obj = _pool[newHandle]; // 值已在物理上移动到新位置
                obj.Rebind(newId);

                // 更新 ObjectMap 的 key：Remove(oldKey) + Upsert(newKey, sameTicket)
                if (_objectMap.Get(oldId.Value, out ulong ticket) == GetIssue.None) {
                    _objectMap.Remove(oldId.Value);
                    _objectMap.Upsert(newId.Value, ticket);
                }

                translationTable.Add(oldId.Value, newId);
            }

            // 遍历 pool 中所有存活的用户对象，重写子引用
            // ObjectMap 保存了所有用户对象的 key
            var rewriter = new CompactRewriter(translationTable);
            foreach (uint key in _objectMap.Keys) {
                var handle = new SlotHandle(key);
                if (!_pool.TryGetValue(handle, out var obj)) { continue; }
                obj.AcceptChildRefRewrite(ref rewriter);
            }

            var validateResult = ValidateAllReferences();
            if (validateResult.IsFailure) {
                var rollbackResult = TryRollbackCompaction(primaryCommitId, applyResult, validateResult.Error!);
                if (rollbackResult.IsFailure) { return rollbackResult.Error!; }
                return BuildCompactionApplyFailureError(
                    primaryCommitId,
                    new InvalidDataException($"Compaction produced invalid references: {validateResult.Error!.Message}")
                );
            }
        }
        catch (Exception ex) when (IsExpectedCommitException(ex)) {
            var rollbackResult = TryRollbackCompaction(primaryCommitId, applyResult, ex);
            if (rollbackResult.IsFailure) { return rollbackResult.Error!; }
            return BuildCompactionApplyFailureError(primaryCommitId, ex);
        }

        return new CompactionApplyContext(true, applyResult.UndoToken, applyResult.Moves);
    }

    private AteliaResult<bool> TryRollbackCompaction(
        CommitId primaryCommitId,
        GcPool<DurableObject>.CompactionApplyResult applyResult,
        AteliaError applyCause
    ) {
        try {
            _pool.RollbackCompaction(applyResult.UndoToken);
            _objectMap.DiscardChanges();
            foreach (var obj in EnumerateRollbackCandidates(applyResult.Moves)) {
                obj.DiscardChanges();
            }
            return true;
        }
        catch (Exception ex) when (IsExpectedCommitException(ex) || ex is NotImplementedException) {
            return BuildCompactionRollbackFailureError(primaryCommitId, ex, applyCause);
        }
    }

    private AteliaResult<bool> TryRollbackCompaction(
        CommitId primaryCommitId,
        GcPool<DurableObject>.CompactionApplyResult applyResult,
        Exception applyException
    ) {
        try {
            _pool.RollbackCompaction(applyResult.UndoToken);
            _objectMap.DiscardChanges();
            foreach (var obj in EnumerateRollbackCandidates(applyResult.Moves)) {
                obj.DiscardChanges();
            }
            return true;
        }
        catch (Exception ex) when (IsExpectedCommitException(ex) || ex is NotImplementedException) {
            return BuildCompactionRollbackFailureError(primaryCommitId, ex, applyException);
        }
    }

    private IEnumerable<DurableObject> EnumerateRollbackCandidates(IReadOnlyList<(SlotHandle Old, SlotHandle New)> moves) {
        var visited = new HashSet<uint>();
        foreach (uint key in _objectMap.Keys) {
            var handle = new SlotHandle(key);
            if (_pool.TryGetValue(handle, out var obj) && visited.Add(obj.LocalId.Value)) {
                yield return obj;
            }
        }

        foreach (var (oldHandle, newHandle) in moves) {
            if (_pool.TryGetValue(oldHandle, out var oldObj) && visited.Add(oldObj.LocalId.Value)) {
                yield return oldObj;
            }
            if (_pool.TryGetValue(newHandle, out var newObj) && visited.Add(newObj.LocalId.Value)) {
                yield return newObj;
            }
        }
    }

    private static SjCompactionApplyError BuildCompactionApplyFailureError(CommitId primaryCommitId, Exception ex) {
        var details = new Dictionary<string, string> {
            ["PrimaryCommitTicket"] = primaryCommitId.Ticket.Serialize().ToString(),
            ["CompactionStage"] = "Apply",
            ["ExceptionType"] = ex.GetType().FullName ?? ex.GetType().Name,
        };

        return new SjCompactionApplyError(
            $"Commit primary snapshot succeeded, but compaction apply failed: {ex.GetType().Name}: {ex.Message}",
            RecoveryHint: "Primary snapshot has been persisted and set as current Head. Fix runtime issue and retry Commit to persist compaction changes.",
            Details: details,
            Cause: null
        );
    }

    private static SjCompactionFollowupPersistError BuildCompactionFollowupPersistFailureError(CommitId primaryCommitId, AteliaError cause) {
        var details = new Dictionary<string, string> {
            ["PrimaryCommitTicket"] = primaryCommitId.Ticket.Serialize().ToString(),
            ["CompactionStage"] = "FollowupPersist",
            ["FollowupErrorCode"] = cause.ErrorCode,
        };

        return new SjCompactionFollowupPersistError(
            "Commit primary snapshot succeeded, but compaction follow-up persistence failed.",
            RecoveryHint: "Primary snapshot has been persisted and set as current Head. Fix runtime/I/O issue and retry Commit to persist compaction changes.",
            Details: details,
            Cause: cause
        );
    }

    private static SjCompactionRollbackError BuildCompactionRollbackFailureError(
        CommitId primaryCommitId, Exception rollbackException, AteliaError applyCause
    ) {
        var details = new Dictionary<string, string> {
            ["PrimaryCommitTicket"] = primaryCommitId.Ticket.Serialize().ToString(),
            ["CompactionStage"] = "Rollback",
            ["RollbackExceptionType"] = rollbackException.GetType().FullName ?? rollbackException.GetType().Name,
            ["ApplyErrorCode"] = applyCause.ErrorCode,
        };

        return new SjCompactionRollbackError(
            $"Compaction apply failed and rollback also failed: {rollbackException.GetType().Name}: {rollbackException.Message}",
            RecoveryHint: "Primary snapshot is durable. Restart revision state from persisted Head before retrying commit.",
            Details: details,
            Cause: applyCause
        );
    }

    private static SjCompactionRollbackError BuildCompactionRollbackFailureError(
        CommitId primaryCommitId, Exception rollbackException, Exception applyException
    ) {
        var details = new Dictionary<string, string> {
            ["PrimaryCommitTicket"] = primaryCommitId.Ticket.Serialize().ToString(),
            ["CompactionStage"] = "Rollback",
            ["ApplyExceptionType"] = applyException.GetType().FullName ?? applyException.GetType().Name,
            ["RollbackExceptionType"] = rollbackException.GetType().FullName ?? rollbackException.GetType().Name,
        };

        return new SjCompactionRollbackError(
            $"Compaction apply failed and rollback also failed: {rollbackException.GetType().Name}: {rollbackException.Message}",
            RecoveryHint: "Primary snapshot is durable. Restart revision state from persisted Head before retrying commit.",
            Details: details,
            Cause: null
        );
    }

    // ───── Compaction 参数（internal 以允许测试覆盖策略验证） ─────

    /// <summary>存活对象数低于此值时不触发压缩。</summary>
    internal const int CompactionMinThreshold = 64;

    /// <summary>碎片率（%）超过此值才触发压缩。</summary>
    internal const int CompactionTriggerPercent = 25;

    /// <summary>每次压缩最多移动的存活对象比例（%）。</summary>
    internal const int CompactionMovePercent = 5;

    /// <summary>Compaction 用的引用重写器：查翻译表，命中则返回新 LocalId，否则原样返回。</summary>
    private ref struct CompactRewriter(Dictionary<uint, LocalId> table) : IChildRefRewriter {
        public LocalId Rewrite(LocalId oldId) {
            return table.TryGetValue(oldId.Value, out var newId) ? newId : oldId;
        }
    }

    private readonly record struct CompactionApplyContext(
        bool Compacted,
        GcPool<DurableObject>.CompactionUndoToken UndoToken,
        IReadOnlyList<(SlotHandle Old, SlotHandle New)> Moves
    ) {
        public static readonly CompactionApplyContext NotCompacted = new(
            Compacted: false,
            UndoToken: default,
            Moves: Array.Empty<(SlotHandle Old, SlotHandle New)>()
        );
    }

    /// <summary>全量校验 pool 中所有用户对象的引用完整性。发现悬空引用则失败。</summary>
    private AteliaResult<bool> ValidateAllReferences() {
        foreach (uint key in _objectMap.Keys) {
            var parentHandle = new SlotHandle(key);
            if (!_pool.TryGetValue(parentHandle, out var parentObj)) {
                return new SjCorruptionError(
                    $"ObjectMap entry LocalId {key} is missing in pool.",
                    RecoveryHint: "Data corruption in ObjectMap or in-memory pool."
                );
            }

            var parentId = LocalId.FromSlotHandle(parentHandle);
            var validator = new ReferenceValidationVisitor(_pool, parentId);
            parentObj.AcceptChildRefVisitor(ref validator);
            if (validator.Error is not null) { return validator.Error!; }
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

    private readonly record struct CommitSnapshot(
        CommitId Id,
        CommitId ParentId,
        DurableDict<uint, ulong> ObjectMap,
        DurableObject? GraphRoot
    );

    private static bool IsExpectedCommitException(Exception ex) {
        return ex is InvalidOperationException
            or ArgumentException
            or InvalidDataException
            or NotSupportedException
            or ObjectDetachedException
            or IOException
            or UnauthorizedAccessException
            or ObjectDisposedException;
    }
}
