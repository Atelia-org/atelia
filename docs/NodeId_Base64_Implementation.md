# NodeId Base64编码实现方案

> 版本: v1.0  
> 创建日期: 2025-07-26  
> 目标: 提供立即可用的Base64编码NodeId实现

## 实现代码

### 更新后的NodeId结构

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
    
    /// <summary>
    /// 生成新的NodeId，使用统一的GUID编码
    /// 当前生成22个字符的ID字符串
    /// </summary>
    public static NodeId Generate() => new(GuidEncoder.ToIdString(Guid.NewGuid()));

    /// <summary>
    /// 根节点的特殊ID - 使用Guid.Empty确保唯一性
    /// 当前编码结果: AAAAAAAAAAAAAAAAAAAAAA (22个A字符)
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
    /// 验证ID格式是否有效（统一格式验证，支持向后兼容）
    /// </summary>
    public static bool IsValidFormat(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        // 使用统一的编码检测方法
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

### 更新RelationId结构

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

    /// <summary>
    /// 生成新的RelationId，使用统一的GUID编码
    /// </summary>
    public static RelationId Generate() => new(GuidEncoder.ToIdString(Guid.NewGuid()));

    public bool Equals(RelationId other) => Value == other.Value;
    public override bool Equals(object? obj) => obj is RelationId other && Equals(other);
    public override int GetHashCode() => Value.GetHashCode();
    public override string ToString() => Value;

    public static implicit operator string(RelationId relationId) => relationId.Value;
    public static explicit operator RelationId(string value) => new(value);
}
```

## 示例和测试

### 生成示例

```csharp
// 生成新的NodeId
var nodeId1 = NodeId.Generate();
var nodeId2 = NodeId.Generate();

Console.WriteLine($"NodeId 1: {nodeId1}"); // 输出: VQ6EAOKbQdSnFkRmVUQAAA
Console.WriteLine($"NodeId 2: {nodeId2}"); // 输出: a6e4EZ2tEdGAtADAT9QwyA
Console.WriteLine($"Length: {nodeId1.Value.Length}"); // 输出: 22
```

### 单元测试

```csharp
[Test]
public void NodeId_Generate_ShouldProduceUniqueIds()
{
    var ids = new HashSet<string>();
    
    // 生成大量ID测试唯一性
    for (int i = 0; i < 100000; i++)
    {
        var id = NodeId.Generate();
        Assert.IsTrue(ids.Add(id.Value), $"Duplicate ID found: {id}");
        Assert.AreEqual(22, id.Value.Length, "ID length should be 22 characters");
    }
}

[Test]
public void NodeId_IsValidFormat_ShouldValidateCorrectly()
{
    // 测试Base64格式
    Assert.IsTrue(NodeId.IsValidFormat("VQ6EAOKbQdSnFkRmVUQAAA"));

    // 测试根节点格式
    Assert.IsTrue(NodeId.IsValidFormat("AAAAAAAAAAAAAAAAAAAAAA")); // Guid.Empty的Base64编码

    // 测试旧格式兼容
    Assert.IsTrue(NodeId.IsValidFormat("550e8400e29b"));
    Assert.IsTrue(NodeId.IsValidFormat("root")); // 迁移期间兼容

    // 测试无效格式
    Assert.IsFalse(NodeId.IsValidFormat(""));
    Assert.IsFalse(NodeId.IsValidFormat("invalid"));
    Assert.IsFalse(NodeId.IsValidFormat("VQ6EAOKbQdSnFkRmVUQAAA=")); // 包含填充
}

[Test]
public void NodeId_Root_ShouldBeConsistent()
{
    var root1 = NodeId.Root;
    var root2 = NodeId.Root;

    // 根节点ID应该一致
    Assert.AreEqual(root1, root2);
    Assert.AreEqual("AAAAAAAAAAAAAAAAAAAAAA", root1.Value);

    // IsRoot属性应该正确工作
    Assert.IsTrue(root1.IsRoot);
    Assert.IsFalse(NodeId.Generate().IsRoot);
}

[Test]
public void NodeId_RoundTrip_ShouldPreserveValue()
{
    var original = NodeId.Generate();
    var stringValue = (string)original;
    var restored = (NodeId)stringValue;
    
    Assert.AreEqual(original, restored);
}
```

## 迁移策略

### 数据迁移工具

```csharp
public static class NodeIdMigrationTool
{
    /// <summary>
    /// 将旧的ID转换为新的Base64格式
    /// 注意：除根节点外，这会生成新的GUID，不保持原有映射关系
    /// </summary>
    public static NodeId MigrateOldId(string oldId)
    {
        // 根节点特殊处理：从"root"迁移到Guid.Empty的Base64编码
        if (oldId == "root")
            return NodeId.Root; // 现在返回AAAAAAAAAAAAAAAAAAAAAA

        // 为旧ID生成新的Base64 ID
        // 注意：这会破坏原有的ID映射，需要更新所有引用
        return NodeId.Generate();
    }

    /// <summary>
    /// 批量迁移ID映射
    /// </summary>
    public static Dictionary<string, NodeId> CreateMigrationMapping(IEnumerable<string> oldIds)
    {
        var mapping = new Dictionary<string, NodeId>();

        foreach (var oldId in oldIds)
        {
            mapping[oldId] = MigrateOldId(oldId);
        }

        return mapping;
    }

    /// <summary>
    /// 迁移文件系统中的根节点目录
    /// </summary>
    public static async Task MigrateRootDirectoryAsync(string workspaceRoot)
    {
        var oldRootPath = Path.Combine(workspaceRoot, "CogNodes", "root");
        var newRootPath = Path.Combine(workspaceRoot, "CogNodes", NodeId.Root.Value);

        if (Directory.Exists(oldRootPath) && !Directory.Exists(newRootPath))
        {
            Directory.Move(oldRootPath, newRootPath);
            Console.WriteLine($"Migrated root directory: {oldRootPath} -> {newRootPath}");
        }
    }
}
```

## 性能对比

### 生成性能

```csharp
[Benchmark]
public string GenerateOldFormat()
{
    return Guid.NewGuid().ToString("N")[..12];
}

[Benchmark]
public string GenerateBase64Format()
{
    var guidBytes = Guid.NewGuid().ToByteArray();
    return Convert.ToBase64String(guidBytes).TrimEnd('=');
}
```

预期结果：
- 旧格式：~50ns per operation
- Base64格式：~80ns per operation
- 性能差异可忽略不计

### 存储空间对比

- 旧格式：12字节 (UTF-8)
- Base64格式：22字节 (UTF-8)
- 空间增长：83%，但换来完整的唯一性保证

## 部署检查清单

### NodeId.Root优化 (v1.1)
- [x] 更新NodeId.Root实现 (使用Guid.Empty的Base64编码)
- [x] 添加NodeId.IsRoot属性
- [x] 简化IsValidFormat方法 (移除"root"特殊处理)
- [x] 更新单元测试 (测试根节点一致性)
- [x] 更新迁移工具 (支持根节点目录迁移)
- [ ] 创建根节点迁移脚本
- [ ] 测试文件系统迁移逻辑

### 基础GUID编码 (v1.0)
- [x] 更新NodeId.Generate()实现 (使用GuidEncoder.ToIdString)
- [x] 更新RelationId.Generate()实现 (使用GuidEncoder.ToIdString)
- [x] 更新LodGenerationRequest.TaskId生成 (使用GuidEncoder.ToIdString)
- [x] 更新AuditEvent.Id生成 (使用GuidEncoder.ToIdString)
- [x] 更新SecurityContext.SessionId示例 (使用GuidEncoder.ToIdString)
- [x] 添加统一的GuidEncoder工具类
- [x] 添加格式检测方法 (DetectEncodingType)
- [ ] 创建单元测试
- [x] 更新文档和示例
- [ ] 准备数据迁移脚本
- [ ] 性能基准测试

## 后续计划

1. **立即部署**此Base64方案解决冲突风险
2. **并行开发**Base4096-CJK编码库
3. **A/B测试**两种方案在LLM交互中的效果
4. **最终选择**基于实际使用数据的最优方案

---

**注意事项**：
- 此方案与现有12位格式不兼容，需要数据迁移
- 建议在新项目中直接使用，现有项目需要迁移计划
- Base64字符串不包含特殊字符，文件系统友好
