# MemoTree 异常类型定义 (Phase 1)

> **版本**: v1.2  
> **创建日期**: 2025-07-24  
> **基于**: Core_Types_Design.md 第7节  
> **依赖**: [Phase1_CoreTypes.md](Phase1_CoreTypes.md)  
> **状态**: 🚧 开发中  

## 概述

本文档定义了MemoTree系统中的异常类型体系和错误处理机制。这些异常类型为系统提供了完整的错误处理和恢复机制，确保系统的稳定性和可维护性。作为Phase 1的基础设施组件，这些异常类型将被所有其他组件使用。

## 实施优先级

1. **立即实现**: MemoTreeException基类、NodeNotFoundException、StorageException
2. **第一周**: NodeContentNotFoundException、RetrievalException  
3. **第二周**: VersionControlException、扩展异常类型

## 1. 基础异常类型

### 1.1 MemoTree基础异常

```csharp
/// <summary>
/// MemoTree基础异常
/// 所有MemoTree特定异常的基类
/// </summary>
public abstract class MemoTreeException : Exception
{
    /// <summary>
    /// 异常代码，用于程序化处理
    /// </summary>
    public virtual string ErrorCode => GetType().Name;

    /// <summary>
    /// 异常上下文信息
    /// </summary>
    public Dictionary<string, object?> Context { get; } = new();

    protected MemoTreeException(string message) : base(message) { }
    
    protected MemoTreeException(string message, Exception innerException) 
        : base(message, innerException) { }

    /// <summary>
    /// 添加上下文信息
    /// </summary>
    public MemoTreeException WithContext(string key, object? value)
    {
        Context[key] = value;
        return this;
    }
}
```

## 2. 节点相关异常

### 2.1 节点未找到异常

```csharp
/// <summary>
/// 节点未找到异常
/// 当请求的节点不存在时抛出
/// </summary>
public class NodeNotFoundException : MemoTreeException
{
    public NodeId NodeId { get; }
    public override string ErrorCode => "NODE_NOT_FOUND";

    public NodeNotFoundException(NodeId nodeId)
        : base($"Node with ID '{nodeId}' was not found.")
    {
        NodeId = nodeId;
        WithContext("NodeId", nodeId.Value);
    }

    public NodeNotFoundException(NodeId nodeId, string additionalInfo)
        : base($"Node with ID '{nodeId}' was not found. {additionalInfo}")
    {
        NodeId = nodeId;
        WithContext("NodeId", nodeId.Value)
            .WithContext("AdditionalInfo", additionalInfo);
    }
}
```

### 2.2 节点内容未找到异常

```csharp
/// <summary>
/// 节点内容未找到异常
/// 当请求的节点内容在指定LOD级别不存在时抛出
/// </summary>
public class NodeContentNotFoundException : MemoTreeException
{
    public NodeId NodeId { get; }
    public LodLevel Level { get; }
    public override string ErrorCode => "NODE_CONTENT_NOT_FOUND";

    public NodeContentNotFoundException(NodeId nodeId, LodLevel level)
        : base($"Content for node '{nodeId}' at level '{level}' was not found.")
    {
        NodeId = nodeId;
        Level = level;
        WithContext("NodeId", nodeId.Value)
            .WithContext("LodLevel", level.ToString());
    }
}
```

## 3. 存储相关异常

### 3.1 存储异常

```csharp
/// <summary>
/// 存储异常
/// 存储操作失败时抛出的异常
/// </summary>
public class StorageException : MemoTreeException
{
    public override string ErrorCode => "STORAGE_ERROR";

    public StorageException(string message) : base(message) { }
    
    public StorageException(string message, Exception innerException) 
        : base(message, innerException) { }

    /// <summary>
    /// 存储连接异常
    /// </summary>
    public static StorageException ConnectionFailed(string connectionString, Exception innerException)
        => new StorageException("Failed to connect to storage", innerException)
            .WithContext("ConnectionString", connectionString) as StorageException;

    /// <summary>
    /// 存储操作超时异常
    /// </summary>
    public static StorageException OperationTimeout(string operation, TimeSpan timeout)
        => new StorageException($"Storage operation '{operation}' timed out after {timeout}")
            .WithContext("Operation", operation)
            .WithContext("Timeout", timeout) as StorageException;
}
```

## 4. 检索相关异常

### 4.1 检索异常

```csharp
/// <summary>
/// 检索异常
/// 检索操作失败时抛出的异常
/// </summary>
public class RetrievalException : MemoTreeException
{
    public override string ErrorCode => "RETRIEVAL_ERROR";

    public RetrievalException(string message) : base(message) { }
    
    public RetrievalException(string message, Exception innerException) 
        : base(message, innerException) { }

    /// <summary>
    /// 搜索查询无效异常
    /// </summary>
    public static RetrievalException InvalidQuery(string query, string reason)
        => new RetrievalException($"Invalid search query: {reason}")
            .WithContext("Query", query)
            .WithContext("Reason", reason) as RetrievalException;

    /// <summary>
    /// 搜索结果过多异常
    /// </summary>
    public static RetrievalException TooManyResults(int resultCount, int maxAllowed)
        => new RetrievalException($"Search returned {resultCount} results, maximum allowed is {maxAllowed}")
            .WithContext("ResultCount", resultCount)
            .WithContext("MaxAllowed", maxAllowed) as RetrievalException;
}
```

## 5. 版本控制异常

### 5.1 版本控制异常

```csharp
/// <summary>
/// 版本控制异常
/// 版本控制操作失败时抛出的异常
/// </summary>
public class VersionControlException : MemoTreeException
{
    public override string ErrorCode => "VERSION_CONTROL_ERROR";

    public VersionControlException(string message) : base(message) { }
    
    public VersionControlException(string message, Exception innerException) 
        : base(message, innerException) { }

    /// <summary>
    /// 提交冲突异常
    /// </summary>
    public static VersionControlException CommitConflict(string conflictDetails)
        => new VersionControlException($"Commit conflict detected: {conflictDetails}")
            .WithContext("ConflictDetails", conflictDetails) as VersionControlException;

    /// <summary>
    /// 分支不存在异常
    /// </summary>
    public static VersionControlException BranchNotFound(string branchName)
        => new VersionControlException($"Branch '{branchName}' not found")
            .WithContext("BranchName", branchName) as VersionControlException;
}
```

## 6. 异常处理指南

### 6.1 异常处理策略

```csharp
/// <summary>
/// 异常处理策略枚举
/// </summary>
public enum ExceptionHandlingStrategy
{
    /// <summary>
    /// 重新抛出异常
    /// </summary>
    Rethrow,
    
    /// <summary>
    /// 记录日志并继续
    /// </summary>
    LogAndContinue,
    
    /// <summary>
    /// 记录日志并返回默认值
    /// </summary>
    LogAndReturnDefault,
    
    /// <summary>
    /// 重试操作
    /// </summary>
    Retry
}
```

### 6.2 异常处理最佳实践

1. **异常分类**：
   - **可恢复异常**: 网络超时、临时存储问题
   - **不可恢复异常**: 数据损坏、配置错误
   - **业务异常**: 节点不存在、权限不足

2. **异常信息**：
   - 提供清晰的错误消息
   - 包含足够的上下文信息
   - 避免暴露敏感信息

3. **异常传播**：
   - 在适当的层级捕获和处理异常
   - 保持异常堆栈信息的完整性
   - 使用WithContext方法添加调试信息

### 6.3 使用示例

```csharp
// 捕获和处理特定异常
try
{
    var node = await nodeStorage.GetNodeAsync(nodeId);
    return node;
}
catch (NodeNotFoundException ex)
{
    // 记录异常并返回null或默认值
    logger.LogWarning(ex, "Node {NodeId} not found", nodeId);
    return null;
}
catch (StorageException ex)
{
    // 存储异常可能需要重试
    logger.LogError(ex, "Storage error occurred");
    throw; // 重新抛出让上层处理
}
```

---
**下一阶段**: [Phase1_Configuration.md](Phase1_Configuration.md)  
**相关文档**: [Phase1_CoreTypes.md](Phase1_CoreTypes.md), [Phase1_Constraints.md](Phase1_Constraints.md)
