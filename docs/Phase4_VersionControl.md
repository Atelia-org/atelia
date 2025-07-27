# MemoTree 版本控制集成 (Phase 4)

> **文档版本**: v1.0  
> **创建日期**: 2025-07-25  
> **所属阶段**: Phase 4 - 集成层  
> **依赖文档**: [Phase1_CoreTypes.md](Phase1_CoreTypes.md), [Phase1_Exceptions.md](Phase1_Exceptions.md), [Phase1_Configuration.md](Phase1_Configuration.md)  
> **相关文档**: [Phase4_ToolCallAPI.md](Phase4_ToolCallAPI.md)

## 概述

本文档定义了MemoTree系统的版本控制集成接口，提供Git版本控制操作的标准化抽象。版本控制服务支持提交管理、分支操作、历史查询等核心Git功能，为知识图谱的版本化管理提供基础设施。

## 版本控制服务接口

### 核心服务接口

```csharp
/// <summary>
/// Git版本控制操作接口
/// 提供对Git仓库的标准化操作抽象
/// </summary>
public interface IVersionControlService
{
    /// <summary>
    /// 提交更改到Git仓库
    /// </summary>
    /// <param name="message">提交消息，最大长度500字符</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>提交哈希值</returns>
    /// <exception cref="VersionControlException">版本控制操作失败时抛出</exception>
    Task<string> CommitAsync(string message, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取提交历史记录
    /// </summary>
    /// <param name="maxCount">最大返回数量，默认100</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>提交信息列表，按时间倒序排列</returns>
    /// <exception cref="VersionControlException">获取历史失败时抛出</exception>
    Task<IReadOnlyList<CommitInfo>> GetHistoryAsync(int maxCount = 100, CancellationToken cancellationToken = default);

    /// <summary>
    /// 回滚到指定提交
    /// </summary>
    /// <param name="commitHash">目标提交的哈希值</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <exception cref="VersionControlException">回滚操作失败时抛出</exception>
    /// <exception cref="ArgumentException">提交哈希无效时抛出</exception>
    Task ResetToCommitAsync(string commitHash, CancellationToken cancellationToken = default);

    /// <summary>
    /// 创建新分支
    /// </summary>
    /// <param name="branchName">分支名称</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <exception cref="VersionControlException">分支创建失败时抛出</exception>
    /// <exception cref="ArgumentException">分支名称无效时抛出</exception>
    Task CreateBranchAsync(string branchName, CancellationToken cancellationToken = default);

    /// <summary>
    /// 切换到指定分支
    /// </summary>
    /// <param name="branchName">目标分支名称</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <exception cref="VersionControlException">分支切换失败时抛出</exception>
    /// <exception cref="ArgumentException">分支不存在时抛出</exception>
    Task CheckoutBranchAsync(string branchName, CancellationToken cancellationToken = default);

    /// <summary>
    /// 合并分支
    /// </summary>
    /// <param name="sourceBranch">源分支名称</param>
    /// <param name="targetBranch">目标分支名称</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <exception cref="VersionControlException">分支合并失败时抛出</exception>
    /// <exception cref="ArgumentException">分支名称无效时抛出</exception>
    Task MergeBranchAsync(string sourceBranch, string targetBranch, CancellationToken cancellationToken = default);
}
```

## 数据类型定义

### 提交信息

```csharp
/// <summary>
/// Git提交信息记录
/// 包含提交的基本元数据和变更文件列表
/// </summary>
public record CommitInfo
{
    /// <summary>
    /// 提交哈希值（SHA-1）
    /// </summary>
    public string Hash { get; init; } = string.Empty;

    /// <summary>
    /// 提交消息
    /// </summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>
    /// 提交作者
    /// </summary>
    public string Author { get; init; } = string.Empty;

    /// <summary>
    /// 提交时间戳（UTC）
    /// </summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// 变更文件列表
    /// </summary>
    public IReadOnlyList<string> ChangedFiles { get; init; } = Array.Empty<string>();
}
```

## 异常类型

### 版本控制异常

```csharp
/// <summary>
/// 版本控制操作异常
/// 用于包装Git操作过程中发生的各种错误
/// </summary>
public class VersionControlException : MemoTreeException
{
    /// <summary>
    /// 初始化版本控制异常
    /// </summary>
    /// <param name="message">异常消息</param>
    public VersionControlException(string message) : base(message) { }

    /// <summary>
    /// 初始化版本控制异常（包含内部异常）
    /// </summary>
    /// <param name="message">异常消息</param>
    /// <param name="innerException">内部异常</param>
    public VersionControlException(string message, Exception innerException) : base(message, innerException) { }
}
```

## 工具调用API集成

### 提交更改请求

```csharp
/// <summary>
/// Git提交请求（引用自Phase4_ToolCallAPI.md）
/// 用于版本控制服务中的Git提交操作，等同于 git commit
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

### 节点操作中的版本控制

```csharp
/// <summary>
/// 创建节点请求（版本控制上下文）
/// 编辑操作只落盘，不直接commit，符合Git工作流
/// </summary>
public record CreateNodeRequest
{
    public string Title { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    // 注意：不包含CommitMessage，编辑操作不直接commit
    // 使用GitCommitAsync进行独立的提交操作
}

/// <summary>
/// 更新节点请求（版本控制上下文）
/// 编辑操作只落盘，不直接commit，符合Git工作流
/// </summary>
public record UpdateNodeRequest
{
    public NodeId NodeId { get; init; }
    public string? Content { get; init; }
    public string? Title { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }

    // 注意：不包含CommitMessage，编辑操作不直接commit
    // 使用GitCommitAsync进行独立的提交操作
}
```

## 配置选项

### 版本控制配置

```csharp
/// <summary>
/// 系统配置中的版本控制选项
/// </summary>
public partial class SystemConfiguration
{
    /// <summary>
    /// 是否启用Git版本控制
    /// 默认启用，可通过配置禁用
    /// </summary>
    public bool EnableVersionControl { get; set; } = true;
}
```

## 系统常量

### 版本控制相关常量

```csharp
/// <summary>
/// 系统限制常量（版本控制部分）
/// </summary>
public static partial class SystemLimits
{
    /// <summary>
    /// Git提交消息最大长度
    /// 符合Git最佳实践的消息长度限制
    /// </summary>
    public const int MaxCommitMessageLength = 500;
}
```

## 实施优先级

### 高优先级
1. **IVersionControlService接口实现** - 核心Git操作功能
2. **CommitInfo数据类型** - 提交信息的标准化表示
3. **VersionControlException异常处理** - 错误处理机制

### 中优先级
1. **工具调用API集成** - LLM接口中的版本控制支持
2. **配置选项集成** - 版本控制功能的开关控制

### 低优先级
1. **高级Git操作** - 分支管理、合并策略等复杂功能
2. **版本控制统计** - 提交频率、变更统计等分析功能

## 最佳实践

### 提交消息规范
- 使用清晰、描述性的提交消息
- 遵循约定式提交（Conventional Commits）格式
- 限制消息长度在500字符以内

### 分支管理策略
- 使用功能分支进行开发
- 定期合并到主分支
- 保持分支命名的一致性

### 错误处理
- 捕获并包装LibGit2Sharp异常
- 提供有意义的错误消息
- 记录详细的操作日志

---

**下一阶段**: [Phase5_Security.md](Phase5_Security.md) - 安全与权限管理
