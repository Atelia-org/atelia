using Markdig;
using Markdig.Extensions.Yaml;
using Markdig.Syntax;

namespace Atelia.DesignDsl.Tests;

/// <summary>
/// 测试辅助方法。
/// </summary>
public static class TestHelpers {
    /// <summary>
    /// 使用标准配置解析 Markdown 文档。
    /// 启用 PreciseSourceLocation（用于准确的 Span 位置）和 YamlFrontMatter（识别 YAML 头）。
    /// </summary>
    /// <param name="markdown">Markdown 字符串。</param>
    /// <returns>解析后的 MarkdownDocument。</returns>
    public static MarkdownDocument ParseMarkdown(string markdown) {
        var pipeline = new MarkdownPipelineBuilder()
            .UsePreciseSourceLocation()
            .UseYamlFrontMatter()
            .Build();
        return Markdown.Parse(markdown, pipeline);
    }

    /// <summary>
    /// 从 Block 中提取第一行文本（用于验证 ParagraphBlock 内容）。
    /// </summary>
    /// <param name="block">Block 对象。</param>
    /// <param name="originalMarkdown">原始 Markdown。</param>
    /// <returns>Block 对应的源文本。</returns>
    public static string GetBlockText(Block block, string originalMarkdown) {
        return originalMarkdown.Substring(block.Span.Start, block.Span.Length);
    }
}
