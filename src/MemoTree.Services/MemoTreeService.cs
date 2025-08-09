using MemoTree.Core.Storage.Interfaces;
using MemoTree.Core.Types;
using MemoTree.Services.Models;
using Microsoft.Extensions.Logging;
using System.Text;
using ViewState = MemoTree.Services.Models.MemoTreeViewState;

namespace MemoTree.Services;

/// <summary>
/// MemoTree核心服务实现 (MVP版本)
/// </summary>
public class MemoTreeService : IMemoTreeService
{
    private readonly ICognitiveNodeStorage _storage;
    private readonly ILogger<MemoTreeService> _logger;
    private readonly Dictionary<string, ViewState> _viewStates = new();

    public MemoTreeService(
        ICognitiveNodeStorage storage,
        ILogger<MemoTreeService> logger)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<string> RenderViewAsync(string viewName = "default", CancellationToken cancellationToken = default)
    {
        try
        {
            var viewState = GetOrCreateViewState(viewName);
            var rootNodes = await GetRootNodesAsync(cancellationToken);
            
            if (!rootNodes.Any())
            {
                return "# MemoTree认知空间 [空]\n\n暂无认知节点。使用 `memotree create \"标题\"` 创建第一个节点。";
            }

            var stats = await GetViewStatsAsync(viewName, cancellationToken);
            var sb = new StringBuilder();
            
            // 渲染标题和统计信息
            sb.AppendLine($"# MemoTree认知空间 [{stats.TotalNodes} nodes, {stats.ExpandedNodes} expanded, {stats.TotalCharacters} chars]");
            sb.AppendLine();

            // 渲染节点树
            foreach (var rootNode in rootNodes)
            {
                await RenderNodeTreeAsync(sb, rootNode, 0, viewState, cancellationToken);
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to render view {ViewName}", viewName);
            throw;
        }
    }

    public async Task ExpandNodeAsync(NodeId nodeId, string viewName = "default", CancellationToken cancellationToken = default)
    {
        var viewState = GetOrCreateViewState(viewName);
        viewState.NodeStates[nodeId] = LodLevel.Full;
        _logger.LogDebug("Expanded node {NodeId} in view {ViewName}", nodeId, viewName);
    }

    public async Task CollapseNodeAsync(NodeId nodeId, string viewName = "default", CancellationToken cancellationToken = default)
    {
        var viewState = GetOrCreateViewState(viewName);
        viewState.NodeStates[nodeId] = LodLevel.Gist;
        _logger.LogDebug("Collapsed node {NodeId} in view {ViewName}", nodeId, viewName);
    }

    public async Task<IReadOnlyList<NodeTreeItem>> GetNodeTreeAsync(NodeId? rootId = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var nodes = rootId == null 
                ? await GetRootNodesAsync(cancellationToken)
                : new[] { await _storage.GetCompleteNodeAsync(rootId.Value, cancellationToken) }.Where(n => n != null).Cast<CognitiveNode>();

            var treeItems = new List<NodeTreeItem>();
            
            foreach (var node in nodes)
            {
                var treeItem = await BuildNodeTreeItemAsync(node, 0, cancellationToken);
                treeItems.Add(treeItem);
            }

            return treeItems;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get node tree for root {RootId}", rootId);
            throw;
        }
    }

    public async Task<ViewStats> GetViewStatsAsync(string viewName = "default", CancellationToken cancellationToken = default)
    {
        try
        {
            var viewState = GetOrCreateViewState(viewName);
            var allNodeIds = new List<NodeId>();
            await foreach (var metadata in _storage.GetAllAsync(cancellationToken))
            {
                allNodeIds.Add(metadata.Id);
            }

            var expandedNodes = viewState.NodeStates.Count(kvp => kvp.Value == LodLevel.Full);
            var totalCharacters = 0;

            // 计算字符数（简化版本，只计算当前展开的节点）
            foreach (var (nodeId, level) in viewState.NodeStates)
            {
                if (level == LodLevel.Full)
                {
                    var content = await _storage.GetAsync(nodeId, level, cancellationToken);
                    if (content != null)
                    {
                        totalCharacters += content.Content.Length;
                    }
                }
            }

            return new ViewStats
            {
                ViewName = viewName,
                TotalNodes = allNodeIds.Count,
                ExpandedNodes = expandedNodes,
                TotalCharacters = totalCharacters,
                LastUpdated = DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get view stats for {ViewName}", viewName);
            throw;
        }
    }

    public async Task<string> GetNodeRenderContentAsync(NodeId nodeId, string viewName = "default", CancellationToken cancellationToken = default)
    {
        try
        {
            var viewState = GetOrCreateViewState(viewName);
            var level = viewState.NodeStates.GetValueOrDefault(nodeId, LodLevel.Gist);
            
            var metadata = await _storage.GetAsync(nodeId, cancellationToken);
            if (metadata == null)
                return $"[节点 {nodeId} 不存在]";

            var content = await _storage.GetAsync(nodeId, level, cancellationToken);
            
            if (level == LodLevel.Gist || content == null)
            {
                return metadata.Title;
            }
            
            return content.Content;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get render content for node {NodeId}", nodeId);
            throw;
        }
    }

    public async Task<IReadOnlyList<NodeId>> FindNodesByTitleAsync(string title, CancellationToken cancellationToken = default)
    {
        try
        {
            var matchingNodes = new List<NodeId>();

            await foreach (var metadata in _storage.GetAllAsync(cancellationToken))
            {
                if (metadata.Title.Equals(title, StringComparison.OrdinalIgnoreCase))
                {
                    matchingNodes.Add(metadata.Id);
                }
            }

            return matchingNodes;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to find nodes by title {Title}", title);
            throw;
        }
    }

    #region Private Helper Methods

    private ViewState GetOrCreateViewState(string viewName)
    {
        if (!_viewStates.TryGetValue(viewName, out var viewState))
        {
            viewState = new ViewState
            {
                ViewName = viewName,
                NodeStates = new Dictionary<NodeId, LodLevel>(),
                FocusNodeId = null,
                LastAccessTime = DateTime.UtcNow
            };
            _viewStates[viewName] = viewState;
        }
        else
        {
            // 更新访问时间
            viewState = viewState with { LastAccessTime = DateTime.UtcNow };
            _viewStates[viewName] = viewState;
        }

        return viewState;
    }

    private async Task<IEnumerable<CognitiveNode>> GetRootNodesAsync(CancellationToken cancellationToken)
    {
        // MVP版本：简化实现，返回所有没有父节点的节点
        var rootNodes = new List<CognitiveNode>();

        await foreach (var metadata in _storage.GetAllAsync(cancellationToken))
        {
            var parent = await _storage.GetParentAsync(metadata.Id, cancellationToken);
            if (parent == null)
            {
                var node = await _storage.GetCompleteNodeAsync(metadata.Id, cancellationToken);
                if (node != null)
                {
                    rootNodes.Add(node);
                }
            }
        }

        return rootNodes;
    }

    private async Task RenderNodeTreeAsync(
        StringBuilder sb, 
        CognitiveNode node, 
        int level, 
        ViewState viewState,
        CancellationToken cancellationToken)
    {
        var indent = new string(' ', level * 2);
        var currentLevel = viewState.NodeStates.GetValueOrDefault(node.Metadata.Id, LodLevel.Gist);
        var isExpanded = currentLevel == LodLevel.Full;
        
        // 渲染节点标题
        var expandedMarker = isExpanded ? "[EXPANDED]" : "";
        sb.AppendLine($"{indent}## {node.Metadata.Title} [{node.Metadata.Id.Value[..8]}] {expandedMarker}");
        
        // 如果展开，渲染内容
        if (isExpanded && node.Contents.TryGetValue(LodLevel.Full, out var fullContent))
        {
            var contentLines = fullContent.Content.Split('\n');
            foreach (var line in contentLines)
            {
                sb.AppendLine($"{indent}   {line}");
            }
            sb.AppendLine();
        }

        // 渲染子节点（MVP版本暂时跳过，因为层次结构未完全实现）
    }

    private async Task<NodeTreeItem> BuildNodeTreeItemAsync(
        CognitiveNode node, 
        int level, 
        CancellationToken cancellationToken)
    {
        var children = await _storage.GetChildrenAsync(node.Metadata.Id, cancellationToken);
        var hasChildren = children.Any();

        // 计算字符数
        var characterCount = 0;
        if (node.Contents.TryGetValue(LodLevel.Full, out var fullContent))
        {
            characterCount = fullContent.Content.Length;
        }

        return new NodeTreeItem
        {
            Id = node.Metadata.Id,
            Title = node.Metadata.Title,
            Type = node.Metadata.Type,
            Level = level,
            HasChildren = hasChildren,
            IsExpanded = false, // MVP版本暂时设为false
            CharacterCount = characterCount,
            EstimatedExpandCharacters = characterCount,
            Children = Array.Empty<NodeTreeItem>() // MVP版本暂时不递归构建
        };
    }

    #endregion
}
