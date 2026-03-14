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
    private readonly HashSet<DurableObject> _dirtySet = new();

    public CommitId HeadParent { get; private set; }
    public CommitId Head { get; private set; }

    /// <summary>从头创建一个新的 root commit（未 Commit 前 Id 为 default）。</summary>
    internal Revision(IRbfFile file) {
        Head = default; // Commit 后才有有效 Id
        HeadParent = default; // root commit has no parent
        _file = file;
        _objectMap = Durable.Dict<uint, ulong>();
        _idAllocator = LocalIdAllocator.FromKeys(Array.Empty<uint>());
    }

    /// <summary>从持久化数据加载 commit（内部构造函数）。</summary>
    private Revision(CommitId id, CommitId parentId, IRbfFile file, DurableDict<uint, ulong> objectMap) {
        Head = id;
        HeadParent = parentId;
        _file = file;
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

    /// <summary>加载指定 LocalId 的 DurableObject。</summary>
    public AteliaResult<DurableObject> Load(LocalId id) {
        if (id.IsNull) { return new SjCorruptionError("Cannot load null LocalId.", RecoveryHint: "Use a valid LocalId."); }
        if (_objectMap.Get(id.Value, out ulong serializedPtr) != GetIssue.None) {
            return new SjCorruptionError(
                $"LocalId {id.Value} not found in ObjectMap.",
                RecoveryHint: "The object may not exist in this commit."
            );
        }
        SizedPtr ticket = SizedPtr.Deserialize(serializedPtr);
        return VersionChain.Load(_file, ticket);
    }

    /// <summary>分配一个 LocalId 并注册对象。</summary>
    internal LocalId AllocateId() {
        return _idAllocator.Allocate();
    }

    /// <summary>
    /// 提交所有脏对象，保存 ObjectMap 帧。
    /// 返回新 commit 的 <see cref="CommitId"/>（即 ObjectMap 帧的 ticket）。
    /// </summary>
    internal AteliaResult<CommitId> Commit() {
        // 1. 保存所有脏对象，更新 ObjectMap
        foreach (var obj in _dirtySet) {
            var saveResult = VersionChain.Save(obj, _file);
            if (saveResult.IsFailure) { return saveResult.Error!; }
            _objectMap.Upsert(obj.LocalId.Value, saveResult.Value.Serialize());
        }
        _dirtySet.Clear();

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

    internal void RegisterDirty(DurableObject durableObject) {
        _dirtySet.Add(durableObject);
    }
}
