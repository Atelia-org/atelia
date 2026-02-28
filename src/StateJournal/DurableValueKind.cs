namespace Atelia.StateJournal;

public enum DurableValueKind : byte {
    Mask = (1 << ValueBox.HeapKindBitCount) - 1,
    Null = 0,
    Undefined,
    Boolean,
    FloatingPoint,
    // 不能设计为Unsigend+Signed划分，否则会因类型不同使得`Encode((ulong)long.MaxValue) != Encode(long.MaxValue)`
    NonnegativeInteger,
    NegativeInteger,
    String,
}
