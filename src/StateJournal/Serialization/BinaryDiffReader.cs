using System.Buffers.Binary;

namespace Atelia.StateJournal.Serialization;

internal ref struct BinaryDiffReader {
    private ReadOnlySpan<byte> _remaining;
    private readonly int _initialLength;

    internal BinaryDiffReader(ReadOnlySpan<byte> source) {
        _remaining = source;
        _initialLength = source.Length;
    }

    internal int ConsumedCount => _initialLength - _remaining.Length;
    internal int RemainingCount => _remaining.Length;
    internal bool End => _remaining.IsEmpty;

    internal void EnsureFullyConsumed() {
        if (!End) { throw new InvalidDataException($"Expected end of diff body, but {RemainingCount} trailing byte(s) remain."); }
    }

    private byte RawByte() {
        if (_remaining.IsEmpty) { throw new InvalidDataException("Unexpected end of diff body while reading a byte."); }

        byte value = _remaining[0];
        _remaining = _remaining[1..];
        return value;
    }
    private ushort RawUInt16() => BinaryPrimitives.ReadUInt16LittleEndian(ReadSpan(sizeof(ushort)));
    private uint RawUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(ReadSpan(sizeof(uint)));
    private ulong RawUInt64() => BinaryPrimitives.ReadUInt64LittleEndian(ReadSpan(sizeof(ulong)));

    #region Read Bare
    internal byte ReadTag() => RawByte();
    internal byte BareByte(bool asKey) => RawByte();
    internal sbyte BareSByte(bool asKey) => unchecked((sbyte)RawByte());

    internal bool BareBoolean(bool asKey) {
        byte value = RawByte();
        return value switch {
            0 => false,
            1 => true,
            _ => throw new InvalidDataException($"Invalid bare boolean byte 0x{value:X2}; expected 0x00 or 0x01."),
        };
    }

    internal ReadOnlySpan<byte> ReadSpan(int length) {
        if (length < 0) { throw new ArgumentOutOfRangeException(nameof(length)); }
        if (_remaining.Length < length) { throw new InvalidDataException($"Unexpected end of diff body while reading {length} byte(s)."); }

        ReadOnlySpan<byte> slice = _remaining[..length];
        _remaining = _remaining[length..];
        return slice;
    }

    private uint VarUInt32() {
        int consumed = VarInt.ReadUInt32(_remaining, out uint value);
        AdvanceVarInt("UInt32", consumed);
        return value;
    }

    internal int ReadCount() {
        uint count = VarUInt32();
        if (count > (uint)int.MaxValue) { throw new InvalidDataException($"count {count} exceeds Int32.MaxValue."); }
        return unchecked((int)count); // 前面抛异常已经检查过了。
    }

    internal ushort BareUInt16(bool asKey) {
        int consumed = VarInt.ReadUInt16(_remaining, out ushort value);
        AdvanceVarInt("UInt16", consumed);
        return value;
    }
    internal uint BareUInt32(bool asKey) => VarUInt32();
    internal ulong BareUInt64(bool asKey) {
        int consumed = VarInt.ReadUInt64(_remaining, out ulong value);
        AdvanceVarInt("UInt64", consumed);
        return value;
    }
    internal short BareInt16(bool asKey) {
        int consumed = VarInt.ReadInt16(_remaining, out short value);
        AdvanceVarInt("Int16", consumed);
        return value;
    }
    internal int BareInt32(bool asKey) {
        int consumed = VarInt.ReadInt32(_remaining, out int value);
        AdvanceVarInt("Int32", consumed);
        return value;
    }
    internal long BareInt64(bool asKey) {
        int consumed = VarInt.ReadInt64(_remaining, out long value);
        AdvanceVarInt("Int64", consumed);
        return value;
    }

    private void AdvanceVarInt(string typeName, int consumed) {
        if (consumed > 0) {
            _remaining = _remaining[consumed..];
            return;
        }

        VarInt.ErrorCode code = (VarInt.ErrorCode)(-consumed);
        throw new InvalidDataException($"Invalid varint while reading {typeName}: {code}.");
    }
    internal Half BareHalf(bool asKey) => BinaryPrimitives.ReadHalfLittleEndian(ReadSpan(2));
    internal float BareSingle(bool asKey) => BinaryPrimitives.ReadSingleLittleEndian(ReadSpan(sizeof(float)));
    internal double BareDouble(bool asKey) => BinaryPrimitives.ReadDoubleLittleEndian(ReadSpan(sizeof(double)));
    #endregion
    #region Read Taged
    internal byte TaggedNonnegative1() => RawByte();
    internal ushort TaggedNonnegative2() => RawUInt16();
    internal uint TaggedNonnegative4() => RawUInt32();
    internal ulong TaggedNonnegative8() => RawUInt64();
    internal int TaggedNegative1() => TaggedInt.DecodeNegative1(RawByte());
    internal int TaggedNegative2() => TaggedInt.DecodeNegative2(RawUInt16());
    internal long TaggedNegative4() => TaggedInt.DecodeNegative4(RawUInt32());
    internal long TaggedNegative8() => TaggedInt.DecodeNegative8(RawUInt64());
    internal Half TaggedHalf() => BareHalf(false);
    internal float TaggedSingle() => BareSingle(false);
    internal double TaggedDouble() => BareDouble(false);
    #endregion
}
