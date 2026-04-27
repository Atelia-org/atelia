using Atelia.StateJournal.Internal;
using Nonneg = Atelia.StateJournal.Serialization.ScalarRules.NonnegativeInteger;
using Neg = Atelia.StateJournal.Serialization.ScalarRules.NegativeInteger;
using Fp = Atelia.StateJournal.Serialization.ScalarRules.FloatingPoint;
using TaggedRefEnc = Atelia.StateJournal.Serialization.ScalarRules.TaggedRefEncoding;

namespace Atelia.StateJournal.Serialization;

// ai:test `tests/StateJournal.Tests/Serialization/TaggedValueDispatcherTests.cs`
internal static class TaggedValueDispatcher {
    internal static bool UpdateOrInit(ref BinaryDiffReader reader, ref ValueBox old) {
        byte head = reader.ReadTag();
        return head switch {
            <= Nonneg.MaxInline => ValueBox.ByteFace.UpdateOrInit(ref old, Nonneg.DecodeTagOnly(head), out _),
            Nonneg.Follow1 => ValueBox.ByteFace.UpdateOrInit(ref old, reader.TaggedNonnegative1(), out _),
            Nonneg.Follow2 => ValueBox.UInt16Face.UpdateOrInit(ref old, reader.TaggedNonnegative2(), out _),
            Nonneg.Follow4 => ValueBox.UInt32Face.UpdateOrInit(ref old, reader.TaggedNonnegative4(), out _),
            Nonneg.Follow8 => ValueBox.UInt64Face.UpdateOrInit(ref old, reader.TaggedNonnegative8(), out _),
            >= Neg.InlineBase and <= Neg.MaxInline => ValueBox.Int32Face.UpdateOrInit(ref old, TaggedInt.DecodeNegativeTagOnly(Neg.DecodeTagOnly(head)), out _),
            Neg.Follow1 => ValueBox.Int32Face.UpdateOrInit(ref old, reader.TaggedNegative1(), out _),
            Neg.Follow2 => ValueBox.Int32Face.UpdateOrInit(ref old, reader.TaggedNegative2(), out _),
            Neg.Follow4 => ValueBox.Int64Face.UpdateOrInit(ref old, reader.TaggedNegative4(), out _),
            Neg.Follow8 => ValueBox.Int64Face.UpdateOrInit(ref old, reader.TaggedNegative8(), out _),
            ScalarRules.False => ValueBox.BooleanFace.UpdateOrInit(ref old, false, out _),
            ScalarRules.True => ValueBox.BooleanFace.UpdateOrInit(ref old, true, out _),
            ScalarRules.Null => ValueBox.UpdateToNull(ref old),
            Fp.Follow2 => ValueBox.HalfFace.UpdateOrInit(ref old, reader.TaggedHalf(), out _),
            Fp.Follow4 => ValueBox.SingleFace.UpdateOrInit(ref old, reader.TaggedSingle(), out _),
            Fp.Follow8 => ValueBox.ExactDoubleFace.UpdateOrInit(ref old, reader.TaggedDouble(), out _), // 内部存储一律走精确语义，RoundedDouble只是外部写入路径之一。
            >= TaggedRefEnc.MinTag and <= TaggedRefEnc.MaxTag => ReadDurableRefOrSymbol(head, ref reader, ref old),
            ScalarRules.StringPayload.Tag => ValueBox.StringPayloadFace.UpdateOrInit(ref old, reader.TaggedStringPayload(), out _),
            _ => throw new InvalidDataException($"Unsupported tagged value head 0x{head:X2}. The current reader supports the CBOR-inspired scalar subset (major type 0/1/5/7) plus the tagged-ref (0xA0..0xBF) and string-payload (0xC0) extensions."),
        };
    }

    private static bool ReadDurableRefOrSymbol(byte head, ref BinaryDiffReader reader, ref ValueBox old) {
        var kind = TaggedRefEnc.DecodeKind(head);
        uint id = TaggedRefEnc.IsWidePayload(head) ? reader.FixedUInt32() : reader.FixedUInt16();
        if (id == 0) { throw new InvalidDataException($"Invalid id=0 in TaggedRefEncoding (kind={kind}). Use tagged null for null references."); }

        if (kind == TaggedRefKind.Symbol) { return ValueBox.SymbolIdFace.UpdateOrInit(ref old, new SymbolId(id), out _); }

        if (!TaggedRefKindHelper.TryToDurableObjectKind(kind, out DurableObjectKind objectKind)) { throw new InvalidDataException($"Invalid tagged ref kind '{kind}' from head 0x{head:X2}."); }
        return ValueBox.DurableRefFace.UpdateOrInit(ref old, new DurableRef(objectKind, new LocalId(id)), out _);
    }
}
