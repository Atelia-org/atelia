using MemoTree.Core.Types;

namespace MemoTree.Core.Storage.Relations
{
    /// <summary>
    /// 关系管理服务接口
    /// 提供高级的关系操作和分析功能
    /// </summary>
    public interface IRelationManagementService
    {
        /// <summary>
        /// 创建关系图
        /// </summary>
        /// <param name="rootNodeId">根节点ID</param>
        /// <param name="maxDepth">最大深度</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>关系图</returns>
        Task<RelationGraph> BuildRelationGraphAsync(
            NodeId rootNodeId, 
            int maxDepth = 3, 
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 查找节点间的路径
        /// </summary>
        /// <param name="fromNodeId">起始节点ID</param>
        /// <param name="toNodeId">目标节点ID</param>
        /// <param name="maxDepth">最大搜索深度</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>关系路径，如果不存在则返回null</returns>
        Task<RelationPath?> FindPathAsync(
            NodeId fromNodeId, 
            NodeId toNodeId, 
            int maxDepth = 5, 
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 获取关系统计信息
        /// </summary>
        /// <param name="nodeId">节点ID</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>关系统计信息</returns>
        Task<RelationStatistics> GetRelationStatisticsAsync(
            NodeId nodeId, 
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 批量创建关系
        /// </summary>
        /// <param name="requests">创建关系请求集合</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>新创建的关系ID列表</returns>
        Task<IReadOnlyList<RelationId>> CreateRelationsBatchAsync(
            IEnumerable<CreateRelationRequest> requests, 
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 验证关系的一致性
        /// </summary>
        /// <param name="nodeId">节点ID</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>验证结果</returns>
        Task<RelationValidationResult> ValidateRelationsAsync(
            NodeId nodeId, 
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 分析关系模式
        /// </summary>
        /// <param name="nodeIds">要分析的节点ID集合</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>关系模式分析结果</returns>
        Task<RelationPatternAnalysis> AnalyzeRelationPatternsAsync(
            IEnumerable<NodeId> nodeIds, 
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 查找相似节点（基于关系模式）
        /// </summary>
        /// <param name="nodeId">参考节点ID</param>
        /// <param name="maxResults">最大结果数量</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>相似节点列表（按相似度排序）</returns>
        Task<IReadOnlyList<(NodeId NodeId, double Similarity)>> FindSimilarNodesAsync(
            NodeId nodeId,
            int maxResults = 10,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 获取节点的影响力分析
        /// </summary>
        /// <param name="nodeId">节点ID</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>影响力分析结果</returns>
        Task<NodeInfluenceAnalysis> GetNodeInfluenceAsync(
            NodeId nodeId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 检测关系图中的社区结构
        /// </summary>
        /// <param name="nodeIds">要分析的节点ID集合</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>社区检测结果</returns>
        Task<CommunityDetectionResult> DetectCommunitiesAsync(
            IEnumerable<NodeId> nodeIds,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// 节点影响力分析结果
    /// </summary>
    public record NodeInfluenceAnalysis
    {
        public NodeId NodeId { get; init; }
        public double CentralityScore { get; init; }
        public double PageRankScore { get; init; }
        public int DirectConnections { get; init; }
        public int IndirectConnections { get; init; }
        public IReadOnlyList<NodeId> MostInfluentialConnections { get; init; } = Array.Empty<NodeId>();
    }

    /// <summary>
    /// 社区检测结果
    /// </summary>
    public record CommunityDetectionResult
    {
        public IReadOnlyList<Community> Communities { get; init; } = Array.Empty<Community>();
        public double Modularity { get; init; }
        public DateTime AnalyzedAt { get; init; } = DateTime.UtcNow;
    }

    /// <summary>
    /// 社区定义
    /// </summary>
    public record Community
    {
        public string Id { get; init; } = string.Empty;
        public IReadOnlySet<NodeId> Members { get; init; } = new HashSet<NodeId>();
        public double Density { get; init; }
        public NodeId? CentralNode { get; init; }
    }
}
