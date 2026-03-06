namespace Atelia.StateJournal.Internal;

/// <summary>
/// <see cref="ValueBox"/>的 LeadingZeroCount 编码。
/// 值 = BitOperations.LeadingZeroCount(bits)。
/// </summary>
/// <remarks>
/// 值域[0~64]
/// </remarks>
internal enum BoxLzc : byte {
    InlineDouble = 0,
    InlineNonnegInt = 1,
    InlineNegInt = 2,
    // 3..23 未分配
    HeapSlot = 64 - 1 - ValueBox.HeapKindBitCount - ValueBox.ExclusiveBitCount - ValueBox.HeapHandleBitCount,
    // 25..61 未分配
    Boolean = 62,
    Null = 63,
    Uninitialized = 64,
}

/// <summary>从 <see cref="BoxLzc"/> 分配推导出的位掩码常量。消除 ValueBox 编码/解码中的 magic number。
/// 应避免跨类型依赖，表达式语义清晰。</summary>
/// <remarks>
/// 1. **Tag位索引定律**
///   LZC 意味着从位 63 开始有多少个零。所以紧挨着首个必须为 1 的 Tag 位的序号，天然即是 `63 - LZC`。
///   `DoubleTag(LZC=0)     = 1UL &lt;&lt; (63 - 0)`
///   `NonnegIntTag(LZC=1)  = 1UL &lt;&lt; (63 - 1)`
/// 2. **纯 Payload 位数定律**
///   对于需要留开 Tag 位的正整数，剩下的有效截断位数必然是 `64 - LZC - 1`。
/// 3. **负整数巧用补码定律**
///   对于负整数 (LZC=2)，截取其底层原汁原味的二进制数据宽度是 `64 - LZC` 即 62 位。那为什么它没有显式去定义和 `|` 一个 `NegIntTag` 呢？因为 62 位的负数补码，其最高位（符号位）必然是 1，这个符号位的 1 极其巧妙且完美地兼任了对应的 Tag，使得截断以后 LZC 自然等于 2！不用任何修改！
///   而这个用 `64 - LZC`（62位）表示的带符号数字，它的下界 `NegIntInlineMin` 自然就是 $-2^{62 - 1}$，因此写成 `-(1L &lt;&lt; (64 - LZC - 1))` 是极其精确的形式推导！
/// </remarks>
internal static class LzcConstants {
    /// <summary>InlineFloatingPoint tag bit (bit63=1 → LZC=0)。</summary>
    internal const ulong DoubleTag = 1UL << (63 - (int)BoxLzc.InlineDouble);

    /// <summary>InlineNonnegativeInteger tag bit (bit62=1 → LZC=1)。</summary>
    internal const ulong NonnegIntTag = 1UL << (63 - (int)BoxLzc.InlineNonnegInt);

    /// <summary>HeapSlot tag bit (bit39=1 → LZC=24)。编码堆分配软指针时必须置位。</summary>
    internal const ulong HeapSlotTag = 1UL << (63 - (int)BoxLzc.HeapSlot);

    /// <summary>InlineNonnegativeInteger 的 inline 容量上界（不含）：[0, 2^62)。</summary>
    internal const ulong NonnegIntInlineCap = 1UL << (64 - (int)BoxLzc.InlineNonnegInt - 1);

    /// <summary>清除 tag 位，保留 bits[61..0]。用于非负整数 inline 解码。</summary>
    internal const ulong NonnegIntPayloadMask = NonnegIntInlineCap - 1;

    /// <summary>清除高 <see cref="BoxLzc.InlineNegInt"/> 位，保留 bits[61..0]。用于负整数 inline 编码。</summary>
    internal const ulong NegIntPayloadMask = (1UL << (64 - (int)BoxLzc.InlineNegInt)) - 1;

    /// <summary>恢复高 <see cref="BoxLzc.InlineNegInt"/> 位（0xC000…）。用于负整数 inline 解码。</summary>
    internal const ulong NegIntSignRestore = ~NegIntPayloadMask;

    /// <summary>InlineNegativeInteger 的 inline 下界（含）：-2^61。</summary>
    internal const long NegIntInlineMin = -(1L << (64 - (int)BoxLzc.InlineNegInt - 1));

    /// <summary>Boolean false codeword (LZC=62): 0x2.</summary>
    internal const ulong BoxFalse = 1UL << (63 - (int)BoxLzc.Boolean);

    /// <summary>Boolean true codeword (LZC=62): 0x3.</summary>
    internal const ulong BoxTrue = BoxFalse | 1UL;

    /// <summary>Null codeword (LZC=63): 0x1.</summary>
    internal const ulong BoxNull = 1UL << (63 - (int)BoxLzc.Null);

    /// <summary>Uninitialized codeword (LZC=64): all bits zero.</summary>
    internal const ulong BoxUninitialized = default;
}
