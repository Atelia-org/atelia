using MemoTree.Core.Types;

namespace MemoTree.Core.Storage.Interfaces
{
    /// <summary>
    /// 视图状态存储接口
    /// 管理MemoTree的视图状态持久化
    /// </summary>
    public interface IViewStateStorage
    {
        /// <summary>
        /// 获取视图状态
        /// </summary>
        /// <param name="viewName">视图名称</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>视图状态，如果不存在则返回null</returns>
        Task<MemoTreeViewState?> GetViewStateAsync(
            string viewName,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 保存视图状态
        /// </summary>
        /// <param name="viewState">视图状态</param>
        /// <param name="cancellationToken">取消令牌</param>
        Task SaveViewStateAsync(MemoTreeViewState viewState, CancellationToken cancellationToken = default);

        /// <summary>
        /// 获取所有视图名称
        /// </summary>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>所有视图名称列表</returns>
        Task<IReadOnlyList<string>> GetViewNamesAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// 删除视图状态
        /// </summary>
        /// <param name="viewName">视图名称</param>
        /// <param name="cancellationToken">取消令牌</param>
        Task DeleteViewStateAsync(string viewName, CancellationToken cancellationToken = default);

        /// <summary>
        /// 检查视图是否存在
        /// </summary>
        /// <param name="viewName">视图名称</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>如果视图存在则返回true</returns>
        Task<bool> ViewExistsAsync(string viewName, CancellationToken cancellationToken = default);

        /// <summary>
        /// 复制视图状态
        /// </summary>
        /// <param name="sourceViewName">源视图名称</param>
        /// <param name="targetViewName">目标视图名称</param>
        /// <param name="cancellationToken">取消令牌</param>
        Task<MemoTreeViewState> CopyViewStateAsync(
            string sourceViewName,
            string targetViewName,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 重命名视图
        /// </summary>
        /// <param name="oldName">旧视图名称</param>
        /// <param name="newName">新视图名称</param>
        /// <param name="cancellationToken">取消令牌</param>
        Task RenameViewAsync(
            string oldName, 
            string newName, 
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 获取视图的最后修改时间
        /// </summary>
        /// <param name="viewName">视图名称</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>最后修改时间，如果视图不存在则返回null</returns>
        Task<DateTime?> GetViewLastModifiedAsync(
            string viewName,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 批量获取视图状态
        /// </summary>
        /// <param name="viewNames">视图名称集合</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>视图名称到视图状态的映射</returns>
        Task<IReadOnlyDictionary<string, MemoTreeViewState>> GetMultipleViewStatesAsync(
            IEnumerable<string> viewNames,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 清理过期的视图状态
        /// </summary>
        /// <param name="olderThan">清理早于此时间的视图</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>清理的视图数量</returns>
        Task<int> CleanupOldViewsAsync(
            DateTime olderThan, 
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 获取视图状态的大小统计
        /// </summary>
        /// <param name="viewName">视图名称</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>视图状态的大小（字节），如果视图不存在则返回null</returns>
        Task<long?> GetViewSizeAsync(
            string viewName, 
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 获取所有视图的统计信息
        /// </summary>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>视图存储统计信息</returns>
        Task<ViewStorageStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// 视图存储统计信息
    /// </summary>
    public record ViewStorageStatistics
    {
        public int TotalViews { get; init; }
        public long TotalSize { get; init; }
        public DateTime OldestView { get; init; }
        public DateTime NewestView { get; init; }
        public string? LargestView { get; init; }
        public long LargestViewSize { get; init; }
        public double AverageViewSize { get; init; }
    }
}
