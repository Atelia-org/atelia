namespace MemoTree.Core.Storage.Versioned
{
    /// <summary>
    /// 版本化存储配置选项
    /// </summary>
    public class VersionedStorageOptions
    {
        /// <summary>
        /// 存储根路径（包含data/、version.json、journals/的目录）
        /// </summary>
        public string StorageRoot { get; set; } = string.Empty;
        
        /// <summary>
        /// 数据目录名（相对于StorageRoot）
        /// </summary>
        public string DataDirectory { get; set; } = "data";
        
        /// <summary>
        /// 版本指针文件名（相对于StorageRoot）
        /// </summary>
        public string VersionFile { get; set; } = "version.json";
        
        /// <summary>
        /// 事务日志目录名（相对于StorageRoot）
        /// </summary>
        public string JournalsDirectory { get; set; } = "journals";
        
        /// <summary>
        /// 保留的历史版本数量
        /// </summary>
        public int KeepVersionCount { get; set; } = 10;
        
        /// <summary>
        /// 文件扩展名
        /// </summary>
        public string FileExtension { get; set; } = ".json";
        
        /// <summary>
        /// 是否启用并发支持（MVP阶段设为false）
        /// </summary>
        public bool EnableConcurrency { get; set; } = false;
        
        /// <summary>
        /// 验证配置有效性
        /// </summary>
        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(StorageRoot))
                throw new ArgumentException("StorageRoot cannot be null or empty", nameof(StorageRoot));
            
            if (string.IsNullOrWhiteSpace(DataDirectory))
                throw new ArgumentException("DataDirectory cannot be null or empty", nameof(DataDirectory));
            
            if (string.IsNullOrWhiteSpace(VersionFile))
                throw new ArgumentException("VersionFile cannot be null or empty", nameof(VersionFile));
            
            if (string.IsNullOrWhiteSpace(JournalsDirectory))
                throw new ArgumentException("JournalsDirectory cannot be null or empty", nameof(JournalsDirectory));
            
            if (KeepVersionCount < 1)
                throw new ArgumentException("KeepVersionCount must be at least 1", nameof(KeepVersionCount));
        }
    }
}
