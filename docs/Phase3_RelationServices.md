# MemoTree 关系管理服务 (Phase 3)

> **版本**: v1.0  
> **创建时间**: 2025-07-25  
> **依赖文档**: [Phase1_CoreTypes.md](./Phase1_CoreTypes.md), [Phase2_RelationStorage.md](./Phase2_RelationStorage.md)  
> **相关文档**: [Phase3_CoreServices.md](./Phase3_CoreServices.md)  

## 概述

本文档定义了MemoTree系统中的关系管理服务接口，包括关系创建、删除、查询、验证和统计等核心功能。关系管理服务是连接存储层和应用层的重要桥梁，提供了高级的关系操作抽象。

## 1. 关系管理服务接口

### 1.1 IRelationManagementService

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

## 2. 关系数据类型

### 2.1 关系图

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

### 2.2 关系路径

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

### 2.3 关系统计信息

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

## 3. 关系类型定义

### 3.1 关系类型定义

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

## 4. 关系管理配置

### 4.1 RelationOptions

```csharp
/// <summary>
/// 关系管理配置选项
/// </summary>
public class RelationOptions
{
    /// <summary>
    /// 是否启用父子关系独立存储
    /// </summary>
    public bool EnableIndependentHierarchyStorage { get; set; } = true;

    /// <summary>
    /// 父子关系存储目录
    /// </summary>
    public string HierarchyStorageDirectory { get; set; } = "./ParentChildrens";

    /// <summary>
    /// 是否启用语义关系数据集中存储
    /// </summary>
    public bool EnableCentralizedRelationStorage { get; set; } = true;

    /// <summary>
    /// 语义关系数据存储目录
    /// </summary>
    public string RelationStorageDirectory { get; set; } = "./Relations";

    /// <summary>
    /// 最大关系深度
    /// </summary>
    public int MaxRelationDepth { get; set; } = 10;

    /// <summary>
    /// 关系图最大节点数
    /// </summary>
    public int MaxRelationGraphNodes { get; set; } = 1000;

    /// <summary>
    /// 是否启用关系验证
    /// </summary>
    public bool EnableRelationValidation { get; set; } = true;

    /// <summary>
    /// 是否自动清理孤立的语义关系
    /// </summary>
    public bool AutoCleanupOrphanedRelations { get; set; } = true;

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
}
```

## 5. 使用示例

### 5.1 基本关系操作

```csharp
// 创建节点关系
var relationService = serviceProvider.GetRequiredService<IRelationManagementService>();
var relationId = await relationService.CreateRelationAsync(
    sourceNodeId: new NodeId("node-001"),
    targetNodeId: new NodeId("node-002"),
    relationType: RelationType.References,
    description: "引用了架构设计原则");

// 获取节点的关系图
var relationGraph = await relationService.GetRelationGraphAsync(
    nodeId: new NodeId("node-001"),
    maxDepth: 2);

Console.WriteLine($"找到 {relationGraph.Relations.Count} 个关系");
foreach (var relation in relationGraph.Relations)
{
    Console.WriteLine($"{relation.SourceId} --{relation.Type}--> {relation.TargetId}: {relation.Description}");
}
```

### 5.2 路径查找和统计

```csharp
// 查找两个节点之间的路径
var paths = await relationService.FindPathsAsync(
    sourceId: new NodeId("node-001"),
    targetId: new NodeId("node-010"),
    maxDepth: 5);

if (paths.Any())
{
    var shortestPath = paths.OrderBy(p => p.Length).First();
    Console.WriteLine($"最短路径长度: {shortestPath.Length}");
}

// 获取关系统计信息
var stats = await relationService.GetRelationStatisticsAsync();
Console.WriteLine($"总关系数: {stats.TotalRelations}");
Console.WriteLine($"平均每节点关系数: {stats.AverageRelationsPerNode:F2}");
```

## 实施优先级

### 高优先级 (P0)
- **IRelationManagementService**: 核心关系管理接口
- **RelationGraph**: 关系图数据结构
- **RelationPath**: 路径查找结果
- **RelationStatistics**: 统计信息类型

### 中优先级 (P1)
- **RelationTypeDefinition**: 关系类型定义
- **RelationOptions**: 配置选项
- **关系验证功能**: 确保数据一致性

### 低优先级 (P2)
- **高级路径算法**: 复杂路径查找优化
- **关系缓存策略**: 性能优化
- **关系分析工具**: 统计和分析功能

---

**下一阶段**: [Phase3_EditingServices.md](./Phase3_EditingServices.md) - 编辑操作和LOD生成服务
