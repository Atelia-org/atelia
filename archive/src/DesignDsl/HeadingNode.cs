using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Atelia.DesignDsl;

/// <summary>
/// 标题节点基类。
/// 表示 ATX-Tree 中的一个节点，包含标题内容、块级内容、深度和父子关系。
/// </summary>
public abstract class HeadingNode {
    private readonly List<HeadingNode> _children = [];

    /// <summary>
    /// 创建 HeadingNode。
    /// </summary>
    /// <param name="heading">标题内容（RootNode 为 null）。</param>
    /// <param name="content">下辖的块级内容。</param>
    /// <param name="depth">节点深度（RootNode=0, AxtNode=HeadingBlock.Level）。</param>
    protected HeadingNode(ContainerInline? heading, IReadOnlyList<Block> content, int depth) {
        Heading = heading;
        Content = content;
        Depth = depth;
    }

    /// <summary>
    /// 标题内容（RootNode 为 null）。
    /// </summary>
    public ContainerInline? Heading { get; }

    /// <summary>
    /// 下辖的块级内容。
    /// </summary>
    public IReadOnlyList<Block> Content { get; }

    /// <summary>
    /// 节点深度（RootNode=0, AxtNode=HeadingBlock.Level）。
    /// </summary>
    public int Depth { get; }

    /// <summary>
    /// 父节点（RootNode 和顶层节点为 null）。
    /// </summary>
    public HeadingNode? Parent { get; internal set; }

    /// <summary>
    /// 子节点列表。
    /// </summary>
    public IReadOnlyList<HeadingNode> Children => _children;

    /// <summary>
    /// 添加子节点并设置其 Parent。
    /// </summary>
    /// <param name="child">要添加的子节点。</param>
    internal void AddChild(HeadingNode child) {
        child.Parent = this;
        _children.Add(child);
    }
}
