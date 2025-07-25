# MemoTree 外部数据源集成 (Phase 4)

> 版本: v1.0  
> 创建日期: 2025-07-25  
> 基于: Core_Types_Design.md 第4.3节  
> 依赖: [Phase1_CoreTypes.md](./Phase1_CoreTypes.md)

## 概述

本文档定义了MemoTree系统的外部数据源集成功能，包括Roslyn代码分析集成和Agent环境信息服务。这些服务为系统提供了与外部代码库和运行环境的交互能力，是Phase 4集成层的重要组成部分。

外部集成服务主要包含两个核心组件：
1. **Roslyn集成服务** - 提供代码分析、重构和变更监听功能
2. **环境信息服务** - 提供系统状态、上下文使用情况和用户偏好管理

## Roslyn代码分析集成

### 核心服务接口

```csharp
/// <summary>
/// Roslyn代码分析服务接口
/// </summary>
public interface IRoslynIntegrationService
{
    /// <summary>
    /// 加载代码库工作空间
    /// </summary>
    Task LoadWorkspaceAsync(string solutionPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取代码库结构
    /// </summary>
    Task<CodebaseStructure> GetCodebaseStructureAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 分析符号信息
    /// </summary>
    Task<SymbolInfo> AnalyzeSymbolAsync(string symbolName, CancellationToken cancellationToken = default);

    /// <summary>
    /// 执行重构操作
    /// </summary>
    Task<RefactoringResult> ExecuteRefactoringAsync(RefactoringOperation operation, CancellationToken cancellationToken = default);

    /// <summary>
    /// 监听代码变更
    /// </summary>
    IObservable<CodeChangeEvent> ObserveCodeChanges();
}
```

### 代码库结构类型

```csharp
/// <summary>
/// 代码库结构
/// </summary>
public record CodebaseStructure
{
    public string SolutionPath { get; init; } = string.Empty;
    public IReadOnlyList<ProjectInfo> Projects { get; init; } = Array.Empty<ProjectInfo>();
    public DateTime LastAnalyzed { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// 项目信息
/// </summary>
public record ProjectInfo
{
    public string Name { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public IReadOnlyList<NamespaceInfo> Namespaces { get; init; } = Array.Empty<NamespaceInfo>();
}

/// <summary>
/// 命名空间信息
/// </summary>
public record NamespaceInfo
{
    public string Name { get; init; } = string.Empty;
    public IReadOnlyList<TypeInfo> Types { get; init; } = Array.Empty<TypeInfo>();
}

/// <summary>
/// 类型信息
/// </summary>
public record TypeInfo
{
    public string Name { get; init; } = string.Empty;
    public string FullName { get; init; } = string.Empty;
    public TypeKind Kind { get; init; }
    public IReadOnlyList<MemberInfo> Members { get; init; } = Array.Empty<MemberInfo>();
    public string Documentation { get; init; } = string.Empty;
}

/// <summary>
/// 成员信息
/// </summary>
public record MemberInfo
{
    public string Name { get; init; } = string.Empty;
    public MemberKind Kind { get; init; }
    public string Signature { get; init; } = string.Empty;
    public string Documentation { get; init; } = string.Empty;
}
```

### 枚举类型定义

```csharp
/// <summary>
/// 类型种类
/// </summary>
public enum TypeKind
{
    Class,
    Interface,
    Struct,
    Enum,
    Delegate
}

/// <summary>
/// 成员种类
/// </summary>
public enum MemberKind
{
    Field,
    Property,
    Method,
    Constructor,
    Event
}
```

### 符号分析类型

```csharp
/// <summary>
/// 符号信息
/// </summary>
public record SymbolInfo
{
    public string Name { get; init; } = string.Empty;
    public string FullName { get; init; } = string.Empty;
    public SymbolKind Kind { get; init; }
    public string Documentation { get; init; } = string.Empty;
    public IReadOnlyList<string> References { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Dependencies { get; init; } = Array.Empty<string>();
}

/// <summary>
/// 符号种类
/// </summary>
public enum SymbolKind
{
    Namespace,
    Type,
    Method,
    Property,
    Field,
    Event,
    Parameter,
    Local
}
```

### 重构操作类型

```csharp
/// <summary>
/// 重构操作
/// </summary>
public abstract record RefactoringOperation
{
    public string Description { get; init; } = string.Empty;
}

/// <summary>
/// 重命名重构操作
/// </summary>
public record RenameRefactoringOperation : RefactoringOperation
{
    public string OldName { get; init; } = string.Empty;
    public string NewName { get; init; } = string.Empty;
    public string SymbolKind { get; init; } = string.Empty;
}

/// <summary>
/// 重构结果
/// </summary>
public record RefactoringResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public IReadOnlyList<string> ChangedFiles { get; init; } = Array.Empty<string>();
}
```

### 代码变更监听

```csharp
/// <summary>
/// 代码变更事件
/// </summary>
public record CodeChangeEvent
{
    public string FilePath { get; init; } = string.Empty;
    public ChangeType ChangeType { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public IReadOnlyList<string> AffectedSymbols { get; init; } = Array.Empty<string>();
}

/// <summary>
/// 变更类型
/// </summary>
public enum ChangeType
{
    Added,
    Modified,
    Deleted,
    Renamed
}
```

## Agent环境信息服务

### 环境服务接口

```csharp
/// <summary>
/// Agent环境信息服务接口
/// </summary>
public interface IAgentEnvironmentService
{
    /// <summary>
    /// 获取当前上下文使用情况
    /// </summary>
    Task<ContextUsageInfo> GetContextUsageAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取系统状态信息
    /// </summary>
    Task<SystemStatusInfo> GetSystemStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取用户偏好设置
    /// </summary>
    Task<UserPreferences> GetUserPreferencesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 更新用户偏好设置
    /// </summary>
    Task UpdateUserPreferencesAsync(UserPreferences preferences, CancellationToken cancellationToken = default);
}
```

### 上下文使用情况

```csharp
/// <summary>
/// 上下文使用情况信息
/// </summary>
public record ContextUsageInfo
{
    public int CurrentTokens { get; init; }
    public int MaxTokens { get; init; }
    public double UsagePercentage { get; init; }
    public int ActiveNodes { get; init; }
    public int ExpandedNodes { get; init; }
    public DateTime LastUpdated { get; init; } = DateTime.UtcNow;
}
```

### 系统状态信息

```csharp
/// <summary>
/// 系统状态信息
/// </summary>
public record SystemStatusInfo
{
    public DateTime CurrentTime { get; init; } = DateTime.UtcNow;
    public string TimeZone { get; init; } = string.Empty;
    public string Location { get; init; } = string.Empty;
    public double MemoryUsageMB { get; init; }
    public double CpuUsagePercentage { get; init; }
    public string Version { get; init; } = string.Empty;
    public TimeSpan Uptime { get; init; }
}
```

### 用户偏好设置

```csharp
/// <summary>
/// 用户偏好设置
/// </summary>
public record UserPreferences
{
    public LodLevel DefaultLodLevel { get; init; } = LodLevel.Summary;
    public int MaxContextTokens { get; init; } = 8000;
    public bool AutoSaveEnabled { get; init; } = true;
    public TimeSpan AutoSaveInterval { get; init; } = TimeSpan.FromMinutes(5);
    public string PreferredLanguage { get; init; } = "zh-CN";
    public IReadOnlyDictionary<string, object> CustomSettings { get; init; } =
        new Dictionary<string, object>();
}
```

## 实施优先级

### 高优先级 (P0)
1. **IRoslynIntegrationService基础接口** - 代码分析的核心功能
2. **IAgentEnvironmentService基础接口** - 环境信息获取的基础功能
3. **核心数据类型** - CodebaseStructure、SymbolInfo等基础类型

### 中优先级 (P1)
1. **重构操作支持** - RefactoringOperation及其实现
2. **代码变更监听** - CodeChangeEvent和变更通知机制
3. **用户偏好管理** - UserPreferences的完整实现

### 低优先级 (P2)
1. **高级重构操作** - 复杂的重构操作类型扩展
2. **性能监控** - 详细的系统性能指标
3. **扩展配置** - CustomSettings的高级功能

## 最佳实践指南

### Roslyn集成最佳实践
1. **工作空间管理** - 合理管理Roslyn工作空间的生命周期
2. **符号缓存** - 实现符号信息的有效缓存机制
3. **变更监听** - 使用响应式编程模式处理代码变更事件
4. **错误处理** - 妥善处理代码分析过程中的异常情况

### 环境服务最佳实践
1. **资源监控** - 定期监控系统资源使用情况
2. **配置持久化** - 确保用户偏好设置的可靠存储
3. **上下文管理** - 实时跟踪上下文窗口的使用情况
4. **性能优化** - 避免频繁的系统状态查询

---

**下一阶段**: [Phase4_VersionControl.md](./Phase4_VersionControl.md) - 版本控制集成
