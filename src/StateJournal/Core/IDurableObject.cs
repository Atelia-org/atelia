// Source: Atelia.StateJournal - 持久化对象堆
// Spec: atelia/docs/StateJournal/mvp-design-v2.md §3.1

using System.Buffers;

namespace Atelia.StateJournal;

/// <summary>
/// 可持久化对象的接口。
/// </summary>
/// <remarks>
/// <para>
/// 所有可被 StateJournal 管理的对象必须实现此接口。
/// </para>
/// <para>
/// 实现者必须遵守以下契约：
/// <list type="bullet">
///   <item><description><see cref="State"/> 和 <see cref="HasChanges"/> 属性的读取复杂度 MUST 为 O(1)</description></item>
///   <item><description><see cref="State"/> 读取 MUST NOT 抛异常（含 <see cref="DurableObjectState.Detached"/> 状态）</description></item>
///   <item><description><see cref="WritePendingDiff"/> 不得修改内存状态（Prepare 阶段）</description></item>
///   <item><description><see cref="OnCommitSucceeded"/> 追平内存状态（Finalize 阶段）</description></item>
/// </list>
/// </para>
/// </remarks>
public interface IDurableObject
{
    /// <summary>
    /// 对象的唯一标识符。
    /// </summary>
    /// <remarks>
    /// ObjectId 在整个 Workspace 范围内唯一，由 Workspace 在 <c>CreateObject()</c> 时分配。
    /// </remarks>
    ulong ObjectId { get; }

    /// <summary>
    /// 对象的生命周期状态。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 对应条款：<c>[A-OBJECT-STATE-PROPERTY]</c>
    /// </para>
    /// <para>
    /// 读取 MUST NOT 抛异常（含 <see cref="DurableObjectState.Detached"/> 状态），复杂度 O(1)。
    /// </para>
    /// </remarks>
    DurableObjectState State { get; }

    /// <summary>
    /// 是否有未提交的变更。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 对应条款：<c>[A-HASCHANGES-O1-COMPLEXITY]</c>
    /// </para>
    /// <para>
    /// 复杂度 MUST 为 O(1)。
    /// </para>
    /// <para>
    /// 语义：<c>HasChanges == true</c> 当且仅当 <see cref="State"/> 为
    /// <see cref="DurableObjectState.PersistentDirty"/> 或 <see cref="DurableObjectState.TransientDirty"/>。
    /// </para>
    /// </remarks>
    bool HasChanges { get; }

    // ========================================================================
    // Two-Phase Commit API (二阶段提交 API)
    // ========================================================================

    /// <summary>
    /// Prepare 阶段：计算 diff 并序列化到 writer。
    /// </summary>
    /// <param name="writer">目标写入器。</param>
    /// <remarks>
    /// <para>
    /// 不更新内存状态。Commit 可能失败（如磁盘满、并发冲突），
    /// 此方法调用后对象仍处于 Dirty 状态。
    /// </para>
    /// <para>
    /// 只有当 <see cref="HasChanges"/> 为 <c>true</c> 时才应调用此方法。
    /// 对 <see cref="DurableObjectState.Clean"/> 或 <see cref="DurableObjectState.Detached"/>
    /// 状态的对象调用此方法的行为未定义。
    /// </para>
    /// </remarks>
    void WritePendingDiff(IBufferWriter<byte> writer);

    /// <summary>
    /// Finalize 阶段：追平内存状态。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 调用后对象状态变为 <see cref="DurableObjectState.Clean"/>。
    /// </para>
    /// <para>
    /// 典型实现：
    /// <code>
    /// _committed = _current.Clone();
    /// _dirtyKeys.Clear();
    /// _state = DurableObjectState.Clean;
    /// </code>
    /// </para>
    /// <para>
    /// 只有在 <see cref="WritePendingDiff"/> 调用后且 Commit 操作成功后才应调用此方法。
    /// </para>
    /// </remarks>
    void OnCommitSucceeded();

    /// <summary>
    /// 丢弃未提交的变更。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 状态转换规则：
    /// <list type="bullet">
    ///   <item><description>
    ///     <see cref="DurableObjectState.PersistentDirty"/> → 重置为 Committed State，
    ///     状态变为 <see cref="DurableObjectState.Clean"/>
    ///   </description></item>
    ///   <item><description>
    ///     <see cref="DurableObjectState.TransientDirty"/> → 状态变为 <see cref="DurableObjectState.Detached"/>，
    ///     后续语义数据访问抛出 <see cref="ObjectDetachedError"/>（通过异常桥接）
    ///   </description></item>
    ///   <item><description>
    ///     <see cref="DurableObjectState.Clean"/> → No-op（无变更可丢弃）
    ///   </description></item>
    ///   <item><description>
    ///     <see cref="DurableObjectState.Detached"/> → No-op（已是终态）
    ///   </description></item>
    /// </list>
    /// </para>
    /// <para>
    /// 对应条款：<c>[S-TRANSIENT-DISCARD-DETACH]</c>
    /// </para>
    /// </remarks>
    void DiscardChanges();
}
