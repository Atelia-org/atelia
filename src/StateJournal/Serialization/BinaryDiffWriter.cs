using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using Atelia.StateJournal.Internal;

namespace Atelia.StateJournal.Serialization;

internal class BinaryDiffWriter : IDiffWriter {
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

    public void BareDurableObjectRef(LocalId value, bool asKey) => BareUInt32(value.Value, asKey);

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

    public void DictBegin(int removeCount, int upsertCount) {
        throw new NotImplementedException();
    }

    public void DictEnd() {
        throw new NotImplementedException();
    }

    public void DictRemoveBegin(int count) {
        Debug.Assert(count >= 0);
        BareUInt32((uint)count, false);
    }

    public void DictUpsertBegin(int count) {
        Debug.Assert(count >= 0);
        BareUInt32((uint)count, false);
    }

    public void TaggedBoolean(bool value) {
        _downstream.GetSpan(1)[0] = value ? ScalarRules.True : ScalarRules.False;
        _downstream.Advance(1);
    }

    public void TaggedDurableObjectRef(LocalId value) {
        throw new NotImplementedException();
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
