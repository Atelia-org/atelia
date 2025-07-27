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
/// MVP阶段：采用简单直接的验证逻辑，优化代码结构但保持零依赖
/// TODO Phase5: 考虑引入FluentValidation以支持更复杂的验证场景
/// </summary>
public class DefaultConfigurationValidator : IConfigurationValidator
{
    public ValidationResult ValidateMemoTreeOptions(MemoTreeOptions options)
    {
        var errors = new List<ValidationError>();

        // 验证Token限制
        ValidateTokenLimit(
            errors,
            options.DefaultMaxContextTokens,
            SystemLimits.DefaultMaxContextTokens,
            nameof(options.DefaultMaxContextTokens),
            "TOKEN_LIMIT_EXCEEDED",
            "DefaultMaxContextTokens"
        );

        return CreateValidationResult(errors);
    }

    public ValidationResult ValidateRelationOptions(RelationOptions options)
    {
        var errors = new List<ValidationError>();

        // 验证关系图节点数不超过子节点限制
        ValidateMaxLimit(
            errors,
            options.MaxRelationGraphNodes,
            NodeConstraints.MaxChildrenCount,
            nameof(options.MaxRelationGraphNodes),
            "RELATION_GRAPH_SIZE_EXCEEDED",
            "MaxRelationGraphNodes",
            "节点子节点硬限制"
        );

        // 验证关系深度合理性
        ValidateMaxLimit(
            errors,
            options.MaxRelationDepth,
            NodeConstraints.MaxTreeDepth,
            nameof(options.MaxRelationDepth),
            "RELATION_DEPTH_EXCEEDED",
            "MaxRelationDepth",
            "树深度硬限制"
        );

        return CreateValidationResult(errors);
    }

    public ValidationResult ValidateTokenLimits(int nodeTokens, int viewTokens)
    {
        var errors = new List<ValidationError>();

        // 验证视图Token下限
        ValidateMinLimit(
            errors,
            viewTokens,
            SystemLimits.MinMemoTreeViewTokens,
            "ViewTokens",
            "VIEW_TOKENS_TOO_LOW",
            "MemoTree视图Token数",
            "最小限制"
        );

        // 验证视图Token上限
        ValidateMaxLimit(
            errors,
            viewTokens,
            SystemLimits.MaxMemoTreeViewTokens,
            "ViewTokens",
            "VIEW_TOKENS_TOO_HIGH",
            "MemoTree视图Token数",
            "最大限制"
        );

        return CreateValidationResult(errors);
    }

    // MVP阶段的验证辅助方法：减少重复代码，保持简单直接
    private static void ValidateTokenLimit(
        List<ValidationError> errors,
        int actualValue,
        int limitValue,
        string propertyName,
        string errorCode,
        string configName)
    {
        if (actualValue > limitValue)
        {
            errors.Add(CreateValidationError(
                errorCode,
                $"配置的{configName} ({actualValue}) 超过系统硬限制 ({limitValue})",
                propertyName,
                actualValue
            ));
        }
    }

    private static void ValidateMaxLimit(
        List<ValidationError> errors,
        int actualValue,
        int maxValue,
        string propertyName,
        string errorCode,
        string configName,
        string limitDescription)
    {
        if (actualValue > maxValue)
        {
            errors.Add(CreateValidationError(
                errorCode,
                $"配置的{configName} ({actualValue}) 超过{limitDescription} ({maxValue})",
                propertyName,
                actualValue
            ));
        }
    }

    private static void ValidateMinLimit(
        List<ValidationError> errors,
        int actualValue,
        int minValue,
        string propertyName,
        string errorCode,
        string configName,
        string limitDescription)
    {
        if (actualValue < minValue)
        {
            errors.Add(CreateValidationError(
                errorCode,
                $"{configName} ({actualValue}) 低于{limitDescription} ({minValue})",
                propertyName,
                actualValue
            ));
        }
    }

    private static ValidationError CreateValidationError(
        string code,
        string message,
        string propertyName,
        object attemptedValue)
    {
        return new ValidationError
        {
            Code = code,
            Message = message,
            PropertyName = propertyName,
            AttemptedValue = attemptedValue
        };
    }

    private static ValidationResult CreateValidationResult(List<ValidationError> errors)
    {
        return errors.Count == 0
            ? ValidationResult.Success()
            : ValidationResult.Failure(errors.ToArray());
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

## 8. 验证架构演进策略

### 8.1 MVP阶段 (当前实现)
**设计原则**: 简单直接，零外部依赖，易于理解和调试

**优势**:
- ✅ **零依赖**: 不引入外部验证库，减少项目复杂度
- ✅ **高性能**: 直接的条件判断，无反射开销
- ✅ **调试友好**: 验证逻辑一目了然，便于问题定位
- ✅ **测试简单**: 每个验证规则独立，易于单元测试

**当前优化**:
- 提取验证辅助方法，减少重复代码
- 统一错误创建模式，提高代码一致性
- 按验证类型分组，提升代码可读性

### 8.2 Phase 5 演进方案
**触发条件**: 验证规则超过10个，或需要复杂的组合验证逻辑

**候选技术方案**:

#### 方案A: FluentValidation (推荐)
```csharp
// 示例：声明式验证规则
public class RelationOptionsValidator : AbstractValidator<RelationOptions>
{
    public RelationOptionsValidator()
    {
        RuleFor(x => x.MaxRelationDepth)
            .LessThanOrEqualTo(NodeConstraints.MaxTreeDepth)
            .WithMessage($"不能超过树深度硬限制 ({NodeConstraints.MaxTreeDepth})");

        RuleFor(x => x.MaxRelationGraphNodes)
            .LessThanOrEqualTo(NodeConstraints.MaxChildrenCount)
            .WithMessage($"不能超过子节点硬限制 ({NodeConstraints.MaxChildrenCount})");
    }
}
```

**优势**:
- 声明式语法，规则清晰易读
- 丰富的内置验证器
- 支持复杂的条件验证和组合规则
- 优秀的错误消息本地化支持

#### 方案B: 自定义验证框架
```csharp
// 示例：基于特性的验证
public class RelationOptions
{
    [MaxValue(NodeConstraints.MaxTreeDepth, ErrorCode = "RELATION_DEPTH_EXCEEDED")]
    public int MaxRelationDepth { get; set; }

    [MaxValue(NodeConstraints.MaxChildrenCount, ErrorCode = "RELATION_GRAPH_SIZE_EXCEEDED")]
    public int MaxRelationGraphNodes { get; set; }
}
```

**优势**:
- 完全控制验证逻辑
- 与现有错误处理体系无缝集成
- 可针对MemoTree特定需求优化

### 8.3 迁移策略
1. **保持接口兼容**: `IConfigurationValidator`接口保持不变
2. **渐进式迁移**: 先迁移复杂验证规则，简单规则可保持现状
3. **性能基准测试**: 确保新方案不降低验证性能
4. **向后兼容**: 提供配置开关，允许回退到简单实现

### 8.4 决策建议
**当前阶段**: 保持现有实现，专注于核心功能开发
**未来规划**: 当验证规则复杂度达到阈值时，优先考虑FluentValidation

---
**下一阶段**: [Phase1_Exceptions.md](Phase1_Exceptions.md)
**相关文档**: [Phase1_CoreTypes.md](Phase1_CoreTypes.md), [Phase2_StorageInterfaces.md](Phase2_StorageInterfaces.md)
