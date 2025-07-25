# MemoTree 工具调用API设计 (Phase 4)

> **文档版本**: v1.0  
> **创建日期**: 2025-07-25  
> **依赖文档**: [Phase1_CoreTypes.md](./Phase1_CoreTypes.md), [Phase3_CoreServices.md](./Phase3_CoreServices.md)  
> **阶段**: Phase 4 - Integration (集成层阶段)  
> **预计行数**: ~400行

## 概述

本文档定义了MemoTree系统的工具调用API设计，为LLM提供标准化的节点操作接口。这些API支持节点的展开、折叠、创建、更新、搜索和提交等核心操作，是LLM与认知画布交互的主要入口。

工具调用API设计遵循以下原则：
- **统一的请求/响应模型**：所有操作使用一致的结果类型
- **异步操作支持**：支持取消令牌和异步处理
- **类型安全**：强类型的请求和响应对象
- **错误处理**：完整的错误信息和警告机制
- **扩展性**：支持未来新增操作类型

## 5.1 核心工具调用服务

### ILlmToolCallService 接口

```csharp
/// <summary>
/// LLM工具调用服务接口
/// 为LLM提供标准化的认知画布操作接口
/// </summary>
public interface ILlmToolCallService
{
    /// <summary>
    /// 展开节点到指定的LOD级别
    /// </summary>
    /// <param name="request">展开节点请求</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>包含展开内容的工具调用结果</returns>
    Task<ToolCallResult> ExpandNodeAsync(ExpandNodeRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// 折叠节点到最小显示状态
    /// </summary>
    /// <param name="request">折叠节点请求</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>工具调用结果</returns>
    Task<ToolCallResult> CollapseNodeAsync(CollapseNodeRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// 创建新的认知节点
    /// </summary>
    /// <param name="request">创建节点请求</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>包含新节点ID的工具调用结果</returns>
    Task<ToolCallResult> CreateNodeAsync(CreateNodeRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// 更新现有节点的内容或属性
    /// </summary>
    /// <param name="request">更新节点请求</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>工具调用结果</returns>
    Task<ToolCallResult> UpdateNodeAsync(UpdateNodeRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// 搜索符合条件的节点
    /// </summary>
    /// <param name="request">搜索节点请求</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>包含搜索结果的工具调用结果</returns>
    Task<ToolCallResult> SearchNodesAsync(SearchNodesRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// 提交当前会话的所有更改
    /// </summary>
    /// <param name="request">提交更改请求</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>工具调用结果</returns>
    Task<ToolCallResult> CommitChangesAsync(CommitChangesRequest request, CancellationToken cancellationToken = default);
}
```

### 工具调用结果类型

```csharp
/// <summary>
/// 工具调用结果
/// 统一的API响应格式，包含操作状态、数据和错误信息
/// </summary>
public record ToolCallResult
{
    /// <summary>
    /// 操作是否成功
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// 操作结果消息
    /// </summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>
    /// 操作返回的数据（可选）
    /// 具体类型取决于操作类型
    /// </summary>
    public object? Data { get; init; }

    /// <summary>
    /// 警告信息列表
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    /// <summary>
    /// 错误代码（操作失败时）
    /// </summary>
    public string? ErrorCode { get; init; }

    /// <summary>
    /// 创建成功结果
    /// </summary>
    /// <param name="message">成功消息</param>
    /// <param name="data">返回数据</param>
    /// <param name="warnings">警告信息</param>
    /// <returns>成功的工具调用结果</returns>
    public static ToolCallResult Success(string message, object? data = null, IReadOnlyList<string>? warnings = null)
        => new()
        {
            Success = true,
            Message = message,
            Data = data,
            Warnings = warnings ?? Array.Empty<string>()
        };

    /// <summary>
    /// 创建失败结果
    /// </summary>
    /// <param name="message">错误消息</param>
    /// <param name="errorCode">错误代码</param>
    /// <param name="warnings">警告信息</param>
    /// <returns>失败的工具调用结果</returns>
    public static ToolCallResult Failure(string message, string? errorCode = null, IReadOnlyList<string>? warnings = null)
        => new()
        {
            Success = false,
            Message = message,
            ErrorCode = errorCode,
            Warnings = warnings ?? Array.Empty<string>()
        };
}
```

## 5.2 请求类型定义

### 节点展开请求

```csharp
/// <summary>
/// 展开节点请求
/// 用于将节点内容展开到指定的LOD级别
/// </summary>
public record ExpandNodeRequest
{
    /// <summary>
    /// 视图名称，用于上下文管理
    /// </summary>
    public string ViewName { get; init; } = "default";

    /// <summary>
    /// 要展开的节点ID
    /// </summary>
    public NodeId NodeId { get; init; }

    /// <summary>
    /// 目标LOD级别
    /// </summary>
    public LodLevel Level { get; init; } = LodLevel.Detail;

    /// <summary>
    /// 是否包含子节点
    /// </summary>
    public bool IncludeChildren { get; init; } = false;

    /// <summary>
    /// 是否包含关系信息
    /// </summary>
    public bool IncludeRelations { get; init; } = false;
}
```

### 节点折叠请求

```csharp
/// <summary>
/// 折叠节点请求
/// 用于将节点内容折叠到最小显示状态
/// </summary>
public record CollapseNodeRequest
{
    /// <summary>
    /// 视图名称，用于上下文管理
    /// </summary>
    public string ViewName { get; init; } = "default";

    /// <summary>
    /// 要折叠的节点ID
    /// </summary>
    public NodeId NodeId { get; init; }

    /// <summary>
    /// 是否递归折叠子节点
    /// </summary>
    public bool Recursive { get; init; } = false;
}
```

### 节点创建请求

```csharp
/// <summary>
/// 创建节点请求
/// 用于创建新的认知节点
/// </summary>
public record CreateNodeRequest
{
    /// <summary>
    /// 父节点ID（可选）
    /// </summary>
    public NodeId? ParentId { get; init; }

    /// <summary>
    /// 节点类型
    /// </summary>
    public NodeType Type { get; init; }

    /// <summary>
    /// 节点标题
    /// </summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>
    /// 节点内容
    /// </summary>
    public string Content { get; init; } = string.Empty;

    /// <summary>
    /// 节点标签
    /// </summary>
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    /// <summary>
    /// 提交消息
    /// </summary>
    public string CommitMessage { get; init; } = string.Empty;

    /// <summary>
    /// 初始LOD级别
    /// </summary>
    public LodLevel InitialLevel { get; init; } = LodLevel.Summary;

    /// <summary>
    /// 是否自动生成摘要
    /// </summary>
    public bool AutoGenerateSummary { get; init; } = true;
}
```

### 节点更新请求

```csharp
/// <summary>
/// 更新节点请求
/// 用于更新现有节点的内容或属性
/// </summary>
public record UpdateNodeRequest
{
    /// <summary>
    /// 要更新的节点ID
    /// </summary>
    public NodeId NodeId { get; init; }

    /// <summary>
    /// 新的LOD级别（可选）
    /// </summary>
    public LodLevel? Level { get; init; }

    /// <summary>
    /// 新的内容（可选）
    /// </summary>
    public string? Content { get; init; }

    /// <summary>
    /// 新的标题（可选）
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// 新的标签列表（可选）
    /// </summary>
    public IReadOnlyList<string>? Tags { get; init; }

    /// <summary>
    /// 提交消息
    /// </summary>
    public string CommitMessage { get; init; } = string.Empty;

    /// <summary>
    /// 是否重新生成摘要
    /// </summary>
    public bool RegenerateSummary { get; init; } = false;

    /// <summary>
    /// 更新模式
    /// </summary>
    public UpdateMode Mode { get; init; } = UpdateMode.Merge;
}
```

## 5.3 搜索相关类型

### 搜索节点请求

```csharp
/// <summary>
/// 搜索节点请求
/// 支持多种搜索模式和过滤条件
/// </summary>
public record SearchNodesRequest
{
    /// <summary>
    /// 搜索查询字符串
    /// </summary>
    public string Query { get; init; } = string.Empty;

    /// <summary>
    /// 搜索类型
    /// </summary>
    public SearchType SearchType { get; init; } = SearchType.FullText;

    /// <summary>
    /// 最大结果数量
    /// </summary>
    public int MaxResults { get; init; } = 10;

    /// <summary>
    /// 节点类型过滤（可选）
    /// </summary>
    public IReadOnlyList<NodeType>? NodeTypes { get; init; }

    /// <summary>
    /// 标签过滤（可选）
    /// </summary>
    public IReadOnlyList<string>? Tags { get; init; }

    /// <summary>
    /// 搜索范围
    /// </summary>
    public SearchScope Scope { get; init; } = SearchScope.All;

    /// <summary>
    /// 是否包含内容预览
    /// </summary>
    public bool IncludePreview { get; init; } = true;

    /// <summary>
    /// 预览长度（字符数）
    /// </summary>
    public int PreviewLength { get; init; } = 200;
}
```

### 搜索类型枚举

```csharp
/// <summary>
/// 搜索类型
/// 定义不同的搜索模式
/// </summary>
public enum SearchType
{
    /// <summary>
    /// 全文搜索
    /// </summary>
    FullText,

    /// <summary>
    /// 语义搜索
    /// </summary>
    Semantic,

    /// <summary>
    /// 关系搜索
    /// </summary>
    Relation,

    /// <summary>
    /// 混合搜索
    /// </summary>
    Hybrid
}
```

### 搜索范围枚举

```csharp
/// <summary>
/// 搜索范围
/// 定义搜索的作用域
/// </summary>
public enum SearchScope
{
    /// <summary>
    /// 搜索所有节点
    /// </summary>
    All,

    /// <summary>
    /// 仅搜索当前视图中的节点
    /// </summary>
    CurrentView,

    /// <summary>
    /// 仅搜索指定节点的子树
    /// </summary>
    Subtree,

    /// <summary>
    /// 仅搜索相关节点
    /// </summary>
    Related
}
```

## 5.4 提交和版本控制

### 提交更改请求

```csharp
/// <summary>
/// 提交更改请求
/// 用于将当前会话的更改持久化到存储
/// </summary>
public record CommitChangesRequest
{
    /// <summary>
    /// 提交消息
    /// </summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>
    /// 特定节点ID列表（可选）
    /// 如果指定，则只提交这些节点的更改
    /// </summary>
    public IReadOnlyList<NodeId>? SpecificNodes { get; init; }

    /// <summary>
    /// 是否创建版本标签
    /// </summary>
    public bool CreateTag { get; init; } = false;

    /// <summary>
    /// 版本标签名称（当CreateTag为true时）
    /// </summary>
    public string? TagName { get; init; }

    /// <summary>
    /// 提交作者信息
    /// </summary>
    public CommitAuthor? Author { get; init; }
}
```

### 更新模式枚举

```csharp
/// <summary>
/// 更新模式
/// 定义节点内容的更新方式
/// </summary>
public enum UpdateMode
{
    /// <summary>
    /// 合并更新（默认）
    /// </summary>
    Merge,

    /// <summary>
    /// 完全替换
    /// </summary>
    Replace,

    /// <summary>
    /// 追加内容
    /// </summary>
    Append,

    /// <summary>
    /// 前置内容
    /// </summary>
    Prepend
}
```

### 提交作者信息

```csharp
/// <summary>
/// 提交作者信息
/// </summary>
public record CommitAuthor
{
    /// <summary>
    /// 作者姓名
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// 作者邮箱
    /// </summary>
    public string Email { get; init; } = string.Empty;

    /// <summary>
    /// 提交时间戳
    /// </summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}
```

## 实施优先级

### 高优先级 (P0)
1. **ILlmToolCallService 接口**：核心工具调用服务接口
2. **ToolCallResult 类型**：统一的结果类型
3. **基础请求类型**：ExpandNodeRequest, CollapseNodeRequest, CreateNodeRequest, UpdateNodeRequest

### 中优先级 (P1)
1. **搜索相关类型**：SearchNodesRequest, SearchType, SearchScope
2. **提交相关类型**：CommitChangesRequest, UpdateMode, CommitAuthor
3. **扩展属性**：IncludeChildren, IncludeRelations等增强功能

### 低优先级 (P2)
1. **高级搜索功能**：混合搜索、语义搜索优化
2. **版本控制集成**：标签创建、分支管理
3. **批量操作支持**：批量创建、更新、删除

---

**下一阶段**: [Phase4_ExternalIntegration.md](./Phase4_ExternalIntegration.md) - 外部数据源集成设计
