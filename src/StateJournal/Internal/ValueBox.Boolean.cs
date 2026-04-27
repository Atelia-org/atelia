using System.Diagnostics;

namespace Atelia.StateJournal.Internal;

partial struct ValueBox {
    internal readonly struct BooleanFace : ITypedFace<bool> {
        public static ValueBox False => new(LzcConstants.BoxFalse);
        public static ValueBox True => new(LzcConstants.BoxTrue);
        public static ValueBox From(bool value) => value ? True : False;
        /// <summary>将 ValueBox 覆写为指定的 bool 值。
        /// Boolean 始终 inline 编码，因此只需清理旧 owned heap slot（如有）。</summary>
        public static bool UpdateOrInit(ref ValueBox old, bool value, out uint oldBareBytesBeforeMutation) {
            // Boolean 与 oldValue 无关的常量 estimate 路径无需特别捕获顺序，
            // 但接口契约要求在任何 mutation 之前确定 oldBareBytes。
            oldBareBytesBeforeMutation = old.IsUninitialized ? 0u : old.EstimateBareSize();
            // if (old.GetLzc() == BoxLzc.Boolean && old.DecodeBoolean() == value) { return false; }
            if (old.GetBits() == (value ? LzcConstants.BoxTrue : LzcConstants.BoxFalse)) { return false; }
            FreeOldOwnedHeapIfNeeded(old);
            old = From(value);
            return true;
        }
        public static GetIssue Get(ValueBox box, out bool value) {
            Debug.Assert(!box.IsUninitialized);
            return box.GetBits() switch {
                LzcConstants.BoxFalse => (GetIssue.None, value = false).Item1,
                LzcConstants.BoxTrue => (GetIssue.None, value = true).Item1,
                _ => (GetIssue.TypeMismatch, value = default).Item1,
            };
        }
    }
    private bool DecodeBoolean() {
        Debug.Assert(GetLzc() == BoxLzc.Boolean);
        return GetBits() == LzcConstants.BoxTrue;
    }
}
