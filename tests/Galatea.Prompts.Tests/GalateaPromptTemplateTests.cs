using Xunit;

namespace Atelia.Galatea.Prompts.Tests;

public sealed class GalateaPromptTemplateTests {
    [Fact]
    public void ExactTokenRendersEveryOccurrenceWithoutRecursion() {
        var name = new GalateaCharacterName("Alice");

        string rendered = GalateaPromptTemplate.Render(
            "[${characterName}] meets ${characterName}.",
            name,
            maximumUtf8Bytes: 1024
        );

        Assert.Equal("[Alice] meets Alice.", rendered);
        Assert.DoesNotContain("${", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void CharacterAndPlayerTokensRenderTogether() {
        string rendered = GalateaPromptTemplate.Render(
            "${playerName} visits [${characterName}].",
            new GalateaCharacterName("Alice"),
            new GalateaPlayerName("老刘"),
            maximumUtf8Bytes: 1024
        );

        Assert.Equal("老刘 visits [Alice].", rendered);
        Assert.DoesNotContain("${", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void PlayerTokenRequiresThePlayerAwareOverload() {
        Assert.Throws<ArgumentException>(() =>
            GalateaPromptTemplate.Render(
                "${characterName} meets ${playerName}.",
                new GalateaCharacterName("Galatea"),
                maximumUtf8Bytes: 1024
            ));
    }

    [Theory]
    [InlineData("${Alice}")]
    [InlineData("Alice${other}")]
    [InlineData("Alice{")]
    public void CharacterNamesCannotLeaveResidualTemplateSyntax(
        string value
    ) {
        Assert.Throws<ArgumentException>(() =>
            new GalateaCharacterName(value));
    }

    [Theory]
    [InlineData("plain text")]
    [InlineData("${CharacterName}")]
    [InlineData("${other}")]
    [InlineData("${")]
    [InlineData("${characterName} then ${other}")]
    public void MissingUnknownAndMalformedTokensAreRejected(string source) {
        Assert.Throws<ArgumentException>(() =>
            GalateaPromptTemplate.Render(
                source,
                new GalateaCharacterName("Galatea"),
                maximumUtf8Bytes: 1024
            ));
    }

    [Fact]
    public void SourceAndRenderedUtf8BoundsAreIndependent() {
        var name = new GalateaCharacterName(new string('a', 128));
        const string Source = "${characterName}";

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GalateaPromptTemplate.Render(
                Source,
                name,
                maximumUtf8Bytes: Source.Length - 1
            ));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GalateaPromptTemplate.Render(
                Source,
                name,
                maximumUtf8Bytes: name.Utf8ByteCount - 1
            ));
        Assert.Equal(
            name.Value,
            GalateaPromptTemplate.Render(
                Source,
                name,
                maximumUtf8Bytes: name.Utf8ByteCount
            )
        );
    }

    [Fact]
    public void PlayerRenderedUtf8BoundIsIncluded() {
        var player = new GalateaPlayerName(new string('p', 128));
        const string Source = "${characterName}${playerName}";

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GalateaPromptTemplate.Render(
                Source,
                new GalateaCharacterName("G"),
                player,
                maximumUtf8Bytes: 128
            ));
        Assert.Equal(
            "G" + player.Value,
            GalateaPromptTemplate.Render(
                Source,
                new GalateaCharacterName("G"),
                player,
                maximumUtf8Bytes: 129
            )
        );
    }

    [Fact]
    public void InvalidArgumentsAreRejectedBeforeRendering() {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GalateaPromptTemplate.Render(
                GalateaPromptTemplate.CharacterNameToken,
                new GalateaCharacterName("Galatea"),
                maximumUtf8Bytes: 0
            ));
        Assert.Throws<ArgumentException>(() =>
            GalateaPromptTemplate.Render(
                "${characterName}\uD800",
                new GalateaCharacterName("Galatea"),
                maximumUtf8Bytes: 1024
            ));
    }
}
