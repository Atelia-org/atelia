namespace Atelia.StateJournal.Internal;

/// <summary>
/// 用于实现<see cref="DurableObject.WritePendingDiff(IDiffWriter)"/>方法。
/// 具体使用见<see cref="DictCore.WritePendingDiff(IDiffWriter)"/>
/// 调用分派见<see cref="ITypeHelper{T}"/>
/// </summary>
internal interface IDiffWriter {
    void WriteCount(int count);
    void WriteBytes(ReadOnlySpan<byte> array);

    #region 写值，用于异构混杂容器，自描述，先类型再值，类似CBOR。
    void TaggedNull();
    void TaggedBoolean(bool value);
    void TaggedString(string? value);
    void TaggedDurableObjectRef(LocalId value);

    void TaggedFloatingPoint(double value);
    void TaggedNonnegativeInteger(ulong value);
    void TaggedNegativeInteger(long value);
    #endregion

    #region 写值，用于类型特化容器，无类型信息，直接写值，比如Base128套ZigZag之类的。
    void BareBoolean(bool value, bool asKey);
    void BareString(string? value, bool asKey);
    void BareDurableObjectRef(LocalId value, bool asKey);

    void BareDouble(double value, bool asKey);
    void BareSingle(float value, bool asKey);
    void BareHalf(Half value, bool asKey);

    void BareUInt64(ulong value, bool asKey);
    void BareUInt32(uint value, bool asKey);
    void BareUInt16(ushort value, bool asKey);
    void BareByte(byte value, bool asKey);

    void BareInt64(long value, bool asKey);
    void BareInt32(int value, bool asKey);
    void BareInt16(short value, bool asKey);
    void BareSByte(sbyte value, bool asKey);
    #endregion
}
