using System.Diagnostics;
using Atelia.StateJournal.Internal;
using Atelia.StateJournal.Serialization;

namespace Atelia.StateJournal.NodeContainers;

/// <summary>
/// 为叶链（singly-linked chain）优化的 KV Arena。
/// <list type="bullet">
///   <item>Key/Value 与 Next 指针分离存储，分别追踪脏状态</item>
///   <item>序列化时独立表达"仅链接变更"（极紧凑：2 × VarUInt32）与"仅 value 变更"</item>
///   <item>Arena 自身理解链表拓扑，可内部完成 mark-sweep GC（沿 Next 链标记可达节点）</item>
/// </list>
/// 适用于叶分离结构（SkipList 叶链、B+Tree 叶链等），索引/分支层由容器在内存中重建，不参与序列化。
/// </summary>
/// <remarks>
/// <para><b>Key 不可变契约</b>：Key 仅在 <see cref="AllocNode"/> 和 <see cref="ApplyDelta"/>（新增段）
/// 时设定，后续只允许改 Value 和 Next。此不变量使得 value-mutation delta 可省略 key，
/// 并保证有序容器的排序和塔索引增量维护假设不被破坏。</para>
/// <para><b>增量帧格式</b>（链接变更与 value 变更正交存储）：</para>
/// <code>
/// [VarUInt32] linkMutationCount          // committed 节点中仅 Next 指针变更
///   { [VarUInt32] seq, [VarUInt32] newNextSeq } × count
/// [VarUInt32] valueMutationCount         // committed 节点中仅 Value 变更（key 不可变，不序列化）
///   { [VarUInt32] seq, VHelper.Write(value) } × count
/// [VarUInt32] appendedCount              // 新增节点（含 Next + 完整 KV）
///   { [VarUInt32] seq, [VarUInt32] nextSeq, KHelper.Write(key) + VHelper.Write(value) } × count
/// </code>
/// <para>同一节点若 link 与 value 同时变更，会分别出现在两个段中。</para>
/// </remarks>
internal struct LeafChainStore<TKey, TValue, KHelper, VHelper>
    where TKey : notnull
    where TValue : notnull
    where KHelper : unmanaged, ITypeHelper<TKey>
    where VHelper : unmanaged, ITypeHelper<TValue> {

    private TKey[] _keys;
    private TValue[] _values;
    private uint[] _nextSequences;  // 0 = null (end of chain)
    private uint[] _sequences;      // persistent identity, kept sorted
    private int _committedCount;
    private int _currentCount;
    private uint _nextAllocSequence; // monotonic: only increases, never reuses

    #region Dirty Tracking
    private BitVector _dirtyValues;
    private BitVector _dirtyLinks;
    /// <summary>标记哪些 committed 节点的原值已被保存到 <see cref="_capturedOriginals"/>。
    /// 独立于 dirty 状态——节点可以"已 capture 但未 dirty"（prepare 后值未变时的中间态）。</summary>
    private BitVector _capturedNodes;
    private ListCore<CapturedOriginal> _capturedOriginals;

    private struct CapturedOriginal {
        public int Index;
        public uint NextSequence;
        public TValue Value;
    }

    private bool IsCaptured(int index) => _capturedNodes.TestBit(index);

    private void CaptureIfNeeded(int index) {
        Debug.Assert(index >= 0 && index < _committedCount);
        if (!IsCaptured(index)) {
            _capturedNodes.SetBit(index);
            _capturedOriginals.Add(
                new CapturedOriginal {
                    Index = index,
                    Value = _values[index],
                    NextSequence = _nextSequences[index]
                }
            );
        }
    }

    private void RevertDirty() {
        while (_capturedOriginals.TryPop(out var orig)) {
            if (VHelper.NeedRelease && _dirtyValues.TestBit(orig.Index)) {
                VHelper.ReleaseSlot(_values[orig.Index]);
            }
            _values[orig.Index] = orig.Value;
            _nextSequences[orig.Index] = orig.NextSequence;
        }
        _dirtyValues.Clear();
        _dirtyLinks.Clear();
        _capturedNodes.Clear();
    }

    private void CommitDirty() {
        if (VHelper.NeedRelease) {
            while (_capturedOriginals.TryPop(out var orig)) {
                if (_dirtyValues.TestBit(orig.Index)) {
                    VHelper.ReleaseSlot(orig.Value);
                }
            }
        }
        else {
            _capturedOriginals.Clear();
        }
        _dirtyValues.Clear();
        _dirtyLinks.Clear();
        _capturedNodes.Clear();
    }

    private bool HasActiveDirtyTracking =>
        _dirtyValues.PopCount != 0 || _dirtyLinks.PopCount != 0 || _capturedNodes.PopCount != 0;

    private void ThrowIfActiveDirtyTracking(string operation) {
        if (!HasActiveDirtyTracking) { return; }
        throw new InvalidOperationException(
            $"{operation} cannot compact committed slots while dirty tracking is active. " +
            "Serialize or finalize the dirty state first."
        );
    }
    #endregion

    internal int CurrentNodeCount => _currentCount;
    internal int CommittedNodeCount => _committedCount;
    internal int DirtyValueCount => _dirtyValues.PopCount;
    internal int DirtyLinkCount => _dirtyLinks.PopCount;
    private int AppendedCount => _currentCount - _committedCount;

    public LeafChainStore() {
        _keys = [];
        _values = [];
        _nextSequences = [];
        _sequences = [];
        _currentCount = 0;
        _committedCount = 0;
        _dirtyValues = new();
        _dirtyLinks = new();
        _capturedNodes = new();
        _capturedOriginals = new();
        ResetDirtyTracking();
    }

    private int FindIndex(uint sequence) =>
        Array.BinarySearch(_sequences, 0, _currentCount, sequence);

    private int FindCommittedIndex(uint sequence) =>
        Array.BinarySearch(_sequences, 0, _committedCount, sequence);

    private bool IsCommittedIndex(int index) {
        Debug.Assert(index >= 0);
        return index < _committedCount;
    }

    /// <summary>由 sequence 解析物理 index，同时维护 LeafHandle 的缓存。</summary>
    internal int ResolveIndex(ref LeafHandle handle) {
        if (handle.MissingCachedIndex) {
            int index = FindIndex(handle.Sequence);
            if (index < 0) { throw new KeyNotFoundException(); }
            handle.CachedIndex = index;
            return index;
        }
        else {
            int index = handle.CachedIndex;
            if ((uint)index >= (uint)_currentCount || _sequences[index] != handle.Sequence) {
                index = FindIndex(handle.Sequence);
                if (index < 0) { throw new KeyNotFoundException(); }
                handle.CachedIndex = index;
            }
            return index;
        }
    }

    #region Read
    public TKey GetKey(ref LeafHandle handle) => _keys[ResolveIndex(ref handle)];

    public TValue GetValue(ref LeafHandle handle) => _values[ResolveIndex(ref handle)];

    public (TKey Key, TValue Value) GetEntry(ref LeafHandle handle) {
        int index = ResolveIndex(ref handle);
        return (_keys[index], _values[index]);
    }

    /// <summary>按物理 index 直接访问 key，无 sequence 查找开销。仅用于塔索引等已知 index 稳定的场景。</summary>
    internal TKey GetKeyByIndex(int index) => _keys[index];

    /// <summary>按物理 index 直接访问 value。仅用于估算等只读路径。</summary>
    internal TValue GetValueByIndex(int index) => _values[index];

    /// <summary>按物理 index 直接获取 sequence。</summary>
    internal uint GetSequenceByIndex(int index) => _sequences[index];

    /// <summary>按物理 index 直接获取 next sequence。仅用于估算等只读路径。</summary>
    internal uint GetNextSequenceByIndex(int index) => _nextSequences[index];

    /// <summary>按升序枚举 dirty link 的 committed 物理 index。</summary>
    internal BitVector.OnesEnumerator EnumerateDirtyLinkIndices() => _dirtyLinks.Ones();

    /// <summary>按升序枚举 dirty value 的 committed 物理 index。</summary>
    internal BitVector.OnesEnumerator EnumerateDirtyValueIndices() => _dirtyValues.Ones();

    public LeafHandle GetNext(ref LeafHandle handle) {
        int index = ResolveIndex(ref handle);
        uint nextSeq = _nextSequences[index];
        if (nextSeq == 0) { return default; }
        var next = new LeafHandle(nextSeq);
        // Opportunistic: 如果下一个物理槽位恰好就是 nextSeq（GC 后或顺序插入时常见），
        // 直接填充 CachedIndex，避免后续的二分查找。
        int nextIndex = index + 1;
        if ((uint)nextIndex < (uint)_currentCount && _sequences[nextIndex] == nextSeq) {
            next.CachedIndex = nextIndex;
        }
        return next;
    }

    public uint GetNextSequence(ref LeafHandle handle) {
        int index = ResolveIndex(ref handle);
        return _nextSequences[index];
    }
    #endregion

    #region Write
    private void EnsureCapacity(int requiredCapacity) {
        int currentCapacity = _keys?.Length ?? 0;
        if (requiredCapacity <= currentCapacity) { return; }

        int newCapacity = currentCapacity == 0 ? 4 : currentCapacity;
        while (newCapacity < requiredCapacity) {
            newCapacity *= 2;
        }

        if (_keys is null) { _keys = new TKey[newCapacity]; }
        else { Array.Resize(ref _keys, newCapacity); }

        if (_values is null) { _values = new TValue[newCapacity]; }
        else { Array.Resize(ref _values, newCapacity); }

        if (_nextSequences is null) { _nextSequences = new uint[newCapacity]; }
        else { Array.Resize(ref _nextSequences, newCapacity); }

        if (_sequences is null) { _sequences = new uint[newCapacity]; }
        else { Array.Resize(ref _sequences, newCapacity); }
    }

    public LeafHandle AllocNode(TKey key, TValue value, uint nextSequence = 0) {
        int allocedIndex = _currentCount;
        EnsureCapacity(allocedIndex + 1);

        uint allocedSequence = _nextAllocSequence;
        if (allocedSequence < NodeHandleConstants.MinSequence) {
            allocedSequence = NodeHandleConstants.MinSequence;
        }
        _nextAllocSequence = allocedSequence + 1;

        _sequences[allocedIndex] = allocedSequence;
        _nextSequences[allocedIndex] = nextSequence;
        _keys[allocedIndex] = key;
        _values[allocedIndex] = value;

        ++_currentCount;
        return new(allocedSequence);
    }

    /// <summary>
    /// 更新节点的 value。自动处理 committed 节点的脏追踪和 NeedRelease 释放。
    /// </summary>
    /// <remarks>
    /// <para>对 committed 节点：首次修改时旧值已被 <see cref="CapturedOriginal"/> 持有，
    /// 不在此处释放（由 <see cref="CommitDirty"/> 或 <see cref="RevertDirty"/> 管理）。
    /// 仅当同一节点重复 SetValue 时，中间的 dirty 值不再被任何方持有，需立即释放。</para>
    /// <para>对 draft 节点：旧值无其它持有者，直接释放。</para>
    /// </remarks>
    public void SetValue(ref LeafHandle handle, TValue newValue) {
        int index = ResolveIndex(ref handle);
        SetValueByIndex(index, newValue);
    }

    /// <summary>按物理 index 更新节点的 value。语义同 <see cref="SetValue(ref LeafHandle, TValue)"/>。</summary>
    internal void SetValueByIndex(int index, TValue newValue) {
        if (IsCommittedIndex(index)) {
            bool isRepeatMutation = _dirtyValues.TestBit(index);
            CaptureIfNeeded(index);
            _dirtyValues.SetBit(index);
            if (VHelper.NeedRelease && isRepeatMutation) {
                VHelper.ReleaseSlot(_values[index]);
            }
        }
        else if (VHelper.NeedRelease) {
            VHelper.ReleaseSlot(_values[index]);
        }
        _values[index] = newValue;
    }

    /// <summary>
    /// 为原位更新准备值槽：仅保存 committed 原值（如尚未保存），但不标记 dirty。
    /// 调用方在确认值确实改变后，须调用 <see cref="ConfirmValueDirty"/> 标记 dirty。
    /// 若调用方发现值未变（如 UpdateOrInit 返回 false）且 <paramref name="capturedNow"/> 为 true，
    /// 应调用 <see cref="CancelPreparedValueUpdate"/> 撤销本次 provisional capture，实现零副作用。
    /// </summary>
    internal ref TValue PrepareValueSlotForUpdate(int index, out bool capturedNow) {
        capturedNow = false;
        if (IsCommittedIndex(index) && !IsCaptured(index)) {
            CaptureIfNeeded(index);
            capturedNow = true;
        }
        return ref _values[index];
    }

    /// <summary>配合 <see cref="PrepareValueSlotForUpdate"/> 使用：确认值已改变，标记 dirty。对 draft 节点无操作。</summary>
    internal void ConfirmValueDirty(int index) {
        if (IsCommittedIndex(index)) {
            _dirtyValues.SetBit(index);
        }
    }

    /// <summary>
    /// 撤销 <see cref="PrepareValueSlotForUpdate"/> 刚创建的 provisional capture。
    /// 仅在 <c>capturedNow == true</c> 且值未实际改变时调用。
    /// </summary>
    internal void CancelPreparedValueUpdate(int index) {
        Debug.Assert(IsCaptured(index), "CancelPreparedValueUpdate called on un-captured index.");
        Debug.Assert(!_dirtyValues.TestBit(index), "CancelPreparedValueUpdate called on already-dirty index.");
        _capturedNodes.ClearBit(index);
        bool popped = _capturedOriginals.TryPop(out var orig);
        Debug.Assert(popped && orig.Index == index, "CancelPreparedValueUpdate: stack top mismatch.");
    }

    public void SetNext(ref LeafHandle handle, LeafHandle newNext) {
        SetNextSequence(ref handle, newNext.Sequence);
    }

    public void SetNextSequence(ref LeafHandle handle, uint newNextSequence) {
        int index = ResolveIndex(ref handle);

        if (IsCommittedIndex(index)) {
            CaptureIfNeeded(index);
            _dirtyLinks.SetBit(index);
        }

        _nextSequences[index] = newNextSequence;
    }
    #endregion

    #region Lifecycle
    public void Commit() {
        CommitDirty();
        _committedCount = _currentCount;
        ResetDirtyTracking();
    }

    public void Revert() {
        RevertDirty();

        if (!VHelper.NeedRelease) {
            ClearTailRange(_committedCount, _currentCount);
            _currentCount = _committedCount;
            ResetDirtyTracking();
            return;
        }
        while (_currentCount > _committedCount) {
            int tailIndex = --_currentCount;
            VHelper.ReleaseSlot(_values[tailIndex]);
            _keys[tailIndex] = default!;
            _values[tailIndex] = default!;
            _nextSequences[tailIndex] = 0;
            _sequences[tailIndex] = 0;
        }
        ResetDirtyTracking();
    }
    #endregion

    #region Serialization
    /// <summary>
    /// 将当前变更写为增量帧。必须在任何 GC 压缩（<c>CollectAll</c> 等）之前调用，
    /// 因为 committed 槽位压缩会使脏追踪中的索引与物理位置不再对应。
    /// </summary>
    public void WriteDeltify(BinaryDiffWriter writer, DiffWriteContext context) {
        // Section 1: link mutations on committed nodes
        writer.WriteCount(DirtyLinkCount);
        foreach (int dirtyIndex in _dirtyLinks.Ones()) {
            writer.BareUInt32(_sequences[dirtyIndex], asKey: true);
            writer.BareUInt32(_nextSequences[dirtyIndex], asKey: false);
        }

        // Section 2: value mutations on committed nodes (key is immutable → only write value)
        writer.WriteCount(DirtyValueCount);
        foreach (int dirtyIndex in _dirtyValues.Ones()) {
            writer.BareUInt32(_sequences[dirtyIndex], asKey: true);
            VHelper.Write(writer, _values[dirtyIndex], asKey: false);
        }

        // Section 3: appended nodes
        writer.WriteCount(AppendedCount);
        WriteNodesRange(writer, _committedCount, _currentCount);
    }

    public void WriteRebase(BinaryDiffWriter writer, DiffWriteContext context) {
        writer.WriteCount(0); // linkMutationCount = 0
        writer.WriteCount(0); // valueMutationCount = 0

        writer.WriteCount(_currentCount);
        WriteNodesRange(writer, 0, _currentCount);
    }

    /// <summary>
    /// 全量序列化，但只写从 <paramref name="headSequence"/> 可达的节点。
    /// 不修改 arena 内部状态（只读），适用于 WritePendingDiff 中的 rebase 路径——
    /// 此时已解链的死节点尚未被 GC，但不应进入 rebase 帧。
    /// </summary>
    public void WriteRebaseLiveOnly(BinaryDiffWriter writer, DiffWriteContext context, uint headSequence) {
        writer.WriteCount(0); // linkMutationCount = 0
        writer.WriteCount(0); // valueMutationCount = 0

        if (_currentCount == 0 || headSequence == 0) {
            writer.WriteCount(0);
            return;
        }

        BitVector reachable = MarkChainInWindow(headSequence, 0, _currentCount);
        int liveCount = reachable.PopCount;
        writer.WriteCount(liveCount);
        // 按数组顺序（即 sequence 升序）写出，保持 ApplyDelta 要求的有序性
        for (int i = 0; i < _currentCount; i++) {
            if (!reachable.TestBit(i)) { continue; }
            writer.BareUInt32(_sequences[i], asKey: true);
            writer.BareUInt32(_nextSequences[i], asKey: false);
            WriteKv(writer, i);
        }
    }

    private void WriteKv(BinaryDiffWriter writer, int index) {
        KHelper.Write(writer, _keys[index], asKey: true);
        VHelper.Write(writer, _values[index], asKey: false);
    }

    private void WriteNodesRange(BinaryDiffWriter writer, int startIndex, int endIndex) {
        for (int i = startIndex; i < endIndex; ++i) {
            writer.BareUInt32(_sequences[i], asKey: true);
            writer.BareUInt32(_nextSequences[i], asKey: false);
            WriteKv(writer, i);
        }
    }

    private void ReadKv(ref BinaryDiffReader reader, int index) {
        _keys[index] = KHelper.Read(ref reader, asKey: true)!;
        // 使用 UpdateOrInit 而非 Read：对 ValueBoxHelper 这是唯一合法路径
        // （支持 OfBits64 Slot 原位复用）；对 typed helper 行为等价于 old = Read(...)。
        VHelper.UpdateOrInit(ref reader, ref _values[index]!);
    }

    public void ApplyDelta(ref BinaryDiffReader reader) {
        Debug.Assert(_currentCount == _committedCount);

        // Section 1: link mutations
        int linkCount = reader.ReadCount();
        while (--linkCount >= 0) {
            uint sequence = reader.BareUInt32(asKey: true);
            uint newNext = reader.BareUInt32(asKey: false);
            int index = FindCommittedIndex(sequence);
            if (index < 0) {
                throw new InvalidDataException(
                    $"Leaf chain delta references unknown committed node sequence {sequence}."
                );
            }
            _nextSequences[index] = newNext;
        }

        // Section 2: value mutations (key is immutable → only read value)
        int valueCount = reader.ReadCount();
        while (--valueCount >= 0) {
            uint sequence = reader.BareUInt32(asKey: true);
            int index = FindCommittedIndex(sequence);
            if (index < 0) {
                throw new InvalidDataException(
                    $"Leaf chain delta references unknown committed node sequence {sequence}."
                );
            }
            // UpdateOrInit 内部负责释放旧值的堆资源（对 ValueBoxHelper 为
            // FreeOldOwnedHeapIfNeeded / StoreOrReuseBits64）。ApplyDelta 只在 load 路径调用，
            // 此时旧值为 exclusive 状态，UpdateOrInit 可以正确释放或原位复用。
            // 对 NeedRelease 且 UpdateOrInit 不自行管理旧值的 helper（测试用），
            // 须在 UpdateOrInit 实现中自行释放（这是 ITypeHelper 契约的一部分）。
            VHelper.UpdateOrInit(ref reader, ref _values[index]!);
        }

        // Section 3: appended nodes
        int appendedCount = reader.ReadCount();
        EnsureCapacity(_committedCount + appendedCount);
        uint previousSequence = _committedCount == 0 ? 0u : _sequences[_committedCount - 1];
        while (--appendedCount >= 0) {
            uint seq = reader.BareUInt32(asKey: true);
            if (seq <= previousSequence) {
                throw new InvalidDataException(
                    $"Leaf chain appended node sequences must be strictly increasing and greater than existing committed sequences; got {seq} after {previousSequence}."
                );
            }
            _sequences[_committedCount] = seq;
            if (seq >= _nextAllocSequence) { _nextAllocSequence = seq + 1; }
            _nextSequences[_committedCount] = reader.BareUInt32(asKey: false);
            ReadKv(ref reader, _committedCount);
            ++_committedCount;
            previousSequence = seq;
        }

        _currentCount = _committedCount;
        ResetDirtyTracking();
    }

    public void SyncCurrentFromCommitted() {
        _currentCount = _committedCount;
        ResetDirtyTracking();
    }
    #endregion

    #region GC
    /// <summary>
    /// 将 committed 窗口按 <paramref name="headSequence"/> 沿 Next 链收敛为规范形态，
    /// 清理其中不可达的死节点。
    /// 典型调用时机：容器层 <c>ApplyDelta</c> 完成后、建立 current 视图与派生索引前。
    /// </summary>
    internal void CollectCommitted(uint headSequence) {
        ThrowIfActiveDirtyTracking(nameof(CollectCommitted));
        Debug.Assert(
            _currentCount == 0 || _currentCount == _committedCount,
            $"{nameof(CollectCommitted)} requires aligned committed/current windows."
        );
        if (_committedCount == 0) { return; }
        BitVector reachable = MarkChainInWindow(headSequence, 0, _committedCount);
        SweepAndCompactStable(0, ref _committedCount, in reachable, 0);
    }

    /// <summary>清理 uncommitted 窗口中不可达节点，保留 committed 快照的 revert 语义。
    /// 适用于 builder/draft 阶段的 GC。
    /// 此操作只移动 draft 槽位，不会破坏 committed dirty tracking。</summary>
    internal void CollectDraft(uint headSequence) {
        if (_currentCount == 0) { return; }
        BitVector reachable = MarkChainInWindow(headSequence, 0, _currentCount);
        SweepAndCompactStable(_committedCount, ref _currentCount, in reachable, 0);
    }

    /// <summary>清理所有不可达节点（破坏 revert 语义）。适用于 commit 前的全量 GC。</summary>
    internal void CollectAll(uint headSequence) {
        ThrowIfActiveDirtyTracking(nameof(CollectAll));
        if (_currentCount == 0) { return; }
        BitVector reachable = MarkChainInWindow(headSequence, 0, _currentCount);
        SweepAndCompactStable(0, ref _currentCount, in reachable, 0);
    }

    private BitVector MarkChainInWindow(uint headSequence, int windowBase, int windowCount) {
        BitVector reachable = new();
        reachable.SetLength(windowCount);
        uint current = headSequence;
        while (current != 0) {
            int index = FindIndex(current);
            if (index < 0) { break; }
            int windowIndex = index - windowBase;
            if (windowIndex >= 0 && windowIndex < windowCount) {
                if (!reachable.SetBit(windowIndex)) { break; /* cycle guard */ }
            }
            current = _nextSequences[index];
        }
        return reachable;
    }

    /// <summary>
    /// 对 <c>[startIndex, endIndex)</c> 做保序 sweep-and-compact。
    /// </summary>
    private void SweepAndCompactStable(
        int startIndex, ref int endIndex, in BitVector reachable, int reachableBase
    ) {
        int writeIndex = startIndex;
        for (int readIndex = startIndex; readIndex < endIndex; ++readIndex) {
            int windowIndex = readIndex - reachableBase;
            if (!reachable.TestBit(windowIndex)) {
                if (VHelper.NeedRelease) {
                    VHelper.ReleaseSlot(_values[readIndex]);
                }
                _keys[readIndex] = default!;
                _values[readIndex] = default!;
                _nextSequences[readIndex] = 0;
                _sequences[readIndex] = 0;
                continue;
            }

            if (writeIndex != readIndex) {
                _keys[writeIndex] = _keys[readIndex];
                _values[writeIndex] = _values[readIndex];
                _nextSequences[writeIndex] = _nextSequences[readIndex];
                _sequences[writeIndex] = _sequences[readIndex];
                _keys[readIndex] = default!;
                _values[readIndex] = default!;
                _nextSequences[readIndex] = 0;
                _sequences[readIndex] = 0;
            }

            ++writeIndex;
        }

        endIndex = writeIndex;
    }
    #endregion

    private void ResetDirtyTracking() {
        _dirtyValues.Clear();
        _dirtyValues.SetLength(_committedCount);
        _dirtyLinks.Clear();
        _dirtyLinks.SetLength(_committedCount);
        _capturedNodes.Clear();
        _capturedNodes.SetLength(_committedCount);
        _capturedOriginals.Clear();
    }

    private void ClearTailRange(int startIndex, int endIndex) {
        if (startIndex >= endIndex) { return; }
        int count = endIndex - startIndex;
        Array.Clear(_keys, startIndex, count);
        Array.Clear(_values, startIndex, count);
        Array.Clear(_nextSequences, startIndex, count);
        Array.Clear(_sequences, startIndex, count);
    }
}
