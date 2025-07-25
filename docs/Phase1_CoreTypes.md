# MemoTree 核心数据类型 (Phase 1)

> 版本: v1.2
> 创建日期: 2025-07-24
> 基于: Core_Types_Design.md 第1-2节

## 概述

本文档定义了MemoTree系统的核心数据类型，这些类型构成了整个系统的基础。作为第一阶段的实现重点，这些类型必须首先完成并稳定。

## 1. 基础标识符类型

### 1.1 节点标识符

```csharp
/// <summary>
/// 认知节点的唯一标识符
/// </summary>
public readonly struct NodeId : IEquatable<NodeId>
{
    public string Value { get; }
    
    public NodeId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("NodeId cannot be null or empty", nameof(value));
        Value = value;
    }
    
    public static NodeId Generate() => new(Guid.NewGuid().ToString("N")[..12]);
    public static NodeId Root => new("root");
    
    public bool Equals(NodeId other) => Value == other.Value;
    public override bool Equals(object? obj) => obj is NodeId other && Equals(other);
    public override int GetHashCode() => Value.GetHashCode();
    public override string ToString() => Value;
    
    public static implicit operator string(NodeId nodeId) => nodeId.Value;
    public static explicit operator NodeId(string value) => new(value);
}
```

### 1.2 关系标识符

```csharp
/// <summary>
/// 关系标识符
/// </summary>
public readonly struct RelationId : IEquatable<RelationId>
{
    public string Value { get; }

    public RelationId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("RelationId cannot be null or empty", nameof(value));
        Value = value;
    }

    public static RelationId Generate() => new(Guid.NewGuid().ToString("N")[..12]);

    public bool Equals(RelationId other) => Value == other.Value;
    public override bool Equals(object? obj) => obj is RelationId other && Equals(other);
    public override int GetHashCode() => Value.GetHashCode();
    public override string ToString() => Value;

    public static implicit operator string(RelationId relationId) => relationId.Value;
    public static explicit operator RelationId(string value) => new(value);
}
```

## 2. 枚举类型定义

### 2.1 LOD级别定义

```csharp
/// <summary>
/// 详细程度级别 (Level of Detail)
/// 对应MVP设计中的文件存储结构
/// </summary>
public enum LodLevel
{
    /// <summary>
    /// 标题级 - 仅显示节点标题 (对应meta.yaml文件中的title字段)
    /// </summary>
    Title = 0,

    /// <summary>
    /// 简介级 - 显示标题和一句话简介 (对应brief.md文件)
    /// </summary>
    Brief = 1,

    /// <summary>
    /// 摘要级 - 显示标题和简要摘要 (对应summary.md文件)
    /// </summary>
    Summary = 2,

    /// <summary>
    /// 详细级 - 显示完整内容 (对应detail.md文件)
    /// </summary>
    Detail = 3
}
```

### 2.2 节点类型

```csharp
/// <summary>
/// 认知节点类型
/// </summary>
public enum NodeType
{
    /// <summary>
    /// 概念节点 - 核心概念和理论
    /// </summary>
    Concept,
    
    /// <summary>
    /// 记忆节点 - 经验记忆和事实信息
    /// </summary>
    Memory,
    
    /// <summary>
    /// 计划节点 - 任务规划和待办事项
    /// </summary>
    Plan,
    
    /// <summary>
    /// 引用节点 - 外部引用和链接
    /// </summary>
    Reference,
    
    /// <summary>
    /// 代码节点 - 代码相关信息
    /// </summary>
    Code
}
```

### 2.3 关系类型

```csharp
/// <summary>
/// 节点间关系类型
/// </summary>
public enum RelationType
{
    /// <summary>
    /// 引用关系
    /// </summary>
    References,

    /// <summary>
    /// 启发关系
    /// </summary>
    InspiredBy,

    /// <summary>
    /// 矛盾关系
    /// </summary>
    Contradicts,

    /// <summary>
    /// 扩展关系
    /// </summary>
    Extends,

    /// <summary>
    /// 依赖关系
    /// </summary>
    DependsOn
}
```

## 3. 核心数据记录类型

### 3.1 节点元数据

```csharp
/// <summary>
/// 认知节点元数据（父子关系和语义关系数据已分离）
/// </summary>
public record NodeMetadata
{
    public NodeId Id { get; init; }
    public NodeType Type { get; init; }
    public string Title { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime LastModified { get; init; } = DateTime.UtcNow;
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<LodLevel, string> ContentHashes { get; init; } =
        new Dictionary<LodLevel, string>();
    public bool IsDirty { get; init; } = false;
    public IReadOnlyDictionary<string, object> CustomProperties { get; init; } =
        new Dictionary<string, object>();
}
```

### 3.2 节点内容

```csharp
/// <summary>
/// 节点内容数据
/// </summary>
public record NodeContent
{
    public NodeId Id { get; init; }
    public LodLevel Level { get; init; }
    public string Content { get; init; } = string.Empty;
    public string ContentHash { get; init; } = string.Empty;
    public DateTime LastModified { get; init; } = DateTime.UtcNow;
}
```

### 3.3 节点关系

```csharp
/// <summary>
/// 语义关系定义（集中存储版本，不包括父子关系）
/// </summary>
public record NodeRelation
{
    public RelationId Id { get; init; }
    public NodeId SourceId { get; init; }
    public NodeId TargetId { get; init; }
    public RelationType Type { get; init; }
    public string Description { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public IReadOnlyDictionary<string, object> Properties { get; init; } =
        new Dictionary<string, object>();
}
```

### 3.4 父子关系类型

```csharp
/// <summary>
/// 父子关系信息（独立存储）
/// </summary>
public record ParentChildrenInfo
{
    public NodeId ParentId { get; init; }
    public IReadOnlyList<ChildNodeInfo> Children { get; init; } = Array.Empty<ChildNodeInfo>();
    public DateTime LastModified { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// 子节点信息
/// </summary>
public record ChildNodeInfo
{
    public NodeId NodeId { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public int Order { get; init; } = 0;
}
```

## 4. 复合类型

### 4.1 完整认知节点

```csharp
/// <summary>
/// 完整的认知节点，包含元数据和所有LOD级别的内容
/// </summary>
public record CognitiveNode
{
    public NodeMetadata Metadata { get; init; } = new();
    public IReadOnlyDictionary<LodLevel, NodeContent> Contents { get; init; } = 
        new Dictionary<LodLevel, NodeContent>();
    
    /// <summary>
    /// 获取指定LOD级别的内容
    /// </summary>
    public NodeContent? GetContent(LodLevel level) => 
        Contents.TryGetValue(level, out var content) ? content : null;
    
    /// <summary>
    /// 检查是否有指定LOD级别的内容
    /// </summary>
    public bool HasContent(LodLevel level) => Contents.ContainsKey(level);
    
    /// <summary>
    /// 获取所有可用的LOD级别
    /// </summary>
    public IEnumerable<LodLevel> AvailableLevels => Contents.Keys;
}
```

## 5. 约束和限制

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
}
```

## 实施优先级

1. **立即实现**：NodeId, RelationId, 基础枚举类型
2. **第一周**：NodeMetadata, NodeContent, NodeRelation
3. **第二周**：CognitiveNode 复合类型和相关方法
4. **第三周**：约束验证和单元测试

---

**下一阶段**: [Phase2_StorageLayer.md](Phase2_StorageLayer.md) - 存储抽象层设计
