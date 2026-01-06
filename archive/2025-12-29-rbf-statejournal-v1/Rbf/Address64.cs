namespace Atelia.Rbf;

/// <summary>
/// RBF 帧地址（8 字节文件偏移）。
/// </summary>
/// <remarks>
/// <para><b>[F-<deleted-place-holder>-DEFINITION]</b>: <deleted-place-holder> 是 8 字节 LE 编码的文件偏移量，
/// 指向一个 Frame 的起始位置（HeadLen 字段起点）。</para>
/// <para><b>[F-<deleted-place-holder>-ALIGNMENT]</b>: 有效 <deleted-place-holder> MUST 4 字节对齐（Value % 4 == 0）。</para>
/// <para><b>[F-<deleted-place-holder>-NULL]</b>: Value == 0 表示 null（无效地址）。</para>
/// </remarks>
/// <param name="Value">文件偏移量。</param>
public readonly record struct <deleted-place-holder>(ulong Value) {
    /// <summary>
    /// 空地址（表示无效/不存在）。
    /// </summary>
    public static readonly <deleted-place-holder> Null = new(0);

    /// <summary>
    /// 检查地址是否为空。
    /// </summary>
    public bool IsNull => Value == 0;

    /// <summary>
    /// 检查地址是否有效（非空且 4 字节对齐）。
    /// </summary>
    /// <remarks>
    /// <para><b>[F-<deleted-place-holder>-ALIGNMENT]</b>: 有效地址 MUST 4 字节对齐。</para>
    /// </remarks>
    public bool IsValid => !IsNull && (Value % 4 == 0);

    /// <summary>
    /// 隐式转换为 ulong。
    /// </summary>
    public static implicit operator ulong(<deleted-place-holder> addr) => addr.Value;

    /// <summary>
    /// 显式从 ulong 转换。
    /// </summary>
    public static explicit operator <deleted-place-holder>(ulong value) => new(value);

    /// <summary>
    /// 从 long 创建 <deleted-place-holder>（方便与文件偏移交互）。
    /// </summary>
    /// <param name="offset">文件偏移（必须非负）。</param>
    /// <returns><deleted-place-holder> 实例。</returns>
    /// <exception cref="ArgumentOutOfRangeException">offset 为负数时抛出。</exception>
    public static <deleted-place-holder> FromOffset(long offset) {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        return new <deleted-place-holder>((ulong)offset);
    }
}
