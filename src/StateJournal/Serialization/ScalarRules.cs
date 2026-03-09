namespace Atelia.StateJournal.Serialization;

internal static class ScalarRules {
    internal const byte False = 0xF4;
    internal const byte True = 0xF5;
    internal const byte Null = 0xF6;

    internal readonly struct NonnegativeInteger : ITaggedIntRule {
        public static ulong TagOnlyMaxValue => 23;

        public static byte EncodeTagOnly(ulong value) {
            if (value > TagOnlyMaxValue) { throw new ArgumentOutOfRangeException(nameof(value), value, "Value is outside the CBOR inline range."); }

            return (byte)value;
        }

        public static byte Tag1 => 0x18;
        public static byte Tag2 => 0x19;
        public static byte Tag4 => 0x1A;
        public static byte Tag8 => 0x1B;
    }

    internal readonly struct NegativeInteger : ITaggedIntRule {
        public static ulong TagOnlyMaxValue => 23;

        public static byte EncodeTagOnly(ulong value) {
            if (value > TagOnlyMaxValue) { throw new ArgumentOutOfRangeException(nameof(value), value, "Value is outside the CBOR inline range."); }

            return (byte)(0x20 + value);
        }

        public static byte Tag1 => 0x38;
        public static byte Tag2 => 0x39;
        public static byte Tag4 => 0x3A;
        public static byte Tag8 => 0x3B;
    }

    internal readonly struct FloatingPoint : ITaggedFloatRule {
        public static byte Tag2 => 0xF9;
        public static byte Tag4 => 0xFA;
        public static byte Tag8 => 0xFB;
    }
}
