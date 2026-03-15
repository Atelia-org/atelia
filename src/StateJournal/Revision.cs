using Atelia.Data;
using Atelia.Rbf;
using Atelia.StateJournal.Internal;

namespace Atelia.StateJournal;

/// <summary>
/// 一个内存对象图状态快照，类似 git commit。
/// 每次 Commit 产生一个新的落盘快照，由CommitId标识。Head标识当前Revision所基于的CommitId，HeadParent 指向Head的前一个 commit。
/// 核心数据：ObjectMap（<see cref="DurableDict{uint, ulong}"/>），记录 LocalId → SizedPtr 映射。
/// </summary>
public class Revision {
    private readonly IRbfFile _file;
    private DurableDict<uint, ulong> _objectMap;
    private LocalIdAllocator _idAllocator;
    private readonly Dictionary<LocalId, DurableObject> _identityMap = new();

    public CommitId HeadParent { get; private set; }
    public CommitId Head { get; private set; }
    DurableObject? _graphRoot;
    public DurableObject? GraphRoot {
        get => _graphRoot;
        set {
            if (value is not null && value.Revision != this) { throw new InvalidOperationException("GraphRoot must belong to this Revision."); }
            _graphRoot = value;
        }
    }

    /// <summary>从头创建一个新的 root commit（未 Commit 前 Id 为 default）。</summary>
    internal Revision(IRbfFile file) {
        Head = default; // Commit 后才有有效 Id
        HeadParent = default; // root commit has no parent
        _file = file;
        // ObjectMap 是 Revision 内部基础设施，不参与用户对象生命周期系统（Bind/NotifyDirty/GraphRoot）。
        _objectMap = Durable.Dict<uint, ulong>();
        _idAllocator = LocalIdAllocator.FromKeys(Array.Empty<uint>());
    }

    /// <summary>从持久化数据加载 commit（内部构造函数）。</summary>
    private Revision(CommitId id, CommitId parentId, IRbfFile file, DurableDict<uint, ulong> objectMap) {
        Head = id;
        HeadParent = parentId;
        _file = file;
        // ObjectMap 是 Revision 内部基础设施，不参与用户对象生命周期系统（Bind/NotifyDirty/GraphRoot）。
        _objectMap = objectMap;
        _idAllocator = LocalIdAllocator.FromKeys(objectMap.Keys);
    }

    /// <summary>从 RBF 文件打开指定 CommitId 对应的 commit。</summary>
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

        // parentId 直接来自头帧的 parentTicket 字段（由 VersionChainStatus 写入，与 tailMeta 无关）
        CommitId parentId = new CommitId(result.HeadParentTicket);

        return new Revision(id, parentId, file, objectMap);
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

    /// <summary>加载指定 LocalId 的 DurableObject。命中 Identity Map 时直接返回缓存实例。</summary>
    public AteliaResult<DurableObject> Load(LocalId id) {
        if (id.IsNull) { return new SjCorruptionError("Cannot load null LocalId.", RecoveryHint: "Use a valid LocalId."); }
        if (_identityMap.TryGetValue(id, out var cached)) { return cached; }
        if (_objectMap.Get(id.Value, out ulong serializedPtr) != GetIssue.None) {
            return new SjCorruptionError(
                $"LocalId {id.Value} not found in ObjectMap.",
                RecoveryHint: "The object may not exist in this commit."
            );
        }
        SizedPtr ticket = SizedPtr.Deserialize(serializedPtr);
        var loadResult = VersionChain.Load(_file, ticket);
        if (loadResult.IsFailure) { return loadResult.Error!; }

        var obj = loadResult.Value!;
        obj.Bind(this, id);
        _identityMap[id] = obj;
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
        var id = _idAllocator.Allocate();
        obj.Bind(this, id, DurableState.TransientDirty);
        _identityMap[id] = obj;
    }

    #endregion

    /// <summary>
    /// 提交所有脏对象，保存 ObjectMap 帧。
    /// 返回新 commit 的 <see cref="CommitId"/>（即 ObjectMap 帧的 ticket）。
    /// </summary>
    internal AteliaResult<CommitId> Commit() {
        // TODO: 引入 Mark-Sweep 后，此处应复用 Mark 阶段的遍历结果；
        //       脏对象筛选可并入 GC 图遍历，避免重复扫描。
        //
        // 1. 保存所有待保存对象，更新 ObjectMap
        //    - !IsTracked: 新建但尚未落盘（即使 HasChanges=false 也必须首存）
        //    - HasChanges: 已落盘对象存在未提交修改
        foreach (var obj in _identityMap.Values) {
            if (obj.IsTracked && !obj.HasChanges) { continue; }
            var saveResult = VersionChain.Save(obj, _file);
            if (saveResult.IsFailure) { return saveResult.Error!; }
            _objectMap.Upsert(obj.LocalId.Value, saveResult.Value.Serialize());
        }

        // 2. 保存 ObjectMap 帧（ForceSave: 即使 ObjectMap 本身无变更也必须产生新帧以创建新 Commit）
        DiffWriteContext context = new() {
            UsageKindOverride = UsageKind.ObjectMap,
            ForceSave = true,
        };
        var result = VersionChain.Save(_objectMap, _file, context);
        if (result.IsFailure) { return result.Error!; }

        var newId = new CommitId(result.Value);
        HeadParent = Head;
        Head = newId;
        return newId;
    }
}
