namespace Atelia.StateJournal;

public enum DurableValueKind : byte {
    Mask = (1 << ValueBox.HeapKindBitCount) - 1,
    Null = 0,
    Undefined,
    Boolean,

    #region 注意这3项表示数值的，需保持连续和保序，用于性能优化
    FloatingPoint,
    // 不能设计为Unsigend+Signed划分，否则会因类型不同使得`Encode((ulong)long.MaxValue) != Encode(long.MaxValue)`
    NonnegativeInteger,
    NegativeInteger,
    #endregion

    String,
}
