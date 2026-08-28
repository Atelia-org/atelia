using System.Text;
using Xunit;

namespace Atelia.Galatea.Prompts.Tests;

public sealed class GalateaCharacterNameTests {
    [Theory]
    [InlineData("Galatea")]
    [InlineData("爱丽丝")]
    [InlineData("👩‍🚀")]
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
    [InlineData("Gala\u202Etea")]
    [InlineData("Gala\u2066tea")]
    [InlineData("Gala\u2069tea")]
    [InlineData("\u200D")]
    [InlineData("[Galatea]")]
    [InlineData("Gala$tea")]
    [InlineData("Gala{tea")]
    [InlineData("Gala}tea")]
    [InlineData("Galatea${other}")]
    [InlineData("旁白")]
    [InlineData("状态摘要")]
    [InlineData("角色名")]
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
