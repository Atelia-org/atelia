using Markdig.Syntax;

namespace Atelia.DesignDsl;

/// <summary>
/// Term 定义节点。
/// 对应 `## term `Term-ID` 可选标题` 模式的 ATX Heading。
/// </summary>
public sealed class TermNode : AxtNode {
    /// <summary>
    /// 创建 TermNode。
    /// </summary>
    /// <param name="heading">原始 HeadingBlock。</param>
    /// <param name="content">下辖的块级内容。</param>
    /// <param name="termId">Term 标识符（不含反引号，保留原始大小写）。</param>
    /// <param name="title">可选标题（已 Trim，空字符串转为 null）。</param>
    public TermNode(HeadingBlock heading, IReadOnlyList<Block> content, string termId, string? title)
        : base(heading, content) {
        TermId = termId;
        Title = title;
    }

    /// <summary>
    /// Term 标识符（不含反引号，保留原始大小写）。
    /// </summary>
    public string TermId { get; }

    /// <summary>
    /// 可选标题（已 Trim，空字符串转为 null）。
    /// </summary>
    public string? Title { get; }
}
