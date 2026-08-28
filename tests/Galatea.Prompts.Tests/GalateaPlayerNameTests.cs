using System.Text;
using Xunit;

namespace Atelia.Galatea.Prompts.Tests;

public sealed class GalateaPlayerNameTests {
    [Theory]
    [InlineData("刘世超")]
    [InlineData("Player One")]
    [InlineData("🧑‍🚀")]
    public void CanonicalNamesPreserveExactValue(string value) {
        var name = new GalateaPlayerName(value);

        Assert.Equal(value, name.Value);
        Assert.Equal(value, name.ToString());
        Assert.Equal(Encoding.UTF8.GetByteCount(value), name.Utf8ByteCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" Player")]
    [InlineData("Player ")]
    [InlineData("Player\nOne")]
    [InlineData("Player\u202E")]
    [InlineData("\u200D")]
    [InlineData("[Player]")]
    [InlineData("Player${other}")]
    [InlineData("旁白")]
    [InlineData("状态摘要")]
    [InlineData("角色名")]
    public void InvalidLabelsAreRejected(string value) {
        Assert.ThrowsAny<ArgumentException>(() =>
            new GalateaPlayerName(value));
    }

    [Fact]
    public void Utf8AndCanonicalUnicodeBoundsMatchCharacterNames() {
        Assert.Equal(
            new string('a', GalateaPlayerName.MaximumUtf8Bytes),
            new GalateaPlayerName(
                new string('a', GalateaPlayerName.MaximumUtf8Bytes)
            ).Value
        );
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new GalateaPlayerName(
                new string('a', GalateaPlayerName.MaximumUtf8Bytes + 1)
            ));
        Assert.Throws<ArgumentException>(() =>
            new GalateaPlayerName("\uD800"));
        Assert.Throws<ArgumentException>(() =>
            new GalateaPlayerName("e\u0301"));
    }
}
