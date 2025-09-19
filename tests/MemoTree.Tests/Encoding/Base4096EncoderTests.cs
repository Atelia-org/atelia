using System;
using System.Text;
using MemoTree.Core.Encoding;
using Xunit;

namespace MemoTree.Tests.Encoding {
    public class Base4096EncoderTests {
        private static string CreateTestCharset4096() {
            // 使用连续的CJK字符（从U+4E00开始）构造4096个唯一字符的字符集
            var sb = new StringBuilder(4096);
            for (int i = 0; i < 4096; i++) {
                sb.Append((char)(0x4E00 + i));
            }
            return sb.ToString();
        }

        private readonly string _charset = CreateTestCharset4096();
        private Base4096Encoder CreateEncoder() => new Base4096Encoder(_charset);

        [Fact]
        public void EncodeDecode_EmptyBytes_ReturnsEmpty() {
            var encoder = CreateEncoder();
            var encoded = encoder.EncodeBytes(Array.Empty<byte>());
            Assert.Equal(string.Empty, encoded);

            var decoded = encoder.DecodeString(string.Empty);
            Assert.Equal(Array.Empty<byte>(), decoded);
        }

        [Theory]
        [InlineData("", "")] // 空
        [InlineData("00", null)] // 一个字节
        [InlineData("ff", null)] // 一个字节
        [InlineData("0001", null)] // 两个字节
        [InlineData("00112233445566778899aabbccddeeff", null)] // 16字节
        public void Roundtrip_HexStrings(string hex, string? expectedEncoded) {
            var encoder = CreateEncoder();
            var bytes = string.IsNullOrEmpty(hex) ? Array.Empty<byte>() : Convert.FromHexString(hex);
            var encoded = encoder.EncodeBytes(bytes);

            if (expectedEncoded != null) {
                Assert.Equal(expectedEncoded, encoded);
            }

            var decoded = encoder.DecodeString(encoded);
            Assert.Equal(bytes, decoded);
        }

        [Fact]
        public void Uuid_ShortForm_EncodeRemovesPaddingMarker() {
            var encoder = CreateEncoder();
            var guid = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
            var full = encoder.EncodeBytes(guid.ToByteArray());
            var shortForm = encoder.EncodeUuid(guid);
            Assert.Equal(full[..^1], shortForm);
        }

        [Fact]
        public void Uuid_Decode_AcceptsShortAndFull() {
            var encoder = CreateEncoder();
            var guid = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
            var shortForm = encoder.EncodeUuid(guid);
            var full = shortForm + _charset[1];

            var d1 = encoder.DecodeUuid(shortForm);
            var d2 = encoder.DecodeUuid(full);

            Assert.Equal(guid, d1);
            Assert.Equal(guid, d2);
        }

        [Fact]
        public void Decode_InvalidChar_Throws() {
            var encoder = CreateEncoder();
            // 使用不在字符集中的字符（例如ASCII 'A'，不在我们构造的CJK范围内）
            Assert.Throws<ArgumentException>(() => encoder.DecodeString("AA" + _charset[0]));
        }

        [Fact]
        public void Decode_InvalidBitLength_Throws() {
            var encoder = CreateEncoder();
            // 构造一个非法编码：数据位数非8倍数。一个数据字符=>12位，再附0 padding标记（字符集[0]），仍非8倍数
            var invalid = new string(_charset[2], 1) + _charset[0];
            Assert.Throws<ArgumentException>(() => encoder.DecodeString(invalid));
        }
    }
}
