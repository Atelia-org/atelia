using System.Text;
using Xunit;

namespace Atelia.Galatea.Prompts.Tests;

public sealed class GalateaCharacterNameTests {
    [Theory]
    [InlineData("Galatea")]
    [InlineData("爱丽丝")]
    [InlineData("👩‍🚀")]
    [InlineData("${Alice}")]
    public void CanonicalNamesPreserveExactValue(string value) {
        var name = new GalateaCharacterName(value);

        Assert.Equal(value, name.Value);
        Assert.Equal(value, name.ToString());
        Assert.Equal(Encoding.UTF8.GetByteCount(value), name.Utf8ByteCount);
    }

    [Fact]
    public void Utf8BoundaryIsExact() {
        Assert.Equal(
            new string('a', GalateaCharacterName.MaximumUtf8Bytes),
            new GalateaCharacterName(
                new string('a', GalateaCharacterName.MaximumUtf8Bytes)
            ).Value
        );
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new GalateaCharacterName(
                new string('a', GalateaCharacterName.MaximumUtf8Bytes + 1)
            ));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" Galatea")]
    [InlineData("Galatea ")]
    [InlineData("Gala\ntea")]
    [InlineData("Gala\u2028tea")]
    [InlineData("[Galatea]")]
    [InlineData("旁白")]
    [InlineData("状态摘要")]
    public void InvalidLabelsAreRejected(string value) {
        Assert.ThrowsAny<ArgumentException>(() =>
            new GalateaCharacterName(value));
    }

    [Fact]
    public void InvalidUtf16AndNonNfcAreRejected() {
        Assert.Throws<ArgumentException>(() =>
            new GalateaCharacterName("\uD800"));
        Assert.Throws<ArgumentException>(() =>
            new GalateaCharacterName("e\u0301"));
    }

    [Fact]
    public void PublicSurfaceIsExact() {
        Assert.Equal(
            [typeof(GalateaCharacterName), typeof(GalateaPromptTemplate)],
            typeof(GalateaCharacterName).Assembly.GetExportedTypes()
                .OrderBy(static value => value.Name)
        );
    }
}
