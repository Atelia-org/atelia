using MemoTree.Core.Types;

namespace MemoTree.Core.Storage.Views
{
    /// <summary>
    /// 视图状态内存管理接口
    /// 提供视图状态的内存使用统计和管理功能
    /// </summary>
    public interface IViewStateMemoryManager
    {
        /// <summary>
        /// 获取内存使用统计
        /// </summary>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>内存使用统计信息</returns>
        Task<MemoryUsageStats> GetMemoryStatsAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// 获取视图状态数量统计
        /// </summary>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>视图状态统计信息</returns>
        Task<ViewStateStats> GetViewStateStatsAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// 预加载常用视图状态（Phase 5可选实现）
        /// </summary>
        /// <param name="cancellationToken">取消令牌</param>
        Task PreloadFrequentViewStatesAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// 清理未使用的视图状态（Phase 5可选实现）
        /// </summary>
        /// <param name="unusedThreshold">未使用时间阈值</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>清理的视图状态数量</returns>
        Task<int> CleanupUnusedViewStatesAsync(
            TimeSpan unusedThreshold, 
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 获取视图状态的内存占用详情
        /// </summary>
        /// <param name="viewName">视图名称</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>内存占用详情，如果视图不存在则返回null</returns>
        Task<ViewMemoryDetail?> GetViewMemoryDetailAsync(
            string viewName, 
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 获取所有视图的内存占用排名
        /// </summary>
        /// <param name="topCount">返回前N个视图</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>按内存占用排序的视图列表</returns>
        Task<IReadOnlyList<ViewMemoryRanking>> GetTopMemoryConsumingViewsAsync(
            int topCount = 10,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 强制垃圾回收并更新内存统计
        /// </summary>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>回收前后的内存使用对比</returns>
        Task<MemoryCleanupResult> ForceMemoryCleanupAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// 设置内存使用警告阈值
        /// </summary>
        /// <param name="warningThresholdBytes">警告阈值（字节）</param>
        /// <param name="criticalThresholdBytes">严重警告阈值（字节）</param>
        Task SetMemoryThresholdsAsync(long warningThresholdBytes, long criticalThresholdBytes);

        /// <summary>
        /// 检查当前内存使用是否超过阈值
        /// </summary>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>内存状态检查结果</returns>
        Task<MemoryHealthStatus> CheckMemoryHealthAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// 内存使用统计信息
    /// </summary>
    public record MemoryUsageStats
    {
        /// <summary>
        /// 视图状态占用内存字节数
        /// </summary>
        public long ViewStateMemoryBytes { get; init; }

        /// <summary>
        /// 节点状态数量
        /// </summary>
        public int NodeStateCount { get; init; }

        /// <summary>
        /// 画布状态数量
        /// </summary>
        public int ViewStateCount { get; init; }

        /// <summary>
        /// 最后更新时间
        /// </summary>
        public DateTime LastUpdated { get; init; } = DateTime.UtcNow;

        /// <summary>
        /// 平均每个状态的内存占用
        /// </summary>
        public double AverageStateMemoryBytes => NodeStateCount > 0 ? (double)ViewStateMemoryBytes / NodeStateCount : 0;

        /// <summary>
        /// 总内存使用量（包括系统开销）
        /// </summary>
        public long TotalMemoryBytes { get; init; }

        /// <summary>
        /// 内存使用效率（有效数据/总内存）
        /// </summary>
        public double MemoryEfficiency => TotalMemoryBytes > 0 ? (double)ViewStateMemoryBytes / TotalMemoryBytes : 0;
    }

    /// <summary>
    /// 视图状态统计信息
    /// </summary>
    public record ViewStateStats
    {
        /// <summary>
        /// 活跃视图状态数量
        /// </summary>
        public int ActiveViewStates { get; init; }

        /// <summary>
        /// 总视图状态数量
        /// </summary>
        public int TotalViewStates { get; init; }

        /// <summary>
        /// 最近访问的视图状态数量
        /// </summary>
        public int RecentlyAccessedStates { get; init; }

        /// <summary>
        /// 统计时间
        /// </summary>
        public DateTime StatisticsTime { get; init; } = DateTime.UtcNow;

        /// <summary>
        /// 平均视图大小（节点数）
        /// </summary>
        public double AverageViewSize { get; init; }

        /// <summary>
        /// 最大视图大小（节点数）
        /// </summary>
        public int MaxViewSize { get; init; }

        /// <summary>
        /// 视图状态访问频率分布
        /// </summary>
        public IReadOnlyDictionary<string, int> AccessFrequencyDistribution { get; init; } = 
            new Dictionary<string, int>();
    }

    /// <summary>
    /// 视图内存占用详情
    /// </summary>
    public record ViewMemoryDetail
    {
        public string ViewName { get; init; } = string.Empty;
        public long MemoryBytes { get; init; }
        public int NodeCount { get; init; }
        public DateTime LastAccessed { get; init; }
        public double MemoryPerNode => NodeCount > 0 ? (double)MemoryBytes / NodeCount : 0;
    }

    /// <summary>
    /// 视图内存占用排名
    /// </summary>
    public record ViewMemoryRanking
    {
        public int Rank { get; init; }
        public string ViewName { get; init; } = string.Empty;
        public long MemoryBytes { get; init; }
        public double PercentageOfTotal { get; init; }
    }

    /// <summary>
    /// 内存清理结果
    /// </summary>
    public record MemoryCleanupResult
    {
        public long MemoryBeforeBytes { get; init; }
        public long MemoryAfterBytes { get; init; }
        public long FreedBytes => MemoryBeforeBytes - MemoryAfterBytes;
        public double FreedPercentage => MemoryBeforeBytes > 0 ? (double)FreedBytes / MemoryBeforeBytes * 100 : 0;
        public DateTime CleanupTime { get; init; } = DateTime.UtcNow;
    }

    /// <summary>
    /// 内存健康状态
    /// </summary>
    public record MemoryHealthStatus
    {
        public MemoryStatus Status { get; init; }
        public long CurrentMemoryBytes { get; init; }
        public long WarningThresholdBytes { get; init; }
        public long CriticalThresholdBytes { get; init; }
        public string? RecommendedAction { get; init; }
        public DateTime CheckTime { get; init; } = DateTime.UtcNow;
    }

    /// <summary>
    /// 内存状态枚举
    /// </summary>
    public enum MemoryStatus
    {
        Healthy,
        Warning,
        Critical,
        Emergency
    }
}
