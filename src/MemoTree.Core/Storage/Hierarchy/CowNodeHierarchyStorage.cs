using MemoTree.Core.Storage.Interfaces;
using MemoTree.Core.Storage.Versioned;
using MemoTree.Core.Types;
using Microsoft.Extensions.Logging;
using System.Threading; // added for SemaphoreSlim
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace MemoTree.Core.Storage.Hierarchy {
    /// <summary>
    /// Copy-on-Write父子关系存储实现
    /// 基于通用版本化存储组件
    /// </summary>
    public class CowNodeHierarchyStorage : INodeHierarchyStorage {
        private readonly IVersionedStorage<NodeId, HierarchyInfo> _versionedStorage;
        private readonly ILogger<CowNodeHierarchyStorage> _logger;

        // Runtime reverse index cache: child -> parent, version-aware
        private IReadOnlyDictionary<NodeId, NodeId>? _parentIndexCache;
        private long _parentIndexVersion = -1;
        private readonly SemaphoreSlim _parentIndexLock = new(1, 1);

        // Metrics instruments (static, shared across instances)
        private static readonly Meter s_meter = new("MemoTree.Core.Storage.Hierarchy", "1.0.0");
        private static readonly Counter<long> s_parentIndexRebuilds = s_meter.CreateCounter<long>(
            name: "cow_hierarchy_parent_index_rebuilds",
            unit: "count",
            description: "Number of times the parent index runtime cache was rebuilt."
        );
        private static readonly Counter<long> s_parentIndexInvalidations = s_meter.CreateCounter<long>(
            name: "cow_hierarchy_parent_index_invalidations",
            unit: "count",
            description: "Number of times the parent index runtime cache was invalidated."
        );
        private static readonly Histogram<double> s_parentIndexRebuildDurationMs = s_meter.CreateHistogram<double>(
            name: "cow_hierarchy_parent_index_rebuild_duration_ms",
            unit: "ms",
            description: "Elapsed time to rebuild the parent index runtime cache."
        );
        private static readonly Counter<long> s_parentIndexHits = s_meter.CreateCounter<long>(
            name: "cow_hierarchy_parent_index_hits",
            unit: "count",
            description: "Cache hits for parent index lookups (GetParentAsync)."
        );
        private static readonly Counter<long> s_parentIndexMisses = s_meter.CreateCounter<long>(
            name: "cow_hierarchy_parent_index_misses",
            unit: "count",
            description: "Cache misses for parent index lookups (GetParentAsync)."
        );

        // Lightweight local counters for logging/inspection
        private long _parentIndexRebuildCount;
        private double _lastParentIndexRebuildMs;

        public CowNodeHierarchyStorage(
            IVersionedStorage<NodeId, HierarchyInfo> versionedStorage,
            ILogger<CowNodeHierarchyStorage> logger
        ) {
            _versionedStorage = versionedStorage ?? throw new ArgumentNullException(nameof(versionedStorage));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// 获取父子关系信息
        /// </summary>
        public async Task<HierarchyInfo?> GetHierarchyInfoAsync(
            NodeId parentId,
            CancellationToken cancellationToken = default
        ) {
            return await _versionedStorage.GetAsync(parentId, cancellationToken);
        }

        /// <summary>
        /// 保存父子关系信息
        /// </summary>
        public async Task SaveHierarchyInfoAsync(
            HierarchyInfo hierarchyInfo,
            CancellationToken cancellationToken = default
        ) {
            var updates = new Dictionary<NodeId, HierarchyInfo> {
                [hierarchyInfo.ParentId] = hierarchyInfo
            };

            var description = $"Save parent-children info for {hierarchyInfo.ParentId}";
            await _versionedStorage.UpdateManyAsync(updates, description, cancellationToken);

            _logger.LogDebug("Saved parent-children info for {ParentId} with {ChildCount} children",
                hierarchyInfo.ParentId, hierarchyInfo.ChildCount
            );
            InvalidateParentIndexCache();
        }

        /// <summary>
        /// 获取子节点ID列表（有序）
        /// </summary>
        public async Task<IReadOnlyList<NodeId>> GetChildrenAsync(
            NodeId parentId,
            CancellationToken cancellationToken = default
        ) {
            var parentInfo = await GetHierarchyInfoAsync(parentId, cancellationToken);
            if (parentInfo == null) {
                return Array.Empty<NodeId>();
            }

            return parentInfo.Children
            .OrderBy(c => c.Order)
            .Select(c => c.NodeId)
            .ToList();
        }

        /// <summary>
        /// 获取父节点ID（通过运行时索引）
        /// </summary>
        public async Task<NodeId?> GetParentAsync(
            NodeId nodeId,
            CancellationToken cancellationToken = default
        ) {
            // Use runtime reverse index cache to avoid full scan
            await EnsureParentIndexUpToDateAsync(cancellationToken);
            if (_parentIndexCache != null && _parentIndexCache.TryGetValue(nodeId, out var parentId)) {
                s_parentIndexHits.Add(1);
                return parentId;
            }
            s_parentIndexMisses.Add(1);
            return null;
        }

        /// <summary>
        /// 检查节点是否有子节点
        /// </summary>
        public async Task<bool> HasChildrenAsync(
            NodeId parentId,
            CancellationToken cancellationToken = default
        ) {
            var parentInfo = await GetHierarchyInfoAsync(parentId, cancellationToken);
            return parentInfo?.HasChildren == true;
        }

        /// <summary>
        /// 获取子节点数量
        /// </summary>
        public async Task<int> GetChildCountAsync(
            NodeId parentId,
            CancellationToken cancellationToken = default
        ) {
            var parentInfo = await GetHierarchyInfoAsync(parentId, cancellationToken);
            return parentInfo?.ChildCount ?? 0;
        }

        /// <summary>
        /// 删除父子关系信息
        /// </summary>
        public async Task DeleteHierarchyInfoAsync(
            NodeId parentId,
            CancellationToken cancellationToken = default
        ) {
            var description = $"Delete parent-children info for {parentId}";
            await _versionedStorage.DeleteAsync(parentId, description, cancellationToken);

            _logger.LogDebug("Deleted parent-children info for {ParentId}", parentId);
            InvalidateParentIndexCache();
        }

        /// <summary>
        /// 添加子节点
        /// </summary>
        public async Task AddChildAsync(
            NodeId parentId,
            NodeId childId,
            int? order = null,
            CancellationToken cancellationToken = default
        ) {
            // guard: prevent cycles
            if (await WouldCreateCycleAsync(parentId, childId, cancellationToken)) {
                throw new InvalidOperationException($"Adding child {childId} to parent {parentId} would create a cycle");
            }

            var parentInfo = await GetHierarchyInfoAsync(parentId, cancellationToken)
            ?? HierarchyInfo.Create(parentId);

            var updatedInfo = parentInfo.AddChild(childId, order);
            await SaveHierarchyInfoAsync(updatedInfo, cancellationToken);

            _logger.LogDebug("Added child {ChildId} to parent {ParentId}", childId, parentId);
            InvalidateParentIndexCache();
        }

        /// <summary>
        /// 移除子节点
        /// </summary>
        public async Task RemoveChildAsync(
            NodeId parentId,
            NodeId childId,
            CancellationToken cancellationToken = default
        ) {
            var parentInfo = await GetHierarchyInfoAsync(parentId, cancellationToken);
            if (parentInfo != null && parentInfo.HasChild(childId)) {
                var updatedInfo = parentInfo.RemoveChild(childId);
                await SaveHierarchyInfoAsync(updatedInfo, cancellationToken);

                _logger.LogDebug("Removed child {ChildId} from parent {ParentId}", childId, parentId);
                InvalidateParentIndexCache();
            }
        }

        /// <summary>
        /// 移动节点到新父节点
        /// </summary>
        public async Task MoveNodeAsync(
            NodeId nodeId,
            NodeId? newParentId,
            int? newOrder = null,
            CancellationToken cancellationToken = default
        ) {
            // 找到当前父节点
            var currentParentId = await GetParentAsync(nodeId, cancellationToken);

            // guard: prevent cycles if moving under a descendant
            if (newParentId.HasValue && await WouldCreateCycleAsync(newParentId.Value, nodeId, cancellationToken)) {
                throw new InvalidOperationException($"Moving node {nodeId} under {newParentId} would create a cycle");
            }

            await MoveNodeAtomicAsync(nodeId, currentParentId, newParentId, newOrder, cancellationToken);
        }

        /// <summary>
        /// 移动节点（原子操作）- 内部方法
        /// </summary>
        private async Task<long> MoveNodeAtomicAsync(
            NodeId nodeId,
            NodeId? oldParentId,
            NodeId? newParentId,
            int? newOrder = null,
            CancellationToken cancellationToken = default
        ) {
            var updates = new Dictionary<NodeId, HierarchyInfo>();

            // 从旧父节点移除
            if (oldParentId.HasValue) {
                var oldParentInfo = await GetHierarchyInfoAsync(oldParentId.Value, cancellationToken);
                if (oldParentInfo != null && oldParentInfo.HasChild(nodeId)) {
                    updates[oldParentId.Value] = oldParentInfo.RemoveChild(nodeId);
                }
            }

            // 添加到新父节点
            if (newParentId.HasValue) {
                var newParentInfo = await GetHierarchyInfoAsync(newParentId.Value, cancellationToken)
                ?? HierarchyInfo.Create(newParentId.Value);
                updates[newParentId.Value] = newParentInfo.AddChild(nodeId, newOrder);
            }

            if (updates.Any()) {
                var description = $"Move node {nodeId} from {oldParentId} to {newParentId}";
                var version = await _versionedStorage.UpdateManyAsync(updates, description, cancellationToken);

                _logger.LogInformation("Moved node {NodeId} from {OldParent} to {NewParent}, version {Version}",
                    nodeId, oldParentId, newParentId, version
                );
                InvalidateParentIndexCache();

                return version;
            }

            return await _versionedStorage.GetCurrentVersionAsync(cancellationToken);
        }

        /// <summary>
        /// 批量更新父子关系（原子操作）
        /// </summary>
        public async Task<long> UpdateHierarchyAtomicAsync(
            IEnumerable<HierarchyInfo> updates,
            string comment = "",
            CancellationToken cancellationToken = default
        ) {
            var updateDict = updates.ToDictionary(info => info.ParentId, info => info);

            if (updateDict.Any()) {
                var description = string.IsNullOrEmpty(comment)
                ? $"Batch update {updateDict.Count} parent-children relationships"
                : comment;

                var version = await _versionedStorage.UpdateManyAsync(updateDict, description, cancellationToken);

                _logger.LogInformation("Updated {Count} parent-children relationships atomically, version {Version}",
                    updateDict.Count, version
                );
                InvalidateParentIndexCache();

                return version;
            }

            return await _versionedStorage.GetCurrentVersionAsync(cancellationToken);
        }

        /// <summary>
        /// 获取所有父节点ID
        /// </summary>
        public async Task<IReadOnlyList<NodeId>> GetAllParentIdsAsync(CancellationToken cancellationToken = default) {
            return await _versionedStorage.GetAllKeysAsync(cancellationToken);
        }

        /// <summary>
        /// 检查父子关系是否存在
        /// </summary>
        public async Task<bool> ExistsAsync(NodeId parentId, CancellationToken cancellationToken = default) {
            return await _versionedStorage.ContainsKeyAsync(parentId, cancellationToken);
        }

        /// <summary>
        /// 重新排序子节点
        /// </summary>
        public async Task ReorderChildrenAsync(
            NodeId parentId,
            IReadOnlyList<NodeId> orderedChildIds,
            CancellationToken cancellationToken = default
        ) {
            var parentInfo = await GetHierarchyInfoAsync(parentId, cancellationToken);
            if (parentInfo != null) {
                var reorderedInfo = parentInfo.ReorderChildren(orderedChildIds);
                await SaveHierarchyInfoAsync(reorderedInfo, cancellationToken);

                _logger.LogDebug("Reordered children for parent {ParentId}", parentId);
                InvalidateParentIndexCache();
            }
        }

        /// <summary>
        /// 获取节点路径（从根到节点）
        /// </summary>
        public async Task<IReadOnlyList<NodeId>> GetPathAsync(
            NodeId nodeId,
            CancellationToken cancellationToken = default
        ) {
            var path = new List<NodeId>();
            var currentId = (NodeId?)nodeId;

            while (currentId.HasValue) {
                path.Insert(0, currentId.Value);
                currentId = await GetParentAsync(currentId.Value, cancellationToken);
            }

            return path;
        }

        /// <summary>
        /// 获取子树中的所有节点ID
        /// </summary>
        public async IAsyncEnumerable<NodeId> GetDescendantsAsync(
            NodeId rootId,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default
        ) {
            var queue = new Queue<NodeId>();
            queue.Enqueue(rootId);

            while (queue.Count > 0) {
                var currentId = queue.Dequeue();
                yield return currentId;

                var children = await GetChildrenAsync(currentId, cancellationToken);
                foreach (var child in children) {
                    queue.Enqueue(child);
                }
            }
        }

        /// <summary>
        /// 构建运行时反向索引（子节点到父节点的映射）
        /// </summary>
        public async Task<IReadOnlyDictionary<NodeId, NodeId>> BuildParentIndexAsync(
            CancellationToken cancellationToken = default
        ) {
            var parentIndex = new Dictionary<NodeId, NodeId>();
            var allParentIds = await GetAllParentIdsAsync(cancellationToken);

            foreach (var parentId in allParentIds) {
                var children = await GetChildrenAsync(parentId, cancellationToken);
                foreach (var childId in children) {
                    parentIndex[childId] = parentId;
                }
            }

            return parentIndex;
        }

        /// <summary>
        /// 获取节点的层级深度
        /// </summary>
        public async Task<int> GetDepthAsync(NodeId nodeId, CancellationToken cancellationToken = default) {
            var depth = 0;
            var currentId = (NodeId?)nodeId;

            while (currentId.HasValue) {
                var parentId = await GetParentAsync(currentId.Value, cancellationToken);
                if (parentId == null) {
                    break;
                }

                depth++;
                currentId = parentId;
            }

            return depth;
        }

        /// <summary>
        /// 获取所有顶层节点（无父节点的节点）
        /// </summary>
        public async Task<IReadOnlyList<NodeId>> GetTopLevelNodesAsync(CancellationToken cancellationToken = default) {
            await EnsureParentIndexUpToDateAsync(cancellationToken);
            var parentIndex = _parentIndexCache ?? new Dictionary<NodeId, NodeId>();
            var allParentIds = await GetAllParentIdsAsync(cancellationToken);

            _logger.LogDebug("GetTopLevelNodesAsync: Found {AllCount} total nodes, {ParentIndexCount} child nodes",
                allParentIds.Count, parentIndex.Count
            );

            // 找出所有不在parentIndex中的节点，这些就是顶层节点
            var topLevelNodes = new List<NodeId>();

            foreach (var parentId in allParentIds) {
                if (!parentIndex.ContainsKey(parentId)) {
                    topLevelNodes.Add(parentId);
                    _logger.LogDebug("Found top-level node: {NodeId}", parentId);
                } else {
                    _logger.LogDebug("Node {NodeId} is a child of {ParentId}", parentId, parentIndex[parentId]);
                }
            }

            _logger.LogDebug("GetTopLevelNodesAsync: Found {TopLevelCount} top-level nodes", topLevelNodes.Count);
            return topLevelNodes;
        }

        /// <summary>
        /// 检查是否存在循环引用
        /// </summary>
        public async Task<bool> WouldCreateCycleAsync(
            NodeId parentId,
            NodeId childId,
            CancellationToken cancellationToken = default
        ) {
            // 如果childId是parentId的祖先，则会产生循环
            var currentId = (NodeId?)parentId;

            while (currentId.HasValue) {
                if (currentId.Value == childId) {
                    return true;
                }

                currentId = await GetParentAsync(currentId.Value, cancellationToken);
            }

            return false;
        }

        /// <summary>
        /// 确保节点在层次关系存储中存在（用于顶层节点）
        /// </summary>
        public async Task EnsureNodeExistsInHierarchyAsync(NodeId nodeId, CancellationToken cancellationToken = default) {
            // 检查节点是否已经存在
            var exists = await _versionedStorage.ContainsKeyAsync(nodeId, cancellationToken);
            if (!exists) {
                // 创建一个空的HierarchyInfo
                var hierarchyInfo = HierarchyInfo.Create(nodeId);
                await SaveHierarchyInfoAsync(hierarchyInfo, cancellationToken);
                _logger.LogDebug("Created hierarchy record for top-level node {NodeId}", nodeId);
                InvalidateParentIndexCache();
            }
        }

        // --- cache helpers ---
        private async Task EnsureParentIndexUpToDateAsync(CancellationToken cancellationToken) {
            var currentVersion = await _versionedStorage.GetCurrentVersionAsync(cancellationToken);
            if (_parentIndexCache != null && _parentIndexVersion == currentVersion) {
                return;
            }

            await _parentIndexLock.WaitAsync(cancellationToken);
            try {
                currentVersion = await _versionedStorage.GetCurrentVersionAsync(cancellationToken);
                if (_parentIndexCache != null && _parentIndexVersion == currentVersion) {
                    return;
                }

                var previousVersion = _parentIndexVersion;
                var sw = Stopwatch.StartNew();
                _logger.LogDebug("Rebuilding parent index cache for version {Version} (prev {PrevVersion})", currentVersion, previousVersion);
                var fresh = await BuildParentIndexAsync(cancellationToken);
                sw.Stop();

                _parentIndexCache = fresh;
                _parentIndexVersion = currentVersion;

                _parentIndexRebuildCount++;
                _lastParentIndexRebuildMs = sw.Elapsed.TotalMilliseconds;
                s_parentIndexRebuilds.Add(1);
                s_parentIndexRebuildDurationMs.Record(_lastParentIndexRebuildMs);

                // Log at Information if rebuild took noticeable time, otherwise Debug
                if (_lastParentIndexRebuildMs >= 50) {
                    _logger.LogInformation(
                        "Parent index cache rebuilt in {ElapsedMs} ms, size {Size}, version {Version} (prev {PrevVersion}), total rebuilds {Rebuilds}",
                        _lastParentIndexRebuildMs, _parentIndexCache.Count, _parentIndexVersion, previousVersion, _parentIndexRebuildCount
                    );
                } else {
                    _logger.LogDebug(
                        "Parent index cache rebuilt in {ElapsedMs} ms, size {Size}, version {Version} (prev {PrevVersion}), total rebuilds {Rebuilds}",
                        _lastParentIndexRebuildMs, _parentIndexCache.Count, _parentIndexVersion, previousVersion, _parentIndexRebuildCount
                    );
                }
            } finally {
                _parentIndexLock.Release();
            }
        }

        private void InvalidateParentIndexCache() {
            // Only count/log when transitioning from a non-invalid state to invalid
            var prev = Interlocked.Exchange(ref _parentIndexVersion, -1);
            if (prev != -1) {
                s_parentIndexInvalidations.Add(1);
                _logger.LogTrace("Parent index cache invalidated (prev version {PrevVersion})", prev);
            }
        }
    }
}
