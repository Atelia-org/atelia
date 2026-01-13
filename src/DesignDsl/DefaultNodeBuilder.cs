using Markdig.Syntax;

namespace Atelia.DesignDsl;

/// <summary>
/// 默认节点构建器（兜底实现）。
/// 总是返回普通 AxtNode，作为职责链的最后一个 Builder。
/// </summary>
public sealed class DefaultNodeBuilder : INodeBuilder {
    /// <summary>
    /// 构建普通 AxtNode。此方法始终返回非 null，作为兜底。
    /// </summary>
    /// <param name="heading">ATX Heading Block。</param>
    /// <param name="content">该 Heading 下辖的块级内容。</param>
    /// <param name="originalMarkdown">原始 Markdown 字符串（兜底实现不使用）。</param>
    /// <returns>普通 AxtNode。</returns>
    public AxtNode? TryBuild(HeadingBlock heading, IReadOnlyList<Block> content, string originalMarkdown) {
        return new AxtNode(heading, content);
    }
}
