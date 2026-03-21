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
    // 用于估算落盘大小
    public int DeltifyCount => PushFrontCount + PushBackCount + KeepDirtyCount;
    public int RebaseCount => _current.Count;
    public bool HasChanges =>
        _committed.Count != _keepCount // TrimFrontCount + TrimBackCount
        || _committedDirtyMap.PopCount != 0
        || _current.Count != _keepCount; // PushFrontCount + PushBackCount
    internal IndexedDeque<TValue?> Current => _current;
    internal IndexedDeque<TValue?> Committed => _committed;
    #endregion

    public DequeChangeTracker() {
        _committed = new();
        _current = new();
        _committedDirtyMap = new();
        _oldKeepLo = 0;
        _newKeepLo = 0;
        _keepCount = 0;
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
                --_oldKeepLo;
                ++_keepCount;
                AssertTrackedInvariant<VHelper>();
                return;
            }
        }

        _current.PushFront(value);
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
                ++_keepCount;
                AssertTrackedInvariant<VHelper>();
                return;
            }
        }

        _current.PushBack(value);
        AssertTrackedInvariant<VHelper>();
    }

    public TValue? PeekFront() {
        if (_current.Count == 0) { throw new InvalidOperationException("Deque is empty."); }
        return _current[0];
    }

    public TValue? PeekBack() {
        if (_current.Count == 0) { throw new InvalidOperationException("Deque is empty."); }
        return _current[_current.Count - 1];
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
    public void AfterSet<VHelper>(int currentIndex, ref TValue? value)
        where VHelper : unmanaged, ITypeHelper<TValue> {
        // 显式要求传入与 GetRef 配对得到的 ref，可在 DEBUG 下校验“就是这个槽位”，
        // 比仅比较值语义更能防止调用方误把别处的临时值传进来。
        Debug.Assert(Unsafe.AreSame(ref value, ref _current.GetRef(currentIndex)));

        if ((uint)(currentIndex - _newKeepLo) >= (uint)_keepCount) {
            TryAbsorbDirtyEdgeIntoKeep<VHelper>(currentIndex, value);
            AssertTrackedInvariant<VHelper>();
            return;
        }

        int keepRelativeIndex = currentIndex - _newKeepLo;
        int committedIndex = _oldKeepLo + keepRelativeIndex;
        TValue? committedValue = _committed[committedIndex];
        bool wasDirty = _committedDirtyMap.TestBit(committedIndex);

        if (VHelper.Equals(value, committedValue)) {
            if (VHelper.NeedRelease) {
                VHelper.ReleaseSlot(value);
            }

            _current[currentIndex] = committedValue;
            if (wasDirty) {
                _committedDirtyMap.ClearBit(committedIndex);
            }

            AssertTrackedInvariant<VHelper>();
            return;
        }

        _committedDirtyMap.SetBit(committedIndex);
        AssertTrackedInvariant<VHelper>();
    }

    public TValue? PopFront<VHelper>(out bool callerOwned)
        where VHelper : unmanaged, ITypeHelper<TValue> {
        if (_current.Count == 0) { throw new InvalidOperationException("Deque is empty."); }

        if (_newKeepLo > 0) {
            TValue? removed = _current.PopFront();
            --_newKeepLo;
            callerOwned = VHelper.NeedRelease;
            AssertTrackedInvariant<VHelper>();
            return removed;
        }

        if (_keepCount > 0) {
            TValue? removed = _current.PopFront();
            bool frontWasDirty = _committedDirtyMap.ClearBit(_oldKeepLo);
            ++_oldKeepLo;
            --_keepCount;
            callerOwned = VHelper.NeedRelease && frontWasDirty;
            AssertTrackedInvariant<VHelper>();
            return removed;
        }

        TValue? suffixRemoved = _current.PopFront();
        callerOwned = VHelper.NeedRelease;
        AssertTrackedInvariant<VHelper>();
        return suffixRemoved;
    }

    public TValue? PopBack<VHelper>(out bool callerOwned)
        where VHelper : unmanaged, ITypeHelper<TValue> {
        if (_current.Count == 0) { throw new InvalidOperationException("Deque is empty."); }

        if (DirtySuffixCount > 0) {
            TValue? removed = _current.PopBack();
            callerOwned = VHelper.NeedRelease;
            AssertTrackedInvariant<VHelper>();
            return removed;
        }

        if (_keepCount > 0) {
            TValue? removed = _current.PopBack();
            int backCommittedIndex = OldKeepHi - 1;
            bool backWasDirty = _committedDirtyMap.ClearBit(backCommittedIndex);
            --_keepCount;
            callerOwned = VHelper.NeedRelease && backWasDirty;
            AssertTrackedInvariant<VHelper>();
            return removed;
        }

        TValue? prefixRemoved = _current.PopBack();
        --_newKeepLo;
        callerOwned = VHelper.NeedRelease;
        AssertTrackedInvariant<VHelper>();
        return prefixRemoved;
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
        for (int i = _oldKeepLo; --i >= 0;) {
            _current.PushFront(VHelper.Freeze(_committed[i]));
        }
        for (int i = OldKeepHi; i < _committed.Count; ++i) {
            _current.PushBack(VHelper.Freeze(_committed[i]));
        }

        ResetTrackedWindow<VHelper>();
    }

    public void Commit<VHelper>()
        where VHelper : unmanaged, ITypeHelper<TValue> {
        if (!HasChanges) { return; }

        // O(delta): 只冻结 prefix 和 suffix（keep 窗口已是 frozen 共享引用）
        for (int i = 0; i < _newKeepLo; ++i) {
            _current[i] = VHelper.Freeze(_current[i]);
        }
        for (int i = NewKeepHi; i < _current.Count; ++i) {
            _current[i] = VHelper.Freeze(_current[i]);
        }
        CommitSparseKeepChanges<VHelper>();

        // 释放 committed 中被 trim 的元素
        if (VHelper.NeedRelease) {
            for (int i = 0; i < _oldKeepLo; ++i) {
                VHelper.ReleaseSlot(_committed[i]);
            }
            for (int i = OldKeepHi; i < _committed.Count; ++i) {
                VHelper.ReleaseSlot(_committed[i]);
            }
        }

        // 裁掉 committed 的 trim 部分，只留 keep 窗口
        _committed.TrimBack(_committed.Count - OldKeepHi);
        _committed.TrimFront(_oldKeepLo);

        // 将 current 的 prefix/suffix 同步到 committed
        for (int i = _newKeepLo; --i >= 0;) {
            _committed.PushFront(_current[i]);
        }
        for (int i = NewKeepHi; i < _current.Count; ++i) {
            _committed.PushBack(_current[i]);
        }

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
        for (int i = _newKeepLo; --i >= 0;) {
            VHelper.Write(writer, _current[i], false);
        }

        writer.WriteCount(PushBackCount);
        for (int i = NewKeepHi; i < _current.Count; ++i) {
            VHelper.Write(writer, _current[i], false);
        }
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
        for (int i = 0; i < _current.Count; ++i) {
            VHelper.Write(writer, _current[i], false);
        }
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
            for (int i = 0; i < trimFrontCount; ++i) {
                VHelper.ReleaseSlot(_committed[i]);
            }
            for (int i = _committed.Count - trimBackCount; i < _committed.Count; ++i) {
                VHelper.ReleaseSlot(_committed[i]);
            }
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
    }

    public void SyncCurrentFromCommitted<VHelper>()
        where VHelper : unmanaged, ITypeHelper<TValue> {
        Debug.Assert(_current.Count == 0, "SyncCurrentFromCommitted 应在空 _current 上调用。");
        for (int i = 0; i < _committed.Count; ++i) {
            _current.PushBack(VHelper.Freeze(_committed[i]));
        }

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

    private bool TryAbsorbDirtyEdgeIntoKeep<VHelper>(int currentIndex, TValue? value)
        where VHelper : unmanaged, ITypeHelper<TValue> =>
        TryAbsorbDirtyFrontIntoKeep<VHelper>(currentIndex, value)
        || TryAbsorbDirtyBackIntoKeep<VHelper>(currentIndex, value);

    private bool TryAbsorbDirtyFrontIntoKeep<VHelper>(int currentIndex, TValue? value)
        where VHelper : unmanaged, ITypeHelper<TValue> {
        if (_oldKeepLo == 0) { return false; }

        bool isStandardFront = currentIndex == _newKeepLo - 1;
        bool isEmptySuffixMatch = _keepCount == 0 && currentIndex == _newKeepLo;
        if (!isStandardFront && !isEmptySuffixMatch) { return false; }

        TValue? committedValue = _committed[_oldKeepLo - 1];
        if (!VHelper.Equals(value, committedValue)) { return false; }

        if (VHelper.NeedRelease) { VHelper.ReleaseSlot(value); }
        _current[currentIndex] = committedValue; // ReplaceDirtyEdgeWithCommitted

        --_oldKeepLo;
        if (isStandardFront) { --_newKeepLo; }
        ++_keepCount;
        return true;
    }

    private bool TryAbsorbDirtyBackIntoKeep<VHelper>(int currentIndex, TValue? value)
        where VHelper : unmanaged, ITypeHelper<TValue> {
        if (OldKeepHi >= _committed.Count) { return false; }

        bool isStandardBack = currentIndex == NewKeepHi;
        bool isEmptyPrefixMatch = _keepCount == 0 && currentIndex == NewKeepHi - 1;
        if (!isStandardBack && !isEmptyPrefixMatch) { return false; }

        TValue? committedValue = _committed[OldKeepHi];
        if (!VHelper.Equals(value, committedValue)) { return false; }

        if (VHelper.NeedRelease) { VHelper.ReleaseSlot(value); }
        _current[currentIndex] = committedValue; // ReplaceDirtyEdgeWithCommitted

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
        AssertTrackedInvariant<VHelper>();
    }

    private void ReleaseCurrentPrefixAndSuffix<VHelper>()
        where VHelper : unmanaged, ITypeHelper<TValue> {
        for (int i = 0; i < DirtyPrefixCount; ++i) {
            VHelper.ReleaseSlot(_current[i]);
        }
        for (int i = NewKeepHi; i < _current.Count; ++i) {
            VHelper.ReleaseSlot(_current[i]);
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
