namespace Atelia.StateJournal;

/// <summary>按照从最成功到最失败的顺序排列，从而支持大小比较。</summary>
public enum GetIssue : byte {
    None = 0,

    PrecisionLost,
    Saturated,
    OverflowedToInfinity,

    LoadFailed,

    /// <summary>找到了目标值，但与承载值的变量类型不匹配。</summary>
    TypeMismatch = byte.MaxValue - 3,

    /// <summary>用于查找的索引或Key没找到。</summary>
    NotFound = byte.MaxValue - 2,

    /// <summary>访问越界了。索引或Key非法。</summary>
    OutOfRange = byte.MaxValue - 1,

    /// <summary>用于承载值的变量类型本身就不受支持。</summary>
    UnsupportedType = byte.MaxValue,
}
