# MemoTree 关系存储实现 (Phase 2)

> **版本**: v2.0  
> **创建日期**: 2025-07-25  
> **依赖**: Phase1_CoreTypes.md, Phase2_StorageInterfaces.md  
> **状态**: 设计阶段  

## 概述

本文档定义了 MemoTree 系统中关系存储层的核心实现，包括语义关系存储、关系类型定义存储和节点层次结构存储。这些存储接口专门处理节点间的各种关系，支持复杂的关系查询、管理和维护操作。

### 设计特点

- **关系分离**: 语义关系与层次关系分别存储和管理
- **类型化关系**: 支持丰富的关系类型定义和元数据
- **高效查询**: 提供多种查询模式和批量操作支持
- **事务性**: 支持关系的原子性操作和一致性保证
- **可扩展性**: 支持自定义关系类型和属性扩展

## 1. 语义关系存储接口

### 1.1 INodeRelationStorage

语义关系存储接口负责管理节点间的语义关系，不包括父子层次关系。

```csharp
/// <summary>
/// 语义关系存储接口（集中存储版本，不包括父子关系）
/// </summary>
public interface INodeRelationStorage
{
    /// <summary>
    /// 获取节点的所有出向语义关系
    /// </summary>
    Task<IReadOnlyList<NodeRelation>> GetOutgoingRelationsAsync(NodeId nodeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取节点的所有入向语义关系
    /// </summary>
    Task<IReadOnlyList<NodeRelation>> GetIncomingRelationsAsync(NodeId nodeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取节点的所有语义关系（入向+出向）
    /// </summary>
    Task<IReadOnlyList<NodeRelation>> GetAllRelationsAsync(NodeId nodeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 根据关系ID获取语义关系
    /// </summary>
    Task<NodeRelation?> GetRelationAsync(RelationId relationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 添加语义关系
    /// </summary>
    Task<RelationId> AddRelationAsync(NodeId sourceId, NodeId targetId, RelationType relationType, string description = "", CancellationToken cancellationToken = default);

    /// <summary>
    /// 更新语义关系
    /// </summary>
    Task UpdateRelationAsync(RelationId relationId, Action<NodeRelation> updateAction, CancellationToken cancellationToken = default);

    /// <summary>
    /// 移除语义关系
    /// </summary>
    Task RemoveRelationAsync(RelationId relationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 批量获取语义关系
    /// </summary>
    Task<IReadOnlyDictionary<RelationId, NodeRelation>> GetRelationsBatchAsync(IEnumerable<RelationId> relationIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// 查找特定类型的语义关系
    /// </summary>
    Task<IReadOnlyList<NodeRelation>> FindRelationsByTypeAsync(RelationType relationType, CancellationToken cancellationToken = default);

    /// <summary>
    /// 查找两个节点之间的语义关系
    /// </summary>
    Task<IReadOnlyList<NodeRelation>> FindRelationsBetweenAsync(NodeId sourceId, NodeId targetId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 异步枚举所有语义关系
    /// </summary>
    IAsyncEnumerable<NodeRelation> GetAllRelationsAsync(CancellationToken cancellationToken = default);
}
```

### 1.2 核心数据类型

#### NodeRelation - 语义关系定义

```csharp
/// <summary>
/// 语义关系定义（集中存储版本，不包括父子关系）
/// </summary>
public record NodeRelation
{
    public RelationId Id { get; init; }
    public NodeId SourceId { get; init; }
    public NodeId TargetId { get; init; }
    public RelationType Type { get; init; }
    public string Description { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public IReadOnlyDictionary<string, object> Properties { get; init; } =
        new Dictionary<string, object>();
}
```

#### RelationId - 关系标识符

```csharp
/// <summary>
/// 关系标识符
/// </summary>
public readonly struct RelationId : IEquatable<RelationId>
{
    public string Value { get; }

    public RelationId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("RelationId cannot be null or empty", nameof(value));
        Value = value;
    }

    public static RelationId Generate() => new(Guid.NewGuid().ToString("N")[..12]);

    public bool Equals(RelationId other) => Value == other.Value;
    public override bool Equals(object? obj) => obj is RelationId other && Equals(other);
    public override int GetHashCode() => Value.GetHashCode();
    public override string ToString() => Value;

    public static implicit operator string(RelationId relationId) => relationId.Value;
    public static explicit operator RelationId(string value) => new(value);
}
```

## 2. 关系类型定义存储

### 2.1 IRelationTypeStorage

关系类型定义存储接口负责管理关系类型的元数据和配置信息。

```csharp
/// <summary>
/// 关系类型定义存储接口
/// </summary>
public interface IRelationTypeStorage
{
    /// <summary>
    /// 获取关系类型定义
    /// </summary>
    Task<RelationTypeDefinition?> GetRelationTypeAsync(RelationType relationType, CancellationToken cancellationToken = default);

    /// <summary>
    /// 保存关系类型定义
    /// </summary>
    Task SaveRelationTypeAsync(RelationTypeDefinition definition, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取所有关系类型定义
    /// </summary>
    Task<IReadOnlyList<RelationTypeDefinition>> GetAllRelationTypesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 删除关系类型定义
    /// </summary>
    Task DeleteRelationTypeAsync(RelationType relationType, CancellationToken cancellationToken = default);
}
```

### 2.2 RelationTypeDefinition - 关系类型定义

```csharp
/// <summary>
/// 关系类型定义
/// </summary>
public record RelationTypeDefinition
{
    public RelationType Type { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public bool IsBidirectional { get; init; } = false;
    public string Color { get; init; } = "#000000";
    public IReadOnlyDictionary<string, object> Metadata { get; init; } =
        new Dictionary<string, object>();
}
```

## 3. 节点层次结构存储

### 3.1 INodeHierarchyStorage

节点层次结构存储接口专门处理父子关系，基于独立的存储机制。

```csharp
/// <summary>
/// 节点层次结构存储接口（基于ParentChildrens文件夹的独立存储）
/// </summary>
public interface INodeHierarchyStorage
{
    /// <summary>
    /// 获取父子关系信息
    /// </summary>
    Task<ParentChildrenInfo?> GetParentChildrenInfoAsync(NodeId parentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 保存父子关系信息
    /// </summary>
    Task SaveParentChildrenInfoAsync(ParentChildrenInfo parentChildrenInfo, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取子节点ID列表（有序）
    /// </summary>
    Task<IReadOnlyList<NodeId>> GetChildrenAsync(NodeId parentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取父节点ID（通过运行时索引）
    /// </summary>
    Task<NodeId?> GetParentAsync(NodeId nodeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 添加子节点
    /// </summary>
    Task AddChildAsync(NodeId parentId, NodeId childId, int? order = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// 移除子节点
    /// </summary>
    Task RemoveChildAsync(NodeId parentId, NodeId childId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 移动节点到新父节点
    /// </summary>
    Task MoveNodeAsync(NodeId nodeId, NodeId? newParentId, int? newOrder = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// 重新排序子节点
    /// </summary>
    Task ReorderChildrenAsync(NodeId parentId, IReadOnlyList<NodeId> orderedChildIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取节点路径（从根到节点）
    /// </summary>
    Task<IReadOnlyList<NodeId>> GetPathAsync(NodeId nodeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取子树中的所有节点ID
    /// </summary>
    IAsyncEnumerable<NodeId> GetDescendantsAsync(NodeId rootId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 构建运行时反向索引（子节点到父节点的映射）
    /// </summary>
    Task<IReadOnlyDictionary<NodeId, NodeId>> BuildParentIndexAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 检查节点是否有子节点
    /// </summary>
    Task<bool> HasChildrenAsync(NodeId nodeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取节点的层级深度
    /// </summary>
    Task<int> GetDepthAsync(NodeId nodeId, CancellationToken cancellationToken = default);
}
```

### 3.2 层次结构数据类型

#### ParentChildrenInfo - 父子关系信息

```csharp
/// <summary>
/// 父子关系信息（独立存储）
/// </summary>
public record ParentChildrenInfo
{
    public NodeId ParentId { get; init; }
    public IReadOnlyList<ChildNodeInfo> Children { get; init; } = Array.Empty<ChildNodeInfo>();
    public DateTime LastModified { get; init; } = DateTime.UtcNow;
}
```

#### ChildNodeInfo - 子节点信息

```csharp
/// <summary>
/// 子节点信息
/// </summary>
public record ChildNodeInfo
{
    public NodeId NodeId { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public int Order { get; init; } = 0;
}
```

## 4. 关系管理服务接口

### 4.1 IRelationManagementService

关系管理服务提供高级的关系操作和查询功能，整合了语义关系和层次关系的管理。

```csharp
/// <summary>
/// 关系管理服务接口
/// </summary>
public interface IRelationManagementService
{
    /// <summary>
    /// 创建关系
    /// </summary>
    Task<RelationId> CreateRelationAsync(NodeId sourceId, NodeId targetId, RelationType relationType, string description = "", CancellationToken cancellationToken = default);

    /// <summary>
    /// 删除关系
    /// </summary>
    Task DeleteRelationAsync(RelationId relationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 更新关系描述
    /// </summary>
    Task UpdateRelationDescriptionAsync(RelationId relationId, string description, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取节点的关系图
    /// </summary>
    Task<RelationGraph> GetRelationGraphAsync(NodeId nodeId, int maxDepth = 2, CancellationToken cancellationToken = default);

    /// <summary>
    /// 查找关系路径
    /// </summary>
    Task<IReadOnlyList<RelationPath>> FindPathsAsync(NodeId sourceId, NodeId targetId, int maxDepth = 5, CancellationToken cancellationToken = default);

    /// <summary>
    /// 验证关系的有效性
    /// </summary>
    Task<ValidationResult> ValidateRelationAsync(NodeId sourceId, NodeId targetId, RelationType relationType, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取关系统计信息
    /// </summary>
    Task<RelationStatistics> GetRelationStatisticsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 清理孤立关系
    /// </summary>
    Task<int> CleanupOrphanedRelationsAsync(CancellationToken cancellationToken = default);
}
```

### 4.2 关系分析数据类型

#### RelationGraph - 关系图

```csharp
/// <summary>
/// 关系图
/// </summary>
public record RelationGraph
{
    public NodeId CenterNodeId { get; init; }
    public IReadOnlyList<NodeRelation> Relations { get; init; } = Array.Empty<NodeRelation>();
    public IReadOnlyList<NodeId> ConnectedNodes { get; init; } = Array.Empty<NodeId>();
    public int MaxDepth { get; init; }
    public DateTime GeneratedAt { get; init; } = DateTime.UtcNow;
}
```

#### RelationPath - 关系路径

```csharp
/// <summary>
/// 关系路径
/// </summary>
public record RelationPath
{
    public NodeId SourceId { get; init; }
    public NodeId TargetId { get; init; }
    public IReadOnlyList<NodeRelation> Relations { get; init; } = Array.Empty<NodeRelation>();
    public IReadOnlyList<NodeId> IntermediateNodes { get; init; } = Array.Empty<NodeId>();
    public int Length { get; init; }
    public double Weight { get; init; }
}
```

#### RelationStatistics - 关系统计信息

```csharp
/// <summary>
/// 关系统计信息
/// </summary>
public record RelationStatistics
{
    public int TotalRelations { get; init; }
    public IReadOnlyDictionary<RelationType, int> RelationsByType { get; init; } =
        new Dictionary<RelationType, int>();
    public int NodesWithRelations { get; init; }
    public int OrphanedRelations { get; init; }
    public double AverageRelationsPerNode { get; init; }
    public DateTime LastUpdated { get; init; } = DateTime.UtcNow;
}
```

## 5. 事件系统支持

### 5.1 关系变更事件

#### NodeRelationChangedEvent - 语义关系变更事件

```csharp
/// <summary>
/// 节点语义关系变更事件（集中存储版本）
/// </summary>
public record NodeRelationChangedEvent : NodeChangeEvent
{
    public RelationId RelationId { get; init; }
    public NodeId TargetNodeId { get; init; }
    public RelationType RelationType { get; init; }
    public RelationChangeType ChangeType { get; init; }
    public string Description { get; init; } = string.Empty;
    public NodeRelation? OldRelation { get; init; }
    public NodeRelation? NewRelation { get; init; }
}
```

#### NodeHierarchyChangedEvent - 层次结构变更事件

```csharp
/// <summary>
/// 节点层次结构变更事件
/// </summary>
public record NodeHierarchyChangedEvent : NodeChangeEvent
{
    public NodeId? OldParentId { get; init; }
    public NodeId? NewParentId { get; init; }
    public int? OldOrder { get; init; }
    public int? NewOrder { get; init; }
    public HierarchyChangeType ChangeType { get; init; }
}
```

### 5.2 变更类型枚举

#### RelationChangeType - 语义关系变更类型

```csharp
/// <summary>
/// 语义关系变更类型
/// </summary>
public enum RelationChangeType
{
    /// <summary>
    /// 关系被创建
    /// </summary>
    Created,

    /// <summary>
    /// 关系被更新
    /// </summary>
    Updated,

    /// <summary>
    /// 关系被删除
    /// </summary>
    Deleted
}
```

#### HierarchyChangeType - 层次结构变更类型

```csharp
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
1. **INodeRelationStorage** - 语义关系的基础存储功能
2. **INodeHierarchyStorage** - 层次结构的基础存储功能
3. **核心数据类型** - NodeRelation, ParentChildrenInfo 等基础类型

### 中优先级 (Phase 2.2)
1. **IRelationTypeStorage** - 关系类型定义管理
2. **IRelationManagementService** - 高级关系管理服务
3. **关系分析功能** - RelationGraph, RelationPath 等

### 低优先级 (Phase 2.3)
1. **事件系统集成** - 关系变更事件的完整支持
2. **性能优化** - 批量操作和缓存策略
3. **高级查询** - 复杂关系查询和分析功能

## 最佳实践

### 存储设计原则
1. **分离关注点**: 语义关系与层次关系分别存储
2. **原子性操作**: 确保关系操作的事务性
3. **索引优化**: 为常用查询模式建立适当索引
4. **数据一致性**: 维护关系数据的引用完整性

### 性能考虑
1. **批量操作**: 优先使用批量接口减少I/O开销
2. **异步枚举**: 使用 IAsyncEnumerable 处理大量数据
3. **缓存策略**: 对频繁访问的关系数据进行缓存
4. **延迟加载**: 按需加载关系详细信息

### 扩展性设计
1. **接口隔离**: 每个存储接口专注于特定职责
2. **类型安全**: 使用强类型标识符和枚举
3. **元数据支持**: 支持关系和类型的自定义属性
4. **版本兼容**: 考虑未来的架构演进需求

### 关系管理策略
1. **关系验证**: 在创建关系前验证节点存在性和关系合法性
2. **循环检测**: 防止在层次关系中创建循环依赖
3. **孤立清理**: 定期清理指向不存在节点的孤立关系
4. **统计维护**: 实时或定期更新关系统计信息

### 事务处理
1. **原子操作**: 确保复杂关系操作的原子性
2. **回滚机制**: 支持操作失败时的完整回滚
3. **并发控制**: 处理多用户同时修改关系的情况
4. **一致性检查**: 定期验证关系数据的一致性

---

**下一阶段**: [Phase2_ViewStorage.md](Phase2_ViewStorage.md) - 视图状态存储和缓存策略
