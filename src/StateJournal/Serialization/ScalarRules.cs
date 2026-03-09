using System.Diagnostics;

namespace Atelia.StateJournal.Serialization;

internal static class ScalarRules {
    internal const byte False = 0xF4;
    internal const byte True = 0xF5;
    internal const byte Null = 0xF6;

    internal readonly struct NonnegativeInteger : ITaggedIntRule {
        internal const byte MaxInline = 0x17;
        internal const byte Follow1 = 0x18;
        internal const byte Follow2 = 0x19;
        internal const byte Follow4 = 0x1A;
        internal const byte Follow8 = 0x1B;

        public static ulong TagOnlyMaxValue => MaxInline;

        public static byte EncodeTagOnly(ulong value) {
            if (value > TagOnlyMaxValue) { throw new ArgumentOutOfRangeException(nameof(value), value, "Value is outside the CBOR inline range."); }

            return (byte)value;
        }
        internal static byte DecodeTagOnly(byte tag) {
            Debug.Assert(tag <= MaxInline);
            return tag;
        }

        public static byte Tag1 => Follow1;
        public static byte Tag2 => Follow2;
        public static byte Tag4 => Follow4;
        public static byte Tag8 => Follow8;
    }

    internal readonly struct NegativeInteger : ITaggedIntRule {
        internal const byte InlineBase = 0x20;
        internal const byte MaxInline = 0x37;
        internal const byte Follow1 = 0x38;
        internal const byte Follow2 = 0x39;
        internal const byte Follow4 = 0x3A;
        internal const byte Follow8 = 0x3B;

        public static ulong TagOnlyMaxValue => 23;

        public static byte EncodeTagOnly(ulong value) {
            if (value > TagOnlyMaxValue) { throw new ArgumentOutOfRangeException(nameof(value), value, "Value is outside the CBOR inline range."); }

            return (byte)(InlineBase + value); // TODO: 这里用`bit or`会不会比加法更合适？
        }
        internal static byte DecodeTagOnly(byte tag) {
            Debug.Assert(InlineBase <= tag && tag <= MaxInline);
            return (byte)(tag - InlineBase); // TODO: 这里用`bit clear`会不会比减法更合适？
        }

        public static byte Tag1 => Follow1;
        public static byte Tag2 => Follow2;
        public static byte Tag4 => Follow4;
        public static byte Tag8 => Follow8;
    }

    internal readonly struct FloatingPoint : ITaggedFloatRule {
        internal const byte Follow2 = 0xF9;
        internal const byte Follow4 = 0xFA;
        internal const byte Follow8 = 0xFB;

        public static byte Tag2 => Follow2;
        public static byte Tag4 => Follow4;
        public static byte Tag8 => Follow8;
    }
}
