using Markdig.Syntax;

namespace Atelia.DesignDsl;

/// <summary>
/// 标题文本提取辅助类。
/// 从 HeadingBlock 提取纯文本标题，供 INodeBuilder 实现复用。
/// </summary>
public static class HeadingTextExtractor {
    /// <summary>
    /// 从 HeadingBlock 提取纯文本标题。
    /// </summary>
    /// <param name="heading">ATX Heading Block。</param>
    /// <param name="originalMarkdown">原始 Markdown 字符串。</param>
    /// <returns>标题的纯文本内容（保留原始格式，如反引号）。</returns>
    /// <remarks>
    /// 使用 HeadingBlock.Inline.Span 从原始 Markdown 切片获取完整标题文本，
    /// 这种方式保留了原始格式（包括 CodeInline 的反引号等）。
    /// </remarks>
    public static string ExtractText(HeadingBlock heading, string originalMarkdown) {
        if (heading.Inline is null || heading.Inline.Span.IsEmpty) { return string.Empty; }

        // 使用 Inline 容器的 Span 获取完整标题文本（不含 ## 前缀）
        var span = heading.Inline.Span;
        return originalMarkdown.Substring(span.Start, span.Length);
    }
}
