using MemoTree.Core.Types;
using Microsoft.Extensions.Logging;
using MemoTree.Core.Exceptions; // added

namespace MemoTree.Services.Storage;

// Partial: Metadata storage region from SimpleCognitiveNodeStorage
public partial class SimpleCognitiveNodeStorage {
    #region INodeMetadataStorage Implementation

    public async Task<NodeMetadata?> GetAsync(NodeId nodeId, CancellationToken cancellationToken = default) {
        var metadataPath = GetNodeMetadataPath(nodeId);
        if (!File.Exists(metadataPath)) { return null; }
        try {
            var yamlContent = await File.ReadAllTextAsync(metadataPath, cancellationToken);
            return _yamlDeserializer.Deserialize<NodeMetadata>(yamlContent);
        }
        catch (Exception ex) {
            _logger.LogError(ex, "Failed to load metadata for node {NodeId}", nodeId);
            throw new StorageException($"Failed to load metadata for node {nodeId}", ex)
            .WithContext("NodeId", nodeId)
            .WithContext("Path", metadataPath);
        }
    }

    public async Task SaveAsync(NodeMetadata metadata, CancellationToken cancellationToken = default) {
        var metadataPath = GetNodeMetadataPath(metadata.Id);
        var directory = Path.GetDirectoryName(metadataPath)!;

        Directory.CreateDirectory(directory);

        try {
            var yamlContent = _yamlSerializer.Serialize(metadata);
            await WriteTextAtomicAsync(metadataPath, yamlContent, cancellationToken);
            _logger.LogDebug("Saved metadata for node {NodeId}", metadata.Id);
        }
        catch (Exception ex) {
            _logger.LogError(ex, "Failed to save metadata for node {NodeId}", metadata.Id);
            throw;
        }
    }

    public Task DeleteAsync(NodeId nodeId, CancellationToken cancellationToken = default) {
        var metadataPath = GetNodeMetadataPath(nodeId);
        if (File.Exists(metadataPath)) {
            File.Delete(metadataPath);
            _logger.LogDebug("Deleted metadata for node {NodeId}", nodeId);
        }
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyDictionary<NodeId, NodeMetadata>> GetBatchAsync(
        IEnumerable<NodeId> nodeIds,
        CancellationToken cancellationToken = default
    ) {
        var result = new Dictionary<NodeId, NodeMetadata>();

        foreach (var nodeId in nodeIds) {
            var metadata = await GetAsync(nodeId, cancellationToken);
            if (metadata != null) {
                result[nodeId] = metadata;
            }
        }

        return result;
    }

    public async Task SaveBatchAsync(
        IEnumerable<NodeMetadata> metadataList,
        CancellationToken cancellationToken = default
    ) {
        foreach (var metadata in metadataList) {
            await SaveAsync(metadata, cancellationToken);
        }
    }

    public Task<bool> ExistsAsync(NodeId nodeId, CancellationToken cancellationToken = default) {
        var metadataPath = GetNodeMetadataPath(nodeId);
        return Task.FromResult(File.Exists(metadataPath));
    }

    public Task<IReadOnlyList<NodeId>> GetAllNodeIdsAsync(CancellationToken cancellationToken = default) {
        var cogNodesPath = _pathService.GetCogNodesDirectory();
        _logger.LogDebug("GetAllNodeIdsAsync: Using CogNodes path: {CogNodesPath}", cogNodesPath);

        if (!Directory.Exists(cogNodesPath)) {
            _logger.LogDebug("GetAllNodeIdsAsync: CogNodes directory does not exist");
            return Task.FromResult<IReadOnlyList<NodeId>>(Array.Empty<NodeId>());
        }

        var nodeIds = new List<NodeId>();
        var directories = Directory.GetDirectories(cogNodesPath);
        _logger.LogDebug("GetAllNodeIdsAsync: Found {DirectoryCount} directories", directories.Length);

        foreach (var dir in directories) {
            var dirName = Path.GetFileName(dir);
            try {
                var nodeId = new NodeId(dirName);
                var metadataPath = Path.Combine(dir, _storageOptions.MetadataFileName);
                if (File.Exists(metadataPath)) {
                    nodeIds.Add(nodeId);
                    _logger.LogDebug("GetAllNodeIdsAsync: Found valid node: {NodeId}", nodeId);
                }
            }
            catch {
                // 忽略无效的目录名
                _logger.LogDebug("GetAllNodeIdsAsync: Ignoring invalid directory: {DirName}", dirName);
            }
        }

        _logger.LogDebug("GetAllNodeIdsAsync: Returning {NodeCount} nodes", nodeIds.Count);
        return Task.FromResult<IReadOnlyList<NodeId>>(nodeIds);
    }

    public async IAsyncEnumerable<NodeMetadata> GetAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) {
        var nodeIds = await GetAllNodeIdsAsync(cancellationToken);

        foreach (var nodeId in nodeIds) {
            var metadata = await GetAsync(nodeId, cancellationToken);
            if (metadata != null) {
                yield return metadata;
            }
        }
    }

    public async Task<int> GetCountAsync(CancellationToken cancellationToken = default) {
        var nodeIds = await GetAllNodeIdsAsync(cancellationToken);
        return nodeIds.Count;
    }

    public async IAsyncEnumerable<NodeMetadata> FindByTypeAsync(NodeType type, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) {
        await foreach (var metadata in GetAllAsync(cancellationToken)) {
            if (metadata.Type == type) {
                yield return metadata;
            }
        }
    }

    public async IAsyncEnumerable<NodeMetadata> FindByTagAsync(string tag, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) {
        // MVP版本：暂不支持标签搜索；加入一次最小await以消除编译器告警
        await Task.Yield();
        yield break;
    }

    #endregion
}
