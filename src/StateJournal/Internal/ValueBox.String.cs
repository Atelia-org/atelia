using Atelia.StateJournal.Internal;

namespace Atelia.StateJournal;

// ai:test `tests/StateJournal.Tests/Internal/ValueBoxStringTests.cs`
partial struct ValueBox {

    internal readonly struct StringFace : ITypedFace<string> {
        internal const uint TagHeapKindString = (uint)(LzcConstants.HeapSlotTag >> KindShift) | (uint)ValueKind.String;

        /// <summary>
        /// 将字符串编码为 ValueBox。通过 <see cref="ValuePools.Strings"/> InternPool 去重。
        /// </summary>
        /// <remarks>
        /// 同值字符串（Ordinal 相等）共享同一 SlotHandle，
        /// 保证 <see cref="ValueEquals"/> 快速路径命中（bits 相等 → 值相等）。
        /// </remarks>
        public static ValueBox From(string? value) => value is not null
            ? EncodeHeapSlot(ValueKind.String, ValuePools.Strings.Store(value))
            : Null;
        /// <summary>
        /// 独占更新：将 ValueBox 覆写为指定的字符串值。
        /// </summary>
        /// <remarks>
        /// 旧值如果持有 <see cref="ValuePools.Bits64"/> slot（数值类型），会立即释放。
        /// 旧值如果持有 <see cref="ValuePools.Strings"/> slot（字符串类型），
        /// 因 InternPool 共享语义不支持手动 Free，旧 slot 由 Mark-Sweep GC 回收。
        /// </remarks>
        public static bool Update(ref ValueBox old, string? value) {
            FreeOldBits64IfNeeded(old);
            old = From(value);
            return true;
        }
        /// <summary>
        /// 尝试将 ValueBox 读取为字符串。
        /// </summary>
        /// <param name="value">读取到的字符串。仅当返回 <see cref="GetIssue.None"/> 时有效。</param>
        /// <returns>
        /// <see cref="GetIssue.None"/> 成功；
        /// <see cref="GetIssue.TypeMismatch"/> 当 ValueBox 不是字符串类型。
        /// </returns>
        public static GetIssue Get(ValueBox box, out string? value) {
            if (box.IsNull) {
                value = null;
                return GetIssue.None;
            }
            // if (GetLZC() == LzcCode.HeapSlot && GetHeapKind() == DurableValueKind.String) {
            if ((uint)(box._bits >> KindShift) == TagHeapKindString) {
                value = ValuePools.Strings[box.GetHeapHandle()];
                return GetIssue.None;
            }
            value = null;
            return GetIssue.TypeMismatch;
        }
    }
}
