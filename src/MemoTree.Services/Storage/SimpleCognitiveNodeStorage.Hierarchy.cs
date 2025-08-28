using MemoTree.Core.Types;

namespace MemoTree.Services.Storage;

// Partial: Hierarchy delegation region from SimpleCognitiveNodeStorage
public partial class SimpleCognitiveNodeStorage {
    #region INodeHierarchyStorage Implementation

    public Task<HierarchyInfo?> GetHierarchyInfoAsync(NodeId parentId, CancellationToken cancellationToken = default) {
        // 委托给注入的层次结构存储
        return _hierarchy.GetHierarchyInfoAsync(parentId, cancellationToken);
    }

    public Task SaveHierarchyInfoAsync(HierarchyInfo hierarchyInfo, CancellationToken cancellationToken = default) {
        // 委托给注入的层次结构存储
        return _hierarchy.SaveHierarchyInfoAsync(hierarchyInfo, cancellationToken);
    }

    public Task<IReadOnlyList<NodeId>> GetChildrenAsync(NodeId parentId, CancellationToken cancellationToken = default) {
        // 委托给注入的层次结构存储
        return _hierarchy.GetChildrenAsync(parentId, cancellationToken);
    }

    public Task<NodeId?> GetParentAsync(NodeId nodeId, CancellationToken cancellationToken = default) {
        // 委托给注入的层次结构存储
        return _hierarchy.GetParentAsync(nodeId, cancellationToken);
    }

    public Task AddChildAsync(NodeId parentId, NodeId childId, int? order = null, CancellationToken cancellationToken = default) {
        // 委托给注入的层次结构存储
        return _hierarchy.AddChildAsync(parentId, childId, order, cancellationToken);
    }

    public Task RemoveChildAsync(NodeId parentId, NodeId childId, CancellationToken cancellationToken = default) {
        // 委托给持久化的层级存储实现
        return _hierarchy.RemoveChildAsync(parentId, childId, cancellationToken);
    }

    public Task MoveNodeAsync(NodeId nodeId, NodeId? newParentId, int? newOrder = null, CancellationToken cancellationToken = default) {
        // 委托给持久化的层级存储实现
        return _hierarchy.MoveNodeAsync(nodeId, newParentId, newOrder, cancellationToken);
    }

    public Task<IReadOnlyList<NodeId>> GetNodePathAsync(NodeId nodeId, CancellationToken cancellationToken = default) {
        // 非接口方法，仅为兼容：委托到 GetPathAsync
        return _hierarchy.GetPathAsync(nodeId, cancellationToken);
    }

    public Task<int> GetNodeDepthAsync(NodeId nodeId, CancellationToken cancellationToken = default) {
        // 非接口方法，仅为兼容：委托到底层
        return _hierarchy.GetDepthAsync(nodeId, cancellationToken);
    }

    public Task<bool> IsAncestorAsync(NodeId ancestorId, NodeId descendantId, CancellationToken cancellationToken = default) {
        // 简化实现：沿父链检查；委托组合
        return _hierarchy.WouldCreateCycleAsync(descendantId, ancestorId, cancellationToken);
    }

    public Task<bool> HasCycleAsync(NodeId nodeId, NodeId potentialParentId, CancellationToken cancellationToken = default) {
        return _hierarchy.WouldCreateCycleAsync(potentialParentId, nodeId, cancellationToken);
    }

    public Task ReorderChildrenAsync(NodeId parentId, IReadOnlyList<NodeId> newOrder, CancellationToken cancellationToken = default) {
        return _hierarchy.ReorderChildrenAsync(parentId, newOrder, cancellationToken);
    }

    public Task<IReadOnlyList<NodeId>> GetPathAsync(NodeId nodeId, CancellationToken cancellationToken = default) {
        return _hierarchy.GetPathAsync(nodeId, cancellationToken);
    }

    public IAsyncEnumerable<NodeId> GetDescendantsAsync(NodeId nodeId, CancellationToken cancellationToken = default)
    => _hierarchy.GetDescendantsAsync(nodeId, cancellationToken);

    public Task<IReadOnlyDictionary<NodeId, NodeId>> BuildParentIndexAsync(CancellationToken cancellationToken = default) {
        return _hierarchy.BuildParentIndexAsync(cancellationToken);
    }

    public async Task<bool> HasChildrenAsync(NodeId nodeId, CancellationToken cancellationToken = default) {
        var children = await GetChildrenAsync(nodeId, cancellationToken);
        return children.Any();
    }

    public Task<int> GetDepthAsync(NodeId nodeId, CancellationToken cancellationToken = default) {
        return _hierarchy.GetDepthAsync(nodeId, cancellationToken);
    }

    public Task<IReadOnlyList<NodeId>> GetTopLevelNodesAsync(CancellationToken cancellationToken = default)
    => _hierarchy.GetTopLevelNodesAsync(cancellationToken);

    public Task<bool> WouldCreateCycleAsync(NodeId nodeId, NodeId potentialParentId, CancellationToken cancellationToken = default) {
        return _hierarchy.WouldCreateCycleAsync(nodeId, potentialParentId, cancellationToken);
    }

    public Task EnsureNodeExistsInHierarchyAsync(NodeId nodeId, CancellationToken cancellationToken = default) {
        // 委托给层次关系存储
        return _hierarchy.EnsureNodeExistsInHierarchyAsync(nodeId, cancellationToken);
    }

    public Task DeleteHierarchyInfoAsync(NodeId parentId, CancellationToken cancellationToken = default) {
        return _hierarchy.DeleteHierarchyInfoAsync(parentId, cancellationToken);
    }

    #endregion
}
