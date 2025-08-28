using MemoTree.Core.Types;
using Microsoft.Extensions.Logging;
using MemoTree.Core.Exceptions; // added

namespace MemoTree.Services.Storage;

// Partial: Content storage region from SimpleCognitiveNodeStorage
public partial class SimpleCognitiveNodeStorage {
    #region INodeContentStorage Implementation

    public async Task<NodeContent?> GetAsync(NodeId nodeId, LodLevel level, CancellationToken cancellationToken = default) {
        var contentPath = GetNodeContentPath(nodeId, level);
        if (!File.Exists(contentPath)) {
            return null;
        }

        try {
            var content = await File.ReadAllTextAsync(contentPath, cancellationToken);
            return new NodeContent {
                Id = nodeId,
                Level = level,
                Content = content,
                LastModified = File.GetLastWriteTimeUtc(contentPath)
            };
        } catch (Exception ex) {
            _logger.LogError(ex, "Failed to load content for node {NodeId} at level {Level}", nodeId, level);
            throw new StorageException($"Failed to load content for node {nodeId} at level {level}", ex)
            .WithContext("NodeId", nodeId)
            .WithContext("Level", level)
            .WithContext("Path", contentPath);
        }
    }

    public async Task SaveAsync(NodeContent content, CancellationToken cancellationToken = default) {
        var contentPath = GetNodeContentPath(content.Id, content.Level);
        var directory = Path.GetDirectoryName(contentPath)!;

        Directory.CreateDirectory(directory);

        try {
            await WriteTextAtomicAsync(contentPath, content.Content, cancellationToken);
            _logger.LogDebug("Saved content for node {NodeId} at level {Level}", content.Id, content.Level);
        } catch (Exception ex) {
            _logger.LogError(ex, "Failed to save content for node {NodeId} at level {Level}", content.Id, content.Level);
            throw;
        }
    }

    public Task DeleteAsync(NodeId nodeId, LodLevel level, CancellationToken cancellationToken = default) {
        var contentPath = GetNodeContentPath(nodeId, level);
        if (File.Exists(contentPath)) {
            File.Delete(contentPath);
            _logger.LogDebug("Deleted content for node {NodeId} at level {Level}", nodeId, level);
        }
        return Task.CompletedTask;
    }

    public async Task DeleteAllAsync(NodeId nodeId, CancellationToken cancellationToken = default) {
        var nodeDirectory = GetNodeDirectory(nodeId);
        if (Directory.Exists(nodeDirectory)) {
            // 删除所有内容文件，保留元数据
            foreach (LodLevel level in Enum.GetValues<LodLevel>()) {
                await DeleteAsync(nodeId, level, cancellationToken);
            }
        }
    }

    public async Task<IReadOnlyDictionary<LodLevel, NodeContent>> GetAllLevelsAsync(
        NodeId nodeId,
        CancellationToken cancellationToken = default
    ) {
        var result = new Dictionary<LodLevel, NodeContent>();

        foreach (LodLevel level in Enum.GetValues<LodLevel>()) {
            var content = await GetAsync(nodeId, level, cancellationToken);
            if (content != null) {
                result[level] = content;
            }
        }

        return result;
    }

    public async Task<IReadOnlyDictionary<(NodeId NodeId, LodLevel Level), NodeContent>> GetBatchAsync(
        IEnumerable<(NodeId NodeId, LodLevel Level)> requests,
        CancellationToken cancellationToken = default
    ) {
        var result = new Dictionary<(NodeId NodeId, LodLevel Level), NodeContent>();

        foreach (var (nodeId, level) in requests) {
            var content = await GetAsync(nodeId, level, cancellationToken);
            if (content != null) {
                result[(nodeId, level)] = content;
            }
        }

        return result;
    }

    public Task<IReadOnlyDictionary<LodLevel, long>> GetContentSizeStatsAsync(
        NodeId nodeId,
        CancellationToken cancellationToken = default
    ) {
        var result = new Dictionary<LodLevel, long>();

        foreach (LodLevel level in Enum.GetValues<LodLevel>()) {
            var contentPath = GetNodeContentPath(nodeId, level);
            if (File.Exists(contentPath)) {
                var fileInfo = new FileInfo(contentPath);
                result[level] = fileInfo.Length;
            }
        }

        return Task.FromResult<IReadOnlyDictionary<LodLevel, long>>(result);
    }

    public Task<IReadOnlyList<LodLevel>> GetAvailableLevelsAsync(NodeId nodeId, CancellationToken cancellationToken = default) {
        var levels = new List<LodLevel>();

        foreach (LodLevel level in Enum.GetValues<LodLevel>()) {
            var contentPath = GetNodeContentPath(nodeId, level);
            if (File.Exists(contentPath)) {
                levels.Add(level);
            }
        }

        return Task.FromResult<IReadOnlyList<LodLevel>>(levels);
    }

    public Task<bool> ExistsAsync(NodeId nodeId, LodLevel level, CancellationToken cancellationToken = default) {
        var contentPath = GetNodeContentPath(nodeId, level);
        return Task.FromResult(File.Exists(contentPath));
    }

    #endregion
}
