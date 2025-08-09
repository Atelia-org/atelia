# C# Partial Class 在 MemoTree 中的应用示例

## 概述

展示如何使用 C# partial class 功能来组织复杂的类型定义，使其更适合 LLM 按功能维度查看和修改。


> 架构说明（内存优先）：本示例以 partial class 展示拆分方式，代码中的 INodeCacheService 仅代表“派生结果/索引类的轻量缓存或视图聚合”，不建议作为独立的二级数据缓存。MemoTree 的主数据（已加载节点）常驻内存且与磁盘同步，避免与缓存产生一致性分叉。

## 1. 复杂服务类的拆分示例

### 1.1 主文件：MemoTreeService.Core.cs
```csharp
namespace MemoTree.Services
{
    /// <summary>
    /// MemoTree核心服务 - 核心功能
    /// </summary>
    public partial class MemoTreeService : IMemoTreeService
    {
        private readonly ICognitiveNodeStorage _storage;
        private readonly INodeCacheService _cache;
        private readonly ILogger<CognitiveCanvasService> _logger;

        public MemoTreeService(
            ICognitiveNodeStorage storage,
            INodeCacheService cache,
            ILogger<MemoTreeService> logger)
        {
            _storage = storage;
            _cache = cache;
            _logger = logger;
        }

        /// <summary>
        /// 渲染指定视图的Markdown内容
        /// </summary>
        public async Task<string> RenderViewAsync(string viewName, CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("Rendering view: {ViewName}", viewName);

            var viewState = await GetViewStateAsync(viewName, cancellationToken);
            if (viewState == null)
            {
                throw new InvalidOperationException($"View '{viewName}' not found");
            }

            var markdown = new StringBuilder();
            foreach (var nodeState in viewState.NodeStates.Where(n => n.IsVisible))
            {
                var nodeMarkdown = await RenderNodeAsync(nodeState, cancellationToken);
                markdown.AppendLine(nodeMarkdown);
            }

            return markdown.ToString();
        }
    }
}
```

### 1.2 节点操作：MemoTreeService.NodeOperations.cs
```csharp
namespace MemoTree.Services
{
    /// <summary>
    /// MemoTree服务 - 节点操作功能
    /// </summary>
    public partial class MemoTreeService
    {
        /// <summary>
        /// 展开节点到指定LOD级别
        /// </summary>
        public async Task ExpandNodeAsync(string viewName, NodeId nodeId, LodLevel level, CancellationToken cancellationToken = default)
        {
            _logger.LogDebug("Expanding node {NodeId} to level {Level} in view {ViewName}", nodeId, level, viewName);

            var viewState = await GetViewStateAsync(viewName, cancellationToken);
            if (viewState == null)
            {
                throw new InvalidOperationException($"View '{viewName}' not found");
            }

            var nodeState = viewState.NodeStates.FirstOrDefault(n => n.Id == nodeId);
            if (nodeState == null)
            {
                throw new NodeNotFoundException(nodeId);
            }

            // 更新节点状态
            var updatedNodeState = nodeState with
            {
                CurrentLevel = level,
                IsExpanded = level > LodLevel.Title
            };

            await UpdateNodeStateAsync(viewName, updatedNodeState, cancellationToken);

            // 预加载相关内容到缓存
            await _cache.PreloadRelatedNodesAsync(nodeId, depth: 1, cancellationToken);
        }

        /// <summary>
        /// 折叠节点
        /// </summary>
        public async Task CollapseNodeAsync(string viewName, NodeId nodeId, CancellationToken cancellationToken = default)
        {
            _logger.LogDebug("Collapsing node {NodeId} in view {ViewName}", nodeId, viewName);

            var viewState = await GetViewStateAsync(viewName, cancellationToken);
            if (viewState == null)
            {
                throw new InvalidOperationException($"View '{viewName}' not found");
            }

            var nodeState = viewState.NodeStates.FirstOrDefault(n => n.Id == nodeId);
            if (nodeState == null)
            {
                throw new NodeNotFoundException(nodeId);
            }

            // 折叠到标题级别
            var updatedNodeState = nodeState with
            {
                CurrentLevel = LodLevel.Title,
                IsExpanded = false
            };

            await UpdateNodeStateAsync(viewName, updatedNodeState, cancellationToken);
        }
    }
}
```

### 1.3 树结构操作：MemoTreeService.TreeOperations.cs
```csharp
namespace MemoTree.Services
{
    /// <summary>
    /// MemoTree服务 - 树结构操作功能
    /// </summary>
    public partial class MemoTreeService
    {
        /// <summary>
        /// 获取节点树结构（基于层次结构存储）
        /// </summary>
        public async Task<IReadOnlyList<NodeTreeItem>> GetNodeTreeAsync(NodeId? rootId = null, CancellationToken cancellationToken = default)
        {
            _logger.LogDebug("Building node tree from root: {RootId}", rootId?.ToString() ?? "null");

            var actualRootId = rootId ?? NodeId.Root;
            var rootMetadata = await _storage.GetAsync(actualRootId, cancellationToken);

            if (rootMetadata == null)
            {
                return Array.Empty<NodeTreeItem>();
            }

            var treeItems = new List<NodeTreeItem>();
            await BuildTreeRecursiveAsync(actualRootId, 0, treeItems, cancellationToken);

            return treeItems;
        }

        private async Task BuildTreeRecursiveAsync(NodeId nodeId, int level, List<NodeTreeItem> treeItems, CancellationToken cancellationToken)
        {
            var metadata = await _storage.GetAsync(nodeId, cancellationToken);
            if (metadata == null) return;

            var children = await _storage.GetChildrenAsync(nodeId, cancellationToken);
            var hasChildren = children.Any();

            var treeItem = new NodeTreeItem
            {
                Id = nodeId,
                Title = metadata.Title,
                Type = metadata.Type,
                Level = level,
                HasChildren = hasChildren,
                Children = Array.Empty<NodeTreeItem>()
            };

            treeItems.Add(treeItem);

            // 递归构建子节点
            if (hasChildren)
            {
                var childItems = new List<NodeTreeItem>();
                foreach (var childId in children)
                {
                    await BuildTreeRecursiveAsync(childId, level + 1, childItems, cancellationToken);
                }

                // 更新树项的子节点
                var updatedTreeItem = treeItem with { Children = childItems };
                treeItems[treeItems.Count - 1] = updatedTreeItem;
            }
        }
    }
}
```

### 1.4 FIFO策略：MemoTreeService.FifoStrategy.cs
```csharp
namespace MemoTree.Services
{
    /// <summary>
    /// MemoTree服务 - FIFO上下文管理策略
    /// </summary>
    public partial class MemoTreeService
    {
        /// <summary>
        /// 应用FIFO策略管理上下文窗口
        /// </summary>
        public async Task ApplyFifoStrategyAsync(string viewName, int maxTokens, CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("Applying FIFO strategy to view {ViewName} with max tokens {MaxTokens}", viewName, maxTokens);

            var viewState = await GetViewStateAsync(viewName, cancellationToken);
            if (viewState == null)
            {
                throw new InvalidOperationException($"View '{viewName}' not found");
            }

            var currentTokens = await CalculateCurrentTokensAsync(viewState, cancellationToken);
            if (currentTokens <= maxTokens)
            {
                _logger.LogDebug("Current tokens {CurrentTokens} within limit {MaxTokens}", currentTokens, maxTokens);
                return;
            }

            _logger.LogInformation("Current tokens {CurrentTokens} exceeds limit {MaxTokens}, applying FIFO", currentTokens, maxTokens);

            // 按时间顺序排序节点状态（最旧的在前）
            var sortedNodes = viewState.NodeStates
                .Where(n => n.IsExpanded)
                .OrderBy(n => GetNodeLastAccessTime(n.Id))
                .ToList();

            // 逐个折叠最旧的节点直到满足Token限制
            foreach (var nodeState in sortedNodes)
            {
                if (currentTokens <= maxTokens) break;

                await CollapseNodeAsync(viewName, nodeState.Id, cancellationToken);

                // 重新计算Token数
                var updatedViewState = await GetViewStateAsync(viewName, cancellationToken);
                currentTokens = await CalculateCurrentTokensAsync(updatedViewState!, cancellationToken);

                _logger.LogDebug("Collapsed node {NodeId}, current tokens: {CurrentTokens}", nodeState.Id, currentTokens);
            }

            _logger.LogInformation("FIFO strategy completed, final tokens: {FinalTokens}", currentTokens);
        }

        private async Task<int> CalculateCurrentTokensAsync(MemoTreeViewState viewState, CancellationToken cancellationToken)
        {
            int totalTokens = 0;

            foreach (var nodeState in viewState.NodeStates.Where(n => n.IsVisible))
            {
                var content = await GetNodeContentForLevelAsync(nodeState.Id, nodeState.CurrentLevel, cancellationToken);
                if (content != null)
                {
                    // 简单的Token估算：每4个字符约等于1个Token
                    totalTokens += content.Length / 4;
                }
            }

            return totalTokens;
        }

        private DateTime GetNodeLastAccessTime(NodeId nodeId)
        {
            // 这里应该从缓存或数据库中获取节点的最后访问时间
            // 简化实现，返回当前时间
            return DateTime.UtcNow;
        }
    }
}
```

## 2. 优势总结

### 2.1 对开发者的优势
- **关注点分离**：每个文件专注于特定功能领域
- **并行开发**：团队成员可以同时编辑不同的功能模块
- **代码导航**：IDE可以更好地组织和导航代码

### 2.2 对LLM的优势
- **上下文聚焦**：可以只加载相关功能的代码进行分析
- **精确修改**：针对特定功能进行修改时不会影响其他部分
- **认知负荷降低**：避免处理过多不相关的代码

### 2.3 维护优势
- **版本控制友好**：不同功能的修改不会产生合并冲突
- **测试隔离**：可以针对特定功能编写独立的单元测试
- **重构安全**：重构某个功能时不会意外影响其他功能

## 3. 最佳实践

1. **文件命名约定**：使用 `ClassName.FunctionArea.cs` 格式
2. **功能内聚**：确保每个partial文件内的方法都高度相关
3. **依赖管理**：将共享的字段和属性放在主文件中
4. **文档同步**：每个partial文件都应该有对应的文档说明

这种方式特别适合复杂的服务类、大型的数据传输对象和包含多种操作的管理器类。
