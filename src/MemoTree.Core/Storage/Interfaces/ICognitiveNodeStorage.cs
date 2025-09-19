using MemoTree.Core.Types;

namespace MemoTree.Core.Storage.Interfaces {
    /// <summary>
    /// 复合存储接口（组合所有存储功能）
    /// 提供统一的认知节点数据访问接口
    /// </summary>
    public interface ICognitiveNodeStorage : INodeMetadataStorage, INodeContentStorage, INodeHierarchyStorage {
        /// <summary>
        /// 获取完整节点
        /// </summary>
        /// <param name="nodeId">节点ID</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>完整的认知节点，如果不存在则返回null</returns>
        Task<CognitiveNode?> GetCompleteNodeAsync(
            NodeId nodeId,
            CancellationToken cancellationToken = default
        );

        /// <summary>
        /// 保存完整节点
        /// </summary>
        /// <param name="node">认知节点</param>
        /// <param name="cancellationToken">取消令牌</param>
        Task SaveCompleteNodeAsync(CognitiveNode node, CancellationToken cancellationToken = default);

        /// <summary>
        /// 删除完整节点（包括元数据、内容和层次关系）
        /// </summary>
        /// <param name="nodeId">节点ID</param>
        /// <param name="cancellationToken">取消令牌</param>
        Task DeleteCompleteNodeAsync(NodeId nodeId, CancellationToken cancellationToken = default);

        /// <summary>
        /// 事务性操作
        /// </summary>
        /// <typeparam name="T">返回值类型</typeparam>
        /// <param name="operation">事务操作</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>操作结果</returns>
        Task<T> ExecuteInTransactionAsync<T>(
            Func<ICognitiveNodeStorage, CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken = default
        );

        /// <summary>
        /// 事务性操作（无返回值）
        /// </summary>
        /// <param name="operation">事务操作</param>
        /// <param name="cancellationToken">取消令牌</param>
        Task ExecuteInTransactionAsync(
            Func<ICognitiveNodeStorage, CancellationToken, Task> operation,
            CancellationToken cancellationToken = default
        );

        /// <summary>
        /// 批量获取完整节点
        /// </summary>
        /// <param name="nodeIds">节点ID集合</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>节点ID到完整节点的映射</returns>
        Task<IReadOnlyDictionary<NodeId, CognitiveNode>> GetCompleteNodesBatchAsync(
            IEnumerable<NodeId> nodeIds,
            CancellationToken cancellationToken = default
        );

        /// <summary>
        /// 批量保存完整节点
        /// </summary>
        /// <param name="nodes">认知节点集合</param>
        /// <param name="cancellationToken">取消令牌</param>
        Task SaveCompleteNodesBatchAsync(
            IEnumerable<CognitiveNode> nodes,
            CancellationToken cancellationToken = default
        );

        /// <summary>
        /// 创建新节点
        /// </summary>
        /// <param name="metadata">节点元数据</param>
        /// <param name="content">节点内容（可选）</param>
        /// <param name="parentId">父节点ID（可选）</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>新创建的节点ID</returns>
        Task<NodeId> CreateNodeAsync(
            NodeMetadata metadata,
            NodeContent? content = null,
            NodeId? parentId = null,
            CancellationToken cancellationToken = default
        );

        /// <summary>
        /// 复制节点（包括子树）
        /// </summary>
        /// <param name="sourceId">源节点ID</param>
        /// <param name="targetParentId">目标父节点ID（可选）</param>
        /// <param name="includeSubtree">是否包括子树</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>新复制的根节点ID</returns>
        Task<NodeId> CopyNodeAsync(
            NodeId sourceId,
            NodeId? targetParentId = null,
            bool includeSubtree = true,
            CancellationToken cancellationToken = default
        );

        /// <summary>
        /// 获取存储统计信息
        /// </summary>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>存储统计信息</returns>
        Task<StorageStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// 验证存储完整性
        /// </summary>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>验证结果</returns>
        Task<StorageIntegrityResult> ValidateIntegrityAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// 存储统计信息
    /// </summary>
    public record StorageStatistics {
        public int TotalNodes {
            get; init;
        }
        public int TotalRelations {
            get; init;
        }
        public long TotalContentSize {
            get; init;
        }
        public int MaxDepth {
            get; init;
        }
        public DateTime LastModified {
            get; init;
        }
        public IReadOnlyDictionary<NodeType, int> NodeTypeDistribution {
            get; init;
        } =
        new Dictionary<NodeType, int>();
        public IReadOnlyDictionary<LodLevel, int> ContentLevelDistribution {
            get; init;
        } =
        new Dictionary<LodLevel, int>();
    }

    /// <summary>
    /// 存储完整性验证结果
    /// </summary>
    public record StorageIntegrityResult {
        public bool IsValid {
            get; init;
        }
        public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
        public DateTime ValidatedAt { get; init; } = DateTime.UtcNow;
    }
}
