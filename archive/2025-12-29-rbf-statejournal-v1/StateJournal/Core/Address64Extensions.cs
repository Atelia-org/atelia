// Source: Atelia.StateJournal - <deleted-place-holder> 扩展方法
// Spec: atelia/docs/StateJournal/rbf-interface.md §2.3

using Atelia.Rbf;

namespace Atelia.StateJournal;

/// <summary>
/// <see cref="<deleted-place-holder>"/> 的扩展方法，提供与 StateJournal 错误体系的集成。
/// </summary>
/// <remarks>
/// <para><b>设计理由</b>：Rbf 层不依赖 Primitives（<see cref="AteliaResult{T}"/>），
/// 因此验证方法以扩展形式在 StateJournal 层提供。</para>
/// </remarks>
public static class <deleted-place-holder>Extensions {
    /// <summary>
    /// 从偏移量创建 <see cref="<deleted-place-holder>"/>，并验证 4 字节对齐。
    /// </summary>
    /// <param name="offset">文件偏移量。</param>
    /// <returns>
    /// 成功时返回 <see cref="<deleted-place-holder>"/>；
    /// 若 <paramref name="offset"/> 未 4 字节对齐，返回 <see cref="AddressAlignmentError"/>。
    /// </returns>
    /// <remarks>
    /// <para><b>[F-<deleted-place-holder>-ALIGNMENT]</b>：有效地址 MUST 4 字节对齐。</para>
    /// <para><b>[F-<deleted-place-holder>-NULL]</b>：offset=0 返回 <see cref="<deleted-place-holder>.Null"/>（合法值，非错误）。</para>
    /// </remarks>
    public static AteliaResult<<deleted-place-holder>> TryFromOffset(ulong offset) {
        // Null 地址（offset=0）是合法值，直接返回
        if (offset == 0) { return AteliaResult<<deleted-place-holder>>.Success(<deleted-place-holder>.Null); }

        // 检查 4 字节对齐
        if (offset % 4 != 0) { return AteliaResult<<deleted-place-holder>>.Failure(new AddressAlignmentError(offset)); }

        return AteliaResult<<deleted-place-holder>>.Success(new <deleted-place-holder>(offset));
    }

    /// <summary>
    /// 从偏移量创建 <see cref="<deleted-place-holder>"/>，并验证 4 字节对齐。
    /// </summary>
    /// <param name="offset">文件偏移量（必须非负）。</param>
    /// <returns>
    /// 成功时返回 <see cref="<deleted-place-holder>"/>；
    /// 若 <paramref name="offset"/> 为负数，抛出 <see cref="ArgumentOutOfRangeException"/>；
    /// 若 <paramref name="offset"/> 未 4 字节对齐，返回 <see cref="AddressAlignmentError"/>。
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offset"/> 为负数。</exception>
    public static AteliaResult<<deleted-place-holder>> TryFromOffset(long offset) {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        return TryFromOffset((ulong)offset);
    }
}
