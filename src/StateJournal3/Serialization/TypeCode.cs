namespace Atelia.StateJournal3.Serialization;

public static class TypeCodecConst {
    public const int TypeBitCount = 5, ArgBigCount = 8 - TypeBitCount;
    public const int TypeMask = (1 << TypeBitCount) - 1;
}

public enum TrailingBitsArg : byte {
    Zero = 0,
    Bits8,
    Bits16,
    Bits32,
    Bits64,
    UntilSequenceEnd

}

/// <summary>CBOR风格的值类型信息编码。用于自描述数据和记录泛型类型编码TypeCodec。为LittleEndian优化。</summary>
/// <see cref="TypeCodec"/>
/// <see cref="IValueOps.WriteTypedData(IBufferWriter{byte}, T)"/>
[Flags]
public enum TypeCode : byte {

    SequenceEnd = 0,

    Byte, // 8-bit
    UInt16, // 16-bit
    UInt32, // 32-bit
    UInt64, // 64-bit
    UInt128, // 128-bit

    SByte,
    Int16,
    Int32,
    Int64,
    Int128,

    Boolean,
    Null,
    Undefined, // for Delete / Remove / Tombstone

    List = 1,
    Dict,

    Array,
    NDArray,

    Char,


    Single,
    Double,

    Decimal,
    Guid,

    Utf16String,
    Utf8String,
}
