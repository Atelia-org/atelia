using System.Diagnostics;
using System.Runtime.InteropServices;
using Atelia.StateJournal.Serialization;

namespace Atelia.StateJournal.Internal;

internal struct DictChangeTracker<TKey, TValue>
where TKey : notnull
where TValue : notnull {
    private struct EstimateSummary {
        public uint CommittedPayloadBareBytes;
        public uint CurrentPayloadBareBytes;
        public uint DirtyRemovedKeyBareBytes;
        public uint DirtyUpsertPayloadBareBytes;
    }

    // === 内部状态（双字典策略） ===
    private Dictionary<TKey, TValue?>? _committed;  // 上次 commit 时的状态；null 表示 frozen（作为 frozen sentinel 替代旧 _isFrozen）
    private Dictionary<TKey, TValue?> _current;    // 当前工作状态 / frozen 只读视图
    private BitDivision<TKey>? _dirtyKeys; // 发生变更的 key 集合, false = remove; true = upsert；null 表示 frozen
    private EstimateSummary _estimate;

    /// <summary>
    /// 冻结 sentinel：<c>_committed is null</c> 等价于 frozen 存储形态。
    /// 对象级 frozen 真源仍在 <see cref="DurableObject"/> 基类；此处仅表达 tracker 是否已释放 mutable backing store。
    /// </summary>
    public readonly bool IsFrozen => _committed is null;

    private readonly Dictionary<TKey, TValue?> Committed => _committed ?? throw new InvalidOperationException("Frozen DictChangeTracker has no mutable committed dictionary.");
    private readonly BitDivision<TKey> DirtyKeys => _dirtyKeys ?? throw new InvalidOperationException("Frozen DictChangeTracker has no dirty-key tracker.");
    private void ThrowIfFrozen() {
        if (_committed is null) {
            // 防御 default-initialized 无效实例：合法的 frozen tracker 必须有 _current。
            Debug.Assert(_current is not null, "DictChangeTracker appears frozen but _current is null — likely a default-initialized (invalid) instance.");
            throw new InvalidOperationException("Frozen DictChangeTracker cannot be modified.");
        }
    }

    /// <summary>
    /// DEBUG-only：验证 frozen sentinel 一致性。
    /// <c>_committed is null</c> 必须与 <c>_dirtyKeys is null</c> 同真同假。
    /// </summary>
    [Conditional("DEBUG")]
    private readonly void AssertFrozenSentinelConsistent() {
        Debug.Assert(
            (_committed is null) == (_dirtyKeys is null),
            "DictChangeTracker frozen sentinel drift: _committed and _dirtyKeys nullity must agree."
        );
    }

    private void MarkSame(TKey key) => DirtyKeys.Remove(key);
    private void MarkRemove(TKey key) => DirtyKeys.SetFalse(key);
    private void MarkUpsert(TKey key) => DirtyKeys.SetTrue(key);
    private bool HasUpsert(TKey key) => DirtyKeys.TryGetSubset(key, out bool isUpsert) && isUpsert;
    #region 可用于单元测试
    public BitDivision<TKey>.Enumerator RemovedKeys => DirtyKeys.FalseKeys;
    public BitDivision<TKey>.Enumerator UpsertedKeys => DirtyKeys.TrueKeys;
    public int RemoveCount => IsFrozen ? 0 : DirtyKeys.FalseCount;
    public int UpsertCount => IsFrozen ? 0 : DirtyKeys.TrueCount;
    public bool HasChanges => !IsFrozen && DirtyKeys.Count > 0;
    #endregion

    /// <summary>估算 rebase 帧所需的 bare 字节数（不含 frame overhead 与 typeCode）。</summary>
    public uint EstimatedRebaseBytes<KHelper, VHelper>()
    where KHelper : unmanaged, ITypeHelper<TKey>
    where VHelper : unmanaged, ITypeHelper<TValue> {
        AssertEstimateSummaryConsistent<KHelper, VHelper>();
        return checked(
            CostEstimateUtil.VarIntSize(0u)
            + CostEstimateUtil.VarIntSize((uint)_current.Count)
            + _estimate.CurrentPayloadBareBytes
        );
    }

    /// <summary>估算 deltify 帧所需的 bare 字节数（不含 frame overhead）。</summary>
    public uint EstimatedDeltifyBytes<KHelper, VHelper>()
    where KHelper : unmanaged, ITypeHelper<TKey>
    where VHelper : unmanaged, ITypeHelper<TValue> {
        AssertEstimateSummaryConsistent<KHelper, VHelper>();
        return checked(
            CostEstimateUtil.VarIntSize((uint)RemoveCount)
            + _estimate.DirtyRemovedKeyBareBytes
            + CostEstimateUtil.VarIntSize((uint)UpsertCount)
            + _estimate.DirtyUpsertPayloadBareBytes
        );
    }

    /// <summary>
    /// DEBUG-only：从 _committed/_current/_dirtyKeys 全量重算 EstimateSummary，
    /// 与增量维护的 _estimate 比对，用于尽早发现 summary drift（参考设计 7.1 节）。
    /// </summary>
    [Conditional("DEBUG")]
    private readonly void AssertEstimateSummaryConsistent<KHelper, VHelper>()
    where KHelper : ITypeHelper<TKey>
    where VHelper : ITypeHelper<TValue> {
        var expected = RecomputeEstimateSummarySlow<KHelper, VHelper>();
        Debug.Assert(
            expected.CommittedPayloadBareBytes == _estimate.CommittedPayloadBareBytes
            && expected.CurrentPayloadBareBytes == _estimate.CurrentPayloadBareBytes
            && expected.DirtyRemovedKeyBareBytes == _estimate.DirtyRemovedKeyBareBytes
            && expected.DirtyUpsertPayloadBareBytes == _estimate.DirtyUpsertPayloadBareBytes,
            $"DictChangeTracker EstimateSummary drift: "
                + $"committed {_estimate.CommittedPayloadBareBytes} vs {expected.CommittedPayloadBareBytes}, "
                + $"current {_estimate.CurrentPayloadBareBytes} vs {expected.CurrentPayloadBareBytes}, "
                + $"dirtyRemoved {_estimate.DirtyRemovedKeyBareBytes} vs {expected.DirtyRemovedKeyBareBytes}, "
                + $"dirtyUpsert {_estimate.DirtyUpsertPayloadBareBytes} vs {expected.DirtyUpsertPayloadBareBytes}."
        );
    }

    private readonly EstimateSummary RecomputeEstimateSummarySlow<KHelper, VHelper>()
    where KHelper : ITypeHelper<TKey>
    where VHelper : ITypeHelper<TValue> {
        var summary = new EstimateSummary {
            CurrentPayloadBareBytes = SumDictionaryPayloadBareBytes<KHelper, VHelper>(_current),
        };
        if (IsFrozen) {
            // frozen 视图：committed 等于 current；dirty 项必须为零。
            summary.CommittedPayloadBareBytes = summary.CurrentPayloadBareBytes;
            return summary;
        }
        summary.CommittedPayloadBareBytes = SumDictionaryPayloadBareBytes<KHelper, VHelper>(Committed);
        foreach (var key in DirtyKeys.FalseKeys) {
            summary.DirtyRemovedKeyBareBytes = checked(summary.DirtyRemovedKeyBareBytes + KHelper.EstimateBareSize(key, asKey: true));
        }
        foreach (var key in DirtyKeys.TrueKeys) {
            // dirty upsert 应当出现在 _current 中。
            TValue? value = _current[key];
            summary.DirtyUpsertPayloadBareBytes = checked(summary.DirtyUpsertPayloadBareBytes + EstimateEntryBareBytes<KHelper, VHelper>(key, value));
        }
        return summary;
    }

    /// <summary>内部读写接口。当集合变更后需要调用<see cref="AfterUpsert"/>与<see cref="AfterRemove"/>。</summary>
    internal Dictionary<TKey, TValue?> Current => _current;

    /// <summary>上次 commit 时的 key 集合（只读视图）。在 <see cref="Commit{VHelper}"/> 前保持不变。</summary>
    internal IReadOnlyCollection<TKey> CommittedKeys => (_committed ?? _current).Keys;

    public DictChangeTracker() {
        _committed = new();
        _current = new();
        _dirtyKeys = new();
    }

    public DictChangeTracker<TKey, TValue> ForkMutableFromCommitted<KHelper, VHelper>()
    where KHelper : unmanaged, ITypeHelper<TKey>
    where VHelper : unmanaged, ITypeHelper<TValue> {
        var fork = new DictChangeTracker<TKey, TValue>();
        var source = IsFrozen ? _current : Committed;
        foreach (var (key, value) in source) {
            TKey forkKey = KHelper.ForkFrozenForNewOwner(key)!;
            TValue? forkValue = value is null ? default : VHelper.ForkFrozenForNewOwner(value);
            fork.Committed.Add(forkKey, forkValue);
            fork._current.Add(forkKey, forkValue);
            uint entryBareBytes = EstimateEntryBareBytes<KHelper, VHelper>(forkKey, forkValue);
            fork._estimate.CommittedPayloadBareBytes = checked(fork._estimate.CommittedPayloadBareBytes + entryBareBytes);
            fork._estimate.CurrentPayloadBareBytes = checked(fork._estimate.CurrentPayloadBareBytes + entryBareBytes);
        }
        return fork;
    }

    /// <summary>
    /// typed / durable-ref 路径的一步写入入口：
    /// 在 tracker 内部完成 get-ref、current no-op 短路，以及 dirty/canonicalize 维护。
    /// </summary>
    public UpsertStatus Upsert<KHelper, VHelper>(TKey key, TValue? value)
    where KHelper : unmanaged, ITypeHelper<TKey>
    where VHelper : unmanaged, ITypeHelper<TValue> {
        ThrowIfFrozen();
        Debug.Assert(
            !VHelper.NeedRelease,
            "一体化 Upsert 仅适用于无额外所有权释放语义的 typed/durable-ref 路径；mixed/ValueBox 仍应走二阶段 Update + AfterUpsert。"
        );

        ref TValue? slot = ref CollectionsMarshal.GetValueRefOrAddDefault(_current, key, out bool exists);
        if (exists && VHelper.Equals(slot, value)) { return UpsertStatus.Updated; }

        uint keyBareBytes = EstimateKeyBareBytes<KHelper>(key);
        // 在覆写 slot 之前先计 oldEntryBytes；typed 路径下 TValue? 是 reference/value 拷贝，
        // 不存在 stale 问题，但仍保持“调用方负责捕获”的统一契约。
        uint oldEntryBareBytes = exists
            ? checked(keyBareBytes + VHelper.EstimateBareSize(slot, asKey: false))
            : 0u;
        slot = value;
        AfterUpsert<VHelper>(key, oldEntryBareBytes, exists, value, keyBareBytes);
        return exists ? UpsertStatus.Updated : UpsertStatus.Inserted;
    }

    public void AfterUpsert<VHelper>(TKey key, uint oldEntryBareBytesBeforeMutation, bool existed, TValue? value, uint keyBareBytes)
    where VHelper : unmanaged, ITypeHelper<TValue> {
        ThrowIfFrozen();
        Debug.Assert(existed || oldEntryBareBytesBeforeMutation == 0u, "新建路径下 oldEntryBareBytesBeforeMutation 必须为 0。");
        uint oldEntryBareBytes = oldEntryBareBytesBeforeMutation;
        RemoveDirtyContribution(key, oldEntryBareBytes, keyBareBytes);

        if (Committed.TryGetValue(key, out TValue? committedValue) && VHelper.Equals(value, committedValue)) {
            // 新值语义等于 committed → 释放新值的独占 slot，恢复为 committed 的冻结副本
            VHelper.ReleaseSlot(value);
            _current[key] = committedValue;
            uint committedEntryBareBytes = EstimateEntryBareBytes<VHelper>(keyBareBytes, committedValue);
            _estimate.CurrentPayloadBareBytes = checked(_estimate.CurrentPayloadBareBytes - oldEntryBareBytes + committedEntryBareBytes);
            MarkSame(key);
        }
        else {
            uint newEntryBareBytes = EstimateEntryBareBytes<VHelper>(keyBareBytes, value);
            _estimate.CurrentPayloadBareBytes = checked(_estimate.CurrentPayloadBareBytes - oldEntryBareBytes + newEntryBareBytes);
            _estimate.DirtyUpsertPayloadBareBytes = checked(_estimate.DirtyUpsertPayloadBareBytes + newEntryBareBytes);
            MarkUpsert(key);
        }
    }

    /// <summary>
    /// 通知 tracker 一个键已从 _current 中移除，更新增量 estimate 与 dirty 状态。
    /// </summary>
    /// <param name="removedValue">
    /// 被移除的值，仅用于在 wasUpsert 路径下释放调用方此前 dirty-upsert 时分配的独占 slot。
    /// 大小核算不再读它（避免 inplace 覆写后的 stale read），改由 <paramref name="removedEntryBareBytesBeforeMutation"/> 显式传递。
    /// </param>
    /// <param name="removedEntryBareBytesBeforeMutation">
    /// 在 _current.Remove 之前 (slot 还活的时刻) 对 <c>keyBareBytes + EstimateBareSize(removedValue)</c> 的快照。
    /// </param>
    public void AfterRemove<VHelper>(TKey key, TValue? removedValue, uint removedEntryBareBytesBeforeMutation, uint keyBareBytes)
    where VHelper : unmanaged, ITypeHelper<TValue> {
        ThrowIfFrozen();
        uint removedEntryBareBytes = removedEntryBareBytesBeforeMutation;
        bool hadDirtySubset = DirtyKeys.TryGetSubset(key, out bool wasUpsert);
        if (VHelper.NeedRelease && hadDirtySubset && wasUpsert) {
            VHelper.ReleaseSlot(removedValue);
        }

        _estimate.CurrentPayloadBareBytes = checked(_estimate.CurrentPayloadBareBytes - removedEntryBareBytes);
        RemoveDirtyContribution(key, removedEntryBareBytes, keyBareBytes);

        if (Committed.ContainsKey(key)) {
            _estimate.DirtyRemovedKeyBareBytes = checked(_estimate.DirtyRemovedKeyBareBytes + keyBareBytes);
            MarkRemove(key);
        }
        else {
            MarkSame(key);
        }
    }

    public void Revert<VHelper>()
    where VHelper : unmanaged, ITypeHelper<TValue> {
        ThrowIfFrozen();
        if (DirtyKeys.Count == 0) { return; }

        // 根据 dirtyKeys 将 _current 局部还原为 _committed 的状态
        foreach (var key in RemovedKeys) {
            // 被移除的：从 _committed 恢复到 _current（共享 frozen 值）
            _current[key] = Committed[key];
        }

        foreach (var key in UpsertedKeys) {
            // 释放 current 的独占 slot（仅对有堆资源的类型有实际效果）
            VHelper.ReleaseSlot(_current[key]);
            // 被新增/修改的：如果原本存在则恢复旧值，原本不存在则是纯新增，直接移除
            if (Committed.TryGetValue(key, out var oldValue)) {
                _current[key] = oldValue;
            }
            else {
                _current.Remove(key);
            }
        }

        DirtyKeys.Clear();
        _estimate.CurrentPayloadBareBytes = _estimate.CommittedPayloadBareBytes;
        _estimate.DirtyRemovedKeyBareBytes = 0;
        _estimate.DirtyUpsertPayloadBareBytes = 0;
    }

    public void Commit<VHelper>()
    where VHelper : unmanaged, ITypeHelper<TValue> {
        if (IsFrozen) { return; }
        if (DirtyKeys.Count == 0) { return; }

        // 根据 dirtyKeys 将变动局部增量应用到 _committed
        foreach (var key in RemovedKeys) {
            // 被移除的：释放 committed 的旧 frozen slot，然后从 _committed 中剔除
            VHelper.ReleaseSlot(Committed[key]);
            Committed.Remove(key);
        }

        foreach (var key in UpsertedKeys) {
            // 释放 committed 中的旧值（如有）
            if (Committed.TryGetValue(key, out var oldCommitted)) {
                VHelper.ReleaseSlot(oldCommitted);
            }
            // 冻结 current 值并共享给两个字典
            var frozen = VHelper.Freeze(_current[key]);
            Committed[key] = frozen;
            _current[key] = frozen;
        }

        DirtyKeys.Clear();
        _estimate.CommittedPayloadBareBytes = _estimate.CurrentPayloadBareBytes;
        _estimate.DirtyRemovedKeyBareBytes = 0;
        _estimate.DirtyUpsertPayloadBareBytes = 0;
    }

    public void FreezeFromClean<VHelper>()
    where VHelper : unmanaged, ITypeHelper<TValue> {
        ThrowIfFrozen();
        if (DirtyKeys.Count != 0) { throw new InvalidOperationException("Cannot FreezeFromClean while dict has changes."); }
        // 防御性冻结 _current 中的所有值，确保 frozen tracker 不会持有 unfrozen/Exclusive slot。
        // 对已冻结的值 Freeze() 幂等；对 ValueBox 等有所有权语义的类型必不可少。
        foreach (var key in _current.Keys) {
            _current[key] = VHelper.Freeze(_current[key]);
        }
        _committed = null;
        _dirtyKeys = null;
        AssertFrozenSentinelConsistent();
    }

    public void FreezeFromCurrent<VHelper>()
    where VHelper : unmanaged, ITypeHelper<TValue> {
        ThrowIfFrozen();
        Commit<VHelper>();
        FreezeFromClean<VHelper>();
    }

    public void UnfreezeToMutableClean<VHelper>()
    where VHelper : unmanaged, ITypeHelper<TValue> {
        if (!IsFrozen) { throw new InvalidOperationException("DictChangeTracker is not frozen."); }
        _committed = new(_current);
        _dirtyKeys = new();
        _estimate.CommittedPayloadBareBytes = _estimate.CurrentPayloadBareBytes;
        _estimate.DirtyRemovedKeyBareBytes = 0;
        _estimate.DirtyUpsertPayloadBareBytes = 0;
        AssertFrozenSentinelConsistent();
    }

    public void MaterializeFrozenFromReconstructedCommitted<VHelper>()
    where VHelper : unmanaged, ITypeHelper<TValue> {
        ThrowIfFrozen();
        Debug.Assert(_current.Count == 0, "MaterializeFrozenFromReconstructedCommitted 应在空 _current 上调用。");
        _current = Committed;
        foreach (var key in _current.Keys) {
            _current[key] = VHelper.Freeze(_current[key]);
        }
        _estimate.CurrentPayloadBareBytes = _estimate.CommittedPayloadBareBytes;
        _estimate.DirtyRemovedKeyBareBytes = 0;
        _estimate.DirtyUpsertPayloadBareBytes = 0;
        _committed = null;
        _dirtyKeys = null;
        AssertFrozenSentinelConsistent();
    }

    public void WriteDeltify<KHelper, VHelper>(BinaryDiffWriter writer, DiffWriteContext context)
    where KHelper : ITypeHelper<TKey>
    where VHelper : ITypeHelper<TValue> {
        if (IsFrozen) {
            writer.WriteCount(0);
            writer.WriteCount(0);
            return;
        }
        // 使用 ITypedHelper 和 IDiffWriter 序列化
        writer.WriteCount(RemoveCount);
        foreach (var key in RemovedKeys) {
            Debug.Assert(!_current.ContainsKey(key));
            Debug.Assert(Committed.ContainsKey(key));
            KHelper.Write(writer, key, true);
        }

        writer.WriteCount(UpsertCount);
        foreach (var key in UpsertedKeys) {
            var keyInCurrent = _current.TryGetValue(key, out var value);
            Debug.Assert(keyInCurrent);
            Debug.Assert(
                !Committed.TryGetValue(key, out var oldValue) // 新 key，合法
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
        DictDiffApplier.Apply<TKey, TValue, KHelper, VHelper>(ref reader, Committed);
        _estimate.CommittedPayloadBareBytes = SumDictionaryPayloadBareBytes<KHelper, VHelper>(Committed);
        _estimate.CurrentPayloadBareBytes = 0;
        _estimate.DirtyRemovedKeyBareBytes = 0;
        _estimate.DirtyUpsertPayloadBareBytes = 0;
    }

    /// <summary>
    /// Load 完成后将 _committed 同步到 _current，使公共 API 可访问已加载的数据。
    /// 对需要 copy-on-write 语义的类型（如 ValueBox），使用 Freeze 生成共享副本。
    /// </summary>
    public void SyncCurrentFromCommitted<VHelper>()
    where VHelper : unmanaged, ITypeHelper<TValue> {
        Debug.Assert(_current.Count == 0, "SyncCurrentFromCommitted 应在空 _current 上调用。");
        foreach (var (key, value) in Committed) {
            _current[key] = VHelper.Freeze(value);
        }
        _estimate.CurrentPayloadBareBytes = _estimate.CommittedPayloadBareBytes;
        _estimate.DirtyRemovedKeyBareBytes = 0;
        _estimate.DirtyUpsertPayloadBareBytes = 0;
    }

    internal IEnumerable<KeyValuePair<TKey, TValue?>> ReconstructedOrCurrent =>
        _current.Count != 0 || (_dirtyKeys?.Count ?? 0) != 0 ? _current : _committed ?? _current;

    private static uint EstimateKeyBareBytes<KHelper>(TKey key)
    where KHelper : ITypeHelper<TKey> =>
        KHelper.EstimateBareSize(key, asKey: true);

    private static uint EstimateEntryBareBytes<KHelper, VHelper>(TKey key, TValue? value)
    where KHelper : ITypeHelper<TKey>
    where VHelper : ITypeHelper<TValue> =>
        checked(EstimateKeyBareBytes<KHelper>(key) + VHelper.EstimateBareSize(value, asKey: false));

    private static uint EstimateEntryBareBytes<VHelper>(uint keyBareBytes, TValue? value)
    where VHelper : ITypeHelper<TValue> =>
        checked(keyBareBytes + VHelper.EstimateBareSize(value, asKey: false));

    private static uint SumDictionaryPayloadBareBytes<KHelper, VHelper>(Dictionary<TKey, TValue?> dict)
    where KHelper : ITypeHelper<TKey>
    where VHelper : ITypeHelper<TValue> {
        uint sum = 0;
        foreach (var (key, value) in dict) {
            sum = checked(sum + EstimateEntryBareBytes<KHelper, VHelper>(key, value));
        }
        return sum;
    }

    private void RemoveDirtyContribution(TKey key, uint oldEntryBareBytes, uint keyBareBytes) {
        if (!DirtyKeys.TryGetSubset(key, out bool wasUpsert)) { return; }
        if (wasUpsert) {
            _estimate.DirtyUpsertPayloadBareBytes = checked(_estimate.DirtyUpsertPayloadBareBytes - oldEntryBareBytes);
        }
        else {
            _estimate.DirtyRemovedKeyBareBytes = checked(_estimate.DirtyRemovedKeyBareBytes - keyBareBytes);
        }
    }
}
