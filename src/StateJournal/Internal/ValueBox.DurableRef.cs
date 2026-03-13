using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Atelia.StateJournal.Internal;

partial struct ValueBox {
    internal readonly struct DurableObjectFace : ITypedFace<DurableObject> {
        internal const uint TagHeapKindMixedDict = (uint)(LzcConstants.HeapSlotTag >> HeapKindShift) | (uint)ValueKind.MixedDict;
        internal const uint TagHeapKindTypedDict = (uint)(LzcConstants.HeapSlotTag >> HeapKindShift) | (uint)ValueKind.TypedDict;
        internal const uint TagHeapKindMixedList = (uint)(LzcConstants.HeapSlotTag >> HeapKindShift) | (uint)ValueKind.MixedList;
        internal const uint TagHeapKindTypedList = (uint)(LzcConstants.HeapSlotTag >> HeapKindShift) | (uint)ValueKind.TypedList;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool IsDurableObject(ValueBox box) => box.GetTagAndKind() is TagHeapKindMixedDict or TagHeapKindTypedDict or TagHeapKindMixedList or TagHeapKindTypedList;

        /// <summary>
        /// 将DurableObject编码为 ValueBox。通过 <see cref="ValuePools.OfDurableObject"/> InternPool 去重。
        /// </summary>
        /// <remarks>
        /// 同一 DurableObject 实例（引用相等）共享同一 SlotHandle，
        /// 保证 <see cref="ValueEquals"/> 快速路径命中（bits 相等 → 值相等）。
        /// </remarks>
        public static ValueBox From(DurableObject? value) => value is not null
            ? EncodeHeapSlot(value.Kind, ValuePools.OfDurableObject.Store(value))
            : Null;

        /// <summary>
        /// 独占更新：将 ValueBox 覆写为指定的DurableObject值。
        /// </summary>
        /// <remarks>
        /// 旧值如果持有 <see cref="ValuePools.OfBits64"/> slot（数值类型），会立即释放。
        /// 旧值如果持有 InternPool slot（字符串或 DurableObject 类型），
        /// 因 InternPool 共享语义不支持手动 Free，旧 slot 由 Mark-Sweep GC 回收。
        /// </remarks>
        public static bool UpdateOrInit(ref ValueBox old, DurableObject? value) {
            if (value is null) {
                if (old.IsNull) { return false; }
            }
            else if (IsDurableObject(old) && old.DecodeDurableObject() == value) { return false; }
            FreeOldBits64IfNeeded(old);
            old = From(value);
            return true;
        }

        /// <summary>
        /// 尝试将 ValueBox 读取为DurableObject。
        /// </summary>
        /// <param name="value">读取到的DurableObject。仅当返回 <see cref="GetIssue.None"/> 时有效。</param>
        /// <returns>
        /// <see cref="GetIssue.None"/> 成功；
        /// <see cref="GetIssue.TypeMismatch"/> 当 ValueBox 不是DurableObject类型。
        /// </returns>
        public static GetIssue Get(ValueBox box, out DurableObject? value) {
            Debug.Assert(!box.IsUninitialized);
            if (box.IsNull) {
                value = null;
                return GetIssue.None;
            }
            if (IsDurableObject(box)) {
                value = box.DecodeDurableObject();
                return GetIssue.None;
            }
            value = null;
            return GetIssue.TypeMismatch;
        }
    }
    private DurableObject DecodeDurableObject() {
        Debug.Assert(DurableObjectFace.IsDurableObject(this));
        return ValuePools.OfDurableObject[GetHeapHandle()];
    }
}
