# MemoTree 关系存储实现 (Phase 2)

> **版本**: v2.1.1
> **创建日期**: 2025-07-25
> **最后更新**: 2025-07-25
> **依赖**: Phase1_CoreTypes.md, **Phase2_StorageInterfaces.md**
> **状态**: 设计阶段

## 概述

本文档定义了 MemoTree 系统中关系存储层的高级服务实现，包括关系管理服务、关系图数据结构和相关事件系统。

> **重要说明**:
> - 基础存储接口（`INodeRelationStorage`、`IRelationTypeStorage`、`INodeHierarchyStorage`）的定义请参考 **Phase2_StorageInterfaces.md** 文档
> - 基础数据类型（`NodeId`、`NodeRelation`、`RelationType`、`RelationId` 等）的定义请参考 **Phase1_CoreTypes.md** 和 **Phase2_StorageInterfaces.md** 文档
> - 本文档专注于基于这些基础定义的高级服务和数据传输对象

### 设计特点

- **服务导向**: 基于存储接口构建高级关系管理服务
- **图数据结构**: 提供关系图和路径查找功能
- **事件驱动**: 支持关系变更事件和通知机制
- **性能优化**: 缓存策略和批量操作支持
- **可扩展性**: 支持自定义关系分析和处理逻辑

### 类型依赖说明

本文档中使用的基础类型定义位置：
- **Phase1_CoreTypes.md**: `NodeId`, `RelationId`, `RelationType`, `NodeRelation`
- **Phase2_StorageInterfaces.md**: `INodeRelationStorage`, `IRelationTypeStorage`, `INodeHierarchyStorage`

## 1. 关系管理服务

### 1.1 IRelationManagementService

关系管理服务提供高级的关系操作和分析功能，基于底层存储接口构建。

```csharp
/// <summary>
/// 关系管理服务接口
/// </summary>
public interface IRelationManagementService
{
    /// <summary>
    /// 创建关系图
    /// </summary>
    Task<RelationGraph> BuildRelationGraphAsync(NodeId rootNodeId, int maxDepth = 3, CancellationToken cancellationToken = default);

    /// <summary>
    /// 查找节点间的路径
    /// </summary>
    Task<RelationPath?> FindPathAsync(NodeId fromNodeId, NodeId toNodeId, int maxDepth = 5, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取关系统计信息
    /// </summary>
    Task<RelationStatistics> GetRelationStatisticsAsync(NodeId nodeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 批量创建关系
    /// </summary>
    Task<IReadOnlyList<RelationId>> CreateRelationsBatchAsync(IEnumerable<CreateRelationRequest> requests, CancellationToken cancellationToken = default);

    /// <summary>
    /// 验证关系的一致性
    /// </summary>
    Task<RelationValidationResult> ValidateRelationsAsync(NodeId nodeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 分析关系模式
    /// </summary>
    Task<RelationPatternAnalysis> AnalyzeRelationPatternsAsync(IEnumerable<NodeId> nodeIds, CancellationToken cancellationToken = default);
}
```

### 1.2 关系图数据结构

#### RelationGraph - 关系图

```csharp
/// <summary>
/// 关系图数据结构
/// </summary>
public class RelationGraph
{
    /// <summary>
    /// 根节点ID
    /// </summary>
    public NodeId RootNodeId { get; init; }

    /// <summary>
    /// 图中的所有节点
    /// </summary>
    public IReadOnlySet<NodeId> Nodes { get; init; } = new HashSet<NodeId>();

    /// <summary>
    /// 图中的所有关系
    /// </summary>
    public IReadOnlyList<NodeRelation> Relations { get; init; } = new List<NodeRelation>();

    /// <summary>
    /// 节点的邻接表（出向关系）
    /// </summary>
    public IReadOnlyDictionary<NodeId, IReadOnlyList<NodeRelation>> OutgoingRelations { get; init; } =
        new Dictionary<NodeId, IReadOnlyList<NodeRelation>>();

    /// <summary>
    /// 节点的邻接表（入向关系）
    /// </summary>
    public IReadOnlyDictionary<NodeId, IReadOnlyList<NodeRelation>> IncomingRelations { get; init; } =
        new Dictionary<NodeId, IReadOnlyList<NodeRelation>>();

    /// <summary>
    /// 图的深度
    /// </summary>
    public int MaxDepth { get; init; }

    /// <summary>
    /// 创建时间
    /// </summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}
```

## 2. 关系路径和分析

### 2.1 RelationPath - 关系路径

```csharp
/// <summary>
/// 关系路径数据结构
/// </summary>
public class RelationPath
{
    /// <summary>
    /// 起始节点ID
    /// </summary>
    public NodeId StartNodeId { get; init; }

    /// <summary>
    /// 目标节点ID
    /// </summary>
    public NodeId EndNodeId { get; init; }

    /// <summary>
    /// 路径中的节点序列
    /// </summary>
    public IReadOnlyList<NodeId> NodePath { get; init; } = new List<NodeId>();

    /// <summary>
    /// 路径中的关系序列
    /// </summary>
    public IReadOnlyList<NodeRelation> Relations { get; init; } = new List<NodeRelation>();

    /// <summary>
    /// 路径长度（跳数）
    /// </summary>
    public int Length => Relations.Count;

    /// <summary>
    /// 路径权重（可用于路径排序）
    /// </summary>
    public double Weight { get; init; }

    /// <summary>
    /// 路径类型（直接、间接等）
    /// </summary>
    public PathType Type { get; init; }
}
```

### 2.2 关系统计信息

```csharp
/// <summary>
/// 关系统计信息
/// </summary>
public class RelationStatistics
{
    /// <summary>
    /// 出向关系数量
    /// </summary>
    public int OutgoingCount { get; init; }

    /// <summary>
    /// 入向关系数量
    /// </summary>
    public int IncomingCount { get; init; }

    /// <summary>
    /// 按类型分组的出向关系统计
    /// </summary>
    public IReadOnlyDictionary<RelationType, int> OutgoingByType { get; init; } = new Dictionary<RelationType, int>();

    /// <summary>
    /// 按类型分组的入向关系统计
    /// </summary>
    public IReadOnlyDictionary<RelationType, int> IncomingByType { get; init; } = new Dictionary<RelationType, int>();

    /// <summary>
    /// 最后更新时间
    /// </summary>
    public DateTime LastUpdated { get; init; }
}
```

## 3. 事件系统

### 3.1 关系变更事件

```csharp
/// <summary>
/// 节点关系变更事件
/// </summary>
public class NodeRelationChangedEvent
{
    /// <summary>
    /// 事件ID
    /// </summary>
    public string EventId { get; init; } = Guid.NewGuid().ToString();

    /// <summary>
    /// 变更类型
    /// </summary>
    public RelationChangeType ChangeType { get; init; }

    /// <summary>
    /// 涉及的关系
    /// </summary>
    public NodeRelation Relation { get; init; } = null!;

    /// <summary>
    /// 变更前的关系状态（更新操作时使用）
    /// </summary>
    public NodeRelation? PreviousRelation { get; init; }

    /// <summary>
    /// 事件发生时间
    /// </summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// 触发变更的用户或系统
    /// </summary>
    public string? TriggeredBy { get; init; }

    /// <summary>
    /// 变更原因或上下文
    /// </summary>
    public string? Reason { get; init; }
}
```

### 3.2 关系变更类型

```csharp
/// <summary>
/// 关系变更类型
/// </summary>
public enum RelationChangeType
{
    /// <summary>
    /// 关系创建
    /// </summary>
    Created,

    /// <summary>
    /// 关系更新
    /// </summary>
    Updated,

    /// <summary>
    /// 关系删除
    /// </summary>
    Deleted,

    /// <summary>
    /// 批量操作
    /// </summary>
    BatchOperation
}
```

### 3.3 路径类型

```csharp
/// <summary>
/// 路径类型枚举
/// </summary>
public enum PathType
{
    /// <summary>
    /// 直接路径（一跳）
    /// </summary>
    Direct,

    /// <summary>
    /// 间接路径（多跳）
    /// </summary>
    Indirect,

    /// <summary>
    /// 最短路径
    /// </summary>
    Shortest,

    /// <summary>
    /// 加权路径
    /// </summary>
    Weighted
}
```

## 4. 请求和响应类型

### 4.1 创建关系请求

```csharp
/// <summary>
/// 创建关系请求
/// </summary>
public class CreateRelationRequest
{
    /// <summary>
    /// 源节点ID
    /// </summary>
    public NodeId SourceId { get; init; }

    /// <summary>
    /// 目标节点ID
    /// </summary>
    public NodeId TargetId { get; init; }

    /// <summary>
    /// 关系类型
    /// </summary>
    public RelationType RelationType { get; init; }

    /// <summary>
    /// 关系描述
    /// </summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// 关系属性
    /// </summary>
    public IReadOnlyDictionary<string, object> Properties { get; init; } = new Dictionary<string, object>();
}
```

### 4.2 关系验证结果

```csharp
/// <summary>
/// 关系验证结果
/// </summary>
public class RelationValidationResult
{
    /// <summary>
    /// 验证是否通过
    /// </summary>
    public bool IsValid { get; init; }

    /// <summary>
    /// 验证错误信息
    /// </summary>
    public IReadOnlyList<string> Errors { get; init; } = new List<string>();

    /// <summary>
    /// 验证警告信息
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = new List<string>();

    /// <summary>
    /// 验证时间
    /// </summary>
    public DateTime ValidatedAt { get; init; } = DateTime.UtcNow;
}
```

### 4.3 关系模式分析

```csharp
/// <summary>
/// 关系模式分析结果
/// </summary>
public class RelationPatternAnalysis
{
    /// <summary>
    /// 分析的节点集合
    /// </summary>
    public IReadOnlySet<NodeId> AnalyzedNodes { get; init; } = new HashSet<NodeId>();

    /// <summary>
    /// 发现的关系模式
    /// </summary>
    public IReadOnlyList<RelationPattern> Patterns { get; init; } = new List<RelationPattern>();

    /// <summary>
    /// 关系密度（关系数/可能的最大关系数）
    /// </summary>
    public double RelationDensity { get; init; }

    /// <summary>
    /// 聚类系数
    /// </summary>
    public double ClusteringCoefficient { get; init; }

    /// <summary>
    /// 分析时间
    /// </summary>
    public DateTime AnalyzedAt { get; init; } = DateTime.UtcNow;
}
```

## 5. 关系模式定义

### 5.1 关系模式

```csharp
/// <summary>
/// 关系模式定义
/// </summary>
public class RelationPattern
{
    /// <summary>
    /// 模式名称
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// 模式描述
    /// </summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// 涉及的关系类型
    /// </summary>
    public IReadOnlySet<RelationType> RelationTypes { get; init; } = new HashSet<RelationType>();

    /// <summary>
    /// 模式中的节点数量
    /// </summary>
    public int NodeCount { get; init; }

    /// <summary>
    /// 模式出现频率
    /// </summary>
    public int Frequency { get; init; }

    /// <summary>
    /// 模式强度（0-1之间）
    /// </summary>
    public double Strength { get; init; }
}
```

### 5.2 层次结构变更事件

```csharp
/// <summary>
/// 节点层次结构变更事件
/// </summary>
public class NodeHierarchyChangedEvent
{
    /// <summary>
    /// 事件ID
    /// </summary>
    public string EventId { get; init; } = Guid.NewGuid().ToString();

    /// <summary>
    /// 变更的节点ID
    /// </summary>
    public NodeId NodeId { get; init; }

    /// <summary>
    /// 变更类型
    /// </summary>
    public HierarchyChangeType ChangeType { get; init; }

    /// <summary>
    /// 原父节点ID（移动操作时使用）
    /// </summary>
    public NodeId? OldParentId { get; init; }

    /// <summary>
    /// 新父节点ID（移动操作时使用）
    /// </summary>
    public NodeId? NewParentId { get; init; }

    /// <summary>
    /// 原顺序位置（重排序操作时使用）
    /// </summary>
    public int? OldOrder { get; init; }

    /// <summary>
    /// 新顺序位置（重排序操作时使用）
    /// </summary>
    public int? NewOrder { get; init; }

    /// <summary>
    /// 事件发生时间
    /// </summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// 触发变更的用户或系统
    /// </summary>
    public string? TriggeredBy { get; init; }

    /// <summary>
    /// 变更原因或上下文
    /// </summary>
    public string? Reason { get; init; }
}

/// <summary>
/// 层次结构变更类型
/// </summary>
public enum HierarchyChangeType
{
    /// <summary>
    /// 节点被添加到父节点
    /// </summary>
    ChildAdded,

    /// <summary>
    /// 节点从父节点移除
    /// </summary>
    ChildRemoved,

    /// <summary>
    /// 节点被移动到新父节点
    /// </summary>
    NodeMoved,

    /// <summary>
    /// 子节点顺序被重新排列
    /// </summary>
    ChildrenReordered
}
```

## 实施优先级

### 高优先级 (Phase 2.1)
1. **基础存储接口** - 参考 Phase2_StorageInterfaces.md 中的接口定义
2. **关系管理服务** - IRelationManagementService 的核心功能
3. **关系图数据结构** - RelationGraph 和 RelationPath 基础实现

### 中优先级 (Phase 2.2)
1. **关系分析功能** - 关系统计和模式分析
2. **事件系统集成** - 关系变更事件的完整支持
3. **验证和请求类型** - 完整的请求响应体系

### 低优先级 (Phase 2.3)
1. **高级分析功能** - 复杂的关系模式识别
2. **性能优化** - 批量操作和缓存策略
3. **扩展功能** - 自定义关系类型和属性

## 最佳实践

### 架构设计原则
1. **接口分离**: 基础存储接口与高级服务分离
2. **单一职责**: 每个服务专注于特定的关系管理功能
3. **依赖倒置**: 服务层依赖于存储接口抽象
4. **开闭原则**: 支持关系类型和分析算法的扩展

### 数据一致性
1. **引用完整性**: 确保关系引用的节点存在
2. **事务边界**: 明确定义关系操作的事务范围
3. **并发控制**: 处理多用户同时修改关系的情况
4. **一致性检查**: 定期验证关系数据的完整性

### 性能优化
1. **批量操作**: 优先使用批量接口减少I/O开销
2. **缓存策略**: 对频繁访问的关系图进行缓存
3. **异步处理**: 使用异步模式处理大量关系数据
4. **索引优化**: 为常用查询模式建立适当索引

### 扩展性考虑
1. **插件化设计**: 支持自定义关系分析算法
2. **配置驱动**: 通过配置控制关系管理行为
3. **版本兼容**: 考虑关系数据结构的演进
4. **国际化支持**: 支持多语言的关系描述和错误信息

---

## 相关文档

- **[Phase1_CoreTypes.md](Phase1_CoreTypes.md)** - 基础数据类型定义（NodeId, RelationId, RelationType, NodeRelation）
- **[Phase2_StorageInterfaces.md](Phase2_StorageInterfaces.md)** - 基础存储接口定义（权威源）
- **[Phase3_RelationServices.md](Phase3_RelationServices.md)** - 关系服务层实现
- **[Phase2_ViewStorage.md](Phase2_ViewStorage.md)** - 视图状态存储和缓存策略

## 变更日志

- **v2.1.1 (2025-07-25)**: 修复类型依赖说明，完善层次结构变更事件，修复RelationPath属性命名冲突
- **v2.1 (2025-07-25)**: 重构文档结构，移除重复的接口定义，专注于高级服务和数据传输对象
- **v2.0 (2025-07-25)**: 初始版本，包含完整的关系存储实现
