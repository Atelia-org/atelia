using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using Atelia.StateJournal.Internal;

namespace Atelia.StateJournal.Serialization;

internal ref struct BinaryDiffWriter {
    internal const byte BareFalse = 0, BareTrue = 1;
    private IBufferWriter<byte> _downstream = null!;

    internal BinaryDiffWriter(IBufferWriter<byte> downstream) {
        _downstream = downstream;
    }

    public void BareBoolean(bool value, bool asKey) {
        _downstream.GetSpan(1)[0] = value ? BareTrue : BareFalse;
        _downstream.Advance(1);
    }

    public void BareString(string? value, bool asKey) {
        throw new NotImplementedException();
    }

    /// <summary>依赖于<see cref="DurableObjectKind"/>基于byte。</summary>
    public void BareDurableRef(LocalId value, bool asKey) {
        BareUInt32(value.Value, asKey);
    }

    public void BareDouble(double value, bool asKey) {
        BinaryPrimitives.WriteDoubleLittleEndian(_downstream.GetSpan(8), value);
        _downstream.Advance(8);
    }
    public void BareSingle(float value, bool asKey) {
        BinaryPrimitives.WriteSingleLittleEndian(_downstream.GetSpan(4), value);
        _downstream.Advance(4);
    }
    public void BareHalf(Half value, bool asKey) {
        BinaryPrimitives.WriteHalfLittleEndian(_downstream.GetSpan(2), value);
        _downstream.Advance(2);
    }

    public void BareUInt64(ulong value, bool asKey) => VarInt.WriteUInt64(_downstream, value);
    public void BareUInt32(uint value, bool asKey) => VarInt.WriteUInt32(_downstream, value);
    public void BareUInt16(ushort value, bool asKey) => VarInt.WriteUInt16(_downstream, value);
    public void BareInt64(long value, bool asKey) => VarInt.WriteInt64(_downstream, value);
    public void BareInt32(int value, bool asKey) => VarInt.WriteInt32(_downstream, value);
    public void BareInt16(short value, bool asKey) => VarInt.WriteInt16(_downstream, value);

    public void BareByte(byte value, bool asKey) {
        _downstream.GetSpan(1)[0] = value;
        _downstream.Advance(1);
    }
    public void BareSByte(sbyte value, bool asKey) {
        _downstream.GetSpan(1)[0] = (byte)value;
        _downstream.Advance(1);
    }

    public void WriteCount(int count) {
        Debug.Assert(count >= 0); // 内部类型，避免层层重复检查。
        VarInt.WriteUInt32(_downstream, (uint)count);
    }
    public void WriteBytes(ReadOnlySpan<byte> array) {
        VarInt.WriteUInt32(_downstream, (uint)array.Length);
        array.CopyTo(_downstream.GetSpan(array.Length));
        _downstream.Advance(array.Length);
    }

    public void TaggedBoolean(bool value) {
        _downstream.GetSpan(1)[0] = value ? ScalarRules.True : ScalarRules.False;
        _downstream.Advance(1);
    }

    public void TaggedDurableRef(DurableRef value) {
        if (!DurableRef.IsValidKind(value.Kind)) { throw new InvalidDataException($"Invalid DurableRef kind '{value.Kind}'."); }
        if (value.IsNull) { throw new InvalidDataException("DurableRef LocalId cannot be null. Use TaggedNull for null references."); }

        bool wide = value.Id.Value > ushort.MaxValue;
        byte tag = ScalarRules.DurableRefEncoding.EncodeTag(value.Kind, wide);
        if (wide) {
            var span = _downstream.GetSpan(1 + 4);
            span[0] = tag;
            BinaryPrimitives.WriteUInt32LittleEndian(span[1..], value.Id.Value);
            _downstream.Advance(1 + 4);
        }
        else {
            var span = _downstream.GetSpan(1 + 2);
            span[0] = tag;
            BinaryPrimitives.WriteUInt16LittleEndian(span[1..], (ushort)value.Id.Value);
            _downstream.Advance(1 + 2);
        }
    }

    public void TaggedFloatingPoint(double value) {
        TaggedFloat<ScalarRules.FloatingPoint>.Write(_downstream, value);
    }

    public void TaggedNegativeInteger(long value) {
        TaggedInt.WriteNegative<ScalarRules.NegativeInteger>(_downstream, value);
    }

    public void TaggedNonnegativeInteger(ulong value) {
        TaggedInt.WriteNonnegative<ScalarRules.NonnegativeInteger>(_downstream, value);
    }

    public void TaggedNull() {
        _downstream.GetSpan(1)[0] = ScalarRules.Null;
        _downstream.Advance(1);
    }

    public void TaggedString(string? value) {
        throw new NotImplementedException();
    }
}
