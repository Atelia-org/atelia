// Source: Atelia.StateJournal - 持久化对象堆
// Spec: atelia/docs/StateJournal/mvp-design-v2.md §3.1.1

namespace Atelia.StateJournal;

/// <summary>
/// 持久化对象的生命周期状态。
/// </summary>
/// <remarks>
/// <para>
/// 对应条款：<c>[A-OBJECT-STATE-CLOSED-SET]</c>
/// </para>
/// <para>
/// 状态转换规则：
/// <list type="bullet">
///   <item><description><c>CreateObject()</c> → <see cref="TransientDirty"/></description></item>
///   <item><description><c>LoadObject()</c> → <see cref="Clean"/></description></item>
///   <item><description><c>Modify</c> (on Clean) → <see cref="PersistentDirty"/></description></item>
///   <item><description><c>Commit()</c> (any Dirty) → <see cref="Clean"/></description></item>
///   <item><description><c>DiscardChanges()</c> (on PersistentDirty) → <see cref="Clean"/></description></item>
///   <item><description><c>DiscardChanges()</c> (on TransientDirty) → <see cref="Detached"/></description></item>
/// </list>
/// </para>
/// </remarks>
public enum DurableObjectState {
    /// <summary>
    /// 干净状态：对象的 Working State 等于 Committed State。
    /// </summary>
    /// <remarks>
    /// 对象从 <c>LoadObject()</c> 返回时，或成功 <c>Commit()</c> 后处于此状态。
    /// Clean 对象可以被 GC 回收（Identity Map 使用 WeakReference）。
    /// </remarks>
    Clean = 0,

    /// <summary>
    /// 持久脏状态：对象已有 Committed 版本，但 Working State 有未提交的变更。
    /// </summary>
    /// <remarks>
    /// 当已加载的对象被修改（<c>Set</c>/<c>Remove</c>）时进入此状态。
    /// <c>DiscardChanges()</c> 会将对象重置为 Committed State，状态变为 <see cref="Clean"/>。
    /// </remarks>
    PersistentDirty = 1,

    /// <summary>
    /// 瞬态脏状态：对象是新建的，尚无 Committed 版本。
    /// </summary>
    /// <remarks>
    /// 由 <c>CreateObject()</c> 创建的对象处于此状态。
    /// <c>DiscardChanges()</c> 会将对象变为 <see cref="Detached"/>（因为没有可回退的 Committed State）。
    /// </remarks>
    TransientDirty = 2,

    /// <summary>
    /// 已分离状态：对象已与 Workspace 断开连接（终态）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 对应条款：<c>[S-TRANSIENT-DISCARD-DETACH]</c>
    /// </para>
    /// <para>
    /// 当 Transient Dirty 对象调用 <c>DiscardChanges()</c> 后进入此状态。
    /// 任何语义数据访问都会抛出 <c>ObjectDetachedException</c>。
    /// </para>
    /// <para>
    /// ⚠️ <b>僵尸对象警告</b>：Detached 对象的 ObjectId 可能被重新分配给新对象。
    /// 调用方应避免在 Discard 后仍持有对 Transient 对象的引用。
    /// </para>
    /// </remarks>
    Detached = 3,
}
