using MemoTree.Core.Storage.Interfaces;
using MemoTree.Core.Storage.Versioned;
using MemoTree.Core.Types;
using Microsoft.Extensions.Logging;

namespace MemoTree.Core.Storage.Hierarchy
{
    /// <summary>
    /// Copy-on-Write父子关系存储实现
    /// 基于通用版本化存储组件
    /// </summary>
    public class CowNodeHierarchyStorage : INodeHierarchyStorage
    {
        private readonly IVersionedStorage<NodeId, ParentChildrenInfo> _versionedStorage;
        private readonly ILogger<CowNodeHierarchyStorage> _logger;
        
        public CowNodeHierarchyStorage(
            IVersionedStorage<NodeId, ParentChildrenInfo> versionedStorage,
            ILogger<CowNodeHierarchyStorage> logger)
        {
            _versionedStorage = versionedStorage ?? throw new ArgumentNullException(nameof(versionedStorage));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }
        
        /// <summary>
        /// 获取父子关系信息
        /// </summary>
        public async Task<ParentChildrenInfo?> GetParentChildrenInfoAsync(
            NodeId parentId, 
            CancellationToken cancellationToken = default)
        {
            return await _versionedStorage.GetAsync(parentId, cancellationToken);
        }
        
        /// <summary>
        /// 保存父子关系信息
        /// </summary>
        public async Task SaveParentChildrenInfoAsync(
            ParentChildrenInfo parentChildrenInfo, 
            CancellationToken cancellationToken = default)
        {
            var updates = new Dictionary<NodeId, ParentChildrenInfo>
            {
                [parentChildrenInfo.ParentId] = parentChildrenInfo
            };
            
            var description = $"Save parent-children info for {parentChildrenInfo.ParentId}";
            await _versionedStorage.UpdateManyAsync(updates, description, cancellationToken);
            
            _logger.LogDebug("Saved parent-children info for {ParentId} with {ChildCount} children",
                parentChildrenInfo.ParentId, parentChildrenInfo.ChildCount);
        }
        
        /// <summary>
        /// 获取子节点ID列表（有序）
        /// </summary>
        public async Task<IReadOnlyList<NodeId>> GetChildrenAsync(
            NodeId parentId, 
            CancellationToken cancellationToken = default)
        {
            var parentInfo = await GetParentChildrenInfoAsync(parentId, cancellationToken);
            if (parentInfo == null)
                return Array.Empty<NodeId>();
            
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
            CancellationToken cancellationToken = default)
        {
            // 遍历所有父子关系信息，查找包含指定子节点的父节点
            var allKeys = await _versionedStorage.GetAllKeysAsync(cancellationToken);
            
            foreach (var parentId in allKeys)
            {
                var parentInfo = await _versionedStorage.GetAsync(parentId, cancellationToken);
                if (parentInfo != null && parentInfo.HasChild(nodeId))
                {
                    return parentId;
                }
            }
            
            return null;
        }
        
        /// <summary>
        /// 检查节点是否有子节点
        /// </summary>
        public async Task<bool> HasChildrenAsync(
            NodeId parentId, 
            CancellationToken cancellationToken = default)
        {
            var parentInfo = await GetParentChildrenInfoAsync(parentId, cancellationToken);
            return parentInfo?.HasChildren == true;
        }
        
        /// <summary>
        /// 获取子节点数量
        /// </summary>
        public async Task<int> GetChildCountAsync(
            NodeId parentId, 
            CancellationToken cancellationToken = default)
        {
            var parentInfo = await GetParentChildrenInfoAsync(parentId, cancellationToken);
            return parentInfo?.ChildCount ?? 0;
        }
        
        /// <summary>
        /// 删除父子关系信息
        /// </summary>
        public async Task DeleteParentChildrenInfoAsync(
            NodeId parentId, 
            CancellationToken cancellationToken = default)
        {
            var description = $"Delete parent-children info for {parentId}";
            await _versionedStorage.DeleteAsync(parentId, description, cancellationToken);
            
            _logger.LogDebug("Deleted parent-children info for {ParentId}", parentId);
        }
        
        /// <summary>
        /// 添加子节点
        /// </summary>
        public async Task AddChildAsync(
            NodeId parentId,
            NodeId childId,
            int? order = null,
            CancellationToken cancellationToken = default)
        {
            var parentInfo = await GetParentChildrenInfoAsync(parentId, cancellationToken)
                ?? ParentChildrenInfo.Create(parentId);

            var updatedInfo = parentInfo.AddChild(childId, order);
            await SaveParentChildrenInfoAsync(updatedInfo, cancellationToken);

            _logger.LogDebug("Added child {ChildId} to parent {ParentId}", childId, parentId);
        }

        /// <summary>
        /// 移除子节点
        /// </summary>
        public async Task RemoveChildAsync(
            NodeId parentId,
            NodeId childId,
            CancellationToken cancellationToken = default)
        {
            var parentInfo = await GetParentChildrenInfoAsync(parentId, cancellationToken);
            if (parentInfo != null && parentInfo.HasChild(childId))
            {
                var updatedInfo = parentInfo.RemoveChild(childId);
                await SaveParentChildrenInfoAsync(updatedInfo, cancellationToken);

                _logger.LogDebug("Removed child {ChildId} from parent {ParentId}", childId, parentId);
            }
        }

        /// <summary>
        /// 移动节点到新父节点
        /// </summary>
        public async Task MoveNodeAsync(
            NodeId nodeId,
            NodeId? newParentId,
            int? newOrder = null,
            CancellationToken cancellationToken = default)
        {
            // 找到当前父节点
            var currentParentId = await GetParentAsync(nodeId, cancellationToken);

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
            CancellationToken cancellationToken = default)
        {
            var updates = new Dictionary<NodeId, ParentChildrenInfo>();
            
            // 从旧父节点移除
            if (oldParentId.HasValue)
            {
                var oldParentInfo = await GetParentChildrenInfoAsync(oldParentId.Value, cancellationToken);
                if (oldParentInfo != null && oldParentInfo.HasChild(nodeId))
                {
                    updates[oldParentId.Value] = oldParentInfo.RemoveChild(nodeId);
                }
            }
            
            // 添加到新父节点
            if (newParentId.HasValue)
            {
                var newParentInfo = await GetParentChildrenInfoAsync(newParentId.Value, cancellationToken) 
                    ?? ParentChildrenInfo.Create(newParentId.Value);
                updates[newParentId.Value] = newParentInfo.AddChild(nodeId, newOrder);
            }
            
            if (updates.Any())
            {
                var description = $"Move node {nodeId} from {oldParentId} to {newParentId}";
                var version = await _versionedStorage.UpdateManyAsync(updates, description, cancellationToken);
                
                _logger.LogInformation("Moved node {NodeId} from {OldParent} to {NewParent}, version {Version}",
                    nodeId, oldParentId, newParentId, version);
                
                return version;
            }
            
            return await _versionedStorage.GetCurrentVersionAsync(cancellationToken);
        }
        
        /// <summary>
        /// 批量更新父子关系（原子操作）
        /// </summary>
        public async Task<long> UpdateHierarchyAtomicAsync(
            IEnumerable<ParentChildrenInfo> updates,
            string comment = "",
            CancellationToken cancellationToken = default)
        {
            var updateDict = updates.ToDictionary(info => info.ParentId, info => info);
            
            if (updateDict.Any())
            {
                var description = string.IsNullOrEmpty(comment) 
                    ? $"Batch update {updateDict.Count} parent-children relationships"
                    : comment;
                
                var version = await _versionedStorage.UpdateManyAsync(updateDict, description, cancellationToken);
                
                _logger.LogInformation("Updated {Count} parent-children relationships atomically, version {Version}",
                    updateDict.Count, version);
                
                return version;
            }
            
            return await _versionedStorage.GetCurrentVersionAsync(cancellationToken);
        }
        
        /// <summary>
        /// 获取所有父节点ID
        /// </summary>
        public async Task<IReadOnlyList<NodeId>> GetAllParentIdsAsync(CancellationToken cancellationToken = default)
        {
            return await _versionedStorage.GetAllKeysAsync(cancellationToken);
        }
        
        /// <summary>
        /// 检查父子关系是否存在
        /// </summary>
        public async Task<bool> ExistsAsync(NodeId parentId, CancellationToken cancellationToken = default)
        {
            return await _versionedStorage.ContainsKeyAsync(parentId, cancellationToken);
        }

        /// <summary>
        /// 重新排序子节点
        /// </summary>
        public async Task ReorderChildrenAsync(
            NodeId parentId,
            IReadOnlyList<NodeId> orderedChildIds,
            CancellationToken cancellationToken = default)
        {
            var parentInfo = await GetParentChildrenInfoAsync(parentId, cancellationToken);
            if (parentInfo != null)
            {
                var reorderedInfo = parentInfo.ReorderChildren(orderedChildIds);
                await SaveParentChildrenInfoAsync(reorderedInfo, cancellationToken);

                _logger.LogDebug("Reordered children for parent {ParentId}", parentId);
            }
        }

        /// <summary>
        /// 获取节点路径（从根到节点）
        /// </summary>
        public async Task<IReadOnlyList<NodeId>> GetPathAsync(
            NodeId nodeId,
            CancellationToken cancellationToken = default)
        {
            var path = new List<NodeId>();
            var currentId = (NodeId?)nodeId;

            while (currentId.HasValue)
            {
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
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var queue = new Queue<NodeId>();
            queue.Enqueue(rootId);

            while (queue.Count > 0)
            {
                var currentId = queue.Dequeue();
                yield return currentId;

                var children = await GetChildrenAsync(currentId, cancellationToken);
                foreach (var child in children)
                {
                    queue.Enqueue(child);
                }
            }
        }

        /// <summary>
        /// 构建运行时反向索引（子节点到父节点的映射）
        /// </summary>
        public async Task<IReadOnlyDictionary<NodeId, NodeId>> BuildParentIndexAsync(
            CancellationToken cancellationToken = default)
        {
            var parentIndex = new Dictionary<NodeId, NodeId>();
            var allParentIds = await GetAllParentIdsAsync(cancellationToken);

            foreach (var parentId in allParentIds)
            {
                var children = await GetChildrenAsync(parentId, cancellationToken);
                foreach (var childId in children)
                {
                    parentIndex[childId] = parentId;
                }
            }

            return parentIndex;
        }

        /// <summary>
        /// 获取节点的层级深度
        /// </summary>
        public async Task<int> GetDepthAsync(NodeId nodeId, CancellationToken cancellationToken = default)
        {
            var depth = 0;
            var currentId = (NodeId?)nodeId;

            while (currentId.HasValue)
            {
                var parentId = await GetParentAsync(currentId.Value, cancellationToken);
                if (parentId == null)
                    break;

                depth++;
                currentId = parentId;
            }

            return depth;
        }

        /// <summary>
        /// 获取所有根节点
        /// </summary>
        public async Task<IReadOnlyList<NodeId>> GetRootNodesAsync(CancellationToken cancellationToken = default)
        {
            var parentIndex = await BuildParentIndexAsync(cancellationToken);
            var allParentIds = await GetAllParentIdsAsync(cancellationToken);

            // 找出所有不在parentIndex中的节点，这些就是根节点
            var rootNodes = new List<NodeId>();

            foreach (var parentId in allParentIds)
            {
                if (!parentIndex.ContainsKey(parentId))
                {
                    rootNodes.Add(parentId);
                }
            }

            return rootNodes;
        }

        /// <summary>
        /// 检查是否存在循环引用
        /// </summary>
        public async Task<bool> WouldCreateCycleAsync(
            NodeId parentId,
            NodeId childId,
            CancellationToken cancellationToken = default)
        {
            // 如果childId是parentId的祖先，则会产生循环
            var currentId = (NodeId?)parentId;

            while (currentId.HasValue)
            {
                if (currentId.Value == childId)
                    return true;

                currentId = await GetParentAsync(currentId.Value, cancellationToken);
            }

            return false;
        }
    }
}
