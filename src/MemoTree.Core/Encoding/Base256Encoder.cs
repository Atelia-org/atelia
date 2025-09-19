using System;
using System.Collections.Generic;

namespace MemoTree.Core.Encoding {
    /// <summary>
    /// Base256编码器：每个字节对应一个字符
    /// </summary>
    public sealed class Base256Encoder : CustomCharsetEncoderBase {
        public Base256Encoder(string charset) : base(charset) {
            ValidateCharsetSize(256, nameof(Base256Encoder));
        }

        public override float BitsPerChar => 8.0f;

        public override string ModeName => "base256";

        public override string EncodeBytes(byte[] data) {
            if (data == null || data.Length == 0) { return string.Empty; }
            var chars = new char[data.Length];
            for (int i = 0; i < data.Length; i++) {
                chars[i] = Charset[data[i]];
            }
            return new string(chars);
        }

        public override byte[] DecodeString(string encoded) {
            if (string.IsNullOrEmpty(encoded)) { return Array.Empty<byte>(); }
            var result = new byte[encoded.Length];
            for (int i = 0; i < encoded.Length; i++) {
                var ch = encoded[i];
                if (!CharToIndex.TryGetValue(ch, out int idx)) { throw new ArgumentException($"无效字符: {ch}", nameof(encoded)); }
                result[i] = (byte)idx;
            }
            return result;
        }
    }
}
