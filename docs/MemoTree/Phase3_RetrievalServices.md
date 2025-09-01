# MemoTree 检索服务接口 (Phase 3)

> **版本**: v1.0
> **阶段**: Phase 3 - 服务层
> **依赖**: Phase1_CoreTypes.md, Phase2_StorageInterfaces.md
> **状态**: 设计完成

## 概述

检索服务接口定义了MemoTree系统的搜索和检索功能，支持多种搜索模式：全文搜索、语义搜索、层次结构搜索和语义关系搜索。该模块为MemoTree提供强大的信息检索能力，支持混合图搜索和智能索引管理。

### 核心特性

- **多模式搜索**: 支持全文、语义、层次结构和关系搜索
- **混合图搜索**: 结合层次结构和语义关系的复合搜索
- **智能索引**: 自动索引管理和重建功能
- **结果排序**: 基于相关性评分的搜索结果排序
- **高亮显示**: 搜索结果中的关键词高亮

## 1. 核心检索服务接口

```csharp
/// <summary>
/// 检索服务接口
/// </summary>
public interface IRetrievalService
{
    /// <summary>
    /// 全文搜索
    /// </summary>
    Task<IReadOnlyList<SearchResult>> FullTextSearchAsync(string query, int maxResults = 10, CancellationToken cancellationToken = default);

    /// <summary>
    /// 语义搜索
    /// </summary>
    Task<IReadOnlyList<SearchResult>> SemanticSearchAsync(string query, int maxResults = 10, CancellationToken cancellationToken = default);

    /// <summary>
    /// 层次结构搜索
    /// </summary>
    Task<IReadOnlyList<NodeId>> HierarchySearchAsync(NodeId startNodeId, int maxDepth = 3, CancellationToken cancellationToken = default);

    /// <summary>
    /// 语义关系搜索
    /// </summary>
    Task<IReadOnlyList<NodeId>> RelationSearchAsync(NodeId startNodeId, RelationType relationType, int maxDepth = 3, CancellationToken cancellationToken = default);

    /// <summary>
    /// 混合图搜索（层次结构 + 语义关系）
    /// </summary>
    Task<IReadOnlyList<SearchResult>> MixedGraphSearchAsync(NodeId startNodeId, string query, int maxDepth = 2, bool includeHierarchy = true, bool includeSemanticRelations = true, CancellationToken cancellationToken = default);

    /// <summary>
    /// 重建索引
    /// </summary>
    Task RebuildIndexAsync(CancellationToken cancellationToken = default);
}
```

## 2. 搜索结果类型

```csharp
/// <summary>
/// 搜索结果
/// </summary>
public record SearchResult
{
    public NodeId NodeId { get; init; }
    public LodLevel Level { get; init; }
    public double Score { get; init; }
    public string Snippet { get; init; } = string.Empty;
    public IReadOnlyList<string> HighlightedTerms { get; init; } = Array.Empty<string>();
}
```

## 3. 工具调用API搜索支持

```csharp
/// <summary>
/// 搜索节点请求
/// </summary>
public record SearchNodesRequest
{
    public string Query { get; init; } = string.Empty;
    public SearchType SearchType { get; init; } = SearchType.FullText;
    public int MaxResults { get; init; } = 10;
    public IReadOnlyList<NodeType>? NodeTypes { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }
}

/// <summary>
/// 搜索类型
/// </summary>
public enum SearchType
{
    FullText,
    Semantic,
    Relation
}
```

> 结果数量上限说明：实际返回数量 = min(request.MaxResults, RetrievalOptions.MaxSearchResults)。
> 默认值：SearchNodesRequest.MaxResults = 10；RetrievalOptions.MaxSearchResults = 50（可通过配置调整）。


## 4. 检索配置选项（引用）

本模块使用的检索配置定义于 Phase1_Configuration.md，并通过依赖注入进行传递，避免重复定义，确保“单一权威来源”。

- 类型来源：Phase1_Configuration.md → RetrievalOptions
- 注入方式：IOptions<RetrievalOptions>

示例：

```csharp
public class RetrievalService : IRetrievalService
{
    private readonly RetrievalOptions _options;
    public RetrievalService(IOptions<RetrievalOptions> options)
    {
        _options = options.Value;
    }
    // ... 其余实现
}
```

## 5. 检索异常类型

```csharp
/// <summary>
/// 检索异常
/// </summary>
public class RetrievalException : MemoTreeException
{
    public RetrievalException(string message) : base(message) { }
    public RetrievalException(string message, Exception innerException) : base(message, innerException) { }
}
```

## 实施优先级

### 高优先级 (P0)
- **IRetrievalService**: 核心检索服务接口
- **SearchResult**: 搜索结果数据结构
- **全文搜索**: 基于Lucene.Net的全文搜索功能

### 中优先级 (P1)
- **层次结构搜索**: 基于节点层次关系的搜索
- **语义关系搜索**: 基于语义关系的搜索
- **RetrievalOptions**: 检索配置选项

### 低优先级 (P2)
- **语义搜索**: 基于向量的语义搜索
- **混合图搜索**: 复合搜索功能
- **索引重建**: 自动索引管理

### 最佳实践

1. **搜索性能优化**
   - 使用适当的索引策略
   - 实施搜索结果缓存
   - 支持分页和限制结果数量

2. **搜索质量提升**
   - 实施相关性评分算法
   - 支持模糊匹配和同义词扩展
   - 提供搜索建议和自动完成

3. **用户体验优化**
   - 提供搜索结果高亮
   - 支持搜索历史和收藏
   - 实施搜索结果预览

---

**下一阶段**: [Phase4_ToolCallAPI.md](Phase4_ToolCallAPI.md) - 工具调用API设计
