// Source: Atelia.StateJournal - 持久化字典
// Spec: atelia/docs/StateJournal/mvp-design-v2.md §3.2

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
/// 双字典模型：
/// <list type="bullet">
///   <item><c>_committed</c>：已提交状态（Committed State）</item>
///   <item><c>_working</c>：工作状态（Working State）</item>
/// </list>
/// </para>
/// <para>
/// 读取时先查 <c>_working</c>，若无则查 <c>_committed</c>。
/// 写入只修改 <c>_working</c>。
/// </para>
/// <para>
/// 对应条款：
/// <list type="bullet">
///   <item><c>[A-DURABLEDICT-API-SIGNATURES]</c></item>
///   <item><c>[S-DURABLEDICT-KEY-ULONG-ONLY]</c></item>
///   <item><c>[S-WORKING-STATE-TOMBSTONE-FREE]</c></item>
/// </list>
/// </para>
/// </remarks>
public class DurableDict : IDurableObject {
    // === 内部状态 ===
    private readonly Dictionary<ulong, object?> _committed;
    private readonly Dictionary<ulong, object?> _working;
    private readonly HashSet<ulong> _removedFromCommitted;  // 跟踪从 _committed 删除的 key
    private readonly HashSet<ulong> _dirtyKeys;
    private DurableObjectState _state;

    // === IDurableObject 实现 ===

    /// <inheritdoc/>
    public ulong ObjectId { get; }

    /// <inheritdoc/>
    public DurableObjectState State => _state;

    /// <inheritdoc/>
    /// <remarks>
    /// 复杂度 O(1)：直接检查 <c>_dirtyKeys.Count</c>。
    /// </remarks>
    /// <exception cref="ObjectDetachedException">对象已分离。</exception>
    public bool HasChanges {
        get {
            ThrowIfDetached();
            return _dirtyKeys.Count > 0;
        }
    }

    // === 构造函数 ===

    /// <summary>
    /// 创建新的 DurableDict（TransientDirty 状态）。
    /// </summary>
    /// <param name="objectId">对象 ID。</param>
    /// <remarks>
    /// 新创建的对象没有 Committed State，调用 <see cref="IDurableObject.DiscardChanges"/>
    /// 会使对象进入 <see cref="DurableObjectState.Detached"/> 状态。
    /// </remarks>
    public DurableDict(ulong objectId) {
        ObjectId = objectId;
        _committed = new Dictionary<ulong, object?>();
        _working = new Dictionary<ulong, object?>();
        _removedFromCommitted = new HashSet<ulong>();
        _dirtyKeys = new HashSet<ulong>();
        _state = DurableObjectState.TransientDirty;
    }

    /// <summary>
    /// 从 Committed State 加载（Clean 状态）。
    /// </summary>
    /// <param name="objectId">对象 ID。</param>
    /// <param name="committed">已提交的键值对。</param>
    /// <remarks>
    /// 此构造函数为 internal，由 Workspace 或加载器使用。
    /// </remarks>
    internal DurableDict(ulong objectId, Dictionary<ulong, object?> committed) {
        ObjectId = objectId;
        _committed = committed ?? throw new ArgumentNullException(nameof(committed));
        _working = new Dictionary<ulong, object?>();
        _removedFromCommitted = new HashSet<ulong>();
        _dirtyKeys = new HashSet<ulong>();
        _state = DurableObjectState.Clean;
    }

    // === 读 API ===

    /// <summary>
    /// 尝试获取指定键的值。
    /// </summary>
    /// <param name="key">键。</param>
    /// <param name="value">输出的值（如找到）。</param>
    /// <returns>如果找到键则返回 true。</returns>
    /// <remarks>
    /// 先查 <c>_working</c>，若无则查 <c>_committed</c>。
    /// </remarks>
    /// <exception cref="ObjectDetachedException">对象已分离。</exception>
    public bool TryGetValue(ulong key, out object? value) {
        ThrowIfDetached();
        // 先检查是否已从 _committed 删除
        if (_removedFromCommitted.Contains(key) && !_working.ContainsKey(key)) {
            value = default;
            return false;
        }
        if (_working.TryGetValue(key, out value)) { return true; }
        return _committed.TryGetValue(key, out value);
    }

    /// <summary>
    /// 检查是否包含指定键。
    /// </summary>
    /// <param name="key">键。</param>
    /// <returns>如果包含键则返回 true。</returns>
    /// <exception cref="ObjectDetachedException">对象已分离。</exception>
    public bool ContainsKey(ulong key) {
        ThrowIfDetached();
        // _working 中有则存在
        if (_working.ContainsKey(key)) { return true; }
        // 已从 _committed 删除则不存在
        if (_removedFromCommitted.Contains(key)) { return false; }
        // 否则查 _committed
        return _committed.ContainsKey(key);
    }

    /// <summary>
    /// 获取或设置指定键的值。
    /// </summary>
    /// <param name="key">键。</param>
    /// <returns>值。</returns>
    /// <exception cref="KeyNotFoundException">获取时键不存在。</exception>
    /// <exception cref="ObjectDetachedException">对象已分离。</exception>
    public object? this[ulong key] {
        get {
            ThrowIfDetached();
            if (_working.TryGetValue(key, out var value)) { return value; }
            // 已从 _committed 删除则不存在
            if (_removedFromCommitted.Contains(key)) { throw new KeyNotFoundException($"Key {key} not found in DurableDict."); }
            if (_committed.TryGetValue(key, out value)) { return value; }
            throw new KeyNotFoundException($"Key {key} not found in DurableDict.");
        }
        set {
            Set(key, value);
        }
    }

    /// <summary>
    /// 获取当前条目数量。
    /// </summary>
    /// <remarks>
    /// 合并 <c>_committed</c> 和 <c>_working</c> 的 key。
    /// </remarks>
    /// <exception cref="ObjectDetachedException">对象已分离。</exception>
    public int Count {
        get {
            ThrowIfDetached();
            // 计算合并后的唯一 key 数量
            // _committed 中未被删除的 + _working 中新增的
            int count = 0;
            foreach (var key in _committed.Keys) {
                if (!_removedFromCommitted.Contains(key)) { count++; }
            }
            foreach (var key in _working.Keys) {
                // _working 中存在且不在 _committed 中的是新增的
                if (!_committed.ContainsKey(key) || _removedFromCommitted.Contains(key)) { count++; }
            }
            return count;
        }
    }

    /// <summary>
    /// 获取所有键的枚举。
    /// </summary>
    /// <remarks>
    /// 合并 <c>_committed</c> 和 <c>_working</c> 的 key。
    /// </remarks>
    /// <exception cref="ObjectDetachedException">对象已分离。</exception>
    public IEnumerable<ulong> Keys {
        get {
            ThrowIfDetached();
            // 合并：_committed 中未删除的 + _working 中所有的
            var allKeys = new HashSet<ulong>();
            foreach (var key in _committed.Keys) {
                if (!_removedFromCommitted.Contains(key)) {
                    allKeys.Add(key);
                }
            }
            allKeys.UnionWith(_working.Keys);
            return allKeys;
        }
    }

    /// <summary>
    /// 获取所有键值对的枚举。
    /// </summary>
    /// <remarks>
    /// 合并 <c>_committed</c> 和 <c>_working</c>，<c>_working</c> 优先。
    /// </remarks>
    /// <exception cref="ObjectDetachedException">对象已分离。</exception>
    public IEnumerable<KeyValuePair<ulong, object?>> Entries {
        get {
            ThrowIfDetached();
            // 立即计算以确保 Detached 检查在调用时执行
            return GetEntriesCore();
        }
    }

    private IEnumerable<KeyValuePair<ulong, object?>> GetEntriesCore() {
        // 合并：_committed 中未删除的 + _working 中所有的
        var allKeys = new HashSet<ulong>();
        foreach (var key in _committed.Keys) {
            if (!_removedFromCommitted.Contains(key)) {
                allKeys.Add(key);
            }
        }
        allKeys.UnionWith(_working.Keys);

        foreach (var key in allKeys) {
            if (_working.TryGetValue(key, out var value)) {
                yield return new KeyValuePair<ulong, object?>(key, value);
            }
            else {
                yield return new KeyValuePair<ulong, object?>(key, _committed[key]);
            }
        }
    }

    // === 写 API ===

    /// <summary>
    /// 设置指定键的值。
    /// </summary>
    /// <param name="key">键。</param>
    /// <param name="value">值。</param>
    /// <exception cref="ObjectDetachedException">对象已分离。</exception>
    public void Set(ulong key, object? value) {
        ThrowIfDetached();

        // 写入 working
        _working[key] = value;

        // 如果之前标记为删除，现在恢复
        _removedFromCommitted.Remove(key);

        // 精确追踪 _dirtyKeys
        UpdateDirtyKeyForSet(key, value);

        // 状态转换（如果有实际变更）
        if (_dirtyKeys.Count > 0) {
            TransitionToDirty();
        }
    }

    /// <summary>
    /// 移除指定键。
    /// </summary>
    /// <param name="key">键。</param>
    /// <returns>如果键存在（在 _working 或 _committed 中）则返回 true。</returns>
    /// <remarks>
    /// <para>
    /// 对应条款：<c>[S-WORKING-STATE-TOMBSTONE-FREE]</c>
    /// </para>
    /// <para>
    /// Remove 从 <c>_working</c> 移除条目，不存储 tombstone。
    /// 如果键只在 <c>_committed</c> 中存在，则在 Diff 生成时需要标记为删除。
    /// </para>
    /// </remarks>
    /// <exception cref="ObjectDetachedException">对象已分离。</exception>
    public bool Remove(ulong key) {
        ThrowIfDetached();

        var hadInWorking = _working.Remove(key);
        var hasInCommitted = _committed.ContainsKey(key);

        // 标记 _committed 中的 key 为已删除
        if (hasInCommitted) {
            _removedFromCommitted.Add(key);
        }

        // 精确追踪 _dirtyKeys
        UpdateDirtyKeyForRemove(key, hadInWorking, hasInCommitted);

        // 只有实际删除了什么才返回 true 并转换状态
        bool removed = hadInWorking || hasInCommitted;
        if (removed && _dirtyKeys.Count > 0) {
            TransitionToDirty();
        }

        return removed;
    }

    // === IDurableObject 方法 ===

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// 对应条款：
    /// <list type="bullet">
    ///   <item><c>[S-POSTCOMMIT-WRITE-ISOLATION]</c>: 不更新内存状态</item>
    ///   <item><c>[S-DIFF-KEY-SORTED-UNIQUE]</c>: key 按升序排列</item>
    /// </list>
    /// </para>
    /// </remarks>
    public void WritePendingDiff(IBufferWriter<byte> writer) {
        ThrowIfDetached();

        // 1. 收集所有变更的 key，按升序排列
        var sortedDirtyKeys = _dirtyKeys.OrderBy(k => k).ToList();

        // 2. 使用 DiffPayloadWriter 序列化
        var payloadWriter = new DiffPayloadWriter(writer);

        foreach (var key in sortedDirtyKeys) {
            // 判断当前状态
            bool inWorking = _working.TryGetValue(key, out var workingValue);
            bool isRemoved = _removedFromCommitted.Contains(key);

            if (inWorking) {
                // Upsert: 写入新值
                WriteValue(ref payloadWriter, key, workingValue);
            }
            else if (isRemoved) {
                // Delete: 写 tombstone
                payloadWriter.WriteTombstone(key);
            }
            // 其他情况不应出现（_dirtyKeys 追踪应正确）
        }

        payloadWriter.Complete();
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Finalize 阶段：追平内存状态。
    /// <para>
    /// 1. 合并 _working 到 _committed
    /// 2. 清空变更追踪
    /// 3. 状态转为 Clean
    /// </para>
    /// </remarks>
    public void OnCommitSucceeded() {
        ThrowIfDetached();

        // 1. 合并 _working 到 _committed
        foreach (var key in _dirtyKeys) {
            if (_working.TryGetValue(key, out var value)) {
                _committed[key] = value;
            }
            else if (_removedFromCommitted.Contains(key)) {
                _committed.Remove(key);
            }
        }

        // 2. 清空变更追踪
        _dirtyKeys.Clear();
        _removedFromCommitted.Clear();

        // 3. 清空 _working（因为已合并到 _committed）
        _working.Clear();

        // 4. 状态转为 Clean
        _state = DurableObjectState.Clean;
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
    public void DiscardChanges() {
        switch (_state) {
            case DurableObjectState.Clean:
                // No-op: 没有变更可丢弃
                return;

            case DurableObjectState.PersistentDirty:
                // 重置为 committed 状态
                _working.Clear();
                _dirtyKeys.Clear();
                _removedFromCommitted.Clear();
                _state = DurableObjectState.Clean;
                return;

            case DurableObjectState.TransientDirty:
                // Detach: 标记为已分离，后续访问抛异常
                _working.Clear();
                _committed.Clear();
                _dirtyKeys.Clear();
                _removedFromCommitted.Clear();
                _state = DurableObjectState.Detached;
                return;

            case DurableObjectState.Detached:
                // No-op: 幂等，符合 [A-DURABLEDICT-API-SIGNATURES] 规范
                return;

            default:
                throw new InvalidOperationException($"Unknown state: {_state}");
        }
    }

    // === 私有序列化方法 ===

    /// <summary>
    /// 根据值类型写入到 DiffPayloadWriter。
    /// </summary>
    /// <param name="writer">Payload 写入器。</param>
    /// <param name="key">键。</param>
    /// <param name="value">值。</param>
    /// <exception cref="NotSupportedException">不支持的值类型。</exception>
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
            // MVP 暂不支持其他类型
            default:
                throw new NotSupportedException(
                    $"Unsupported value type: {value.GetType()}. MVP only supports null, long, int, and ulong."
                );
        }
    }

    // === 私有方法 ===

    /// <summary>
    /// 精确追踪 _dirtyKeys（Set 操作）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 对应条款：<c>[S-DIRTYKEYS-TRACKING-EXACT]</c>
    /// </para>
    /// <para>
    /// 规则：比较 newValue 与 _committed[key]：
    /// <list type="bullet">
    ///   <item>若相等（含两边都不存在）：移除 dirtyKey</item>
    ///   <item>若不等：添加 dirtyKey</item>
    /// </list>
    /// </para>
    /// </remarks>
    /// <param name="key">变更的键。</param>
    /// <param name="newValue">新值。</param>
    private void UpdateDirtyKeyForSet(ulong key, object? newValue) {
        // 获取 committed 值
        bool hasCommitted = _committed.TryGetValue(key, out var committedValue);

        // 比较：新值是否等于已提交值
        bool isEqual = hasCommitted
            ? Equals(newValue, committedValue)
            : false;  // 如果 committed 中没有，则不相等

        if (isEqual) {
            _dirtyKeys.Remove(key);
        }
        else {
            _dirtyKeys.Add(key);
        }
    }

    /// <summary>
    /// 精确追踪 _dirtyKeys（Remove 操作）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 对应条款：<c>[S-DIRTYKEYS-TRACKING-EXACT]</c>
    /// </para>
    /// <para>
    /// 规则：
    /// <list type="bullet">
    ///   <item>若删除已提交的 key：添加 dirtyKey（删除了已提交的 key）</item>
    ///   <item>若删除未提交的新 key：移除 dirtyKey（回到原状态）</item>
    ///   <item>若 key 在两处都不存在：不变</item>
    /// </list>
    /// </para>
    /// </remarks>
    /// <param name="key">变更的键。</param>
    /// <param name="hadInWorking">是否在 _working 中存在（已被删除）。</param>
    /// <param name="hasInCommitted">是否在 _committed 中存在。</param>
    private void UpdateDirtyKeyForRemove(ulong key, bool hadInWorking, bool hasInCommitted) {
        if (hasInCommitted) {
            // 删除了已提交的 key → 记录变更
            _dirtyKeys.Add(key);
        }
        else if (hadInWorking) {
            // 删除了未提交的新 key → 回到原状态，移除脏标记
            _dirtyKeys.Remove(key);
        }
        // else: key 在两处都不存在，Remove 无效果
    }

    /// <summary>
    /// 转换到 Dirty 状态。
    /// </summary>
    private void TransitionToDirty() {
        // Clean → PersistentDirty
        // TransientDirty 保持不变
        if (_state == DurableObjectState.Clean) {
            _state = DurableObjectState.PersistentDirty;
        }
    }

    /// <summary>
    /// 如果对象已分离则抛出异常。
    /// </summary>
    /// <exception cref="ObjectDetachedException">对象已分离。</exception>
    private void ThrowIfDetached() {
        if (_state == DurableObjectState.Detached) { throw new ObjectDetachedException(ObjectId); }
    }
}

/// <summary>
/// 对象已分离异常。
/// </summary>
/// <remarks>
/// 对应条款：<c>[S-TRANSIENT-DISCARD-DETACH]</c>
/// </remarks>
public class ObjectDetachedException : InvalidOperationException {
    /// <summary>
    /// 已分离对象的 ID。
    /// </summary>
    public ulong ObjectId { get; }

    /// <summary>
    /// 创建新的 ObjectDetachedException。
    /// </summary>
    /// <param name="objectId">对象 ID。</param>
    public ObjectDetachedException(ulong objectId)
        : base($"Object {objectId} has been detached and cannot be accessed.") {
        ObjectId = objectId;
    }
}
