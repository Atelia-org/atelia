namespace Atelia.StateJournal;

/// <summary>按照从最成功到最失败的顺序排列，从而支持大小比较。</summary>
public enum GetIssue : byte {
    None = 0,

    PrecisionLost,
    Saturated,
    OverflowedToInfinity,

    TypeMismatch = byte.MaxValue-1,
    NotFound = byte.MaxValue,
}
