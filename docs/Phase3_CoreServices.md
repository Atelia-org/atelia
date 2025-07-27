# MemoTree 核心业务服务接口 (Phase 3)

> **版本**: v1.0  
> **阶段**: Phase 3 - Services (服务层阶段)  
> **依赖**: Phase1_CoreTypes.md, Phase2_StorageInterfaces.md  
> **文档行数**: ~400行  

## 概述

本文档定义了MemoTree系统的核心业务服务接口，包括认知画布服务、LOD生成服务、外部集成服务和环境信息服务。这些服务构成了系统的核心业务逻辑层，为上层应用提供高级的业务功能抽象。

### 核心服务组件

1. **认知画布服务** (`ICognitiveCanvasService`) - 核心画布渲染和节点操作
2. **LOD生成服务** (`ILodGenerationService`) - 异步内容层次生成
3. **外部集成服务** (`IRoslynIntegrationService`) - 代码分析集成
4. **环境信息服务** (`IAgentEnvironmentService`) - 系统状态和用户偏好

## 1. 认知画布核心服务

### 1.1 ICognitiveCanvasService 接口

```csharp
/// <summary>
/// 认知画布核心服务
/// </summary>
public interface ICognitiveCanvasService
{
    /// <summary>
    /// 渲染指定视图的Markdown内容
    /// </summary>
    Task<string> RenderViewAsync(string viewName, CancellationToken cancellationToken = default);

    /// <summary>
    /// 展开节点到指定LOD级别
    /// </summary>
    Task ExpandNodeAsync(string viewName, NodeId nodeId, LodLevel level, CancellationToken cancellationToken = default);

    /// <summary>
    /// 折叠节点
    /// </summary>
    Task CollapseNodeAsync(string viewName, NodeId nodeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取节点树结构（基于层次结构存储）
    /// </summary>
    Task<IReadOnlyList<NodeTreeItem>> GetNodeTreeAsync(NodeId? rootId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// 应用FIFO策略管理上下文窗口
    /// </summary>
    Task ApplyFifoStrategyAsync(string viewName, int maxTokens, CancellationToken cancellationToken = default);
}
```

### 1.2 节点树项数据结构

```csharp
/// <summary>
/// 节点树项
/// </summary>
public record NodeTreeItem
{
    public NodeId Id { get; init; }
    public string Title { get; init; } = string.Empty;
    public NodeType Type { get; init; }
    public int Level { get; init; }
    public bool HasChildren { get; init; }
    public IReadOnlyList<NodeTreeItem> Children { get; init; } = Array.Empty<NodeTreeItem>();
}
```

## 2. 异步LOD生成服务

### 2.1 ILodGenerationService 接口

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

### 2.2 LOD生成相关数据结构

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

## 3. 外部数据源集成服务

### 3.1 Roslyn代码分析服务

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

### 3.2 代码分析数据结构

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

## 4. 符号分析和重构支持

### 4.1 符号信息结构

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

### 4.2 重构操作支持

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

## 5. Agent环境信息服务

### 5.1 IAgentEnvironmentService 接口

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

### 5.2 环境信息数据结构

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

### 高优先级 (Phase 3.1)
1. **ICognitiveCanvasService** - 核心画布功能，系统的核心交互接口
2. **ILodGenerationService** - LOD内容生成，支持渐进式内容展示

### 中优先级 (Phase 3.2)
3. **IAgentEnvironmentService** - 环境信息管理，支持上下文感知
4. **IRoslynIntegrationService** - 代码分析集成，支持开发工作流

### 最佳实践指南

#### 服务设计原则
- **单一职责**: 每个服务接口专注于特定的业务领域
- **异步优先**: 所有I/O操作使用异步模式，支持取消令牌
- **错误处理**: 通过异常和结果对象提供清晰的错误信息
- **可测试性**: 接口设计便于单元测试和模拟

#### 依赖管理
- 服务间通过接口依赖，避免直接实现依赖
- 使用依赖注入容器管理服务生命周期
- 遵循依赖倒置原则，高层模块不依赖低层模块

#### 性能考虑
- LOD生成服务支持批量操作和任务状态跟踪
- 认知画布服务集成缓存策略，优化渲染性能
- 环境信息服务提供轻量级的状态查询接口

---

**下一阶段**: [Phase3_RelationServices.md](Phase3_RelationServices.md) - 关系管理服务接口
