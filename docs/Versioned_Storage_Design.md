# 通用版本化存储设计 (Versioned Storage)

> **版本**: v1.0  
> **创建日期**: 2025-08-09  
> **作者**: 刘德智  
> **目的**: 为MemoTree提供通用的Copy-on-Write版本化存储组件  

## 概述

本文档定义了一个通用的版本化存储模式，支持Copy-on-Write语义、原子性操作和灾难恢复。该模式可用于父子关系、语义关系、视图状态等多种数据的存储。

### 核心特征

1. **可文件名化的Key**: 支持任何可序列化为文件名的标识符
2. **版本化存储**: 每次修改创建新版本文件，保留历史
3. **原子性操作**: 支持多文件的事务性批量更新
4. **灾难恢复**: 通过操作日志和版本扫描实现故障恢复
5. **内存常驻**: 最新版本数据常驻内存，提供高性能访问

## 存储结构

### 根路径概念
每个版本化存储实例都有一个独立的根路径，包含标准的三部分结构：

```
{storage-root}/                 # 版本化存储根路径
├── data/                       # 数据文件目录
│   ├── {key}.{version}.json    # 版本化数据文件
│   └── ...
├── version.json                # 版本指针文件
└── journals/                   # 事务日志目录
    ├── {operation-id}.log.json # 操作日志文件
    └── ...
```

### 具体应用示例

```
workspace/
├── hierarchy/                  # 父子关系存储根路径
│   ├── data/
│   │   ├── {node-id}.{version}.json
│   │   └── ...
│   ├── version.json
│   └── journals/
│       └── ...
├── relations/                  # 语义关系存储根路径
│   ├── data/
│   │   ├── {relation-group-id}.{version}.json
│   │   └── ...
│   ├── version.json
│   └── journals/
│       └── ...
└── viewstates/                 # 视图状态存储根路径
    ├── data/
    │   ├── {view-name}.{version}.json
    │   └── ...
    ├── version.json
    └── journals/
        └── ...
```

## 核心接口设计

### 1. 通用版本化存储接口

```csharp
/// <summary>
/// 通用版本化存储接口
/// </summary>
/// <typeparam name="TKey">键类型</typeparam>
/// <typeparam name="TValue">值类型</typeparam>
public interface IVersionedStorage<TKey, TValue>
    where TKey : notnull
    where TValue : class
{
    /// <summary>
    /// 获取当前版本号
    /// </summary>
    Task<long> GetCurrentVersionAsync(CancellationToken cancellationToken = default);
    
    /// <summary>
    /// 获取指定键的最新值
    /// </summary>
    Task<TValue?> GetAsync(TKey key, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// 批量获取多个键的最新值
    /// </summary>
    Task<IReadOnlyDictionary<TKey, TValue>> GetManyAsync(
        IEnumerable<TKey> keys, 
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// 原子性批量更新（Copy-on-Write）
    /// </summary>
    Task<long> UpdateManyAsync(
        IReadOnlyDictionary<TKey, TValue> updates,
        string operationDescription = "",
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// 获取所有键
    /// </summary>
    Task<IReadOnlyList<TKey>> GetAllKeysAsync(CancellationToken cancellationToken = default);
}
```

### 2. 配置选项

```csharp
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
}
```

### 3. 键序列化接口

```csharp
/// <summary>
/// 键序列化器接口
/// </summary>
/// <typeparam name="TKey">键类型</typeparam>
public interface IKeySerializer<TKey>
{
    /// <summary>
    /// 将键序列化为文件名安全的字符串
    /// </summary>
    string Serialize(TKey key);
    
    /// <summary>
    /// 从字符串反序列化键
    /// </summary>
    TKey Deserialize(string serialized);
    
}
```

### 4. 版本格式化接口

```csharp
/// <summary>
/// 版本号格式化器接口，用于统一处理版本号的编码和解码
/// </summary>
public interface IVersionFormatter
{
    /// <summary>
    /// 将版本号格式化为文件名中的字符串
    /// </summary>
    string FormatVersion(long version);

    /// <summary>
    /// 从文件名中的字符串解析版本号
    /// </summary>
    long? ParseVersion(string versionString);
}

/// <summary>
/// 十进制版本号格式化器（向后兼容）
/// </summary>
public class DecimalVersionFormatter : IVersionFormatter
{
    public string FormatVersion(long version) => version.ToString();

    public long? ParseVersion(string versionString)
    {
        if (long.TryParse(versionString, out var version) && version >= 1)
            return version;
        return null;
    }
}

/// <summary>
/// 十六进制版本号格式化器（更短的文件名，默认使用）
/// </summary>
public class HexVersionFormatter : IVersionFormatter
{
    public string FormatVersion(long version) => version.ToString("X");

    public long? ParseVersion(string versionString)
    {
        if (long.TryParse(versionString, NumberStyles.HexNumber, null, out var version) && version >= 1)
            return version;
        return null;
    }
}
```

## 实现组件

### 1. 文件路径提供器

```csharp
/// <summary>
/// 版本化存储文件路径提供器
/// </summary>
public class VersionedStoragePathProvider<TKey>
{
    private readonly VersionedStorageOptions _options;
    private readonly IKeySerializer<TKey> _keySerializer;
    private readonly IVersionFormatter _versionFormatter;

    public VersionedStoragePathProvider(
        VersionedStorageOptions options,
        IKeySerializer<TKey> keySerializer,
        IVersionFormatter versionFormatter)
    {
        _options = options;
        _keySerializer = keySerializer;
        _versionFormatter = versionFormatter;
    }

    /// <summary>
    /// 获取存储根目录
    /// </summary>
    public string GetStorageRoot() => _options.StorageRoot;

    /// <summary>
    /// 获取数据目录路径
    /// </summary>
    public string GetDataDirectory()
        => Path.Combine(_options.StorageRoot, _options.DataDirectory);

    /// <summary>
    /// 获取版本指针文件路径
    /// </summary>
    public string GetVersionFilePath()
        => Path.Combine(_options.StorageRoot, _options.VersionFile);

    /// <summary>
    /// 获取事务日志目录路径
    /// </summary>
    public string GetJournalsDirectory()
        => Path.Combine(_options.StorageRoot, _options.JournalsDirectory);

    /// <summary>
    /// 获取数据文件路径
    /// </summary>
    public string GetDataFilePath(TKey key, long version)
    {
        var keyString = _keySerializer.Serialize(key);
        var versionString = _versionFormatter.FormatVersion(version);
        var fileName = $"{keyString}.{versionString}{_options.FileExtension}";
        return Path.Combine(GetDataDirectory(), fileName);
    }

    /// <summary>
    /// 尝试从文件名解析键和版本
    /// 文件名格式：{serialized-key}.{version}
    /// </summary>
    public (TKey key, long version)? TryParseFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        var parts = fileName.Split('.');
        if (parts.Length != 2)
            return null;

        var keyPart = parts[0];
        var versionPart = parts[1];

        // 验证key部分
        if (string.IsNullOrWhiteSpace(keyPart))
            return null;

        // 验证版本部分
        var version = _versionFormatter.ParseVersion(versionPart);
        if (!version.HasValue)
            return null;

        try
        {
            var key = _keySerializer.Deserialize(keyPart);
            return (key, version.Value);
        }
        catch
        {
            // key反序列化失败
            return null;
        }
    }

    /// <summary>
    /// 获取操作日志文件路径
    /// </summary>
    public string GetJournalFilePath(string operationId)
    {
        var fileName = $"{operationId}.log.json";
        return Path.Combine(GetJournalsDirectory(), fileName);
    }

    /// <summary>
    /// 确保目录结构存在
    /// </summary>
    public void EnsureDirectoriesExist()
    {
        Directory.CreateDirectory(GetDataDirectory());
        Directory.CreateDirectory(GetJournalsDirectory());

        var storageRoot = GetStorageRoot();
        if (!Directory.Exists(storageRoot))
        {
            Directory.CreateDirectory(storageRoot);
        }
    }

    /// <summary>
    /// 扫描数据目录获取所有文件
    /// </summary>
    public IEnumerable<(TKey key, long version, string filePath)> ScanDataFiles()
    {
        var dataDir = GetDataDirectory();
        if (!Directory.Exists(dataDir)) yield break;

        var pattern = $"*{_options.FileExtension}";
        var files = Directory.GetFiles(dataDir, pattern);

        foreach (var file in files)
        {
            var fileName = Path.GetFileNameWithoutExtension(file);
            var parseResult = _keySerializer.TryParseFileName(fileName);

            if (parseResult.HasValue)
            {
                yield return (parseResult.Value.key, parseResult.Value.version, file);
            }
        }
    }
}
```

### 2. 内存管理器

```csharp
/// <summary>
/// 版本化存储内存管理器
/// </summary>
public class VersionedStorageMemoryManager<TKey, TValue>
    where TKey : notnull
    where TValue : class
{
    private readonly ConcurrentDictionary<TKey, TValue> _data = new();
    private volatile long _currentVersion = 1;
    
    /// <summary>
    /// 获取当前版本
    /// </summary>
    public long CurrentVersion => _currentVersion;
    
    /// <summary>
    /// 获取值
    /// </summary>
    public TValue? Get(TKey key)
        => _data.TryGetValue(key, out var value) ? value : null;
    
    /// <summary>
    /// 批量获取值
    /// </summary>
    public IReadOnlyDictionary<TKey, TValue> GetMany(IEnumerable<TKey> keys)
    {
        var result = new Dictionary<TKey, TValue>();
        foreach (var key in keys)
        {
            if (_data.TryGetValue(key, out var value))
            {
                result[key] = value;
            }
        }
        return result;
    }
    
    /// <summary>
    /// 获取所有键
    /// </summary>
    public IReadOnlyList<TKey> GetAllKeys()
        => _data.Keys.ToList();
    
    /// <summary>
    /// 批量更新并返回新版本号
    /// </summary>
    public long UpdateMany(IReadOnlyDictionary<TKey, TValue> updates)
    {
        var newVersion = Interlocked.Increment(ref _currentVersion);
        
        foreach (var (key, value) in updates)
        {
            _data.AddOrUpdate(key, value, (_, _) => value);
        }
        
        return newVersion;
    }
    
    /// <summary>
    /// 设置版本号（用于启动时加载）
    /// </summary>
    public void SetVersion(long version)
    {
        _currentVersion = version;
    }
    
    /// <summary>
    /// 加载数据（用于启动时加载）
    /// </summary>
    public void LoadData(IReadOnlyDictionary<TKey, TValue> data)
    {
        _data.Clear();
        foreach (var (key, value) in data)
        {
            _data.TryAdd(key, value);
        }
    }
}
```

## 适用场景

### 1. 父子关系存储
- **Key**: `NodeId`
- **Value**: `HierarchyInfo`
- **StorageRoot**: `"workspace/hierarchy"`
- **配置示例**:
```csharp
new VersionedStorageOptions
{
    StorageRoot = "workspace/hierarchy",
    EnableConcurrency = false  // MVP阶段单会话使用
}
```

### 2. 语义关系存储
- **Key**: `RelationGroupId`
- **Value**: `RelationGroup`
- **StorageRoot**: `"workspace/relations"`

### 3. 视图状态存储
- **Key**: `ViewName`
- **Value**: `ViewState`
- **StorageRoot**: `"workspace/viewstates"`

## 设计优势

1. **通用性**: 支持多种数据类型的版本化存储
2. **原子性**: 通过操作日志确保事务完整性
3. **性能**: 内存常驻提供高速访问
4. **可靠性**: 完整的灾难恢复机制
5. **可维护性**: 清晰的目录结构和数据分离
6. **简单性**: MVP阶段无并发支持，专注单会话LLM使用场景
7. **扩展性**: 根路径概念便于日后添加新的存储实例
8. **职责分离**: 键序列化、版本格式化、路径管理各司其职
9. **格式灵活**: 支持十进制和十六进制版本格式，默认使用更短的十六进制

### 4. 原子操作管理器

```csharp
/// <summary>
/// 原子操作管理器
/// </summary>
public class AtomicOperationManager<TKey, TValue>
    where TKey : notnull
    where TValue : class
{
    private readonly VersionedStoragePathProvider<TKey> _pathProvider;
    private readonly SemaphoreSlim _operationLock = new(1, 1);

    /// <summary>
    /// 执行原子操作
    /// </summary>
    public async Task<T> ExecuteAtomicAsync<T>(
        string operationType,
        IEnumerable<TKey> affectedKeys,
        Func<long, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            var operationId = Guid.NewGuid().ToString();
            var targetVersion = await GetNextVersionAsync();

            // 1. 写入操作日志
            var log = new OperationLog
            {
                OperationId = operationId,
                OperationType = operationType,
                TargetVersion = targetVersion,
                AffectedKeys = affectedKeys.Select(k => k.ToString()).ToList(),
                StartTime = DateTime.UtcNow
            };

            var logFile = _pathProvider.GetOperationLogPath(operationId);
            await WriteOperationLogAsync(logFile, log, cancellationToken);

            try
            {
                // 2. 执行操作
                var result = await operation(targetVersion);

                // 3. 标记完成
                await MarkOperationCompleteAsync(logFile, cancellationToken);

                // 4. 清理日志
                File.Delete(logFile);

                return result;
            }
            catch
            {
                // 保留日志用于恢复
                throw;
            }
        }
        finally
        {
            _operationLock.Release();
        }
    }

    /// <summary>
    /// 恢复未完成的操作
    /// </summary>
    public async Task RecoverIncompleteOperationsAsync()
    {
        var logDir = Path.GetDirectoryName(_pathProvider.GetOperationLogPath("dummy"));
        if (!Directory.Exists(logDir)) return;

        var logFiles = Directory.GetFiles(logDir, "*.log.json");

        foreach (var logFile in logFiles)
        {
            if (await IsOperationCompleteAsync(logFile))
            {
                File.Delete(logFile);
            }
            else
            {
                await RollbackOperationAsync(logFile);
            }
        }
    }
}
```

### 5. 启动加载器

```csharp
/// <summary>
/// 版本化存储启动加载器
/// </summary>
public class VersionedStorageBootloader<TKey, TValue>
    where TKey : notnull
    where TValue : class
{
    private readonly VersionedStoragePathProvider<TKey> _pathProvider;
    private readonly IKeySerializer<TKey> _keySerializer;

    /// <summary>
    /// 加载存储状态
    /// </summary>
    public async Task<(VersionedStorageMemoryManager<TKey, TValue> manager, long version)> LoadAsync()
    {
        // 1. 恢复未完成的操作
        var atomicManager = new AtomicOperationManager<TKey, TValue>(_pathProvider);
        await atomicManager.RecoverIncompleteOperationsAsync();

        // 2. 扫描数据文件
        var fileGroups = _pathProvider.ScanDataFiles()
            .GroupBy(x => x.key)
            .ToList();

        // 3. 找出每个键的最新版本
        var latestData = new Dictionary<TKey, TValue>();
        var maxVersion = 0L;

        foreach (var group in fileGroups)
        {
            var latest = group.OrderByDescending(x => x.version).First();
            maxVersion = Math.Max(maxVersion, latest.version);

            var json = await File.ReadAllTextAsync(latest.filePath);
            var value = JsonSerializer.Deserialize<TValue>(json);
            if (value != null)
            {
                latestData[latest.key] = value;
            }
        }

        // 4. 验证版本一致性
        var versionFromPointer = await LoadVersionFromPointerAsync();
        var finalVersion = Math.Max(maxVersion, versionFromPointer);

        // 5. 创建内存管理器
        var manager = new VersionedStorageMemoryManager<TKey, TValue>();
        manager.LoadData(latestData);
        manager.SetVersion(finalVersion);

        return (manager, finalVersion);
    }

    private async Task<long> LoadVersionFromPointerAsync()
    {
        var pointerFile = _pathProvider.GetVersionPointerPath();
        if (!File.Exists(pointerFile)) return 1;

        var json = await File.ReadAllTextAsync(pointerFile);
        var versionInfo = JsonSerializer.Deserialize<VersionPointer>(json);
        return versionInfo?.CurrentVersion ?? 1;
    }
}
```

### 6. 垃圾回收器

```csharp
/// <summary>
/// 版本垃圾回收器
/// </summary>
public class VersionedStorageGarbageCollector<TKey>
{
    private readonly VersionedStoragePathProvider<TKey> _pathProvider;
    private readonly int _keepVersions;

    public VersionedStorageGarbageCollector(
        VersionedStoragePathProvider<TKey> pathProvider,
        int keepVersions = 10)
    {
        _pathProvider = pathProvider;
        _keepVersions = keepVersions;
    }

    /// <summary>
    /// 执行垃圾回收
    /// </summary>
    public async Task<GarbageCollectionResult> CollectAsync(long currentVersion)
    {
        var fileGroups = _pathProvider.ScanDataFiles()
            .GroupBy(x => x.key)
            .ToList();

        var deletedFiles = 0;
        var freedBytes = 0L;

        foreach (var group in fileGroups)
        {
            var filesToDelete = group
                .OrderByDescending(x => x.version)
                .Skip(_keepVersions)
                .ToList();

            foreach (var (_, _, filePath) in filesToDelete)
            {
                var fileInfo = new FileInfo(filePath);
                freedBytes += fileInfo.Length;
                File.Delete(filePath);
                deletedFiles++;
            }
        }

        return new GarbageCollectionResult
        {
            DeletedFileCount = deletedFiles,
            FreedBytes = freedBytes,
            CollectionTime = DateTime.UtcNow
        };
    }
}
```

## 支持数据类型

```csharp
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
    public bool Completed { get; init; } = false;
}

/// <summary>
/// 垃圾回收结果
/// </summary>
public record GarbageCollectionResult
{
    public int DeletedFileCount { get; init; }
    public long FreedBytes { get; init; }
    public DateTime CollectionTime { get; init; }
}
```

## NodeId键序列化器实现

```csharp
/// <summary>
/// NodeId键序列化器
/// </summary>
public class NodeIdKeySerializer : IKeySerializer<NodeId>
{
    public string Serialize(NodeId key) => key.Value;

    public NodeId Deserialize(string serialized) => new NodeId(serialized);

}
```

## 版本格式化器性能对比

对于大版本号，十六进制格式可以显著减少文件名长度：

| 版本号 | 十进制格式 | 十六进制格式 | 节省字符数 |
|--------|------------|--------------|------------|
| 1,000,000 | `1000000` (7字符) | `F4240` (5字符) | 2字符 |
| 1,000,000,000 | `1000000000` (10字符) | `3B9ACA00` (8字符) | 2字符 |
| long.MaxValue | `9223372036854775807` (19字符) | `7FFFFFFFFFFFFFFF` (16字符) | 3字符 |

## 工厂方法更新

```csharp
/// <summary>
/// 版本化存储工厂（支持版本格式化器）
/// </summary>
public static class VersionedStorageFactory
{
    /// <summary>
    /// 创建NodeId到HierarchyInfo的版本化存储
    /// </summary>
    public static async Task<IVersionedStorage<NodeId, HierarchyInfo>> CreateHierarchyStorageAsync(
        string workspaceRoot,
        ILogger<VersionedStorageImpl<NodeId, HierarchyInfo>> logger,
        IVersionFormatter? versionFormatter = null)
    {
        var options = new VersionedStorageOptions
        {
            StorageRoot = Path.Combine(workspaceRoot, "hierarchy"),
            EnableConcurrency = false // MVP阶段单会话使用
        };

        var keySerializer = new NodeIdKeySerializer();
        var formatter = versionFormatter ?? new HexVersionFormatter(); // 默认使用十六进制
        var storage = new VersionedStorageImpl<NodeId, HierarchyInfo>(options, keySerializer, formatter, logger);

        await storage.InitializeAsync();
        return storage;
    }
}
```

## 下一步实施

1. **实现通用版本化存储组件** - 创建核心接口和实现类
2. **为NodeId实现键序列化器** - 支持NodeId作为存储键
3. **基于通用组件实现父子关系存储** - 具体应用到Hierarchy
4. **添加垃圾回收和监控功能** - 完善运维能力
5. **编写单元测试** - 确保组件可靠性

## 设计验证

你的抽象完全正确！这个通用模式具备以下特征：

✅ **可文件名化的Key**: 通过IKeySerializer支持任意键类型
✅ **Key到文件的存储**: 每个键对应版本化的数据文件
✅ **有最新版概念**: 通过version serial管理当前状态
✅ **原子化多文件操作**: 通过操作日志确保事务性
✅ **文件较短适合全量重写**: Copy-on-Write策略的理想场景

**配置参数**:
1. **数据目录**: DataDirectory (如"Hierarchy")
2. **根指针文件**: VersionPointerFile (如"hierarchy.version.json")
3. **事务日志目录**: OperationLogDirectory (如"operation-logs")

这个设计非常solid，建议我们先实现这个通用组件！

---

**相关文档**:
- [Phase1_CoreTypes.md](Phase1_CoreTypes.md) - 基础数据类型
- [Phase2_StorageInterfaces.md](Phase2_StorageInterfaces.md) - 存储接口定义
