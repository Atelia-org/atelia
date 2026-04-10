using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using Atelia.StateJournal.Internal;
using Atelia.StateJournal.Serialization;

namespace Atelia.StateJournal.NodeContainers;

/// <summary>
/// 基于 <see cref="LeafChainStore{TKey, TValue, KHelper, VHelper}"/> 的跳表有序字典核心。
/// <list type="bullet">
///   <item>叶链存储有序 KV 对，参与序列化</item>
///   <item>索引塔纯内存态，从叶链确定性重建</item>
/// </list>
/// </summary>
internal struct SkipListCore<TKey, TValue, KHelper, VHelper>
    where TKey : notnull
    where TValue : notnull
    where KHelper : unmanaged, ITypeHelper<TKey>
    where VHelper : unmanaged, ITypeHelper<TValue> {

    private const int MaxTowerHeight = 16;

    private LeafChainStore<TKey, TValue, KHelper, VHelper> _arena;
    private LeafHandle _head;
    private LeafHandle _committedHead;
    private int _count;
    private int _committedCount;

    // 索引塔（纯内存态）
    private TowerEntry[] _tower;
    private int _towerCount;
    private int _towerHeight;
    private int[] _levelHeads; // [level] → tower index of first entry at that level, -1 = empty
    private int _towerFreeHead; // free list head, -1 = empty; 用 NextInLevel 串联

    private struct TowerEntry {
        /// <summary>该塔节点对应的叶节点物理 index（RebuildIndex 期间稳定）。</summary>
        public int LeafIndex;
        /// <summary>该层中的下一个塔节点索引（-1 = 末尾）。</summary>
        public int NextInLevel;
        /// <summary>下一层中对应同一 key 的塔节点索引（-1 = 底层以下，即直达叶链）。</summary>
        public int Down;
    }

    /// <summary>
    /// Upsert 定位结果：要么命中现有节点，要么已完成新节点插入。
    /// value 更新语义由调用方按物理槽位决定（SetValueByIndex 或 PrepareValueSlotForUpdate）。
    /// </summary>
    private struct UpsertLocateResult {
        public bool Existed;
        public int PhysicalIndex;
    }

    public int Count => _count;
    internal int LeafNodeCount => _arena.CurrentNodeCount;
    internal int CommittedLeafNodeCount => _arena.CommittedNodeCount;
    internal int TowerHeight => _towerHeight;
    internal uint HeadSequence => _head.Sequence;

    public SkipListCore() {
        _arena = new();
        _head = default;
        _committedHead = default;
        _count = 0;
        _committedCount = 0;
        _tower = [];
        _towerCount = 0;
        _towerHeight = 0;
        _towerFreeHead = -1;
        _levelHeads = new int[MaxTowerHeight];
        _levelHeads.AsSpan().Fill(-1);
    }

    #region Public API

    public bool TryGet(TKey key, out TValue? value) {
        if (_head.IsNull) {
            value = default;
            return false;
        }

        var (headKey, headValue) = _arena.GetEntry(ref _head);
        int cmp = KHelper.Compare(headKey, key);
        if (cmp == 0) {
            value = headValue;
            return true;
        }
        if (cmp > 0) {
            value = default;
            return false;
        }

        LeafHandle pred = FindLeafPredecessor(key);
        if (pred.IsNull) {
            value = default;
            return false;
        }
        LeafHandle next = _arena.GetNext(ref pred);
        if (next.IsNotNull) {
            var (nextKey, nextValue) = _arena.GetEntry(ref next);
            if (KHelper.Compare(nextKey, key) == 0) {
                value = nextValue;
                return true;
            }
        }
        value = default;
        return false;
    }

    /// <summary>插入或更新。返回 true 表示新插入，false 表示更新已有 key。</summary>
    public bool Upsert(TKey key, TValue value) {
        UpsertLocateResult located = LocateOrInsertNode(key, value);
        if (!located.Existed) { return true; }

        _arena.SetValueByIndex(located.PhysicalIndex, value);
        return false;
    }

    /// <summary>
    /// Upsert 变体：返回值槽的引用，供调用方通过 <c>UpdateOrInit</c> 原位更新。
    /// <list type="bullet">
    ///   <item>已存在的 key → committed 原值已保存（provisional capture）但未标记 dirty，<paramref name="existed"/> = true</item>
    ///   <item>新 key → 已分配节点（值为 <c>default</c>），<paramref name="existed"/> = false</item>
    /// </list>
    /// 调用方在确认值确实改变后，须调用 <see cref="ConfirmValueDirty"/> 标记 dirty。
    /// 若值未变且 <paramref name="capturedNow"/> 为 true，应调用 <see cref="CancelPreparedValueUpdate"/> 回滚。
    /// 调用方在返回的 ref 上完成赋值前不得再调用本结构的任何变更方法。
    /// </summary>
    public ref TValue UpsertGetValueRef(TKey key, out bool existed, out int physicalIndex, out bool capturedNow) {
        UpsertLocateResult located = LocateOrInsertNode(key, default!);
        existed = located.Existed;
        physicalIndex = located.PhysicalIndex;
        return ref _arena.PrepareValueSlotForUpdate(physicalIndex, out capturedNow);
    }

    /// <summary>配合 <see cref="UpsertGetValueRef"/> 使用：确认值已改变，标记物理槽位 dirty。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ConfirmValueDirty(int physicalIndex) => _arena.ConfirmValueDirty(physicalIndex);

    /// <summary>配合 <see cref="UpsertGetValueRef"/> 使用：回滚刚创建的 provisional capture（值未变时）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CancelPreparedValueUpdate(int physicalIndex) => _arena.CancelPreparedValueUpdate(physicalIndex);

    public bool Remove(TKey key) => TryRemove(key, out _);

    public bool TryRemove(TKey key, out TValue? value) {
        if (_head.IsNull) { value = default; return false; }

        var (headKey, headValue) = _arena.GetEntry(ref _head);
        int headCmp = KHelper.Compare(headKey, key);
        if (headCmp == 0) {
            // 删除头节点（towerUpdate 全 -1 = 所有层前驱即 _levelHeads）
            value = headValue;
            _head = _arena.GetNext(ref _head);
            _count--;
            Span<int> headUpdate = stackalloc int[MaxTowerHeight];
            headUpdate.Fill(-1);
            RemoveFromTower(key, headUpdate);
            return true;
        }
        if (headCmp > 0) { value = default; return false; }

        Span<int> towerUpdate = stackalloc int[MaxTowerHeight];
        towerUpdate.Fill(-1);
        LeafHandle pred = FindLeafPredecessorAndTowerUpdate(key, towerUpdate);
        if (pred.IsNull) { value = default; return false; }

        LeafHandle candidate = _arena.GetNext(ref pred);
        if (candidate.IsNull) { value = default; return false; }

        var (candKey, candValue) = _arena.GetEntry(ref candidate);
        if (KHelper.Compare(candKey, key) != 0) { value = default; return false; }

        // 重链接：pred → candidate.next
        value = candValue;
        uint candidateNextSeq = _arena.GetNextSequence(ref candidate);
        _arena.SetNextSequence(ref pred, candidateNextSeq);
        _count--;
        RemoveFromTower(key, towerUpdate);
        return true;
    }

    /// <summary>从 <paramref name="minInclusive"/> 开始按升序读取最多 <paramref name="maxCount"/> 个 KV 对。</summary>
    public List<KeyValuePair<TKey, TValue>> ReadAscendingFrom(TKey minInclusive, int maxCount) {
        ArgumentOutOfRangeException.ThrowIfNegative(maxCount);
        var result = new List<KeyValuePair<TKey, TValue>>(Math.Min(maxCount, _count));
        if (maxCount <= 0 || _head.IsNull) { return result; }

        LeafHandle leaf = FindLowerBoundLeaf(minInclusive);
        while (leaf.IsNotNull && result.Count < maxCount) {
            var (k, v) = _arena.GetEntry(ref leaf);
            result.Add(new(k, v));
            leaf = _arena.GetNext(ref leaf);
        }
        return result;
    }

    /// <summary>从 <paramref name="minInclusive"/> 开始按升序读取最多 <paramref name="maxCount"/> 个 key。</summary>
    public List<TKey> ReadKeysAscendingFrom(TKey minInclusive, int maxCount) {
        ArgumentOutOfRangeException.ThrowIfNegative(maxCount);
        var result = new List<TKey>(Math.Min(maxCount, _count));
        if (maxCount <= 0 || _head.IsNull) { return result; }

        LeafHandle leaf = FindLowerBoundLeaf(minInclusive);
        while (leaf.IsNotNull && result.Count < maxCount) {
            result.Add(_arena.GetKey(ref leaf));
            leaf = _arena.GetNext(ref leaf);
        }
        return result;
    }

    /// <summary>按升序返回所有 key。</summary>
    public List<TKey> GetAllKeys() {
        var keys = new List<TKey>(_count);
        LeafHandle current = _head;
        while (current.IsNotNull) {
            keys.Add(_arena.GetKey(ref current));
            current = _arena.GetNext(ref current);
        }
        return keys;
    }

    /// <summary>按叶链顺序读取当前游标处的 KV 对。</summary>
    internal (TKey Key, TValue Value) GetEntry(ref LeafHandle handle) => _arena.GetEntry(ref handle);

    /// <summary>按叶链顺序前进到下一个节点。</summary>
    internal LeafHandle GetNext(ref LeafHandle handle) => _arena.GetNext(ref handle);

    /// <summary>遍历叶链中每个节点的 key 和 value，分别调用对应的 VisitChildRefs。</summary>
    internal void AcceptChildRefVisitor<TVisitor>(Revision revision, ref TVisitor visitor)
        where TVisitor : IChildRefVisitor, allows ref struct {
        if (!KHelper.NeedVisitChildRefs && !VHelper.NeedVisitChildRefs) { return; }
        LeafHandle cursor = _head;
        while (cursor.IsNotNull) {
            var (k, v) = _arena.GetEntry(ref cursor);
            if (KHelper.NeedVisitChildRefs) { KHelper.VisitChildRefs(k, revision, ref visitor); }
            if (VHelper.NeedVisitChildRefs) { VHelper.VisitChildRefs(v, revision, ref visitor); }
            cursor = _arena.GetNext(ref cursor);
        }
    }

    /// <summary>遍历叶链中每个节点，校验加载后的 placeholder 是否已全部解析。</summary>
    internal AteliaError? ValidateReconstructed(
        LoadPlaceholderTracker tracker, string ownerName, bool validateValues = true
    ) {
        bool needKeys = KHelper.NeedValidateReconstructed;
        bool needValues = validateValues && VHelper.NeedValidateReconstructed;
        if (!needKeys && !needValues) { return null; }
        LeafHandle cursor = _head;
        while (cursor.IsNotNull) {
            var (k, v) = _arena.GetEntry(ref cursor);
            if (needKeys) {
                if (KHelper.ValidateReconstructed(k, tracker, ownerName) is { } e) { return e; }
            }
            if (needValues) {
                if (VHelper.ValidateReconstructed(v, tracker, ownerName) is { } e) { return e; }
            }
            cursor = _arena.GetNext(ref cursor);
        }
        return null;
    }

    internal LeafHandle Head => _head;

    public bool ContainsKey(TKey key) => TryGet(key, out _);

    #endregion

    #region Lifecycle

    public void Commit() {
        _arena.Commit();
        _arena.CollectCommitted(_head.Sequence);
        if (_arena.CommittedNodeCount != _count) {
            throw new InvalidOperationException(
                $"SkipList committed count mismatch after commit canonicalization: expected {_count}, actual {_arena.CommittedNodeCount}."
            );
        }
        _arena.SyncCurrentFromCommitted();
        _committedHead = _head;
        _committedHead.ClearCachedIndex();
        _head.ClearCachedIndex();
        _committedCount = _count;
        RebuildIndex();
    }

    public void Revert() {
        _arena.Revert();
        _head = _committedHead;
        _head.ClearCachedIndex();
        _count = _committedCount;
        RebuildIndex();
    }

    public bool HasChanges =>
        _count != _committedCount
        || _head.Sequence != _committedHead.Sequence
        || _arena.DirtyValueCount > 0
        || _arena.DirtyLinkCount > 0;

    #endregion

    #region Serialization

    /// <summary>增量序列化当前变更。包含 head/count 元信息 + arena delta。</summary>
    public void WriteDeltify(BinaryDiffWriter writer, DiffWriteContext context) {
        writer.BareUInt32(_head.Sequence, asKey: false);
        writer.WriteCount(_count);
        _arena.WriteDeltify(writer, context);
    }

    /// <summary>全量序列化当前可见状态（仅写可达节点）。</summary>
    public void WriteRebase(BinaryDiffWriter writer, DiffWriteContext context) {
        writer.BareUInt32(_head.Sequence, asKey: false);
        writer.WriteCount(_count);
        _arena.WriteRebaseLiveOnly(writer, context, _head.Sequence);
    }

    /// <summary>
    /// 应用增量帧，并按新 committed head 将叶链收敛为规范 committed 形态。
    /// 调用后仍需 <see cref="SyncCurrentFromCommitted"/> 以建立 current 视图与塔索引。
    /// </summary>
    public void ApplyDelta(ref BinaryDiffReader reader) {
        _committedHead = new(reader.BareUInt32(asKey: false));
        _committedCount = reader.ReadCount();
        _arena.ApplyDelta(ref reader);
        _arena.CollectCommitted(_committedHead.Sequence);
        if (_arena.CommittedNodeCount != _committedCount) {
            throw new InvalidDataException(
                $"SkipList committed count mismatch after load canonicalization: expected {_committedCount}, actual {_arena.CommittedNodeCount}."
            );
        }
    }

    public void SyncCurrentFromCommitted() {
        _arena.SyncCurrentFromCommitted();
        _head = _committedHead;
        _head.ClearCachedIndex();
        _count = _committedCount;
        RebuildIndex();
    }

    /// <summary>估算开销输入：live 条目数（逻辑 key 计数）。与 DeltifyCount 单位不严格对齐，ShouldRebase 的启发式已容忍此差异。</summary>
    public int RebaseCount => _count;
    /// <summary>估算开销输入：物理变更数（dirty links + dirty values + new nodes）。</summary>
    public int DeltifyCount => _arena.DirtyLinkCount + _arena.DirtyValueCount + (_arena.CurrentNodeCount - _arena.CommittedNodeCount);

    #endregion

    #region Index (Tower) — 纯内存态

    /// <summary>
    /// 从叶链 O(n) 全量重建索引塔。仅在 arena 压缩后调用（Commit/Revert/SyncCurrentFromCommitted），
    /// 因为压缩会使物理 index 变化。常规 Upsert/Remove 走 <see cref="InsertIntoTower"/>/
    /// <see cref="RemoveFromTower"/> 增量维护。
    /// </summary>
    private void RebuildIndex() {
        _towerHeight = 0;
        _towerCount = 0;
        _towerFreeHead = -1;
        _levelHeads.AsSpan().Fill(-1);

        if (_head.IsNull) { return; }

        Span<int> lastAtLevel = stackalloc int[MaxTowerHeight];
        lastAtLevel.Fill(-1);

        LeafHandle cursor = _head;
        while (cursor.IsNotNull) {
            TKey key = _arena.GetKey(ref cursor);
            int height = DeterministicTowerHeight(key);
            int physicalIndex = cursor.CachedIndex;

            if (height > _towerHeight) { _towerHeight = height; }

            int downIndex = -1;
            for (int level = 0; level < height; level++) {
                int towerIndex = AllocTowerSlot();
                ref TowerEntry entry = ref _tower[towerIndex];
                entry.LeafIndex = physicalIndex;
                entry.NextInLevel = -1;
                entry.Down = downIndex;

                if (lastAtLevel[level] >= 0) {
                    _tower[lastAtLevel[level]].NextInLevel = towerIndex;
                }
                else {
                    _levelHeads[level] = towerIndex;
                }
                lastAtLevel[level] = towerIndex;
                downIndex = towerIndex;
            }

            cursor = _arena.GetNext(ref cursor);
        }
    }

    /// <summary>
    /// 基于 key 哈希的塔高分配（trailing ones 计数）。
    /// 概率：height=0 → 50%, height=1 → 25%, height=2 → 12.5%, ...
    /// 依赖 <c>GetHashCode()</c>（对 string 等跨进程不稳定），但塔不持久化，每次 load 后从叶链重建。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int DeterministicTowerHeight(TKey key) {
        uint hash = (uint)(key.GetHashCode() * 2654435761u);
        hash ^= hash >> 16;
        int height = BitOperations.TrailingZeroCount(~hash);
        return height > MaxTowerHeight ? MaxTowerHeight : height;
    }

    private void EnsureTowerCapacity(int required) {
        if (_tower is not null && _tower.Length >= required) { return; }
        int newCap = _tower is null || _tower.Length == 0 ? 8 : _tower.Length;
        while (newCap < required) { newCap *= 2; }
        Array.Resize(ref _tower, newCap);
    }

    /// <summary>从 free list 或尾部分配一个塔槽位。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int AllocTowerSlot() {
        if (_towerFreeHead >= 0) {
            int idx = _towerFreeHead;
            _towerFreeHead = _tower[idx].NextInLevel;
            return idx;
        }
        EnsureTowerCapacity(_towerCount + 1);
        return _towerCount++;
    }

    /// <summary>将塔槽位归还到 free list。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void FreeTowerSlot(int idx) {
        _tower[idx] = default;
        _tower[idx].NextInLevel = _towerFreeHead;
        _towerFreeHead = idx;
    }

    #endregion

    #region Incremental Tower Maintenance

    /// <summary>将新节点增量加入索引塔。O(log n) 期望时间。</summary>
    private void InsertIntoTower(TKey key, int leafPhysicalIndex, ReadOnlySpan<int> towerUpdate) {
        int height = DeterministicTowerHeight(key);
        if (height == 0) { return; }

        // 扩展塔高（_levelHeads 始终按 MaxTowerHeight 预分配，无需扩容）
        if (height > _towerHeight) {
            _towerHeight = height;
        }

        int downIndex = -1;
        for (int level = 0; level < height; level++) {
            int towerIdx = AllocTowerSlot();
            ref TowerEntry entry = ref _tower[towerIdx];
            entry.LeafIndex = leafPhysicalIndex;
            entry.Down = downIndex;

            int predIdx = towerUpdate[level];
            if (predIdx < 0) {
                entry.NextInLevel = _levelHeads[level];
                _levelHeads[level] = towerIdx;
            }
            else {
                entry.NextInLevel = _tower[predIdx].NextInLevel;
                _tower[predIdx].NextInLevel = towerIdx;
            }
            downIndex = towerIdx;
        }
    }

    /// <summary>从索引塔增量移除节点。O(log n) 期望时间。towerUpdate 全 -1 时等价于头删。</summary>
    private void RemoveFromTower(TKey key, ReadOnlySpan<int> towerUpdate) {
        for (int level = 0; level < _towerHeight; level++) {
            int predIdx = towerUpdate[level];
            int entryIdx = (predIdx >= 0) ? _tower[predIdx].NextInLevel : _levelHeads[level];
            if (entryIdx < 0) { continue; }

            TKey entryKey = _arena.GetKeyByIndex(_tower[entryIdx].LeafIndex);
            if (KHelper.Compare(entryKey, key) != 0) { continue; }

            if (predIdx >= 0) {
                _tower[predIdx].NextInLevel = _tower[entryIdx].NextInLevel;
            }
            else {
                _levelHeads[level] = _tower[entryIdx].NextInLevel;
            }
            FreeTowerSlot(entryIdx);
        }
        ShrinkTowerHeight();
    }

    private void ShrinkTowerHeight() {
        while (_towerHeight > 0 && _levelHeads[_towerHeight - 1] < 0) {
            _towerHeight--;
        }
    }

    #endregion

    #region Internal Search

    /// <summary>
    /// 定位 key 对应节点；若不存在则完成叶链与塔索引插入。
    /// 不处理 value 更新生命周期，供 Upsert / UpsertGetValueRef 复用。
    /// </summary>
    private UpsertLocateResult LocateOrInsertNode(TKey key, TValue insertedValue) {
        if (_head.IsNull) {
            _head = _arena.AllocNode(key, insertedValue);
            int newIndex = _arena.CurrentNodeCount - 1;
            _count = 1;
            Span<int> emptyUpdate = stackalloc int[MaxTowerHeight];
            emptyUpdate.Fill(-1);
            InsertIntoTower(key, newIndex, emptyUpdate);
            _head.CachedIndex = newIndex;
            return new UpsertLocateResult { Existed = false, PhysicalIndex = newIndex };
        }

        TKey headKey = _arena.GetKey(ref _head);
        int headCmp = KHelper.Compare(headKey, key);
        if (headCmp == 0) {
            int headIndex = _arena.ResolveIndex(ref _head);
            return new UpsertLocateResult { Existed = true, PhysicalIndex = headIndex };
        }
        if (headCmp > 0) {
            LeafHandle oldHead = _head;
            _head = _arena.AllocNode(key, insertedValue, oldHead.Sequence);
            int newIndex = _arena.CurrentNodeCount - 1;
            _count++;
            Span<int> emptyUpdate = stackalloc int[MaxTowerHeight];
            emptyUpdate.Fill(-1);
            InsertIntoTower(key, newIndex, emptyUpdate);
            _head.CachedIndex = newIndex;
            return new UpsertLocateResult { Existed = false, PhysicalIndex = newIndex };
        }

        Span<int> towerUpdate = stackalloc int[MaxTowerHeight];
        towerUpdate.Fill(-1);
        LeafHandle pred = FindLeafPredecessorAndTowerUpdate(key, towerUpdate);
        Debug.Assert(pred.IsNotNull);

        LeafHandle successor = _arena.GetNext(ref pred);
        if (successor.IsNotNull) {
            TKey succKey = _arena.GetKey(ref successor);
            if (KHelper.Compare(succKey, key) == 0) {
                int succIndex = _arena.ResolveIndex(ref successor);
                return new UpsertLocateResult { Existed = true, PhysicalIndex = succIndex };
            }
        }

        uint predNextSeq = _arena.GetNextSequence(ref pred);
        LeafHandle insertedHandle = _arena.AllocNode(key, insertedValue, predNextSeq);
        int insertedIndex = _arena.CurrentNodeCount - 1;
        insertedHandle.CachedIndex = insertedIndex;
        _arena.SetNext(ref pred, insertedHandle);
        _count++;
        InsertIntoTower(key, insertedIndex, towerUpdate);
        return new UpsertLocateResult { Existed = false, PhysicalIndex = insertedIndex };
    }

    /// <summary>只读前驱查找。用于 TryGet、FindLowerBoundLeaf 等不修改塔的路径。</summary>
    private LeafHandle FindLeafPredecessor(TKey key)
        => FindLeafPredecessorCore(key, Span<int>.Empty);

    /// <summary>前驱查找 + 记录每层塔前驱。用于 Upsert/Remove 增量维护塔。调用前 towerUpdate 应已 Fill(-1)。</summary>
    private LeafHandle FindLeafPredecessorAndTowerUpdate(
        TKey key, Span<int> towerUpdate
    )
        => FindLeafPredecessorCore(key, towerUpdate);

    /// <summary>核心前驱查找。当 <paramref name="towerUpdate"/> 非空时，同时填充每层的塔前驱索引。</summary>
    private LeafHandle FindLeafPredecessorCore(
        TKey key, Span<int> towerUpdate
    ) {
        Debug.Assert(towerUpdate.IsEmpty || towerUpdate.Length >= MaxTowerHeight);
        if (_towerHeight == 0 || _head.IsNull) { return LinearFindPredecessor(key); }

        int predLeafIndex = -1;
        int currentPred = -1;

        for (int level = _towerHeight - 1; level >= 0; level--) {
            // 下降：从上层前驱的 Down 进入当前层
            if (currentPred >= 0) {
                currentPred = _tower[currentPred].Down;
            }

            // 从当前前驱的 NextInLevel（或 levelHead）开始扫描
            int entry = (currentPred >= 0)
                ? _tower[currentPred].NextInLevel
                : _levelHeads[level];

            while (entry >= 0) {
                TKey entryKey = _arena.GetKeyByIndex(_tower[entry].LeafIndex);
                if (KHelper.Compare(entryKey, key) < 0) {
                    currentPred = entry;
                    predLeafIndex = _tower[entry].LeafIndex;
                    entry = _tower[entry].NextInLevel;
                }
                else { break; }
            }

            if (!towerUpdate.IsEmpty) { towerUpdate[level] = currentPred; }
        }

        if (predLeafIndex < 0) { return LinearFindPredecessor(key); }

        // 从塔前驱开始线性扫描，跨越高度为 0 的叶节点，找到精确的叶前驱
        LeafHandle current = new(_arena.GetSequenceByIndex(predLeafIndex));
        current.CachedIndex = predLeafIndex;
        while (true) {
            LeafHandle next = _arena.GetNext(ref current);
            if (next.IsNull) { break; }
            if (KHelper.Compare(_arena.GetKey(ref next), key) >= 0) { break; }
            current = next;
        }
        return current;
    }

    /// <summary>无索引时的线性前驱查找。</summary>
    private LeafHandle LinearFindPredecessor(TKey key) {
        LeafHandle pred = default;
        LeafHandle cursor = _head;
        while (cursor.IsNotNull) {
            if (KHelper.Compare(_arena.GetKey(ref cursor), key) >= 0) { break; }
            pred = cursor;
            cursor = _arena.GetNext(ref cursor);
        }
        return pred;
    }

    /// <summary>找到 &gt;= minInclusive 的第一个叶节点 handle。</summary>
    private LeafHandle FindLowerBoundLeaf(TKey minInclusive) {
        if (_head.IsNull) { return default; }
        if (KHelper.Compare(_arena.GetKey(ref _head), minInclusive) >= 0) { return _head; }

        LeafHandle pred = FindLeafPredecessor(minInclusive);
        if (pred.IsNull) { return _head; }

        // pred < minInclusive, 所以 pred.next 是候选
        return _arena.GetNext(ref pred);
    }

    #endregion
}
