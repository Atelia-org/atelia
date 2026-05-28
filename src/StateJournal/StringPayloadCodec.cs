using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Atelia.StateJournal;

/// <summary>
/// 值语义 string 的内部 payload codec。
/// 公开 facade 已切换为裸 <see cref="string"/>；这里仅保留最底层的 bare 编解码实现。
/// </summary>
internal static class StringPayloadCodec {
    /// <summary>纯粹为了提高可测试性才抽象出来的。</summary>
    internal interface IFastPathStrategy {
        static abstract bool IsFastPath { get; }
    }

    private readonly struct FastPathOnLe : IFastPathStrategy {
        public static bool IsFastPath => BitConverter.IsLittleEndian;
    }

    private const uint Utf8FlagMask = 1;

    /// <summary>
    /// 写入格式：VarUInt header，后跟 payload 字节。
    /// header LSB=0 → UTF-16LE，header 本身同时就是 payloadByteCount。
    /// header LSB=1 → UTF-8，payloadByteCount = header &gt;&gt; 1。
    /// 自动选择更短的编码。
    /// </summary>
    internal static void WriteTo(IBufferWriter<byte> downstream, string value) => WriteTo<FastPathOnLe>(downstream, value);
    internal static void WriteTo<FastPathStrategy>(IBufferWriter<byte> downstream, string value) where FastPathStrategy : unmanaged, IFastPathStrategy {
        if (value.Length == 0) {
            Serialization.VarInt.WriteUInt32(downstream, 0);
            return;
        }

        int utf16Bytes = value.Length * 2;
        int utf8Bytes = Encoding.UTF8.GetByteCount(value);

        if (utf8Bytes < utf16Bytes) {
            Serialization.VarInt.WriteUInt32(downstream, EncodeUtf8Header(utf8Bytes));
            var span = downstream.GetSpan(utf8Bytes);
            int written = Encoding.UTF8.GetBytes(value, span);
            Debug.Assert(written == utf8Bytes);
            downstream.Advance(utf8Bytes);
        }
        else {
            Serialization.VarInt.WriteUInt32(downstream, EncodeUtf16LeHeader(value.Length));
            WriteUtf16Le<FastPathStrategy>(downstream, value, utf16Bytes);
            downstream.Advance(utf16Bytes);
        }
    }

    /// <summary>
    /// 可空 string 的 bare 写入。
    /// 头部规则：<c>0</c> 表示 null；非 null 时把非空/空字符串原本的 header 整体加一。
    /// </summary>
    internal static void WriteNullableTo(IBufferWriter<byte> downstream, string? value) => WriteNullableTo<FastPathOnLe>(downstream, value);
    internal static void WriteNullableTo<FastPathStrategy>(IBufferWriter<byte> downstream, string? value) where FastPathStrategy : unmanaged, IFastPathStrategy {
        if (value is null) {
            Serialization.VarInt.WriteUInt32(downstream, Serialization.NullablePayloadHeader.EncodeNull());
            return;
        }

        if (value.Length == 0) {
            Serialization.VarInt.WriteUInt32(downstream, Serialization.NullablePayloadHeader.EncodePresent(0));
            return;
        }

        int utf16Bytes = value.Length * 2;
        int utf8Bytes = Encoding.UTF8.GetByteCount(value);

        if (utf8Bytes < utf16Bytes) {
            uint rawHeader = EncodeUtf8Header(utf8Bytes);
            Serialization.VarInt.WriteUInt32(downstream, Serialization.NullablePayloadHeader.EncodePresent(rawHeader));
            var span = downstream.GetSpan(utf8Bytes);
            int written = Encoding.UTF8.GetBytes(value, span);
            Debug.Assert(written == utf8Bytes);
            downstream.Advance(utf8Bytes);
        }
        else {
            uint rawHeader = EncodeUtf16LeHeader(value.Length);
            Serialization.VarInt.WriteUInt32(downstream, Serialization.NullablePayloadHeader.EncodePresent(rawHeader));
            WriteUtf16Le<FastPathStrategy>(downstream, value, utf16Bytes);
            downstream.Advance(utf16Bytes);
        }
    }

    internal static string ReadFrom(ref Serialization.BinaryDiffReader reader) => ReadFrom<FastPathOnLe>(ref reader);
    internal static string ReadFrom<FastPathStrategy>(ref Serialization.BinaryDiffReader reader) where FastPathStrategy : unmanaged, IFastPathStrategy {
        uint rawHeader = reader.BareUInt32(asKey: false);
        return ReadFromRawHeader<FastPathStrategy>(ref reader, rawHeader);
    }

    internal static string? ReadNullableFrom(ref Serialization.BinaryDiffReader reader) => ReadNullableFrom<FastPathOnLe>(ref reader);
    internal static string? ReadNullableFrom<FastPathStrategy>(ref Serialization.BinaryDiffReader reader) where FastPathStrategy : unmanaged, IFastPathStrategy {
        uint encodedHeader = reader.BareUInt32(asKey: false);
        if (!Serialization.NullablePayloadHeader.TryDecode(encodedHeader, out uint rawHeader)) { return null; }
        return ReadFromRawHeader<FastPathStrategy>(ref reader, rawHeader);
    }

    private static string ReadFromRawHeader<FastPathStrategy>(ref Serialization.BinaryDiffReader reader, uint rawHeader) where FastPathStrategy : unmanaged, IFastPathStrategy {
        bool isUtf8 = DecodeIsUtf8AndByteCount(rawHeader, out int payloadByteCount);
        if (payloadByteCount == 0) { return string.Empty; }

        var payload = reader.ReadSpan(payloadByteCount);
        return isUtf8
            ? Encoding.UTF8.GetString(payload)
            : ReadUtf16Le<FastPathStrategy>(payload);
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
}
