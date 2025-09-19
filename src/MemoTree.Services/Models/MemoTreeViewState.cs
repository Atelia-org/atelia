using MemoTree.Core.Types;

namespace MemoTree.Services.Models;

/// <summary>
/// MemoTree视图状态 (MVP实现版本)
/// 管理节点的展开/折叠状态和统计信息
/// </summary>
public record MemoTreeViewState {
    /// <summary>
    /// 节点的LOD状态映射
    /// Key: NodeId, Value: 当前LOD级别 (Gist=折叠, Full=展开)
    /// </summary>
    public Dictionary<NodeId, LodLevel> NodeStates { get; init; } = new();

    /// <summary>
    /// 当前焦点节点ID (可选)
    /// </summary>
    public NodeId? FocusNodeId {
        get; init;
    }

    /// <summary>
    /// 视图名称
    /// </summary>
    public string ViewName { get; init; } = "default";

    /// <summary>
    /// 最后访问时间
    /// </summary>
    public DateTime LastAccessTime { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// 获取节点的LOD级别，如果未设置则返回默认值(Gist)
    /// </summary>
    public LodLevel GetNodeLodLevel(NodeId nodeId) {
        return NodeStates.TryGetValue(nodeId, out var level) ? level : LodLevel.Gist;
    }

    /// <summary>
    /// 设置节点的LOD级别
    /// </summary>
    public MemoTreeViewState WithNodeLodLevel(NodeId nodeId, LodLevel level) {
        var newStates = new Dictionary<NodeId, LodLevel>(NodeStates) {
            [nodeId] = level
        };

        return this with {
            NodeStates = newStates,
            LastAccessTime = DateTime.UtcNow
        };
    }

    /// <summary>
    /// 移除节点的LOD状态 (当节点变为不可见时)
    /// </summary>
    public MemoTreeViewState WithoutNode(NodeId nodeId) {
        var newStates = new Dictionary<NodeId, LodLevel>(NodeStates);
        newStates.Remove(nodeId);

        return this with {
            NodeStates = newStates,
            LastAccessTime = DateTime.UtcNow
        };
    }

    /// <summary>
    /// 设置焦点节点
    /// </summary>
    public MemoTreeViewState WithFocus(NodeId? focusNodeId) {
        return this with {
            FocusNodeId = focusNodeId,
            LastAccessTime = DateTime.UtcNow
        };
    }

    /// <summary>
    /// 获取展开的节点数量
    /// </summary>
    public int GetExpandedNodeCount() {
        return NodeStates.Count(kvp => kvp.Value == LodLevel.Full);
    }

    /// <summary>
    /// 获取折叠的节点数量
    /// </summary>
    public int GetCollapsedNodeCount() {
        return NodeStates.Count(kvp => kvp.Value == LodLevel.Gist);
    }

    /// <summary>
    /// 创建默认视图状态
    /// </summary>
    public static MemoTreeViewState CreateDefault(string viewName = "default") {
        return new MemoTreeViewState {
            ViewName = viewName,
            LastAccessTime = DateTime.UtcNow
        };
    }
}
