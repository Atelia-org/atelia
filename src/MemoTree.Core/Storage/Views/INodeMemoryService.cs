using MemoTree.Core.Types;

namespace MemoTree.Core.Storage.Views {
    /// <summary>
    /// 节点内存服务接口
    /// 提供节点数据的内存管理和快速访问功能
    /// </summary>
    public interface INodeMemoryService {
        /// <summary>
        /// 检查节点是否已加载到内存
        /// </summary>
        /// <param name="nodeId">节点ID</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>如果节点已加载则返回true</returns>
        Task<bool> IsNodeLoadedAsync(NodeId nodeId, CancellationToken cancellationToken = default);

        /// <summary>
        /// 获取已加载节点的数量
        /// </summary>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>已加载节点数量</returns>
        Task<int> GetLoadedNodeCountAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// 获取节点内存使用统计
        /// </summary>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>节点内存统计信息</returns>
        Task<NodeMemoryStats> GetNodeMemoryStatsAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// 预加载相关节点到内存（Phase 5可选实现）
        /// </summary>
        /// <param name="nodeId">中心节点ID</param>
        /// <param name="depth">预加载深度</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>预加载的节点数量</returns>
        Task<int> PreloadRelatedNodesAsync(
            NodeId nodeId,
            int depth = 1,
            CancellationToken cancellationToken = default
        );

        /// <summary>
        /// 批量检查节点加载状态
        /// </summary>
        /// <param name="nodeIds">节点ID集合</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>节点ID到加载状态的映射</returns>
        Task<IReadOnlyDictionary<NodeId, bool>> CheckMultipleNodesLoadedAsync(
            IEnumerable<NodeId> nodeIds,
            CancellationToken cancellationToken = default
        );

        /// <summary>
        /// 获取内存中所有已加载节点的ID列表
        /// </summary>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>已加载节点ID列表</returns>
        Task<IReadOnlyList<NodeId>> GetLoadedNodeIdsAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// 强制加载节点到内存
        /// </summary>
        /// <param name="nodeId">节点ID</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>如果成功加载则返回true</returns>
        Task<bool> ForceLoadNodeAsync(NodeId nodeId, CancellationToken cancellationToken = default);

        /// <summary>
        /// 从内存中卸载节点
        /// </summary>
        /// <param name="nodeId">节点ID</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>如果成功卸载则返回true</returns>
        Task<bool> UnloadNodeAsync(NodeId nodeId, CancellationToken cancellationToken = default);

        /// <summary>
        /// 批量加载节点到内存
        /// </summary>
        /// <param name="nodeIds">节点ID集合</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>成功加载的节点数量</returns>
        Task<int> BatchLoadNodesAsync(
            IEnumerable<NodeId> nodeIds,
            CancellationToken cancellationToken = default
        );

        /// <summary>
        /// 获取节点的内存占用详情
        /// </summary>
        /// <param name="nodeId">节点ID</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>节点内存详情，如果节点未加载则返回null</returns>
        Task<NodeMemoryDetail?> GetNodeMemoryDetailAsync(
            NodeId nodeId,
            CancellationToken cancellationToken = default
        );

        /// <summary>
        /// 获取内存占用最大的节点排名
        /// </summary>
        /// <param name="topCount">返回前N个节点</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>按内存占用排序的节点列表</returns>
        Task<IReadOnlyList<NodeMemoryRanking>> GetTopMemoryConsumingNodesAsync(
            int topCount = 10,
            CancellationToken cancellationToken = default
        );

        /// <summary>
        /// 清理长时间未访问的节点
        /// </summary>
        /// <param name="unusedThreshold">未访问时间阈值</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>清理的节点数量</returns>
        Task<int> CleanupUnusedNodesAsync(
            TimeSpan unusedThreshold,
            CancellationToken cancellationToken = default
        );

        /// <summary>
        /// 获取节点访问统计
        /// </summary>
        /// <param name="nodeId">节点ID</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>节点访问统计，如果节点未加载则返回null</returns>
        Task<NodeAccessStats?> GetNodeAccessStatsAsync(
            NodeId nodeId,
            CancellationToken cancellationToken = default
        );
    }

    /// <summary>
    /// 节点内存统计信息
    /// </summary>
    public record NodeMemoryStats {
        /// <summary>
        /// 已加载节点数量
        /// </summary>
        public int LoadedNodeCount {
            get; init;
        }

        /// <summary>
        /// 节点数据占用内存字节数
        /// </summary>
        public long NodeMemoryBytes {
            get; init;
        }

        /// <summary>
        /// 平均每个节点的内存占用
        /// </summary>
        public double AverageNodeMemoryBytes => LoadedNodeCount > 0 ? (double)NodeMemoryBytes / LoadedNodeCount : 0;

        /// <summary>
        /// 最大节点内存占用
        /// </summary>
        public long MaxNodeMemoryBytes {
            get; init;
        }

        /// <summary>
        /// 最小节点内存占用
        /// </summary>
        public long MinNodeMemoryBytes {
            get; init;
        }

        /// <summary>
        /// 统计时间
        /// </summary>
        public DateTime StatisticsTime { get; init; } = DateTime.UtcNow;

        /// <summary>
        /// 内存碎片率
        /// </summary>
        public double FragmentationRate {
            get; init;
        }

        /// <summary>
        /// 按节点类型分组的内存统计
        /// </summary>
        public IReadOnlyDictionary<NodeType, long> MemoryByNodeType {
            get; init;
        } =
        new Dictionary<NodeType, long>();

        /// <summary>
        /// 按LOD级别分组的内存统计
        /// </summary>
        public IReadOnlyDictionary<LodLevel, long> MemoryByLodLevel {
            get; init;
        } =
        new Dictionary<LodLevel, long>();
    }

    /// <summary>
    /// 节点内存占用详情
    /// </summary>
    public record NodeMemoryDetail {
        public NodeId NodeId {
            get; init;
        }
        public long MemoryBytes {
            get; init;
        }
        public NodeType NodeType {
            get; init;
        }
        public DateTime LoadedAt {
            get; init;
        }
        public DateTime LastAccessed {
            get; init;
        }
        public int AccessCount {
            get; init;
        }
        public IReadOnlyDictionary<LodLevel, long> MemoryByLevel {
            get; init;
        } =
        new Dictionary<LodLevel, long>();
    }

    /// <summary>
    /// 节点内存占用排名
    /// </summary>
    public record NodeMemoryRanking {
        public int Rank {
            get; init;
        }
        public NodeId NodeId {
            get; init;
        }
        public long MemoryBytes {
            get; init;
        }
        public double PercentageOfTotal {
            get; init;
        }
        public NodeType NodeType {
            get; init;
        }
    }

    /// <summary>
    /// 节点访问统计
    /// </summary>
    public record NodeAccessStats {
        public NodeId NodeId {
            get; init;
        }
        public int TotalAccesses {
            get; init;
        }
        public DateTime FirstAccessed {
            get; init;
        }
        public DateTime LastAccessed {
            get; init;
        }
        public double AccessFrequency {
            get; init;
        } // 每小时访问次数
        public TimeSpan AverageAccessInterval {
            get; init;
        }
        public IReadOnlyList<DateTime> RecentAccesses { get; init; } = Array.Empty<DateTime>();
    }
}
