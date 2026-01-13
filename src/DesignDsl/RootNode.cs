using Markdig.Syntax;

namespace Atelia.DesignDsl;

/// <summary>
/// 隐式根节点（Depth=0）。
/// 承载 Preface 内容（YAML Front Matter 后、首个 ATX Heading 前的内容）。
/// </summary>
public sealed class RootNode : HeadingNode {
    /// <summary>
    /// 创建 RootNode。
    /// </summary>
    /// <param name="content">Preface 内容（来自 AtxSectionResult.Preface）。</param>
    public RootNode(IReadOnlyList<Block> content)
        : base(heading: null, content, depth: 0) {
    }

    /// <summary>
    /// RootNode 的 Parent 始终为 null。
    /// </summary>
    public new HeadingNode? Parent => null;
}
