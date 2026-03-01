using System.Runtime.CompilerServices;
using Atelia.StateJournal.Internal;
using Atelia.StateJournal.Pools;

namespace Atelia.StateJournal;

// ai:test `tests/StateJournal.Tests/Internal/ValueBoxEqualityTests.cs`
// ai:note 没有实现IEquatable<ValueBox>以及没有override int GetHashCode()，是出于方便测试角度出发的，将“指针相等”与“指针的目标的相等”语义保持分离。
partial struct ValueBox {

    /// <summary>
    /// 比较两个 <see cref="ValueBox"/> 的值相等性。
    /// </summary>
    /// <remarks>
    /// 快速路径：<c>GetBits()</c> 相等即值相等。
    /// 这覆盖了所有 inline 编码（数值、浮点、bool、null、undefined）
    /// 和所有 InternPool 引用类型（同值同 handle → 同 bits）。
    ///
    /// 慢路径：仅当双方都是 HeapSlot 且 <see cref="DurableValueKind"/> 相同时才触发。
    /// 当前仅 <see cref="ValuePools.Bits64"/> 中的数值类型（<see cref="DurableValueKind.NonnegativeInteger"/>、
    /// <see cref="DurableValueKind.NegativeInteger"/>、<see cref="DurableValueKind.FloatingPoint"/>）
    /// 使用 GcPool（独占 slot），可能出现同值不同 handle 的情况，需要取出堆中 raw bits 比较。
    ///
    /// 设计决策：<c>42.0 != 42</c>（整数与浮点数不互等），
    /// 因此只有 Kind 相同才可能相等。非负整数与负整数之间也不可能数学值相等。
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool ValueEquals(ValueBox a, ValueBox b) {
        ulong ab = a._bits, bb = b._bits;
        if (ab == bb) { return true; }
        // 如果高 32 位不同 → LZC 不同 或 Kind 不同 → 值不等
        if ((ab >>= HeapHandleBitCount) != (bb >> HeapHandleBitCount)) { return false; }
        return ValueEqualsSlowPath(a, b, (uint)ab);
        // return (ab == bb) || (((ab >>= HeapHandleBitCount) == (bb >> HeapHandleBitCount)) && ValueEqualsSlowPath(a, b, (uint)ab));
    }

    /// <summary>检查是否是堆分配的数值且堆中值相等</summary>
    private static bool ValueEqualsSlowPath(ValueBox a, ValueBox b, uint tagAndKind) {
        // return (MinNumberTagAndKind <= tagAndKind) & (tagAndKind <= MaxNumberTagAndKind)
        return IsNumericTagAndKind(tagAndKind)
            && ValuePools.Bits64[a.GetHeapHandle()] == ValuePools.Bits64[b.GetHeapHandle()];
        // 非 Bits64 heap 类型（如 String）→ InternPool 保证同值同 handle → bits 不同即值不同
    }

    /// <summary>
    /// 计算 <see cref="ValueBox"/> 的哈希码，与 <see cref="ValueEquals"/> 一致。
    /// </summary>
    /// <remarks>
    /// 对于 heap 数值类型（GcPool，同值可能不同 handle），
    /// 必须基于堆中的实际值计算哈希，而不是基于含 handle 的 bits。
    ///
    /// 对于所有其他情况（inline 或 InternPool），<c>GetBits()</c> 即为
    /// 值的唯一表示，直接用于哈希。
    ///
    /// </remarks>
    internal static int ValueHashCode(ValueBox box) {
        if (box.TryGetBits64Handle(out SlotHandle handle)) {
            // heap 数值：用实际值 + Kind 做哈希
            ulong raw = ValuePools.Bits64[handle];
            // return HashCode.Combine(box.GetHeapKind(), raw); // 似乎杀鸡用牛刀了
            return ((int)box.GetHeapKind() * 16777619) ^ raw.GetHashCode();
        }
        // inline 值 或 InternPool 引用：bits 即值
        return box._bits.GetHashCode();
    }

    /// <summary>
    /// <see cref="IEqualityComparer{T}"/> 实现，基于 <see cref="ValueEquals"/> 和 <see cref="ValueHashCode"/>。
    /// </summary>
    /// <remarks>
    /// 用于 <c>Dictionary&lt;TKey, ValueBox&gt;</c> 等需要值相等性的场景。
    /// 嵌套在 <see cref="ValueBox"/> 内部以访问私有成员，避免暴露内部 API。
    /// </remarks>
    internal sealed class EqualityComparer : IEqualityComparer<ValueBox> {
        /// <summary>单例。</summary>
        public static readonly EqualityComparer Instance = new();
        private EqualityComparer() { }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Equals(ValueBox x, ValueBox y) => ValueEquals(x, y);

        public int GetHashCode(ValueBox obj) => ValueHashCode(obj);
    }
}
