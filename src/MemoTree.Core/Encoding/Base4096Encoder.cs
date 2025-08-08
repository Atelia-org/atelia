using System;
using System.Text;

namespace MemoTree.Core.Encoding
{
    /// <summary>
    /// Base4096编码器：每12位对应一个字符，类似Base64的补零处理
    /// </summary>
    public sealed class Base4096Encoder : CustomCharsetEncoderBase
    {
        public Base4096Encoder(string? charset = null)
            : base(charset ?? DefaultCharsets.DefaultCharset4096)
        {
            ValidateCharsetSize(4096, nameof(Base4096Encoder));
        }

        public override float BitsPerChar => 12.0f;

        public override string ModeName => "base4096";

        public override string EncodeBytes(byte[] data)
        {
            if (data == null || data.Length == 0)
                return string.Empty;

            // 将字节转换为位串
            var sbBits = new StringBuilder(data.Length * 8);
            foreach (var b in data)
                sbBits.Append(Convert.ToString(b, 2).PadLeft(8, '0'));

            // 计算需要补多少个零位到12的倍数
            int paddingBits = (12 - (sbBits.Length % 12)) % 12;

            // 补齐到12的倍数
            if (paddingBits > 0)
                sbBits.Append('0', paddingBits);

            // 每12位转换为一个字符
            var result = new StringBuilder(sbBits.Length / 12 + 1);
            for (int i = 0; i < sbBits.Length; i += 12)
            {
                var chunk = sbBits.ToString(i, 12);
                int value = Convert.ToInt32(chunk, 2);
                result.Append(Charset[value]);
            }

            if (result.Length == 0)
                return string.Empty;

            // 在末尾添加补零信息字符：paddingBits ∈ {0,4} => code ∈ {0,1}
            int paddingCode = paddingBits / 4;
            result.Append(Charset[paddingCode]);

            return result.ToString();
        }

        public override byte[] DecodeString(string encoded)
        {
            if (string.IsNullOrEmpty(encoded))
                return Array.Empty<byte>();

            if (encoded.Length < 1)
                throw new ArgumentException("Base4096编码至少需要1个字符", nameof(encoded));

            // 最后一个字符是补零信息
            char paddingChar = encoded[^1];
            var dataChars = encoded.Substring(0, encoded.Length - 1);

            if (!CharToIndex.TryGetValue(paddingChar, out int paddingCode))
                throw new ArgumentException($"无效的补零字符: {paddingChar}", nameof(encoded));

            int paddingBits = paddingCode * 4;

            var sbBits = new StringBuilder(dataChars.Length * 12);
            foreach (var ch in dataChars)
            {
                if (!CharToIndex.TryGetValue(ch, out int val))
                    throw new ArgumentException($"无效字符: {ch}", nameof(encoded));
                sbBits.Append(Convert.ToString(val, 2).PadLeft(12, '0'));
            }

            // 移除末尾的补零位
            if (paddingBits > 0 && sbBits.Length >= paddingBits)
                sbBits.Length -= paddingBits;

            // 确保位数是8的倍数
            if (sbBits.Length % 8 != 0)
                throw new ArgumentException($"解码后的位数不是8的倍数: {sbBits.Length}", nameof(encoded));

            // 转换为字节
            var bytes = new byte[sbBits.Length / 8];
            for (int i = 0, bi = 0; i < sbBits.Length; i += 8, bi++)
            {
                var byteBits = sbBits.ToString(i, 8);
                bytes[bi] = Convert.ToByte(byteBits, 2);
            }

            return bytes;
        }

        // ---- UUID特化：去除固定补零尾部，解码时自动补回（保持向后兼容） ----
        public override string EncodeUuid(Guid? guid = null)
        {
            var uuid = guid ?? Guid.NewGuid();
            var full = EncodeBytes(uuid.ToByteArray());
            // 对于16字节，128位 -> 需要4位补零，padding_code=1，故总是可安全去掉最后一字符
            return string.IsNullOrEmpty(full) ? full : full.Substring(0, full.Length - 1);
        }

        public override Guid DecodeUuid(string encoded)
        {
            if (string.IsNullOrEmpty(encoded))
                throw new ArgumentException("空编码无法解码为UUID", nameof(encoded));

            int expectedDataLen = (int)Math.Ceiling(128.0 / 12.0); // 11
            string encodedFull = encoded.Length == expectedDataLen
                ? encoded + Charset[1]
                : encoded;

            var bytes = DecodeString(encodedFull);
            if (bytes.Length != 16)
                throw new ArgumentException($"解码结果长度错误: {bytes.Length} bytes，期望16 bytes", nameof(encoded));
            return new Guid(bytes);
        }
    }
}
