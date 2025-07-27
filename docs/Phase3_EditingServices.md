# MemoTree 编辑操作服务 (Phase 3)

> **版本**: v1.0  
> **创建时间**: 2025-07-25  
> **文档类型**: 服务层接口定义  
> **依赖**: Phase1_CoreTypes.md, Phase3_CoreServices.md  
> **状态**: ✅ 完成

## 概述

本文档定义了MemoTree系统中的编辑操作服务接口，包括认知画布编辑器、异步LOD生成服务以及相关的事件系统。这些服务提供了完整的节点编辑、内容生成和事件通知功能。

### 核心组件

1. **ICognitiveCanvasEditor** - 认知画布编辑器接口
2. **ILodGenerationService** - 异步LOD内容生成服务
3. **编辑事件系统** - 节点变更事件定义
4. **事件发布订阅接口** - 事件通信机制

## 1. 认知画布编辑器接口

### 1.1 核心编辑接口

```csharp
/// <summary>
/// 认知画布编辑器接口
/// </summary>
public interface ICognitiveCanvasEditor
{
    /// <summary>
    /// 创建新节点（自动处理层次结构）
    /// </summary>
    Task<NodeId> CreateNodeAsync(NodeId? parentId, NodeType type, string title, string content, int? order = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// 更新节点内容
    /// </summary>
    Task UpdateNodeContentAsync(NodeId nodeId, LodLevel level, string content, CancellationToken cancellationToken = default);

    /// <summary>
    /// 更新节点元数据
    /// </summary>
    Task UpdateNodeMetadataAsync(NodeId nodeId, Action<NodeMetadata> updateAction, CancellationToken cancellationToken = default);

    /// <summary>
    /// 删除节点（自动处理层次结构清理）
    /// </summary>
    Task DeleteNodeAsync(NodeId nodeId, bool recursive = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// 移动节点（包括层次结构调整）
    /// </summary>
    Task MoveNodeAsync(NodeId nodeId, NodeId? newParentId, int? newOrder = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// 分割节点
    /// </summary>
    Task<NodeId> SplitNodeAsync(NodeId nodeId, string splitPoint, CancellationToken cancellationToken = default);

    /// <summary>
    /// 合并节点
    /// </summary>
    Task MergeNodesAsync(NodeId sourceNodeId, NodeId targetNodeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 添加节点关系
    /// </summary>
    Task<RelationId> AddRelationAsync(NodeId sourceNodeId, NodeId targetNodeId, RelationType relationType, string description = "", CancellationToken cancellationToken = default);

    /// <summary>
    /// 移除节点关系
    /// </summary>
    Task RemoveRelationAsync(RelationId relationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 更新节点关系
    /// </summary>
    Task UpdateRelationAsync(RelationId relationId, string description, CancellationToken cancellationToken = default);
}
```

## 2. 异步LOD生成服务

### 2.1 LOD生成服务接口

```csharp
/// <summary>
/// 异步LOD内容生成服务接口
/// </summary>
public interface ILodGenerationService
{
    /// <summary>
    /// 异步生成节点的LOD内容
    /// </summary>
    Task<GenerationResult> GenerateLodContentAsync(NodeId nodeId, LodLevel targetLevel, string sourceContent, CancellationToken cancellationToken = default);

    /// <summary>
    /// 批量生成多个节点的LOD内容
    /// </summary>
    Task<IReadOnlyList<GenerationResult>> GenerateBatchLodContentAsync(IEnumerable<LodGenerationRequest> requests, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取生成任务状态
    /// </summary>
    Task<GenerationStatus> GetGenerationStatusAsync(string taskId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 取消生成任务
    /// </summary>
    Task CancelGenerationAsync(string taskId, CancellationToken cancellationToken = default);
}
```

### 2.2 LOD生成相关类型

```csharp
/// <summary>
/// LOD生成请求
/// </summary>
public record LodGenerationRequest
{
    public NodeId NodeId { get; init; }
    public LodLevel TargetLevel { get; init; }
    public string SourceContent { get; init; } = string.Empty;
    public LodLevel SourceLevel { get; init; }
    public string TaskId { get; init; } = GuidEncoder.ToBase64String(Guid.NewGuid());
}

/// <summary>
/// LOD生成结果
/// </summary>
public record GenerationResult
{
    public string TaskId { get; init; } = string.Empty;
    public NodeId NodeId { get; init; }
    public LodLevel TargetLevel { get; init; }
    public bool Success { get; init; }
    public string GeneratedContent { get; init; } = string.Empty;
    public string ErrorMessage { get; init; } = string.Empty;
    public DateTime CompletedAt { get; init; } = DateTime.UtcNow;
    public TimeSpan Duration { get; init; }
}

/// <summary>
/// 生成任务状态
/// </summary>
public enum GenerationStatus
{
    /// <summary>
    /// 等待中
    /// </summary>
    Pending,

    /// <summary>
    /// 生成中
    /// </summary>
    InProgress,

    /// <summary>
    /// 已完成
    /// </summary>
    Completed,

    /// <summary>
    /// 已失败
    /// </summary>
    Failed,

    /// <summary>
    /// 已取消
    /// </summary>
    Cancelled
}
```

## 3. 编辑事件系统

### 3.1 节点变更事件

```csharp
/// <summary>
/// 节点变更事件基类
/// </summary>
public abstract record NodeChangeEvent
{
    public NodeId NodeId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public string UserId { get; init; } = string.Empty;
}

/// <summary>
/// 节点创建事件
/// </summary>
public record NodeCreatedEvent : NodeChangeEvent
{
    public NodeType NodeType { get; init; }
    public string Title { get; init; } = string.Empty;
    public NodeId? ParentId { get; init; }
}

/// <summary>
/// 节点更新事件
/// </summary>
public record NodeUpdatedEvent : NodeChangeEvent
{
    public LodLevel Level { get; init; }
    public string? OldContent { get; init; }
    public string? NewContent { get; init; }
    public NodeMetadata? OldMetadata { get; init; }
    public NodeMetadata? NewMetadata { get; init; }
}

/// <summary>
/// 节点删除事件
/// </summary>
public record NodeDeletedEvent : NodeChangeEvent
{
    public NodeType NodeType { get; init; }
    public string Title { get; init; } = string.Empty;
    public bool WasRecursive { get; init; }
}

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

### 3.2 事件类型枚举

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
    /// 节点移动到新父节点
    /// </summary>
    NodeMoved,

    /// <summary>
    /// 子节点重新排序
    /// </summary>
    ChildrenReordered
}

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

## 4. 事件发布订阅接口

### 4.1 事件通信接口

```csharp
/// <summary>
/// 事件发布器接口
/// </summary>
public interface IEventPublisher
{
    /// <summary>
    /// 发布事件
    /// </summary>
    Task PublishAsync<T>(T eventData, CancellationToken cancellationToken = default) where T : NodeChangeEvent;

    /// <summary>
    /// 批量发布事件
    /// </summary>
    Task PublishBatchAsync<T>(IEnumerable<T> events, CancellationToken cancellationToken = default) where T : NodeChangeEvent;
}

/// <summary>
/// 事件订阅器接口
/// </summary>
public interface IEventSubscriber
{
    /// <summary>
    /// 订阅特定类型的事件
    /// </summary>
    IDisposable Subscribe<T>(Func<T, CancellationToken, Task> handler) where T : NodeChangeEvent;

    /// <summary>
    /// 订阅所有节点变更事件
    /// </summary>
    IDisposable SubscribeAll(Func<NodeChangeEvent, CancellationToken, Task> handler);
}
```

## 实施优先级

### 高优先级 (P0)
- ICognitiveCanvasEditor 基础CRUD操作
- NodeChangeEvent 事件系统基础
- IEventPublisher/IEventSubscriber 基础实现

### 中优先级 (P1)
- ILodGenerationService 异步生成功能
- 节点分割和合并操作
- 完整的事件类型支持

### 低优先级 (P2)
- 批量操作优化
- 高级编辑功能
- 事件系统性能优化

---

**下一阶段**: [Phase3_RetrievalServices.md](Phase3_RetrievalServices.md)
