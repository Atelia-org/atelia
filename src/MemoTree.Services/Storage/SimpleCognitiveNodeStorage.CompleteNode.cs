using MemoTree.Core.Storage.Interfaces;
using MemoTree.Core.Types;
using Microsoft.Extensions.Logging;

namespace MemoTree.Services.Storage;

// Partial: ICognitiveNodeStorage implementation region from SimpleCognitiveNodeStorage
public partial class SimpleCognitiveNodeStorage
{    
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

        // 收集有效节点集合，并进行基础文件/元数据校验
        var validNodes = new HashSet<NodeId>();
        var cogNodesPath = _pathService.GetCogNodesDirectory();
        try
        {
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

                    NodeMetadata? meta = null;
                    try
                    {
                        var yaml = await File.ReadAllTextAsync(metadataPath, cancellationToken);
                        meta = _yamlDeserializer.Deserialize<NodeMetadata>(yaml);
                        if (meta.Id != parsedId)
                        {
                            errors.Add($"Metadata Id mismatch in {dirName}: dir={parsedId}, meta={meta.Id}");
                        }
                        else
                        {
                            validNodes.Add(parsedId);
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Failed to parse metadata {dirName}/{_storageOptions.MetadataFileName}: {ex.Message}");
                    }

                    // 校验内容文件与哈希
                    if (meta != null)
                    {
                        foreach (var level in Enum.GetValues<LodLevel>())
                        {
                            var contentPath = GetNodeContentPath(parsedId, level);
                            var hasFile = File.Exists(contentPath);
                            var hasHash = meta.ContentHashes.TryGetValue(level, out var declaredHash) && !string.IsNullOrEmpty(declaredHash);

                            if (hasFile && !hasHash)
                            {
                                warnings.Add($"Node {parsedId} has {level} content file but no hash in metadata");
                            }
                            else if (!hasFile && hasHash)
                            {
                                warnings.Add($"Node {parsedId} metadata declares {level} content but file is missing");
                            }
                            else if (hasFile && hasHash)
                            {
                                try
                                {
                                    var text = await File.ReadAllTextAsync(contentPath, cancellationToken);
                                    var actualHash = ComputeSha256Base64(text);
                                    if (!string.Equals(actualHash, declaredHash, StringComparison.Ordinal))
                                    {
                                        errors.Add($"Node {parsedId} {level} content hash mismatch: meta={declaredHash}, actual={actualHash}");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    errors.Add($"Failed to read/verify content for node {parsedId} {level}: {ex.Message}");
                                }
                            }
                        }

                        // 检查未知额外文件（非标准文件名）
                        try
                        {
                            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                            {
                                _storageOptions.MetadataFileName,
                                _storageOptions.GistContentFileName,
                                _storageOptions.SummaryContentFileName,
                                _storageOptions.FullContentFileName,
                                _storageOptions.ExternalLinksFileName,
                                _storageOptions.RelationsFileName
                            };
                            var files = Directory.GetFiles(dir).Select(Path.GetFileName);
                            foreach (var f in files)
                            {
                                if (f != null && !known.Contains(f))
                                {
                                    warnings.Add($"Node {parsedId} has unrecognized file: {f}");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            warnings.Add($"Failed to scan extra files for node {parsedId}: {ex.Message}");
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            errors.Add($"Failed to validate files/metadata: {ex.Message}");
        }

        // 层级一致性与循环检测
        try
        {
            var parentIndex = await _hierarchy.BuildParentIndexAsync(cancellationToken);
            var parentSet = new HashSet<NodeId>(parentIndex.Values);

            // 引用缺失节点检测
            foreach (var kv in parentIndex)
            {
                if (!validNodes.Contains(kv.Key))
                    errors.Add($"Hierarchy references missing child node: {kv.Key}");
                if (!validNodes.Contains(kv.Value))
                    errors.Add($"Hierarchy references missing parent node: {kv.Value}");
            }

            var topLevels = await _hierarchy.GetTopLevelNodesAsync(cancellationToken);
            foreach (var top in topLevels)
            {
                if (!validNodes.Contains(top))
                    warnings.Add($"Top-level hierarchy node missing metadata/content: {top}");
            }

            // 构建邻接表并进行循环检测（DFS）
            var adjacency = new Dictionary<NodeId, List<NodeId>>();
            foreach (var parentId in parentSet)
            {
                try
                {
                    var children = await _hierarchy.GetChildrenAsync(parentId, cancellationToken);
                    adjacency[parentId] = children.ToList();
                }
                catch (Exception ex)
                {
                    warnings.Add($"Failed to enumerate children of {parentId}: {ex.Message}");
                }
            }

            // 节点全集：有效节点 + 所有在层级中出现的节点
            var allHierarchyNodes = new HashSet<NodeId>(validNodes);
            foreach (var p in adjacency.Keys) allHierarchyNodes.Add(p);
            foreach (var ch in adjacency.Values.SelectMany(x => x)) allHierarchyNodes.Add(ch);

            var visiting = new HashSet<NodeId>();
            var visited = new HashSet<NodeId>();
            bool Dfs(NodeId node)
            {
                if (visiting.Contains(node))
                    return true; // cycle
                if (visited.Contains(node))
                    return false;
                visiting.Add(node);
                if (adjacency.TryGetValue(node, out var chs))
                {
                    foreach (var c in chs)
                    {
                        if (Dfs(c)) return true;
                    }
                }
                visiting.Remove(node);
                visited.Add(node);
                return false;
            }

            foreach (var n in allHierarchyNodes)
            {
                if (!visited.Contains(n) && Dfs(n))
                {
                    errors.Add($"Hierarchy cycle detected involving node {n}");
                    break; // 报告一次即可
                }
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"Failed to validate hierarchy: {ex.Message}");
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
}
