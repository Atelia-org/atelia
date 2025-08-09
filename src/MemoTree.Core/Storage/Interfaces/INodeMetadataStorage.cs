using MemoTree.Core.Types;

namespace MemoTree.Core.Storage.Interfaces
{
    /// <summary>
    /// 节点元数据存储接口
    /// 提供节点基础信息的CRUD操作
    /// </summary>
    public interface INodeMetadataStorage
    {
        /// <summary>
        /// 获取节点元数据
        /// </summary>
        /// <param name="nodeId">节点ID</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>节点元数据，如果不存在则返回null</returns>
        Task<NodeMetadata?> GetAsync(NodeId nodeId, CancellationToken cancellationToken = default);

        /// <summary>
        /// 保存节点元数据
        /// </summary>
        /// <param name="metadata">节点元数据</param>
        /// <param name="cancellationToken">取消令牌</param>
        Task SaveAsync(NodeMetadata metadata, CancellationToken cancellationToken = default);

        /// <summary>
        /// 删除节点元数据
        /// </summary>
        /// <param name="nodeId">节点ID</param>
        /// <param name="cancellationToken">取消令牌</param>
        Task DeleteAsync(NodeId nodeId, CancellationToken cancellationToken = default);

        /// <summary>
        /// 批量获取元数据
        /// </summary>
        /// <param name="nodeIds">节点ID集合</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>节点ID到元数据的映射</returns>
        Task<IReadOnlyDictionary<NodeId, NodeMetadata>> GetBatchAsync(
            IEnumerable<NodeId> nodeIds, 
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 异步枚举所有元数据
        /// </summary>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>所有节点元数据的异步枚举</returns>
        IAsyncEnumerable<NodeMetadata> GetAllAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// 检查节点是否存在
        /// </summary>
        /// <param name="nodeId">节点ID</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>如果节点存在则返回true</returns>
        Task<bool> ExistsAsync(NodeId nodeId, CancellationToken cancellationToken = default);

        /// <summary>
        /// 获取节点数量
        /// </summary>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>节点总数</returns>
        Task<int> GetCountAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// 根据节点类型查找节点
        /// </summary>
        /// <param name="nodeType">节点类型</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>指定类型的所有节点元数据</returns>
        IAsyncEnumerable<NodeMetadata> FindByTypeAsync(
            NodeType nodeType, 
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 根据标签查找节点
        /// </summary>
        /// <param name="tag">标签</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>包含指定标签的所有节点元数据</returns>
        IAsyncEnumerable<NodeMetadata> FindByTagAsync(
            string tag, 
            CancellationToken cancellationToken = default);
    }
}
