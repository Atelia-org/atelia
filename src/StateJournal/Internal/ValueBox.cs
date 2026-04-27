using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Atelia.StateJournal.Pools;

namespace Atelia.StateJournal.Internal;

/// <summary>
///
/// </summary>
/// <remarks>
/// 作为Dictionary的TValue时，如果TKey是4字节值，`StructLayout(Pack = 4)`能避免Padding以节约4字节内存。
/// </remarks>
/// <summary>
/// 内部值存储的不透明载体。此类型本身为 public 以便用作泛型类型参数，
/// 但所有成员均为 internal，外部代码不应直接操作。
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal readonly partial struct ValueBox {
    private readonly ulong _bits;
    internal ValueBox(ulong bits) => _bits = bits;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly ulong GetBits() => _bits;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal readonly BoxLzc GetLzc() => (BoxLzc)BitOperations.LeadingZeroCount(GetBits());

    public static ValueBox Null => new(LzcConstants.BoxNull); // 有意避开了0值，default，以实现内部的明确赋值检查。
    public readonly bool IsNull => GetBits() == LzcConstants.BoxNull;
    internal readonly bool IsUninitialized => GetBits() == LzcConstants.BoxUninitialized;

    /// <summary>
    /// 估算 <see cref="ValueBox.Write"/> 写入的 bare 字节数（含 1B tag）。
    /// 用于 cost-model 决策；不要求精确，仅与真实值同数量级。
    /// </summary>
    /// <remarks>
    /// <para>调用方契约：必须保证 box 处于"活"状态（slot/pool entry 未被释放，且非
    /// <see cref="IsUninitialized"/>）。CMS Step 1 已让所有 mutation 路径在 mutate 前
    /// snapshot 此值，所以 StringPayload 分支可以安全解引用 <see cref="ValuePools.OfOwnedString"/>。</para>
    /// <para>StringPayload 采用 <c>1B tag + VarUInt(L*2) + L*2</c> 的 O(1) 上界算法
    /// （UTF-16 字节数；UTF-8 写出时按较小者，但 estimate 不必复刻）。全 ASCII 时
    /// 偏大 ~2×，cost-model 容忍。空 string 走 <c>1B tag + 1B header(=VarUInt(0))</c> = 2u。</para>
    /// </remarks>
    internal readonly uint EstimateBareSize() => GetLzc() switch {
        BoxLzc.Boolean => 2, // tag + 1B
        BoxLzc.Null => 1,
        BoxLzc.InlineDouble => 9, // tag + 8B
        BoxLzc.InlineNonnegInt or BoxLzc.InlineNegInt => 9, // tag + VarUInt(<=8)
        BoxLzc.HeapSlot => GetHeapKind() switch {
            HeapValueKind.StringPayload => EstimateStringPayloadBareSize(),
            _ => 9, // float/integer 走 VarUInt；Symbol 走 SymbolId VarUInt（更短，9 是上界）
        },
        BoxLzc.DurableRef => 7, // tag + kind + LocalId VarUInt(<=5)
        _ => throw new UnreachableException($"EstimateBareSize on invalid LZC={(int)GetLzc()} (Uninitialized 或未分配槽位)。调用方必须先排除 IsUninitialized。"),
    };

    /// <summary>
    /// StringPayload 上界算法：<c>1B tag + VarUInt(utf16Bytes) + utf16Bytes</c>，其中 <c>utf16Bytes = s.Length * 2</c>。
    /// 不做 UTF-8 dry-run（<see cref="string.Length"/> 是 O(1) 字段读取，hot mutation path 友好）。
    /// </summary>
    private readonly uint EstimateStringPayloadBareSize() {
        Debug.Assert(GetLzc() == BoxLzc.HeapSlot && GetHeapKind() == HeapValueKind.StringPayload);
        // pool 内 string 永不为 null（StringPayloadFace.From 已分流 null 到 ValueBox.Null），
        // 但空 string ("") 是合法值，header = VarUInt(0) = 1B → 总 size = 2u。
        string s = ValuePools.OfOwnedString[GetHeapHandle()];
        if (s.Length == 0) { return 2u; }
        uint utf16Bytes = checked((uint)s.Length * 2u);
        return checked(1u + CostEstimateUtil.VarIntSize(utf16Bytes) + utf16Bytes);
    }

    public readonly ValueKind GetValueKind() => GetLzc() switch {
        BoxLzc.InlineDouble => ValueKind.FloatingPoint,
        BoxLzc.InlineNonnegInt => ValueKind.NonnegativeInteger,
        BoxLzc.InlineNegInt => ValueKind.NegativeInteger,
        BoxLzc.HeapSlot => GetHeapKind() switch {
            HeapValueKind.FloatingPoint => ValueKind.FloatingPoint,
            HeapValueKind.NonnegativeInteger => ValueKind.NonnegativeInteger,
            HeapValueKind.NegativeInteger => ValueKind.NegativeInteger,
            HeapValueKind.Symbol => ValueKind.Symbol,
            HeapValueKind.StringPayload => ValueKind.String,
            _ => throw new UnreachableException()
        },
        BoxLzc.DurableRef => GetDurRefKind() switch {
            DurableObjectKind.MixedDict => ValueKind.MixedDict,
            DurableObjectKind.TypedDict => ValueKind.TypedDict,
            DurableObjectKind.MixedDeque => ValueKind.MixedDeque,
            DurableObjectKind.TypedDeque => ValueKind.TypedDeque,
            DurableObjectKind.MixedOrderedDict => ValueKind.MixedOrderedDict,
            DurableObjectKind.TypedOrderedDict => ValueKind.TypedOrderedDict,
            DurableObjectKind.Text => ValueKind.Text,
            _ => throw new UnreachableException()
        },
        BoxLzc.Boolean => ValueKind.Boolean,
        BoxLzc.Null => ValueKind.Null,
        _ => throw new UnreachableException()
    };

    #region HeapSlot
    internal const int HeapKindBitCount = 6, ExclusiveBitCount = 1, HeapHandleBitCount = 32;
    private const int HeapKindShift = ExclusiveBitCount + HeapHandleBitCount; // 33
    private const ulong ExclusiveBit = 1UL << HeapHandleBitCount; // bit32
    private readonly HeapValueKind GetHeapKind() {
        Debug.Assert(GetLzc() == BoxLzc.HeapSlot);
        return (HeapValueKind)((GetBits() >> HeapKindShift) & HeapValueKindHelper.BitMask);
    }
    /// <summary>依赖<see cref="HeapHandleBitCount"/> == 32</summary>
    private readonly SlotHandle GetHeapHandle() {
        Debug.Assert(GetLzc() == BoxLzc.HeapSlot);
        return new((uint)GetBits()); // 依赖 HeapHandleBitCount == 32
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly uint GetTagAndKind() => (uint)(GetBits() >> HeapKindShift);
    /// <summary>默认设置ExclusiveBit。目前仅对Heap Float/Integer有效。</summary>
    private static ValueBox EncodeHeapSlot(HeapValueKind kind, SlotHandle handle) => new(LzcConstants.HeapSlotTag | ((ulong)kind << HeapKindShift) | ExclusiveBit | handle.Packed);
    #endregion

    #region Bits64 Slot 管理原语（ExclusiveSet 共用）
    internal const uint TagHeapKindFloat = (uint)(LzcConstants.HeapSlotTag >> HeapKindShift) | (uint)HeapValueKind.FloatingPoint;
    internal const uint TagHeapKindNonnegInt = (uint)(LzcConstants.HeapSlotTag >> HeapKindShift) | (uint)HeapValueKind.NonnegativeInteger;
    internal const uint TagHeapKindNegInt = (uint)(LzcConstants.HeapSlotTag >> HeapKindShift) | (uint)HeapValueKind.NegativeInteger;
    internal const uint TagHeapKindStringPayload = (uint)(LzcConstants.HeapSlotTag >> HeapKindShift) | (uint)HeapValueKind.StringPayload;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsHeapFloatOrInteger(uint tagAndKind) => tagAndKind is TagHeapKindFloat or TagHeapKindNonnegInt or TagHeapKindNegInt;
    internal readonly bool IsHeapFloatOrInteger() => IsHeapFloatOrInteger(GetTagAndKind());

    /// <summary>
    /// 检查当前 ValueBox 是否持有 Bits64 池的堆 Slot（NonnegativeInteger、NegativeInteger 或 FloatingPoint）。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly bool TryGetBits64Handle(out SlotHandle handle) {
        if (IsHeapFloatOrInteger()) {
            handle = GetHeapHandle();
            return true;
        }
        handle = default;
        return false;
    }

    /// <summary>释放旧 ValueBox 持有的独占 owned 堆 slot（Bits64 数值或 StringPayload）。冻结 slot 不释放（属于 committed 共享）。</summary>
    private static void FreeOldOwnedHeapIfNeeded(ValueBox old) {
        if (!old.IsExclusive) { return; }
        if (old.TryGetBits64Handle(out SlotHandle h)) {
            ValuePools.OfBits64.Free(h);
            return;
        }
        if (old.GetTagAndKind() == TagHeapKindStringPayload) { ValuePools.OfOwnedString.Free(old.GetHeapHandle()); }
    }

    /// <summary>
    /// 为新的 64-bit 原始值获取 Slot：若旧 box 持有独占 Bits64 Slot 则 inplace 更新并返回同一 handle，
    /// 否则分配新 Slot（旧 box 冻结或非 Bits64 时触发）。
    /// </summary>
    private static SlotHandle StoreOrReuseBits64(ValueBox old, ulong rawBits) {
        if (old.TryGetBits64Handle(out SlotHandle h) && old.IsExclusive) {
            ValuePools.OfBits64[h] = rawBits;
            return h;
        }
        FreeOldOwnedHeapIfNeeded(old);
        return ValuePools.OfBits64.Store(rawBits);
    }

    /// <summary>当前 ValueBox 是否持有独占（可变）的堆 Slot。仅对 HeapSlot 编码有意义。</summary>
    private readonly bool IsExclusive => (GetBits() & ExclusiveBit) != 0;

    /// <summary>清除 Exclusive bit。仅对 HeapSlot 调用安全（inline 值的 bit32 是 payload）。</summary>
    private readonly ValueBox AsFrozen() => new(GetBits() & ~ExclusiveBit);

    /// <summary>
    /// 冻结 ValueBox 用于 Commit 时共享。
    /// 对堆分配值：清除 Exclusive bit。对 inline 值：直接返回（bit32 是 payload 的一部分，不可修改）。
    /// </summary>
    internal static ValueBox Freeze(ValueBox box) =>
        box.GetLzc() == BoxLzc.HeapSlot ? box.AsFrozen() : box;

    internal static ValueBox CloneFrozenForNewOwner(ValueBox box) {
        if (box.TryGetBits64Handle(out SlotHandle h)) {
            ulong rawBits = ValuePools.OfBits64[h];
            SlotHandle newHandle = ValuePools.OfBits64.Store(rawBits);
            return EncodeHeapSlot(box.GetHeapKind(), newHandle).AsFrozen();
        }
        if (box.GetLzc() == BoxLzc.HeapSlot && box.GetTagAndKind() == TagHeapKindStringPayload) {
            // 第一版：frozen StringPayload fork → 深 clone。简单且正确，避免共享 slot 的双 free 风险；
            // 后续若引入引用计数可优化为共享。
            string value = ValuePools.OfOwnedString[box.GetHeapHandle()];
            SlotHandle newHandle = ValuePools.OfOwnedString.Store(value);
            return EncodeHeapSlot(HeapValueKind.StringPayload, newHandle).AsFrozen();
        }
        return box;
    }

    /// <summary>
    /// 无条件释放 ValueBox 持有的 owned 堆 Slot（Bits64 数值或 StringPayload），不论 exclusive/frozen 状态。
    /// 调用者必须确保自己是该 Slot 的最后持有者。对 inline 值和 InternPool 类型（Symbol）无操作。
    /// </summary>
    internal static void ReleaseOwnedHeapSlot(ValueBox box) {
        if (box.TryGetBits64Handle(out SlotHandle h)) {
            ValuePools.OfBits64.Free(h);
            return;
        }
        if (box.GetLzc() == BoxLzc.HeapSlot && box.GetTagAndKind() == TagHeapKindStringPayload) {
            ValuePools.OfOwnedString.Free(box.GetHeapHandle());
        }
    }

    internal static bool UpdateToNull(ref ValueBox old) {
        if (old.IsNull) { return false; }
        FreeOldOwnedHeapIfNeeded(old);
        old = Null;
        return true;
    }

    #endregion

    internal interface ITypedFace<T> where T : notnull {
        static abstract ValueBox From(T? value);
        /// <summary>
        /// Update if <paramref name="old"/> is not Uninitialized.
        /// Init if <paramref name="old"/> is Uninitialized.
        /// </summary>
        /// <param name="old">要突变的 slot。</param>
        /// <param name="value">新值。</param>
        /// <param name="oldBareBytesBeforeMutation">
        /// 在 slot 被任何修改之前，旧 ValueBox 的 <see cref="EstimateBareSize"/> 值（不含 key bytes）。
        /// 即使返回 false (no-op) 也设为有效值（与新 box 的 estimate 相同）。
        /// 调用方将此值传给 tracker 的 AfterUpsert / AfterSet 用于 dirty 增量减算，
        /// 避免在 inplace 覆写后对旧 slot 重新解码。
        /// </param>
        /// <returns>is <paramref name="old"/> changed.</returns>
        static abstract bool UpdateOrInit(ref ValueBox old, T? value, out uint oldBareBytesBeforeMutation);
        static abstract GetIssue Get(ValueBox box, out T? value);
    }
}
