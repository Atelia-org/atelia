using System.Collections.Concurrent;
using MemoTree.Core.Storage.Interfaces;
using MemoTree.Core.Types;
using Microsoft.Extensions.Logging;

namespace MemoTree.Services.Storage.Hierarchy;

/// <summary>
/// 极简内存版层次结构存储（MVP）
/// - 仅在进程内维护父子关系
/// - 不做持久化
/// - 为正式版 CowNodeHierarchyStorage 的占位替代
/// </summary>
public class InMemoryHierarchyStorage : INodeHierarchyStorage {
    private readonly ILogger<InMemoryHierarchyStorage> _logger;
    private readonly ConcurrentDictionary<NodeId, List<NodeId>> _children = new();
    private readonly ConcurrentDictionary<NodeId, NodeId?> _parent = new();
    private readonly object _lock = new();

    public InMemoryHierarchyStorage(ILogger<InMemoryHierarchyStorage> logger) {
        _logger = logger;
    }

    public Task<HierarchyInfo?> GetHierarchyInfoAsync(NodeId parentId, CancellationToken cancellationToken = default) {
        var list = GetChildrenUnsafe(parentId);
        var childInfos = list.Select((id, index) => ChildNodeInfo.Create(id, index)).ToList();
        return Task.FromResult<HierarchyInfo?>(HierarchyInfo.Create(parentId, childInfos));
    }

    public Task SaveHierarchyInfoAsync(HierarchyInfo hierarchyInfo, CancellationToken cancellationToken = default) {
        lock (_lock) {
            _children[hierarchyInfo.ParentId] = hierarchyInfo.GetChildIds().ToList();
            foreach (var child in hierarchyInfo.Children) {
                _parent[child.NodeId] = hierarchyInfo.ParentId;
            }
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<NodeId>> GetChildrenAsync(NodeId parentId, CancellationToken cancellationToken = default) {
        return Task.FromResult<IReadOnlyList<NodeId>>(GetChildrenUnsafe(parentId));
    }

    public Task<NodeId?> GetParentAsync(NodeId nodeId, CancellationToken cancellationToken = default) {
        return Task.FromResult(_parent.TryGetValue(nodeId, out var p) ? p : null);
    }

    public Task AddChildAsync(NodeId parentId, NodeId childId, int? order = null, CancellationToken cancellationToken = default) {
        lock (_lock) {
            var list = GetChildrenUnsafe(parentId);
            if (!list.Contains(childId)) {
                if (order.HasValue && order.Value >= 0 && order.Value <= list.Count) {
                    list.Insert(order.Value, childId);
                }
                else {
                    list.Add(childId);
                }
            }
            _children[parentId] = list;
            _parent[childId] = parentId;
        }
        return Task.CompletedTask;
    }

    public Task RemoveChildAsync(NodeId parentId, NodeId childId, CancellationToken cancellationToken = default) {
        lock (_lock) {
            var list = GetChildrenUnsafe(parentId);
            list.Remove(childId);
            _children[parentId] = list;
            _parent.TryRemove(childId, out _);
        }
        return Task.CompletedTask;
    }

    public Task MoveNodeAsync(NodeId nodeId, NodeId? newParentId, int? newOrder = null, CancellationToken cancellationToken = default) {
        lock (_lock) {
            if (_parent.TryGetValue(nodeId, out var oldParent) && oldParent != null) {
                var list = GetChildrenUnsafe(oldParent.Value);
                list.Remove(nodeId);
                _children[oldParent.Value] = list;
            }
            if (newParentId != null) {
                var newList = GetChildrenUnsafe(newParentId.Value);
                if (newOrder.HasValue && newOrder.Value >= 0 && newOrder.Value <= newList.Count) {
                    newList.Insert(newOrder.Value, nodeId);
                }
                else {
                    newList.Add(nodeId);
                }

                _children[newParentId.Value] = newList;
                _parent[nodeId] = newParentId;
            }
            else {
                _parent[nodeId] = null;
            }
        }
        return Task.CompletedTask;
    }

    public Task ReorderChildrenAsync(NodeId parentId, IReadOnlyList<NodeId> newOrder, CancellationToken cancellationToken = default) {
        lock (_lock) {
            _children[parentId] = newOrder.ToList();
            foreach (var child in newOrder) {
                _parent[child] = parentId;
            }
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<NodeId>> GetPathAsync(NodeId nodeId, CancellationToken cancellationToken = default) {
        var path = new List<NodeId>();
        var current = nodeId;
        while (true) {
            path.Add(current);
            var p = _parent.TryGetValue(current, out var parent) ? parent : null;
            if (p == null) { break; }
            current = p.Value;
        }
        path.Reverse();
        return Task.FromResult<IReadOnlyList<NodeId>>(path);
    }

    public Task<int> GetNodeDepthAsync(NodeId nodeId, CancellationToken cancellationToken = default) {
        var depth = 0;
        var current = nodeId;
        while (true) {
            var p = _parent.TryGetValue(current, out var parent) ? parent : null;
            if (p == null) { break; }
            depth++;
            current = p.Value;
        }
        return Task.FromResult(depth);
    }

    public Task<bool> IsAncestorAsync(NodeId ancestorId, NodeId descendantId, CancellationToken cancellationToken = default) {
        var current = descendantId;
        while (true) {
            var p = _parent.TryGetValue(current, out var parent) ? parent : null;
            if (p == null) { return Task.FromResult(false); }
            if (p.Value == ancestorId) { return Task.FromResult(true); }
            current = p.Value;
        }
    }

    public Task<bool> HasCycleAsync(NodeId nodeId, NodeId potentialParentId, CancellationToken cancellationToken = default) {
        // 简单检测：potentialParentId 是否在 nodeId 的子树中
        return IsAncestorAsync(nodeId, potentialParentId, cancellationToken);
    }

    public async IAsyncEnumerable<NodeId> GetDescendantsAsync(NodeId rootId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) {
        var queue = new Queue<NodeId>();
        queue.Enqueue(rootId);
        while (queue.Count > 0) {
            var current = queue.Dequeue();
            var children = GetChildrenUnsafe(current);
            foreach (var c in children) {
                yield return c;
                queue.Enqueue(c);
            }
            await Task.Yield();
        }
    }

    public Task<IReadOnlyDictionary<NodeId, NodeId>> BuildParentIndexAsync(CancellationToken cancellationToken = default) {
        var copy = _parent.Where(kv => kv.Value != null).ToDictionary(kv => kv.Key, kv => kv.Value!.Value);
        return Task.FromResult<IReadOnlyDictionary<NodeId, NodeId>>(copy);
    }

    public Task<bool> HasChildrenAsync(NodeId nodeId, CancellationToken cancellationToken = default) {
        return Task.FromResult(GetChildrenUnsafe(nodeId).Count > 0);
    }

    public Task<int> GetDepthAsync(NodeId nodeId, CancellationToken cancellationToken = default) {
        return GetNodeDepthAsync(nodeId, cancellationToken);
    }

    public Task<IReadOnlyList<NodeId>> GetTopLevelNodesAsync(CancellationToken cancellationToken = default) {
        var topLevelNodes = _parent.Where(kv => kv.Value == null).Select(kv => kv.Key).Distinct().ToList();
        return Task.FromResult<IReadOnlyList<NodeId>>(topLevelNodes);
    }

    public Task<bool> WouldCreateCycleAsync(NodeId nodeId, NodeId potentialParentId, CancellationToken cancellationToken = default) {
        return HasCycleAsync(nodeId, potentialParentId, cancellationToken);
    }

    public Task EnsureNodeExistsInHierarchyAsync(NodeId nodeId, CancellationToken cancellationToken = default) {
        // 在内存存储中，确保节点存在于父子关系映射中
        if (!_parent.ContainsKey(nodeId)) {
            _parent[nodeId] = null; // 顶层节点，父节点为null
        }
        if (!_children.ContainsKey(nodeId)) {
            _children[nodeId] = new List<NodeId>();
        }
        return Task.CompletedTask;
    }

    public Task DeleteHierarchyInfoAsync(NodeId parentId, CancellationToken cancellationToken = default) {
        lock (_lock) {
            if (_children.TryRemove(parentId, out var removedChildren)) {
                foreach (var child in removedChildren) {
                    _parent.TryRemove(child, out _);
                }
            }
        }
        return Task.CompletedTask;
    }

    private List<NodeId> GetChildrenUnsafe(NodeId parent) {
        if (!_children.TryGetValue(parent, out var list)) {
            list = new List<NodeId>();
            _children[parent] = list;
        }
        return list;
    }
}

