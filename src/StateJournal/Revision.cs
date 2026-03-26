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
    /// Per-Revision Symbol Table：durable mirror，占据 pool slot 1。
    /// 平时保留最近一次已 durable 化的镜像，用于后续 diff/rebase 复用；
    /// 仅在需要持久化前，才从 <see cref="_symbolPool"/> 统一 reconcile。
    /// </summary>
    private DurableDict<uint, InlineString> _symbolTable;
    /// <summary>
    /// 运行时 intern 引擎 + Mark-Sweep-Compact GC 池。
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

        _symbolTable = Durable.Dict<uint, InlineString>();
        _pool.Store(_symbolTable); // slot 1 = SymbolTable
        _symbolPool = new StringPool();
    }

    /// <summary>从持久化数据全量加载 commit（内部构造函数，_head 由 Open 设置）。</summary>
    private Revision(
        uint boundSegmentNumber,
        DurableDict<uint, ulong> objectMap,
        GcPool<DurableObject> pool,
        DurableDict<uint, InlineString> symbolTable,
        StringPool symbolPool
    ) {
        ArgumentOutOfRangeException.ThrowIfZero(boundSegmentNumber);
        _headSegmentNumber = boundSegmentNumber;
        _objectMap = objectMap;
        _pool = pool;
        _symbolTable = symbolTable;
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

        // 全量加载：遍历 ObjectMap 所有 entries，从 RBF 加载对象。
        // 为 ObjectMap（slot 0）和 SymbolTable（slot 1）预留位置。
        const int reservedSlots = 2;
        var entries = new (SlotHandle, DurableObject)[objectMap.Count + reservedSlots];
        entries[0] = (new SlotHandle(1, 0), objectMap); // slot 0 = ObjectMap

        DurableDict<uint, InlineString>? symbolTable = null;
        int i = reservedSlots; // user objects start after reserved slots
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

            if (key == symbolTablePacked) {
                // SymbolTable 放到 reserved slot 1
                if (objLoad.Value is not DurableDict<uint, InlineString> st) {
                    return new SjCorruptionError(
                        $"SymbolTable frame at key {key} resolved to unexpected type: {objLoad.Value!.GetType()}.",
                        RecoveryHint: "Expected DurableDict<uint, InlineString>."
                    );
                }
                symbolTable = st;
                entries[1] = (handle, st);
            }
            else {
                entries[i++] = (handle, objLoad.Value!);
            }
        }

        if (symbolTable is null) {
            return new SjCorruptionError(
                $"SymbolTable key {symbolTablePacked} referenced in TailMeta but not found in ObjectMap.",
                RecoveryHint: "Data corruption in TailMeta or ObjectMap."
            );
        }

        var pool = GcPool<DurableObject>.Rebuild(entries.AsSpan(0, i));

        // 从 SymbolTable 重建 StringPool
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
    /// 运行时真源始终是 <see cref="_symbolPool"/>；<see cref="_symbolTable"/> 只在持久化前统一收敛。
    /// </summary>
    internal SymbolId InternSymbol(string? value) {
        if (value is null) { return SymbolId.Null; }

        var handle = _symbolPool.Store(value);
        return SymbolId.FromSlotHandle(handle);
    }

    /// <summary>按 SymbolId 读取 intern 字符串；<see cref="SymbolId.Null"/> 返回 <c>null</c>。</summary>
    internal string? GetSymbol(SymbolId id) => id.IsNull ? null : _symbolPool[id.ToSlotHandle()];

    /// <summary>
    /// 防御性读取：尝试按 SymbolId 读取 intern 字符串。
    /// handle 无效或 slot 未占用时返回 false，不抛异常。
    /// </summary>
    internal bool TryGetSymbol(SymbolId id, out string? value) {
        if (id.IsNull) { value = null; return true; }
        return _symbolPool.TryGetValue(id.ToSlotHandle(), out value!);
    }

    private void ReconcileSymbolTableFromPool(bool reachableOnly) {
        var liveSymbols = new Dictionary<uint, string>(_symbolPool.Count);
        var collector = new SymbolMirrorCollector(_symbolPool, liveSymbols, reachableOnly);
        _symbolPool.VisitEntries(ref collector);

        foreach (var (packed, value) in liveSymbols) {
            if (_symbolTable.Get(packed, out InlineString existing) == GetIssue.None && existing.Value == value) {
                continue;
            }
            _symbolTable.Upsert(packed, new InlineString(value));
        }

        List<uint> keysToRemove = [];
        foreach (uint key in _symbolTable.Keys) {
            if (!liveSymbols.ContainsKey(key)) { keysToRemove.Add(key); }
        }
        foreach (uint key in keysToRemove) {
            _symbolTable.Remove(key);
        }
    }

    private AteliaResult<bool> ValidateSymbolMirrorConsistency(bool reachableOnly) {
        var checker = new SymbolMirrorValidator(_symbolTable, _symbolPool, reachableOnly);
        _symbolPool.VisitEntries(ref checker);
        if (checker.Error is not null) { return checker.Error; }

        if (checker.ObservedCount != _symbolTable.Count) {
            return new SjCorruptionError(
                $"SymbolTable count {_symbolTable.Count} does not match symbol pool live count {checker.ObservedCount}.",
                RecoveryHint: "Symbol mirror is inconsistent with the runtime symbol pool."
            );
        }

        return true;
    }

    #endregion

    private readonly struct SymbolMirrorCollector(
        StringPool symbolPool,
        Dictionary<uint, string> liveSymbols,
        bool reachableOnly) : StringPool.IEntryVisitor {
        public void Visit(SlotHandle handle, string value) {
            if (reachableOnly && !symbolPool.IsMarkedReachable(handle)) { return; }
            liveSymbols[handle.Packed] = value;
        }
    }

    private ref struct SymbolMirrorValidator(
        DurableDict<uint, InlineString> symbolTable,
        StringPool symbolPool,
        bool reachableOnly) : StringPool.IEntryVisitor {
        public AteliaError? Error { get; private set; }
        public int ObservedCount { get; private set; }

        public void Visit(SlotHandle handle, string value) {
            if (Error is not null) { return; }
            if (reachableOnly && !symbolPool.IsMarkedReachable(handle)) { return; }

            ObservedCount++;
            uint packed = handle.Packed;
            if (symbolTable.Get(packed, out InlineString inline) != GetIssue.None) {
                Error = new SjCorruptionError(
                    $"SymbolTable is missing runtime symbol entry {packed}.",
                    RecoveryHint: "Symbol mirror is inconsistent with the runtime symbol pool."
                );
                return;
            }

            if (inline.Value != value) {
                Error = new SjCorruptionError(
                    $"SymbolTable value mismatch for symbol {packed}.",
                    RecoveryHint: "Symbol mirror is inconsistent with the runtime symbol pool."
                );
            }
        }
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
    /// <returns>
    /// 返回显式的 <see cref="CommitOutcome"/>，区分：
    /// - 仅完成 primary commit；
    /// - primary commit + compaction follow-up 全部成功；
    /// - primary commit 成功，但 compaction 的后续持久化受外部不可控因素影响而失败，且内存 compaction 已回滚到 primary commit 对齐状态。
    ///
    /// 目标语义下，failure 表示“可诊断但非 bug”的失败；
    /// compaction 的纯内存 apply / rollback 若违反内部不变量，应直接抛异常 fail-fast，而不折叠为 <see cref="AteliaError"/>。
    /// </returns>
    /// <remarks>
    /// 提交流程分为三段：
    /// A) Primary Commit（三阶段）：
    /// 1) WalkAndMark — 从 graphRoot DFS 遍历对象图，同时执行 GcPool Mark（用 mark bitmap 替代 HashSet 去重）；
    /// 2) Persist — 仅追加写盘，不改对象内存状态；
    /// 3) Finalize — 持久化成功后执行 Sweep GC 和状态更新。
    /// B) Compaction Apply（可选）：
    /// - 若触发 compaction，在 primary commit 产出的同一批 live objects 上执行 MoveSlot/Rebind/引用重写。
    /// C) Follow-up Persist（可选）：
    /// - durable 化 compaction 造成的脏变更；
    /// - 复用 primary commit 产出的 live objects，不再重新 WalkAndMark/Sweep。
    ///
    /// 错误分类目标：
    /// - primary commit 中来自对象图状态校验、RBF I/O、文件句柄状态等“外部/宿主环境”问题，返回 <see cref="AteliaError"/>；
    /// - compaction follow-up persist 的外部失败，在 primary 已 durable 的前提下返回带 issue 的结果；
    /// - compaction apply / rollback 的内部不变量破坏属于 bug，应 fail-fast。
    ///
    /// 因此 Commit 具备“primary 先提交，再尝试 compaction”的语义；若 follow-up persist 失败但回滚成功，则返回成功结果，并显式标记为
    /// <see cref="CommitCompletion.CompactionRolledBack"/>。
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
    /// <returns>与 <see cref="Commit"/> 相同的 <see cref="CommitOutcome"/>，但不触发 Compaction。</returns>
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

    /// <summary>全量校验 ObjectMap / pool / 用户对象引用完整性。发现悬空引用则失败。</summary>
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
            var validator = new ReferenceValidationVisitor(_pool, _symbolPool, parentId);
            parentObj.AcceptChildRefVisitor(ref validator);
            if (validator.Error is not null) { return validator.Error; }
        }
        return true;
    }
}
