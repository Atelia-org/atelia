using Atelia.Agent.Core.Text;
using Xunit;

namespace Atelia.LiveContextProto.Tests;

public class BlockizerAndRendererTests {

    [Fact]
    public void DefaultBlockizer_SplitsByNewline() {
        var blocks = DefaultBlockizer.Instance.Blockize("aaa\nbbb\nccc");
        Assert.Equal(3, blocks.Length);
        Assert.Equal("aaa", blocks[0]);
        Assert.Equal("bbb", blocks[1]);
        Assert.Equal("ccc", blocks[2]);
    }

    [Fact]
    public void DefaultBlockizer_SingleLine_ReturnsSingleBlock() {
        var blocks = DefaultBlockizer.Instance.Blockize("hello");
        Assert.Single(blocks);
        Assert.Equal("hello", blocks[0]);
    }

    [Fact]
    public void DefaultBlockizer_EmptyString_ReturnsSingleEmptyBlock() {
        var blocks = DefaultBlockizer.Instance.Blockize("");
        Assert.Single(blocks);
        Assert.Equal("", blocks[0]);
    }

    [Fact]
    public void DefaultBlockizer_TrailingNewline_PreservesEmptyTrailingBlock() {
        var blocks = DefaultBlockizer.Instance.Blockize("a\nb\n");
        Assert.Equal(3, blocks.Length);
        Assert.Equal("a", blocks[0]);
        Assert.Equal("b", blocks[1]);
        Assert.Equal("", blocks[2]);
    }

    [Fact]
    public void Render_Bracketed_BasicFormat() {
        var blocks = new List<(uint Id, string Content)> {
            (1, "first line"),
            (2, "second line"),
            (3, "third line"),
        };

        var result = TextRenderer.Render(blocks);

        Assert.Contains("[1] first line", result);
        Assert.Contains("[2] second line", result);
        Assert.Contains("[3] third line", result);
    }

    [Fact]
    public void Render_Fenced_MultiLineBlocks() {
        var blocks = new List<(uint Id, string Content)> {
            (10, "line a\nline b"),
            (20, "line c"),
        };

        var opts = new RenderOptions { Style = RenderStyle.Fenced };
        var result = TextRenderer.Render(blocks, opts);

        Assert.Contains("--- block 10 ---", result);
        Assert.Contains("line a\nline b", result);
        Assert.Contains("--- block 20 ---", result);
    }

    [Fact]
    public void Render_MaxBlocks_TruncatesWithSummary() {
        var blocks = new List<(uint Id, string Content)>();
        for (uint i = 1; i <= 10; i++) {
            blocks.Add((i, $"block {i}"));
        }

        var opts = new RenderOptions { MaxBlocks = 3 };
        var result = TextRenderer.Render(blocks, opts);

        Assert.Contains("[1]", result);
        Assert.Contains("[2]", result);
        Assert.Contains("[3]", result);
        Assert.DoesNotContain("[4]", result);
        Assert.Contains("7 more blocks omitted", result);
    }

    [Fact]
    public void Render_MaxContentLength_TruncatesLongContent() {
        var blocks = new List<(uint Id, string Content)> {
            (1, new string('x', 500)),
        };

        var opts = new RenderOptions { MaxContentLength = 20 };
        var result = TextRenderer.Render(blocks, opts);

        Assert.Contains("500 chars", result);
        Assert.True(result.Length < 500);
    }

    [Fact]
    public void Render_EmptyBlocks_ReturnsEmpty() {
        var result = TextRenderer.Render(new List<(uint Id, string Content)>());
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void Render_ContentBlock_Overload_Works() {
        var blocks = new List<ContentBlock> {
            new(1, "alpha"),
            new(2, "beta"),
        };

        var result = TextRenderer.Render(blocks);
        Assert.Contains("[1] alpha", result);
        Assert.Contains("[2] beta", result);
    }

    [Fact]
    public void RoundTrip_Blockize_ThenRender() {
        var original = "hello world\nfoo bar\nbaz";
        var rawBlocks = DefaultBlockizer.Instance.Blockize(original);

        // 模拟分配 blockId（DurableText.LoadBlocks 的职责）
        var blocks = new List<(uint Id, string Content)>();
        for (int i = 0; i < rawBlocks.Length; i++) {
            blocks.Add(((uint)(i + 1), rawBlocks[i]));
        }

        var rendered = TextRenderer.Render(blocks);

        Assert.Contains("[1] hello world", rendered);
        Assert.Contains("[2] foo bar", rendered);
        Assert.Contains("[3] baz", rendered);
    }
}
