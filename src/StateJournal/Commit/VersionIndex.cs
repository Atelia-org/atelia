// Source: Atelia.StateJournal - 版本索引
// Spec: atelia/docs/StateJournal/mvp-design-v2.md §F-VERSIONINDEX-REUSE-DURABLEDICT

using System.Buffers;

namespace Atelia.StateJournal;

/// <summary>
/// 版本索引：ObjectId → ObjectVersionPtr 的映射表。
/// </summary>
/// <remarks>
/// <para>
/// VersionIndex 是 StateJournal 的"引导扇区"，使用 Well-Known ObjectId 0。
/// 复用 DurableDict 实现，key 为 ObjectId (ulong)，value 为 ObjectVersionPtr (ulong?/Ptr64)。
/// </para>
/// <para>
/// Bootstrap 入口：VersionIndex 的指针（VersionIndexPtr）直接存储在 MetaCommitRecord 中，
/// 打破"读 VersionIndex 需要先查 VersionIndex"的概念死锁。
/// </para>
/// <para>
/// 对应条款：<c>[F-VERSIONINDEX-REUSE-DURABLEDICT]</c>
/// </para>
/// </remarks>
public sealed class VersionIndex : IDurableObject {
    /// <summary>
    /// Well-Known ObjectId for VersionIndex.
    /// </summary>
    /// <remarks>
    /// ObjectId 0 是保留区的第一个 ID，专门分配给 VersionIndex。
    /// </remarks>
    public const ulong WellKnownObjectId = 0;

    /// <summary>
    /// 用户可分配的最小 ObjectId（保留区之后的第一个 ID）。
    /// </summary>
    /// <remarks>
    /// ObjectId 0-15 为保留区，用于 Well-Known 对象。
    /// </remarks>
    private const ulong MinUserObjectId = 16;

    private readonly DurableDict<ulong?> _inner;

    /// <summary>
    /// 创建新的空 VersionIndex。
    /// </summary>
    public VersionIndex() {
        _inner = new DurableDict<ulong?>(WellKnownObjectId);
    }

    /// <summary>
    /// 从 committed 状态恢复 VersionIndex。
    /// </summary>
    /// <param name="committed">已提交的 ObjectId → ObjectVersionPtr 映射。</param>
    internal VersionIndex(Dictionary<ulong, ulong?> committed) {
        _inner = new DurableDict<ulong?>(WellKnownObjectId, committed);
    }

    // === IDurableObject 实现（委托给 _inner）===

    /// <inheritdoc/>
    public ulong ObjectId => WellKnownObjectId;

    /// <inheritdoc/>
    public DurableObjectState State => _inner.State;

    /// <inheritdoc/>
    public bool HasChanges => _inner.HasChanges;

    /// <inheritdoc/>
    public void WritePendingDiff(IBufferWriter<byte> writer)
        => _inner.WritePendingDiff(writer);

    /// <inheritdoc/>
    public void OnCommitSucceeded()
        => _inner.OnCommitSucceeded();

    /// <inheritdoc/>
    public void DiscardChanges()
        => _inner.DiscardChanges();

    // === VersionIndex 特有 API ===

    /// <summary>
    /// 获取对象的最新版本指针。
    /// </summary>
    /// <param name="objectId">对象 ID。</param>
    /// <param name="versionPtr">输出的版本指针（如果找到）。</param>
    /// <returns>如果找到有效指针则返回 true；否则返回 false。</returns>
    /// <remarks>
    /// 返回 false 的情况：
    /// <list type="bullet">
    ///   <item>objectId 不存在于索引中</item>
    ///   <item>objectId 存在但值为 null（已删除或无效）</item>
    /// </list>
    /// </remarks>
    public bool TryGetObjectVersionPtr(ulong objectId, out ulong versionPtr) {
        if (_inner.TryGetValue(objectId, out var ptr) && ptr.HasValue) {
            versionPtr = ptr.Value;
            return true;
        }
        versionPtr = 0;
        return false;
    }

    /// <summary>
    /// 设置对象的最新版本指针。
    /// </summary>
    /// <param name="objectId">对象 ID。</param>
    /// <param name="versionPtr">版本指针（ObjectVersionRecord 的 Address64）。</param>
    public void SetObjectVersionPtr(ulong objectId, ulong versionPtr) {
        _inner.Set(objectId, versionPtr);
    }

    /// <summary>
    /// 获取所有已注册的对象 ID。
    /// </summary>
    public IEnumerable<ulong> ObjectIds => _inner.Keys;

    /// <summary>
    /// 获取已注册的对象数量。
    /// </summary>
    public int Count => _inner.Count;

    /// <summary>
    /// 计算下一个可分配的 ObjectId（最大 key + 1，至少为 16）。
    /// </summary>
    /// <returns>下一个可用的 ObjectId。</returns>
    /// <remarks>
    /// <para>
    /// 返回规则：
    /// <list type="bullet">
    ///   <item>如果索引为空，返回 16（保留区之后的第一个 ID）</item>
    ///   <item>否则返回 max(所有 key) + 1</item>
    ///   <item>但不低于 16（保护保留区）</item>
    /// </list>
    /// </para>
    /// <para>
    /// 注意：此方法不会修改 VersionIndex 状态，只是计算下一个可用 ID。
    /// 实际分配由 Workspace.CreateObject 完成。
    /// </para>
    /// </remarks>
    public ulong ComputeNextObjectId() {
        ulong maxId = MinUserObjectId - 1;  // 15，保留区最大值
        foreach (var id in _inner.Keys) {
            if (id > maxId) { maxId = id; }
        }
        return maxId + 1;
    }
}
