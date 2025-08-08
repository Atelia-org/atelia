using System;
using MemoTree.Core.Encoding;
using Xunit;

namespace MemoTree.Tests.Encoding
{
    public class Base64EncoderTests
    {
        private readonly Base64Encoder _encoder = new();

        [Fact]
        public void EncodeDecode_EmptyBytes_ReturnsEmpty()
        {
            var encoded = _encoder.EncodeBytes(Array.Empty<byte>());
            Assert.Equal(string.Empty, encoded);

            var decoded = _encoder.DecodeString(string.Empty);
            Assert.Equal(Array.Empty<byte>(), decoded);
        }

        [Theory]
        [InlineData("", "")] // 空
        [InlineData("000102", null)]
        [InlineData("00112233445566778899aabbccddeeff", null)] // 16字节
        public void Roundtrip_HexStrings(string hex, string? expectedEncoded)
        {
            var bytes = string.IsNullOrEmpty(hex) ? Array.Empty<byte>() : Convert.FromHexString(hex);
            var encoded = _encoder.EncodeBytes(bytes);

            if (expectedEncoded != null)
            {
                Assert.Equal(expectedEncoded, encoded);
            }

            var decoded = _encoder.DecodeString(encoded);
            Assert.Equal(bytes, decoded);
        }

        [Fact]
        public void Uuid_ShortForm_EncodeRemovesPadding()
        {
            var guid = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
            var full = _encoder.EncodeBytes(guid.ToByteArray());
            var shortForm = _encoder.EncodeUuid(guid);
            Assert.EndsWith("==", full);
            Assert.Equal(full[..^2], shortForm);
        }

        [Fact]
        public void Uuid_Decode_AcceptsShortAndFull()
        {
            var guid = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
            var shortForm = _encoder.EncodeUuid(guid);
            var full = shortForm + "==";

            var d1 = _encoder.DecodeUuid(shortForm);
            var d2 = _encoder.DecodeUuid(full);

            Assert.Equal(guid, d1);
            Assert.Equal(guid, d2);
        }
    }
}
