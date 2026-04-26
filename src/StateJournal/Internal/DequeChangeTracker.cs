using System.Diagnostics;
using System.Runtime.CompilerServices;
using Atelia.StateJournal.Serialization;

namespace Atelia.StateJournal.Internal;

internal struct DequeChangeTracker<TValue>
    where TValue : notnull {
    private readonly IndexedDeque<TValue?> _committed;
    private readonly IndexedDeque<TValue?> _current;

    /// <summary>
    /// committed 绝对索引上的脏位图。bit j 置位表示 committed[j] 已被修改。
    /// keep window 滑动时（_oldKeepLo 变化）committed 元素的绝对位置不变，因此无需位移。
    /// 位图容量在 <c>ResetTrackedWindow</c>（Commit / Revert / SyncCurrentFromCommitted）时
    /// 与 <c>committed.Count</c> 对齐；<c>ApplyDelta</c> 阶段仅 Clear，
    /// 由后续 <c>SyncCurrentFromCommitted</c> 完成容量校准。
    /// </summary>
    private BitVector _committedDirtyMap;
    private uint _currentValueBytes;
    private uint _dirtyPrefixValueBytes;
    private uint _dirtySuffixValueBytes;
    private uint _dirtyKeepValueBytes;
    private uint _dirtyKeepIndexBytesCache;
    private bool _dirtyKeepIndexBytesCacheValid;

    // committed 中仍然幸存的连续窗口: [_oldKeepLo, OldKeepHi)
    private int _oldKeepLo;

    // 该窗口在 current 中对应的位置: [_newKeepLo, NewKeepHi)
    private int _newKeepLo;
    private int _keepCount;

    private int OldKeepHi => _oldKeepLo + _keepCount;
    private int NewKeepHi => _newKeepLo + _keepCount;
    private int DirtyPrefixCount => _newKeepLo;
    private int DirtySuffixCount => _current.Count - NewKeepHi;

    #region 可用于单元测试
    public int TrimFrontCount => _oldKeepLo;
    public int TrimBackCount => _committed.Count - OldKeepHi;
    public int PushFrontCount => _newKeepLo;
    public int PushBackCount => _current.Count - NewKeepHi;
    public int KeepCount => _keepCount;
    public int KeepDirtyCount => _committedDirtyMap.PopCount;
    public bool HasChanges =>
        _committed.Count != _keepCount // TrimFrontCount + TrimBackCount
        || _committedDirtyMap.PopCount != 0
        || _current.Count != _keepCount; // PushFrontCount + PushBackCount
    internal IndexedDeque<TValue?> Current => _current;
    internal IndexedDeque<TValue?> Committed => _committed;
    #endregion

    /// <summary>估算 rebase 帧所需的 bare 字节数（仅 payload，不含 frame overhead 与 typeCode）。</summary>
    public uint EstimatedRebaseBytes<VHelper>()
        where VHelper : unmanaged, ITypeHelper<TValue> {
        AssertEstimateSummaryConsistent<VHelper>();
        return CountHeaderBytes(0)
            + CountHeaderBytes(0)
            + CountHeaderBytes(0)
            + CountHeaderBytes(0)
            + CountHeaderBytes(_current.Count)
            + _currentValueBytes;
    }

    /// <summary>估算 deltify 帧所需的 bare 字节数（仅 payload，不含 frame overhead）。</summary>
    public uint EstimatedDeltifyBytes<VHelper>()
        where VHelper : unmanaged, ITypeHelper<TValue> {
        AssertEstimateSummaryConsistent<VHelper>();
        // 5 个序列化 count header + dirty value bytes（prefix/suffix/keep）+ keep patch index header。
        // 三个 value bytes 字段均在 mutation 热路径上增量维护；keep patch index header 走 lazy cache。
        return CountHeaderBytes(TrimFrontCount)
            + CountHeaderBytes(TrimBackCount)
            + CountHeaderBytes(KeepDirtyCount)
            + CountHeaderBytes(PushFrontCount)
            + CountHeaderBytes(PushBackCount)
            + _dirtyPrefixValueBytes
            + _dirtySuffixValueBytes
            + _dirtyKeepValueBytes
            + GetDirtyKeepIndexBytes();
    }

    private static uint CountHeaderBytes(int count) => CostEstimateUtil.VarIntSize((uint)count);

    private static uint SumSegment<VHelper>(Span<TValue?> segment)
        where VHelper : unmanaged, ITypeHelper<TValue> {
        uint s = 0;
        foreach (var v in segment) {
            s += VHelper.EstimateBareSize(v, asKey: false);
        }
        return s;
    }

    public DequeChangeTracker() {
        _committed = new();
        _current = new();
        _committedDirtyMap = new();
        _oldKeepLo = 0;
        _newKeepLo = 0;
        _keepCount = 0;
        _currentValueBytes = 0;
        _dirtyPrefixValueBytes = 0;
        _dirtySuffixValueBytes = 0;
        _dirtyKeepValueBytes = 0;
        _dirtyKeepIndexBytesCache = 0;
        _dirtyKeepIndexBytesCacheValid = true;
    }

    public void PushFront<VHelper>(TValue? value)
        where VHelper : unmanaged, ITypeHelper<TValue> {
        if (_newKeepLo == 0 && _oldKeepLo > 0) {
            TValue? committedValue = _committed[_oldKeepLo - 1];
            if (VHelper.Equals(value, committedValue)) {
                if (VHelper.NeedRelease) {
                    // 与 committed 对位值语义相等时，丢弃新值并复用 committed 的 frozen 副本。
                    VHelper.ReleaseSlot(value);
                }

                _current.PushFront(committedValue);
                _currentValueBytes += EstimateValueBytes<VHelper>(committedValue);
                --_oldKeepLo;
                ++_keepCount;
                InvalidateDirtyKeepIndexBytes();
                AssertTrackedInvariant<VHelper>();
                return;
            }
        }

        _current.PushFront(value);
        uint valueBytes = EstimateValueBytes<VHelper>(value);
        _currentValueBytes += valueBytes;
        _dirtyPrefixValueBytes += valueBytes;
        ++_newKeepLo;
        AssertTrackedInvariant<VHelper>();
    }

    public void PushBack<VHelper>(TValue? value)
        where VHelper : unmanaged, ITypeHelper<TValue> {
        if (NewKeepHi == _current.Count && OldKeepHi < _committed.Count) {
            TValue? committedValue = _committed[OldKeepHi];
            if (VHelper.Equals(value, committedValue)) {
                if (VHelper.NeedRelease) {
                    // 与 committed 对位值语义相等时，丢弃新值并复用 committed 的 frozen 副本。
                    VHelper.ReleaseSlot(value);
                }

                _current.PushBack(committedValue);
                _currentValueBytes += EstimateValueBytes<VHelper>(committedValue);
                ++_keepCount;
                AssertTrackedInvariant<VHelper>();
                return;
            }
        }

        _current.PushBack(value);
        uint pushBackBytes = EstimateValueBytes<VHelper>(value);
        _currentValueBytes += pushBackBytes;
        _dirtySuffixValueBytes += pushBackBytes;
        AssertTrackedInvariant<VHelper>();
    }

    public bool TryGetAt(int index, out TValue? value) => _current.TryGetAt(index, out value);

    public bool TryPeekFront(out TValue? value) => _current.TryPeekFront(out value);

    public bool TryPeekBack(out TValue? value) => _current.TryPeekBack(out value);

    /// <summary>
    /// typed / durable-ref 路径的一步写入入口：
    /// 在 tracker 内部完成 current no-op 短路，以及 keep window / dirty map 维护。
    /// </summary>
    public bool SetAt<VHelper>(int index, TValue? value)
        where VHelper : unmanaged, ITypeHelper<TValue> {
        Debug.Assert(
            !VHelper.NeedRelease,
            "一体化 SetAt 仅适用于无额外所有权释放语义的 typed/durable-ref 路径；mixed/ValueBox 仍应走二阶段 Update + AfterSet。"
        );

        ref TValue? slot = ref _current.GetRef(index);
        TValue? oldValue = slot;
        if (VHelper.Equals(slot, value)) { return false; }

        slot = value;
        AfterSet<VHelper>(index, ref slot, oldValue);
        return true;
    }

    /// <summary>
    /// 返回 current 槽位的可写引用。调用方可直接对槽位做原地更新。
    /// 若更新后语义发生变化，必须再调用 <see cref="AfterSet{VHelper}"/> 维护 tracker 状态。
    /// </summary>
    public ref TValue? GetRef(int index) => ref _current.GetRef(index);

    /// <summary>
    /// 配合 <see cref="GetRef"/> 使用：调用方先原地更新 current 槽位，确认语义发生变化后，
    /// 再调用本方法维护 keep window / dirty map。
    /// </summary>
    public void AfterSet<VHelper>(int currentIndex, ref TValue? value, TValue? oldValue)
        where VHelper : unmanaged, ITypeHelper<TValue> {
        // 显式要求传入与 GetRef 配对得到的 ref，可在 DEBUG 下校验“就是这个槽位”，
        // 比仅比较值语义更能防止调用方误把别处的临时值传进来。
        Debug.Assert(Unsafe.AreSame(ref value, ref _current.GetRef(currentIndex)));
        uint oldValueBytes = EstimateValueBytes<VHelper>(oldValue);

        if ((uint)(currentIndex - _newKeepLo) >= (uint)_keepCount) {
            // index 在 dirty prefix / dirty suffix 区域。
            bool isPrefix = currentIndex < _newKeepLo;
            if (TryAbsorbDirtyEdgeIntoKeep<VHelper>(currentIndex, value, out uint absorbedBytes)) {
                // absorb 成功：原 dirty edge 槽位进入 keep（keep 仍 clean，因 _committedDirtyMap 未设位）。
                if (isPrefix) {
                    _dirtyPrefixValueBytes -= oldValueBytes;
                }
                else {
                    _dirtySuffixValueBytes -= oldValueBytes;
                }
                ApplyValueBytesDelta(ref _currentValueBytes, oldValueBytes, absorbedBytes);
            }
            else {
                uint finalBytes = EstimateValueBytes<VHelper>(value);
                if (isPrefix) {
                    ApplyValueBytesDelta(ref _dirtyPrefixValueBytes, oldValueBytes, finalBytes);
                }
                else {
                    ApplyValueBytesDelta(ref _dirtySuffixValueBytes, oldValueBytes, finalBytes);
                }
                ApplyValueBytesDelta(ref _currentValueBytes, oldValueBytes, finalBytes);
            }
            AssertTrackedInvariant<VHelper>();
            return;
        }

        int keepRelativeIndex = currentIndex - _newKeepLo;
        int committedIndex = _oldKeepLo + keepRelativeIndex;
        TValue? committedValue = _committed[committedIndex];
        bool wasDirty = _committedDirtyMap.TestBit(committedIndex);
        uint finalKeepBytes;

        if (VHelper.Equals(value, committedValue)) {
            if (VHelper.NeedRelease) {
                VHelper.ReleaseSlot(value);
            }

            _current[currentIndex] = committedValue;
            if (wasDirty) {
                _committedDirtyMap.ClearBit(committedIndex);
                InvalidateDirtyKeepIndexBytes();
                _dirtyKeepValueBytes -= oldValueBytes;
            }
            finalKeepBytes = EstimateValueBytes<VHelper>(committedValue);

            ApplyValueBytesDelta(ref _currentValueBytes, oldValueBytes, finalKeepBytes);
            AssertTrackedInvariant<VHelper>();
            return;
        }

        finalKeepBytes = EstimateValueBytes<VHelper>(value);
        if (!wasDirty) {
            InvalidateDirtyKeepIndexBytes();
            _dirtyKeepValueBytes += finalKeepBytes;
        }
        else {
            ApplyValueBytesDelta(ref _dirtyKeepValueBytes, oldValueBytes, finalKeepBytes);
        }
        _committedDirtyMap.SetBit(committedIndex);
        ApplyValueBytesDelta(ref _currentValueBytes, oldValueBytes, finalKeepBytes);
        AssertTrackedInvariant<VHelper>();
    }

    public bool TryPopFront<VHelper>(out TValue? value, out bool callerOwned)
        where VHelper : unmanaged, ITypeHelper<TValue> {
        bool popDirtyPrefix = _newKeepLo > 0;
        bool popKeep = !popDirtyPrefix && _keepCount > 0;
        if (!_current.TryPopFront(out value)) {
            callerOwned = false;
            return false;
        }
        uint poppedBytes = EstimateValueBytes<VHelper>(value);
        _currentValueBytes -= poppedBytes;

        if (popDirtyPrefix) {
            _dirtyPrefixValueBytes -= poppedBytes;
            --_newKeepLo;
            callerOwned = VHelper.NeedRelease;
            AssertTrackedInvariant<VHelper>();
            return true;
        }

        if (popKeep) {
            bool frontWasDirty = _committedDirtyMap.ClearBit(_oldKeepLo);
            if (frontWasDirty) {
                _dirtyKeepValueBytes -= poppedBytes;
            }
            ++_oldKeepLo;
            --_keepCount;
            InvalidateDirtyKeepIndexBytes();
            callerOwned = VHelper.NeedRelease && frontWasDirty;
            AssertTrackedInvariant<VHelper>();
            return true;
        }

        // 仅剩 dirty suffix（_keepCount==0 且 _newKeepLo==0）时，pop 的是 suffix 首元。
        _dirtySuffixValueBytes -= poppedBytes;
        callerOwned = VHelper.NeedRelease;
        AssertTrackedInvariant<VHelper>();
        return true;
    }

    public bool TryPopBack<VHelper>(out TValue? value, out bool callerOwned)
        where VHelper : unmanaged, ITypeHelper<TValue> {
        bool popDirtySuffix = DirtySuffixCount > 0;
        bool popKeep = !popDirtySuffix && _keepCount > 0;
        if (!_current.TryPopBack(out value)) {
            callerOwned = false;
            return false;
        }
        uint poppedBytes = EstimateValueBytes<VHelper>(value);
        _currentValueBytes -= poppedBytes;

        if (popDirtySuffix) {
            _dirtySuffixValueBytes -= poppedBytes;
            callerOwned = VHelper.NeedRelease;
            AssertTrackedInvariant<VHelper>();
            return true;
        }

        if (popKeep) {
            int backCommittedIndex = OldKeepHi - 1;
            bool backWasDirty = _committedDirtyMap.ClearBit(backCommittedIndex);
            if (backWasDirty) {
                _dirtyKeepValueBytes -= poppedBytes;
            }
            --_keepCount;
            if (backWasDirty) {
                InvalidateDirtyKeepIndexBytes();
            }
            callerOwned = VHelper.NeedRelease && backWasDirty;
            AssertTrackedInvariant<VHelper>();
            return true;
        }

        // 仅剩 dirty prefix（_keepCount==0 且 DirtySuffixCount==0）时，pop 的是 prefix 末元。
        _dirtyPrefixValueBytes -= poppedBytes;
        --_newKeepLo;
        callerOwned = VHelper.NeedRelease;
        AssertTrackedInvariant<VHelper>();
        return true;
    }

    public void Revert<VHelper>()
        where VHelper : unmanaged, ITypeHelper<TValue> {
        if (!HasChanges) { return; }

        if (VHelper.NeedRelease) {
            ReleaseCurrentPrefixAndSuffix<VHelper>();
            ReleaseCurrentSparseKeepValues<VHelper>();
        }

        // O(delta): 裁掉 current 的 prefix/suffix，只留 keep 窗口
        _current.TrimBack(_current.Count - NewKeepHi);
        _current.TrimFront(_newKeepLo);

        RestoreCurrentKeepFromCommitted<VHelper>();

        // 从 committed 恢复被 trim 的前后部分
        CopyCommittedRangeToCurrent<VHelper>(0, _oldKeepLo, toFront: true);
        CopyCommittedRangeToCurrent<VHelper>(OldKeepHi, _committed.Count - OldKeepHi, toFront: false);

        RebuildCurrentValueBytesFromCurrent<VHelper>();
        ResetTrackedWindow<VHelper>();
    }

    public void Commit<VHelper>()
        where VHelper : unmanaged, ITypeHelper<TValue> {
        if (!HasChanges) { return; }

        // O(delta): 只冻结 prefix 和 suffix（keep 窗口已是 frozen 共享引用）
        FreezeCurrentRange<VHelper>(0, _newKeepLo);
        FreezeCurrentRange<VHelper>(NewKeepHi, _current.Count - NewKeepHi);
        CommitSparseKeepChanges<VHelper>();

        // 释放 committed 中被 trim 的元素
        if (VHelper.NeedRelease) {
            ReleaseRange<VHelper>(_committed, 0, _oldKeepLo);
            ReleaseRange<VHelper>(_committed, OldKeepHi, _committed.Count - OldKeepHi);
        }

        // 裁掉 committed 的 trim 部分，只留 keep 窗口
        _committed.TrimBack(_committed.Count - OldKeepHi);
        _committed.TrimFront(_oldKeepLo);

        // 将 current 的 prefix/suffix 同步到 committed
        CopyCurrentRangeToCommitted(0, _newKeepLo, toFront: true);
        CopyCurrentRangeToCommitted(NewKeepHi, _current.Count - NewKeepHi, toFront: false);

        ResetTrackedWindow<VHelper>();
    }

    public void WriteDeltify<VHelper>(BinaryDiffWriter writer, DiffWriteContext context)
        where VHelper : ITypeHelper<TValue> {
        writer.WriteCount(TrimFrontCount);
        writer.WriteCount(TrimBackCount);

        writer.WriteCount(KeepDirtyCount);
        foreach (int committedIndex in _committedDirtyMap.Ones()) {
            int keepRelativeIndex = KeepRelativeIndexFromCommittedIndex(committedIndex);
            writer.WriteCount(keepRelativeIndex);
            VHelper.Write(writer, _current[CurrentIndexFromCommittedIndex(committedIndex)], false);
        }

        writer.WriteCount(PushFrontCount);
        // 逆序写 prefix，使 ApplyDelta 可边读边 PushFront，无需临时缓存。
        WriteCurrentRange<VHelper>(writer, 0, _newKeepLo, reverse: true);

        writer.WriteCount(PushBackCount);
        WriteCurrentRange<VHelper>(writer, NewKeepHi, _current.Count - NewKeepHi, reverse: false);
    }

    public void WriteRebase<VHelper>(BinaryDiffWriter writer, DiffWriteContext context)
        where VHelper : ITypeHelper<TValue> {
        // Rebase 也编码成 ApplyDelta 可直接消费的同形协议：
        // 在空 committed 上表现为“无 trim、无 keep patch、无前缀、整段作为 pushBack”。
        writer.WriteCount(0);
        writer.WriteCount(0);
        writer.WriteCount(0);
        writer.WriteCount(0);
        writer.WriteCount(_current.Count);
        WriteCurrentRange<VHelper>(writer, 0, _current.Count, reverse: false);
    }

    public void ApplyDelta<VHelper>(ref BinaryDiffReader reader)
        where VHelper : unmanaged, ITypeHelper<TValue> {
        // ApplyDelta 只负责在反序列化阶段叠加 delta 以重建 committed。
        // 此时 _current 尚未通过 SyncCurrentFromCommitted 建立，因此必须保持为空。
        if (_current.Count != 0) { throw new InvalidOperationException("ApplyDelta requires _current to be empty. Apply deltas during deserialization, then call SyncCurrentFromCommitted."); }

        int trimFrontCount = reader.ReadCount();
        int trimBackCount = reader.ReadCount();

        if (trimFrontCount + trimBackCount > _committed.Count) { throw new InvalidDataException("Deque delta trims more elements than committed contains."); }

        if (VHelper.NeedRelease) {
            ReleaseRange<VHelper>(_committed, 0, trimFrontCount);
            ReleaseRange<VHelper>(_committed, _committed.Count - trimBackCount, trimBackCount);
        }

        _committed.TrimBack(trimBackCount);
        _committed.TrimFront(trimFrontCount);

        int keepPatchCount = reader.ReadCount();
        int previousKeepPatchIndex = -1;
        for (int i = 0; i < keepPatchCount; ++i) {
            int keepRelativeIndex = reader.ReadCount();
            if ((uint)keepRelativeIndex >= (uint)_committed.Count) { throw new InvalidDataException("Deque delta keep patch index is out of range."); }
            if (keepRelativeIndex <= previousKeepPatchIndex) { throw new InvalidDataException("Deque delta keep patch indices must be strictly increasing."); }

            TValue? patchedValue = VHelper.Freeze(ReadValue<VHelper>(ref reader));
            if (VHelper.NeedRelease) {
                VHelper.ReleaseSlot(_committed[keepRelativeIndex]);
            }
            _committed[keepRelativeIndex] = patchedValue;
            previousKeepPatchIndex = keepRelativeIndex;
        }

        int pushFrontCount = reader.ReadCount();
        for (int i = 0; i < pushFrontCount; ++i) {
            _committed.PushFront(VHelper.Freeze(ReadValue<VHelper>(ref reader)));
        }

        int pushBackCount = reader.ReadCount();
        for (int i = 0; i < pushBackCount; ++i) {
            _committed.PushBack(VHelper.Freeze(ReadValue<VHelper>(ref reader)));
        }

        _current.Clear();
        _oldKeepLo = 0;
        _newKeepLo = 0;
        _keepCount = 0;
        _committedDirtyMap.Clear();
        _currentValueBytes = 0;
        _dirtyPrefixValueBytes = 0;
        _dirtySuffixValueBytes = 0;
        _dirtyKeepValueBytes = 0;
        _dirtyKeepIndexBytesCache = 0;
        _dirtyKeepIndexBytesCacheValid = true;
    }

    public void SyncCurrentFromCommitted<VHelper>()
        where VHelper : unmanaged, ITypeHelper<TValue> {
        Debug.Assert(_current.Count == 0, "SyncCurrentFromCommitted 应在空 _current 上调用。");
        CopyCommittedRangeToCurrent<VHelper>(0, _committed.Count, toFront: false);

        RebuildCurrentValueBytesFromCurrent<VHelper>();
        ResetTrackedWindow<VHelper>();
    }

    private void ReleaseCurrentSparseKeepValues<VHelper>()
        where VHelper : unmanaged, ITypeHelper<TValue> {
        foreach (int committedIndex in _committedDirtyMap.Ones()) {
            VHelper.ReleaseSlot(_current[CurrentIndexFromCommittedIndex(committedIndex)]);
        }
    }

    /// <remarks>
    /// 调用时机：Revert 中 _current 已 TrimFront(_newKeepLo)，此时 keep 窗口从 index 0 开始。
    /// </remarks>
    private void RestoreCurrentKeepFromCommitted<VHelper>()
        where VHelper : unmanaged, ITypeHelper<TValue> {
        foreach (int committedIndex in _committedDirtyMap.Ones()) {
            _current[KeepRelativeIndexFromCommittedIndex(committedIndex)] = VHelper.Freeze(_committed[committedIndex]);
        }
    }

    private void CommitSparseKeepChanges<VHelper>()
        where VHelper : unmanaged, ITypeHelper<TValue> {
        foreach (int committedIndex in _committedDirtyMap.Ones()) {
            int currentIndex = CurrentIndexFromCommittedIndex(committedIndex);
            TValue? frozenValue = VHelper.Freeze(_current[currentIndex]);
            if (VHelper.NeedRelease) {
                VHelper.ReleaseSlot(_committed[committedIndex]);
            }
            _committed[committedIndex] = frozenValue;
            _current[currentIndex] = frozenValue;
        }
    }

    private int KeepRelativeIndexFromCommittedIndex(int committedIndex) => committedIndex - _oldKeepLo;

    private int CurrentIndexFromCommittedIndex(int committedIndex) => _newKeepLo + KeepRelativeIndexFromCommittedIndex(committedIndex);

    private bool TryAbsorbDirtyEdgeIntoKeep<VHelper>(int currentIndex, TValue? value, out uint finalBytes)
        where VHelper : unmanaged, ITypeHelper<TValue> =>
        TryAbsorbDirtyFrontIntoKeep<VHelper>(currentIndex, value, out finalBytes)
        || TryAbsorbDirtyBackIntoKeep<VHelper>(currentIndex, value, out finalBytes);

    private bool TryAbsorbDirtyFrontIntoKeep<VHelper>(int currentIndex, TValue? value, out uint finalBytes)
        where VHelper : unmanaged, ITypeHelper<TValue> {
        if (_oldKeepLo == 0) {
            finalBytes = 0;
            return false;
        }

        bool isStandardFront = currentIndex == _newKeepLo - 1;
        bool isEmptySuffixMatch = _keepCount == 0 && currentIndex == _newKeepLo;
        if (!isStandardFront && !isEmptySuffixMatch) {
            finalBytes = 0;
            return false;
        }

        TValue? committedValue = _committed[_oldKeepLo - 1];
        if (!VHelper.Equals(value, committedValue)) {
            finalBytes = 0;
            return false;
        }

        if (VHelper.NeedRelease) { VHelper.ReleaseSlot(value); }
        _current[currentIndex] = committedValue; // ReplaceDirtyEdgeWithCommitted
        finalBytes = EstimateValueBytes<VHelper>(committedValue);

        --_oldKeepLo;
        if (isStandardFront) { --_newKeepLo; }
        ++_keepCount;
        InvalidateDirtyKeepIndexBytes();
        return true;
    }

    private bool TryAbsorbDirtyBackIntoKeep<VHelper>(int currentIndex, TValue? value, out uint finalBytes)
        where VHelper : unmanaged, ITypeHelper<TValue> {
        if (OldKeepHi >= _committed.Count) {
            finalBytes = 0;
            return false;
        }

        bool isStandardBack = currentIndex == NewKeepHi;
        bool isEmptyPrefixMatch = _keepCount == 0 && currentIndex == NewKeepHi - 1;
        if (!isStandardBack && !isEmptyPrefixMatch) {
            finalBytes = 0;
            return false;
        }

        TValue? committedValue = _committed[OldKeepHi];
        if (!VHelper.Equals(value, committedValue)) {
            finalBytes = 0;
            return false;
        }

        if (VHelper.NeedRelease) { VHelper.ReleaseSlot(value); }
        _current[currentIndex] = committedValue; // ReplaceDirtyEdgeWithCommitted
        finalBytes = EstimateValueBytes<VHelper>(committedValue);

        if (isEmptyPrefixMatch) { --_newKeepLo; }
        ++_keepCount;
        return true;
    }

    private void ResetTrackedWindow<VHelper>()
        where VHelper : unmanaged, ITypeHelper<TValue> {
        _oldKeepLo = 0;
        _newKeepLo = 0;
        _keepCount = _committed.Count;
        _committedDirtyMap.Clear();
        _committedDirtyMap.SetLength(_committed.Count);
        _dirtyPrefixValueBytes = 0;
        _dirtySuffixValueBytes = 0;
        _dirtyKeepValueBytes = 0;
        _dirtyKeepIndexBytesCache = 0;
        _dirtyKeepIndexBytesCacheValid = true;
        AssertTrackedInvariant<VHelper>();
    }

    private static uint EstimateValueBytes<VHelper>(TValue? value)
        where VHelper : ITypeHelper<TValue> => VHelper.EstimateBareSize(value, asKey: false);

    private uint GetDirtyKeepIndexBytes() {
        if (_dirtyKeepIndexBytesCacheValid) { return _dirtyKeepIndexBytesCache; }

        uint sum = 0;
        foreach (int committedIndex in _committedDirtyMap.Ones()) {
            sum += CountHeaderBytes(KeepRelativeIndexFromCommittedIndex(committedIndex));
        }

        _dirtyKeepIndexBytesCache = sum;
        _dirtyKeepIndexBytesCacheValid = true;
        return sum;
    }

    private void InvalidateDirtyKeepIndexBytes() => _dirtyKeepIndexBytesCacheValid = false;

    private static void ApplyValueBytesDelta(ref uint field, uint oldBytes, uint newBytes) {
        if (newBytes >= oldBytes) {
            field += newBytes - oldBytes;
        }
        else {
            field -= oldBytes - newBytes;
        }
    }

    private void RebuildCurrentValueBytesFromCurrent<VHelper>()
        where VHelper : unmanaged, ITypeHelper<TValue> {
        _current.GetSegments(out Span<TValue?> first, out Span<TValue?> second);
        _currentValueBytes = SumSegment<VHelper>(first) + SumSegment<VHelper>(second);
    }

    /// <summary>
    /// DEBUG-only：从 _current / _committedDirtyMap 全量重算 estimate summary 字段，
    /// 与增量维护的状态比对，用于尽早发现 summary drift（参考设计 7.1 节）。
    /// </summary>
    [Conditional("DEBUG")]
    private void AssertEstimateSummaryConsistent<VHelper>()
        where VHelper : unmanaged, ITypeHelper<TValue> {
        _current.GetSegments(out Span<TValue?> first, out Span<TValue?> second);
        uint expectedCurrentValueBytes = SumSegment<VHelper>(first) + SumSegment<VHelper>(second);
        Debug.Assert(
            expectedCurrentValueBytes == _currentValueBytes,
            $"DequeChangeTracker _currentValueBytes drift: tracked={_currentValueBytes}, expected={expectedCurrentValueBytes}."
        );

        // dirty prefix / suffix 是 current 中 keep 窗口之外的全部元素。
        _current.GetSegments(0, _newKeepLo, out Span<TValue?> pfFirst, out Span<TValue?> pfSecond);
        uint expectedDirtyPrefix = SumSegment<VHelper>(pfFirst) + SumSegment<VHelper>(pfSecond);
        Debug.Assert(
            expectedDirtyPrefix == _dirtyPrefixValueBytes,
            $"DequeChangeTracker _dirtyPrefixValueBytes drift: tracked={_dirtyPrefixValueBytes}, expected={expectedDirtyPrefix}."
        );

        int suffixCount = _current.Count - NewKeepHi;
        _current.GetSegments(NewKeepHi, suffixCount, out Span<TValue?> sfFirst, out Span<TValue?> sfSecond);
        uint expectedDirtySuffix = SumSegment<VHelper>(sfFirst) + SumSegment<VHelper>(sfSecond);
        Debug.Assert(
            expectedDirtySuffix == _dirtySuffixValueBytes,
            $"DequeChangeTracker _dirtySuffixValueBytes drift: tracked={_dirtySuffixValueBytes}, expected={expectedDirtySuffix}."
        );

        uint expectedDirtyKeep = 0;
        foreach (int committedIndex in _committedDirtyMap.Ones()) {
            int currentIndex = CurrentIndexFromCommittedIndex(committedIndex);
            expectedDirtyKeep += EstimateValueBytes<VHelper>(_current[currentIndex]);
        }
        Debug.Assert(
            expectedDirtyKeep == _dirtyKeepValueBytes,
            $"DequeChangeTracker _dirtyKeepValueBytes drift: tracked={_dirtyKeepValueBytes}, expected={expectedDirtyKeep}."
        );

        if (_dirtyKeepIndexBytesCacheValid) {
            uint expectedDirtyKeepIndexBytes = 0;
            foreach (int committedIndex in _committedDirtyMap.Ones()) {
                expectedDirtyKeepIndexBytes += CountHeaderBytes(KeepRelativeIndexFromCommittedIndex(committedIndex));
            }
            Debug.Assert(
                expectedDirtyKeepIndexBytes == _dirtyKeepIndexBytesCache,
                $"DequeChangeTracker _dirtyKeepIndexBytesCache drift: tracked={_dirtyKeepIndexBytesCache}, expected={expectedDirtyKeepIndexBytes}."
            );
        }
    }

    private void ReleaseCurrentPrefixAndSuffix<VHelper>()
        where VHelper : unmanaged, ITypeHelper<TValue> {
        ReleaseRange<VHelper>(_current, 0, DirtyPrefixCount);
        ReleaseRange<VHelper>(_current, NewKeepHi, _current.Count - NewKeepHi);
    }

    private void FreezeCurrentRange<VHelper>(int index, int count)
        where VHelper : unmanaged, ITypeHelper<TValue> {
        if (count == 0) { return; }

        _current.GetSegments(index, count, out Span<TValue?> first, out Span<TValue?> second);
        FreezeSegment<VHelper>(first);
        FreezeSegment<VHelper>(second);
    }

    private static void ReleaseRange<VHelper>(IndexedDeque<TValue?> deque, int index, int count)
        where VHelper : unmanaged, ITypeHelper<TValue> {
        if (count == 0) { return; }

        deque.GetSegments(index, count, out Span<TValue?> first, out Span<TValue?> second);
        ReleaseSegments<VHelper>(first, second);
    }

    private void CopyCommittedRangeToCurrent<VHelper>(int index, int count, bool toFront)
        where VHelper : unmanaged, ITypeHelper<TValue> {
        if (count == 0) { return; }

        _committed.GetSegments(index, count, out Span<TValue?> sourceFirst, out Span<TValue?> sourceSecond);
        if (toFront) {
            _current.ReserveFront(count, out Span<TValue?> destFirst, out Span<TValue?> destSecond);
            CopySegments<VHelper>(sourceFirst, sourceSecond, destFirst, destSecond, freeze: true);
            return;
        }

        _current.ReserveBack(count, out Span<TValue?> backDestFirst, out Span<TValue?> backDestSecond);
        CopySegments<VHelper>(sourceFirst, sourceSecond, backDestFirst, backDestSecond, freeze: true);
    }

    private void CopyCurrentRangeToCommitted(int index, int count, bool toFront) {
        if (count == 0) { return; }

        _current.GetSegments(index, count, out Span<TValue?> sourceFirst, out Span<TValue?> sourceSecond);
        if (toFront) {
            _committed.ReserveFront(count, out Span<TValue?> destFirst, out Span<TValue?> destSecond);
            CopySegments(sourceFirst, sourceSecond, destFirst, destSecond);
            return;
        }

        _committed.ReserveBack(count, out Span<TValue?> backDestFirst, out Span<TValue?> backDestSecond);
        CopySegments(sourceFirst, sourceSecond, backDestFirst, backDestSecond);
    }

    private void WriteCurrentRange<VHelper>(BinaryDiffWriter writer, int index, int count, bool reverse)
        where VHelper : ITypeHelper<TValue> {
        if (count == 0) { return; }

        _current.GetSegments(index, count, out Span<TValue?> first, out Span<TValue?> second);
        if (reverse) {
            WriteSegmentReverse<VHelper>(writer, second);
            WriteSegmentReverse<VHelper>(writer, first);
            return;
        }

        WriteSegmentForward<VHelper>(writer, first);
        WriteSegmentForward<VHelper>(writer, second);
    }

    private static void FreezeSegment<VHelper>(Span<TValue?> segment)
        where VHelper : ITypeHelper<TValue> {
        for (int i = 0; i < segment.Length; ++i) {
            segment[i] = VHelper.Freeze(segment[i]);
        }
    }

    private static void ReleaseSegments<VHelper>(Span<TValue?> first, Span<TValue?> second)
        where VHelper : ITypeHelper<TValue> {
        ReleaseSegment<VHelper>(first);
        ReleaseSegment<VHelper>(second);
    }

    private static void WriteSegmentForward<VHelper>(BinaryDiffWriter writer, Span<TValue?> segment)
        where VHelper : ITypeHelper<TValue> {
        foreach (var value in segment) {
            VHelper.Write(writer, value, false);
        }
    }

    private static void WriteSegmentReverse<VHelper>(BinaryDiffWriter writer, Span<TValue?> segment)
        where VHelper : ITypeHelper<TValue> {
        for (int i = segment.Length; --i >= 0;) {
            VHelper.Write(writer, segment[i], false);
        }
    }

    private static void ReleaseSegment<VHelper>(Span<TValue?> segment)
        where VHelper : ITypeHelper<TValue> {
        foreach (var value in segment) {
            VHelper.ReleaseSlot(value);
        }
    }

    private static void CopySegments(
        Span<TValue?> sourceFirst,
        Span<TValue?> sourceSecond,
        Span<TValue?> destFirst,
        Span<TValue?> destSecond
    ) {
        CopyInto(ref sourceFirst, ref sourceSecond, destFirst);
        CopyInto(ref sourceFirst, ref sourceSecond, destSecond);
        Debug.Assert(sourceFirst.IsEmpty && sourceSecond.IsEmpty, "all source items should have been copied.");
    }

    private static void CopySegments<VHelper>(
        Span<TValue?> sourceFirst,
        Span<TValue?> sourceSecond,
        Span<TValue?> destFirst,
        Span<TValue?> destSecond,
        bool freeze
    )
        where VHelper : ITypeHelper<TValue> {
        CopyInto<VHelper>(ref sourceFirst, ref sourceSecond, destFirst, freeze);
        CopyInto<VHelper>(ref sourceFirst, ref sourceSecond, destSecond, freeze);
        Debug.Assert(sourceFirst.IsEmpty && sourceSecond.IsEmpty, "all source items should have been copied.");
    }

    private static void CopyInto(ref Span<TValue?> currentSource, ref Span<TValue?> nextSource, Span<TValue?> destination) {
        int written = 0;
        while (written < destination.Length) {
            if (currentSource.IsEmpty) {
                currentSource = nextSource;
                nextSource = [];
                continue;
            }

            int take = Math.Min(currentSource.Length, destination.Length - written);
            currentSource[..take].CopyTo(destination.Slice(written, take));
            currentSource = currentSource[take..];
            written += take;
        }
    }

    private static void CopyInto<VHelper>(ref Span<TValue?> currentSource, ref Span<TValue?> nextSource, Span<TValue?> destination, bool freeze)
        where VHelper : ITypeHelper<TValue> {
        int written = 0;
        while (written < destination.Length) {
            if (currentSource.IsEmpty) {
                currentSource = nextSource;
                nextSource = [];
                continue;
            }

            int take = Math.Min(currentSource.Length, destination.Length - written);
            var sourceSlice = currentSource[..take];
            var destSlice = destination.Slice(written, take);
            if (freeze) {
                for (int i = 0; i < take; ++i) {
                    destSlice[i] = VHelper.Freeze(sourceSlice[i]);
                }
            }
            else {
                sourceSlice.CopyTo(destSlice);
            }

            currentSource = currentSource[take..];
            written += take;
        }
    }

    private static TValue? ReadValue<VHelper>(ref BinaryDiffReader reader)
        where VHelper : unmanaged, ITypeHelper<TValue> {
        TValue? value = default;
        VHelper.UpdateOrInit(ref reader, ref value);
        return value;
    }

    [Conditional("DEBUG")]
    private void AssertTrackedInvariant<VHelper>()
        where VHelper : ITypeHelper<TValue> {
        Debug.Assert(0 <= _oldKeepLo && _oldKeepLo <= _committed.Count);
        Debug.Assert(0 <= _newKeepLo && _newKeepLo <= _current.Count);
        Debug.Assert(0 <= _keepCount);
        Debug.Assert(OldKeepHi <= _committed.Count);
        Debug.Assert(NewKeepHi <= _current.Count);

        // 验证脏位图中的所有位都在 keep window [_oldKeepLo, OldKeepHi) 内且严格递增
        int previousDirtyCommitted = -1;
        foreach (int committedIndex in _committedDirtyMap.Ones()) {
            Debug.Assert(committedIndex >= _oldKeepLo && committedIndex < OldKeepHi,
                "dirty bit must be inside keep window on committed."
            );
            Debug.Assert(committedIndex > previousDirtyCommitted,
                "dirty bits must be strictly increasing."
            );
            previousDirtyCommitted = committedIndex;
        }

        // 验证 keep window 中未标脏的位置 current 与 committed 保持相等
        var dirtyEnumerator = _committedDirtyMap.Ones();
        int nextDirtyCommitted = dirtyEnumerator.MoveNext() ? dirtyEnumerator.Current : -1;
        for (int i = 0; i < _keepCount; ++i) {
            int ci = _oldKeepLo + i;
            if (ci == nextDirtyCommitted) {
                nextDirtyCommitted = dirtyEnumerator.MoveNext() ? dirtyEnumerator.Current : -1;
                continue;
            }

            Debug.Assert(
                VHelper.Equals(_current[_newKeepLo + i], _committed[ci]),
                "keep window 中未标记 patch 的位置必须保持相等。"
            );
        }
    }
}
