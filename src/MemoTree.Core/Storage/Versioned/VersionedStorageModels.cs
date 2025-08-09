namespace MemoTree.Core.Storage.Versioned
{
    /// <summary>
    /// 版本指针信息
    /// </summary>
    public record VersionPointer
    {
        public long CurrentVersion { get; init; } = 1;
        public DateTime LastModified { get; init; } = DateTime.UtcNow;
        public string Comment { get; init; } = string.Empty;
    }

    /// <summary>
    /// 操作日志
    /// </summary>
    public record OperationLog
    {
        public string OperationId { get; init; } = string.Empty;
        public string OperationType { get; init; } = string.Empty;
        public long TargetVersion { get; init; }
        public List<string> AffectedKeys { get; init; } = new();
        public DateTime StartTime { get; init; } = DateTime.UtcNow;
        // 已移除 Completed 字段 - 日志文件的存在性本身就是操作状态的标记
        // 文件存在 = 操作未完成，文件不存在 = 操作已完成
        public string Description { get; init; } = string.Empty;
    }

    /// <summary>
    /// 垃圾回收结果
    /// </summary>
    public record GarbageCollectionResult
    {
        public int DeletedFileCount { get; init; }
        public long FreedBytes { get; init; }
        public DateTime CollectionTime { get; init; }
        public TimeSpan Duration { get; init; }
    }

    /// <summary>
    /// 启动加载结果
    /// </summary>
    public record LoadResult<TKey, TValue>
        where TKey : notnull
        where TValue : class
    {
        public IReadOnlyDictionary<TKey, TValue> Data { get; init; } = new Dictionary<TKey, TValue>();
        public long CurrentVersion { get; init; } = 1;
        public int LoadedFileCount { get; init; }
        public int RecoveredOperationCount { get; init; }
        public TimeSpan LoadDuration { get; init; }
    }

    /// <summary>
    /// 文件扫描结果
    /// </summary>
    public record FileInfo<TKey>
        where TKey : notnull
    {
        public TKey Key { get; init; } = default!;
        public long Version { get; init; }
        public string FilePath { get; init; } = string.Empty;
        public DateTime LastModified { get; init; }
        public long FileSize { get; init; }
    }
}
