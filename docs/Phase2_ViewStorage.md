# MemoTree 视图状态存储和缓存策略 (Phase 2)

> **版本**: v1.0
> **创建时间**: 2025-07-25
> **依赖**: Phase1_CoreTypes.md, Phase1_Configuration.md, Phase2_StorageInterfaces.md
> **阶段**: Phase 2 - Storage Layer

## 概述

本文档定义了MemoTree系统的视图状态存储和缓存策略，包括视图状态的持久化、缓存管理和性能优化。视图状态存储负责保存用户的界面状态，包括节点的展开/折叠状态、LOD级别、焦点节点等信息，确保用户体验的连续性。

视图存储系统包含：
- **视图状态存储**: 持久化用户的界面状态和偏好设置
- **缓存策略**: 提供高性能的数据访问和内存管理
- **节点缓存服务**: 专门针对认知节点的缓存优化

### 类型引用说明

本文档中使用的核心类型定义位置：
- **NodeId, LodLevel, NodeMetadata, NodeContent**: 定义于 [Phase1_CoreTypes.md](Phase1_CoreTypes.md)
- **ViewOptions, RelationOptions**: 定义于 [Phase1_Configuration.md](Phase1_Configuration.md)
- **CacheStatistics**: 本文档中定义的缓存统计信息类型
- **NodeViewState, CanvasViewState**: 本文档中定义的视图状态类型

## 视图状态数据类型

### 节点视图状态

```csharp
/// <summary>
/// 节点在视图中的状态
/// </summary>
public record NodeViewState
{
    public NodeId Id { get; init; }
    public LodLevel CurrentLevel { get; init; } = LodLevel.Summary;
    public bool IsExpanded { get; init; } = false;
    public bool IsVisible { get; init; } = true;
    public int Order { get; init; } = 0;
}
```

### 认知画布视图状态

```csharp
/// <summary>
/// 认知画布视图状态
/// </summary>
public record CanvasViewState
{
    public string Name { get; init; } = "default";
    public DateTime LastModified { get; init; } = DateTime.UtcNow;
    public IReadOnlyList<NodeViewState> NodeStates { get; init; } = Array.Empty<NodeViewState>();
    public NodeId? FocusedNodeId { get; init; }
    public IReadOnlyDictionary<string, object> ViewSettings { get; init; } = 
        new Dictionary<string, object>();
}
```

## 视图状态存储接口

### IViewStateStorage 接口

```csharp
/// <summary>
/// 视图状态存储接口
/// </summary>
public interface IViewStateStorage
{
    /// <summary>
    /// 获取视图状态
    /// </summary>
    Task<CanvasViewState?> GetViewStateAsync(string viewName, CancellationToken cancellationToken = default);

    /// <summary>
    /// 保存视图状态
    /// </summary>
    Task SaveViewStateAsync(CanvasViewState viewState, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取所有视图名称
    /// </summary>
    Task<IReadOnlyList<string>> GetViewNamesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 删除视图状态
    /// </summary>
    Task DeleteViewStateAsync(string viewName, CancellationToken cancellationToken = default);

    /// <summary>
    /// 检查视图是否存在
    /// </summary>
    Task<bool> ViewExistsAsync(string viewName, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取视图的最后修改时间
    /// </summary>
    Task<DateTime?> GetViewLastModifiedAsync(string viewName, CancellationToken cancellationToken = default);

    /// <summary>
    /// 批量获取视图状态
    /// </summary>
    Task<IReadOnlyDictionary<string, CanvasViewState>> GetMultipleViewStatesAsync(
        IEnumerable<string> viewNames, 
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 复制视图状态
    /// </summary>
    Task<CanvasViewState> CopyViewStateAsync(string sourceViewName, string targetViewName, 
        CancellationToken cancellationToken = default);
}
```

## 缓存策略接口

### 通用缓存策略

```csharp
/// <summary>
/// 缓存策略接口
/// </summary>
public interface ICacheStrategy<TKey, TValue>
{
    /// <summary>
    /// 获取缓存项
    /// </summary>
    Task<TValue?> GetAsync(TKey key, CancellationToken cancellationToken = default);

    /// <summary>
    /// 设置缓存项
    /// </summary>
    Task SetAsync(TKey key, TValue value, TimeSpan? expiration = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// 移除缓存项
    /// </summary>
    Task RemoveAsync(TKey key, CancellationToken cancellationToken = default);

    /// <summary>
    /// 清空缓存
    /// </summary>
    Task ClearAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取缓存统计信息
    /// </summary>
    Task<CacheStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 批量获取缓存项
    /// </summary>
    Task<IReadOnlyDictionary<TKey, TValue>> GetMultipleAsync(
        IEnumerable<TKey> keys, 
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 批量设置缓存项
    /// </summary>
    Task SetMultipleAsync(
        IReadOnlyDictionary<TKey, TValue> items, 
        TimeSpan? expiration = null, 
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 检查缓存项是否存在
    /// </summary>
    Task<bool> ExistsAsync(TKey key, CancellationToken cancellationToken = default);
}
```

### 缓存统计信息

```csharp
/// <summary>
/// 缓存统计信息
/// </summary>
public record CacheStatistics
{
    public long HitCount { get; init; }
    public long MissCount { get; init; }
    public long TotalRequests { get; init; }
    public double HitRatio { get; init; }
    public long ItemCount { get; init; }
    public long MemoryUsageBytes { get; init; }
    public DateTime LastUpdated { get; init; } = DateTime.UtcNow;
    public TimeSpan AverageAccessTime { get; init; }
    public long EvictionCount { get; init; }
}
```

## 节点缓存服务

### INodeCacheService 接口

```csharp
/// <summary>
/// 节点缓存服务接口
/// </summary>
public interface INodeCacheService
{
    /// <summary>
    /// 获取缓存的节点元数据
    /// </summary>
    Task<NodeMetadata?> GetMetadataAsync(NodeId nodeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 缓存节点元数据
    /// </summary>
    Task SetMetadataAsync(NodeId nodeId, NodeMetadata metadata, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取缓存的节点内容
    /// </summary>
    Task<NodeContent?> GetContentAsync(NodeId nodeId, LodLevel level, CancellationToken cancellationToken = default);

    /// <summary>
    /// 缓存节点内容
    /// </summary>
    Task SetContentAsync(NodeId nodeId, LodLevel level, NodeContent content, CancellationToken cancellationToken = default);

    /// <summary>
    /// 使节点缓存失效
    /// </summary>
    Task InvalidateNodeAsync(NodeId nodeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 预加载相关节点
    /// </summary>
    Task PreloadRelatedNodesAsync(NodeId nodeId, int depth = 1, CancellationToken cancellationToken = default);

    /// <summary>
    /// 批量获取节点元数据
    /// </summary>
    Task<IReadOnlyDictionary<NodeId, NodeMetadata>> GetMultipleMetadataAsync(
        IEnumerable<NodeId> nodeIds, 
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 批量缓存节点元数据
    /// </summary>
    Task SetMultipleMetadataAsync(
        IReadOnlyDictionary<NodeId, NodeMetadata> metadataMap, 
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取缓存使用情况
    /// </summary>
    Task<CacheStatistics> GetCacheStatisticsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 清理过期缓存
    /// </summary>
    Task CleanupExpiredCacheAsync(CancellationToken cancellationToken = default);
}
```

## 视图状态配置

### 配置选项归属说明

视图状态存储相关的配置选项分布在以下配置类中：

#### ViewOptions 配置类 (定义于 Phase1_Configuration.md)

```csharp
/// <summary>
/// 视图状态专用配置选项
/// 详细定义请参考 Phase1_Configuration.md 中的 ViewOptions 类
/// </summary>
public class ViewOptions
{
    // 文件名配置
    public string ViewStateFileName { get; set; } = "last-view.json";
    public string IndexCacheFileName { get; set; } = "index-cache.json";

    // 缓存配置
    public int ViewStateCacheExpirationMinutes { get; set; } = 60;
    public int MaxCachedViewStates { get; set; } = 10;

    // 自动保存配置
    public bool EnableAutoSaveViewState { get; set; } = true;
    public int ViewStateAutoSaveIntervalSeconds { get; set; } = 30;

    // 其他视图相关配置...
}
```

#### RelationOptions 配置类 (定义于 Phase1_Configuration.md)

```csharp
/// <summary>
/// 关系缓存相关配置选项
/// 详细定义请参考 Phase1_Configuration.md 中的 RelationOptions 类
/// </summary>
public class RelationOptions
{
    // 父节点索引缓存配置
    public bool EnableParentIndexCache { get; set; } = true;
    public int ParentIndexCacheExpirationMinutes { get; set; } = 15;

    // 语义关系缓存配置
    public int RelationCacheExpirationMinutes { get; set; } = 30;

    // 其他关系管理配置...
}
```

> **配置引用说明**:
> - 视图状态专用配置请使用 `ViewOptions` 类
> - 关系缓存配置请使用 `RelationOptions` 类
> - 完整的配置定义请参考 [Phase1_Configuration.md](Phase1_Configuration.md)

## 缓存策略实现指南

### 1. 内存缓存策略
- **LRU (Least Recently Used)**: 适用于视图状态缓存
- **TTL (Time To Live)**: 适用于临时数据缓存
- **Size-based**: 基于内存使用量的缓存清理

### 2. 分层缓存策略
- **L1缓存**: 内存中的快速访问缓存
- **L2缓存**: 本地文件系统缓存
- **L3缓存**: 可选的分布式缓存

### 3. 缓存失效策略
- **主动失效**: 数据更新时主动清理相关缓存
- **被动失效**: 基于TTL的自动过期
- **依赖失效**: 基于数据依赖关系的级联失效

## 性能优化建议

### 1. 视图状态优化
- 使用增量更新减少序列化开销
- 实现视图状态的差异化存储
- 支持视图状态的压缩存储

### 2. 缓存优化
- 实现预加载策略提高响应速度
- 使用批量操作减少I/O次数
- 实现智能缓存预热机制

### 3. 内存管理
- 监控缓存内存使用情况
- 实现自适应的缓存大小调整
- 提供缓存统计和监控接口

## 实施优先级

### 高优先级 (Phase 2.3.1)
1. **IViewStateStorage** - 基础视图状态存储
2. **基础缓存策略** - 内存缓存实现
3. **视图状态配置** - 基本配置选项

### 中优先级 (Phase 2.3.2)
1. **INodeCacheService** - 节点专用缓存服务
2. **批量操作支持** - 提高批量访问性能
3. **缓存统计功能** - 监控和诊断支持

### 低优先级 (Phase 2.3.3)
1. **分层缓存策略** - 多级缓存实现
2. **智能预加载** - 基于使用模式的预加载
3. **缓存压缩** - 减少内存使用的压缩策略

## 最佳实践

### 1. 数据一致性
- 确保缓存与持久化存储的一致性
- 实现适当的缓存失效机制
- 处理并发访问的数据竞争

### 2. 错误处理
- 缓存失败时的降级策略
- 提供详细的错误诊断信息
- 实现缓存恢复机制

### 3. 监控和调试
- 提供缓存命中率统计
- 实现缓存性能监控
- 支持缓存内容的调试查看

---

**下一阶段**: [Phase3_CoreServices.md](Phase3_CoreServices.md) - 核心业务服务接口
