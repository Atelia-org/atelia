namespace Atelia.DesignDsl;

/// <summary>
/// ATX 标题树。
/// 包含隐式根节点和所有节点的按文档顺序列表。
/// </summary>
public sealed class AxtTree {
    /// <summary>
    /// 创建 AxtTree。
    /// </summary>
    /// <param name="root">隐式根节点。</param>
    /// <param name="allNodes">按文档出现顺序的所有节点（RootNode 在首位）。</param>
    internal AxtTree(RootNode root, IReadOnlyList<HeadingNode> allNodes) {
        Root = root;
        AllNodes = allNodes;
    }

    /// <summary>
    /// 隐式根节点（Depth=0）。
    /// </summary>
    public RootNode Root { get; }

    /// <summary>
    /// 按文档出现顺序返回所有节点（RootNode 在首位）。
    /// </summary>
    public IReadOnlyList<HeadingNode> AllNodes { get; }
}
