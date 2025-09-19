using MemoTree.Core.Types;

namespace MemoTree.Core.Storage.Interfaces {
    /// <summary>
    /// 语义关系存储接口（集中存储版本，不包括父子关系）
    /// 管理节点间的语义关系数据
    /// </summary>
    public interface INodeRelationStorage {
        /// <summary>
        /// 获取节点的所有出向语义关系
        /// </summary>
        /// <param name="nodeId">节点ID</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>出向关系列表</returns>
        Task<IReadOnlyList<NodeRelation>> GetOutgoingRelationsAsync(
            NodeId nodeId,
            CancellationToken cancellationToken = default
        );

        /// <summary>
        /// 获取节点的所有入向语义关系
        /// </summary>
        /// <param name="nodeId">节点ID</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>入向关系列表</returns>
        Task<IReadOnlyList<NodeRelation>> GetIncomingRelationsAsync(
            NodeId nodeId,
            CancellationToken cancellationToken = default
        );

        /// <summary>
        /// 获取节点的所有语义关系（入向+出向）
        /// </summary>
        /// <param name="nodeId">节点ID</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>所有关系列表</returns>
        Task<IReadOnlyList<NodeRelation>> GetAllRelationsAsync(
            NodeId nodeId,
            CancellationToken cancellationToken = default
        );

        /// <summary>
        /// 根据关系ID获取语义关系
        /// </summary>
        /// <param name="relationId">关系ID</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>关系对象，如果不存在则返回null</returns>
        Task<NodeRelation?> GetRelationAsync(
            RelationId relationId,
            CancellationToken cancellationToken = default
        );

        /// <summary>
        /// 添加语义关系
        /// </summary>
        /// <param name="sourceId">源节点ID</param>
        /// <param name="targetId">目标节点ID</param>
        /// <param name="relationType">关系类型</param>
        /// <param name="description">关系描述</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>新创建的关系ID</returns>
        Task<RelationId> AddRelationAsync(
            NodeId sourceId,
            NodeId targetId,
            RelationType relationType,
            string description = "",
            CancellationToken cancellationToken = default
        );

        /// <summary>
        /// 更新语义关系
        /// </summary>
        /// <param name="relationId">关系ID</param>
        /// <param name="updateAction">更新操作</param>
        /// <param name="cancellationToken">取消令牌</param>
        Task UpdateRelationAsync(
            RelationId relationId,
            Action<NodeRelation> updateAction,
            CancellationToken cancellationToken = default
        );

        /// <summary>
        /// 移除语义关系
        /// </summary>
        /// <param name="relationId">关系ID</param>
        /// <param name="cancellationToken">取消令牌</param>
        Task RemoveRelationAsync(RelationId relationId, CancellationToken cancellationToken = default);

        /// <summary>
        /// 批量获取语义关系
        /// </summary>
        /// <param name="relationIds">关系ID集合</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>关系ID到关系对象的映射</returns>
        Task<IReadOnlyDictionary<RelationId, NodeRelation>> GetRelationsBatchAsync(
            IEnumerable<RelationId> relationIds,
            CancellationToken cancellationToken = default
        );

        /// <summary>
        /// 查找特定类型的语义关系
        /// </summary>
        /// <param name="relationType">关系类型</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>指定类型的所有关系</returns>
        Task<IReadOnlyList<NodeRelation>> FindRelationsByTypeAsync(
            RelationType relationType,
            CancellationToken cancellationToken = default
        );

        /// <summary>
        /// 查找两个节点之间的语义关系
        /// </summary>
        /// <param name="sourceId">源节点ID</param>
        /// <param name="targetId">目标节点ID</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>两节点间的所有关系</returns>
        Task<IReadOnlyList<NodeRelation>> FindRelationsBetweenAsync(
            NodeId sourceId,
            NodeId targetId,
            CancellationToken cancellationToken = default
        );

        /// <summary>
        /// 异步枚举所有语义关系
        /// </summary>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>所有关系的异步枚举</returns>
        IAsyncEnumerable<NodeRelation> GetAllRelationsAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// 检查关系是否存在
        /// </summary>
        /// <param name="relationId">关系ID</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>如果关系存在则返回true</returns>
        Task<bool> ExistsAsync(RelationId relationId, CancellationToken cancellationToken = default);

        /// <summary>
        /// 获取关系总数
        /// </summary>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>关系总数</returns>
        Task<int> GetCountAsync(CancellationToken cancellationToken = default);
    }
}
