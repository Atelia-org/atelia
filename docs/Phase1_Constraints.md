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
/// 定义系统级别的硬约束，这些值不可通过配置修改
/// </summary>
public static class SystemLimits
{
    /// <summary>
    /// 单个认知节点的默认最大上下文Token数
    /// 用于单个CogNode内容的Token限制
    /// </summary>
    public const int DefaultMaxContextTokens = 8000;

    /// <summary>
    /// 整个MemoTree视图的最大Token数下限
    /// 确保整个视图至少能容纳基本的上下文信息
    /// </summary>
    public const int MinMemoTreeViewTokens = 128_000;

    /// <summary>
    /// 整个MemoTree视图的最大Token数上限
    /// 防止视图过大导致性能问题
    /// </summary>
    public const int MaxMemoTreeViewTokens = 200_000;

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

## 5. 配置约束验证器

```csharp
/// <summary>
/// 配置约束验证器接口
/// 用于验证配置值是否符合系统硬约束
/// </summary>
public interface IConfigurationValidator
{
    /// <summary>
    /// 验证MemoTree配置选项
    /// </summary>
    ValidationResult ValidateMemoTreeOptions(MemoTreeOptions options);

    /// <summary>
    /// 验证关系配置选项
    /// </summary>
    ValidationResult ValidateRelationOptions(RelationOptions options);

    /// <summary>
    /// 验证Token相关配置
    /// </summary>
    ValidationResult ValidateTokenLimits(int nodeTokens, int viewTokens);
}

/// <summary>
/// 默认配置验证器实现
/// </summary>
public class DefaultConfigurationValidator : IConfigurationValidator
{
    public ValidationResult ValidateMemoTreeOptions(MemoTreeOptions options)
    {
        var errors = new List<ValidationError>();

        // 验证Token限制
        if (options.DefaultMaxContextTokens > SystemLimits.DefaultMaxContextTokens)
        {
            errors.Add(new ValidationError
            {
                Code = "TOKEN_LIMIT_EXCEEDED",
                Message = $"配置的DefaultMaxContextTokens ({options.DefaultMaxContextTokens}) 超过系统硬限制 ({SystemLimits.DefaultMaxContextTokens})",
                PropertyName = nameof(options.DefaultMaxContextTokens),
                AttemptedValue = options.DefaultMaxContextTokens
            });
        }

        return errors.Count == 0 ? ValidationResult.Success() : ValidationResult.Failure(errors.ToArray());
    }

    public ValidationResult ValidateRelationOptions(RelationOptions options)
    {
        var errors = new List<ValidationError>();

        // 验证关系图节点数不超过子节点限制
        if (options.MaxRelationGraphNodes > NodeConstraints.MaxChildrenCount)
        {
            errors.Add(new ValidationError
            {
                Code = "RELATION_GRAPH_SIZE_EXCEEDED",
                Message = $"配置的MaxRelationGraphNodes ({options.MaxRelationGraphNodes}) 超过节点子节点硬限制 ({NodeConstraints.MaxChildrenCount})",
                PropertyName = nameof(options.MaxRelationGraphNodes),
                AttemptedValue = options.MaxRelationGraphNodes
            });
        }

        // 验证关系深度合理性
        if (options.MaxRelationDepth > NodeConstraints.MaxTreeDepth)
        {
            errors.Add(new ValidationError
            {
                Code = "RELATION_DEPTH_EXCEEDED",
                Message = $"配置的MaxRelationDepth ({options.MaxRelationDepth}) 超过树深度硬限制 ({NodeConstraints.MaxTreeDepth})",
                PropertyName = nameof(options.MaxRelationDepth),
                AttemptedValue = options.MaxRelationDepth
            });
        }

        return errors.Count == 0 ? ValidationResult.Success() : ValidationResult.Failure(errors.ToArray());
    }

    public ValidationResult ValidateTokenLimits(int nodeTokens, int viewTokens)
    {
        var errors = new List<ValidationError>();

        if (viewTokens < SystemLimits.MinMemoTreeViewTokens)
        {
            errors.Add(new ValidationError
            {
                Code = "VIEW_TOKENS_TOO_LOW",
                Message = $"MemoTree视图Token数 ({viewTokens}) 低于最小限制 ({SystemLimits.MinMemoTreeViewTokens})",
                PropertyName = "ViewTokens",
                AttemptedValue = viewTokens
            });
        }

        if (viewTokens > SystemLimits.MaxMemoTreeViewTokens)
        {
            errors.Add(new ValidationError
            {
                Code = "VIEW_TOKENS_TOO_HIGH",
                Message = $"MemoTree视图Token数 ({viewTokens}) 超过最大限制 ({SystemLimits.MaxMemoTreeViewTokens})",
                PropertyName = "ViewTokens",
                AttemptedValue = viewTokens
            });
        }

        return errors.Count == 0 ? ValidationResult.Success() : ValidationResult.Failure(errors.ToArray());
    }
}
```

## 6. 约束层次和优先级

### 6.1 约束层次结构

```
约束层次 (从高到低优先级):
├── 系统硬约束 (SystemLimits + NodeConstraints)
│   ├── 不可配置的技术限制
│   ├── 确保系统稳定性和安全性
│   └── 违反时必须阻止操作
│
├── 配置软约束 (MemoTreeOptions + RelationOptions)
│   ├── 可通过配置调整的业务限制
│   ├── 不能超过系统硬约束
│   └── 违反时可记录警告或阻止操作
│
└── 运行时动态约束
    ├── 基于当前系统状态的临时限制
    ├── 如内存使用、并发连接数等
    └── 可能动态调整配置约束
```

### 6.2 约束验证策略

1. **配置加载时验证**
   - 所有配置项必须通过`IConfigurationValidator`验证
   - 配置值不能超过对应的系统硬约束
   - 验证失败时阻止系统启动

2. **运行时验证**
   - 操作执行前检查相关约束
   - 优先检查系统硬约束，再检查配置约束
   - 支持约束的动态调整和重新验证

3. **约束冲突处理**
   - 系统硬约束始终优先
   - 配置约束与硬约束冲突时，使用硬约束值并记录警告
   - 提供约束冲突的详细错误信息

## 7. 约束应用指南

### 7.1 验证时机
- **系统启动时**: 验证所有配置约束
- **创建时验证**: 所有新节点必须通过完整验证
- **更新时验证**: 修改的属性必须重新验证
- **关系建立时验证**: 验证关系的合法性和循环引用

### 7.2 错误处理策略
- **系统硬约束**: 违反时抛出异常，阻止操作
- **配置软约束**: 违反时记录警告，可选择阻止操作
- **业务规则**: 根据具体场景决定处理方式

### 7.3 性能考虑
- 验证操作应该是异步的
- 批量操作时应该批量验证
- 缓存验证结果以提高性能
- 配置验证结果可缓存，避免重复验证

---
**下一阶段**: [Phase1_Exceptions.md](Phase1_Exceptions.md)  
**相关文档**: [Phase1_CoreTypes.md](Phase1_CoreTypes.md), [Phase2_StorageInterfaces.md](Phase2_StorageInterfaces.md)
