using System.Buffers;
using System.Numerics;

namespace Atelia.StateJournal.Serialization;

internal static class VarInt {
    #region Base128
    internal const int MaxLength64 = 10; // (64+6) / 7
    internal const int MaxLength32 = 5; // (32+6) / 7
    internal const int MaxLength16 = 3; // (16+6) / 7
    /// <summary>计算 varuint 编码长度（canonical 最短编码）。</summary>
    /// <param name="value">要编码的无符号整数值。</param>
    /// <returns>编码所需的字节数（1-10）。</returns>
    internal static int GetCodewordLength(ulong value) {
        // 每 7 bit 需要 1 字节
        // return (6 + 64 - BitOperations.LeadingZeroCount(value | 1)) / 7;
        return (6 + 64 - BitOperations.LeadingZeroCount(value | 1)) * 37 >> 8;
    }

    /// <summary>写入 varuint（无符号 base-128 编码）。</summary>
    /// <returns>写入的字节数。</returns>
    /// <exception cref="ArgumentException">目标缓冲区太小。</exception>
    /// <remarks></remarks>
    internal static int WriteUInt64(IBufferWriter<byte> writer, ulong value) {
        var destSpan = writer.GetSpan(MaxLength64);
        int offset = 0;
        while (value >= 0x80) {
            // 低 7 bit + continuation flag
            destSpan[offset++] = (byte)(value | 0x80);
            value >>= 7;
        }
        // 最后一个字节没有 continuation flag
        destSpan[offset++] = (byte)value;
        writer.Advance(offset);
        return offset;
    }

    /// <summary>写入 varuint（无符号 base-128 编码）。</summary>
    /// <returns>写入的字节数。</returns>
    /// <exception cref="ArgumentException">目标缓冲区太小。</exception>
    /// <remarks></remarks>
    internal static int WriteUInt32(IBufferWriter<byte> writer, uint value) {
        var destSpan = writer.GetSpan(MaxLength32);
        int offset = 0;
        while (value >= 0x80) {
            // 低 7 bit + continuation flag
            destSpan[offset++] = (byte)(value | 0x80);
            value >>= 7;
        }
        // 最后一个字节没有 continuation flag
        destSpan[offset++] = (byte)value;
        writer.Advance(offset);
        return offset;
    }

    /// <summary>写入 varuint（无符号 base-128 编码）。</summary>
    /// <returns>写入的字节数。</returns>
    /// <exception cref="ArgumentException">目标缓冲区太小。</exception>
    /// <remarks></remarks>
    internal static int WriteUInt16(IBufferWriter<byte> writer, ushort value) {
        var destSpan = writer.GetSpan(MaxLength16);
        int offset = 0;
        while (value >= 0x80) {
            // 低 7 bit + continuation flag
            destSpan[offset++] = (byte)(value | 0x80);
            value >>= 7;
        }
        // 最后一个字节没有 continuation flag
        destSpan[offset++] = (byte)value;
        writer.Advance(offset);
        return offset;
    }

    internal enum ErrorCode {
        None = 0,
        UnexpectedEof,
        Overflow,
        NonCanonicalEncoding,
    }

    internal static int ReadUInt64(ReadOnlySpan<byte> source, out ulong value) {
        static int Err(ErrorCode code) => -(int)code;
        static int Finish(out ulong ret, ulong result, int readed) {
            ret = result;
            return readed;
        }
        value = default;

        if (source.IsEmpty) { return Err(ErrorCode.UnexpectedEof); }
        byte data = source[0];
        ulong result = data & 0x7Fu;
        if ((data & 0x80) == 0) { return Finish(out value, result, 1); }

        for (int i = 1; i < MaxLength64-1; ++i) {
            if (source.Length <= i) { return Err(ErrorCode.UnexpectedEof); }
            data = source[i];
            result |= (ulong)(data & 0x7F) << (7 * i);
            if (data <= 127) { return (data == 0) ? Err(ErrorCode.NonCanonicalEncoding) : Finish(out value, result, 1 + i); }
        }

        if (source.Length <= MaxLength64-1) { return Err(ErrorCode.UnexpectedEof); }
        data = source[MaxLength64-1];
        result |= (ulong)data << 7*(MaxLength64-1);
        return (data > 0x01) ? Err(ErrorCode.Overflow) : Finish(out value, result, MaxLength64);
    }

    internal static int ReadUInt32(ReadOnlySpan<byte> source, out uint value) {
        static int Err(ErrorCode code) => -(int)code;
        static int Finish(out uint ret, uint result, int readed) {
            ret = result;
            return readed;
        }
        value = default;

        if (source.IsEmpty) { return Err(ErrorCode.UnexpectedEof); }
        byte data = source[0];
        uint result = data & 0x7Fu;
        if ((data & 0x80) == 0) { return Finish(out value, result, 1); }

        for (int i = 1; i < MaxLength32-1; ++i) {
            if (source.Length <= i) { return Err(ErrorCode.UnexpectedEof); }
            data = source[i];
            result |= (uint)(data & 0x7F) << (7 * i);
            if (data <= 127) { return (data == 0) ? Err(ErrorCode.NonCanonicalEncoding) : Finish(out value, result, 1 + i); }
        }

        if (source.Length <= MaxLength32-1) { return Err(ErrorCode.UnexpectedEof); }
        data = source[MaxLength32-1];
        result |= (uint)data << 7*(MaxLength32-1);
        return (data > 0x0F) ? Err(ErrorCode.Overflow) : Finish(out value, result, MaxLength32);
    }

    internal static int ReadUInt16(ReadOnlySpan<byte> source, out ushort value) {
        static int Err(ErrorCode code) => -(int)code;
        static int Finish(out ushort ret, ushort result, int readed) {
            ret = result;
            return readed;
        }
        value = default;

        if (source.IsEmpty) { return Err(ErrorCode.UnexpectedEof); }
        byte data = source[0];
        ushort result = (ushort)(data & 0x7Fu);
        if ((data & 0x80) == 0) { return Finish(out value, result, 1); }

        for (int i = 1; i < MaxLength16-1; ++i) {
            if (source.Length <= i) { return Err(ErrorCode.UnexpectedEof); }
            data = source[i];
            result |= (ushort)((data & 0x7F) << (7 * i));
            if (data <= 127) { return (data == 0) ? Err(ErrorCode.NonCanonicalEncoding) : Finish(out value, result, 1 + i); }
        }

        if (source.Length <= MaxLength16-1) { return Err(ErrorCode.UnexpectedEof); }
        data = source[MaxLength16-1];
        result |= (ushort)(data << 7*(MaxLength16-1));
        return (data > 0x03) ? Err(ErrorCode.Overflow) : Finish(out value, result, MaxLength16);
    }

    #endregion

    #region ZigZag Signed → Unsigned Mapping

    /// <summary>ZigZag 编码：将有符号整数映射为无符号整数。</summary>
    /// <param name="value">有符号整数。</param>
    /// <returns>ZigZag 编码后的无符号整数。</returns>
    /// <remarks>
    /// 映射规则：<c>zz = (n &lt;&lt; 1) ^ (n &gt;&gt; 63)</c>
    /// 示例：0→0, -1→1, 1→2, -2→3, ...
    /// </remarks>
    internal static ulong ZigZagEncode64(long value) {
        // 算术右移 63 位得到符号扩展：正数→0x0000...，负数→0xFFFF...
        // 然后与左移 1 位的结果异或
        return (ulong)((value << 1) ^ (value >> 63));
    }

    internal static uint ZigZagEncode32(int value) {
        return (uint)((value << 1) ^ (value >> 31));
    }

    internal static ushort ZigZagEncode16(short value) {
        int signed = value;
        return (ushort)((signed << 1) ^ (signed >> 15));
    }

    /// <summary>ZigZag 解码：将无符号整数映射回有符号整数。</summary>
    /// <param name="encoded">ZigZag 编码的无符号整数。</param>
    /// <returns>解码后的有符号整数。</returns>
    /// <remarks>
    /// 映射规则：<c>n = (zz &gt;&gt;&gt; 1) ^ -(zz &amp; 1)</c>
    /// 示例：0→0, 1→-1, 2→1, 3→-2, ...
    /// </remarks>
    internal static long ZigZagDecode64(ulong encoded) {
        // 逻辑右移 1 位，然后与符号位扩展异或
        // -(encoded & 1) 等于：偶数→0，奇数→-1（即 0xFFFF...）
        return (long)(encoded >> 1) ^ -(long)(encoded & 1);
    }

    internal static int ZigZagDecode32(uint encoded) {
        return (int)(encoded >> 1) ^ -(int)(encoded & 1);
    }

    internal static short ZigZagDecode16(ushort encoded) {
        return (short)((encoded >> 1) ^ -(encoded & 1));
    }

    #endregion

    #region Signed
    internal static int WriteInt64(IBufferWriter<byte> writer, long value) => WriteUInt64(writer, ZigZagEncode64(value));
    internal static int WriteInt32(IBufferWriter<byte> writer, int value) => WriteUInt32(writer, ZigZagEncode32(value));
    internal static int WriteInt16(IBufferWriter<byte> writer, short value) => WriteUInt16(writer, ZigZagEncode16(value));

    internal static int ReadInt64(ReadOnlySpan<byte> source, out long value) {
        int ret = ReadUInt64(source, out ulong unsigned);
        value = ZigZagDecode64(unsigned);
        return ret;
    }

    internal static int ReadInt32(ReadOnlySpan<byte> source, out int value) {
        int ret = ReadUInt32(source, out uint unsigned);
        value = ZigZagDecode32(unsigned);
        return ret;
    }

    internal static int ReadInt16(ReadOnlySpan<byte> source, out short value) {
        int ret = ReadUInt16(source, out ushort unsigned);
        value = ZigZagDecode16(unsigned);
        return ret;
    }
    #endregion
}
