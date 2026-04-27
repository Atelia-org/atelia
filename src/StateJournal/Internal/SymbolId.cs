using Atelia.StateJournal.Pools;

namespace Atelia.StateJournal.Internal;

/// <summary>
/// Symbol（intern 字符串）的编译期语义包装，隔离 object slot handle 与 symbol slot handle。
/// 其中 <c>0</c> 专门保留给 <see cref="Null"/>；非空 symbol 直接使用底层 <see cref="SlotHandle.Packed"/> 编码。
/// </summary>
/// <remarks>
/// <para><b>语义边界</b>：<see cref="Null"/> 表示“未指向任何 interned symbol slot”，是 handle 家族的 sentinel，
/// 与 <see cref="LocalId.Null"/> / <see cref="NodeContainers.LeafHandle"/> / <see cref="DurableRef"/> 等 nullable handle 同构。</para>
/// <para><b>不要</b>把它误读为“user-facing <see cref="Symbol"/> 处于 null 态”——按新契约 <c>Symbol.Value</c> 永非 null，
/// typed <see cref="Symbol"/> 容器 / wire 上也不允许出现 <see cref="Null"/>。</para>
/// <para><b>合法出现位置</b>：内部 handle / <see cref="ValueBox"/> 层可用它表示“没有 symbol slot”；
/// user-facing <see cref="Symbol"/> 层不暴露 null symbol。</para>
/// </remarks>
internal readonly record struct SymbolId(uint Value) {
    /// <summary>handle 家族 sentinel：未指向任何 interned symbol。与 <c>Symbol.Value is null</c> 无关。</summary>
    public static SymbolId Null => default; // Value = 0
    public bool IsNull => Value == 0;

    /// <summary>从 SlotHandle 构造 SymbolId（直接使用 <see cref="SlotHandle.Packed"/>）。</summary>
    internal static SymbolId FromSlotHandle(SlotHandle handle) => new(handle.Packed);

    /// <summary>将 <see cref="Value"/> 解释为底层 <see cref="SlotHandle.Packed"/>。</summary>
    internal SlotHandle ToSlotHandle() => new(Value);
}
