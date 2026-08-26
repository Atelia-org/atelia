using Xunit;

namespace Atelia.SessionJournal.Tests;

public sealed class AdaptiveMarkdownFenceRendererTests {
    [Theory]
    [InlineData(
        "plain",
        "~~~~delegate-reply\nplain\n~~~~"
    )]
    [InlineData(
        "before\n~~~~\nafter",
        "~~~~~delegate-reply\nbefore\n~~~~\nafter\n~~~~~"
    )]
    [InlineData(
        "already terminated\n",
        "~~~~delegate-reply\nalready terminated\n~~~~"
    )]
    [InlineData(
        "```markdown\n<x>&y\n```",
        "~~~~delegate-reply\n```markdown\n<x>&y\n```\n~~~~"
    )]
    public void RenderBlock_PreservesExactBodyAndUsesIndependentFence(
        string exactBody,
        string expected
    ) => Assert.Equal(
        expected,
        AdaptiveMarkdownFenceRenderer.RenderBlock(
            "delegate-reply",
            exactBody
        )
    );

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("recap.block")]
    [InlineData("非ascii")]
    public void RenderBlock_RejectsNonTokenInfoStrings(string infoString) =>
        Assert.Throws<ArgumentException>(() =>
            AdaptiveMarkdownFenceRenderer.RenderBlock(
                infoString,
                "body"
            ));

    [Fact]
    public void RenderBlock_RejectsOverlongInfoString() =>
        Assert.Throws<ArgumentException>(() =>
            AdaptiveMarkdownFenceRenderer.RenderBlock(
                new string('a',
                    AdaptiveMarkdownFenceRenderer
                        .MaximumInfoStringLength + 1),
                "body"
            ));
}
