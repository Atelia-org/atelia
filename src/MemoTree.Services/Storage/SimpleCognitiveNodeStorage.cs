using MemoTree.Core.Storage.Interfaces;
using MemoTree.Core.Types;
using MemoTree.Core.Configuration;
using MemoTree.Core.Json;
using MemoTree.Core.Services;
using MemoTree.Services.Yaml;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using MemoTree.Core.Storage.Hierarchy;

namespace MemoTree.Services.Storage;

/// <summary>
/// 简化的认知节点存储实现 (MVP版本)
/// 基于文件系统的直接存储，不使用版本化存储
/// </summary>
public class SimpleCognitiveNodeStorage : ICognitiveNodeStorage
{
    private readonly MemoTreeOptions _options;
    private readonly StorageOptions _storageOptions;
    private readonly IWorkspacePathService _pathService;
    private readonly ILogger<SimpleCognitiveNodeStorage> _logger;
    private readonly ISerializer _yamlSerializer;
    private readonly IDeserializer _yamlDeserializer;
    // Note: JSON options removed as YAML is used for metadata; keep IO consistent
    private readonly INodeHierarchyStorage _hierarchy;

    public SimpleCognitiveNodeStorage(
        IOptions<MemoTreeOptions> options,
        IOptions<StorageOptions> storageOptions,
        IWorkspacePathService pathService,
        INodeHierarchyStorage hierarchy,
        ILogger<SimpleCognitiveNodeStorage> logger)
    {
        _options = options.Value;
        _storageOptions = storageOptions.Value;
        _pathService = pathService ?? throw new ArgumentNullException(nameof(pathService));
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

    // no JSON options needed here
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
            await WriteTextAtomicAsync(metadataPath, yamlContent, cancellationToken);
            _logger.LogDebug("Saved metadata for node {NodeId}", metadata.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save metadata for node {NodeId}", metadata.Id);
            throw;
        }
    }

    public Task DeleteAsync(NodeId nodeId, CancellationToken cancellationToken = default)
    {
        var metadataPath = GetNodeMetadataPath(nodeId);
        if (File.Exists(metadataPath))
        {
            File.Delete(metadataPath);
            _logger.LogDebug("Deleted metadata for node {NodeId}", nodeId);
        }
        return Task.CompletedTask;
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

    public Task<bool> ExistsAsync(NodeId nodeId, CancellationToken cancellationToken = default)
    {
        var metadataPath = GetNodeMetadataPath(nodeId);
        return Task.FromResult(File.Exists(metadataPath));
    }

    public Task<IReadOnlyList<NodeId>> GetAllNodeIdsAsync(CancellationToken cancellationToken = default)
    {
        var cogNodesPath = _pathService.GetCogNodesDirectory();
        _logger.LogDebug("GetAllNodeIdsAsync: Using CogNodes path: {CogNodesPath}", cogNodesPath);

        if (!Directory.Exists(cogNodesPath))
        {
            _logger.LogDebug("GetAllNodeIdsAsync: CogNodes directory does not exist");
            return Task.FromResult<IReadOnlyList<NodeId>>(Array.Empty<NodeId>());
        }

        var nodeIds = new List<NodeId>();
        var directories = Directory.GetDirectories(cogNodesPath);
        _logger.LogDebug("GetAllNodeIdsAsync: Found {DirectoryCount} directories", directories.Length);

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
                    _logger.LogDebug("GetAllNodeIdsAsync: Found valid node: {NodeId}", nodeId);
                }
            }
            catch
            {
                // 忽略无效的目录名
                _logger.LogDebug("GetAllNodeIdsAsync: Ignoring invalid directory: {DirName}", dirName);
            }
        }

        _logger.LogDebug("GetAllNodeIdsAsync: Returning {NodeCount} nodes", nodeIds.Count);
    return Task.FromResult<IReadOnlyList<NodeId>>(nodeIds);
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
        // MVP版本：暂不支持标签搜索；加入一次最小await以消除编译器告警
        await Task.Yield();
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
            await WriteTextAtomicAsync(contentPath, content.Content, cancellationToken);
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
    // 委托给持久化的层级存储实现
    return _hierarchy.RemoveChildAsync(parentId, childId, cancellationToken);
    }

    public Task MoveNodeAsync(NodeId nodeId, NodeId? newParentId, int? newOrder = null, CancellationToken cancellationToken = default)
    {
    // 委托给持久化的层级存储实现
    return _hierarchy.MoveNodeAsync(nodeId, newParentId, newOrder, cancellationToken);
    }

    public Task<IReadOnlyList<NodeId>> GetNodePathAsync(NodeId nodeId, CancellationToken cancellationToken = default)
    {
        // 非接口方法，仅为兼容：委托到 GetPathAsync
        return _hierarchy.GetPathAsync(nodeId, cancellationToken);
    }

    public Task<int> GetNodeDepthAsync(NodeId nodeId, CancellationToken cancellationToken = default)
    {
        // 非接口方法，仅为兼容：委托到底层
        return _hierarchy.GetDepthAsync(nodeId, cancellationToken);
    }

    public Task<bool> IsAncestorAsync(NodeId ancestorId, NodeId descendantId, CancellationToken cancellationToken = default)
    {
        // 简化实现：沿父链检查；委托组合
        return _hierarchy.WouldCreateCycleAsync(descendantId, ancestorId, cancellationToken);
    }

    public Task<bool> HasCycleAsync(NodeId nodeId, NodeId potentialParentId, CancellationToken cancellationToken = default)
    {
        return _hierarchy.WouldCreateCycleAsync(potentialParentId, nodeId, cancellationToken);
    }

    public Task ReorderChildrenAsync(NodeId parentId, IReadOnlyList<NodeId> newOrder, CancellationToken cancellationToken = default)
    {
        return _hierarchy.ReorderChildrenAsync(parentId, newOrder, cancellationToken);
    }

    public Task<IReadOnlyList<NodeId>> GetPathAsync(NodeId nodeId, CancellationToken cancellationToken = default)
    {
        return _hierarchy.GetPathAsync(nodeId, cancellationToken);
    }

    public IAsyncEnumerable<NodeId> GetDescendantsAsync(NodeId nodeId, CancellationToken cancellationToken = default)
        => _hierarchy.GetDescendantsAsync(nodeId, cancellationToken);

    public Task<IReadOnlyDictionary<NodeId, NodeId>> BuildParentIndexAsync(CancellationToken cancellationToken = default)
    {
        return _hierarchy.BuildParentIndexAsync(cancellationToken);
    }

    public async Task<bool> HasChildrenAsync(NodeId nodeId, CancellationToken cancellationToken = default)
    {
        var children = await GetChildrenAsync(nodeId, cancellationToken);
        return children.Any();
    }

    public Task<int> GetDepthAsync(NodeId nodeId, CancellationToken cancellationToken = default)
    {
        return _hierarchy.GetDepthAsync(nodeId, cancellationToken);
    }

    public Task<IReadOnlyList<NodeId>> GetTopLevelNodesAsync(CancellationToken cancellationToken = default)
        => _hierarchy.GetTopLevelNodesAsync(cancellationToken);

    public Task<bool> WouldCreateCycleAsync(NodeId nodeId, NodeId potentialParentId, CancellationToken cancellationToken = default)
    {
        return _hierarchy.WouldCreateCycleAsync(nodeId, potentialParentId, cancellationToken);
    }

    public Task EnsureNodeExistsInHierarchyAsync(NodeId nodeId, CancellationToken cancellationToken = default)
    {
        // 委托给层次关系存储
        return _hierarchy.EnsureNodeExistsInHierarchyAsync(nodeId, cancellationToken);
    }

    public Task DeleteHierarchyInfoAsync(NodeId parentId, CancellationToken cancellationToken = default)
    {
        return _hierarchy.DeleteHierarchyInfoAsync(parentId, cancellationToken);
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

        // 清理层级关系：
        // 1) 从父节点的子列表中移除自身
        var parentId = await _hierarchy.GetParentAsync(nodeId, cancellationToken);
        if (parentId.HasValue)
        {
            await _hierarchy.RemoveChildAsync(parentId.Value, nodeId, cancellationToken);
        }

        // 2) 删除自身的层级记录（若作为父节点存在记录）
        try
        {
            await _hierarchy.DeleteHierarchyInfoAsync(nodeId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete hierarchy info for node {NodeId}", nodeId);
        }

        _logger.LogDebug("Deleted complete node {NodeId} and cleaned up hierarchy", nodeId);

        // 3) 清理空目录
        try
        {
            var nodeDir = GetNodeDirectory(nodeId);
            if (Directory.Exists(nodeDir) && !Directory.EnumerateFileSystemEntries(nodeDir).Any())
            {
                Directory.Delete(nodeDir);
                _logger.LogDebug("Deleted empty node directory for {NodeId}", nodeId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete empty node directory for {NodeId}", nodeId);
        }
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

        long totalContentSize = 0;
        var contentLevelDistribution = new Dictionary<LodLevel, int>();
        int maxDepth = 0;

        foreach (var meta in allMetadataList)
        {
            var sizeStats = await GetContentSizeStatsAsync(meta.Id, cancellationToken);
            foreach (var kv in sizeStats)
            {
                totalContentSize += kv.Value;
                contentLevelDistribution[kv.Key] = contentLevelDistribution.TryGetValue(kv.Key, out var c) ? c + 1 : 1;
            }

            var depth = await _hierarchy.GetDepthAsync(meta.Id, cancellationToken);
            if (depth > maxDepth) maxDepth = depth;
        }

        var nodeTypeDistribution = allMetadataList
            .GroupBy(m => m.Type)
            .ToDictionary(g => g.Key, g => g.Count());

        return new StorageStatistics
        {
            TotalNodes = allMetadataList.Count,
            TotalRelations = 0, // 关系暂不统计
            TotalContentSize = totalContentSize,
            MaxDepth = maxDepth,
            LastModified = allMetadataList.Count > 0 ? allMetadataList.Max(m => m.LastModified) : DateTime.UtcNow,
            NodeTypeDistribution = nodeTypeDistribution,
            ContentLevelDistribution = contentLevelDistribution
        };
    }

    public async Task<StorageIntegrityResult> ValidateIntegrityAsync(CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        try
        {
            var cogNodesPath = _pathService.GetCogNodesDirectory();
            if (!Directory.Exists(cogNodesPath))
            {
                warnings.Add($"CogNodes directory not found: {cogNodesPath}");
            }
            else
            {
                var allDirs = Directory.GetDirectories(cogNodesPath);
                foreach (var dir in allDirs)
                {
                    var dirName = Path.GetFileName(dir);
                    NodeId parsedId;
                    try
                    {
                        parsedId = new NodeId(dirName);
                    }
                    catch
                    {
                        warnings.Add($"Invalid node directory name: {dirName}");
                        continue;
                    }

                    var metadataPath = Path.Combine(dir, _storageOptions.MetadataFileName);
                    if (!File.Exists(metadataPath))
                    {
                        errors.Add($"Directory {dirName} missing metadata file {_storageOptions.MetadataFileName}");
                        continue;
                    }

                    try
                    {
                        var yaml = await File.ReadAllTextAsync(metadataPath, cancellationToken);
                        var meta = _yamlDeserializer.Deserialize<NodeMetadata>(yaml);
                        if (meta.Id != parsedId)
                        {
                            errors.Add($"Metadata Id mismatch in {dirName}: dir={parsedId}, meta={meta.Id}");
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Failed to parse metadata {dirName}/{_storageOptions.MetadataFileName}: {ex.Message}");
                    }
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
    return _pathService.GetNodeDirectory(nodeId);
    }

    private string GetNodeMetadataPath(NodeId nodeId)
    {
    return _pathService.GetNodeMetadataPath(nodeId);
    }

    private string GetNodeContentPath(NodeId nodeId, LodLevel level)
    {
    return _pathService.GetNodeContentPath(nodeId, level);
    }

    #endregion

    #region Atomic IO helpers
    private static async Task WriteTextAtomicAsync(string targetPath, string content, CancellationToken ct)
    {
        var tempPath = targetPath + ".tmp";
        var encoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        await File.WriteAllTextAsync(tempPath, content, encoding, ct);

        // Ensure target directory exists
        var dir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // Replace or move atomically where possible
        if (File.Exists(targetPath))
        {
            // On Windows, File.Replace offers atomic semantics
            var backup = targetPath + ".bak";
            try
            {
                File.Replace(tempPath, targetPath, backup, ignoreMetadataErrors: true);
                // Best-effort cleanup
                if (File.Exists(backup)) File.Delete(backup);
            }
            catch
            {
                // Fallback: move over
                if (File.Exists(targetPath)) File.Delete(targetPath);
                File.Move(tempPath, targetPath);
            }
        }
        else
        {
            File.Move(tempPath, targetPath);
        }
    }
    #endregion
}
