namespace Atelia.StateJournal.Internal;

/// <summary>
/// 用于实现<see cref="DurableObject.WritePendingDiff(IDiffWriter)"/>方法。
/// 具体使用见<see cref="DictCore.WritePendingDiff(IDiffWriter)"/>
/// 调用分派见<see cref="ITypeHelper{T}"/>
/// </summary>
internal interface IDiffWriter {
    #region 操作数入栈 用于异构混杂容器，自描述，先类型再值，类似CBOR。
    void TaggedNull();
    void TaggedBoolean(bool value);
    void TaggedString(string? value);
    void TaggedLocalId(LocalId value);

    void TaggedFloatingPoint(double value);
    void TaggedNonnegativeInteger(ulong value);
    void TaggedNegativeInteger(long value);
    #endregion

    #region 操作数入栈 用于类型特化容器，无类型信息，直接写值，比如Base128套ZigZag之类的。
    void BareBoolean(bool value);
    void BareString(string? value);
    void BareLocalId(LocalId value);

    void BareDouble(double value);
    void BareSingle(float value);
    void BareHalf(Half value);

    void BareUInt64(ulong value);
    void BareUInt32(uint value);
    void BareUInt16(ushort value);
    void BareByte(byte value);

    void BareInt64(long value);
    void BareInt32(int value);
    void BareInt16(short value);
    void BareSByte(sbyte value);
    #endregion

    #region Dict diff payload
    void DictBegin();

    void DictRemoveBegin(int count);
    /// <summary>pop {Key} from stack and write a remove entry</summary>
    void DictRemove();
    void DictRemoveEnd();

    void DictUpsertBegin(int count);
    /// <summary>pop {Key,Value} from stack and write a upsert entry</summary>
    void DictUpsert();
    void DictUpsertEnd();

    void DictEnd();
    #endregion

    #region List diff payload
    #endregion
}
