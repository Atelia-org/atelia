using MemoTree.Core.Types;
using MemoTree.Services.Models;

namespace MemoTree.Services;

/// <summary>
/// MemoTree核心服务接口 (MVP版本)
/// 提供视图渲染、节点操作和树结构管理功能
/// </summary>
public interface IMemoTreeService
{
    /// <summary>
    /// 渲染指定视图的Markdown内容
    /// </summary>
    /// <param name="viewName">视图名称，默认为"default"</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>渲染后的Markdown字符串</returns>
    Task<string> RenderViewAsync(string viewName = "default", CancellationToken cancellationToken = default);

    /// <summary>
    /// 展开节点到Full级别 (MVP简化版，忽略LodLevel参数)
    /// </summary>
    /// <param name="nodeId">要展开的节点ID</param>
    /// <param name="viewName">视图名称，默认为"default"</param>
    /// <param name="cancellationToken">取消令牌</param>
    Task ExpandNodeAsync(NodeId nodeId, string viewName = "default", CancellationToken cancellationToken = default);

    /// <summary>
    /// 折叠节点到Gist级别 (MVP简化版，只显示标题)
    /// </summary>
    /// <param name="nodeId">要折叠的节点ID</param>
    /// <param name="viewName">视图名称，默认为"default"</param>
    /// <param name="cancellationToken">取消令牌</param>
    Task CollapseNodeAsync(NodeId nodeId, string viewName = "default", CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取节点树结构
    /// </summary>
    /// <param name="rootId">根节点ID，null表示从系统根节点开始</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>节点树项列表</returns>
    Task<IReadOnlyList<NodeTreeItem>> GetNodeTreeAsync(NodeId? rootId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取视图统计信息
    /// </summary>
    /// <param name="viewName">视图名称，默认为"default"</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>视图统计信息</returns>
    Task<ViewStats> GetViewStatsAsync(string viewName = "default", CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取节点的当前渲染内容 (根据视图状态决定显示级别)
    /// </summary>
    /// <param name="nodeId">节点ID</param>
    /// <param name="viewName">视图名称，默认为"default"</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>节点的渲染内容</returns>
    Task<string> GetNodeRenderContentAsync(NodeId nodeId, string viewName = "default", CancellationToken cancellationToken = default);

    /// <summary>
    /// 通过标题查找节点 (支持精确匹配)
    /// </summary>
    /// <param name="title">节点标题</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>匹配的节点ID列表</returns>
    Task<IReadOnlyList<NodeId>> FindNodesByTitleAsync(string title, CancellationToken cancellationToken = default);

    /// <summary>
    /// 创建新的视图（若已存在则抛出异常）
    /// </summary>
    /// <param name="viewName">视图名称</param>
    /// <param name="description">可选描述</param>
    /// <param name="cancellationToken">取消令牌</param>
    Task CreateViewAsync(string viewName, string? description = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// 更新已存在视图的描述（若不存在则抛出异常）
    /// </summary>
    /// <param name="viewName">视图名称</param>
    /// <param name="description">新的描述</param>
    /// <param name="cancellationToken">取消令牌</param>
    Task UpdateViewDescriptionAsync(string viewName, string description, CancellationToken cancellationToken = default);
}

/// <summary>
/// 节点树项 (简化版)
/// </summary>
public record NodeTreeItem
{
    public NodeId Id { get; init; }
    public string Title { get; init; } = string.Empty;
    public NodeType Type { get; init; }
    public int Level { get; init; }
    public bool HasChildren { get; init; }
    public bool IsExpanded { get; init; }
    public int CharacterCount { get; init; }
    public int EstimatedExpandCharacters { get; init; }
    public IReadOnlyList<NodeTreeItem> Children { get; init; } = Array.Empty<NodeTreeItem>();
}
