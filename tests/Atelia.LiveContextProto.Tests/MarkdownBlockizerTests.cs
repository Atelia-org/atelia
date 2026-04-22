using Atelia.Agent.Core.Text;
using Xunit;

namespace Atelia.LiveContextProto.Tests;

public class MarkdownBlockizerTests {

    [Fact]
    public void Empty_ReturnsEmpty() {
        var blocks = MarkdownBlockizer.Instance.Blockize("");
        Assert.Empty(blocks);
    }

    [Fact]
    public void SingleParagraph_ReturnsOneBlock() {
        var blocks = MarkdownBlockizer.Instance.Blockize("hello world");
        Assert.Single(blocks);
        Assert.Equal("hello world", blocks[0]);
    }

    [Fact]
    public void HeadingAndParagraph_TwoBlocksPlusGap() {
        var src = "# Title\n\nSome paragraph.";
        var blocks = MarkdownBlockizer.Instance.Blockize(src);

        // Expect: ["# Title", "\n\n", "Some paragraph."]
        Assert.Equal(3, blocks.Length);
        Assert.Equal("# Title", blocks[0]);
        Assert.Equal("\n\n", blocks[1]);
        Assert.Equal("Some paragraph.", blocks[2]);

        // Fidelity contract
        Assert.Equal(src, string.Concat(blocks));
    }

    [Fact]
    public void ListBlock_StaysAsSingleBlock() {
        var src = "- item 1\n- item 2\n- item 3";
        var blocks = MarkdownBlockizer.Instance.Blockize(src);

        Assert.Single(blocks);
        Assert.Equal(src, blocks[0]);
    }

    [Fact]
    public void FencedCodeBlock_StaysAsSingleBlock() {
        var src = "```csharp\nvar x = 1;\nvar y = 2;\n```";
        var blocks = MarkdownBlockizer.Instance.Blockize(src);

        Assert.Single(blocks);
        Assert.Equal(src, blocks[0]);
    }

    [Fact]
    public void MixedDocument_PreservesAllChars() {
        var src =
            "# Atelia\n\n" +
            "Atelia is an experimental project.\n\n" +
            "## Components\n\n" +
            "- StateJournal\n" +
            "- DurableText\n\n" +
            "```csharp\nvar x = 1;\n```\n\n" +
            "End.";

        var blocks = MarkdownBlockizer.Instance.Blockize(src);

        // Fidelity: concatenation must equal original
        Assert.Equal(src, string.Concat(blocks));

        // Should have multiple blocks (heading, p, heading, list, code, p, plus gaps)
        Assert.True(blocks.Length >= 6);
    }

    [Fact]
    public void TrailingNewline_Preserved() {
        var src = "# Title\n";
        var blocks = MarkdownBlockizer.Instance.Blockize(src);

        Assert.Equal(src, string.Concat(blocks));
    }

    [Fact]
    public void ThematicBreak_IsOwnBlock() {
        var src = "above\n\n---\n\nbelow";
        var blocks = MarkdownBlockizer.Instance.Blockize(src);

        Assert.Equal(src, string.Concat(blocks));
        // 3 content blocks + 2 gaps = 5
        Assert.Equal(5, blocks.Length);
        Assert.Equal("above", blocks[0]);
        Assert.Equal("---", blocks[2]);
        Assert.Equal("below", blocks[4]);
    }
}
