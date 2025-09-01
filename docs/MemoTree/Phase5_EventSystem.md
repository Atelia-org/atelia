# MemoTree 事件系统 (Phase 5)

> **版本**: v1.0  
> **创建时间**: 2025-07-25  
> **阶段**: Phase 5 - 高级特性层  
> **依赖**: Phase1_CoreTypes.md  
> **源位置**: Core_Types_Design.md 第8节  

## 概述

事件系统是MemoTree的核心组件之一，提供完整的事件驱动架构支持。系统采用发布-订阅模式，支持节点变更事件的实时通知和处理，为系统的响应性和扩展性提供基础。

### 主要特性

- **完整的节点变更事件体系**：涵盖创建、更新、删除、层次结构变更和关系变更
- **类型安全的发布订阅接口**：基于泛型的强类型事件处理
- **异步事件处理**：支持异步事件发布和订阅
- **批量事件支持**：优化性能的批量事件发布机制
- **事件溯源能力**：完整的事件历史记录和回放支持

## 节点变更事件

### 事件基类

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
```

### 具体事件类型

#### 节点创建事件

```csharp
/// <summary>
/// 节点创建事件
/// </summary>
public record NodeCreatedEvent : NodeChangeEvent
{
    public NodeType NodeType { get; init; }
    public string Title { get; init; } = string.Empty;
    public NodeId? ParentId { get; init; }
}
```

#### 节点更新事件

```csharp
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
```

#### 节点删除事件

```csharp
/// <summary>
/// 节点删除事件
/// </summary>
public record NodeDeletedEvent : NodeChangeEvent
{
    public NodeType NodeType { get; init; }
    public string Title { get; init; } = string.Empty;
    public bool WasRecursive { get; init; }
}
```

#### 层次结构变更事件

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

#### 语义关系变更事件

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

## 事件类型枚举

### 层次结构变更类型

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
```

### 语义关系变更类型

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

## 发布订阅接口

### 事件发布器

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
```

### 事件订阅器

```csharp
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

## 使用示例

### 事件订阅示例

```csharp
// 订阅节点变更事件
var eventSubscriber = serviceProvider.GetRequiredService<IEventSubscriber>();

var subscription = eventSubscriber.Subscribe<NodeUpdatedEvent>(async (evt, ct) =>
{
    // 处理节点更新事件
    await auditLogService.LogEventAsync(new AuditEvent
    {
        EventType = AuditEventType.NodeUpdated,
        UserId = evt.UserId,
        ResourceId = evt.NodeId,
        Description = $"Node '{evt.NodeId}' was updated at level {evt.Level}"
    }, ct);

    // 清理缓存
    await cacheService.InvalidateNodeAsync(evt.NodeId, ct);
});
```

### 事件发布示例

```csharp
// 发布事件
await eventPublisher.PublishAsync(new NodeCreatedEvent
{
    NodeId = newNodeId,
    NodeType = NodeType.Concept,
    Title = "新概念节点",
    ParentId = parentNodeId
});
```

## 实施优先级

### 高优先级
- **NodeChangeEvent基类**：所有事件的基础
- **基本事件类型**：NodeCreatedEvent、NodeUpdatedEvent、NodeDeletedEvent
- **发布订阅接口**：IEventPublisher、IEventSubscriber

### 中优先级
- **层次结构事件**：NodeHierarchyChangedEvent及相关枚举
- **关系变更事件**：NodeRelationChangedEvent及相关枚举

### 低优先级
- **高级事件处理**：事件过滤、事件聚合等扩展功能

## 最佳实践

1. **事件设计**：保持事件的不可变性，使用record类型
2. **异步处理**：所有事件处理器都应该是异步的
3. **错误处理**：事件处理失败不应影响主业务流程
4. **性能优化**：使用批量发布减少网络开销
5. **内存管理**：及时释放事件订阅以避免内存泄漏

---

**下一阶段**: [Phase5_Performance.md](Phase5_Performance.md) - 性能优化和缓存策略
