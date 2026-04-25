using System.Diagnostics;
using System.Runtime.InteropServices;
using Atelia.StateJournal.Serialization;

namespace Atelia.StateJournal.Internal;

internal readonly struct DictChangeTracker<TKey, TValue>
where TKey : notnull
where TValue : notnull {

    // === 内部状态（双字典策略） ===
    private readonly Dictionary<TKey, TValue?> _committed;  // 上次 commit 时的状态
    private readonly Dictionary<TKey, TValue?> _current;    // 当前工作状态
    private readonly BitDivision<TKey> _dirtyKeys; // 发生变更的 key 集合, false = remove; true = upsert

    private void MarkSame(TKey key) => _dirtyKeys.Remove(key);
    private void MarkRemove(TKey key) => _dirtyKeys.SetFalse(key);
    private void MarkUpsert(TKey key) => _dirtyKeys.SetTrue(key);
    private bool HasUpsert(TKey key) => _dirtyKeys.TryGetSubset(key, out bool isUpsert) && isUpsert;
    #region 可用于单元测试
    public BitDivision<TKey>.Enumerator RemovedKeys => _dirtyKeys.FalseKeys;
    public BitDivision<TKey>.Enumerator UpsertedKeys => _dirtyKeys.TrueKeys;
    public int RemoveCount => _dirtyKeys.FalseCount;
    public int UpsertCount => _dirtyKeys.TrueCount;
    public int DeltifyCount => _dirtyKeys.Count;
    public int RebaseCount => _current.Count;
    public bool HasChanges => _dirtyKeys.Count > 0;
    #endregion

    /// <summary>内部读写接口。当集合变更后需要调用<see cref="AfterUpsert"/>与<see cref="AfterRemove"/>。</summary>
    internal Dictionary<TKey, TValue?> Current => _current;

    /// <summary>上次 commit 时的 key 集合（只读视图）。在 <see cref="Commit{VHelper}"/> 前保持不变。</summary>
    internal Dictionary<TKey, TValue?>.KeyCollection CommittedKeys => _committed.Keys;

    public DictChangeTracker() {
        _committed = new();
        _current = new();
        _dirtyKeys = new();
    }

    public DictChangeTracker<TKey, TValue> ForkMutableFromCommitted<KHelper, VHelper>()
    where KHelper : unmanaged, ITypeHelper<TKey>
    where VHelper : unmanaged, ITypeHelper<TValue> {
        var fork = new DictChangeTracker<TKey, TValue>();
        foreach (var (key, value) in _committed) {
            TKey forkKey = KHelper.ForkFrozenForNewOwner(key)!;
            TValue? forkValue = value is null ? default : VHelper.ForkFrozenForNewOwner(value);
            fork._committed.Add(forkKey, forkValue);
            fork._current.Add(forkKey, forkValue);
        }
        return fork;
    }

    /// <summary>
    /// typed / durable-ref 路径的一步写入入口：
    /// 在 tracker 内部完成 get-ref、current no-op 短路，以及 dirty/canonicalize 维护。
    /// </summary>
    public UpsertStatus Upsert<VHelper>(TKey key, TValue? value)
    where VHelper : unmanaged, ITypeHelper<TValue> {
        Debug.Assert(
            !VHelper.NeedRelease,
            "一体化 Upsert 仅适用于无额外所有权释放语义的 typed/durable-ref 路径；mixed/ValueBox 仍应走二阶段 Update + AfterUpsert。"
        );

        ref TValue? slot = ref CollectionsMarshal.GetValueRefOrAddDefault(_current, key, out bool exists);
        if (exists && VHelper.Equals(slot, value)) { return UpsertStatus.Updated; }

        slot = value;
        AfterUpsert<VHelper>(key, value);
        return exists ? UpsertStatus.Updated : UpsertStatus.Inserted;
    }

    public void AfterUpsert<VHelper>(TKey key, TValue? value)
    where VHelper : unmanaged, ITypeHelper<TValue> {
        if (_committed.TryGetValue(key, out TValue? committedValue) && VHelper.Equals(value, committedValue)) {
            // 新值语义等于 committed → 释放新值的独占 slot，恢复为 committed 的冻结副本
            VHelper.ReleaseSlot(value);
            _current[key] = committedValue;
            MarkSame(key);
        }
        else {
            MarkUpsert(key);
        }
    }

    public void AfterRemove<VHelper>(TKey key, TValue? removedValue)
    where VHelper : unmanaged, ITypeHelper<TValue> {
        if (VHelper.NeedRelease && HasUpsert(key)) {
            VHelper.ReleaseSlot(removedValue);
        }

        if (_committed.ContainsKey(key)) {
            MarkRemove(key);
        }
        else {
            MarkSame(key);
        }
    }

    public void Revert<VHelper>()
    where VHelper : unmanaged, ITypeHelper<TValue> {
        if (_dirtyKeys.Count == 0) { return; }

        // 根据 dirtyKeys 将 _current 局部还原为 _committed 的状态
        foreach (var key in RemovedKeys) {
            // 被移除的：从 _committed 恢复到 _current（共享 frozen 值）
            _current[key] = _committed[key];
        }

        foreach (var key in UpsertedKeys) {
            // 释放 current 的独占 slot（仅对有堆资源的类型有实际效果）
            VHelper.ReleaseSlot(_current[key]);
            // 被新增/修改的：如果原本存在则恢复旧值，原本不存在则是纯新增，直接移除
            if (_committed.TryGetValue(key, out var oldValue)) {
                _current[key] = oldValue;
            }
            else {
                _current.Remove(key);
            }
        }

        _dirtyKeys.Clear();
    }

    public void Commit<VHelper>()
    where VHelper : unmanaged, ITypeHelper<TValue> {
        if (_dirtyKeys.Count == 0) { return; }

        // 根据 dirtyKeys 将变动局部增量应用到 _committed
        foreach (var key in RemovedKeys) {
            // 被移除的：释放 committed 的旧 frozen slot，然后从 _committed 中剔除
            VHelper.ReleaseSlot(_committed[key]);
            _committed.Remove(key);
        }

        foreach (var key in UpsertedKeys) {
            // 释放 committed 中的旧值（如有）
            if (_committed.TryGetValue(key, out var oldCommitted)) {
                VHelper.ReleaseSlot(oldCommitted);
            }
            // 冻结 current 值并共享给两个字典
            var frozen = VHelper.Freeze(_current[key]);
            _committed[key] = frozen;
            _current[key] = frozen;
        }

        _dirtyKeys.Clear();
    }

    public void WriteDeltify<KHelper, VHelper>(BinaryDiffWriter writer, DiffWriteContext context)
    where KHelper : ITypeHelper<TKey>
    where VHelper : ITypeHelper<TValue> {
        // 使用 ITypedHelper 和 IDiffWriter 序列化
        writer.WriteCount(RemoveCount);
        foreach (var key in RemovedKeys) {
            Debug.Assert(!_current.ContainsKey(key));
            Debug.Assert(_committed.ContainsKey(key));
            KHelper.Write(writer, key, true);
        }

        writer.WriteCount(UpsertCount);
        foreach (var key in UpsertedKeys) {
            var keyInCurrent = _current.TryGetValue(key, out var value);
            Debug.Assert(keyInCurrent);
            Debug.Assert(
                !_committed.TryGetValue(key, out var oldValue) // 新 key，合法
                || !VHelper.Equals(value, oldValue) // 旧 key，值必须不同
            );
            KHelper.Write(writer, key, true);
            VHelper.Write(writer, value, false);
        }
    }

    public void WriteRebase<KHelper, VHelper>(BinaryDiffWriter writer, DiffWriteContext context)
    where KHelper : ITypeHelper<TKey>
    where VHelper : ITypeHelper<TValue> {
        // 使用 ITypedHelper 和 IDiffWriter 序列化
        writer.WriteCount(0);
        writer.WriteCount(_current.Count);
        foreach (var (key, value) in _current) {
            KHelper.Write(writer, key, true);
            VHelper.Write(writer, value, false);
        }
    }

    public void ApplyDelta<KHelper, VHelper>(ref BinaryDiffReader reader)
    where KHelper : ITypeHelper<TKey>
    where VHelper : ITypeHelper<TValue> {
        DictDiffApplier.Apply<TKey, TValue, KHelper, VHelper>(ref reader, _committed);
    }

    /// <summary>
    /// Load 完成后将 _committed 同步到 _current，使公共 API 可访问已加载的数据。
    /// 对需要 copy-on-write 语义的类型（如 ValueBox），使用 Freeze 生成共享副本。
    /// </summary>
    public void SyncCurrentFromCommitted<VHelper>()
    where VHelper : unmanaged, ITypeHelper<TValue> {
        Debug.Assert(_current.Count == 0, "SyncCurrentFromCommitted 应在空 _current 上调用。");
        foreach (var (key, value) in _committed) {
            _current[key] = VHelper.Freeze(value);
        }
    }
}
