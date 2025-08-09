using MemoTree.Core.Types;

namespace MemoTree.Core.Storage.Interfaces
{
    /// <summary>
    /// 节点内容存储接口
    /// 支持多级LOD内容的存储和检索
    /// </summary>
    public interface INodeContentStorage
    {
        /// <summary>
        /// 获取节点内容
        /// </summary>
        /// <param name="nodeId">节点ID</param>
        /// <param name="level">LOD级别</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>节点内容，如果不存在则返回null</returns>
        Task<NodeContent?> GetAsync(
            NodeId nodeId, 
            LodLevel level, 
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 保存节点内容
        /// </summary>
        /// <param name="content">节点内容</param>
        /// <param name="cancellationToken">取消令牌</param>
        Task SaveAsync(NodeContent content, CancellationToken cancellationToken = default);

        /// <summary>
        /// 删除节点内容
        /// </summary>
        /// <param name="nodeId">节点ID</param>
        /// <param name="level">LOD级别</param>
        /// <param name="cancellationToken">取消令牌</param>
        Task DeleteAsync(
            NodeId nodeId, 
            LodLevel level, 
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 删除节点的所有内容
        /// </summary>
        /// <param name="nodeId">节点ID</param>
        /// <param name="cancellationToken">取消令牌</param>
        Task DeleteAllAsync(NodeId nodeId, CancellationToken cancellationToken = default);

        /// <summary>
        /// 获取节点的所有内容级别
        /// </summary>
        /// <param name="nodeId">节点ID</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>LOD级别到内容的映射</returns>
        Task<IReadOnlyDictionary<LodLevel, NodeContent>> GetAllLevelsAsync(
            NodeId nodeId, 
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 获取节点可用的LOD级别
        /// </summary>
        /// <param name="nodeId">节点ID</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>可用的LOD级别列表</returns>
        Task<IReadOnlyList<LodLevel>> GetAvailableLevelsAsync(
            NodeId nodeId, 
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 检查指定级别的内容是否存在
        /// </summary>
        /// <param name="nodeId">节点ID</param>
        /// <param name="level">LOD级别</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>如果内容存在则返回true</returns>
        Task<bool> ExistsAsync(
            NodeId nodeId, 
            LodLevel level, 
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 批量获取节点内容
        /// </summary>
        /// <param name="requests">内容请求列表（节点ID和LOD级别的组合）</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>请求到内容的映射</returns>
        Task<IReadOnlyDictionary<(NodeId NodeId, LodLevel Level), NodeContent>> GetBatchAsync(
            IEnumerable<(NodeId NodeId, LodLevel Level)> requests,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 获取内容大小统计
        /// </summary>
        /// <param name="nodeId">节点ID</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>各级别内容的大小统计</returns>
        Task<IReadOnlyDictionary<LodLevel, long>> GetContentSizeStatsAsync(
            NodeId nodeId, 
            CancellationToken cancellationToken = default);
    }
}
