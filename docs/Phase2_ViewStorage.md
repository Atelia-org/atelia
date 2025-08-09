# MemoTree 视图状态存储 (Phase 2) - 内存优先架构

> **版本**: v1.1 (内存优先架构)
> **创建时间**: 2025-07-25
> **依赖**: Phase1_CoreTypes.md, Phase1_Configuration.md, Phase2_StorageInterfaces.md
> **阶段**: Phase 2 - Storage Layer

## 概述

本文档定义了MemoTree系统的视图状态存储，采用**内存优先架构**提供高性能的视图状态管理。视图状态包括节点的展开/折叠状态、LOD级别、焦点节点等信息，全部常驻内存以确保流畅的用户体验。

### 🎯 内存优先视图存储特点
- **即时响应**: 视图状态常驻内存，UI操作零延迟
- **自动持久化**: 状态变更立即同步到磁盘，确保数据安全
- **简化架构**: 移除复杂的缓存层，专注于核心功能
- **批量优化**: 支持批量状态更新，提升大规模操作性能

视图存储系统包含：
- **视图状态存储**: 持久化用户的界面状态和偏好设置
- **内存状态管理**: 高效的内存数据结构和访问模式
- **同步落盘机制**: 确保状态变更的持久化和一致性

### 类型引用说明

本文档中使用的核心类型定义位置：
- **NodeId, LodLevel, NodeMetadata, NodeContent**: 定义于 [Phase1_CoreTypes.md](Phase1_CoreTypes.md)
- **ViewOptions, RelationOptions**: 定义于 [Phase1_Configuration.md](Phase1_Configuration.md)
- **MemoryUsageStats, NodeMemoryStats**: 本文档中定义的内存统计信息类型
- **NodeViewState, MemoTreeViewState**: 本文档中定义的视图状态类型

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

### MemoTree视图状态

```csharp
/// <summary>
/// MemoTree视图状态
/// </summary>
public record MemoTreeViewState
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
    Task<MemoTreeViewState?> GetViewStateAsync(string viewName, CancellationToken cancellationToken = default);

    /// <summary>
    /// 保存视图状态
    /// </summary>
    Task SaveViewStateAsync(MemoTreeViewState viewState, CancellationToken cancellationToken = default);

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
    Task<IReadOnlyDictionary<string, MemoTreeViewState>> GetMultipleViewStatesAsync(
        IEnumerable<string> viewNames,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 复制视图状态
    /// </summary>
    Task<MemoTreeViewState> CopyViewStateAsync(string sourceViewName, string targetViewName,
        CancellationToken cancellationToken = default);
}
```

## 内存管理接口

### 视图状态内存管理

```csharp
/// <summary>
/// 视图状态内存管理接口
/// 提供视图状态的内存使用统计和管理功能
/// </summary>
public interface IViewStateMemoryManager
{
    /// <summary>
    /// 获取内存使用统计
    /// </summary>
    Task<MemoryUsageStats> GetMemoryStatsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取视图状态数量统计
    /// </summary>
    Task<ViewStateStats> GetViewStateStatsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 预加载常用视图状态（Phase 5可选实现）
    /// </summary>
    Task PreloadFrequentViewStatesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 清理未使用的视图状态（Phase 5可选实现）
    /// </summary>
    Task CleanupUnusedViewStatesAsync(TimeSpan unusedThreshold, CancellationToken cancellationToken = default);
}
```

### 内存使用统计

```csharp
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
}
```

## 节点内存服务

### INodeMemoryService 接口

```csharp
/// <summary>
/// 节点内存服务接口
/// 提供节点数据的内存管理和快速访问功能
/// </summary>
public interface INodeMemoryService
{
    /// <summary>
    /// 检查节点是否已加载到内存
    /// </summary>
    Task<bool> IsNodeLoadedAsync(NodeId nodeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取已加载节点的数量
    /// </summary>
    Task<int> GetLoadedNodeCountAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取节点内存使用统计
    /// </summary>
    Task<NodeMemoryStats> GetNodeMemoryStatsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 预加载相关节点到内存（Phase 5可选实现）
    /// </summary>
    Task PreloadRelatedNodesAsync(NodeId nodeId, int depth = 1, CancellationToken cancellationToken = default);

    /// <summary>
    /// 批量检查节点加载状态
    /// </summary>
    Task<IReadOnlyDictionary<NodeId, bool>> CheckMultipleNodesLoadedAsync(
        IEnumerable<NodeId> nodeIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取内存中所有已加载节点的ID列表
    /// </summary>
    Task<IReadOnlyList<NodeId>> GetLoadedNodeIdsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 节点内存统计信息
/// </summary>
public record NodeMemoryStats
{
    /// <summary>
    /// 已加载节点数量
    /// </summary>
    public int LoadedNodeCount { get; init; }

    /// <summary>
    /// 节点数据占用内存字节数
    /// </summary>
    public long NodeMemoryBytes { get; init; }

    /// <summary>
    /// 平均每个节点的内存占用
    /// </summary>
    public double AverageNodeMemoryBytes => LoadedNodeCount > 0 ? (double)NodeMemoryBytes / LoadedNodeCount : 0;

    /// <summary>
    /// 最大节点内存占用
    /// </summary>
    public long MaxNodeMemoryBytes { get; init; }

    /// <summary>
    /// 最小节点内存占用
    /// </summary>
    public long MinNodeMemoryBytes { get; init; }

    /// <summary>
    /// 统计时间
    /// </summary>
    public DateTime StatisticsTime { get; init; } = DateTime.UtcNow;
}
```

## 视图状态配置

### 配置选项归属说明

视图状态存储相关的配置选项分布在以下配置类中：

#### ViewOptions 配置类 (定义于 Phase1_Configuration.md)

```csharp
/// <summary>
/// 视图状态专用配置选项 - 内存优先架构
/// 详细定义请参考 Phase1_Configuration.md 中的 ViewOptions 类
/// </summary>
public class ViewOptions
{
    // 文件名配置
    public string ViewStateFileName { get; set; } = "last-view.json";
    public string ViewStateBackupFileName { get; set; } = "view-state-backup.json";

    // 内存管理配置
    public int MaxInMemoryViewStates { get; set; } = 1000;
    public bool EnableViewStateCompression { get; set; } = false;

    // 自动保存配置
    public bool EnableAutoSaveViewState { get; set; } = true;
    public int ViewStateAutoSaveIntervalSeconds { get; set; } = 30;

    // 性能配置
    public bool EnableBatchViewStateUpdates { get; set; } = true;
    public int BatchUpdateIntervalMilliseconds { get; set; } = 100;

    // 其他视图相关配置...
}
```

#### RelationOptions 配置类 (定义于 Phase1_Configuration.md)

```csharp
/// <summary>
/// 关系管理相关配置选项 - 内存优先架构
/// 详细定义请参考 Phase1_Configuration.md 中的 RelationOptions 类
/// </summary>
public class RelationOptions
{
    // 关系存储配置
    public bool EnableIndependentHierarchyStorage { get; set; } = true;
    public int MaxRelationDepth { get; set; } = 10;

    // 内存管理配置
    public int MaxInMemoryRelations { get; set; } = 10000;
    public bool EnableRelationIndexing { get; set; } = true;

    // 其他关系管理配置...
}
```

> **配置引用说明**:
> - 视图状态专用配置请使用 `ViewOptions` 类
> - 关系管理配置请使用 `RelationOptions` 类
> - 内存优先架构移除了缓存过期时间等复杂配置
> - 完整的配置定义请参考 [Phase1_Configuration.md](Phase1_Configuration.md)

## 内存优先架构实施指南

### 1. 内存数据结构选择
- **ConcurrentDictionary**: 用于线程安全的节点状态存储
- **ImmutableDictionary**: 用于只读的视图状态快照
- **Memory Pool**: 减少频繁的内存分配和回收

### 2. 同步落盘策略
- **Write-Through**: 写操作立即同步到磁盘
- **Batch Write**: 可选的批量写入优化（Phase 5）
- **Atomic Write**: 确保写操作的原子性

### 3. 内存管理策略
- **启动预加载**: 系统启动时异步加载常用状态
- **内存监控**: 实时监控内存使用情况
- **优雅降级**: 内存不足时的处理策略（Phase 5）

## 性能优化建议

### 1. 视图状态优化
- 使用增量更新减少序列化开销
- 实现视图状态的差异化存储
- 支持视图状态的压缩存储（可选）

### 2. 内存访问优化
- 使用高效的数据结构（Dictionary vs List）
- 实现批量操作减少锁竞争
- 优化序列化/反序列化性能

### 3. 持久化优化
- 异步写入避免阻塞UI线程
- 使用文件锁确保并发安全
- 实现写入失败的重试机制

## 实施优先级

### 高优先级 (Phase 2.3.1)
1. **IViewStateStorage** - 基础视图状态存储
2. **内存数据结构** - ConcurrentDictionary等核心结构
3. **同步落盘机制** - Write-Through持久化

### 中优先级 (Phase 2.3.2)
1. **INodeMemoryService** - 节点内存管理服务
2. **批量操作支持** - 提高批量访问性能
3. **内存统计功能** - 监控和诊断支持

### 低优先级 (Phase 2.3.3)
1. **内存优化策略** - 冷数据卸载等高级功能（Phase 5）
2. **智能预加载** - 基于使用模式的预加载
3. **缓存压缩** - 减少内存使用的压缩策略

## 最佳实践

### 1. 数据一致性（内存优先架构约束）
- 不引入独立二级缓存：内存中已加载数据即为主数据源，与持久化保持同步
- 如需优化，仅针对外部系统/昂贵查询使用轻量结果/索引缓存，避免与内存主数据产生一致性分叉
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
