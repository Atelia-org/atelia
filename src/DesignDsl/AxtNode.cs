using Markdig.Syntax;

namespace Atelia.DesignDsl;

/// <summary>
/// ATX 标题节点。
/// 对应 Markdown ATX Heading（# ~ ######），Depth 等于井号数量。
/// </summary>
public class AxtNode : HeadingNode {
    /// <summary>
    /// 创建 AxtNode。
    /// </summary>
    /// <param name="heading">原始 HeadingBlock。</param>
    /// <param name="content">下辖的块级内容。</param>
    public AxtNode(HeadingBlock heading, IReadOnlyList<Block> content)
        : base(heading.Inline, content, heading.Level) {
        SourceHeadingBlock = heading;
    }

    /// <summary>
    /// 原始 Markdig HeadingBlock 对象。
    /// </summary>
    public HeadingBlock SourceHeadingBlock { get; }
}
