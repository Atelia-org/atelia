using Atelia.StateJournal.Internal;
using Nonneg = Atelia.StateJournal.Serialization.ScalarRules.NonnegativeInteger;
using Neg = Atelia.StateJournal.Serialization.ScalarRules.NegativeInteger;
using Fp = Atelia.StateJournal.Serialization.ScalarRules.FloatingPoint;
using DurRefEnc = Atelia.StateJournal.Serialization.ScalarRules.DurableRefEncoding;

namespace Atelia.StateJournal.Serialization;

// ai:test `tests/StateJournal.Tests/Serialization/TaggedValueDispatcherTests.cs`
internal static class TaggedValueDispatcher {
    internal static bool UpdateOrInit(ref BinaryDiffReader reader, ref ValueBox old) {
        byte head = reader.ReadTag();
        return head switch {
            <= Nonneg.MaxInline => ValueBox.ByteFace.UpdateOrInit(ref old, Nonneg.DecodeTagOnly(head)),
            Nonneg.Follow1 => ValueBox.ByteFace.UpdateOrInit(ref old, reader.TaggedNonnegative1()),
            Nonneg.Follow2 => ValueBox.UInt16Face.UpdateOrInit(ref old, reader.TaggedNonnegative2()),
            Nonneg.Follow4 => ValueBox.UInt32Face.UpdateOrInit(ref old, reader.TaggedNonnegative4()),
            Nonneg.Follow8 => ValueBox.UInt64Face.UpdateOrInit(ref old, reader.TaggedNonnegative8()),
            >= Neg.InlineBase and <= Neg.MaxInline => ValueBox.Int32Face.UpdateOrInit(ref old, TaggedInt.DecodeNegativeTagOnly(Neg.DecodeTagOnly(head))),
            Neg.Follow1 => ValueBox.Int32Face.UpdateOrInit(ref old, reader.TaggedNegative1()),
            Neg.Follow2 => ValueBox.Int32Face.UpdateOrInit(ref old, reader.TaggedNegative2()),
            Neg.Follow4 => ValueBox.Int64Face.UpdateOrInit(ref old, reader.TaggedNegative4()),
            Neg.Follow8 => ValueBox.Int64Face.UpdateOrInit(ref old, reader.TaggedNegative8()),
            ScalarRules.False => ValueBox.BooleanFace.UpdateOrInit(ref old, false),
            ScalarRules.True => ValueBox.BooleanFace.UpdateOrInit(ref old, true),
            ScalarRules.Null => ValueBox.UpdateToNull(ref old),
            Fp.Follow2 => ValueBox.HalfFace.UpdateOrInit(ref old, reader.TaggedHalf()),
            Fp.Follow4 => ValueBox.SingleFace.UpdateOrInit(ref old, reader.TaggedSingle()),
            Fp.Follow8 => ValueBox.ExactDoubleFace.UpdateOrInit(ref old, reader.TaggedDouble()), // 内部存储一律走精确语义，RoundedDouble只是外部写入路径之一。
            >= DurRefEnc.MinTag and <= DurRefEnc.MaxTag => ReadDurableRef(head, ref reader, ref old),
            _ => throw new InvalidDataException($"Unsupported tagged value head 0x{head:X2}. The current reader only supports the CBOR-inspired scalar subset (major type 0/1/5/7)."),
        };
    }

    private static bool ReadDurableRef(byte head, ref BinaryDiffReader reader, ref ValueBox old) {
        var kind = DurRefEnc.DecodeKind(head);
        if (!DurableRef.IsValidKind(kind)) { throw new InvalidDataException($"Invalid DurableRef kind '{kind}' from head 0x{head:X2}."); }
        uint id = DurRefEnc.IsWidePayload(head) ? reader.FixedUInt32() : reader.FixedUInt16();
        if (id == 0) { throw new InvalidDataException("Invalid DurableRef LocalId=0. Use tagged null for null references."); }
        return ValueBox.DurableRefFace.UpdateOrInit(ref old, new DurableRef(kind, new LocalId(id)));
    }
}
