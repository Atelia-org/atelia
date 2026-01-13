using Markdig.Syntax;

namespace Atelia.DesignDsl;

/// <summary>
/// 节点构建器职责链调度器。
/// 按顺序调用注册的 INodeBuilder，返回首个非 null 结果。
/// </summary>
public sealed class NodeBuilderPipeline {
    private readonly List<INodeBuilder> _builders;

    /// <summary>
    /// 创建 NodeBuilderPipeline，默认包含 DefaultNodeBuilder 作为兜底。
    /// </summary>
    public NodeBuilderPipeline() {
        _builders = [new DefaultNodeBuilder()];
    }

    /// <summary>
    /// 创建 NodeBuilderPipeline，使用指定的 Builder 列表。
    /// 如果列表末尾不是 DefaultNodeBuilder，会自动添加一个。
    /// </summary>
    /// <param name="builders">Builder 列表。</param>
    public NodeBuilderPipeline(IEnumerable<INodeBuilder> builders) {
        _builders = builders.ToList();

        // 确保最后一个是 DefaultNodeBuilder（兜底）
        if (_builders.Count == 0 || _builders[^1] is not DefaultNodeBuilder) {
            _builders.Add(new DefaultNodeBuilder());
        }
    }

    /// <summary>
    /// 注册的 Builder 列表（只读）。
    /// </summary>
    public IReadOnlyList<INodeBuilder> Builders => _builders;

    /// <summary>
    /// 在 DefaultNodeBuilder 之前插入一个 Builder。
    /// </summary>
    /// <param name="builder">要插入的 Builder。</param>
    public void InsertBefore(INodeBuilder builder) {
        // 在倒数第二个位置插入（DefaultNodeBuilder 保持在最后）
        _builders.Insert(_builders.Count - 1, builder);
    }

    /// <summary>
    /// 构建节点。按顺序调用 Builder，返回首个非 null 结果。
    /// </summary>
    /// <param name="heading">ATX Heading Block。</param>
    /// <param name="content">该 Heading 下辖的块级内容。</param>
    /// <param name="originalMarkdown">原始 Markdown 字符串（用于 Span 切片获取原始文本）。</param>
    /// <returns>构建的节点。由于有 DefaultNodeBuilder 兜底，始终返回非 null。</returns>
    public AxtNode Build(HeadingBlock heading, IReadOnlyList<Block> content, string originalMarkdown) {
        foreach (var builder in _builders) {
            var node = builder.TryBuild(heading, content, originalMarkdown);
            if (node is not null) { return node; }
        }

        // 不应该到达这里，因为 DefaultNodeBuilder 始终返回非 null
        // 但为了类型安全，这里仍然需要返回
        return new AxtNode(heading, content);
    }
}
