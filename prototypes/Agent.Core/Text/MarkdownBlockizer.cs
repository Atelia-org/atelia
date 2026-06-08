using Markdig;
using Markdig.Syntax;

namespace Atelia.Agent.Core.Text;

/// <summary>
/// 基于 Markdig 的 Markdown 分块策略：把文档拆分为顶层 BlockElement 序列。
/// </summary>
/// <remarks>
/// <para>使用 Markdig 的 <c>UsePreciseSourceLocation()</c> 解析 Markdown，遍历 <see cref="MarkdownDocument"/>
/// 的顶层子 block，按 <see cref="SourceSpan"/> 切片得到原始文本。</para>
/// <para>顶层 block 类型典型例子：</para>
/// <list type="bullet">
///   <item><see cref="HeadingBlock"/> — # 一二级标题等</item>
///   <item><see cref="ParagraphBlock"/> — 段落</item>
///   <item><see cref="ListBlock"/> — 整个列表（嵌套列表项不拆）</item>
///   <item><see cref="FencedCodeBlock"/> — ``` 围栏代码块</item>
///   <item><see cref="QuoteBlock"/> — &gt; 引用</item>
///   <item><see cref="ThematicBreakBlock"/> — --- 分隔线</item>
///   <item><see cref="HtmlBlock"/> — 原生 HTML</item>
/// </list>
/// <para>Block 之间的空白（典型为 <c>\n\n</c>）会作为独立"gap block"保留，
/// 以满足 <see cref="IBlockizer"/>"输出按顺序拼接后等于原文"的字符无损契约。</para>
/// <para><b>未来扩展点</b>：长 block 的二次拆分（如把 ListBlock 拆成 ListItem 序列）、
/// 树形折叠（MemoTree 风格的 LOD）等可作为独立 IBlockizer 实现叠加，本类只做最朴素的"按顶层 block 拆分"。</para>
/// </remarks>
public sealed class MarkdownBlockizer : IBlockizer {
    public static readonly MarkdownBlockizer Instance = new();

    private readonly MarkdownPipeline _pipeline;

    public MarkdownBlockizer() {
        _pipeline = new MarkdownPipelineBuilder()
            .UsePreciseSourceLocation()
            .Build();
    }

    public string[] Blockize(string text) {
        if (string.IsNullOrEmpty(text)) {
            return text.Length == 0 ? Array.Empty<string>() : new[] { text };
        }

        var document = Markdown.Parse(text, _pipeline);

        var blocks = new List<string>();
        int cursor = 0;

        foreach (var child in document) {
            int start = child.Span.Start;
            int endExclusive = child.Span.End + 1; // SourceSpan.End is inclusive

            // 如果 Markdig 没能给出有效 span（罕见情况，例如空 block），跳过
            if (child.Span.IsEmpty || endExclusive <= start || start < cursor) {
                continue;
            }

            // 保留 block 之间的间隔（通常是 "\n\n"）作为独立 gap block
            if (start > cursor) {
                blocks.Add(text.Substring(cursor, start - cursor));
            }

            // block 本体
            blocks.Add(text.Substring(start, endExclusive - start));
            cursor = endExclusive;
        }

        // 文档末尾的残余字符（典型是末尾换行）
        if (cursor < text.Length) {
            blocks.Add(text.Substring(cursor));
        }

        return blocks.Count == 0 ? new[] { text } : blocks.ToArray();
    }
}
