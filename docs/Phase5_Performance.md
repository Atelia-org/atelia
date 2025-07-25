# Phase5_Performance.md - 性能优化和监控

## 文档信息
- **阶段**: Phase 5 - 系统优化层
- **版本**: v1.0
- **创建日期**: 2025-07-25
- **依赖**: Phase1_CoreTypes.md, Phase2_StorageInterfaces.md, Phase3_CoreServices.md

## 概述

本文档定义了MemoTree系统的性能优化和监控相关类型，包括缓存策略、性能指标收集、系统监控和优化配置。这些类型确保系统能够高效运行并提供实时的性能监控能力。

## 目录

1. [缓存策略接口](#1-缓存策略接口)
2. [节点缓存服务](#2-节点缓存服务)
3. [性能监控系统](#3-性能监控系统)
4. [性能指标类型](#4-性能指标类型)
5. [缓存配置](#5-缓存配置)
6. [性能常量](#6-性能常量)
7. [使用示例](#7-使用示例)

---

## 1. 缓存策略接口

### 1.1 通用缓存策略

```csharp
/// <summary>
/// 缓存策略接口
/// 提供统一的缓存操作抽象，支持多种缓存实现
/// </summary>
/// <typeparam name="TKey">缓存键类型</typeparam>
/// <typeparam name="TValue">缓存值类型</typeparam>
public interface ICacheStrategy<TKey, TValue>
{
    /// <summary>
    /// 获取缓存项
    /// </summary>
    /// <param name="key">缓存键</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>缓存值，如果不存在则返回null</returns>
    Task<TValue?> GetAsync(TKey key, CancellationToken cancellationToken = default);

    /// <summary>
    /// 设置缓存项
    /// </summary>
    /// <param name="key">缓存键</param>
    /// <param name="value">缓存值</param>
    /// <param name="expiration">过期时间，null表示使用默认过期时间</param>
    /// <param name="cancellationToken">取消令牌</param>
    Task SetAsync(TKey key, TValue value, TimeSpan? expiration = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// 移除缓存项
    /// </summary>
    /// <param name="key">缓存键</param>
    /// <param name="cancellationToken">取消令牌</param>
    Task RemoveAsync(TKey key, CancellationToken cancellationToken = default);

    /// <summary>
    /// 清空缓存
    /// </summary>
    /// <param name="cancellationToken">取消令牌</param>
    Task ClearAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取缓存统计信息
    /// </summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>缓存统计信息</returns>
    Task<CacheStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default);
}
```

### 1.2 缓存统计信息

```csharp
/// <summary>
/// 缓存统计信息
/// 提供缓存性能和使用情况的详细统计
/// </summary>
public record CacheStatistics
{
    /// <summary>
    /// 缓存命中次数
    /// </summary>
    public long HitCount { get; init; }

    /// <summary>
    /// 缓存未命中次数
    /// </summary>
    public long MissCount { get; init; }

    /// <summary>
    /// 总请求次数
    /// </summary>
    public long TotalRequests { get; init; }

    /// <summary>
    /// 缓存命中率 (0.0 - 1.0)
    /// </summary>
    public double HitRatio { get; init; }

    /// <summary>
    /// 当前缓存项数量
    /// </summary>
    public long ItemCount { get; init; }

    /// <summary>
    /// 内存使用量（字节）
    /// </summary>
    public long MemoryUsageBytes { get; init; }

    /// <summary>
    /// 最后更新时间
    /// </summary>
    public DateTime LastUpdated { get; init; } = DateTime.UtcNow;
}
```

---

## 2. 节点缓存服务

### 2.1 节点缓存服务接口

```csharp
/// <summary>
/// 节点缓存服务接口
/// 专门用于缓存节点相关数据，提供高效的节点访问
/// </summary>
public interface INodeCacheService
{
    /// <summary>
    /// 获取缓存的节点元数据
    /// </summary>
    /// <param name="nodeId">节点ID</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>节点元数据，如果不存在则返回null</returns>
    Task<NodeMetadata?> GetMetadataAsync(NodeId nodeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 缓存节点元数据
    /// </summary>
    /// <param name="nodeId">节点ID</param>
    /// <param name="metadata">节点元数据</param>
    /// <param name="cancellationToken">取消令牌</param>
    Task SetMetadataAsync(NodeId nodeId, NodeMetadata metadata, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取缓存的节点内容
    /// </summary>
    /// <param name="nodeId">节点ID</param>
    /// <param name="level">LOD级别</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>节点内容，如果不存在则返回null</returns>
    Task<NodeContent?> GetContentAsync(NodeId nodeId, LodLevel level, CancellationToken cancellationToken = default);

    /// <summary>
    /// 缓存节点内容
    /// </summary>
    /// <param name="nodeId">节点ID</param>
    /// <param name="level">LOD级别</param>
    /// <param name="content">节点内容</param>
    /// <param name="cancellationToken">取消令牌</param>
    Task SetContentAsync(NodeId nodeId, LodLevel level, NodeContent content, CancellationToken cancellationToken = default);

    /// <summary>
    /// 使节点缓存失效
    /// 当节点被修改时调用，确保缓存一致性
    /// </summary>
    /// <param name="nodeId">节点ID</param>
    /// <param name="cancellationToken">取消令牌</param>
    Task InvalidateNodeAsync(NodeId nodeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 预加载相关节点
    /// 根据节点关系预加载可能需要的节点，提高访问性能
    /// </summary>
    /// <param name="nodeId">起始节点ID</param>
    /// <param name="depth">预加载深度，默认为1</param>
    /// <param name="cancellationToken">取消令牌</param>
    Task PreloadRelatedNodesAsync(NodeId nodeId, int depth = 1, CancellationToken cancellationToken = default);
}
```

---

## 3. 性能监控系统

### 3.1 性能指标类型

```csharp
/// <summary>
/// 性能指标类型
/// 定义不同类型的性能指标
/// </summary>
public enum MetricType
{
    /// <summary>
    /// 计数器 - 只增不减的累计值
    /// </summary>
    Counter,

    /// <summary>
    /// 仪表 - 可增可减的瞬时值
    /// </summary>
    Gauge,

    /// <summary>
    /// 直方图 - 值的分布统计
    /// </summary>
    Histogram,

    /// <summary>
    /// 计时器 - 时间测量
    /// </summary>
    Timer
}
```

### 3.2 性能指标数据

```csharp
/// <summary>
/// 性能指标
/// 表示单个性能指标的数据
/// </summary>
public record PerformanceMetric
{
    /// <summary>
    /// 指标名称
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// 指标类型
    /// </summary>
    public MetricType Type { get; init; }

    /// <summary>
    /// 指标值
    /// </summary>
    public double Value { get; init; }

    /// <summary>
    /// 值的单位
    /// </summary>
    public string Unit { get; init; } = string.Empty;

    /// <summary>
    /// 时间戳
    /// </summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// 标签集合，用于分类和过滤
    /// </summary>
    public IReadOnlyDictionary<string, string> Tags { get; init; } =
        new Dictionary<string, string>();
}
```

### 3.3 系统性能统计

```csharp
/// <summary>
/// 系统性能统计
/// 提供系统整体性能状况的快照
/// </summary>
public record SystemPerformanceStats
{
    /// <summary>
    /// CPU使用率百分比 (0.0 - 100.0)
    /// </summary>
    public double CpuUsagePercentage { get; init; }

    /// <summary>
    /// 内存使用量（字节）
    /// </summary>
    public long MemoryUsageBytes { get; init; }

    /// <summary>
    /// 可用内存量（字节）
    /// </summary>
    public long MemoryAvailableBytes { get; init; }

    /// <summary>
    /// 磁盘使用率百分比 (0.0 - 100.0)
    /// </summary>
    public double DiskUsagePercentage { get; init; }

    /// <summary>
    /// 活跃连接数
    /// </summary>
    public int ActiveConnections { get; init; }

    /// <summary>
    /// 总请求数
    /// </summary>
    public int TotalRequests { get; init; }

    /// <summary>
    /// 平均响应时间（毫秒）
    /// </summary>
    public double AverageResponseTimeMs { get; init; }

    /// <summary>
    /// 错误计数
    /// </summary>
    public int ErrorCount { get; init; }

    /// <summary>
    /// 统计收集时间
    /// </summary>
    public DateTime CollectedAt { get; init; } = DateTime.UtcNow;
}
```

### 3.4 性能监控服务接口

```csharp
/// <summary>
/// 性能监控服务接口
/// 提供性能指标收集、记录和查询功能
/// </summary>
public interface IPerformanceMonitoringService
{
    /// <summary>
    /// 记录指标
    /// </summary>
    /// <param name="metric">性能指标</param>
    /// <param name="cancellationToken">取消令牌</param>
    Task RecordMetricAsync(PerformanceMetric metric, CancellationToken cancellationToken = default);

    /// <summary>
    /// 增加计数器
    /// </summary>
    /// <param name="name">计数器名称</param>
    /// <param name="value">增加的值，默认为1</param>
    /// <param name="tags">标签集合</param>
    /// <param name="cancellationToken">取消令牌</param>
    Task IncrementCounterAsync(string name, double value = 1, IReadOnlyDictionary<string, string>? tags = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// 设置仪表值
    /// </summary>
    /// <param name="name">仪表名称</param>
    /// <param name="value">仪表值</param>
    /// <param name="tags">标签集合</param>
    /// <param name="cancellationToken">取消令牌</param>
    Task SetGaugeAsync(string name, double value, IReadOnlyDictionary<string, string>? tags = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// 记录时间
    /// </summary>
    /// <param name="name">计时器名称</param>
    /// <param name="duration">持续时间</param>
    /// <param name="tags">标签集合</param>
    /// <param name="cancellationToken">取消令牌</param>
    Task RecordTimingAsync(string name, TimeSpan duration, IReadOnlyDictionary<string, string>? tags = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取系统性能统计
    /// </summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>系统性能统计信息</returns>
    Task<SystemPerformanceStats> GetSystemStatsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 创建计时器
    /// 返回一个IDisposable对象，当释放时自动记录时间
    /// </summary>
    /// <param name="name">计时器名称</param>
    /// <param name="tags">标签集合</param>
    /// <returns>计时器对象</returns>
    IDisposable StartTimer(string name, IReadOnlyDictionary<string, string>? tags = null);
}
```

---

## 4. 性能指标类型

### 4.1 预定义指标名称

```csharp
/// <summary>
/// 预定义的性能指标名称常量
/// 确保指标名称的一致性
/// </summary>
public static class MetricNames
{
    // 节点操作指标
    public const string NodeCreated = "memo_tree.node.created";
    public const string NodeUpdated = "memo_tree.node.updated";
    public const string NodeDeleted = "memo_tree.node.deleted";
    public const string NodeLoaded = "memo_tree.node.loaded";

    // 缓存指标
    public const string CacheHit = "memo_tree.cache.hit";
    public const string CacheMiss = "memo_tree.cache.miss";
    public const string CacheEviction = "memo_tree.cache.eviction";

    // 搜索指标
    public const string SearchExecuted = "memo_tree.search.executed";
    public const string SearchDuration = "memo_tree.search.duration";
    public const string SearchResults = "memo_tree.search.results";

    // 关系指标
    public const string RelationCreated = "memo_tree.relation.created";
    public const string RelationDeleted = "memo_tree.relation.deleted";
    public const string RelationTraversed = "memo_tree.relation.traversed";

    // 系统指标
    public const string MemoryUsage = "memo_tree.system.memory_usage";
    public const string CpuUsage = "memo_tree.system.cpu_usage";
    public const string ActiveConnections = "memo_tree.system.active_connections";
}
```

---

## 5. 缓存配置

### 5.1 缓存配置选项

```csharp
/// <summary>
/// 缓存配置选项
/// 定义缓存行为的配置参数
/// </summary>
public partial class MemoTreeConfiguration
{
    /// <summary>
    /// 索引缓存文件名
    /// </summary>
    public string IndexCacheFileName { get; set; } = "index-cache.json";

    /// <summary>
    /// 是否启用运行时父节点索引缓存
    /// </summary>
    public bool EnableParentIndexCache { get; set; } = true;

    /// <summary>
    /// 父节点索引缓存过期时间（分钟）
    /// </summary>
    public int ParentIndexCacheExpirationMinutes { get; set; } = 15;

    /// <summary>
    /// 语义关系缓存过期时间（分钟）
    /// </summary>
    public int RelationCacheExpirationMinutes { get; set; } = 30;

    /// <summary>
    /// 节点内容缓存过期时间（分钟）
    /// </summary>
    public int NodeContentCacheExpirationMinutes { get; set; } = 60;

    /// <summary>
    /// 最大缓存项数量
    /// </summary>
    public int MaxCacheItems { get; set; } = 10000;

    /// <summary>
    /// 缓存清理间隔（分钟）
    /// </summary>
    public int CacheCleanupIntervalMinutes { get; set; } = 30;

    /// <summary>
    /// 是否启用缓存预热
    /// </summary>
    public bool EnableCacheWarmup { get; set; } = true;

    /// <summary>
    /// 缓存预热节点数量
    /// </summary>
    public int CacheWarmupNodeCount { get; set; } = 100;
}
```

### 5.2 性能配置选项

```csharp
/// <summary>
/// 性能配置选项
/// 定义性能监控和优化的配置参数
/// </summary>
public partial class MemoTreeConfiguration
{
    /// <summary>
    /// 是否启用性能监控
    /// </summary>
    public bool EnablePerformanceMonitoring { get; set; } = true;

    /// <summary>
    /// 性能指标收集间隔（秒）
    /// </summary>
    public int MetricsCollectionIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// 性能指标保留天数
    /// </summary>
    public int MetricsRetentionDays { get; set; } = 7;

    /// <summary>
    /// 是否启用详细性能跟踪
    /// </summary>
    public bool EnableDetailedPerformanceTracking { get; set; } = false;

    /// <summary>
    /// 慢查询阈值（毫秒）
    /// </summary>
    public int SlowQueryThresholdMs { get; set; } = 1000;

    /// <summary>
    /// 批处理大小
    /// </summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>
    /// 并发度限制
    /// </summary>
    public int MaxConcurrency { get; set; } = Environment.ProcessorCount * 2;
}
```

---

## 6. 性能常量

### 6.1 性能限制常量

```csharp
/// <summary>
/// 性能相关常量定义
/// 定义系统性能限制和默认值
/// </summary>
public static partial class MemoTreeConstants
{
    /// <summary>
    /// 缓存项最大生存时间（小时）
    /// </summary>
    public const int MaxCacheItemLifetimeHours = 24;

    /// <summary>
    /// 最大批处理大小
    /// </summary>
    public const int MaxBatchSize = 50;

    /// <summary>
    /// 默认缓存容量
    /// </summary>
    public const int DefaultCacheCapacity = 1000;

    /// <summary>
    /// 最大预加载深度
    /// </summary>
    public const int MaxPreloadDepth = 3;

    /// <summary>
    /// 性能监控采样率 (0.0 - 1.0)
    /// </summary>
    public const double PerformanceMonitoringSampleRate = 0.1;

    /// <summary>
    /// 最大并发连接数
    /// </summary>
    public const int MaxConcurrentConnections = 100;

    /// <summary>
    /// 内存使用警告阈值（字节）
    /// </summary>
    public const long MemoryWarningThresholdBytes = 1024 * 1024 * 1024; // 1GB

    /// <summary>
    /// CPU使用率警告阈值（百分比）
    /// </summary>
    public const double CpuWarningThresholdPercentage = 80.0;
}
```

---

## 7. 使用示例

### 7.1 缓存服务使用示例

```csharp
// 使用节点缓存服务
public class NodeService
{
    private readonly INodeCacheService _cacheService;
    private readonly INodeStorage _storage;

    public NodeService(INodeCacheService cacheService, INodeStorage storage)
    {
        _cacheService = cacheService;
        _storage = storage;
    }

    public async Task<NodeMetadata?> GetNodeMetadataAsync(NodeId nodeId, CancellationToken ct = default)
    {
        // 首先尝试从缓存获取
        var cached = await _cacheService.GetMetadataAsync(nodeId, ct);
        if (cached != null)
        {
            return cached;
        }

        // 缓存未命中，从存储加载
        var metadata = await _storage.GetMetadataAsync(nodeId, ct);
        if (metadata != null)
        {
            // 缓存结果
            await _cacheService.SetMetadataAsync(nodeId, metadata, ct);
        }

        return metadata;
    }

    public async Task UpdateNodeAsync(NodeId nodeId, NodeContent content, CancellationToken ct = default)
    {
        // 更新存储
        await _storage.UpdateContentAsync(nodeId, content, ct);

        // 使缓存失效
        await _cacheService.InvalidateNodeAsync(nodeId, ct);
    }
}
```

### 7.2 性能监控使用示例

```csharp
// 使用性能监控服务
public class SearchService
{
    private readonly IPerformanceMonitoringService _monitoring;
    private readonly ISearchEngine _searchEngine;

    public SearchService(IPerformanceMonitoringService monitoring, ISearchEngine searchEngine)
    {
        _monitoring = monitoring;
        _searchEngine = searchEngine;
    }

    public async Task<SearchResult> SearchAsync(string query, CancellationToken ct = default)
    {
        // 使用计时器测量搜索性能
        using var timer = _monitoring.StartTimer(MetricNames.SearchDuration, new Dictionary<string, string>
        {
            ["query_type"] = "text",
            ["query_length"] = query.Length.ToString()
        });

        try
        {
            // 执行搜索
            var result = await _searchEngine.SearchAsync(query, ct);

            // 记录成功指标
            await _monitoring.IncrementCounterAsync(MetricNames.SearchExecuted, 1, new Dictionary<string, string>
            {
                ["status"] = "success",
                ["result_count"] = result.Items.Count.ToString()
            }, ct);

            await _monitoring.SetGaugeAsync(MetricNames.SearchResults, result.Items.Count, null, ct);

            return result;
        }
        catch (Exception ex)
        {
            // 记录错误指标
            await _monitoring.IncrementCounterAsync(MetricNames.SearchExecuted, 1, new Dictionary<string, string>
            {
                ["status"] = "error",
                ["error_type"] = ex.GetType().Name
            }, ct);

            throw;
        }
    }
}
```

### 7.3 缓存策略实现示例

```csharp
// 内存缓存策略实现
public class MemoryCacheStrategy<TKey, TValue> : ICacheStrategy<TKey, TValue>
    where TKey : notnull
{
    private readonly MemoryCache _cache;
    private readonly CacheStatistics _statistics = new();
    private long _hitCount;
    private long _missCount;

    public MemoryCacheStrategy(IOptions<MemoryCacheOptions> options)
    {
        _cache = new MemoryCache(options.Value);
    }

    public Task<TValue?> GetAsync(TKey key, CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue(key, out var value))
        {
            Interlocked.Increment(ref _hitCount);
            return Task.FromResult((TValue?)value);
        }

        Interlocked.Increment(ref _missCount);
        return Task.FromResult(default(TValue));
    }

    public Task SetAsync(TKey key, TValue value, TimeSpan? expiration = null, CancellationToken cancellationToken = default)
    {
        var options = new MemoryCacheEntryOptions();
        if (expiration.HasValue)
        {
            options.AbsoluteExpirationRelativeToNow = expiration;
        }

        _cache.Set(key, value, options);
        return Task.CompletedTask;
    }

    public Task RemoveAsync(TKey key, CancellationToken cancellationToken = default)
    {
        _cache.Remove(key);
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        _cache.Dispose();
        return Task.CompletedTask;
    }

    public Task<CacheStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
    {
        var hitCount = Interlocked.Read(ref _hitCount);
        var missCount = Interlocked.Read(ref _missCount);
        var totalRequests = hitCount + missCount;

        var statistics = new CacheStatistics
        {
            HitCount = hitCount,
            MissCount = missCount,
            TotalRequests = totalRequests,
            HitRatio = totalRequests > 0 ? (double)hitCount / totalRequests : 0.0,
            ItemCount = _cache.Count,
            MemoryUsageBytes = GC.GetTotalMemory(false), // 近似值
            LastUpdated = DateTime.UtcNow
        };

        return Task.FromResult(statistics);
    }
}
```

---

## 8. 总结

本文档定义了MemoTree系统的性能优化和监控架构，包括：

### 8.1 核心组件

1. **缓存策略接口** - 提供统一的缓存抽象
2. **节点缓存服务** - 专门的节点数据缓存
3. **性能监控系统** - 全面的性能指标收集
4. **配置选项** - 灵活的性能调优配置

### 8.2 设计特点

- **多层缓存** - 支持不同级别的缓存策略
- **实时监控** - 提供实时性能指标收集
- **可配置性** - 丰富的配置选项支持不同场景
- **类型安全** - 强类型设计确保编译时检查

### 8.3 性能优化策略

- **预加载机制** - 智能预加载相关节点
- **批量操作** - 减少I/O操作次数
- **异步处理** - 非阻塞的性能监控
- **内存管理** - 智能缓存清理和过期策略

这些类型为构建高性能、可监控的MemoTree系统提供了坚实的基础。

---

**文档状态**: ✅ 完成
**最后更新**: 2025-07-25
**版本**: v1.0
**审查状态**: 待审查

本文档定义了MemoTree系统的性能优化和监控类型，确保系统能够高效运行并提供全面的性能监控能力。
