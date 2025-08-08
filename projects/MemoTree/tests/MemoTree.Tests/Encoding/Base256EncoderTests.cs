using System;
using System.Linq;
using MemoTree.Core.Encoding;
using Xunit;

namespace MemoTree.Tests.Encoding
{
    public class Base256EncoderTests
    {
        private static string CreateCharset256()
        {
            // 使用代码点0..255依次构造，确保256个唯一字符
            return new string(Enumerable.Range(0, 256).Select(i => (char)i).ToArray());
        }

        private readonly Base256Encoder _encoder = new(CreateCharset256());

        [Fact]
        public void EncodeDecode_EmptyBytes_ReturnsEmpty()
        {
            var encoded = _encoder.EncodeBytes(Array.Empty<byte>());
            Assert.Equal(string.Empty, encoded);

            var decoded = _encoder.DecodeString(string.Empty);
            Assert.Equal(Array.Empty<byte>(), decoded);
        }

        [Fact]
        public void Roundtrip_RandomBytes()
        {
            var rnd = new Random(42);
            var bytes = new byte[1024];
            rnd.NextBytes(bytes);

            var encoded = _encoder.EncodeBytes(bytes);
            var decoded = _encoder.DecodeString(encoded);

            Assert.Equal(bytes, decoded);
        }

        [Fact]
        public void Decode_InvalidChar_Throws()
        {
            // 字符\u0100不在0..255范围内，因此应当抛出异常
            Assert.Throws<ArgumentException>(() => _encoder.DecodeString("\u0100"));
        }
    }
}
