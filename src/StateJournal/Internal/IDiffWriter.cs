namespace Atelia.StateJournal.Internal;

/// <summary>
/// 用于实现<see cref="DurableObject.WritePendingDiff(IDiffWriter)"/>方法。
/// 具体使用见<see cref="DictCore.WritePendingDiff(IDiffWriter)"/>
/// 调用分派见<see cref="ITypeHelper{T}"/>
/// </summary>
internal interface IDiffWriter {
    #region 操作数入栈 用于异构混杂容器，自描述，先类型再值，类似CBOR
    void PushTypedByte(byte value);
    void PushTypedSByte(sbyte value);
    void PushTypedUInt16(ushort value);
    void PushTypedInt16(short value);
    void PushTypedInt32(int value);
    void PushTypedDouble(double value);
    void PushTypedString(string? value);
    #endregion

    #region 操作数入栈 用于类型特化容器，无类型信息，直接写裸值，类似ProtoBuffer
    void PushRawByte(byte value);
    void PushRawSByte(sbyte value);
    void PushRawUInt16(ushort value);
    void PushRawInt16(short value);
    void PushRawInt32(int value);
    void PushRawDouble(double value);
    void PushRawString(string? value);
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
