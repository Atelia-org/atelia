using System.Diagnostics;
using Atelia.StateJournal.Serialization;

namespace Atelia.StateJournal.Internal;

internal struct DequeChangeTracker<TValue>
    where TValue : notnull {
    private readonly IndexedDeque<TValue?> _committed;
    private readonly IndexedDeque<TValue?> _current;

    // committed 中仍然幸存的连续窗口: [_oldKeepLo, OldKeepHi)
    private int _oldKeepLo;

    // 该窗口在 current 中对应的位置: [_newKeepLo, NewKeepHi)
    private int _newKeepLo;
    private int _keepCount;

    private int OldKeepHi => _oldKeepLo + _keepCount;
    private int NewKeepHi => _newKeepLo + _keepCount;
    private int DirtyPrefixCount => _newKeepLo;
    private int DirtySuffixCount => _current.Count - NewKeepHi;
    private bool HasSingletonDirtyDeque => _keepCount == 0 && _current.Count == 1;

    #region 可用于单元测试
    public int TrimFrontCount => _oldKeepLo;
    public int TrimBackCount => _committed.Count - OldKeepHi;
    public int PushFrontCount => _newKeepLo;
    public int PushBackCount => _current.Count - NewKeepHi;
    public int KeepCount => _keepCount;
    public int DeltifyCount => TrimFrontCount + TrimBackCount + PushFrontCount + PushBackCount;
    public int RebaseCount => _current.Count;
    public bool HasChanges => TrimFrontCount != 0 || TrimBackCount != 0 || PushFrontCount != 0 || PushBackCount != 0;
    internal IndexedDeque<TValue?> Current => _current;
    internal IndexedDeque<TValue?> Committed => _committed;
    #endregion

    public DequeChangeTracker() {
        _committed = new();
        _current = new();
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
                _oldKeepLo--;
                _keepCount++;
                AssertTrackedInvariant<VHelper>();
                return;
            }
        }

        _current.PushFront(value);
        _newKeepLo++;
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
                _keepCount++;
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

    public void SetFront<VHelper>(TValue? value)
        where VHelper : unmanaged, ITypeHelper<TValue> {
        if (_current.Count == 0) { throw new InvalidOperationException("Deque is empty."); }

        if (DirtyPrefixCount > 0) {
            SetDirtyFront<VHelper>(value);
            return;
        }

        if (_keepCount > 0) {
            TValue? committedValue = _committed[_oldKeepLo];
            if (VHelper.Equals(value, committedValue)) {
                if (VHelper.NeedRelease) {
                    VHelper.ReleaseSlot(value);
                }
            }
            else {
                _current[0] = value;
                _oldKeepLo++;
                _newKeepLo++;
                _keepCount--;
            }

            AssertTrackedInvariant<VHelper>();
            return;
        }

        SetDirtyFront<VHelper>(value);
    }

    public void SetBack<VHelper>(TValue? value)
        where VHelper : unmanaged, ITypeHelper<TValue> {
        if (_current.Count == 0) { throw new InvalidOperationException("Deque is empty."); }

        if (DirtySuffixCount > 0) {
            SetDirtyBack<VHelper>(value);
            return;
        }

        if (_keepCount > 0) {
            TValue? committedValue = _committed[OldKeepHi - 1];
            if (VHelper.Equals(value, committedValue)) {
                if (VHelper.NeedRelease) {
                    VHelper.ReleaseSlot(value);
                }
            }
            else {
                _current[_current.Count - 1] = value;
                _keepCount--;
            }

            AssertTrackedInvariant<VHelper>();
            return;
        }

        SetDirtyBack<VHelper>(value);
    }

    public TValue? PopFront<VHelper>()
        where VHelper : unmanaged, ITypeHelper<TValue> {
        if (_current.Count == 0) { throw new InvalidOperationException("Deque is empty."); }

        if (_newKeepLo > 0) {
            TValue? removed = _current.PopFront();
            if (VHelper.NeedRelease) {
                VHelper.ReleaseSlot(removed);
            }
            _newKeepLo--;
            AssertTrackedInvariant<VHelper>();
            return removed;
        }

        if (_keepCount > 0) {
            TValue? removed = _current.PopFront();
            _oldKeepLo++;
            _keepCount--;
            AssertTrackedInvariant<VHelper>();
            return removed;
        }

        TValue? suffixRemoved = _current.PopFront();
        if (VHelper.NeedRelease) {
            VHelper.ReleaseSlot(suffixRemoved);
        }
        AssertTrackedInvariant<VHelper>();
        return suffixRemoved;
    }

    public TValue? PopBack<VHelper>()
        where VHelper : unmanaged, ITypeHelper<TValue> {
        if (_current.Count == 0) { throw new InvalidOperationException("Deque is empty."); }

        if (DirtySuffixCount > 0) {
            TValue? removed = _current.PopBack();
            if (VHelper.NeedRelease) {
                VHelper.ReleaseSlot(removed);
            }
            AssertTrackedInvariant<VHelper>();
            return removed;
        }

        if (_keepCount > 0) {
            TValue? removed = _current.PopBack();
            _keepCount--;
            AssertTrackedInvariant<VHelper>();
            return removed;
        }

        TValue? prefixRemoved = _current.PopBack();
        if (VHelper.NeedRelease) {
            VHelper.ReleaseSlot(prefixRemoved);
        }
        _newKeepLo--;
        AssertTrackedInvariant<VHelper>();
        return prefixRemoved;
    }

    public void Revert<VHelper>()
        where VHelper : unmanaged, ITypeHelper<TValue> {
        if (!HasChanges) { return; }

        if (VHelper.NeedRelease) {
            ReleaseCurrentPrefixAndSuffix<VHelper>();
        }

        // O(delta): 裁掉 current 的 prefix/suffix，只留 keep 窗口
        _current.TrimBack(_current.Count - NewKeepHi);
        _current.TrimFront(_newKeepLo);

        // 从 committed 恢复被 trim 的前后部分
        for (int i = _oldKeepLo - 1; i >= 0; i--) {
            _current.PushFront(VHelper.Freeze(_committed[i]));
        }
        for (int i = OldKeepHi; i < _committed.Count; i++) {
            _current.PushBack(VHelper.Freeze(_committed[i]));
        }

        ResetTrackedWindow<VHelper>();
    }

    public void Commit<VHelper>()
        where VHelper : unmanaged, ITypeHelper<TValue> {
        if (!HasChanges) { return; }

        // O(delta): 只冻结 prefix 和 suffix（keep 窗口已是 frozen 共享引用）
        for (int i = 0; i < _newKeepLo; i++) {
            _current[i] = VHelper.Freeze(_current[i]);
        }
        for (int i = NewKeepHi; i < _current.Count; i++) {
            _current[i] = VHelper.Freeze(_current[i]);
        }

        // 释放 committed 中被 trim 的元素
        if (VHelper.NeedRelease) {
            for (int i = 0; i < _oldKeepLo; i++) {
                VHelper.ReleaseSlot(_committed[i]);
            }
            for (int i = OldKeepHi; i < _committed.Count; i++) {
                VHelper.ReleaseSlot(_committed[i]);
            }
        }

        // 裁掉 committed 的 trim 部分，只留 keep 窗口
        _committed.TrimBack(_committed.Count - OldKeepHi);
        _committed.TrimFront(_oldKeepLo);

        // 将 current 的 prefix/suffix 同步到 committed
        for (int i = _newKeepLo - 1; i >= 0; i--) {
            _committed.PushFront(_current[i]);
        }
        for (int i = NewKeepHi; i < _current.Count; i++) {
            _committed.PushBack(_current[i]);
        }

        ResetTrackedWindow<VHelper>();
    }

    public void WriteDeltify<VHelper>(BinaryDiffWriter writer, DiffWriteContext context)
        where VHelper : ITypeHelper<TValue> {
        writer.WriteCount(TrimFrontCount);
        writer.WriteCount(TrimBackCount);

        writer.WriteCount(PushFrontCount);
        // 逆序写 prefix，使 ApplyDelta 可边读边 PushFront，无需临时缓存。
        for (int i = _newKeepLo - 1; i >= 0; i--) {
            VHelper.Write(writer, _current[i], false);
        }

        writer.WriteCount(PushBackCount);
        for (int i = NewKeepHi; i < _current.Count; i++) {
            VHelper.Write(writer, _current[i], false);
        }
    }

    public void WriteRebase<VHelper>(BinaryDiffWriter writer, DiffWriteContext context)
        where VHelper : ITypeHelper<TValue> {
        // Rebase 也编码成 ApplyDelta 可直接消费的同形协议：
        // 在空 committed 上表现为“无 trim、无前缀、整段作为 pushBack”。
        writer.WriteCount(0);
        writer.WriteCount(0);
        writer.WriteCount(0);
        writer.WriteCount(_current.Count);
        for (int i = 0; i < _current.Count; i++) {
            VHelper.Write(writer, _current[i], false);
        }
    }

    public void ApplyDelta<VHelper>(ref BinaryDiffReader reader)
        where VHelper : unmanaged, ITypeHelper<TValue> {
        int trimFrontCount = reader.ReadCount();
        int trimBackCount = reader.ReadCount();
        int pushFrontCount = reader.ReadCount();

        if (trimFrontCount + trimBackCount > _committed.Count) { throw new InvalidDataException("Deque delta trims more elements than committed contains."); }

        for (int i = 0; i < trimFrontCount; i++) {
            TValue? removed = _committed.PopFront();
            if (VHelper.NeedRelease) {
                VHelper.ReleaseSlot(removed);
            }
        }

        for (int i = 0; i < trimBackCount; i++) {
            TValue? removed = _committed.PopBack();
            if (VHelper.NeedRelease) {
                VHelper.ReleaseSlot(removed);
            }
        }

        for (int i = 0; i < pushFrontCount; i++) {
            _committed.PushFront(VHelper.Freeze(ReadValue<VHelper>(ref reader)));
        }

        int pushBackCount = reader.ReadCount();
        for (int i = 0; i < pushBackCount; i++) {
            _committed.PushBack(VHelper.Freeze(ReadValue<VHelper>(ref reader)));
        }

        _current.Clear();
        _oldKeepLo = 0;
        _newKeepLo = 0;
        _keepCount = 0;
    }

    public void SyncCurrentFromCommitted<VHelper>()
        where VHelper : unmanaged, ITypeHelper<TValue> {
        Debug.Assert(_current.Count == 0, "SyncCurrentFromCommitted 应在空 _current 上调用。");
        for (int i = 0; i < _committed.Count; i++) {
            _current.PushBack(VHelper.Freeze(_committed[i]));
        }

        ResetTrackedWindow<VHelper>();
    }

    private void SetDirtyFront<VHelper>(TValue? value)
        where VHelper : unmanaged, ITypeHelper<TValue> {
        TValue? oldValue = _current[0];
        if (VHelper.Equals(value, oldValue)) {
            if (VHelper.NeedRelease) {
                VHelper.ReleaseSlot(value);
            }
            AssertTrackedInvariant<VHelper>();
            return;
        }

        if (TryAbsorbFrontNeighborIntoKeep<VHelper>(value, oldValue)) {
            AssertTrackedInvariant<VHelper>();
            return;
        }

        if (VHelper.NeedRelease) {
            VHelper.ReleaseSlot(oldValue);
        }

        _current[0] = value;
        AssertTrackedInvariant<VHelper>();
    }

    private void SetDirtyBack<VHelper>(TValue? value)
        where VHelper : unmanaged, ITypeHelper<TValue> {
        int lastIndex = _current.Count - 1;
        TValue? oldValue = _current[lastIndex];
        if (VHelper.Equals(value, oldValue)) {
            if (VHelper.NeedRelease) {
                VHelper.ReleaseSlot(value);
            }
            AssertTrackedInvariant<VHelper>();
            return;
        }

        if (TryAbsorbBackNeighborIntoKeep<VHelper>(value, oldValue, lastIndex)) {
            AssertTrackedInvariant<VHelper>();
            return;
        }

        if (VHelper.NeedRelease) {
            VHelper.ReleaseSlot(oldValue);
        }

        _current[lastIndex] = value;
        AssertTrackedInvariant<VHelper>();
    }

    private bool TryAbsorbFrontNeighborIntoKeep<VHelper>(TValue? value, TValue? oldValue)
        where VHelper : unmanaged, ITypeHelper<TValue> {
        if (_oldKeepLo == 0) { return false; }

        bool hasSingleDirtyPrefix = DirtyPrefixCount == 1;
        // keep window 为空时，唯一脏元素可能被当前状态归类到 suffix；此时从 front 改回 committed 也应恢复 clean。
        bool hasSingletonDirtyDequeClassifiedAsSuffix = DirtyPrefixCount == 0 && HasSingletonDirtyDeque;
        if (!hasSingleDirtyPrefix && !hasSingletonDirtyDequeClassifiedAsSuffix) { return false; }

        TValue? committedValue = _committed[_oldKeepLo - 1];
        if (!VHelper.Equals(value, committedValue)) { return false; }

        ReplaceDirtyEdgeWithCommitted<VHelper>(currentIndex: 0, committedValue, value, oldValue);
        _oldKeepLo--;
        if (hasSingleDirtyPrefix) {
            _newKeepLo--;
        }
        _keepCount++;
        return true;
    }

    private bool TryAbsorbBackNeighborIntoKeep<VHelper>(TValue? value, TValue? oldValue, int lastIndex)
        where VHelper : unmanaged, ITypeHelper<TValue> {
        bool hasSingleDirtySuffix = DirtySuffixCount == 1;
        // keep window 为空时，唯一脏元素可能被当前状态归类到 prefix；此时从 back 改回 committed 也应恢复 clean。
        bool hasSingletonDirtyDequeClassifiedAsPrefix = DirtySuffixCount == 0 && HasSingletonDirtyDeque;
        if (!hasSingleDirtySuffix && !hasSingletonDirtyDequeClassifiedAsPrefix) { return false; }
        if (OldKeepHi >= _committed.Count) { return false; }

        TValue? committedValue = _committed[OldKeepHi];
        if (!VHelper.Equals(value, committedValue)) { return false; }

        ReplaceDirtyEdgeWithCommitted<VHelper>(lastIndex, committedValue, value, oldValue);
        if (hasSingletonDirtyDequeClassifiedAsPrefix) {
            _newKeepLo--;
        }
        _keepCount++;
        return true;
    }

    private void ReplaceDirtyEdgeWithCommitted<VHelper>(int currentIndex, TValue? committedValue, TValue? incomingValue, TValue? oldValue)
        where VHelper : unmanaged, ITypeHelper<TValue> {
        if (VHelper.NeedRelease) {
            VHelper.ReleaseSlot(incomingValue);
            VHelper.ReleaseSlot(oldValue);
        }

        _current[currentIndex] = committedValue;
    }

    private void ResetTrackedWindow<VHelper>()
        where VHelper : unmanaged, ITypeHelper<TValue> {
        _oldKeepLo = 0;
        _newKeepLo = 0;
        _keepCount = _committed.Count;
        AssertTrackedInvariant<VHelper>();
    }

    private void ReleaseCurrentPrefixAndSuffix<VHelper>()
        where VHelper : unmanaged, ITypeHelper<TValue> {
        for (int i = 0; i < DirtyPrefixCount; i++) {
            VHelper.ReleaseSlot(_current[i]);
        }
        for (int i = NewKeepHi; i < _current.Count; i++) {
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

        for (int i = 0; i < _keepCount; i++) {
            Debug.Assert(
                VHelper.Equals(_current[_newKeepLo + i], _committed[_oldKeepLo + i]),
                "keep window 映射必须保持相等。"
            );
        }
    }
}
