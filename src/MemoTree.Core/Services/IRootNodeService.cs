using MemoTree.Core.Types;

namespace MemoTree.Core.Services
{
    /// <summary>
    /// 根节点判断服务接口
    /// 提供基于存储层的根节点判断机制，支持缓存优化
    /// </summary>
    public interface IRootNodeService
    {
        /// <summary>
        /// 检查指定节点是否为根节点
        /// </summary>
        /// <param name="nodeId">节点ID</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>如果是根节点则返回true</returns>
        Task<bool> IsRootNodeAsync(NodeId nodeId, CancellationToken cancellationToken = default);

        /// <summary>
        /// 获取所有根节点
        /// </summary>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>所有根节点ID列表</returns>
        Task<IReadOnlyList<NodeId>> GetRootNodesAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// 批量检查多个节点是否为根节点
        /// </summary>
        /// <param name="nodeIds">节点ID列表</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>节点ID到根节点状态的映射</returns>
        Task<IReadOnlyDictionary<NodeId, bool>> IsRootNodeBatchAsync(
            IEnumerable<NodeId> nodeIds, 
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 清除根节点缓存
        /// 在层次结构发生变化时调用
        /// </summary>
        void InvalidateCache();
    }
}
