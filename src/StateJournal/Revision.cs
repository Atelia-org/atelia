using System.Buffers.Binary;
using System.IO;
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
    /// <returns>新 commit 的 <see cref="CommitId"/>（即 ObjectMap 帧的 ticket）。</returns>
    /// <remarks>
    /// 提交流程分为三阶段：
    /// 1) Preflight（纯读校验）；
    /// 2) Persist（仅追加写）；
    /// 3) Finalize（持久化成功后再执行 GC/状态更新）。
    /// 预期的运行时/IO 异常会在持久化成功前转换为失败结果返回；
    /// 若持久化成功后 Finalize 抛异常，则直接向上抛出（避免“已落盘却返回失败”的语义不一致）。
    /// </remarks>
    internal AteliaResult<CommitId> Commit(DurableObject graphRoot) {
        CommitPreflight preflight;
        List<PendingSave> pendingSaves;
        CommitId newCommitId;
        try {
            EnsureCanReference(graphRoot);

            // Phase 1: Preflight（纯读，不执行破坏性修改）
            var preflightResult = BuildCommitPreflight(graphRoot);
            if (preflightResult.IsFailure) { return preflightResult.Error!; }
            preflight = preflightResult.Value!;

            // Phase 2: Persist（写盘但不改对象内存，失败时通过 DiscardChanges 回滚 _objectMap）
            var persistResult = PersistCommit(preflight);
            if (persistResult.IsFailure) {
                _objectMap.DiscardChanges();
                return persistResult.Error!;
            }
            (pendingSaves, newCommitId) = persistResult.Value;
        }
        catch (Exception ex) when (IsExpectedCommitException(ex)) {
            _objectMap.DiscardChanges();
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
        FinalizeCommit(preflight, pendingSaves, newCommitId);
        return newCommitId;
    }

    private void SweepUnreachable(List<DurableObject> liveObjects) {
        _pool.BeginMark();
        _pool.MarkReachable(new SlotHandle(0, 0)); // ObjectMap 始终可达（slot 0）
        foreach (var obj in liveObjects) {
            _pool.MarkReachable(obj.LocalId.ToSlotHandle());
        }
        _pool.Sweep<DetachOnSweepCollectHandler>();
    }

    private AteliaResult<CommitPreflight> BuildCommitPreflight(DurableObject graphRoot) {
        var collectResult = CollectReachableOrFail(graphRoot);
        if (collectResult.IsFailure) { return collectResult.Error!; }
        var liveObjects = collectResult.Value!;

        var liveKeys = new HashSet<uint>(liveObjects.Count);
        foreach (var obj in liveObjects) { liveKeys.Add(obj.LocalId.Value); }

        return new CommitPreflight(
            graphRoot,
            liveObjects,
            liveKeys
        );
    }

    private AteliaResult<(List<PendingSave> PendingSaves, CommitId Id)> PersistCommit(CommitPreflight preflight) {
        // 二阶段 Phase A：写盘但不修改对象内存状态。失败时由调用方 DiscardChanges() 回滚 _objectMap。
        var pendingSaves = new List<PendingSave>();
        foreach (var obj in preflight.LiveObjects) {
            if (obj.IsTracked && !obj.HasChanges) { continue; }
            var writeResult = VersionChain.Write(obj, _file);
            if (writeResult.IsFailure) { return writeResult.Error!; }
            pendingSaves.Add(writeResult.Value);
            _objectMap.Upsert(obj.LocalId.Value, writeResult.Value.Ticket.Serialize());
        }

        var staleKeys = new List<uint>();
        foreach (uint key in _objectMap.Keys) {
            if (!preflight.LiveKeys.Contains(key)) { staleKeys.Add(key); }
        }
        foreach (uint key in staleKeys) { _objectMap.Remove(key); }

        Span<byte> rootMeta = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(rootMeta, preflight.GraphRoot.LocalId.Value);
        DiffWriteContext mapContext = new() {
            UsageKindOverride = UsageKind.ObjectMap,
            ForceSave = true,
        };
        var mapWriteResult = VersionChain.Write(_objectMap, _file, mapContext, tailMeta: rootMeta);
        if (mapWriteResult.IsFailure) { return mapWriteResult.Error!; }
        pendingSaves.Add(mapWriteResult.Value);

        return (pendingSaves, new CommitId(mapWriteResult.Value.Ticket));
    }

    private void FinalizeCommit(CommitPreflight preflight, List<PendingSave> pendingSaves, CommitId newCommitId) {
        // 二阶段 Phase B：全部落盘成功，统一将写盘结果应用到对象内存状态。
        foreach (var pending in pendingSaves) { pending.Complete(); }

        SweepUnreachable(preflight.LiveObjects);

        _head = new CommitSnapshot(
            newCommitId,
            _head?.Id ?? default,
            _objectMap,
            preflight.GraphRoot
        );
    }

    /// <summary>从 GraphRoot 遍历收集可达对象。若发现悬空引用则返回失败。</summary>
    private AteliaResult<List<DurableObject>> CollectReachableOrFail(DurableObject graphRoot) {
        var reachableObjects = new List<DurableObject>();
        var visited = new HashSet<uint>();
        var stack = new Stack<DurableObject>();
        var childBuffer = new List<LocalId>();

        visited.Add(graphRoot.LocalId.Value);
        reachableObjects.Add(graphRoot);
        stack.Push(graphRoot);

        while (stack.Count > 0) {
            var current = stack.Pop();
            childBuffer.Clear();
            var collector = new ChildCollectVisitor(childBuffer);
            current.AcceptChildRefVisitor(ref collector);
            foreach (var childId in childBuffer) {
                SlotHandle handle = childId.ToSlotHandle();
                if (!_pool.TryGetValue(handle, out var child)) {
                    return new SjCorruptionError(
                        $"Dangling reference detected during commit: Graph contains missing LocalId {childId.Value}.",
                        RecoveryHint: "Fix object graph references before commit."
                    );
                }
                if (!visited.Add(childId.Value)) { continue; }
                reachableObjects.Add(child);
                stack.Push(child);
            }
        }

        return reachableObjects;
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

    private ref struct ChildCollectVisitor(List<LocalId> childRefs) : IChildRefVisitor {
        public void Visit(LocalId childId) {
            if (!childId.IsNull) { childRefs.Add(childId); }
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

    private readonly record struct CommitPreflight(
        DurableObject GraphRoot,
        List<DurableObject> LiveObjects,
        HashSet<uint> LiveKeys
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
