# MemoTree 工具调用API设计 (Phase 4)

> **文档版本**: v2.1 (Git命令直接映射)
> **创建日期**: 2025-07-25
> **最后更新**: 2025-07-27
> **依赖文档**: [Phase1_CoreTypes.md](./Phase1_CoreTypes.md), [Phase3_CoreServices.md](./Phase3_CoreServices.md)
> **阶段**: Phase 4 - Integration (集成层阶段)
> **实际行数**: 908行

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
    /// Git Status - 查询当前工作区状态
    /// 显示所有已修改但未提交的节点，等同于 git status
    /// </summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>包含工作区状态的工具调用结果</returns>
    Task<ToolCallResult> GitStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Git Diff - 查看节点变更差异
    /// 显示指定节点的具体变更内容，等同于 git diff
    /// </summary>
    /// <param name="request">差异查看请求</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>包含变更差异的工具调用结果</returns>
    Task<ToolCallResult> GitDiffAsync(GitDiffRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Git Commit - 提交变更到仓库
    /// 将工作区的变更提交到Git仓库，等同于 git commit
    /// </summary>
    /// <param name="request">提交请求</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>工具调用结果</returns>
    Task<ToolCallResult> GitCommitAsync(GitCommitRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Git Checkout - 丢弃工作区变更
    /// 恢复指定节点到最后一次提交的状态，等同于 git checkout -- <file>
    /// </summary>
    /// <param name="request">检出请求</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>工具调用结果</returns>
    Task<ToolCallResult> GitCheckoutAsync(GitCheckoutRequest request, CancellationToken cancellationToken = default);
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
/// 未提交变更信息
/// 表示工作区中已修改但未提交的节点变更
/// </summary>
public record PendingChange
{
    /// <summary>
    /// 变更的节点ID
    /// </summary>
    public NodeId NodeId { get; init; }

    /// <summary>
    /// 节点标题
    /// </summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>
    /// 变更类型
    /// </summary>
    public ChangeType ChangeType { get; init; }

    /// <summary>
    /// 变更时间戳
    /// </summary>
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// 变更摘要描述
    /// </summary>
    public string Summary { get; init; } = string.Empty;
}
```

### 变更类型枚举

```csharp
/// <summary>
/// 变更类型
/// 定义节点的变更操作类型
/// </summary>
public enum ChangeType
{
    /// <summary>
    /// 新增节点
    /// </summary>
    Added,

    /// <summary>
    /// 修改节点
    /// </summary>
    Modified,

    /// <summary>
    /// 删除节点
    /// </summary>
    Deleted,

    /// <summary>
    /// 移动节点（层次结构变更）
    /// </summary>
    Moved
}
```

### Git Diff 请求

```csharp
/// <summary>
/// Git Diff 请求
/// 用于获取指定节点的变更差异，等同于 git diff
/// </summary>
public record GitDiffRequest
{
    /// <summary>
    /// 要查看差异的节点ID（可选）
    /// 如果为空，显示所有变更节点的差异
    /// </summary>
    public NodeId? NodeId { get; init; }

    /// <summary>
    /// 是否包含内容差异
    /// </summary>
    public bool IncludeContentDiff { get; init; } = true;

    /// <summary>
    /// 是否包含关系变更
    /// </summary>
    public bool IncludeRelationChanges { get; init; } = true;

    /// <summary>
    /// 是否显示简化格式
    /// </summary>
    public bool Brief { get; init; } = false;
}
```

### 节点变更详情

```csharp
/// <summary>
/// 节点变更详情
/// 包含节点的具体变更内容和差异信息
/// </summary>
public record NodeChangeDetails
{
    /// <summary>
    /// 节点ID
    /// </summary>
    public NodeId NodeId { get; init; }

    /// <summary>
    /// 变更类型
    /// </summary>
    public ChangeType ChangeType { get; init; }

    /// <summary>
    /// 标题变更（如果有）
    /// </summary>
    public PropertyChange<string>? TitleChange { get; init; }

    /// <summary>
    /// 内容变更（如果有）
    /// </summary>
    public PropertyChange<string>? ContentChange { get; init; }

    /// <summary>
    /// 标签变更（如果有）
    /// </summary>
    public PropertyChange<IReadOnlyList<string>>? TagsChange { get; init; }

    /// <summary>
    /// 关系变更列表
    /// </summary>
    public IReadOnlyList<RelationChange> RelationChanges { get; init; } = Array.Empty<RelationChange>();
}
```

### 属性变更记录

```csharp
/// <summary>
/// 属性变更记录
/// 记录属性的前后值变化
/// </summary>
/// <typeparam name="T">属性类型</typeparam>
public record PropertyChange<T>
{
    /// <summary>
    /// 变更前的值
    /// </summary>
    public T? OldValue { get; init; }

    /// <summary>
    /// 变更后的值
    /// </summary>
    public T? NewValue { get; init; }
}
```

### 关系变更记录

```csharp
/// <summary>
/// 关系变更记录
/// 记录节点关系的变更信息
/// </summary>
public record RelationChange
{
    /// <summary>
    /// 关系ID
    /// </summary>
    public RelationId RelationId { get; init; }

    /// <summary>
    /// 变更类型
    /// </summary>
    public ChangeType ChangeType { get; init; }

    /// <summary>
    /// 目标节点ID
    /// </summary>
    public NodeId TargetNodeId { get; init; }

    /// <summary>
    /// 关系类型
    /// </summary>
    public RelationType RelationType { get; init; }
}
```

### Git Commit 请求

```csharp
/// <summary>
/// Git Commit 请求
/// 用于将工作区变更提交到Git仓库，等同于 git commit
/// </summary>
public record GitCommitRequest
{
    /// <summary>
    /// 提交消息
    /// </summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>
    /// 要提交的节点ID列表（可选）
    /// 如果为空，则提交所有未提交的变更
    /// </summary>
    public IReadOnlyList<NodeId>? NodeIds { get; init; }

    /// <summary>
    /// 提交作者信息（可选）
    /// </summary>
    public CommitAuthor? Author { get; init; }

    /// <summary>
    /// 是否创建版本标签
    /// </summary>
    public bool CreateTag { get; init; } = false;

    /// <summary>
    /// 版本标签名称
    /// </summary>
    public string? TagName { get; init; }

    /// <summary>
    /// 标签描述
    /// </summary>
    public string? TagDescription { get; init; }
}
```

### Git Checkout 请求

```csharp
/// <summary>
/// Git Checkout 请求
/// 用于丢弃指定节点的未提交变更，等同于 git checkout -- <file>
/// </summary>
public record GitCheckoutRequest
{
    /// <summary>
    /// 要丢弃变更的节点ID列表
    /// </summary>
    public IReadOnlyList<NodeId> NodeIds { get; init; } = Array.Empty<NodeId>();

    /// <summary>
    /// 是否确认丢弃（安全检查）
    /// </summary>
    public bool Confirmed { get; init; } = false;
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

## 使用模式

### MemoTree = Git Workspace 映射层

MemoTree为LLM提供了Git工作区的直观映射：
- **文件路径** → **NodeId (GUID)**
- **文件内容** → **Markdown节点**
- **Git命令** → **对应的API调用**

### 标准Git工作流

```csharp
// 1. 编辑操作（等同于修改文件，自动落盘但不commit）
await service.CreateNodeAsync(new CreateNodeRequest
{
    Title = "依赖注入模块重构",
    Content = "重构整个依赖注入系统..."
});

await service.CreateNodeAsync(new CreateNodeRequest
{
    ParentId = parentNodeId,
    Title = "接口定义",
    Content = "定义新的服务接口..."
});

await service.UpdateNodeAsync(new UpdateNodeRequest
{
    NodeId = existingNodeId,
    Content = "更新相关文档..."
});

// 2. git status - 查看工作区状态
var statusResult = await service.GitStatusAsync();
var changes = statusResult.Data as IReadOnlyList<PendingChange>;

Console.WriteLine($"Changes not staged for commit:");
foreach (var change in changes)
{
    Console.WriteLine($"  {change.ChangeType}: {change.Title} ({change.NodeId})");
}

// 3. git diff - 查看具体变更
var diffResult = await service.GitDiffAsync(new GitDiffRequest
{
    NodeId = someNodeId,
    IncludeContentDiff = true
});

// 4. git commit - 提交变更
await service.GitCommitAsync(new GitCommitRequest
{
    Message = "Refactored the dependency injection module",
    CreateTag = true,
    TagName = "v1.2.0"
});
```

### 选择性提交（git add + git commit）

```csharp
// 编辑多个节点（等同于修改多个文件）
await service.CreateNodeAsync(new CreateNodeRequest { Title = "Feature A" });
await service.CreateNodeAsync(new CreateNodeRequest { Title = "Feature B" });
await service.UpdateNodeAsync(new UpdateNodeRequest { NodeId = nodeC, Content = "Updated C" });

// git status - 查看所有变更
var status = await service.GitStatusAsync();

// 选择性提交Feature A（等同于 git add <specific-files> && git commit）
await service.GitCommitAsync(new GitCommitRequest
{
    Message = "Add Feature A implementation",
    NodeIds = new[] { featureANodeId }  // 指定要提交的节点
});

// 稍后提交其他变更（等同于 git add . && git commit）
await service.GitCommitAsync(new GitCommitRequest
{
    Message = "Add Feature B and update documentation"
    // NodeIds为空时提交所有剩余变更
});
```

### Git命令映射表

| Git命令 | MemoTree API | 说明 |
|---------|-------------|------|
| `git status` | `GitStatusAsync()` | 查看工作区状态 |
| `git diff` | `GitDiffAsync()` | 查看变更差异 |
| `git diff <file>` | `GitDiffAsync(new GitDiffRequest { NodeId = nodeId })` | 查看特定节点差异 |
| `git commit -m "msg"` | `GitCommitAsync(new GitCommitRequest { Message = "msg" })` | 提交所有变更 |
| `git commit <files> -m "msg"` | `GitCommitAsync(new GitCommitRequest { NodeIds = [...], Message = "msg" })` | 选择性提交 |
| `git checkout -- <file>` | `GitCheckoutAsync(new GitCheckoutRequest { NodeIds = [...] })` | 丢弃变更 |

### 实际使用示例

```csharp
// git status
var status = await service.GitStatusAsync();

// git diff
var allDiff = await service.GitDiffAsync(new GitDiffRequest());

// git diff <specific-node>
var nodeDiff = await service.GitDiffAsync(new GitDiffRequest
{
    NodeId = targetNodeId,
    IncludeContentDiff = true,
    IncludeRelationChanges = true
});

// git checkout -- <node>
await service.GitCheckoutAsync(new GitCheckoutRequest
{
    NodeIds = new[] { unwantedNodeId },
    Confirmed = true
});
```

## 实施优先级

### 高优先级 (P0)
1. **ILlmToolCallService 接口**：核心工具调用服务接口
2. **ToolCallResult 类型**：统一的结果类型
3. **基础请求类型**：ExpandNodeRequest, CollapseNodeRequest, CreateNodeRequest, UpdateNodeRequest

### 中优先级 (P1)
1. **Git命令API**：GitStatusAsync, GitDiffAsync, GitCommitAsync, GitCheckoutAsync
2. **Git请求类型**：GitDiffRequest, GitCommitRequest, GitCheckoutRequest
3. **变更管理类型**：PendingChange, NodeChangeDetails, PropertyChange, RelationChange, ChangeType
4. **搜索相关类型**：SearchNodesRequest, SearchType, SearchScope
5. **扩展属性**：IncludeChildren, IncludeRelations等增强功能

### 低优先级 (P2)
1. **高级搜索功能**：混合搜索、语义搜索优化
2. **版本控制集成**：标签创建、分支管理
3. **批量操作支持**：批量创建、更新、删除

---

> 版本控制冲突说明：MVP 阶段默认假设单用户/单写入者，不处理复杂合并冲突；详见 [Phase4_VersionControl.md](./Phase4_VersionControl.md) 的“MVP 冲突处理假设”。


**下一阶段**: [Phase4_ExternalIntegration.md](./Phase4_ExternalIntegration.md) - 外部数据源集成设计
