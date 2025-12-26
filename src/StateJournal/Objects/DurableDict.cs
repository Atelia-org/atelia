// Source: Atelia.StateJournal - 持久化字典
// Spec: atelia/docs/StateJournal/mvp-design-v2.md §3.4.2, A.1
// Binding: atelia/docs/StateJournal/workspace-binding-spec.md

using System.Buffers;
using System.Collections.Generic;

namespace Atelia.StateJournal;

/// <summary>
/// 持久化字典，支持 ulong 键和 object? 值。
/// </summary>
/// <remarks>
/// <para>
/// 非泛型"文档容器"设计：DurableDict 不使用泛型参数，内部存储 object?。
/// 这符合"持久化容器作为文档"的定位，而非类型化容器。
/// </para>
/// <para>
/// 双字典策略（参见 §3.4.2 和 A.1 伪代码骨架）：
/// <list type="bullet">
///   <item><c>_committed</c>：上次 Commit 成功时的状态快照</item>
///   <item><c>_current</c>：当前的完整工作状态（Working State）</item>
///   <item><c>_dirtyKeys</c>：记录自上次 Commit 以来发生变更的 key 集合</item>
/// </list>
/// </para>
/// <para>
/// 读取只查 <c>_current</c>；写入只修改 <c>_current</c>。
/// Commit 时通过比较 <c>_committed</c> 与 <c>_current</c> 生成 diff。
/// </para>
/// <para>
/// 对应条款：
/// <list type="bullet">
///   <item><c>[A-DURABLEDICT-API-SIGNATURES]</c></item>
///   <item><c>[S-DURABLEDICT-KEY-ULONG-ONLY]</c></item>
///   <item><c>[S-WORKING-STATE-TOMBSTONE-FREE]</c></item>
///   <item><c>[S-WORKSPACE-OWNING-EXACTLY-ONE]</c></item>
/// </list>
/// </para>
/// </remarks>
public class DurableDict : DurableObjectBase {
    // === 内部状态（双字典策略） ===
    private Dictionary<ulong, object?> _committed;  // 上次 commit 时的状态
    private Dictionary<ulong, object?> _current;    // 当前工作状态
    private readonly HashSet<ulong> _dirtyKeys;     // 发生变更的 key 集合

    // === IDurableObject 实现 ===

    /// <inheritdoc/>
    /// <remarks>
    /// 复杂度 O(1)：直接检查 <c>_dirtyKeys.Count</c>。
    /// </remarks>
    /// <exception cref="ObjectDetachedException">对象已分离。</exception>
    public override bool HasChanges {
        get {
            ThrowIfDetached();
            return _dirtyKeys.Count > 0;
        }
    }

    // === 构造函数 ===

    /// <summary>
    /// 创建新的 DurableDict（TransientDirty 状态）。
    /// </summary>
    /// <param name="workspace">对象所属的 Workspace。</param>
    /// <param name="objectId">对象 ID。</param>
    /// <remarks>
    /// <para>
    /// 此构造函数为 internal，用户应通过 <see cref="Workspace.CreateObject{T}"/> 创建。
    /// </para>
    /// <para>
    /// 新创建的对象没有 Committed State，调用 <see cref="IDurableObject.DiscardChanges"/>
    /// 会使对象进入 <see cref="DurableObjectState.Detached"/> 状态。
    /// </para>
    /// </remarks>
    internal DurableDict(Workspace workspace, ulong objectId)
        : base(workspace, objectId) {
        _committed = new Dictionary<ulong, object?>();
        _current = new Dictionary<ulong, object?>();
        _dirtyKeys = new HashSet<ulong>();
    }

    /// <summary>
    /// 从 Committed State 加载（Clean 状态）。
    /// </summary>
    /// <param name="workspace">对象所属的 Workspace。</param>
    /// <param name="objectId">对象 ID。</param>
    /// <param name="committed">已提交的键值对（Materialize 的结果）。</param>
    /// <remarks>
    /// <para>
    /// 此构造函数为 internal，由 <see cref="Workspace.LoadObject{T}"/> 使用。
    /// </para>
    /// <para>
    /// <c>_current</c> 初始化为 <c>_committed</c> 的深拷贝。
    /// </para>
    /// </remarks>
    internal DurableDict(Workspace workspace, ulong objectId, Dictionary<ulong, object?> committed)
        : base(workspace, objectId, DurableObjectState.Clean) {
        _committed = committed ?? throw new ArgumentNullException(nameof(committed));
        _current = new Dictionary<ulong, object?>(committed);  // 深拷贝
        _dirtyKeys = new HashSet<ulong>();
    }

    // === 内部无绑定构造函数（仅供 VersionIndex 等 Well-Known 对象使用）===

    /// <summary>
    /// 创建无 Workspace 绑定的 DurableDict（用于 VersionIndex）。
    /// </summary>
    /// <param name="objectId">对象 ID。</param>
    /// <remarks>
    /// ⚠️ 此构造函数不绑定 Workspace，不支持 Lazy Load。仅供内部使用。
    /// </remarks>
    internal DurableDict(ulong objectId)
        : base(objectId, DurableObjectState.TransientDirty) {
        _committed = new Dictionary<ulong, object?>();
        _current = new Dictionary<ulong, object?>();
        _dirtyKeys = new HashSet<ulong>();
    }

    /// <summary>
    /// 从 Committed State 加载无 Workspace 绑定的 DurableDict（用于 VersionIndex）。
    /// </summary>
    /// <param name="objectId">对象 ID。</param>
    /// <param name="committed">已提交的键值对（Materialize 的结果）。</param>
    /// <remarks>
    /// ⚠️ 此构造函数不绑定 Workspace，不支持 Lazy Load。仅供内部使用。
    /// </remarks>
    internal DurableDict(ulong objectId, Dictionary<ulong, object?> committed)
        : base(objectId, DurableObjectState.Clean) {
        _committed = committed ?? throw new ArgumentNullException(nameof(committed));
        _current = new Dictionary<ulong, object?>(committed);  // 深拷贝
        _dirtyKeys = new HashSet<ulong>();
    }

    // === 读 API（只查 _current，支持透明 Lazy Loading） ===

    /// <summary>
    /// 尝试获取指定键的值。
    /// </summary>
    /// <param name="key">键。</param>
    /// <param name="value">输出的值（如找到）。</param>
    /// <returns>如果找到键则返回 true。</returns>
    /// <remarks>
    /// <para>
    /// 对应条款：<c>[A-OBJREF-TRANSPARENT-LAZY-LOAD]</c>
    /// </para>
    /// <para>
    /// 如果内部存储为 <see cref="ObjectId"/>，会自动调用 <see cref="DurableObjectBase.LoadObject{T}"/>
    /// 并返回 <see cref="IDurableObject"/> 实例。加载成功后会回填到 <c>_current</c>。
    /// </para>
    /// </remarks>
    /// <exception cref="ObjectDetachedException">对象已分离。</exception>
    /// <exception cref="InvalidOperationException">Lazy Load 失败（引用对象不存在或已损坏）。</exception>
    public bool TryGetValue(ulong key, out object? value) {
        ThrowIfDetached();
        if (!_current.TryGetValue(key, out value)) { return false; }
        value = ResolveAndBackfill(key, value);
        return true;
    }

    /// <summary>
    /// 检查是否包含指定键。
    /// </summary>
    /// <param name="key">键。</param>
    /// <returns>如果包含键则返回 true。</returns>
    /// <exception cref="ObjectDetachedException">对象已分离。</exception>
    public bool ContainsKey(ulong key) {
        ThrowIfDetached();
        return _current.ContainsKey(key);
    }

    /// <summary>
    /// 获取或设置指定键的值。
    /// </summary>
    /// <param name="key">键。</param>
    /// <returns>值。</returns>
    /// <remarks>
    /// <para>
    /// 对应条款：<c>[A-OBJREF-TRANSPARENT-LAZY-LOAD]</c>
    /// </para>
    /// <para>
    /// Getter 如果内部存储为 <see cref="ObjectId"/>，会自动 Lazy Load。
    /// </para>
    /// </remarks>
    /// <exception cref="KeyNotFoundException">获取时键不存在。</exception>
    /// <exception cref="ObjectDetachedException">对象已分离。</exception>
    /// <exception cref="InvalidOperationException">Lazy Load 失败。</exception>
    public object? this[ulong key] {
        get {
            ThrowIfDetached();
            if (_current.TryGetValue(key, out var value)) { return ResolveAndBackfill(key, value); }
            throw new KeyNotFoundException($"Key {key} not found in DurableDict.");
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
            ThrowIfDetached();
            return _current.Count;
        }
    }

    /// <summary>
    /// 获取所有键的枚举。
    /// </summary>
    /// <exception cref="ObjectDetachedException">对象已分离。</exception>
    public IEnumerable<ulong> Keys {
        get {
            ThrowIfDetached();
            return _current.Keys;
        }
    }

    /// <summary>
    /// 获取所有键值对的枚举。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 对应条款：<c>[A-OBJREF-TRANSPARENT-LAZY-LOAD]</c>
    /// </para>
    /// <para>
    /// 枚举时如果 value 为 <see cref="ObjectId"/>，会自动 Lazy Load。
    /// </para>
    /// </remarks>
    /// <exception cref="ObjectDetachedException">对象已分离。</exception>
    /// <exception cref="InvalidOperationException">Lazy Load 失败。</exception>
    public IEnumerable<KeyValuePair<ulong, object?>> Entries {
        get {
            ThrowIfDetached();
            return GetEntriesWithLazyLoad();
        }
    }

    /// <summary>
    /// 枚举时执行 Lazy Load。
    /// </summary>
    private IEnumerable<KeyValuePair<ulong, object?>> GetEntriesWithLazyLoad() {
        foreach (var kvp in _current) {
            var resolved = ResolveAndBackfill(kvp.Key, kvp.Value);
            yield return new KeyValuePair<ulong, object?>(kvp.Key, resolved);
        }
    }

    // === Lazy Loading 核心方法 ===

    /// <summary>
    /// 解析 value，如果是 ObjectId 则 Lazy Load 并回填。
    /// </summary>
    /// <param name="key">键（用于回填）。</param>
    /// <param name="value">原始值。</param>
    /// <returns>解析后的值（如果是 ObjectId 则返回加载的实例）。</returns>
    /// <remarks>
    /// <para>
    /// 对应条款：
    /// <list type="bullet">
    ///   <item><c>[A-OBJREF-TRANSPARENT-LAZY-LOAD]</c>: 透明 Lazy Load</item>
    ///   <item><c>[A-OBJREF-BACKFILL-CURRENT]</c>: 回填到 _current</item>
    ///   <item><c>[S-LAZYLOAD-DISPATCH-BY-OWNER]</c>: 按 Owning Workspace 分派</item>
    /// </list>
    /// </para>
    /// </remarks>
    private object? ResolveAndBackfill(ulong key, object? value) {
        // 如果不是 ObjectId，直接返回
        if (value is not ObjectId objectId) { return value; }

        // 透明 Lazy Load：按 Owning Workspace 分派
        var instance = LoadObject<IDurableObject>(objectId.Value);

        // 回填到 _current（不改变 dirty 状态，因为语义值未变）
        _current[key] = instance;
        // 注意：不调用 UpdateDirtyKey，因为 ObjectId 和实例语义等价

        return instance;
    }

    // === 写 API（只修改 _current，维护 _dirtyKeys） ===

    /// <summary>
    /// 设置指定键的值。
    /// </summary>
    /// <param name="key">键。</param>
    /// <param name="value">值。</param>
    /// <exception cref="ObjectDetachedException">对象已分离。</exception>
    public void Set(ulong key, object? value) {
        ThrowIfDetached();

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
    /// 对应条款：<c>[S-WORKING-STATE-TOMBSTONE-FREE]</c>
    /// Remove 直接从 <c>_current</c> 移除条目，不存储 tombstone。
    /// </remarks>
    /// <exception cref="ObjectDetachedException">对象已分离。</exception>
    public bool Remove(ulong key) {
        ThrowIfDetached();

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
    /// <para>
    /// 使用 <see cref="AreValuesEqual"/> 进行比较，支持 ObjectId 和实例的语义等价判定。
    /// </para>
    /// </remarks>
    private void UpdateDirtyKey(ulong key) {
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
    /// <para>
    /// 对应条款：<c>[A-OBJREF-BACKFILL-CURRENT]</c> 的"不改变 dirty 状态"要求。
    /// </para>
    /// <para>
    /// ObjRef 等价判定：<see cref="ObjectId"/> 和对应的 <see cref="IDurableObject"/> 实例是语义等价的。
    /// 例如：<c>ObjectId(42)</c> 等价于 <c>instance where instance.ObjectId == 42</c>。
    /// </para>
    /// </remarks>
    private static bool AreValuesEqual(object? a, object? b) {
        // 快速路径：引用相等或都为 null
        if (ReferenceEquals(a, b)) { return true; }
        if (a is null || b is null) { return false; }

        // ObjRef 等价判定：ObjectId(x) ≡ instance where instance.ObjectId == x
        return (a, b) switch {
            (ObjectId idA, ObjectId idB) => idA.Value == idB.Value,
            (ObjectId id, IDurableObject obj) => id.Value == obj.ObjectId,
            (IDurableObject obj, ObjectId id) => obj.ObjectId == id.Value,
            (IDurableObject objA, IDurableObject objB) => objA.ObjectId == objB.ObjectId,
            _ => Equals(a, b)
        };
    }

    // === IDurableObject 方法（二阶段提交） ===

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// Prepare 阶段：计算 diff 并写入 writer。
    /// 不更新 <c>_committed</c>/<c>_dirtyKeys</c>——状态追平由 <see cref="OnCommitSucceeded"/> 负责。
    /// </para>
    /// <para>
    /// 对应条款：
    /// <list type="bullet">
    ///   <item><c>[S-POSTCOMMIT-WRITE-ISOLATION]</c>: 不更新内存状态</item>
    ///   <item><c>[S-DIFF-KEY-SORTED-UNIQUE]</c>: key 按升序排列</item>
    /// </list>
    /// </para>
    /// </remarks>
    public override void WritePendingDiff(IBufferWriter<byte> writer) {
        ThrowIfDetached();

        // Fast path: O(1)
        if (_dirtyKeys.Count == 0) { return; }

        // 收集所有变更的 key，按升序排列
        var sortedDirtyKeys = _dirtyKeys.OrderBy(k => k).ToList();

        // 使用 DiffPayloadWriter 序列化
        var payloadWriter = new DiffPayloadWriter(writer);

        foreach (var key in sortedDirtyKeys) {
            if (_current.TryGetValue(key, out var value)) {
                // current 有值 → Set
                WriteValue(ref payloadWriter, key, value);
            }
            else if (_committed.ContainsKey(key)) {
                // current 无值，committed 有值 → Delete (tombstone)
                payloadWriter.WriteTombstone(key);
            }
            // else: 两边都没有 → 不写（理论上不应在 dirtyKeys 中）
        }

        payloadWriter.Complete();
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Finalize 阶段：追平内存状态。
    /// <c>_committed = Clone(_current)</c>，清空 <c>_dirtyKeys</c>。
    /// </remarks>
    public override void OnCommitSucceeded() {
        ThrowIfDetached();

        if (_dirtyKeys.Count == 0) { return; }

        _committed = new Dictionary<ulong, object?>(_current);  // 深拷贝
        _dirtyKeys.Clear();
        SetState(DurableObjectState.Clean);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// 对应条款：
    /// <list type="bullet">
    ///   <item><c>[A-DISCARDCHANGES-REVERT-COMMITTED]</c>: PersistentDirty 对象重置为 Committed State</item>
    ///   <item><c>[S-TRANSIENT-DISCARD-DETACH]</c>: TransientDirty 对象变为 Detached</item>
    /// </list>
    /// </para>
    /// <para>
    /// 状态机：
    /// <list type="bullet">
    ///   <item>Clean → DiscardChanges() → Clean (no-op)</item>
    ///   <item>PersistentDirty → DiscardChanges() → Clean</item>
    ///   <item>TransientDirty → DiscardChanges() → Detached</item>
    ///   <item>Detached → DiscardChanges() → Detached (no-op, 幂等)</item>
    /// </list>
    /// </para>
    /// </remarks>
    public override void DiscardChanges() {
        switch (State) {
            case DurableObjectState.Clean:
                // No-op: 没有变更可丢弃
                return;

            case DurableObjectState.PersistentDirty:
                // 重置为 committed 状态
                _current = new Dictionary<ulong, object?>(_committed);  // 深拷贝
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
    private static void WriteValue(ref DiffPayloadWriter writer, ulong key, object? value) {
        switch (value) {
            case null:
                writer.WriteNull(key);
                break;
            case long longVal:
                writer.WriteVarInt(key, longVal);
                break;
            case int intVal:
                writer.WriteVarInt(key, intVal);
                break;
            case ulong ulongVal:
                // [F-VERSIONINDEX-REUSE-DURABLEDICT]: VersionIndex 使用 Val_Ptr64 编码 ObjectVersionPtr
                writer.WritePtr64(key, ulongVal);
                break;
            default:
                throw new NotSupportedException(
                    $"Unsupported value type: {value.GetType()}. MVP only supports null, long, int, and ulong."
                );
        }
    }

    // === 私有方法 ===

    /// <summary>
    /// 转换到 Dirty 状态。
    /// </summary>
    private void TransitionToDirty() {
        if (State == DurableObjectState.Clean) {
            SetState(DurableObjectState.PersistentDirty);
        }
    }

    // ThrowIfDetached 已由基类提供
}
