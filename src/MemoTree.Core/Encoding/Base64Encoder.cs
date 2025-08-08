using System;
using System.Text;

namespace MemoTree.Core.Encoding
{
    /// <summary>
    /// 标准Base64编码器
    /// </summary>
    public sealed class Base64Encoder : EncoderBase
    {
        public override float BitsPerChar => 6.0f;

        public override string ModeName => "base64";

        public override string EncodeBytes(byte[] data)
        {
            if (data == null || data.Length == 0) return string.Empty;
            return Convert.ToBase64String(data);
        }

        public override byte[] DecodeString(string encoded)
        {
            if (string.IsNullOrEmpty(encoded)) return Array.Empty<byte>();
            return Convert.FromBase64String(encoded);
        }

        // ---- UUID特化：去除固定补零尾部，解码时自动补回（保持向后兼容） ----
        public override string EncodeUuid(Guid? guid = null)
        {
            var uuid = guid ?? Guid.NewGuid();
            var full = EncodeBytes(uuid.ToByteArray());
            // 16字节 -> 128位 -> Base64固定'=='结尾
            return full.EndsWith("==", StringComparison.Ordinal) ? full[..^2] : full;
        }

        public override Guid DecodeUuid(string encoded)
        {
            if (string.IsNullOrEmpty(encoded))
                throw new ArgumentException("空编码无法解码为UUID", nameof(encoded));

            int expectedDataLen = (int)Math.Ceiling(128.0 / 6.0); // 22
            string encodedFull = encoded.Length == expectedDataLen
                ? encoded + "=="
                : encoded;

            var bytes = DecodeString(encodedFull);
            if (bytes.Length != 16)
                throw new ArgumentException($"解码结果长度错误: {bytes.Length} bytes，期望16 bytes", nameof(encoded));
            return new Guid(bytes);
        }
    }
}
