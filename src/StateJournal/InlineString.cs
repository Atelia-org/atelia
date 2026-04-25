using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Atelia.StateJournal;

/// <summary>
/// 值语义的字符串包装，可作为 typed durable 容器的 key 或 value。
/// 序列化时直接写入字符串内容（UTF-8 或 UTF-16LE，由 LSB encoding flag 自动选择最短表示）。
/// </summary>
/// <remarks>
/// 与 symbol-backed 的 <see cref="string"/> 相对应。
/// <see cref="InlineString"/> 总是直接内联持久化 payload，不经过 per-revision symbol table。
/// </remarks>
public readonly struct InlineString : IEquatable<InlineString> {
    /// <summary>纯粹为了提高可测试性才抽象出来的。</summary>
    internal interface IFastPathStrategy {
        static abstract bool IsFastPath { get; }
    }

    private readonly struct FastPathOnLe : IFastPathStrategy {
        public static bool IsFastPath => BitConverter.IsLittleEndian;
    }

    private const uint Utf8FlagMask = 1;
    private readonly string _value;

    public InlineString(string? value) {
        _value = value ?? string.Empty;
    }

    public string Value => _value;

    public bool Equals(InlineString other) => string.Equals(_value, other._value, StringComparison.Ordinal);
    public override bool Equals(object? obj) => obj is InlineString other && Equals(other);
    public override int GetHashCode() => string.GetHashCode(_value, StringComparison.Ordinal);
    public override string ToString() => _value;

    public static bool operator ==(InlineString left, InlineString right) => left.Equals(right);
    public static bool operator !=(InlineString left, InlineString right) => !left.Equals(right);

    public static implicit operator InlineString(string? value) => new(value);
    public static implicit operator string(InlineString s) => s._value;

    #region Serialization

    /// <summary>
    /// 写入格式：VarUInt header，后跟 payload 字节。
    /// header LSB=0 → UTF-16LE，header 本身同时就是 payloadByteCount。
    /// header LSB=1 → UTF-8，payloadByteCount = header &gt;&gt; 1。
    /// 自动选择更短的编码。
    /// </summary>
    internal static void WriteTo(IBufferWriter<byte> downstream, string value) => WriteTo<FastPathOnLe>(downstream, value);
    internal static void WriteTo<FastPathStrategy>(IBufferWriter<byte> downstream, string value) where FastPathStrategy : unmanaged, IFastPathStrategy {
        if (value.Length == 0) {
            // 空字符串：header = 0（payloadByteCount=0, flag=0）
            Serialization.VarInt.WriteUInt32(downstream, 0);
            return;
        }

        int utf16Bytes = value.Length * 2;
        int utf8Bytes = Encoding.UTF8.GetByteCount(value);

        if (utf8Bytes < utf16Bytes) {
            // UTF-8 更短 → LSB = 1, 高位编码 UTF-8 byte/codeUnit count
            Serialization.VarInt.WriteUInt32(downstream, EncodeUtf8Header(utf8Bytes));
            var span = downstream.GetSpan(utf8Bytes);
            int written = Encoding.UTF8.GetBytes(value, span);
            Debug.Assert(written == utf8Bytes);
            downstream.Advance(utf8Bytes);
        }
        else {
            // UTF-16LE 更短或相同 → LSB = 0, header 本身就是 UTF-16LE byte count, 兼容高位编码codeUnit语义。
            Serialization.VarInt.WriteUInt32(downstream, EncodeUtf16LeHeader(value.Length));
            WriteUtf16Le<FastPathStrategy>(downstream, value, utf16Bytes);
            downstream.Advance(utf16Bytes);
        }
    }

    /// <summary>
    /// 从 <see cref="Serialization.BinaryDiffReader"/> 中读取一个 InlineString。
    /// </summary>
    internal static string ReadFrom(ref Serialization.BinaryDiffReader reader) => ReadFrom<FastPathOnLe>(ref reader);
    internal static string ReadFrom<FastPathStrategy>(ref Serialization.BinaryDiffReader reader) where FastPathStrategy : unmanaged, IFastPathStrategy {
        uint header = reader.BareUInt32(asKey: false);
        bool isUtf8 = DecodeIsUtf8AndByteCount(header, out int payloadByteCount);

        if (payloadByteCount == 0) { return string.Empty; }

        var payload = reader.ReadSpan(payloadByteCount);

        return isUtf8
            ? Encoding.UTF8.GetString(payload) // UTF-8 → standard decode
            : ReadUtf16Le<FastPathStrategy>(payload); // UTF-16LE → 小端机快路径，否则兼容路径
    }

    private static void WriteUtf16Le<TEndianness>(IBufferWriter<byte> downstream, string value, int utf16Bytes) where TEndianness : IFastPathStrategy {
        if (TEndianness.IsFastPath) {
            Debug.Assert(BitConverter.IsLittleEndian);
            MemoryMarshal.AsBytes(value.AsSpan()).CopyTo(downstream.GetSpan(utf16Bytes));
            return;
        }

        int written = Encoding.Unicode.GetBytes(value.AsSpan(), downstream.GetSpan(utf16Bytes));
        Debug.Assert(written == utf16Bytes);
    }

    private static string ReadUtf16Le<TEndianness>(ReadOnlySpan<byte> payload) where TEndianness : IFastPathStrategy {
        if (TEndianness.IsFastPath) {
            Debug.Assert(BitConverter.IsLittleEndian);
            return new string(MemoryMarshal.Cast<byte, char>(payload));
        }
        return Encoding.Unicode.GetString(payload);
    }

    private static uint EncodeUtf16LeHeader(int codeUnitCount) {
        Debug.Assert(codeUnitCount >= 0);
        return (uint)codeUnitCount << 1;
    }

    private static uint EncodeUtf8Header(int utf8ByteCount) {
        Debug.Assert(utf8ByteCount >= 0);
        return ((uint)utf8ByteCount << 1) | Utf8FlagMask;
    }

    private static bool DecodeIsUtf8AndByteCount(uint header, out int byteCount) {
        bool isUtf8 = (header & Utf8FlagMask) != 0;
        byteCount = checked((int)(isUtf8 ? (header >> 1) : header));
        return isUtf8;
    }

    #endregion
}
