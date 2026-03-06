namespace Atelia.StateJournal;

public enum ValueKind : byte {
    Mask = (1 << Internal.ValueBox.HeapKindBitCount) - 1,
    Null = 0,
    Undefined,
    Boolean,
    String,

    #region 注意这3项表示数值的，需保持连续和保序，用于性能优化
    FloatingPoint,
    // 不能设计为Unsigend+Signed划分，否则会因类型不同使得`Encode((ulong)long.MaxValue) != Encode(long.MaxValue)`
    NonnegativeInteger,
    NegativeInteger,
    #endregion

    #region 注意这3项表示DurableObject的，需保持连续和保序，用于性能优化
    /// <summary><see cref="DurableDict{TKey}"/> heterogeneous</summary>
    MixedDict,

    /// <summary><see cref="DurableDict{TKey,TValue}"/>  homogeneous</summary>
    TypedDict,

    /// <summary><see cref="DurableList"/> heterogeneous</summary>
    MixedList,

    /// <summary><see cref="DurableList{T}"/> homogeneous</summary>
    TypedList,
    #endregion
}
