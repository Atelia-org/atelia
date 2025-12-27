// Source: Atelia.StateJournal - 对象身份映射
// Spec: atelia/docs/StateJournal/mvp-design-v2.md §Identity Map

using System.Diagnostics.CodeAnalysis;

namespace Atelia.StateJournal;

/// <summary>
/// 对象身份映射，用于保证同一 ObjectId 在存活期间只对应一个内存实例。
/// </summary>
/// <remarks>
/// <para>
/// 使用 <see cref="WeakReference{T}"/> 允许 Clean 对象被 GC 回收。
/// </para>
/// <para>
/// 核心路径只接入 <see cref="DurableObjectBase"/>，确保对象满足 Workspace 绑定约束。
/// </para>
/// <para>
/// 对应条款：<c>[S-IDENTITY-MAP-KEY-COHERENCE]</c>
/// — Identity Map 与 Dirty Set 的 key 必须等于对象自身 ObjectId。
/// </para>
/// </remarks>
internal class IdentityMap {
    private readonly Dictionary<ulong, WeakReference<DurableObjectBase>> _map = new();

    /// <summary>
    /// 尝试获取已缓存的对象。
    /// </summary>
    /// <param name="objectId">对象 ID。</param>
    /// <param name="obj">输出的对象（如果存在且仍存活）。</param>
    /// <returns>如果找到存活的对象则返回 true。</returns>
    public bool TryGet(ulong objectId, [NotNullWhen(true)] out DurableObjectBase? obj) {
        if (_map.TryGetValue(objectId, out var weakRef)) {
            if (weakRef.TryGetTarget(out obj)) { return true; }
            // WeakReference 已失效，清理该条目
            _map.Remove(objectId);
        }
        obj = null;
        return false;
    }

    /// <summary>
    /// 添加对象到映射。
    /// </summary>
    /// <param name="obj">要添加的对象。</param>
    /// <remarks>
    /// <para>
    /// 对应条款：<c>[S-IDENTITY-MAP-KEY-COHERENCE]</c>
    /// — 使用对象的 <see cref="DurableObjectBase.ObjectId"/> 作为 key。
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="obj"/> 为 null。</exception>
    /// <exception cref="InvalidOperationException">已存在同一 ObjectId 的不同活对象。</exception>
    public void Add(DurableObjectBase obj) {
        ArgumentNullException.ThrowIfNull(obj);

        var objectId = obj.ObjectId;

        // 检查是否已存在活对象
        if (_map.TryGetValue(objectId, out var existing) && existing.TryGetTarget(out var existingObj)) {
            // 同一对象重复添加是允许的（幂等）
            if (ReferenceEquals(existingObj, obj)) { return; }
            throw new InvalidOperationException(
                $"An object with ObjectId {objectId} already exists in the IdentityMap."
            );
        }

        _map[objectId] = new WeakReference<DurableObjectBase>(obj);
    }

    /// <summary>
    /// 从映射中移除对象。
    /// </summary>
    /// <param name="objectId">对象 ID。</param>
    /// <returns>如果成功移除则返回 true。</returns>
    public bool Remove(ulong objectId) {
        return _map.Remove(objectId);
    }

    /// <summary>
    /// 清理已被 GC 回收的 WeakReference（可选，定期调用）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 遍历所有条目，移除 <see cref="WeakReference{T}.TryGetTarget"/> 返回 false 的条目。
    /// </para>
    /// </remarks>
    /// <returns>清理的条目数量。</returns>
    public int Cleanup() {
        var toRemove = new List<ulong>();

        foreach (var (objectId, weakRef) in _map) {
            if (!weakRef.TryGetTarget(out _)) {
                toRemove.Add(objectId);
            }
        }

        foreach (var objectId in toRemove) {
            _map.Remove(objectId);
        }

        return toRemove.Count;
    }

    /// <summary>
    /// 映射中的条目数（包括可能已失效的 WeakReference）。
    /// </summary>
    public int Count => _map.Count;
}
