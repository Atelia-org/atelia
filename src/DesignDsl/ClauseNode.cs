using Markdig.Syntax;

namespace Atelia.DesignDsl;

/// <summary>
/// Clause 定义节点。
/// 对应 `### decision [CLAUSE-ID] 可选标题` 模式的 ATX Heading。
/// </summary>
public sealed class ClauseNode : AxtNode {
    /// <summary>
    /// 创建 ClauseNode。
    /// </summary>
    /// <param name="heading">原始 HeadingBlock。</param>
    /// <param name="content">下辖的块级内容。</param>
    /// <param name="modifier">Clause 修饰符类型。</param>
    /// <param name="clauseId">Clause 标识符（不含方括号，保留原始大小写）。</param>
    /// <param name="title">可选标题（已 Trim，空字符串转为 null）。</param>
    public ClauseNode(HeadingBlock heading, IReadOnlyList<Block> content,
                      ClauseModifier modifier, string clauseId, string? title)
        : base(heading, content) {
        Modifier = modifier;
        ClauseId = clauseId;
        Title = title;
    }

    /// <summary>
    /// Clause 修饰符类型。
    /// </summary>
    public ClauseModifier Modifier { get; }

    /// <summary>
    /// Clause 标识符（不含方括号，保留原始大小写）。
    /// </summary>
    public string ClauseId { get; }

    /// <summary>
    /// 可选标题（已 Trim，空字符串转为 null）。
    /// </summary>
    public string? Title { get; }
}
