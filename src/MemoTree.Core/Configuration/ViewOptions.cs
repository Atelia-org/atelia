namespace MemoTree.Core.Configuration {
    /// <summary>
    /// 视图状态配置选项 - 内存优先架构
    /// 对应Phase2_ViewStorage.md中定义的视图状态存储和内存管理
    /// </summary>
    public class ViewOptions {
        /// <summary>
        /// 视图状态文件名
        /// </summary>
        public string ViewStateFileName { get; set; } = "last-view.json";

        /// <summary>
        /// 视图状态备份文件名
        /// </summary>
        public string ViewStateBackupFileName { get; set; } = "view-state-backup.json";

        /// <summary>
        /// 最大内存中视图状态数量
        /// </summary>
        public int MaxInMemoryViewStates { get; set; } = 1000;

        /// <summary>
        /// 是否启用视图状态自动保存
        /// </summary>
        public bool EnableAutoSaveViewState { get; set; } = true;

        /// <summary>
        /// 视图状态自动保存间隔（秒）
        /// </summary>
        public int ViewStateAutoSaveIntervalSeconds { get; set; } = 30;

        /// <summary>
        /// 是否启用批量视图状态更新
        /// </summary>
        public bool EnableBatchViewStateUpdates { get; set; } = true;

        /// <summary>
        /// 批量更新间隔（毫秒）
        /// </summary>
        public int BatchUpdateIntervalMilliseconds { get; set; } = 100;

        /// <summary>
        /// 是否启用视图状态压缩存储
        /// </summary>
        public bool EnableViewStateCompression { get; set; } = false;

        /// <summary>
        /// 视图状态批量操作的最大数量
        /// </summary>
        public int MaxBatchViewStateOperations { get; set; } = 20;

        /// <summary>
        /// 是否启用视图状态预加载
        /// </summary>
        public bool EnableViewStatePreloading { get; set; } = true;
    }
}
