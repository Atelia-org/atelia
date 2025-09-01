# MemoTree 工厂和构建器模式 (Phase 5)

> **文档版本**: 1.0  
> **创建日期**: 2025-07-25  
> **最后更新**: 2025-07-25  
> **文档状态**: ✅ 已完成  
> **预计行数**: ~300行  
> **实际行数**: 300行  

## 概述

本文档定义了MemoTree系统中的工厂和构建器模式相关类型，提供了创建复杂认知节点对象的标准化接口。这些模式简化了对象创建过程，支持流畅的API设计，并确保对象创建的一致性和可维护性。

### 核心特性

- **工厂模式**: 提供标准化的节点创建接口
- **构建器模式**: 支持流畅的API和复杂对象构建
- **验证集成**: 构建过程中的数据验证支持
- **方法链**: 语义化的流畅接口设计
- **类型安全**: 编译时类型检查和约束

### 设计原则

1. **简化创建**: 隐藏复杂的对象创建逻辑
2. **流畅接口**: 提供直观的方法链调用
3. **验证集成**: 构建过程中的数据完整性保障
4. **可扩展性**: 支持新的创建模式和验证规则
5. **一致性**: 统一的对象创建标准

## 节点工厂接口

### ICognitiveNodeFactory

认知节点工厂接口，提供标准化的节点创建方法。

```csharp
/// <summary>
/// 认知节点工厂接口
/// </summary>
public interface ICognitiveNodeFactory
{
    /// <summary>
    /// 创建新的认知节点
    /// </summary>
    /// <param name="type">节点类型</param>
    /// <param name="title">节点标题</param>
    /// <param name="parentId">父节点ID（可选）</param>
    /// <returns>创建的认知节点</returns>
    CognitiveNode CreateNode(NodeType type, string title, NodeId? parentId = null);

    /// <summary>
    /// 从元数据创建节点
    /// </summary>
    /// <param name="metadata">节点元数据</param>
    /// <returns>创建的认知节点</returns>
    CognitiveNode CreateFromMetadata(NodeMetadata metadata);

    /// <summary>
    /// 创建根节点
    /// </summary>
    /// <returns>根节点实例</returns>
    CognitiveNode CreateRootNode();
}
```

### 工厂接口特性

- **简单创建**: 提供最常用的节点创建方法
- **元数据支持**: 从完整元数据创建节点
- **根节点创建**: 专门的根节点创建方法
- **类型安全**: 强类型参数和返回值
- **可扩展**: 支持添加新的创建方法

## 节点构建器接口

### ICognitiveNodeBuilder

流畅的节点构建器接口，支持复杂节点的逐步构建。

```csharp
/// <summary>
/// 流畅的节点构建器接口
/// </summary>
public interface ICognitiveNodeBuilder
{
    /// <summary>
    /// 设置节点类型
    /// </summary>
    /// <param name="type">节点类型</param>
    /// <returns>构建器实例</returns>
    ICognitiveNodeBuilder OfType(NodeType type);

    /// <summary>
    /// 设置标题
    /// </summary>
    /// <param name="title">节点标题</param>
    /// <returns>构建器实例</returns>
    ICognitiveNodeBuilder WithTitle(string title);

    /// <summary>
    /// 设置父节点
    /// </summary>
    /// <param name="parentId">父节点ID</param>
    /// <returns>构建器实例</returns>
    ICognitiveNodeBuilder UnderParent(NodeId parentId);

    /// <summary>
    /// 作为根节点
    /// </summary>
    /// <returns>构建器实例</returns>
    ICognitiveNodeBuilder AsRoot();

    /// <summary>
    /// 添加单个标签
    /// </summary>
    /// <param name="tag">标签名称</param>
    /// <returns>构建器实例</returns>
    ICognitiveNodeBuilder Tagged(string tag);

    /// <summary>
    /// 添加多个标签
    /// </summary>
    /// <param name="tags">标签数组</param>
    /// <returns>构建器实例</returns>
    ICognitiveNodeBuilder TaggedWith(params string[] tags);

    /// <summary>
    /// 添加标签集合
    /// </summary>
    /// <param name="tags">标签集合</param>
    /// <returns>构建器实例</returns>
    ICognitiveNodeBuilder TaggedWith(IEnumerable<string> tags);

    /// <summary>
    /// 设置详细内容
    /// </summary>
    /// <param name="content">详细内容</param>
    /// <returns>构建器实例</returns>
    ICognitiveNodeBuilder WithDetailContent(string content);

    /// <summary>
    /// 设置摘要内容
    /// </summary>
    /// <param name="content">摘要内容</param>
    /// <returns>构建器实例</returns>
    ICognitiveNodeBuilder WithSummaryContent(string content);

    /// <summary>
    /// 设置简要内容
    /// </summary>
    /// <param name="content">简要内容</param>
    /// <returns>构建器实例</returns>
    ICognitiveNodeBuilder WithTitleContent(string content);

    /// <summary>
    /// 设置指定级别的内容
    /// </summary>
    /// <param name="level">LOD级别</param>
    /// <param name="content">内容</param>
    /// <returns>构建器实例</returns>
    ICognitiveNodeBuilder WithContent(LodLevel level, string content);

    /// <summary>
    /// 添加外部链接
    /// </summary>
    /// <param name="path">链接路径</param>
    /// <param name="type">链接类型</param>
    /// <param name="description">链接描述</param>
    /// <returns>构建器实例</returns>
    ICognitiveNodeBuilder WithExternalLink(string path, ExternalLinkType type, string description = "");

    /// <summary>
    /// 设置创建时间
    /// </summary>
    /// <param name="timestamp">时间戳</param>
    /// <returns>构建器实例</returns>
    ICognitiveNodeBuilder CreatedAt(DateTime timestamp);

    /// <summary>
    /// 使用当前时间作为创建时间
    /// </summary>
    /// <returns>构建器实例</returns>
    ICognitiveNodeBuilder CreatedNow();

    /// <summary>
    /// 构建节点
    /// </summary>
    /// <returns>构建的认知节点</returns>
    CognitiveNode Build();

    /// <summary>
    /// 构建并验证节点
    /// </summary>
    /// <param name="validator">节点验证器</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>节点和验证结果的元组</returns>
    Task<(CognitiveNode Node, ValidationResult Validation)> BuildAndValidateAsync(
        INodeValidator validator, 
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 重置构建器到初始状态
    /// </summary>
    /// <returns>重置后的构建器实例</returns>
    ICognitiveNodeBuilder Reset();

    /// <summary>
    /// 从现有节点创建构建器
    /// </summary>
    /// <param name="node">现有节点</param>
    /// <returns>基于现有节点的构建器实例</returns>
    ICognitiveNodeBuilder FromExisting(CognitiveNode node);
}
```

### 构建器接口特性

- **流畅接口**: 支持方法链调用
- **语义化API**: 直观的方法命名
- **灵活配置**: 支持各种节点属性设置
- **验证集成**: 构建时验证支持
- **状态管理**: 重置和复用功能
- **现有节点支持**: 从现有节点创建构建器

## 使用示例

### 基本工厂使用

```csharp
// 使用工厂模式创建节点
var nodeFactory = serviceProvider.GetRequiredService<ICognitiveNodeFactory>();
var node = nodeFactory.CreateNode(NodeType.Concept, "依赖注入原理");

// 从元数据创建节点
var metadata = new NodeMetadata
{
    Type = NodeType.Concept,
    Title = "设计模式",
    Tags = new[] { "architecture", "patterns" }
};
var nodeFromMetadata = nodeFactory.CreateFromMetadata(metadata);

// 创建根节点
var rootNode = nodeFactory.CreateRootNode();
```

### 流畅构建器使用

```csharp
// 使用流畅构建器创建复杂节点
var builder = serviceProvider.GetRequiredService<ICognitiveNodeBuilder>();
var complexNode = await builder
    .OfType(NodeType.Concept)
    .WithTitle("SOLID原则详解")
    .TaggedWith("architecture", "design-patterns", "best-practices")
    .WithDetailContent("SOLID原则是面向对象设计的五个基本原则...")
    .WithSummaryContent("SOLID原则包括单一职责、开闭原则等五个原则")
    .WithTitleContent("SOLID原则")
    .CreatedNow()
    .BuildAndValidateAsync(validator);

if (complexNode.Validation.IsValid)
{
    await storage.SaveCompleteNodeAsync(complexNode.Node);
}
```

### 构建器重用和重置

```csharp
// 重用构建器创建多个相似节点
var builder = serviceProvider.GetRequiredService<ICognitiveNodeBuilder>();

var node1 = builder
    .OfType(NodeType.Concept)
    .WithTitle("第一个概念")
    .TaggedWith("category1")
    .Build();

// 重置构建器
builder.Reset();

var node2 = builder
    .OfType(NodeType.Process)
    .WithTitle("第二个流程")
    .TaggedWith("category2")
    .Build();
```

### 从现有节点创建构建器

```csharp
// 从现有节点创建构建器进行修改
var existingNode = await storage.GetNodeAsync(nodeId);
var modifiedNode = builder
    .FromExisting(existingNode)
    .TaggedWith("updated")
    .WithSummaryContent("更新的摘要内容")
    .Build();
```

## 实施优先级

### 高优先级 (P0)
- **ICognitiveNodeFactory**: 基础节点创建功能
- **ICognitiveNodeBuilder**: 流畅构建器核心接口
- **基本构建方法**: OfType, WithTitle, Build等核心方法

### 中优先级 (P1)
- **验证集成**: BuildAndValidateAsync方法
- **标签支持**: Tagged, TaggedWith方法
- **内容设置**: WithContent系列方法

### 低优先级 (P2)
- **外部链接**: WithExternalLink方法
- **时间设置**: CreatedAt, CreatedNow方法
- **状态管理**: Reset, FromExisting方法

### 最佳实践

1. **工厂模式使用**
   - 简单对象创建使用工厂模式
   - 复杂对象创建使用构建器模式
   - 保持工厂方法的简洁性

2. **构建器模式使用**
   - 利用方法链提高代码可读性
   - 在构建完成前进行验证
   - 重用构建器实例以提高性能

3. **验证集成**
   - 优先使用BuildAndValidateAsync
   - 处理验证失败的情况
   - 提供有意义的错误信息

4. **性能考虑**
   - 重用构建器实例
   - 避免不必要的对象创建
   - 合理使用异步方法

---

**下一阶段**: 项目完成 🎉  
**相关文档**: [Phase1_CoreTypes.md](Phase1_CoreTypes.md) | [Phase3_CoreServices.md](Phase3_CoreServices.md) | [Phase5_Extensions.md](Phase5_Extensions.md)
