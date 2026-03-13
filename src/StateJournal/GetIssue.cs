namespace Atelia.StateJournal;

/// <summary>按照从最成功到最失败的顺序排列，从而支持大小比较。</summary>
public enum GetIssue : byte {
    None = 0,

    PrecisionLost,
    Saturated,
    OverflowedToInfinity,

    LoadFailed,

    TypeMismatch = byte.MaxValue - 2,
    NotFound = byte.MaxValue - 1,
    UnsupportedType = byte.MaxValue,
}
