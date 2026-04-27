using System.Buffers.Binary;
using Atelia.StateJournal.Internal;
using Atelia.StateJournal.Pools;

namespace Atelia.StateJournal.Serialization;

internal ref struct BinaryDiffReader {
    private ReadOnlySpan<byte> _remaining;
    private readonly int _initialLength;
    private readonly StringPool? _symbolPool;
    private readonly LoadPlaceholderTracker? _placeholderTracker;

    internal BinaryDiffReader(ReadOnlySpan<byte> source, StringPool? symbolPool = null, LoadPlaceholderTracker? placeholderTracker = null) {
        _remaining = source;
        _initialLength = source.Length;
        _symbolPool = symbolPool;
        _placeholderTracker = placeholderTracker;
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
    internal ReadOnlySpan<byte> ReadBytes() {
        int length = ReadCount();
        return ReadSpan(length);
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

    /// <summary>
    /// <see cref="Symbol"/> 的裸读取。需要调用方提供 symbol 解码上下文。
    /// typed <see cref="Symbol"/> wire 的 health 契约要求 id 非零；读到 <see cref="SymbolId.Null"/> 会抛 <see cref="InvalidDataException"/>。
    /// </summary>
    internal Symbol BareSymbol(bool asKey) {
        SymbolId id = BareSymbolId(asKey);
        if (id.IsNull) { throw new InvalidDataException("Typed Symbol payload must not contain the no-symbol sentinel (SymbolId.Null). The wire data is corrupted or produced by a legacy writer."); }
        if (_symbolPool is null) { throw new InvalidDataException("Symbol deserialization requires a symbol pool context."); }
        if (_symbolPool.TryGetValue(id.ToSlotHandle(), out string value)) { return new Symbol(value); }
        if (_placeholderTracker is not null) { return new Symbol(_placeholderTracker.Create(id)); }
        throw new InvalidDataException($"Missing symbol entry for SymbolId {id.Value} during Symbol deserialization.");
    }

    /// <summary>已编码 <see cref="SymbolId"/> 的裸读取，不做任何 <see cref="Revision"/> 级转换。</summary>
    internal SymbolId BareSymbolId(bool asKey) => new(BareUInt32(asKey));

    /// <summary>
    /// 值语义 string 的裸读取。格式：VarUInt header，后跟 payload。
    /// header LSB=0 → UTF-16LE，header 本身就是 payloadByteCount。
    /// header LSB=1 → UTF-8，payloadByteCount = header &gt;&gt; 1。
    /// </summary>
    internal string BareStringPayload(bool asKey) => StringPayloadCodec.ReadFrom(ref this);
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
    internal ushort FixedUInt16() => RawUInt16();
    internal uint FixedUInt32() => RawUInt32();

    /// <summary>
    /// 读取一个 tagged string payload（调用方已消耗 <see cref="ScalarRules.StringPayload.Tag"/> (0xC0) 标签字节）。
    /// null 走 <see cref="ScalarRules.Null"/> 路径，本方法只处理非 null payload，永远返回非 null（空字符串编码为长度 0 的 UTF-16LE）。
    /// </summary>
    internal string TaggedStringPayload() => BareStringPayload(asKey: false);
    #endregion
}
