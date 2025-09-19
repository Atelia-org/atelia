using MemoTree.Core.Storage.Interfaces;
using MemoTree.Core.Types;
using Microsoft.Extensions.Logging;
using System.Text;
using ViewState = MemoTree.Core.Types.MemoTreeViewState;
using VNodeState = MemoTree.Core.Types.NodeViewState;
using MemoTree.Services.Models;

namespace MemoTree.Services;

/// <summary>
/// MemoTree核心服务实现 (MVP版本)
/// </summary>
public class MemoTreeService : IMemoTreeService {
    private readonly ICognitiveNodeStorage _storage;
    private readonly ILogger<MemoTreeService> _logger;
    private readonly IViewStateStorage _viewStorage;
    private readonly Dictionary<string, ViewState> _viewStates = new();

    public MemoTreeService(
        ICognitiveNodeStorage storage,
        IViewStateStorage viewStorage,
        ILogger<MemoTreeService> logger
    ) {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _viewStorage = viewStorage ?? throw new ArgumentNullException(nameof(viewStorage));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<string> RenderViewAsync(string viewName = "default", CancellationToken cancellationToken = default) {
        try {
            var viewState = await GetOrLoadViewStateAsync(viewName, cancellationToken);
            var topLevelNodes = await GetTopLevelNodesAsync(cancellationToken);
            var stats = await GetViewStatsAsync(viewName, cancellationToken);
            var sb = new StringBuilder();

            // 视图面板（Meta）：当前视图 + 描述 + 其他视图（前N个）
            // MVP 暂不做截断；显示数量做轻约束
            const int maxOtherViews = 5;
            var meta = new StringBuilder();

            var otherViewNames = (await _viewStorage.GetViewNamesAsync(cancellationToken))
            .Where(n => !string.Equals(n, viewName, StringComparison.OrdinalIgnoreCase))
            .Take(maxOtherViews)
            .ToList();

            meta.AppendLine($"# MemoTree 视图面板");
            meta.AppendLine($"- 当前视图: {viewState.Name}");
            if (!string.IsNullOrWhiteSpace(viewState.Description)) {
                meta.AppendLine($"- 说明: {viewState.Description}");
            }

            meta.AppendLine($"- 统计: {stats.TotalNodes} nodes, {stats.ExpandedNodes} expanded, {stats.TotalCharacters} chars");
            if (otherViewNames.Any()) {
                meta.AppendLine("- 其他可用视图:");
                foreach (var name in otherViewNames) {
                    var vs = await _viewStorage.GetViewStateAsync(name, cancellationToken);
                    var desc = vs?.Description ?? string.Empty;
                    var last = await _viewStorage.GetViewLastModifiedAsync(name, cancellationToken);
                    meta.AppendLine($"  - {name}{(string.IsNullOrWhiteSpace(desc) ? string.Empty : $" — {desc}")} {(last.HasValue ? $"(last: {last.Value:yyyy-MM-dd HH:mm})" : string.Empty)}");
                }
            }
            meta.AppendLine("- 操作提示: 使用 switch-view/rename-view/delete-view 等命令进行视图管理");
            meta.AppendLine();

            sb.Append(meta.ToString());

            // 树内容或空引导
            if (!topLevelNodes.Any()) {
                sb.AppendLine("# MemoTree认知空间 [空]");
                sb.AppendLine();
                sb.AppendLine("暂无认知节点。使用 `memotree create \"标题\"` 创建第一个节点。");
            }
            else {
                sb.AppendLine($"# MemoTree认知空间 [{stats.TotalNodes} nodes, {stats.ExpandedNodes} expanded, {stats.TotalCharacters} chars]");
                sb.AppendLine();

                foreach (var topLevelNode in topLevelNodes) {
                    await RenderNodeTreeAsync(sb, topLevelNode, 0, viewState, cancellationToken);
                }
            }

            return sb.ToString();
        }
        catch (Exception ex) {
            _logger.LogError(ex, "Failed to render view {ViewName}", viewName);
            throw;
        }
    }

    public async Task ExpandNodeAsync(NodeId nodeId, string viewName = "default", CancellationToken cancellationToken = default) {
        var viewState = await GetOrLoadViewStateAsync(viewName, cancellationToken);
        var updated = SetNodeLevel(viewState, nodeId, LodLevel.Full);
        _viewStates[viewName] = updated;
        await _viewStorage.SaveViewStateAsync(updated, cancellationToken);
        _logger.LogDebug("Expanded node {NodeId} in view {ViewName}", nodeId, viewName);
    }

    public async Task CollapseNodeAsync(NodeId nodeId, string viewName = "default", CancellationToken cancellationToken = default) {
        var viewState = await GetOrLoadViewStateAsync(viewName, cancellationToken);
        var updated = SetNodeLevel(viewState, nodeId, LodLevel.Gist);
        _viewStates[viewName] = updated;
        await _viewStorage.SaveViewStateAsync(updated, cancellationToken);
        _logger.LogDebug("Collapsed node {NodeId} in view {ViewName}", nodeId, viewName);
    }

    public async Task<IReadOnlyList<NodeTreeItem>> GetNodeTreeAsync(NodeId? rootId = null, CancellationToken cancellationToken = default) {
        try {
            var nodes = rootId == null
            ? await GetTopLevelNodesAsync(cancellationToken)
            : new[] { await _storage.GetCompleteNodeAsync(rootId.Value, cancellationToken) }.Where(n => n != null).Cast<CognitiveNode>();

            var treeItems = new List<NodeTreeItem>();

            foreach (var node in nodes) {
                var treeItem = await BuildNodeTreeItemAsync(node, 0, cancellationToken);
                treeItems.Add(treeItem);
            }

            return treeItems;
        }
        catch (Exception ex) {
            _logger.LogError(ex, "Failed to get node tree for root {RootId}", rootId);
            throw;
        }
    }

    public async Task<ViewStats> GetViewStatsAsync(string viewName = "default", CancellationToken cancellationToken = default) {
        try {
            var viewState = await GetOrLoadViewStateAsync(viewName, cancellationToken);
            var allNodeIds = new List<NodeId>();
            await foreach (var metadata in _storage.GetAllAsync(cancellationToken)) {
                allNodeIds.Add(metadata.Id);
            }

            var expandedNodes = viewState.NodeStates.Count(s => s.CurrentLevel == LodLevel.Full);
            var totalCharacters = 0;

            // 计算字符数（简化版本，只计算当前展开的节点）
            foreach (var s in viewState.NodeStates) {
                if (s.CurrentLevel == LodLevel.Full) {
                    var content = await _storage.GetAsync(s.Id, LodLevel.Full, cancellationToken);
                    if (content != null) {
                        totalCharacters += content.Content.Length;
                    }
                }
            }

            return new ViewStats {
                ViewName = viewName,
                TotalNodes = allNodeIds.Count,
                ExpandedNodes = expandedNodes,
                TotalCharacters = totalCharacters,
                LastUpdated = DateTime.UtcNow
            };
        }
        catch (Exception ex) {
            _logger.LogError(ex, "Failed to get view stats for {ViewName}", viewName);
            throw;
        }
    }

    public async Task<string> GetNodeRenderContentAsync(NodeId nodeId, string viewName = "default", CancellationToken cancellationToken = default) {
        try {
            var viewState = await GetOrLoadViewStateAsync(viewName, cancellationToken);
            var level = GetNodeLevelOrDefault(viewState, nodeId, LodLevel.Gist);

            var metadata = await _storage.GetAsync(nodeId, cancellationToken);
            if (metadata == null) { return $"[节点 {nodeId} 不存在]"; }
            var content = await _storage.GetAsync(nodeId, level, cancellationToken);

            if (content == null) {
                // Gist/Summary 尚未创建摘要时：
                if (level == LodLevel.Gist) { return "尚未创建Gist内容"; /* 占位提示 */ }
                if (level == LodLevel.Summary) { return ""; /* Summary 级别跳过，交由调用者决定不渲染 */ }
                return "";
            }

            return content.Content;
        }
        catch (Exception ex) {
            _logger.LogError(ex, "Failed to get render content for node {NodeId}", nodeId);
            throw;
        }
    }

    public async Task<IReadOnlyList<NodeId>> FindNodesByTitleAsync(string title, CancellationToken cancellationToken = default) {
        try {
            var matchingNodes = new List<NodeId>();

            await foreach (var metadata in _storage.GetAllAsync(cancellationToken)) {
                if (metadata.Title.Equals(title, StringComparison.OrdinalIgnoreCase)) {
                    matchingNodes.Add(metadata.Id);
                }
            }

            return matchingNodes;
        }
        catch (Exception ex) {
            _logger.LogError(ex, "Failed to find nodes by title {Title}", title);
            throw;
        }
    }

    public async Task CreateViewAsync(string viewName, string? description = null, CancellationToken cancellationToken = default) {
        if (string.IsNullOrWhiteSpace(viewName)) { throw new ArgumentException("viewName is required", nameof(viewName)); }
        if (await _viewStorage.ViewExistsAsync(viewName, cancellationToken)) { throw new InvalidOperationException($"View '{viewName}' already exists."); }
        var state = new ViewState {
            Name = viewName,
            Description = description ?? string.Empty,
            LastModified = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            NodeStates = Array.Empty<VNodeState>(),
        };

        await _viewStorage.SaveViewStateAsync(state, cancellationToken);
        _viewStates[viewName] = state;
        _logger.LogInformation("Created view {ViewName}", viewName);
    }

    public async Task UpdateViewDescriptionAsync(string viewName, string description, CancellationToken cancellationToken = default) {
        if (string.IsNullOrWhiteSpace(viewName)) { throw new ArgumentException("viewName is required", nameof(viewName)); }
        var state = await _viewStorage.GetViewStateAsync(viewName, cancellationToken);
        if (state == null) { throw new InvalidOperationException($"View '{viewName}' not found."); }
        var updated = state with {
            Description = description ?? string.Empty,
            LastModified = DateTime.UtcNow
        };
        await _viewStorage.SaveViewStateAsync(updated, cancellationToken);
        _viewStates[viewName] = updated;
        _logger.LogInformation("Updated view description {ViewName}", viewName);
    }

    #region Private Helper Methods

    private async Task<ViewState> GetOrLoadViewStateAsync(string viewName, CancellationToken ct) {
        if (!_viewStates.TryGetValue(viewName, out var viewState)) {
            viewState = await _viewStorage.GetViewStateAsync(viewName, ct)
            ?? new ViewState { Name = viewName, LastModified = DateTime.UtcNow, NodeStates = Array.Empty<VNodeState>() };
            _viewStates[viewName] = viewState;
        }
        else {
            // 更新修改时间为访问时间（近似处理）
            viewState = viewState with {
                LastModified = DateTime.UtcNow
            };
            _viewStates[viewName] = viewState;
        }

        return viewState;
    }

    private static ViewState SetNodeLevel(ViewState state, NodeId id, LodLevel level) {
        var list = state.NodeStates.ToList();
        var idx = list.FindIndex(s => s.Id == id);
        if (idx >= 0) {
            var existing = list[idx];
            list[idx] = existing with {
                CurrentLevel = level,
                IsExpanded = (level == LodLevel.Full),
                LastAccessTime = DateTime.UtcNow
            };
        }
        else {
            list.Add(
                new VNodeState {
                    Id = id,
                    CurrentLevel = level,
                    IsExpanded = (level == LodLevel.Full),
                    IsVisible = true,
                    Order = 0,
                    LastAccessTime = DateTime.UtcNow
                }
            );
        }
        return state with {
            NodeStates = list,
            LastModified = DateTime.UtcNow
        };
    }

    private static LodLevel GetNodeLevelOrDefault(ViewState state, NodeId id, LodLevel @default) {
        var st = state.NodeStates.FirstOrDefault(s => s.Id == id);
        return st is null or default(VNodeState) ? @default : st.CurrentLevel;
    }

    private async Task<IEnumerable<CognitiveNode>> GetTopLevelNodesAsync(CancellationToken cancellationToken) {
        _logger.LogDebug("MemoTreeService.GetTopLevelNodesAsync called");

        // 获取所有顶层节点（无父节点的节点）
        var topLevelNodeIds = await _storage.GetTopLevelNodesAsync(cancellationToken);
        _logger.LogDebug("Found {Count} top-level node IDs", topLevelNodeIds.Count);

        var topLevelNodes = new List<CognitiveNode>();

        foreach (var nodeId in topLevelNodeIds) {
            var node = await _storage.GetCompleteNodeAsync(nodeId, cancellationToken);
            if (node != null) {
                topLevelNodes.Add(node);
                _logger.LogDebug("Loaded top-level node: {NodeId} - {Title}", nodeId, node.Metadata.Title);
            }
            else {
                _logger.LogWarning("Failed to load top-level node: {NodeId}", nodeId);
            }
        }

        _logger.LogDebug("Returning {Count} top-level nodes", topLevelNodes.Count);
        return topLevelNodes;
    }

    private async Task RenderNodeTreeAsync(
        StringBuilder sb,
        CognitiveNode node,
        int level,
        ViewState viewState,
        CancellationToken cancellationToken
    ) {
        var indent = new string(' ', level * 2);
        var currentLevel = GetNodeLevelOrDefault(viewState, node.Metadata.Id, LodLevel.Gist);
        var isExpanded = currentLevel == LodLevel.Full;

        // 渲染节点标题 - 使用Markdown标题层级 + 缩进
        var headingLevel = new string('#', Math.Min(level + 2, 6)); // 从##开始，最多到######
        var lodMarker = currentLevel switch {
            LodLevel.Gist => "[Gist]",
            LodLevel.Summary => "[Summary]",
            LodLevel.Full => "[Full]",
            _ => "[Gist]"
        };
        sb.AppendLine($"{indent}{headingLevel} {node.Metadata.Title} [{node.Metadata.Id.Value}] {lodMarker}");

        // 如果展开，渲染内容
        if (isExpanded && node.Contents.TryGetValue(LodLevel.Full, out var fullContent)) {
            var contentLines = fullContent.Content.Split('\n');
            foreach (var line in contentLines) {
                sb.AppendLine($"{indent}   {line}");
            }
            sb.AppendLine();
        }

        // 渲染子节点
        var children = await _storage.GetChildrenAsync(node.Metadata.Id, cancellationToken);
        foreach (var childId in children) {
            var childNode = await _storage.GetCompleteNodeAsync(childId, cancellationToken);
            if (childNode != null) {
                await RenderNodeTreeAsync(sb, childNode, level + 1, viewState, cancellationToken);
            }
        }
    }

    private async Task<NodeTreeItem> BuildNodeTreeItemAsync(
        CognitiveNode node,
        int level,
        CancellationToken cancellationToken
    ) {
        var children = await _storage.GetChildrenAsync(node.Metadata.Id, cancellationToken);
        var hasChildren = children.Any();

        // 计算字符数
        var characterCount = 0;
        if (node.Contents.TryGetValue(LodLevel.Full, out var fullContent)) {
            characterCount = fullContent.Content.Length;
        }

        return new NodeTreeItem {
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
