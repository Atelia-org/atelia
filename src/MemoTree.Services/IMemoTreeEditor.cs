using MemoTree.Core.Types;

namespace MemoTree.Services;

/// <summary>
/// MemoTree编辑器接口 (MVP版本)
/// 提供节点的创建、更新、删除等编辑操作
/// </summary>
public interface IMemoTreeEditor
{
    /// <summary>
    /// 创建新节点
    /// </summary>
    /// <param name="title">节点标题</param>
    /// <param name="content">节点内容，默认为空</param>
    /// <param name="parentId">父节点ID，null表示添加到根节点</param>
    /// <param name="type">节点类型，默认为Concept</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>新创建的节点ID</returns>
    Task<NodeId> CreateNodeAsync(
        string title, 
        string content = "", 
        NodeId? parentId = null, 
        NodeType type = NodeType.Concept,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 更新节点内容
    /// </summary>
    /// <param name="nodeId">节点ID</param>
    /// <param name="content">新的内容</param>
    /// <param name="cancellationToken">取消令牌</param>
    Task UpdateNodeContentAsync(NodeId nodeId, string content, CancellationToken cancellationToken = default);

    /// <summary>
    /// 更新节点标题
    /// </summary>
    /// <param name="nodeId">节点ID</param>
    /// <param name="title">新的标题</param>
    /// <param name="cancellationToken">取消令牌</param>
    Task UpdateNodeTitleAsync(NodeId nodeId, string title, CancellationToken cancellationToken = default);

    /// <summary>
    /// 删除节点
    /// </summary>
    /// <param name="nodeId">要删除的节点ID</param>
    /// <param name="recursive">是否递归删除子节点，默认为false</param>
    /// <param name="cancellationToken">取消令牌</param>
    Task DeleteNodeAsync(NodeId nodeId, bool recursive = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// 移动节点到新的父节点
    /// </summary>
    /// <param name="nodeId">要移动的节点ID</param>
    /// <param name="newParentId">新的父节点ID，null表示移动到根节点</param>
    /// <param name="newOrder">在新位置的排序，默认为0（添加到末尾）</param>
    /// <param name="cancellationToken">取消令牌</param>
    Task MoveNodeAsync(NodeId nodeId, NodeId? newParentId, int newOrder = 0, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取节点信息
    /// </summary>
    /// <param name="nodeId">节点ID</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>节点信息，如果不存在则返回null</returns>
    Task<CognitiveNode?> GetNodeAsync(NodeId nodeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 检查节点是否存在
    /// </summary>
    /// <param name="nodeId">节点ID</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>节点是否存在</returns>
    Task<bool> NodeExistsAsync(NodeId nodeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取节点的子节点列表
    /// </summary>
    /// <param name="parentId">父节点ID，null表示获取根节点的子节点</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>子节点列表</returns>
    Task<IReadOnlyList<CognitiveNode>> GetChildrenAsync(NodeId? parentId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取所有节点 (用于统计和搜索)
    /// </summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>所有节点列表</returns>
    Task<IReadOnlyList<CognitiveNode>> GetAllNodesAsync(CancellationToken cancellationToken = default);
}
