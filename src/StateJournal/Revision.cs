using System.Buffers.Binary;
using Atelia.Data;
using Atelia.Rbf;
using Atelia.StateJournal.Internal;
using Atelia.StateJournal.Pools;

namespace Atelia.StateJournal;

/// <summary>
/// 一个内存对象图状态快照，类似 git commit。
/// 每次 Commit 产生一个新的落盘快照，由CommitId标识。Head标识当前Revision所基于的CommitId，HeadParent 指向Head的前一个 commit。
/// 核心数据：ObjectMap（<see cref="DurableDict{uint, ulong}"/>），记录 LocalId → SizedPtr 映射。
/// </summary>
public class Revision {
    private readonly IRbfFile _file;
    private DurableDict<uint, ulong> _objectMap;
    private GcPool<DurableObject> _pool;

    public CommitId HeadParent { get; private set; }
    public CommitId Head { get; private set; }
    /// <summary>最近一次 Commit 时使用的 GraphRoot。首次 Commit 前为 null。Open 后自动从 TailMeta 恢复。</summary>
    public DurableObject? GraphRoot { get; private set; }

    /// <summary>从头创建一个新的 root commit（未 Commit 前 Id 为 default）。</summary>
    internal Revision(IRbfFile file) {
        Head = default; // Commit 后才有有效 Id
        HeadParent = default; // root commit has no parent
        _file = file;
        // ObjectMap 是 Revision 内部基础设施，不参与用户对象生命周期系统（Bind/GraphRoot）。
        _objectMap = Durable.Dict<uint, ulong>();
        // 创建空 pool，用 ObjectMap 占据 0 号槽位（对应 LocalId.Null，不分配给用户对象）。
        _pool = new GcPool<DurableObject>();
        _pool.Store(_objectMap); // slot 0 = ObjectMap
    }

    /// <summary>从持久化数据全量加载 commit（内部构造函数）。</summary>
    private Revision(CommitId id, CommitId parentId, IRbfFile file,
        DurableDict<uint, ulong> objectMap, GcPool<DurableObject> pool
    ) {
        Head = id;
        HeadParent = parentId;
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

        var revision = new Revision(id, parentId, file, objectMap, pool);

        // 绑定所有用户对象到 Revision（ObjectMap 在 slot 0，不 Bind）
        foreach (uint key in objectMap.Keys) {
            var handle = new SlotHandle(key);
            var localId = LocalId.FromSlotHandle(handle);
            var obj = pool[handle];
            obj.Bind(revision, localId);
        }

        // 从 TailMeta 恢复 GraphRoot（4 字节 = GraphRoot 的 LocalId.Value）
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
            revision.GraphRoot = rootObj;
        }

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
    /// <exception cref="ArgumentNullException"><paramref name="graphRoot"/> 为 null。</exception>
    /// <exception cref="InvalidOperationException"><paramref name="graphRoot"/> 不属于当前 Revision。</exception>
    internal AteliaResult<CommitId> Commit(DurableObject graphRoot) {
        EnsureCanReference(graphRoot);
        GraphRoot = graphRoot;

        // 0. 从 GraphRoot 收集可达对象；若出现悬空引用则 fail-fast
        var collectResult = CollectReachableOrFail();
        if (collectResult.IsFailure) { return collectResult.Error!; }
        var liveObjects = collectResult.Value!;

        // 1. Mark-Sweep GC（仅在图一致时执行）
        SweepUnreachable(liveObjects);

        // 2. 保存所有待保存的存活对象，更新 ObjectMap
        foreach (var obj in liveObjects) {
            if (obj.IsTracked && !obj.HasChanges) { continue; }
            var saveResult = VersionChain.Save(obj, _file);
            if (saveResult.IsFailure) { return saveResult.Error!; }
            _objectMap.Upsert(obj.LocalId.Value, saveResult.Value.Serialize());
        }

        // 3. 从 ObjectMap 中移除被 GC 回收的对象（它们的 key 还残留在 ObjectMap 中）
        // GC 后 liveObjects 是完整的存活集合，ObjectMap 中多出来的 key 需要清理
        var liveKeys = new HashSet<uint>(liveObjects.Count);
        foreach (var obj in liveObjects) { liveKeys.Add(obj.LocalId.Value); }
        var staleKeys = new List<uint>();
        foreach (uint key in _objectMap.Keys) {
            if (!liveKeys.Contains(key)) { staleKeys.Add(key); }
        }
        foreach (uint key in staleKeys) { _objectMap.Remove(key); }

        // 4. 保存 ObjectMap 帧（TailMeta = GraphRoot 的 LocalId.Value）
        Span<byte> rootMeta = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(rootMeta, GraphRoot!.LocalId.Value);
        DiffWriteContext context = new() {
            UsageKindOverride = UsageKind.ObjectMap,
            ForceSave = true,
        };
        var result = VersionChain.Save(_objectMap, _file, context, tailMeta: rootMeta);
        if (result.IsFailure) { return result.Error!; }

        var newId = new CommitId(result.Value);
        HeadParent = Head;
        Head = newId;
        return newId;
    }

    private void SweepUnreachable(List<DurableObject> liveObjects) {
        _pool.BeginMark();
        _pool.MarkReachable(new SlotHandle(0, 0)); // ObjectMap 始终可达（slot 0）
        foreach (var obj in liveObjects) {
            _pool.MarkReachable(obj.LocalId.ToSlotHandle());
        }
        _pool.Sweep<DetachOnSweepCollectHandler>();
    }

    /// <summary>从 GraphRoot 遍历收集可达对象。若发现悬空引用则返回失败。</summary>
    private AteliaResult<List<DurableObject>> CollectReachableOrFail() {
        var reachableObjects = new List<DurableObject>();
        var visited = new HashSet<uint>();

        // 从 GraphRoot 开始 DFS 遍历
        var markVisitor = new MarkVisitor(_pool, reachableObjects, visited);
        markVisitor.MarkRoot(GraphRoot!);
        if (markVisitor.Error is not null) { return markVisitor.Error!; }

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

    /// <summary>Mark 阶段的 Visitor：标记可达 + 收集存活对象 + 递归遍历子引用。</summary>
    private ref struct MarkVisitor(
        GcPool<DurableObject> pool,
        List<DurableObject> reachableObjects,
        HashSet<uint> visited
    ) : IChildRefVisitor {
        public AteliaError? Error { get; private set; }

        /// <summary>标记根对象并递归遍历其子引用。</summary>
        public void MarkRoot(DurableObject root) {
            if (!visited.Add(root.LocalId.Value)) { return; }
            reachableObjects.Add(root);
            root.AcceptChildRefVisitor(ref this);
        }

        public void Visit(LocalId childId) {
            if (Error is not null) { return; }
            if (childId.IsNull) { return; }
            SlotHandle handle = childId.ToSlotHandle();
            if (!pool.TryGetValue(handle, out var child)) {
                Error = new SjCorruptionError(
                    $"Dangling reference detected during commit: Graph contains missing LocalId {childId.Value}.",
                    RecoveryHint: "Fix object graph references before commit."
                );
                return;
            }
            if (!visited.Add(childId.Value)) { return; }
            reachableObjects.Add(child);
            child.AcceptChildRefVisitor(ref this);
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
}
