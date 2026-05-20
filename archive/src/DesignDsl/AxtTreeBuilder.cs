namespace Atelia.DesignDsl;

/// <summary>
/// ATX 标题树构建器。
/// 使用深度栈算法从分段结果构建完整的树结构。
/// </summary>
public static class AxtTreeBuilder {
    /// <summary>
    /// 从分段结果构建 ATX 标题树。
    /// </summary>
    /// <param name="sections">Block 序列分段结果（来自 AtxSectionSplitter）。</param>
    /// <param name="pipeline">节点构建器职责链。</param>
    /// <returns>构建的 ATX 标题树。</returns>
    /// <remarks>
    /// <para>嵌套规则（ATX-Tree）：</para>
    /// <list type="bullet">
    /// <item>RootNode 是隐式根，Depth=0</item>
    /// <item>对于任意 AxtNode X，其父节点 Y 是 X 上方首个 Depth 更小的节点</item>
    /// <item>如果 X 前面没有 Depth 更小的节点，则 Y 是 RootNode</item>
    /// </list>
    /// <para>算法：使用栈维护当前祖先链。遇到新节点时：</para>
    /// <list type="number">
    /// <item>弹出栈顶所有 Depth &gt;= 当前节点的节点</item>
    /// <item>栈顶节点作为父节点</item>
    /// <item>调用 parent.AddChild(newNode) 建立关系</item>
    /// <item>将新节点压栈</item>
    /// </list>
    /// </remarks>
    public static AxtTree Build(AtxSectionResult sections, NodeBuilderPipeline pipeline) {
        // 1. 创建 RootNode，Content = AtxSectionResult.Preface
        var root = new RootNode(sections.Preface);

        // 2. 收集所有节点（按文档顺序）
        var allNodes = new List<HeadingNode> { root };

        // 3. 使用栈维护祖先链（栈底是 RootNode）
        var ancestorStack = new Stack<HeadingNode>();
        ancestorStack.Push(root);

        // 4. 遍历所有 AtxSection，转换为 AxtNode 并建立父子关系
        foreach (var section in sections.Sections) {
            // 使用 pipeline 构建节点（HeadingText 已预存在 section 中）
            var node = pipeline.Build(section);

            // 弹出栈顶所有 Depth >= 当前节点的节点
            // 这样栈顶节点就是首个 Depth 更小的祖先
            while (ancestorStack.Count > 0 && ancestorStack.Peek().Depth >= node.Depth) {
                ancestorStack.Pop();
            }

            // 栈顶节点作为父节点（如果栈空则用 root，但由于 root 在栈底，栈不会空）
            var parent = ancestorStack.Count > 0 ? ancestorStack.Peek() : root;
            parent.AddChild(node);

            // 将新节点压栈（成为后续节点的潜在祖先）
            ancestorStack.Push(node);

            // 收集到 AllNodes
            allNodes.Add(node);
        }

        return new AxtTree(root, allNodes);
    }
}
