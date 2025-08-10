using MemoTree.Core.Storage.Interfaces;
using MemoTree.Core.Types;
using MemoTree.Core.Configuration;
using MemoTree.Core.Json;
using MemoTree.Services.Yaml;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace MemoTree.Services.Storage;

/// <summary>
/// 简化的认知节点存储实现 (MVP版本)
/// 基于文件系统的直接存储，不使用版本化存储
/// </summary>
public class SimpleCognitiveNodeStorage : ICognitiveNodeStorage
{
    private readonly MemoTreeOptions _options;
    private readonly StorageOptions _storageOptions;
    private readonly ILogger<SimpleCognitiveNodeStorage> _logger;
    private readonly ISerializer _yamlSerializer;
    private readonly IDeserializer _yamlDeserializer;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly INodeHierarchyStorage _hierarchy;

    public SimpleCognitiveNodeStorage(
        IOptions<MemoTreeOptions> options,
        IOptions<StorageOptions> storageOptions,
        INodeHierarchyStorage hierarchy,
        ILogger<SimpleCognitiveNodeStorage> logger)
    {
        _options = options.Value;
        _storageOptions = storageOptions.Value;
        _hierarchy = hierarchy;
        _logger = logger;

        _yamlSerializer = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .WithTypeConverter(new NodeIdYamlConverter())
            .Build();

        _yamlDeserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .WithTypeConverter(new NodeIdYamlConverter())
            .Build();

        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new NodeIdJsonConverter() }
        };
    }

    #region INodeMetadataStorage Implementation

    public async Task<NodeMetadata?> GetAsync(NodeId nodeId, CancellationToken cancellationToken = default)
    {
        var metadataPath = GetNodeMetadataPath(nodeId);
        if (!File.Exists(metadataPath))
            return null;

        try
        {
            var yamlContent = await File.ReadAllTextAsync(metadataPath, cancellationToken);
            return _yamlDeserializer.Deserialize<NodeMetadata>(yamlContent);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load metadata for node {NodeId}", nodeId);
            return null;
        }
    }

    public async Task SaveAsync(NodeMetadata metadata, CancellationToken cancellationToken = default)
    {
        var metadataPath = GetNodeMetadataPath(metadata.Id);
        var directory = Path.GetDirectoryName(metadataPath)!;
        
        Directory.CreateDirectory(directory);

        try
        {
            var yamlContent = _yamlSerializer.Serialize(metadata);
            await File.WriteAllTextAsync(metadataPath, yamlContent, cancellationToken);
            _logger.LogDebug("Saved metadata for node {NodeId}", metadata.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save metadata for node {NodeId}", metadata.Id);
            throw;
        }
    }

    public async Task DeleteAsync(NodeId nodeId, CancellationToken cancellationToken = default)
    {
        var metadataPath = GetNodeMetadataPath(nodeId);
        if (File.Exists(metadataPath))
        {
            File.Delete(metadataPath);
            _logger.LogDebug("Deleted metadata for node {NodeId}", nodeId);
        }
    }

    public async Task<IReadOnlyDictionary<NodeId, NodeMetadata>> GetBatchAsync(
        IEnumerable<NodeId> nodeIds, 
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<NodeId, NodeMetadata>();
        
        foreach (var nodeId in nodeIds)
        {
            var metadata = await GetAsync(nodeId, cancellationToken);
            if (metadata != null)
            {
                result[nodeId] = metadata;
            }
        }
        
        return result;
    }

    public async Task SaveBatchAsync(
        IEnumerable<NodeMetadata> metadataList, 
        CancellationToken cancellationToken = default)
    {
        foreach (var metadata in metadataList)
        {
            await SaveAsync(metadata, cancellationToken);
        }
    }

    public async Task<bool> ExistsAsync(NodeId nodeId, CancellationToken cancellationToken = default)
    {
        var metadataPath = GetNodeMetadataPath(nodeId);
        return File.Exists(metadataPath);
    }

    public async Task<IReadOnlyList<NodeId>> GetAllNodeIdsAsync(CancellationToken cancellationToken = default)
    {
        var cogNodesPath = Path.Combine(_options.WorkspaceRoot, _options.CogNodesDirectory);
        if (!Directory.Exists(cogNodesPath))
            return Array.Empty<NodeId>();

        var nodeIds = new List<NodeId>();
        var directories = Directory.GetDirectories(cogNodesPath);

        foreach (var dir in directories)
        {
            var dirName = Path.GetFileName(dir);
            try
            {
                var nodeId = new NodeId(dirName);
                var metadataPath = Path.Combine(dir, _storageOptions.MetadataFileName);
                if (File.Exists(metadataPath))
                {
                    nodeIds.Add(nodeId);
                }
            }
            catch
            {
                // 忽略无效的目录名
            }
        }

        return nodeIds;
    }

    public async IAsyncEnumerable<NodeMetadata> GetAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var nodeIds = await GetAllNodeIdsAsync(cancellationToken);

        foreach (var nodeId in nodeIds)
        {
            var metadata = await GetAsync(nodeId, cancellationToken);
            if (metadata != null)
            {
                yield return metadata;
            }
        }
    }

    public async Task<int> GetCountAsync(CancellationToken cancellationToken = default)
    {
        var nodeIds = await GetAllNodeIdsAsync(cancellationToken);
        return nodeIds.Count;
    }

    public async IAsyncEnumerable<NodeMetadata> FindByTypeAsync(NodeType type, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var metadata in GetAllAsync(cancellationToken))
        {
            if (metadata.Type == type)
            {
                yield return metadata;
            }
        }
    }

    public async IAsyncEnumerable<NodeMetadata> FindByTagAsync(string tag, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // MVP版本：暂时不支持标签搜索，返回空序列
        yield break;
    }

    #endregion

    #region INodeContentStorage Implementation

    public async Task<NodeContent?> GetAsync(NodeId nodeId, LodLevel level, CancellationToken cancellationToken = default)
    {
        var contentPath = GetNodeContentPath(nodeId, level);
        if (!File.Exists(contentPath))
            return null;

        try
        {
            var content = await File.ReadAllTextAsync(contentPath, cancellationToken);
            return new NodeContent
            {
                Id = nodeId,
                Level = level,
                Content = content,
                LastModified = File.GetLastWriteTimeUtc(contentPath)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load content for node {NodeId} at level {Level}", nodeId, level);
            return null;
        }
    }

    public async Task SaveAsync(NodeContent content, CancellationToken cancellationToken = default)
    {
        var contentPath = GetNodeContentPath(content.Id, content.Level);
        var directory = Path.GetDirectoryName(contentPath)!;
        
        Directory.CreateDirectory(directory);

        try
        {
            await File.WriteAllTextAsync(contentPath, content.Content, cancellationToken);
            _logger.LogDebug("Saved content for node {NodeId} at level {Level}", content.Id, content.Level);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save content for node {NodeId} at level {Level}", content.Id, content.Level);
            throw;
        }
    }

    public Task DeleteAsync(NodeId nodeId, LodLevel level, CancellationToken cancellationToken = default)
    {
        var contentPath = GetNodeContentPath(nodeId, level);
        if (File.Exists(contentPath))
        {
            File.Delete(contentPath);
            _logger.LogDebug("Deleted content for node {NodeId} at level {Level}", nodeId, level);
        }
        return Task.CompletedTask;
    }

    public async Task DeleteAllAsync(NodeId nodeId, CancellationToken cancellationToken = default)
    {
        var nodeDirectory = GetNodeDirectory(nodeId);
        if (Directory.Exists(nodeDirectory))
        {
            // 删除所有内容文件，保留元数据
            foreach (LodLevel level in Enum.GetValues<LodLevel>())
            {
                await DeleteAsync(nodeId, level, cancellationToken);
            }
        }
    }

    public async Task<IReadOnlyDictionary<LodLevel, NodeContent>> GetAllLevelsAsync(
        NodeId nodeId, 
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<LodLevel, NodeContent>();
        
        foreach (LodLevel level in Enum.GetValues<LodLevel>())
        {
            var content = await GetAsync(nodeId, level, cancellationToken);
            if (content != null)
            {
                result[level] = content;
            }
        }
        
        return result;
    }

    public async Task<IReadOnlyDictionary<(NodeId NodeId, LodLevel Level), NodeContent>> GetBatchAsync(
        IEnumerable<(NodeId NodeId, LodLevel Level)> requests, 
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<(NodeId NodeId, LodLevel Level), NodeContent>();
        
        foreach (var (nodeId, level) in requests)
        {
            var content = await GetAsync(nodeId, level, cancellationToken);
            if (content != null)
            {
                result[(nodeId, level)] = content;
            }
        }
        
        return result;
    }

    public Task<IReadOnlyDictionary<LodLevel, long>> GetContentSizeStatsAsync(
        NodeId nodeId,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<LodLevel, long>();

        foreach (LodLevel level in Enum.GetValues<LodLevel>())
        {
            var contentPath = GetNodeContentPath(nodeId, level);
            if (File.Exists(contentPath))
            {
                var fileInfo = new FileInfo(contentPath);
                result[level] = fileInfo.Length;
            }
        }

        return Task.FromResult<IReadOnlyDictionary<LodLevel, long>>(result);
    }

    public Task<IReadOnlyList<LodLevel>> GetAvailableLevelsAsync(NodeId nodeId, CancellationToken cancellationToken = default)
    {
        var levels = new List<LodLevel>();

        foreach (LodLevel level in Enum.GetValues<LodLevel>())
        {
            var contentPath = GetNodeContentPath(nodeId, level);
            if (File.Exists(contentPath))
            {
                levels.Add(level);
            }
        }

        return Task.FromResult<IReadOnlyList<LodLevel>>(levels);
    }

    public Task<bool> ExistsAsync(NodeId nodeId, LodLevel level, CancellationToken cancellationToken = default)
    {
        var contentPath = GetNodeContentPath(nodeId, level);
        return Task.FromResult(File.Exists(contentPath));
    }

    #endregion

    #region INodeHierarchyStorage Implementation

    public Task<HierarchyInfo?> GetHierarchyInfoAsync(NodeId parentId, CancellationToken cancellationToken = default)
    {
        // 委托给注入的层次结构存储
        return _hierarchy.GetHierarchyInfoAsync(parentId, cancellationToken);
    }

    public Task SaveHierarchyInfoAsync(HierarchyInfo hierarchyInfo, CancellationToken cancellationToken = default)
    {
        // 委托给注入的层次结构存储
        return _hierarchy.SaveHierarchyInfoAsync(hierarchyInfo, cancellationToken);
    }

    public Task<IReadOnlyList<NodeId>> GetChildrenAsync(NodeId parentId, CancellationToken cancellationToken = default)
    {
        // 委托给注入的层次结构存储
        return _hierarchy.GetChildrenAsync(parentId, cancellationToken);
    }

    public Task<NodeId?> GetParentAsync(NodeId nodeId, CancellationToken cancellationToken = default)
    {
        // 委托给注入的层次结构存储
        return _hierarchy.GetParentAsync(nodeId, cancellationToken);
    }

    public Task AddChildAsync(NodeId parentId, NodeId childId, int? order = null, CancellationToken cancellationToken = default)
    {
        // 委托给注入的层次结构存储
        return _hierarchy.AddChildAsync(parentId, childId, order, cancellationToken);
    }

    public Task RemoveChildAsync(NodeId parentId, NodeId childId, CancellationToken cancellationToken = default)
    {
        // MVP版本：暂时不实现，后续会集成CowNodeHierarchyStorage
        _logger.LogWarning("RemoveChildAsync not implemented in MVP version");
        return Task.CompletedTask;
    }

    public Task MoveNodeAsync(NodeId nodeId, NodeId? newParentId, int? newOrder = null, CancellationToken cancellationToken = default)
    {
        // MVP版本：暂时不实现，后续会集成CowNodeHierarchyStorage
        _logger.LogWarning("MoveNodeAsync not implemented in MVP version");
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<NodeId>> GetNodePathAsync(NodeId nodeId, CancellationToken cancellationToken = default)
    {
        // MVP版本：暂时返回只包含节点自身的路径
        return Task.FromResult<IReadOnlyList<NodeId>>(new[] { nodeId });
    }

    public Task<int> GetNodeDepthAsync(NodeId nodeId, CancellationToken cancellationToken = default)
    {
        // MVP版本：暂时返回深度0
        return Task.FromResult(0);
    }

    public Task<bool> IsAncestorAsync(NodeId ancestorId, NodeId descendantId, CancellationToken cancellationToken = default)
    {
        // MVP版本：暂时返回false
        return Task.FromResult(false);
    }

    public Task<bool> HasCycleAsync(NodeId nodeId, NodeId potentialParentId, CancellationToken cancellationToken = default)
    {
        // MVP版本：暂时返回false
        return Task.FromResult(false);
    }

    public Task ReorderChildrenAsync(NodeId parentId, IReadOnlyList<NodeId> newOrder, CancellationToken cancellationToken = default)
    {
        // MVP版本：暂时不实现
        _logger.LogWarning("ReorderChildrenAsync not implemented in MVP version");
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<NodeId>> GetPathAsync(NodeId nodeId, CancellationToken cancellationToken = default)
    {
        // MVP版本：暂时返回只包含节点自身的路径
        return Task.FromResult<IReadOnlyList<NodeId>>(new[] { nodeId });
    }

    public async IAsyncEnumerable<NodeId> GetDescendantsAsync(NodeId nodeId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // MVP版本：暂时返回空序列
        yield break;
    }

    public Task<IReadOnlyDictionary<NodeId, NodeId>> BuildParentIndexAsync(CancellationToken cancellationToken = default)
    {
        // MVP版本：暂时返回空字典
        _logger.LogWarning("BuildParentIndexAsync not implemented in MVP version");
        return Task.FromResult<IReadOnlyDictionary<NodeId, NodeId>>(new Dictionary<NodeId, NodeId>());
    }

    public async Task<bool> HasChildrenAsync(NodeId nodeId, CancellationToken cancellationToken = default)
    {
        var children = await GetChildrenAsync(nodeId, cancellationToken);
        return children.Any();
    }

    public Task<int> GetDepthAsync(NodeId nodeId, CancellationToken cancellationToken = default)
    {
        // MVP版本：暂时返回深度0
        return Task.FromResult(0);
    }

    public async Task<IReadOnlyList<NodeId>> GetRootNodesAsync(CancellationToken cancellationToken = default)
    {
        // MVP版本：返回所有没有父节点的节点
        var allNodeIds = await GetAllNodeIdsAsync(cancellationToken);
        var rootNodes = new List<NodeId>();

        foreach (var nodeId in allNodeIds)
        {
            var parent = await GetParentAsync(nodeId, cancellationToken);
            if (parent == null)
            {
                rootNodes.Add(nodeId);
            }
        }

        return rootNodes;
    }

    public Task<bool> WouldCreateCycleAsync(NodeId nodeId, NodeId potentialParentId, CancellationToken cancellationToken = default)
    {
        // MVP版本：暂时返回false
        return Task.FromResult(false);
    }

    #endregion

    #region ICognitiveNodeStorage Implementation

    public async Task<CognitiveNode?> GetCompleteNodeAsync(NodeId nodeId, CancellationToken cancellationToken = default)
    {
        // 获取元数据
        var metadata = await GetAsync(nodeId, cancellationToken);
        if (metadata == null)
            return null;

        // 获取所有级别的内容
        var allContent = await GetAllLevelsAsync(nodeId, cancellationToken);
        
        return new CognitiveNode
        {
            Metadata = metadata,
            Contents = allContent
        };
    }

    public async Task SaveCompleteNodeAsync(CognitiveNode node, CancellationToken cancellationToken = default)
    {
        // 保存元数据
        await SaveAsync(node.Metadata, cancellationToken);

        // 保存所有内容
        foreach (var (_, content) in node.Contents)
        {
            await SaveAsync(content, cancellationToken);
        }
    }

    public async Task<T> ExecuteInTransactionAsync<T>(
        Func<ICognitiveNodeStorage, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        // MVP版本：暂时不实现事务，直接执行操作
        return await operation(this, cancellationToken);
    }

    public async Task ExecuteInTransactionAsync(
        Func<ICognitiveNodeStorage, CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
    {
        // MVP版本：暂时不实现事务，直接执行操作
        await operation(this, cancellationToken);
    }

    public async Task DeleteCompleteNodeAsync(NodeId nodeId, CancellationToken cancellationToken = default)
    {
        // 删除所有内容
        await DeleteAllAsync(nodeId, cancellationToken);

        // 删除元数据
        await DeleteAsync(nodeId, cancellationToken);

        // 删除层次信息（MVP版本暂时跳过）
        _logger.LogDebug("Deleted complete node {NodeId}", nodeId);
    }

    public async Task<IReadOnlyDictionary<NodeId, CognitiveNode>> GetCompleteNodesBatchAsync(
        IEnumerable<NodeId> nodeIds,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<NodeId, CognitiveNode>();

        foreach (var nodeId in nodeIds)
        {
            var node = await GetCompleteNodeAsync(nodeId, cancellationToken);
            if (node != null)
            {
                result[nodeId] = node;
            }
        }

        return result;
    }

    public async Task SaveCompleteNodesBatchAsync(
        IEnumerable<CognitiveNode> nodes,
        CancellationToken cancellationToken = default)
    {
        foreach (var node in nodes)
        {
            await SaveCompleteNodeAsync(node, cancellationToken);
        }
    }

    public async Task<NodeId> CreateNodeAsync(
        NodeMetadata metadata,
        NodeContent? content = null,
        NodeId? parentId = null,
        CancellationToken cancellationToken = default)
    {
        // 保存元数据
        await SaveAsync(metadata, cancellationToken);

        // 保存内容（如果提供）
        if (content != null)
        {
            await SaveAsync(content, cancellationToken);
        }

        // 添加到父节点（如果指定）
        if (parentId != null)
        {
            await AddChildAsync(parentId.Value, metadata.Id, null, cancellationToken);
        }

        return metadata.Id;
    }

    public Task<NodeId> CopyNodeAsync(
        NodeId sourceId,
        NodeId? targetParentId = null,
        bool includeSubtree = true,
        CancellationToken cancellationToken = default)
    {
        // MVP版本：暂时不实现复制功能
        throw new NotImplementedException("Node copying will be implemented in a future version");
    }

    public async Task<StorageStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
    {
        var allMetadataList = new List<NodeMetadata>();
        await foreach (var metadata in GetAllAsync(cancellationToken))
        {
            allMetadataList.Add(metadata);
        }

        var nodeTypeDistribution = allMetadataList.GroupBy(m => m.Type)
            .ToDictionary(g => g.Key, g => g.Count());

        return new StorageStatistics
        {
            TotalNodes = allMetadataList.Count,
            TotalRelations = 0, // MVP版本暂时不统计关系
            TotalContentSize = 0, // MVP版本暂时不统计内容大小
            MaxDepth = 0, // MVP版本暂时不计算深度
            LastModified = allMetadataList.Count > 0 ? allMetadataList.Max(m => m.LastModified) : DateTime.UtcNow,
            NodeTypeDistribution = nodeTypeDistribution,
            ContentLevelDistribution = new Dictionary<LodLevel, int>()
        };
    }

    public async Task<StorageIntegrityResult> ValidateIntegrityAsync(CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        try
        {
            var allNodeIds = await GetAllNodeIdsAsync(cancellationToken);

            foreach (var nodeId in allNodeIds)
            {
                // 检查元数据是否存在
                var metadata = await GetAsync(nodeId, cancellationToken);
                if (metadata == null)
                {
                    errors.Add($"Node {nodeId} has directory but no metadata");
                }
            }
        }
        catch (Exception ex)
        {
            errors.Add($"Failed to validate integrity: {ex.Message}");
        }

        return new StorageIntegrityResult
        {
            IsValid = errors.Count == 0,
            Errors = errors,
            Warnings = warnings,
            ValidatedAt = DateTime.UtcNow
        };
    }

    #endregion

    #region Helper Methods

    private string GetNodeDirectory(NodeId nodeId)
    {
        return Path.Combine(_options.WorkspaceRoot, _options.CogNodesDirectory, nodeId.Value);
    }

    private string GetNodeMetadataPath(NodeId nodeId)
    {
        return Path.Combine(GetNodeDirectory(nodeId), _storageOptions.MetadataFileName);
    }

    private string GetNodeContentPath(NodeId nodeId, LodLevel level)
    {
        var fileName = level switch
        {
            LodLevel.Gist => _storageOptions.GistContentFileName,
            LodLevel.Summary => _storageOptions.SummaryContentFileName,
            LodLevel.Full => _storageOptions.FullContentFileName,
            _ => throw new ArgumentException($"Unsupported LOD level: {level}")
        };

        return Path.Combine(GetNodeDirectory(nodeId), fileName);
    }

    #endregion
}
