using System.Buffers.Binary;
using Atelia.Data;
using Atelia.Rbf;
using Atelia.StateJournal.Internal;
using Atelia.StateJournal.Pools;

namespace Atelia.StateJournal;

/// <summary>
/// 一个已打开的对象图工作会话。
/// 每次 Commit 会把当前工作态 durable 化为一个新的落盘快照，由 <see cref="CommitTicket"/> 标识。
/// <see cref="_head"/> 持有当前已提交的快照状态（Id / ParentId / ObjectMap / GraphRoot），首次 Commit 前为 null。
/// </summary>
public partial class Revision {
    private uint _headSegmentNumber;

    /// <summary>
    /// 当前活跃的 ObjectMap，始终占据 pool slot 0。
    /// 首次 Commit 前为空 dict；Commit 后与 <see cref="CommitSnapshot.ObjectMap"/> 是同一实例。
    /// </summary>
    private DurableDict<uint, ulong> _objectMap;
    private GcPool<DurableObject> _pool;

    /// <summary>
    /// Per-Revision symbol durable mirror，占据 pool slot 1。
    /// 运行时真源是 <see cref="_symbolPool"/>；
    /// 本镜像负责承接增量落盘与后续 diff/rebase 复用。
    /// </summary>
    private DurableDict<uint, InlineString> _symbolMirror;
    /// <summary>
    /// 运行时 intern 引擎 + Mark-Sweep GC 池。
    /// 对外通过 <see cref="InternSymbol"/> / <see cref="GetSymbol"/> 提供 string ↔ SymbolId 转换。
    /// </summary>
    private StringPool _symbolPool;

    /// <summary>最近一次成功 Commit 的快照。首次 Commit 前为 null。</summary>
    private CommitSnapshot? _head;

    public CommitTicket HeadId => _head?.Id ?? default;
    public CommitTicket HeadParentId => _head?.ParentId ?? default;
    /// <summary>最近一次 Commit 时使用的 GraphRoot。首次 Commit 前为 null。Open 后自动从 TailMeta 恢复。</summary>
    public DurableObject? GraphRoot => _head?.GraphRoot;
    internal uint HeadSegmentNumber => _headSegmentNumber;

    /// <summary>
    /// 把 <see cref="GraphRoot"/> 取为指定的 <typeparamref name="T"/>。
    /// </summary>
    /// <remarks>
    /// 失败语义：
    /// <list type="bullet">
    ///   <item>unborn branch（<c>GraphRoot is null</c>）→ <see cref="SjStateError"/>。</item>
    ///   <item><c>GraphRoot</c> 实际类型不可赋值给 <typeparamref name="T"/> → <see cref="SjStateError"/>。</item>
    /// </list>
    /// </remarks>
    public AteliaResult<T> GetGraphRoot<T>() where T : DurableObject {
        var root = _head?.GraphRoot;
        if (root is null) {
            return new SjStateError(
                "Branch is unborn — no graph root committed yet.",
                RecoveryHint: "Commit a root object first or check GraphRoot is null before calling."
            );
        }
        if (root is not T typed) {
            return new SjStateError(
                $"GraphRoot is of type {root.GetType()} (Kind={root.Kind}), requested as {typeof(T)}.",
                RecoveryHint: "Use the type that was committed as the graph root."
            );
        }
        return typed;
    }

    /// <summary>此 Revision 所属的 branch 名称。由 Repository 在绑定时设置一次，之后不可更改。</summary>
    internal string? BranchName {
        get => field;
        set {
            if (field is not null) { throw new InvalidOperationException($"BranchName is already set to '{field}' and cannot be changed."); }
            ArgumentNullException.ThrowIfNull(value);
            field = value;
        }
    }



    /// <summary>从头创建一个新的 root commit（未 Commit 前 _head 为 null）。</summary>
    internal Revision(uint boundSegmentNumber) {
        ArgumentOutOfRangeException.ThrowIfZero(boundSegmentNumber);
        _headSegmentNumber = boundSegmentNumber;
        _objectMap = Durable.Dict<uint, ulong>();
        _pool = new GcPool<DurableObject>();
        _pool.Store(_objectMap); // slot 0 = ObjectMap

        _symbolMirror = Durable.Dict<uint, InlineString>();
        _pool.Store(_symbolMirror); // slot 1 = SymbolTable
        _symbolPool = new StringPool();
    }

    /// <summary>从持久化数据全量加载 commit（内部构造函数，_head 由 Open 设置）。</summary>
    private Revision(
        uint boundSegmentNumber,
        DurableDict<uint, ulong> objectMap,
        GcPool<DurableObject> pool,
        DurableDict<uint, InlineString> symbolMirror,
        StringPool symbolPool
    ) {
        ArgumentOutOfRangeException.ThrowIfZero(boundSegmentNumber);
        _headSegmentNumber = boundSegmentNumber;
        _objectMap = objectMap;
        _pool = pool;
        _symbolMirror = symbolMirror;
        _symbolPool = symbolPool;
    }

    /// <summary>从 RBF 文件打开指定 CommitTicket 对应的 commit，全量加载所有对象到 GcPool。</summary>
    /// <param name="id">commit 的标识（内含 ObjectMap 帧的 SizedPtr）。</param>
    /// <param name="file">RBF 文件。</param>
    /// <param name="segmentNumber">此 commit 所在的 segment number。</param>
    internal static AteliaResult<Revision> Open(CommitTicket id, IRbfFile file, uint segmentNumber) {
        var loadResult = VersionChain.LoadFull(
            file, id.Ticket,
            expectUsage: FrameUsage.ObjectMap,
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

        CommitTicket parentId = new CommitTicket(result.HeadParentTicket);
        byte[] tailMeta = result.HeadTailMeta;

        // 从 TailMeta 读取 GraphRoot + SymbolTable 信息
        // 布局：[0..3] GraphRoot.LocalId.Value (uint LE)
        //        [4..7] SymbolTable slot packed (uint LE)
        if (tailMeta.Length < 8) {
            return new SjCorruptionError(
                $"TailMeta length {tailMeta.Length} < 8.",
                RecoveryHint: "This commit uses an unsupported or corrupted TailMeta format."
            );
        }
        uint rootIdValue = BinaryPrimitives.ReadUInt32LittleEndian(tailMeta);
        if (rootIdValue == 0) {
            return new SjCorruptionError(
                "Invalid GraphRoot LocalId 0 in TailMeta.",
                RecoveryHint: "Data corruption in TailMeta."
            );
        }

        uint symbolTablePacked = BinaryPrimitives.ReadUInt32LittleEndian(tailMeta.AsSpan(4));
        uint expectedSymbolTablePacked = new SlotHandle(0, 1).Packed;
        if (symbolTablePacked != expectedSymbolTablePacked) {
            return new SjCorruptionError(
                $"Unexpected SymbolTable LocalId {symbolTablePacked}; expected fixed slot {expectedSymbolTablePacked}.",
                RecoveryHint: "This commit uses an unsupported or corrupted SymbolTable layout."
            );
        }

        // 先加载 SymbolTable，重建 string decode 上下文，再加载用户对象。
        if (objectMap.Get(symbolTablePacked, out ulong serializedSymbolPtr) != GetIssue.None) {
            return new SjCorruptionError(
                $"SymbolTable key {symbolTablePacked} could not be read from ObjectMap.",
                RecoveryHint: "Data corruption in ObjectMap."
            );
        }
        SizedPtr symbolTableTicket = SizedPtr.Deserialize(serializedSymbolPtr);
        var symbolTableLoad = VersionChain.Load(file, symbolTableTicket);
        if (symbolTableLoad.IsFailure) { return symbolTableLoad.Error!; }
        if (symbolTableLoad.Value is not DurableDict<uint, InlineString> symbolTable) {
            return new SjCorruptionError(
                $"SymbolTable frame at key {symbolTablePacked} resolved to unexpected type: {symbolTableLoad.Value!.GetType()}.",
                RecoveryHint: "Expected DurableDict<uint, InlineString>."
            );
        }

        var symbolEntries = new (SlotHandle, string)[symbolTable.Count];
        int si = 0;
        foreach (uint key in symbolTable.Keys) {
            if (symbolTable.Get(key, out InlineString inlineStr) != GetIssue.None) {
                return new SjCorruptionError(
                    $"SymbolTable key {key} could not be read.",
                    RecoveryHint: "Data corruption in SymbolTable."
                );
            }
            symbolEntries[si++] = (new SlotHandle(key), inlineStr.ToString());
        }
        StringPool symbolPool = StringPool.Rebuild(symbolEntries);

        // 全量加载：遍历 ObjectMap 所有 entries，从 RBF 加载对象。
        // 为 ObjectMap（slot 0）和 SymbolTable（slot 1）预留位置。
        const int reservedSlots = 2;
        var entries = new (SlotHandle, DurableObject)[objectMap.Count + reservedSlots];
        entries[0] = (new SlotHandle(1, 0), objectMap); // slot 0 = ObjectMap
        entries[1] = (new SlotHandle(symbolTablePacked), symbolTable);

        int i = reservedSlots; // user objects start after reserved slots
        foreach (uint key in objectMap.Keys) {
            var handle = new SlotHandle(key); // LocalId.Value == SlotHandle.Packed
            if (key == symbolTablePacked) { continue; }
            if (objectMap.Get(key, out ulong serializedPtr) != GetIssue.None) {
                return new SjCorruptionError(
                    $"ObjectMap key {key} could not be read.",
                    RecoveryHint: "Data corruption in ObjectMap."
                );
            }
            SizedPtr ticket = SizedPtr.Deserialize(serializedPtr);
            var objLoad = VersionChain.Load(file, ticket, symbolPool: symbolPool);
            if (objLoad.IsFailure) { return objLoad.Error!; }
            entries[i++] = (handle, objLoad.Value!);
        }

        var pool = GcPool<DurableObject>.Rebuild(entries.AsSpan(0, i));

        var revision = new Revision(segmentNumber, objectMap, pool, symbolTable, symbolPool);

        // 绑定所有用户对象到 Revision（skip ObjectMap slot 0 和 SymbolTable）
        foreach (uint key in objectMap.Keys) {
            if (key == symbolTablePacked) { continue; } // skip SymbolTable
            var handle = new SlotHandle(key);
            var localId = LocalId.FromSlotHandle(handle);
            var obj = pool[handle];
            obj.Bind(revision, localId);
        }

        // 从 TailMeta 恢复 GraphRoot
        var rootHandle = new SlotHandle(rootIdValue);
        if (!pool.TryGetValue(rootHandle, out var rootObj)) {
            return new SjCorruptionError(
                $"Unknown GraphRoot LocalId {rootIdValue}.",
                RecoveryHint: "Data corruption in TailMeta."
            );
        }
        DurableObject? graphRoot = rootObj;

        revision._head = new CommitSnapshot(id, parentId, objectMap, graphRoot);

        var validateResult = revision.ValidateAllReferences();
        if (validateResult.IsFailure) { return validateResult.Error!; }

        return revision;
    }

    /// <summary>查找 RBF 文件中最新的 CommitTicket（逆向扫描最新的 ObjectMap 帧）。</summary>
    internal static AteliaResult<CommitTicket> FindLatestCommitTicket(IRbfFile file) {
        foreach (var info in file.ScanReverse()) {
            FrameTag tag = new(info.Tag);
            if (tag.Usage == FrameUsage.ObjectMap) { return new CommitTicket(info.Ticket); }
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

    #region Symbol API

    /// <summary>
    /// 将字符串 intern 到当前 Revision 的 Symbol Pool，返回 SymbolId。
    /// <c>null</c> 映射为 <see cref="SymbolId.Null"/>；非空字符串若已存在则去重返回已有 id。
    /// 运行时真源始终是 <see cref="_symbolPool"/>；durable mirror 保存在 <see cref="_symbolMirror"/>。
    /// </summary>
    internal SymbolId InternSymbol(string? value) {
        if (value is null) { return SymbolId.Null; }

        var handle = _symbolPool.Store(value);
        return SymbolId.FromSlotHandle(handle);
    }

    /// <summary>
    /// 在 commit / serialization 阶段将 string 编码为当前 Revision 的 SymbolId，并立即标记为可达。
    /// </summary>
    internal SymbolId InternReachableSymbol(string? value) {
        if (value is null) { return SymbolId.Null; }

        var handle = _symbolPool.Store(value);
        _symbolPool.TryMarkReachable(handle);
        return SymbolId.FromSlotHandle(handle);
    }

    /// <summary>按 SymbolId 读取 intern 字符串；<see cref="SymbolId.Null"/> 返回 <c>null</c>。</summary>
    internal string? GetSymbol(SymbolId id) => id.IsNull ? null : _symbolPool[id.ToSlotHandle()];

    /// <summary>
    /// 防御性读取：尝试按 SymbolId 读取 intern 字符串。
    /// handle 无效或 slot 未占用时返回 false，不抛异常。
    /// </summary>
    internal bool TryGetSymbol(SymbolId id, out string? value) {
        if (id.IsNull) {
            value = null;
            return true;
        }
        return _symbolPool.TryGetValue(id.ToSlotHandle(), out value!);
    }

    #endregion

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

    /// <summary>创建 TypedDeque 并绑定到当前 Revision。</summary>
    public DurableDeque<T> CreateDeque<T>() where T : notnull {
        var obj = Durable.Deque<T>();
        BindNewObject(obj);
        return obj;
    }

    /// <summary>创建 MixedDeque 并绑定到当前 Revision。</summary>
    public DurableDeque CreateDeque() {
        var obj = Durable.Deque();
        BindNewObject(obj);
        return obj;
    }

    /// <summary>创建 TypedOrderedDict 并绑定到当前 Revision。</summary>
    public DurableOrderedDict<TKey, TValue> CreateOrderedDict<TKey, TValue>() where TKey : notnull where TValue : notnull {
        var obj = Durable.OrderedDict<TKey, TValue>();
        BindNewObject(obj);
        return obj;
    }

    /// <summary>创建 MixedOrderedDict 并绑定到当前 Revision。</summary>
    public DurableOrderedDict<TKey> CreateOrderedDict<TKey>() where TKey : notnull {
        var obj = Durable.OrderedDict<TKey>();
        BindNewObject(obj);
        return obj;
    }

    /// <summary>创建 DurableText 并绑定到当前 Revision。</summary>
    public DurableText CreateText() {
        var obj = Durable.Text();
        BindNewObject(obj);
        return obj;
    }

    private void BindNewObject(DurableObject obj) {
        var handle = _pool.Store(obj);
        var id = LocalId.FromSlotHandle(handle);
        obj.Bind(this, id, DurableState.TransientDirty);
    }

    private void BindForkedObject(DurableObject obj) {
        var handle = _pool.Store(obj);
        var id = LocalId.FromSlotHandle(handle);
        obj.Bind(this, id, DurableState.Clean);
        obj.MarkPendingObjectMapRegistration();
    }

    internal TFork ForkCommittedAsMutable<TFork>(TFork source)
        where TFork : DurableObject {
        ArgumentNullException.ThrowIfNull(source);
        EnsureCanReference(source);
        if (!source.IsTracked) {
            throw new InvalidOperationException(
                $"Cannot fork LocalId={source.LocalId.Value}: source has no committed version chain yet."
            );
        }
        if (source.IsFrozen && source.ForceRebaseForFrozenSnapshot) {
            throw new InvalidOperationException(
                $"Cannot fork LocalId={source.LocalId.Value}: dirty frozen state has not been committed yet."
            );
        }

        var fork = source.ForkAsMutableCore();
        if (fork is not TFork typedFork) {
            throw new InvalidOperationException(
                $"Fork implementation returned {fork.GetType().Name}, expected {typeof(TFork).Name}."
            );
        }

        BindForkedObject(typedFork);
        if (typedFork.VersionObjectFlags != typedFork.CurrentObjectFlags) {
            typedFork.MarkMutabilityDirty();
        }
        return typedFork;
    }

    /// <summary>校验 DurableObject 可作为当前 Revision 的图引用被持有/写入。</summary>
    internal void EnsureCanReference(DurableObject obj) {
        ArgumentNullException.ThrowIfNull(obj);
        if (!obj.IsBoundTo(this)) {
            throw new InvalidOperationException(
                $"Cannot store a DurableObject from a different Revision or an unbound object. Object LocalId={obj.LocalId}, this Revision={HeadId}."
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
    /// <returns>返回显式的 <see cref="CommitOutcome"/>；failure 表示“可诊断但非 bug”的失败。</returns>
    /// <remarks>
    /// 提交流程分为三段：
    /// 1) WalkAndMark — 从 graphRoot DFS 遍历对象图，同时执行 GcPool Mark（用 mark bitmap 替代 HashSet 去重）；
    /// 2) Persist — 仅追加写盘，不改对象内存状态；
    /// 3) Finalize — 持久化成功后执行 Sweep GC 和状态更新。
    /// </remarks>
    internal partial AteliaResult<CommitOutcome> Commit(DurableObject graphRoot, IRbfFile targetFile);

    /// <summary>
    /// 将当前对象图的完整快照（全量 rebase）导出到 <paramref name="targetFile"/>，
    /// 不修改当前 Revision 的任何内部状态（_boundSegmentNumber / _objectMap / _head / 对象 HeadTicket 均不变）。
    /// </summary>
    /// <remarks>
    /// 导出到新文件时，会保留当前快照的“逻辑祖先”信息：
    /// 新写出的 rebase/ObjectMap 头帧仍会记录源 Revision 当时看到的 parent ticket。
    /// 该 ticket 可属于其他文件；它作为元数据保留给未来的跨文件组织/管理能力使用，
    /// 而不是要求目标文件在本地继续沿该 ticket 追溯读取。
    /// </remarks>
    /// <param name="graphRoot">对象图的根节点，必须属于当前 Revision。</param>
    /// <param name="targetFile">导出目标 RBF 文件。</param>
    /// <returns>新文件中的 CommitTicket，可用于 <see cref="Open"/> 独立打开。</returns>
    internal partial AteliaResult<CommitTicket> ExportTo(DurableObject graphRoot, IRbfFile targetFile);

    /// <summary>
    /// 将当前对象图的完整快照（全量 rebase）保存到 <paramref name="targetFile"/>，
    /// 并将当前 Revision 切换到新文件继续工作。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="ExportTo"/> 一样，SaveAs 到新文件时会保留当前快照的逻辑祖先信息，
    /// 即新文件中的 rebase/ObjectMap 头帧仍记录源 Revision 视角下的 parent ticket。
    /// 这允许未来在文件之间建立更高层的 commit 组织关系；当前读取路径只依赖 rebase 头帧自身，
    /// 不要求目标文件内存在该 parent ticket 对应的物理帧。
    /// </remarks>
    /// <param name="graphRoot">对象图的根节点，必须属于当前 Revision。</param>
    /// <param name="targetFile">另存为目标 RBF 文件。</param>
    /// <returns>与 <see cref="Commit"/> 相同的 <see cref="CommitOutcome"/>。</returns>
    internal partial AteliaResult<CommitOutcome> SaveAs(DurableObject graphRoot, IRbfFile targetFile);

    /// <summary>
    /// 由 Repository 在 branch CAS 成功后确认当前 Revision 已切换到新的 segment。
    /// </summary>
    internal void AcceptPersistedSegment(uint segmentNumber) {
        ArgumentOutOfRangeException.ThrowIfZero(segmentNumber);
        _headSegmentNumber = segmentNumber;
    }

    private readonly record struct CommitSnapshot(
        CommitTicket Id,
        CommitTicket ParentId,
        DurableDict<uint, ulong> ObjectMap,
        DurableObject? GraphRoot
    );

    /// <summary>全量校验 ObjectMap / pool / 用户对象之间的 DurableObject 引用完整性。发现悬空引用则失败。</summary>
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
            if (validator.Error is not null) { return validator.Error; }
        }
        return true;
    }
}
