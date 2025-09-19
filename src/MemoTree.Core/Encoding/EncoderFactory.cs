using System;

namespace MemoTree.Core.Encoding {
    /// <summary>
    /// 编码器工厂
    /// </summary>
    public static class EncoderFactory {
        /// <summary>
        /// 创建编码器
        /// </summary>
        /// <param name="mode">
        /// 编码模式：
        /// - "base64": 标准Base64编码
        /// - "base256": Base256编码（需要提供charset）
        /// - "base4096": Base4096编码（可使用默认字符集）
        /// </param>
        /// <param name="charset">可选的字符集</param>
        public static IEncoder Create(string mode, string? charset = null) {
            return mode switch {
                "base64" => new Base64Encoder(),
                "base256" => charset != null ? new Base256Encoder(charset) : throw new ArgumentException("Base256需要提供charset", nameof(charset)),
                "base4096" => new Base4096Encoder(charset),
                _ => throw new ArgumentException($"不支持的编码模式: {mode}", nameof(mode))
            };
        }

        /// <summary>
        /// 自动选择编码器：根据字符集大小选择最佳编码（无字符集则使用Base4096默认字符集）
        /// </summary>
        public static IEncoder CreateAuto(string? charset = null) {
            if (charset == null) { return new Base4096Encoder(null); }
            if (charset.Length >= 4096) { return new Base4096Encoder(charset); }
            if (charset.Length >= 256) { return new Base256Encoder(charset); }
            throw new ArgumentException("自动模式需要至少256个字符的字符集，或不提供字符集以使用Base4096默认字符集", nameof(charset));
        }
    }
}
