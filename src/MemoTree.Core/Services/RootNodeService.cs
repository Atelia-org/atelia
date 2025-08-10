using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using MemoTree.Core.Storage.Interfaces;
using MemoTree.Core.Types;

namespace MemoTree.Core.Services
{
    /// <summary>
    /// 根节点判断服务实现
    /// 基于INodeHierarchyStorage提供根节点判断，支持内存缓存优化
    /// </summary>
    public class RootNodeService : IRootNodeService
    {
        private readonly INodeHierarchyStorage _hierarchyStorage;
        private readonly IMemoryCache _cache;
        private readonly ILogger<RootNodeService> _logger;

        private const string ROOT_NODES_CACHE_KEY = "root_nodes";
        private const string ROOT_NODE_PREFIX = "is_root_";
        private static readonly TimeSpan CacheExpiration = TimeSpan.FromMinutes(5);

        public RootNodeService(
            INodeHierarchyStorage hierarchyStorage,
            IMemoryCache cache,
            ILogger<RootNodeService> logger)
        {
            _hierarchyStorage = hierarchyStorage ?? throw new ArgumentNullException(nameof(hierarchyStorage));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// 检查指定节点是否为根节点
        /// </summary>
        public async Task<bool> IsRootNodeAsync(NodeId nodeId, CancellationToken cancellationToken = default)
        {
            var cacheKey = ROOT_NODE_PREFIX + nodeId.Value;
            
            if (_cache.TryGetValue(cacheKey, out bool cachedResult))
            {
                _logger.LogDebug("Root node status for {NodeId} found in cache: {IsRoot}", nodeId, cachedResult);
                return cachedResult;
            }

            var isRoot = await _hierarchyStorage.IsRootNodeAsync(nodeId, cancellationToken);
            
            _cache.Set(cacheKey, isRoot, CacheExpiration);
            _logger.LogDebug("Root node status for {NodeId} cached: {IsRoot}", nodeId, isRoot);
            
            return isRoot;
        }

        /// <summary>
        /// 获取所有根节点
        /// </summary>
        public async Task<IReadOnlyList<NodeId>> GetRootNodesAsync(CancellationToken cancellationToken = default)
        {
            if (_cache.TryGetValue(ROOT_NODES_CACHE_KEY, out IReadOnlyList<NodeId>? cachedRoots))
            {
                _logger.LogDebug("Root nodes list found in cache: {Count} nodes", cachedRoots!.Count);
                return cachedRoots;
            }

            var rootNodes = await _hierarchyStorage.GetRootNodesAsync(cancellationToken);
            
            _cache.Set(ROOT_NODES_CACHE_KEY, rootNodes, CacheExpiration);
            _logger.LogDebug("Root nodes list cached: {Count} nodes", rootNodes.Count);
            
            return rootNodes;
        }

        /// <summary>
        /// 批量检查多个节点是否为根节点
        /// </summary>
        public async Task<IReadOnlyDictionary<NodeId, bool>> IsRootNodeBatchAsync(
            IEnumerable<NodeId> nodeIds, 
            CancellationToken cancellationToken = default)
        {
            var result = new Dictionary<NodeId, bool>();
            var uncachedNodes = new List<NodeId>();

            // 先检查缓存
            foreach (var nodeId in nodeIds)
            {
                var cacheKey = ROOT_NODE_PREFIX + nodeId.Value;
                if (_cache.TryGetValue(cacheKey, out bool cachedResult))
                {
                    result[nodeId] = cachedResult;
                }
                else
                {
                    uncachedNodes.Add(nodeId);
                }
            }

            // 批量查询未缓存的节点
            if (uncachedNodes.Count > 0)
            {
                _logger.LogDebug("Batch checking {Count} uncached nodes for root status", uncachedNodes.Count);
                
                foreach (var nodeId in uncachedNodes)
                {
                    var isRoot = await _hierarchyStorage.IsRootNodeAsync(nodeId, cancellationToken);
                    result[nodeId] = isRoot;
                    
                    // 缓存结果
                    var cacheKey = ROOT_NODE_PREFIX + nodeId.Value;
                    _cache.Set(cacheKey, isRoot, CacheExpiration);
                }
            }

            return result;
        }

        /// <summary>
        /// 清除根节点缓存
        /// </summary>
        public void InvalidateCache()
        {
            _cache.Remove(ROOT_NODES_CACHE_KEY);
            
            // 注意：这里无法清除所有单个节点的缓存，因为我们不知道所有的键
            // 在实际应用中，可以考虑使用更复杂的缓存策略，如标签缓存
            _logger.LogDebug("Root nodes cache invalidated");
        }
    }
}
