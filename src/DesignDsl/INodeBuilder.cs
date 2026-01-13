using Markdig.Syntax;

namespace Atelia.DesignDsl;

/// <summary>
/// 节点构建器接口（职责链模式）。
/// 检查 HeadingBlock 是否属于其职能范围，返回对应类型的 AxtNode。
/// </summary>
public interface INodeBuilder {
    /// <summary>
    /// 尝试构建节点。能处理返回节点，不能处理返回 null。
    /// </summary>
    /// <param name="heading">ATX Heading Block。</param>
    /// <param name="content">该 Heading 下辖的块级内容。</param>
    /// <param name="originalMarkdown">原始 Markdown 字符串（用于 Span 切片获取原始文本）。</param>
    /// <returns>构建的节点，或 null 表示不能处理。</returns>
    AxtNode? TryBuild(HeadingBlock heading, IReadOnlyList<Block> content, string originalMarkdown);
}
