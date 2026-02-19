using System.Buffers;

namespace Atelia.StateJournal3;

public abstract partial class DurableDict<TKey, TValue> : DurableObject, IDictionary<TKey, TValue>
    where TKey : notnull, ValueBox
    where TValue : DurableBase {

    // === 内部状态（双字典策略） ===
    private Dictionary<TKey, TValue> _committed;  // 上次 commit 时的状态
    private Dictionary<TKey, TValue> _current;    // 当前工作状态
    private HashSet<TKey> _dirtyKeys;     // 发生变更的 key 集合

    public DurableDict() {
        _committed = new Dictionary<TKey, TValue>();
        _current = new Dictionary<TKey, TValue>();
        _dirtyKeys = new HashSet<TKey>();
    }

    // === IDurableObject 实现 ===

    /// <inheritdoc/>
    /// <remarks>
    /// 复杂度 O(1)：直接检查 <c>_dirtyKeys.Count</c>。
    /// </remarks>
    /// <exception cref="ObjectDetachedException">对象已分离。</exception>
    public override bool HasChanges {
        get {
            EnsureReady();
            return _dirtyKeys.Count > 0;
        }
    }

    /// <summary>
    /// 尝试获取指定键的值。
    /// </summary>
    /// <param name="key">键。</param>
    /// <param name="value">输出的值（如找到）。</param>
    /// <returns>如果找到键则返回 true。</returns>
    /// <remarks>
    /// 如果内部存储为 <see cref="LocalId"/>，会自动调用 <see cref="DurableObjectBase.LoadObject{T}"/>
    /// 并返回 <see cref="IDurableObject"/> 实例。加载成功后会回填到 <c>_current</c>。
    /// </remarks>
    /// <exception cref="ObjectDetachedException">对象已分离。</exception>
    /// <exception cref="InvalidOperationException">Lazy Load 失败（引用对象不存在或已损坏）。</exception>
    public bool TryGetValue(TKey key, out TValue value) {
        EnsureReady();
        return _current.TryGetValue(key, out value);
    }

    /// <summary>
    /// 检查是否包含指定键。
    /// </summary>
    /// <param name="key">键。</param>
    /// <returns>如果包含键则返回 true。</returns>
    /// <exception cref="ObjectDetachedException">对象已分离。</exception>
    public bool ContainsKey(TKey key) {
        EnsureReady();
        return _current.ContainsKey(key);
    }

    /// <summary>
    /// 获取或设置指定键的值。
    /// </summary>
    /// <param name="key">键。</param>
    /// <returns>值。</returns>
    /// <remarks>
    /// Getter 如果内部存储为 <see cref="LocalId"/>，会自动 Lazy Load。
    /// </remarks>
    /// <exception cref="KeyNotFoundException">获取时键不存在。</exception>
    /// <exception cref="ObjectDetachedException">对象已分离。</exception>
    /// <exception cref="InvalidOperationException">Lazy Load 失败。</exception>
    public TValue? this[TKey key] {
        get {
            EnsureReady();
            return _current[key];
        }
        set {
            Set(key, value);
        }
    }

    /// <summary>
    /// 获取当前条目数量。
    /// </summary>
    /// <exception cref="ObjectDetachedException">对象已分离。</exception>
    public int Count {
        get {
            EnsureReady();
            return _current.Count;
        }
    }

    /// <summary>
    /// 获取所有键的枚举。
    /// </summary>
    /// <exception cref="ObjectDetachedException">对象已分离。</exception>
    public ICollection<TKey> Keys {
        get {
            EnsureReady();
            return _current.Keys;
        }
    }

    /// <summary>
    /// 获取所有键值对的枚举。
    /// </summary>
    /// <remarks>
    /// 枚举时如果 value 为 <see cref="LocalId"/>，会自动 Lazy Load。
    /// </remarks>
    /// <exception cref="ObjectDetachedException">对象已分离。</exception>
    /// <exception cref="InvalidOperationException">Lazy Load 失败。</exception>
    public IEnumerable<KeyValuePair<TKey, TValue?>> Entries {
        get {
            EnsureReady();
            return _current;
        }
    }

    // === 写 API（只修改 _current，维护 _dirtyKeys） ===

    /// <summary>
    /// 设置指定键的值。
    /// </summary>
    /// <param name="key">键。</param>
    /// <param name="value">值。</param>
    /// <exception cref="ObjectDetachedException">对象已分离。</exception>
    public void Set(TKey key, TValue? value) {
        EnsureReady();

        _current[key] = value;
        UpdateDirtyKey(key);

        if (_dirtyKeys.Count > 0) {
            TransitionToDirty();
        }
    }

    /// <summary>
    /// 移除指定键。
    /// </summary>
    /// <param name="key">键。</param>
    /// <returns>如果键存在则返回 true。</returns>
    /// <remarks>
    /// Remove 直接从 <c>_current</c> 移除条目，不存储 tombstone。
    /// </remarks>
    /// <exception cref="ObjectDetachedException">对象已分离。</exception>
    public bool Remove(TKey key) {
        EnsureReady();

        var removed = _current.Remove(key);
        UpdateDirtyKey(key);

        if (_dirtyKeys.Count > 0) {
            TransitionToDirty();
        }

        return removed;
    }

    /// <summary>
    /// 维护 _dirtyKeys（参见 §3.4.2 规则）。
    /// </summary>
    /// <param name="key">变更的键。</param>
    /// <remarks>
    /// 使用 <see cref="AreValuesEqual"/> 进行比较，支持 InstanceId 和实例的语义等价判定。
    /// </remarks>
    private void UpdateDirtyKey(TKey key) {
        var hasCurrent = _current.TryGetValue(key, out var currentValue);
        var hasCommitted = _committed.TryGetValue(key, out var committedValue);

        bool isDifferent = (hasCurrent, hasCommitted) switch {
            (true, true) => !AreValuesEqual(currentValue, committedValue),
            (false, false) => false,
            _ => true
        };

        if (isDifferent) {
            _dirtyKeys.Add(key);
        }
        else {
            _dirtyKeys.Remove(key);
        }
    }

    /// <summary>
    /// 判断两个值是否语义等价。
    /// </summary>
    /// <param name="a">第一个值。</param>
    /// <param name="b">第二个值。</param>
    /// <returns>如果语义等价则返回 true。</returns>
    /// <remarks>
    /// ObjRef 等价判定：<see cref="LocalId"/> 和对应的 <see cref="IDurableObject"/> 实例是语义等价的。
    /// 例如：<c>InstanceId(42)</c> 等价于 <c>instance where instance.InstanceId == 42</c>。
    /// </remarks>
    private static bool AreValuesEqual(TValue? a, TValue? b) {
        // 快速路径：引用相等或都为 null
        if (ReferenceEquals(a, b)) { return true; }
        if (a is null || b is null) { return false; }

        // ObjRef 等价判定：InstanceId(x) ≡ instance where instance.InstanceId == x
        return (a, b) switch {

            (DurableObject objA, DurableObject objB) => objA.LocalId == objB.LocalId,
            _ => Equals(a, b)
        };
    }

    // === IDurableObject 方法（二阶段提交） ===

    /// <inheritdoc/>
    /// <remarks>
    /// Prepare 阶段：计算 diff 并写入 writer。
    /// 不更新 <c>_committed</c>/<c>_dirtyKeys</c>——状态追平由 <see cref="OnCommitSucceeded"/> 负责。
    /// </remarks>
    public override void WritePendingDiff(IBufferWriter<byte> writer) {
        EnsureReady();

        // Fast path: O(1)
        if (_dirtyKeys.Count == 0) { return; }

        // 收集所有变更的 key，按升序排列
        var sortedDirtyKeys = _dirtyKeys.OrderBy(k => k).ToList();

        // 使用 DiffPayloadWriter 序列化
        // var payloadWriter = new DiffPayloadWriter(writer);

        // foreach (var key in sortedDirtyKeys) {
        //     if (_current.TryGetValue(key, out var value)) {
        //         // current 有值 → Set
        //         WriteValue(ref payloadWriter, key, value);
        //     }
        //     else if (_committed.ContainsKey(key)) {
        //         // current 无值，committed 有值 → Delete (tombstone)
        //         payloadWriter.WriteTombstone(key);
        //     }
        //     // else: 两边都没有 → 不写（理论上不应在 dirtyKeys 中）
        // }

        // payloadWriter.Complete();
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Finalize 阶段：追平内存状态。
    /// <c>_committed = Clone(_current)</c>，清空 <c>_dirtyKeys</c>。
    /// </remarks>
    public override void OnCommitSucceeded() {
        EnsureReady();

        if (_dirtyKeys.Count == 0) { return; }

        _committed = new Dictionary<TKey, TValue>(_current);  // 潜拷贝
        _dirtyKeys.Clear();
        SetState(DurableObjectState.Clean);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// 状态机：
    /// <list type="bullet">
    ///   <item>Clean → DiscardChanges() → Clean (no-op)</item>
    ///   <item>PersistentDirty → DiscardChanges() → Clean</item>
    ///   <item>TransientDirty → DiscardChanges() → Detached</item>
    ///   <item>Detached → DiscardChanges() → Detached (no-op, 幂等)</item>
    /// </list>
    ///
    /// </remarks>
    public override void DiscardChanges() {
        switch (State) {
            case DurableObjectState.Clean:
                // No-op: 没有变更可丢弃
                return;

            case DurableObjectState.PersistentDirty:
                // 重置为 committed 状态
                _current = new Dictionary<TKey, TValue>(_committed);  // 深拷贝
                _dirtyKeys.Clear();
                SetState(DurableObjectState.Clean);
                return;

            case DurableObjectState.TransientDirty:
                // Detach: 标记为已分离，后续访问抛异常
                _current.Clear();
                _committed.Clear();
                _dirtyKeys.Clear();
                SetState(DurableObjectState.Detached);
                return;

            case DurableObjectState.Detached:
                // No-op: 幂等
                return;

            default:
                throw new InvalidOperationException($"Unknown state: {State}");
        }
    }

    // === 私有序列化方法 ===

    /// <summary>
    /// 根据值类型写入到 DiffPayloadWriter。
    /// </summary>
    // private static void WriteValue(ref DiffPayloadWriter writer, TKey key, TValue? value) {
    //     switch (value) {
    //         case null:
    //             writer.WriteNull(key);
    //             break;
    //         case long longVal:
    //             writer.WriteVarInt(key, longVal);
    //             break;
    //         case int intVal:
    //             writer.WriteVarInt(key, intVal);
    //             break;
    //         case ulong ulongVal:
    //             // [F-VERSIONINDEX-REUSE-DURABLEDICT]: VersionIndex 使用 Val_Ptr64 编码 ObjectVersionPtr
    //             writer.WritePtr64(key, ulongVal);
    //             break;
    //         default:
    //             throw new NotSupportedException(
    //                 $"Unsupported value type: {value.GetType()}. MVP only supports null, long, int, and ulong."
    //             );
    //     }
    // }

    // === 私有方法 ===

    /// <summary>
    /// 转换到 Dirty 状态。
    /// </summary>
    private void TransitionToDirty() {
        if (State == DurableObjectState.Clean) {
            SetState(DurableObjectState.PersistentDirty);
            NotifyDirty();  // 通知 Workspace 重新添加到 DirtySet
        }
    }

    // EnsureReady 已由基类提供
}
