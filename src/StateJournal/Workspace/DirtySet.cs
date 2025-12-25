// Source: Atelia.StateJournal - 脏对象集合
// Spec: atelia/docs/StateJournal/mvp-design-v2.md §Dirty Set

namespace Atelia.StateJournal;

/// <summary>
/// 脏对象集合，持有强引用防止 GC 回收。
/// </summary>
/// <remarks>
/// <para>
/// 对应条款：
/// <list type="bullet">
///   <item><c>[S-DIRTYSET-OBJECT-PINNING]</c>: Dirty Set MUST 持有对象实例的强引用，
///   直到该对象的变更被 Commit Point 确认成功或被显式 DiscardChanges。</item>
///   <item><c>[S-DIRTY-OBJECT-GC-PROHIBIT]</c>: Dirty 对象不得被 GC 回收（由 Dirty Set 的强引用保证）。</item>
///   <item><c>[S-NEW-OBJECT-AUTO-DIRTY]</c>: 新建对象 MUST 在创建时立即加入 Dirty Set。</item>
///   <item><c>[S-IDENTITY-MAP-KEY-COHERENCE]</c>: Dirty Set 的 key 必须等于对象自身 ObjectId。</item>
/// </list>
/// </para>
/// </remarks>
internal class DirtySet {
    private readonly Dictionary<ulong, IDurableObject> _set = new();

    /// <summary>
    /// 添加脏对象。
    /// </summary>
    /// <param name="obj">要添加的对象。</param>
    /// <remarks>
    /// <para>
    /// 对应条款：<c>[S-IDENTITY-MAP-KEY-COHERENCE]</c>
    /// — 使用对象的 <see cref="IDurableObject.ObjectId"/> 作为 key。
    /// </para>
    /// <para>
    /// 如果对象已存在，此操作为 No-op（幂等）。
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="obj"/> 为 null。</exception>
    public void Add(IDurableObject obj) {
        ArgumentNullException.ThrowIfNull(obj);
        _set[obj.ObjectId] = obj;
    }

    /// <summary>
    /// 移除对象（Commit 成功或 DiscardChanges 后调用）。
    /// </summary>
    /// <param name="objectId">对象 ID。</param>
    /// <returns>如果成功移除则返回 true。</returns>
    public bool Remove(ulong objectId) {
        return _set.Remove(objectId);
    }

    /// <summary>
    /// 检查对象是否在集合中。
    /// </summary>
    /// <param name="objectId">对象 ID。</param>
    /// <returns>如果对象在集合中则返回 true。</returns>
    public bool Contains(ulong objectId) {
        return _set.ContainsKey(objectId);
    }

    /// <summary>
    /// 获取所有脏对象（用于 CommitAll）。
    /// </summary>
    /// <returns>所有脏对象的枚举。</returns>
    public IEnumerable<IDurableObject> GetAll() {
        return _set.Values;
    }

    /// <summary>
    /// 清空所有（Commit 成功后调用）。
    /// </summary>
    public void Clear() {
        _set.Clear();
    }

    /// <summary>
    /// 脏对象数量。
    /// </summary>
    public int Count => _set.Count;
}
