// Source: Atelia.StateJournal - VarInt 编解码
// Spec: atelia/docs/StateJournal/mvp-design-v2.md §3.2.0.1

namespace Atelia.StateJournal;

/// <summary>
/// VarInt 编解码工具类（protobuf 风格 base-128 / ULEB128）。
/// </summary>
/// <remarks>
/// <para><b>varuint</b>：无符号 base-128，每字节低 7 bit 为数据，高 1 bit 为 continuation（1=后续有字节）。</para>
/// <para><b>varint</b>：有符号整数采用 ZigZag 映射后按 varuint 编码。</para>
/// <para>对应条款：<c>[F-VARINT-CANONICAL-ENCODING]</c>、<c>[F-DECODE-ERROR-FAILFAST]</c></para>
/// </remarks>
public static class VarInt
{
    /// <summary>
    /// varuint64 编码的最大字节数（uint64.MaxValue 需要 10 字节）。
    /// </summary>
    public const int MaxVarUInt64Bytes = 10;

    // ==========================================================================
    // Unsigned VarInt
    // ==========================================================================

    /// <summary>
    /// 计算 varuint 编码长度（canonical 最短编码）。
    /// </summary>
    /// <param name="value">要编码的无符号整数值。</param>
    /// <returns>编码所需的字节数（1-10）。</returns>
    public static int GetVarUIntLength(ulong value)
    {
        // 每 7 bit 需要 1 字节
        int length = 1;
        while (value >= 0x80)
        {
            value >>= 7;
            length++;
        }
        return length;
    }

    /// <summary>
    /// 写入 varuint（无符号 base-128 编码）。
    /// </summary>
    /// <param name="destination">目标缓冲区。</param>
    /// <param name="value">要编码的无符号整数值。</param>
    /// <returns>写入的字节数。</returns>
    /// <exception cref="ArgumentException">目标缓冲区太小。</exception>
    /// <remarks>
    /// <para><b>[F-VARINT-CANONICAL-ENCODING]</b>：保证产生 canonical 最短编码。</para>
    /// </remarks>
    public static int WriteVarUInt(Span<byte> destination, ulong value)
    {
        int length = GetVarUIntLength(value);
        if (destination.Length < length)
        {
            throw new ArgumentException(
                $"Destination buffer too small. Need {length} bytes but only {destination.Length} available.",
                nameof(destination));
        }

        int offset = 0;
        while (value >= 0x80)
        {
            // 低 7 bit + continuation flag
            destination[offset++] = (byte)(value | 0x80);
            value >>= 7;
        }
        // 最后一个字节没有 continuation flag
        destination[offset++] = (byte)value;

        return offset;
    }

    /// <summary>
    /// 尝试读取 varuint（无符号 base-128 编码）。
    /// </summary>
    /// <param name="source">源缓冲区。</param>
    /// <returns>
    /// 成功时返回 (Value, BytesConsumed) 元组；
    /// 失败时返回 <see cref="VarIntDecodeError"/> 或 <see cref="VarIntNonCanonicalError"/>。
    /// </returns>
    /// <remarks>
    /// <para><b>[F-DECODE-ERROR-FAILFAST]</b>：遇到 EOF、溢出或非 canonical 一律失败。</para>
    /// <para><b>[F-VARINT-CANONICAL-ENCODING]</b>：拒绝非 canonical 编码（如 0x80 0x00 表示 0）。</para>
    /// </remarks>
    public static AteliaResult<(ulong Value, int BytesConsumed)> TryReadVarUInt(ReadOnlySpan<byte> source)
    {
        if (source.IsEmpty)
        {
            return AteliaResult<(ulong, int)>.Failure(
                new VarIntDecodeError("Unexpected EOF: empty buffer when reading varuint."));
        }

        ulong result = 0;
        int shift = 0;
        int bytesConsumed = 0;

        while (bytesConsumed < source.Length)
        {
            byte b = source[bytesConsumed];
            bytesConsumed++;

            // 检查溢出：varuint64 最多 10 字节
            if (bytesConsumed > MaxVarUInt64Bytes)
            {
                return AteliaResult<(ulong, int)>.Failure(
                    new VarIntDecodeError(
                        $"VarUInt overflow: more than {MaxVarUInt64Bytes} bytes.",
                        "The encoded value exceeds uint64 range."));
            }

            // 第 10 字节特殊处理：只能有低 1 bit 有效（0x00 或 0x01）
            if (bytesConsumed == MaxVarUInt64Bytes && b > 0x01)
            {
                return AteliaResult<(ulong, int)>.Failure(
                    new VarIntDecodeError(
                        $"VarUInt overflow: 10th byte value 0x{b:X2} exceeds allowed range.",
                        "The encoded value exceeds uint64 range."));
            }

            // 累加当前字节的 7 bit 数据
            result |= ((ulong)(b & 0x7F)) << shift;
            shift += 7;

            // 检查 continuation flag
            if ((b & 0x80) == 0)
            {
                // 检查 canonical 编码
                int expectedLength = GetVarUIntLength(result);
                if (bytesConsumed != expectedLength)
                {
                    return AteliaResult<(ulong, int)>.Failure(
                        new VarIntNonCanonicalError(result, bytesConsumed, expectedLength));
                }

                return AteliaResult<(ulong, int)>.Success((result, bytesConsumed));
            }
        }

        // 到达缓冲区末尾但 continuation flag 仍为 1（EOF）
        return AteliaResult<(ulong, int)>.Failure(
            new VarIntDecodeError(
                $"Unexpected EOF: continuation flag set at byte {bytesConsumed} but no more data.",
                "The varuint encoding is truncated."));
    }

    // ==========================================================================
    // ZigZag Encoding (Signed → Unsigned Mapping)
    // ==========================================================================

    /// <summary>
    /// ZigZag 编码：将有符号整数映射为无符号整数。
    /// </summary>
    /// <param name="value">有符号整数。</param>
    /// <returns>ZigZag 编码后的无符号整数。</returns>
    /// <remarks>
    /// <para>映射规则：<c>zz = (n &lt;&lt; 1) ^ (n &gt;&gt; 63)</c></para>
    /// <para>示例：0→0, -1→1, 1→2, -2→3, ...</para>
    /// </remarks>
    public static ulong ZigZagEncode(long value)
    {
        // 算术右移 63 位得到符号扩展：正数→0x0000...，负数→0xFFFF...
        // 然后与左移 1 位的结果异或
        return (ulong)((value << 1) ^ (value >> 63));
    }

    /// <summary>
    /// ZigZag 解码：将无符号整数映射回有符号整数。
    /// </summary>
    /// <param name="encoded">ZigZag 编码的无符号整数。</param>
    /// <returns>解码后的有符号整数。</returns>
    /// <remarks>
    /// <para>映射规则：<c>n = (zz &gt;&gt;&gt; 1) ^ -(zz &amp; 1)</c></para>
    /// <para>示例：0→0, 1→-1, 2→1, 3→-2, ...</para>
    /// </remarks>
    public static long ZigZagDecode(ulong encoded)
    {
        // 逻辑右移 1 位，然后与符号位扩展异或
        // -(encoded & 1) 等于：偶数→0，奇数→-1（即 0xFFFF...）
        return (long)(encoded >> 1) ^ -((long)(encoded & 1));
    }

    // ==========================================================================
    // Signed VarInt (ZigZag + VarUInt)
    // ==========================================================================

    /// <summary>
    /// 写入 varint（有符号，ZigZag + base-128 编码）。
    /// </summary>
    /// <param name="destination">目标缓冲区。</param>
    /// <param name="value">要编码的有符号整数值。</param>
    /// <returns>写入的字节数。</returns>
    /// <exception cref="ArgumentException">目标缓冲区太小。</exception>
    public static int WriteVarInt(Span<byte> destination, long value)
    {
        ulong zigzag = ZigZagEncode(value);
        return WriteVarUInt(destination, zigzag);
    }

    /// <summary>
    /// 尝试读取 varint（有符号，ZigZag + base-128 编码）。
    /// </summary>
    /// <param name="source">源缓冲区。</param>
    /// <returns>
    /// 成功时返回 (Value, BytesConsumed) 元组；
    /// 失败时返回 <see cref="VarIntDecodeError"/> 或 <see cref="VarIntNonCanonicalError"/>。
    /// </returns>
    public static AteliaResult<(long Value, int BytesConsumed)> TryReadVarInt(ReadOnlySpan<byte> source)
    {
        var result = TryReadVarUInt(source);
        if (result.IsFailure)
        {
            return AteliaResult<(long, int)>.Failure(result.Error!);
        }

        long value = ZigZagDecode(result.Value.Value);
        return AteliaResult<(long, int)>.Success((value, result.Value.BytesConsumed));
    }
}
