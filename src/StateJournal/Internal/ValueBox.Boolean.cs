using Atelia.StateJournal.Internal;

namespace Atelia.StateJournal;

partial struct ValueBox {
    internal readonly struct BooleanFace : ITypedFace<bool> {
        public static ValueBox False => new(LzcConstants.SimpleFalse);
        public static ValueBox True => new(LzcConstants.SimpleTrue);
        public static ValueBox From(bool value) => value ? True : False;
        /// <summary>将 ValueBox 覆写为指定的 bool 值。
        /// Boolean 始终 inline 编码，因此只需清理旧 Bits64 slot（如有）。</summary>
        public static bool Update(ref ValueBox old, bool value) {
            FreeOldBits64IfNeeded(old);
            old = From(value);
            return true;
        }
        public static GetIssue Get(ValueBox box, out bool value) => box._bits switch {
            LzcConstants.SimpleFalse => (GetIssue.None, value = false).Item1,
            LzcConstants.SimpleTrue => (GetIssue.None, value = true).Item1,
            _ => (GetIssue.TypeMismatch, value = default).Item1,
        };
    }
}
