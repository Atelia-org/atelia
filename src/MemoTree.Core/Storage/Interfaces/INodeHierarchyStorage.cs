using MemoTree.Core.Types;

namespace MemoTree.Core.Storage.Interfaces
{
    /// <summary>
    /// 节点层次结构存储接口（基于Hierarchy文件夹的独立存储）
    /// 专门处理父子关系和树形结构
    /// </summary>
    public interface INodeHierarchyStorage
    {
        /// <summary>
        /// 获取父子关系信息
        /// </summary>
        /// <param name="parentId">父节点ID</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>父子关系信息，如果不存在则返回null</returns>
        Task<HierarchyInfo?> GetHierarchyInfoAsync(
            NodeId parentId, 
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 保存父子关系信息
        /// </summary>
        /// <param name="HierarchyInfo">父子关系信息</param>
        /// <param name="cancellationToken">取消令牌</param>
        Task SaveHierarchyInfoAsync(
            HierarchyInfo HierarchyInfo, 
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 获取子节点ID列表（有序）
        /// </summary>
        /// <param name="parentId">父节点ID</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>有序的子节点ID列表</returns>
        Task<IReadOnlyList<NodeId>> GetChildrenAsync(
            NodeId parentId, 
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 获取父节点ID（通过运行时索引）
        /// </summary>
        /// <param name="nodeId">节点ID</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>父节点ID，如果是根节点则返回null</returns>
        Task<NodeId?> GetParentAsync(NodeId nodeId, CancellationToken cancellationToken = default);

        /// <summary>
        /// 添加子节点
        /// </summary>
        /// <param name="parentId">父节点ID</param>
        /// <param name="childId">子节点ID</param>
        /// <param name="order">插入位置，null表示添加到末尾</param>
        /// <param name="cancellationToken">取消令牌</param>
        Task AddChildAsync(
            NodeId parentId, 
            NodeId childId, 
            int? order = null, 
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 移除子节点
        /// </summary>
        /// <param name="parentId">父节点ID</param>
        /// <param name="childId">子节点ID</param>
        /// <param name="cancellationToken">取消令牌</param>
        Task RemoveChildAsync(
            NodeId parentId, 
            NodeId childId, 
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 移动节点到新父节点
        /// </summary>
        /// <param name="nodeId">要移动的节点ID</param>
        /// <param name="newParentId">新父节点ID，null表示移动到根级别</param>
        /// <param name="newOrder">在新父节点中的位置，null表示添加到末尾</param>
        /// <param name="cancellationToken">取消令牌</param>
        Task MoveNodeAsync(
            NodeId nodeId, 
            NodeId? newParentId, 
            int? newOrder = null, 
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 重新排序子节点
        /// </summary>
        /// <param name="parentId">父节点ID</param>
        /// <param name="orderedChildIds">重新排序后的子节点ID列表</param>
        /// <param name="cancellationToken">取消令牌</param>
        Task ReorderChildrenAsync(
            NodeId parentId, 
            IReadOnlyList<NodeId> orderedChildIds, 
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 获取节点路径（从根到节点）
        /// </summary>
        /// <param name="nodeId">节点ID</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>从根节点到目标节点的路径</returns>
        Task<IReadOnlyList<NodeId>> GetPathAsync(
            NodeId nodeId, 
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 获取子树中的所有节点ID
        /// </summary>
        /// <param name="rootId">子树根节点ID</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>子树中所有节点ID的异步枚举</returns>
        IAsyncEnumerable<NodeId> GetDescendantsAsync(
            NodeId rootId, 
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 构建运行时反向索引（子节点到父节点的映射）
        /// </summary>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>子节点ID到父节点ID的映射</returns>
        Task<IReadOnlyDictionary<NodeId, NodeId>> BuildParentIndexAsync(
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 检查节点是否有子节点
        /// </summary>
        /// <param name="nodeId">节点ID</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>如果有子节点则返回true</returns>
        Task<bool> HasChildrenAsync(NodeId nodeId, CancellationToken cancellationToken = default);

        /// <summary>
        /// 获取节点的层级深度
        /// </summary>
        /// <param name="nodeId">节点ID</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>节点深度（根节点为0）</returns>
        Task<int> GetDepthAsync(NodeId nodeId, CancellationToken cancellationToken = default);

        /// <summary>
        /// 获取所有根节点
        /// </summary>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>所有根节点ID列表</returns>
        Task<IReadOnlyList<NodeId>> GetRootNodesAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// 检查是否存在循环引用
        /// </summary>
        /// <param name="parentId">父节点ID</param>
        /// <param name="childId">子节点ID</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>如果会产生循环引用则返回true</returns>
        Task<bool> WouldCreateCycleAsync(
            NodeId parentId, 
            NodeId childId, 
            CancellationToken cancellationToken = default);
    }
}
