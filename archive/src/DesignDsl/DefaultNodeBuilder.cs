namespace Atelia.DesignDsl;

/// <summary>
/// 默认节点构建器（兆底实现）。
/// 总是返回普通 AxtNode，作为职责链的最后一个 Builder。
/// </summary>
public sealed class DefaultNodeBuilder : INodeBuilder {
    /// <summary>
    /// 构建普通 AxtNode。此方法始终返回非 null，作为兆底。
    /// </summary>
    /// <param name="section">ATX Section。</param>
    /// <returns>普通 AxtNode。</returns>
    public AxtNode? TryBuild(AtxSection section) {
        return new AxtNode(section.Heading, section.Content);
    }
}
