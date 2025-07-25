# MemoTree 约束和验证系统 (Phase 1)

> **版本**: v1.2  
> **创建日期**: 2025-07-24  
> **基于**: Core_Types_Design.md 第11节、NodeConstraints、SystemLimits  
> **依赖**: [Phase1_CoreTypes.md](Phase1_CoreTypes.md)  
> **状态**: 🚧 开发中  

## 概述

本文档定义了MemoTree系统中的约束定义、验证规则和系统限制。这些约束确保数据完整性、系统稳定性和性能优化。作为Phase 1的基础设施组件，这些约束将被后续的存储层和服务层广泛使用。

## 实施优先级

1. **立即实现**: ValidationResult、ValidationError、NodeConstraints、SystemLimits
2. **第一周**: INodeValidator接口、基础验证逻辑  
3. **第二周**: IBusinessRuleValidator接口、高级业务规则验证

## 1. 验证结果类型

### 1.1 验证结果

```csharp
/// <summary>
/// 验证结果
/// </summary>
public record ValidationResult
{
    public bool IsValid { get; init; }
    public IReadOnlyList<ValidationError> Errors { get; init; } = Array.Empty<ValidationError>();
    public IReadOnlyList<ValidationWarning> Warnings { get; init; } = Array.Empty<ValidationWarning>();

    public static ValidationResult Success() => new() { IsValid = true };
    public static ValidationResult Failure(params ValidationError[] errors) =>
        new() { IsValid = false, Errors = errors };
}
```

### 1.2 验证错误

```csharp
/// <summary>
/// 验证错误
/// </summary>
public record ValidationError
{
    public string Code { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string PropertyName { get; init; } = string.Empty;
    public object? AttemptedValue { get; init; }
}
```

### 1.3 验证警告

```csharp
/// <summary>
/// 验证警告
/// </summary>
public record ValidationWarning
{
    public string Code { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string PropertyName { get; init; } = string.Empty;
}
```

## 2. 验证器接口

### 2.1 节点验证器

```csharp
/// <summary>
/// 节点验证器接口
/// </summary>
public interface INodeValidator
{
    /// <summary>
    /// 验证节点元数据
    /// </summary>
    Task<ValidationResult> ValidateMetadataAsync(NodeMetadata metadata, CancellationToken cancellationToken = default);

    /// <summary>
    /// 验证节点内容
    /// </summary>
    Task<ValidationResult> ValidateContentAsync(NodeContent content, CancellationToken cancellationToken = default);

    /// <summary>
    /// 验证完整节点
    /// </summary>
    Task<ValidationResult> ValidateNodeAsync(CognitiveNode node, CancellationToken cancellationToken = default);

    /// <summary>
    /// 验证节点关系
    /// </summary>
    Task<ValidationResult> ValidateRelationAsync(NodeId sourceId, NodeId targetId, RelationType relationType, CancellationToken cancellationToken = default);
}
```

### 2.2 业务规则验证器

```csharp
/// <summary>
/// 业务规则验证器接口
/// </summary>
public interface IBusinessRuleValidator
{
    /// <summary>
    /// 验证节点创建规则
    /// </summary>
    Task<ValidationResult> ValidateNodeCreationAsync(NodeType type, NodeId? parentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 验证节点删除规则
    /// </summary>
    Task<ValidationResult> ValidateNodeDeletionAsync(NodeId nodeId, bool recursive, CancellationToken cancellationToken = default);

    /// <summary>
    /// 验证节点移动规则
    /// </summary>
    Task<ValidationResult> ValidateNodeMoveAsync(NodeId nodeId, NodeId? newParentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 验证循环引用
    /// </summary>
    Task<ValidationResult> ValidateCircularReferenceAsync(NodeId sourceId, NodeId targetId, CancellationToken cancellationToken = default);
}
```

## 3. 节点约束定义

```csharp
/// <summary>
/// 节点约束定义
/// </summary>
public static class NodeConstraints
{
    /// <summary>
    /// 节点ID最大长度
    /// </summary>
    public const int MaxNodeIdLength = 50;

    /// <summary>
    /// 节点标题最大长度
    /// </summary>
    public const int MaxTitleLength = 200;

    /// <summary>
    /// 节点内容最大长度（字符数）
    /// </summary>
    public const int MaxContentLength = 1_000_000;

    /// <summary>
    /// 最大标签数量
    /// </summary>
    public const int MaxTagCount = 20;

    /// <summary>
    /// 标签最大长度
    /// </summary>
    public const int MaxTagLength = 50;

    /// <summary>
    /// 最大关系数量
    /// </summary>
    public const int MaxRelationCount = 100;

    /// <summary>
    /// 关系描述最大长度
    /// </summary>
    public const int MaxRelationDescriptionLength = 500;

    /// <summary>
    /// 最大子节点数量
    /// </summary>
    public const int MaxChildrenCount = 1000;

    /// <summary>
    /// 最大树深度
    /// </summary>
    public const int MaxTreeDepth = 20;

    /// <summary>
    /// 外部链接路径最大长度
    /// </summary>
    public const int MaxExternalLinkPathLength = 1000;
}
```

## 4. 系统限制定义

```csharp
/// <summary>
/// 系统限制定义
/// </summary>
public static class SystemLimits
{
    /// <summary>
    /// 默认最大上下文Token数
    /// </summary>
    public const int DefaultMaxContextTokens = 8000;

    /// <summary>
    /// 最大并发操作数
    /// </summary>
    public const int MaxConcurrentOperations = 10;

    /// <summary>
    /// 最大搜索结果数
    /// </summary>
    public const int MaxSearchResults = 100;

    /// <summary>
    /// 缓存项最大生存时间（小时）
    /// </summary>
    public const int MaxCacheItemLifetimeHours = 24;

    /// <summary>
    /// 最大批处理大小
    /// </summary>
    public const int MaxBatchSize = 50;

    /// <summary>
    /// Git提交消息最大长度
    /// </summary>
    public const int MaxCommitMessageLength = 500;
}
```

## 5. 约束应用指南

### 5.1 验证时机
- **创建时验证**: 所有新节点必须通过完整验证
- **更新时验证**: 修改的属性必须重新验证
- **关系建立时验证**: 验证关系的合法性和循环引用

### 5.2 错误处理策略
- **硬约束**: 违反时抛出异常，阻止操作
- **软约束**: 违反时记录警告，允许操作继续
- **业务规则**: 根据具体场景决定处理方式

### 5.3 性能考虑
- 验证操作应该是异步的
- 批量操作时应该批量验证
- 缓存验证结果以提高性能

---
**下一阶段**: [Phase1_Exceptions.md](Phase1_Exceptions.md)  
**相关文档**: [Phase1_CoreTypes.md](Phase1_CoreTypes.md), [Phase2_StorageInterfaces.md](Phase2_StorageInterfaces.md)
