// DocGraph v0.1 - FrontmatterParser 测试
// 参考：spec.md §2 Frontmatter 解析约束

using Atelia.DocGraph.Utils;
using Xunit;

namespace Atelia.DocGraph.Tests;

public class FrontmatterParserTests
{
    #region Basic Parsing

    [Fact]
    public void TryParse_ShouldParseSimpleFrontmatter()
    {
        var content = """
            ---
            title: "测试文档"
            docId: "DOC-001"
            ---
            正文内容
            """;

        var result = FrontmatterParser.TryParse(content, out var frontmatter, out var error);

        Assert.True(result);
        Assert.NotNull(frontmatter);
        Assert.Null(error);
        Assert.Equal("测试文档", frontmatter["title"]);
        Assert.Equal("DOC-001", FrontmatterParser.GetString(frontmatter, "docId") ?? frontmatter["doc_id"]?.ToString());
    }

    [Fact]
    public void TryParse_ShouldParseArrayFields()
    {
        var content = """
            ---
            title: "Wish 文档"
            produce:
              - "docs/api.md"
              - "docs/spec.md"
            ---
            """;

        var result = FrontmatterParser.TryParse(content, out var frontmatter, out _);

        Assert.True(result);
        Assert.NotNull(frontmatter);

        var produceList = FrontmatterParser.GetStringArray(frontmatter, "produce");
        Assert.Equal(2, produceList.Count);
        Assert.Contains("docs/api.md", produceList);
        Assert.Contains("docs/spec.md", produceList);
    }

    #endregion

    #region Boundary Detection

    [Fact]
    public void TryParse_ShouldReturnFalseForNoFrontmatter()
    {
        var content = """
            # 普通 Markdown 文档

            这是正文内容，没有 frontmatter。
            """;

        var result = FrontmatterParser.TryParse(content, out var frontmatter, out var error);

        Assert.False(result);
        Assert.Null(frontmatter);
        Assert.Null(error);  // 无 frontmatter 不是错误
    }

    [Fact]
    public void TryParse_ShouldIgnoreFrontmatterNotAtStart()
    {
        var content = """
            这是正文

            ---
            title: "不是 frontmatter"
            ---
            """;

        var result = FrontmatterParser.TryParse(content, out var frontmatter, out _);

        Assert.False(result);
        Assert.Null(frontmatter);
    }

    [Fact]
    public void TryParse_ShouldAllowLeadingWhitespace()
    {
        var content = """
            
            ---
            title: "带前导空白"
            ---
            """;

        var result = FrontmatterParser.TryParse(content, out var frontmatter, out _);

        Assert.True(result);
        Assert.NotNull(frontmatter);
    }

    #endregion

    #region Error Handling

    [Fact]
    public void TryParse_ShouldDetectYamlAnchorAlias()
    {
        var content = """
            ---
            base: &base
              name: "共享配置"
            derived:
              <<: *base
            ---
            """;

        var result = FrontmatterParser.TryParse(content, out _, out var error);

        Assert.False(result);
        Assert.NotNull(error);
        Assert.Equal("DOCGRAPH_YAML_ALIAS_DETECTED", error.ErrorCode);
    }

    [Fact]
    public void TryParse_ShouldHandleSyntaxError()
    {
        var content = """
            ---
            title: "缺少结束引号
            invalid: yaml: content:
            ---
            """;

        var result = FrontmatterParser.TryParse(content, out _, out var error);

        Assert.False(result);
        Assert.NotNull(error);
        Assert.Equal("DOCGRAPH_YAML_SYNTAX_ERROR", error.ErrorCode);
    }

    #endregion

    #region Field Extraction Helpers

    [Fact]
    public void GetString_ShouldReturnNullForMissingField()
    {
        var frontmatter = new Dictionary<string, object?>
        {
            ["title"] = "测试"
        };

        var result = FrontmatterParser.GetString(frontmatter, "nonexistent");

        Assert.Null(result);
    }

    [Fact]
    public void GetStringArray_ShouldReturnEmptyListForMissingField()
    {
        var frontmatter = new Dictionary<string, object?>
        {
            ["title"] = "测试"
        };

        var result = FrontmatterParser.GetStringArray(frontmatter, "nonexistent");

        Assert.Empty(result);
    }

    [Fact]
    public void GetStringArray_ShouldConvertSingleValueToArray()
    {
        var frontmatter = new Dictionary<string, object?>
        {
            ["produce"] = "single/file.md"
        };

        var result = FrontmatterParser.GetStringArray(frontmatter, "produce");

        Assert.Single(result);
        Assert.Equal("single/file.md", result[0]);
    }

    #endregion
}
