using Xunit;
using MemoTree.Core.Storage.Versioned;

namespace MemoTree.Tests.Storage.Versioned {
    public class VersionFormatterTests {
        [Theory]
        [InlineData(1, "1")]
        [InlineData(10, "A")]
        [InlineData(255, "FF")]
        [InlineData(4096, "1000")]
        [InlineData(long.MaxValue, "7FFFFFFFFFFFFFFF")]
        public void HexVersionFormatter_FormatVersion_ProducesCorrectHex(long version, string expectedHex) {
            var formatter = new HexVersionFormatter();
            var result = formatter.FormatVersion(version);
            Assert.Equal(expectedHex, result);
        }

        [Theory]
        [InlineData("1", 1)]
        [InlineData("A", 10)]
        [InlineData("a", 10)] // 小写也应该支持
        [InlineData("FF", 255)]
        [InlineData("ff", 255)] // 小写也应该支持
        [InlineData("1000", 4096)]
        [InlineData("7FFFFFFFFFFFFFFF", long.MaxValue)]
        public void HexVersionFormatter_ParseVersion_ParsesCorrectly(string hexString, long expectedVersion) {
            var formatter = new HexVersionFormatter();
            var result = formatter.ParseVersion(hexString);
            Assert.True(result.HasValue);
            Assert.Equal(expectedVersion, result.Value);
        }

        [Theory]
        [InlineData("")]
        [InlineData("G")] // 无效的十六进制字符
        [InlineData("0")] // 版本号不能为0
        [InlineData("-1")] // 负数
        [InlineData("FFFFFFFFFFFFFFFF")] // 超出long.MaxValue
        public void HexVersionFormatter_ParseVersion_ReturnsNullForInvalidInput(string invalidInput) {
            var formatter = new HexVersionFormatter();
            var result = formatter.ParseVersion(invalidInput);
            Assert.Null(result);
        }

        [Theory]
        [InlineData(1, "1")]
        [InlineData(10, "10")]
        [InlineData(255, "255")]
        [InlineData(4096, "4096")]
        [InlineData(long.MaxValue, "9223372036854775807")]
        public void DecimalVersionFormatter_FormatVersion_ProducesCorrectDecimal(long version, string expectedDecimal) {
            var formatter = new DecimalVersionFormatter();
            var result = formatter.FormatVersion(version);
            Assert.Equal(expectedDecimal, result);
        }

        [Theory]
        [InlineData("1", 1)]
        [InlineData("10", 10)]
        [InlineData("255", 255)]
        [InlineData("4096", 4096)]
        [InlineData("9223372036854775807", long.MaxValue)]
        public void DecimalVersionFormatter_ParseVersion_ParsesCorrectly(string decimalString, long expectedVersion) {
            var formatter = new DecimalVersionFormatter();
            var result = formatter.ParseVersion(decimalString);
            Assert.True(result.HasValue);
            Assert.Equal(expectedVersion, result.Value);
        }

        [Fact]
        public void HexVersionFormatter_ProducesShortFileNames() {
            var hexFormatter = new HexVersionFormatter();
            var decimalFormatter = new DecimalVersionFormatter();

            var largeVersion = 1000000000L; // 10亿

            var hexResult = hexFormatter.FormatVersion(largeVersion);
            var decimalResult = decimalFormatter.FormatVersion(largeVersion);

            // Hex should be shorter for large numbers
            Assert.True(hexResult.Length < decimalResult.Length);
            Assert.Equal("3B9ACA00", hexResult);
            Assert.Equal("1000000000", decimalResult);
        }
    }
}
