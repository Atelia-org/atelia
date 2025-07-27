# MemoTree 核心数据类型 (Phase 1)

> 版本: v1.2
> 创建日期: 2025-07-24
> 基于: Core_Types_Design.md 第1-2节

## 概述

本文档定义了MemoTree系统的核心数据类型，这些类型构成了整个系统的基础。作为第一阶段的实现重点，这些类型必须首先完成并稳定。

## 1. 基础标识符类型

### 1.0 GUID编码工具

```csharp
/// <summary>
/// 统一的GUID编码工具，确保项目中所有GUID到字符串的转换使用相同的编码方式
/// 当前使用Base64编码(22字符)，未来可无缝切换到Base4096-CJK编码(11字符)
/// </summary>
public static class GuidEncoder
{
    /// <summary>
    /// 将GUID编码为ID字符串表示
    /// 当前实现：Base64编码，移除末尾填充，生成22个字符
    /// 未来可切换为Base4096-CJK编码，生成11个汉字字符
    /// </summary>
    public static string ToIdString(Guid guid)
    {
        var bytes = guid.ToByteArray();
        var base64 = Convert.ToBase64String(bytes);
        return base64.TrimEnd('='); // 移除固定的==填充
    }

    /// <summary>
    /// 从ID字符串解码回GUID（用于调试和验证）
    /// </summary>
    public static Guid FromIdString(string encoded)
    {
        if (encoded.Length != 22)
            throw new ArgumentException($"Invalid encoded GUID length: {encoded.Length}, expected 22");

        var withPadding = encoded + "=="; // 添加回填充
        var bytes = Convert.FromBase64String(withPadding);
        return new Guid(bytes);
    }

    /// <summary>
    /// 检测编码格式类型（用于未来多格式支持）
    /// </summary>
    public static GuidEncodingType DetectEncodingType(string encoded)
    {
        return encoded.Length switch
        {
            22 => GuidEncodingType.Base64,
            11 when encoded.All(c => c >= '\u4e00' && c <= '\u9fff') => GuidEncodingType.Base4096CJK,
            12 when encoded.All(c => char.IsDigit(c) || (c >= 'a' && c <= 'f')) => GuidEncodingType.HexTruncated,
            _ => GuidEncodingType.Unknown
        };
    }
}

/// <summary>
/// GUID编码类型枚举
/// </summary>
public enum GuidEncodingType
{
    Unknown,
    Base64,
    Base4096CJK,
    HexTruncated // 旧的12位十六进制格式
}
```

### 1.1 节点标识符

```csharp
/// <summary>
/// 认知节点的唯一标识符
/// 使用统一的GUID编码策略，包括根节点的特殊处理
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

    /// <summary>
    /// 生成新的NodeId，使用统一的GUID编码
    /// </summary>
    public static NodeId Generate() => new(GuidEncoder.ToIdString(Guid.NewGuid()));

    /// <summary>
    /// 根节点的特殊ID - 使用Guid.Empty确保唯一性
    /// 当前编码结果: AAAAAAAAAAAAAAAAAAAAAA (22个A字符)
    /// 优势: 1) 零冲突风险 2) 格式一致性 3) 简化验证逻辑
    /// </summary>
    public static NodeId Root => new(RootValue);

    /// <summary>
    /// 根节点ID的字符串值（缓存以提高性能）
    /// </summary>
    private static readonly string RootValue = GuidEncoder.ToIdString(Guid.Empty);

    /// <summary>
    /// 检查当前NodeId是否为根节点
    /// </summary>
    public bool IsRoot => Value == RootValue;

    /// <summary>
    /// 验证ID格式是否有效（支持向后兼容）
    /// </summary>
    public static bool IsValidFormat(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        // 检测编码类型并验证
        var encodingType = GuidEncoder.DetectEncodingType(value);
        if (encodingType != GuidEncodingType.Unknown)
            return true;

        // 兼容旧的"root"字符串（迁移期间）
        if (value == "root")
            return true;

        return false;
    }

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

    public static RelationId Generate() => new(GuidEncoder.ToIdString(Guid.NewGuid()));

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

    /// <summary>
    /// 自定义属性字典
    ///
    /// MVP阶段类型约定：
    /// - 支持基本类型：string, int, long, double, bool, DateTime
    /// - 支持集合类型：string[], List&lt;string&gt;
    /// - 避免复杂对象，优先使用JSON字符串存储
    /// - 所有访问都应使用 CustomPropertiesExtensions 提供的安全方法
    ///
    /// 长期规划：Phase 5将升级为 IReadOnlyDictionary&lt;string, JsonElement&gt; 提供完整类型安全
    /// </summary>
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

    /// <summary>
    /// 关系属性字典
    ///
    /// 类型约定与 NodeMetadata.CustomProperties 相同：
    /// - 支持基本类型：string, int, long, double, bool, DateTime
    /// - 支持集合类型：string[], List&lt;string&gt;
    /// - 使用 CustomPropertiesExtensions 提供的安全访问方法
    ///
    /// 常见关系属性示例：
    /// - "weight": 关系权重 (double)
    /// - "confidence": 置信度 (double, 0.0-1.0)
    /// - "bidirectional": 是否双向关系 (bool)
    /// - "created_by": 创建者 (string)
    /// </summary>
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

## 3.5 CustomProperties 类型安全扩展方法

```csharp
/// <summary>
/// NodeMetadata.CustomProperties 的类型安全访问扩展方法
/// 提供MVP阶段的类型安全访问模式，避免直接类型转换
/// </summary>
public static class CustomPropertiesExtensions
{
    /// <summary>
    /// 安全获取字符串属性
    /// </summary>
    public static string? GetString(this IReadOnlyDictionary<string, object> properties, string key)
    {
        return properties.TryGetValue(key, out var value) ? value as string : null;
    }

    /// <summary>
    /// 安全获取整数属性
    /// </summary>
    public static int? GetInt32(this IReadOnlyDictionary<string, object> properties, string key)
    {
        if (!properties.TryGetValue(key, out var value)) return null;
        return value switch
        {
            int intValue => intValue,
            long longValue when longValue >= int.MinValue && longValue <= int.MaxValue => (int)longValue,
            string strValue when int.TryParse(strValue, out var parsed) => parsed,
            _ => null
        };
    }

    /// <summary>
    /// 安全获取长整数属性
    /// </summary>
    public static long? GetInt64(this IReadOnlyDictionary<string, object> properties, string key)
    {
        if (!properties.TryGetValue(key, out var value)) return null;
        return value switch
        {
            long longValue => longValue,
            int intValue => intValue,
            string strValue when long.TryParse(strValue, out var parsed) => parsed,
            _ => null
        };
    }

    /// <summary>
    /// 安全获取双精度浮点数属性
    /// </summary>
    public static double? GetDouble(this IReadOnlyDictionary<string, object> properties, string key)
    {
        if (!properties.TryGetValue(key, out var value)) return null;
        return value switch
        {
            double doubleValue => doubleValue,
            float floatValue => floatValue,
            int intValue => intValue,
            long longValue => longValue,
            string strValue when double.TryParse(strValue, out var parsed) => parsed,
            _ => null
        };
    }

    /// <summary>
    /// 安全获取布尔属性
    /// </summary>
    public static bool? GetBoolean(this IReadOnlyDictionary<string, object> properties, string key)
    {
        if (!properties.TryGetValue(key, out var value)) return null;
        return value switch
        {
            bool boolValue => boolValue,
            string strValue when bool.TryParse(strValue, out var parsed) => parsed,
            _ => null
        };
    }

    /// <summary>
    /// 安全获取日期时间属性
    /// </summary>
    public static DateTime? GetDateTime(this IReadOnlyDictionary<string, object> properties, string key)
    {
        if (!properties.TryGetValue(key, out var value)) return null;
        return value switch
        {
            DateTime dateValue => dateValue,
            string strValue when DateTime.TryParse(strValue, out var parsed) => parsed,
            _ => null
        };
    }

    /// <summary>
    /// 安全获取字符串数组属性
    /// </summary>
    public static string[]? GetStringArray(this IReadOnlyDictionary<string, object> properties, string key)
    {
        if (!properties.TryGetValue(key, out var value)) return null;
        return value switch
        {
            string[] arrayValue => arrayValue,
            List<string> listValue => listValue.ToArray(),
            IEnumerable<string> enumValue => enumValue.ToArray(),
            _ => null
        };
    }

    /// <summary>
    /// 安全获取属性，带默认值
    /// </summary>
    public static T GetValueOrDefault<T>(this IReadOnlyDictionary<string, object> properties, string key, T defaultValue)
    {
        if (!properties.TryGetValue(key, out var value)) return defaultValue;
        try
        {
            return value is T typedValue ? typedValue : defaultValue;
        }
        catch
        {
            return defaultValue;
        }
    }

    /// <summary>
    /// 检查属性是否存在且为指定类型
    /// </summary>
    public static bool HasProperty<T>(this IReadOnlyDictionary<string, object> properties, string key)
    {
        return properties.TryGetValue(key, out var value) && value is T;
    }
}
```

### 使用示例

```csharp
// 安全的属性访问
var metadata = new NodeMetadata { /* ... */ };

// 推荐的安全访问方式
string? author = metadata.CustomProperties.GetString("author");
int? priority = metadata.CustomProperties.GetInt32("priority");
long? fileSize = metadata.CustomProperties.GetInt64("fileSize");
double? weight = metadata.CustomProperties.GetDouble("weight");
bool isPublic = metadata.CustomProperties.GetValueOrDefault("isPublic", false);
DateTime? deadline = metadata.CustomProperties.GetDateTime("deadline");
string[] categories = metadata.CustomProperties.GetStringArray("categories") ?? Array.Empty<string>();

// 类型检查
if (metadata.CustomProperties.HasProperty<string>("description"))
{
    // 安全处理
}

// ❌ 避免直接转换（容易出错）
// var author = (string)metadata.CustomProperties["author"]; // 可能抛异常
// var priority = metadata.CustomProperties["priority"] as int?; // 可能返回null
```

### 长期方案：JsonElement 类型安全升级

```csharp
// Phase 5 长期方案：使用 JsonElement 提供完整类型安全
// 将在 Phase5_Extensions.md 中详细定义
// 需要引用：using System.Text.Json;

/// <summary>
/// Phase 5 升级版本的节点元数据（类型安全版本）
/// </summary>
public record NodeMetadataV2
{
    // ... 其他属性保持不变

    /// <summary>
    /// 类型安全的自定义属性字典
    /// 使用 JsonElement 保留结构化信息，提供安全的类型提取
    /// </summary>
    public IReadOnlyDictionary<string, JsonElement> CustomProperties { get; init; } =
        new Dictionary<string, JsonElement>();
}

/// <summary>
/// JsonElement 扩展方法，提供安全的类型提取
/// </summary>
public static class JsonElementExtensions
{
    public static string? TryGetString(this JsonElement element) =>
        element.ValueKind == JsonValueKind.String ? element.GetString() : null;

    public static int? TryGetInt32(this JsonElement element) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value) ? value : null;

    public static bool? TryGetBoolean(this JsonElement element) =>
        element.ValueKind == JsonValueKind.True ? true :
        element.ValueKind == JsonValueKind.False ? false : null;

    // ... 更多类型安全方法
}

// 使用示例（Phase 5）
var author = metadata.CustomProperties.TryGetValue("author", out var authorElement)
    ? authorElement.TryGetString()
    : null;
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
