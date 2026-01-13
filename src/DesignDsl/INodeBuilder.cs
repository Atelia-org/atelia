namespace Atelia.DesignDsl;

/// <summary>
/// 节点构建器接口（职责链模式）。
/// 检查 AtxSection 是否属于其职能范围，返回对应类型的 AxtNode。
/// </summary>
public interface INodeBuilder {
    /// <summary>
    /// 尝试构建节点。能处理返回节点，不能处理返回 null。
    /// </summary>
    /// <param name="section">ATX Section，包含 Heading、Content 和 HeadingText。</param>
    /// <returns>构建的节点，或 null 表示不能处理。</returns>
    AxtNode? TryBuild(AtxSection section);
}
