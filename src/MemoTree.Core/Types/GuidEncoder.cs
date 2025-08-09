using System;
using System.Linq;
using MemoTree.Core.Encoding;

namespace MemoTree.Core.Types
{
    /// <summary>
    /// 统一的GUID编码工具，确保项目中所有GUID到字符串的转换使用相同的编码方式
    /// 推荐使用Base4096-CJK编码(11字符)解决Base64的文件系统兼容性和URL安全性问题
    /// </summary>
    public static class GuidEncoder
    {
        private static readonly IEncoder _defaultEncoder = new Base4096Encoder();
        private static readonly IEncoder _base64Encoder = new Base64Encoder();

        /// <summary>
        /// 将GUID编码为ID字符串表示
        /// 推荐实现：Base4096-CJK编码，生成11个语义中性汉字字符
        /// 优势：文件系统安全、URL相对安全、路径长度减半、LLM友好
        /// </summary>
        public static string ToIdString(Guid guid)
        {
            return _defaultEncoder.EncodeUuid(guid);
        }

        /// <summary>
        /// 从ID字符串解码回GUID（智能识别编码格式）
        /// </summary>
        public static Guid FromIdString(string encoded)
        {
            var encodingType = DetectEncodingType(encoded);
            return encodingType switch
            {
                GuidEncodingType.Base4096CJK => _defaultEncoder.DecodeUuid(encoded),
                GuidEncodingType.Base64 => _base64Encoder.DecodeUuid(encoded),
                GuidEncodingType.HexTruncated => FromHexTruncated(encoded),
                _ => throw new ArgumentException($"Unknown encoding format: {encoded}")
            };
        }

        /// <summary>
        /// 检测编码格式类型（智能格式识别）
        /// </summary>
        public static GuidEncodingType DetectEncodingType(string encoded)
        {
            if (string.IsNullOrWhiteSpace(encoded))
                return GuidEncodingType.Unknown;

            return encoded.Length switch
            {
                11 when encoded.All(c => c >= '\u4e00' && c <= '\u9fff') => GuidEncodingType.Base4096CJK,
                22 when IsBase64String(encoded) => GuidEncodingType.Base64,
                12 when encoded.All(c => char.IsDigit(c) || (c >= 'a' && c <= 'f')) => GuidEncodingType.HexTruncated,
                _ => GuidEncodingType.Unknown
            };
        }

        /// <summary>
        /// 生成URL安全的Base64编码（用于Web传输）
        /// </summary>
        public static string ToUrlSafeString(Guid guid)
        {
            var bytes = guid.ToByteArray();
            var base64 = Convert.ToBase64String(bytes);
            return base64.TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        /// <summary>
        /// 从URL安全的Base64编码解码
        /// </summary>
        public static Guid FromUrlSafeString(string encoded)
        {
            var base64 = encoded.Replace('-', '+').Replace('_', '/');
            var withPadding = base64 + "==";
            var bytes = Convert.FromBase64String(withPadding);
            return new Guid(bytes);
        }

        private static bool IsBase64String(string value)
        {
            try
            {
                var withPadding = value + "==";
                Convert.FromBase64String(withPadding);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static Guid FromHexTruncated(string encoded)
        {
            if (encoded.Length != 12)
                throw new ArgumentException($"Invalid hex truncated GUID length: {encoded.Length}, expected 12");

            // 这是一个已废弃的格式，仅用于向后兼容
            // 由于截取了GUID的一部分，无法完全恢复原始GUID
            throw new NotSupportedException("Hex truncated format is deprecated and cannot be reliably decoded");
        }
    }

    /// <summary>
    /// GUID编码类型枚举（按推荐优先级排序）
    /// </summary>
    public enum GuidEncodingType
    {
        Unknown,
        Base4096CJK,    // 推荐：11字符，文件系统安全，LLM友好
        Base64,         // 备选：22字符，存在兼容性问题
        HexTruncated    // 已废弃：12位十六进制格式，存在冲突风险
    }
}
