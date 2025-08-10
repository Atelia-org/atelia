using MemoTree.Core.Storage.Interfaces;
using MemoTree.Core.Types;
using MemoTree.Core.Exceptions;
using Microsoft.Extensions.Logging;

namespace MemoTree.Services;

/// <summary>
/// MemoTree编辑器实现 (MVP版本)
/// </summary>
public class MemoTreeEditor : IMemoTreeEditor
{
    private readonly ICognitiveNodeStorage _storage;
    private readonly ILogger<MemoTreeEditor> _logger;

    public MemoTreeEditor(
        ICognitiveNodeStorage storage,
        ILogger<MemoTreeEditor> logger)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<NodeId> CreateNodeAsync(
        string title, 
        string content = "", 
        NodeId? parentId = null, 
        NodeType type = NodeType.Concept, 
        CancellationToken cancellationToken = default)
    {
        try
        {
            var nodeId = NodeId.Generate();
            var now = DateTime.UtcNow;

            // 创建元数据
            var metadata = new NodeMetadata
            {
                Id = nodeId,
                Title = title,
                Type = type,
                CreatedAt = now,
                LastModified = now,
                CustomProperties = new Dictionary<string, object>()
            };

            // 创建内容（如果提供了内容）
            var nodeContent = new Dictionary<LodLevel, NodeContent>();
            if (!string.IsNullOrEmpty(content))
            {
                // MVP：仅保存 Full 正文；Gist/Summary 不创建文件（保持为“尚未摘要”状态）
                nodeContent[LodLevel.Full] = new NodeContent
                {
                    Id = nodeId,
                    Level = LodLevel.Full,
                    Content = content,
                    LastModified = now
                };
            }

            // 创建层次信息
            var hierarchyInfo = HierarchyInfo.Create(nodeId);

            // 创建完整节点
            var cognitiveNode = new CognitiveNode
            {
                Metadata = metadata,
                Contents = nodeContent
            };

            // 保存节点
            await _storage.SaveCompleteNodeAsync(cognitiveNode, cancellationToken);

            // 如果有父节点，添加到父节点的子节点列表
            if (parentId != null)
            {
                await _storage.AddChildAsync(parentId.Value, nodeId, null, cancellationToken);
            }

            _logger.LogInformation("Created node {NodeId} with title '{Title}'", nodeId, title);
            return nodeId;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create node with title '{Title}'", title);
            throw;
        }
    }

    public async Task UpdateNodeContentAsync(NodeId nodeId, string content, CancellationToken cancellationToken = default)
    {
        try
        {
            var existingNode = await _storage.GetCompleteNodeAsync(nodeId, cancellationToken);
            if (existingNode == null)
            {
                throw new NodeNotFoundException(nodeId);
            }

            // 更新Full级别的内容
            var updatedContent = new NodeContent
            {
                Id = nodeId,
                Level = LodLevel.Full,
                Content = content,
                LastModified = DateTime.UtcNow
            };

            await _storage.SaveAsync(updatedContent, cancellationToken);

            // 更新元数据的修改时间
            var updatedMetadata = existingNode.Metadata with { LastModified = DateTime.UtcNow };
            await _storage.SaveAsync(updatedMetadata, cancellationToken);

            _logger.LogInformation("Updated content for node {NodeId}", nodeId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update content for node {NodeId}", nodeId);
            throw;
        }
    }

    public async Task UpdateNodeTitleAsync(NodeId nodeId, string title, CancellationToken cancellationToken = default)
    {
        try
        {
            var existingNode = await _storage.GetCompleteNodeAsync(nodeId, cancellationToken);
            if (existingNode == null)
            {
                throw new NodeNotFoundException(nodeId);
            }

            // 更新元数据
            var updatedMetadata = existingNode.Metadata with 
            { 
                Title = title, 
                LastModified = DateTime.UtcNow 
            };

            await _storage.SaveAsync(updatedMetadata, cancellationToken);

            // 标题与Gist/内容正交：更新标题不应自动写入Gist文件
            // MVP阶段：不创建Gist文件，保持“尚未摘要”状态

            _logger.LogInformation("Updated title for node {NodeId} to '{Title}'", nodeId, title);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update title for node {NodeId}", nodeId);
            throw;
        }
    }

    public async Task DeleteNodeAsync(NodeId nodeId, bool recursive = false, CancellationToken cancellationToken = default)
    {
        try
        {
            var existingNode = await _storage.GetCompleteNodeAsync(nodeId, cancellationToken);
            if (existingNode == null)
            {
                _logger.LogWarning("Attempted to delete non-existent node {NodeId}", nodeId);
                return;
            }

            if (recursive)
            {
                // 递归删除子节点
                var children = await _storage.GetChildrenAsync(nodeId, cancellationToken);
                foreach (var childId in children)
                {
                    await DeleteNodeAsync(childId, true, cancellationToken);
                }
            }

            // 删除完整节点
            await _storage.DeleteCompleteNodeAsync(nodeId, cancellationToken);

            _logger.LogInformation("Deleted node {NodeId} (recursive: {Recursive})", nodeId, recursive);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete node {NodeId}", nodeId);
            throw;
        }
    }

    public async Task MoveNodeAsync(NodeId nodeId, NodeId? newParentId, int newOrder = 0, CancellationToken cancellationToken = default)
    {
        // MVP版本：暂时不实现移动功能
        _logger.LogWarning("MoveNodeAsync not implemented in MVP version");
        throw new NotImplementedException("Node moving will be implemented in a future version");
    }

    public async Task<CognitiveNode?> GetNodeAsync(NodeId nodeId, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _storage.GetCompleteNodeAsync(nodeId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get node {NodeId}", nodeId);
            throw;
        }
    }

    public async Task<bool> NodeExistsAsync(NodeId nodeId, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _storage.ExistsAsync(nodeId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check existence of node {NodeId}", nodeId);
            throw;
        }
    }

    public async Task<IReadOnlyList<CognitiveNode>> GetChildrenAsync(NodeId? parentId = null, CancellationToken cancellationToken = default)
    {
        try
        {
            IReadOnlyList<NodeId> childIds;

            if (parentId == null)
            {
                // 如果没有指定父节点，返回所有顶层节点
                childIds = await _storage.GetTopLevelNodesAsync(cancellationToken);
            }
            else
            {
                // 获取指定父节点的子节点
                childIds = await _storage.GetChildrenAsync(parentId.Value, cancellationToken);
            }

            var children = new List<CognitiveNode>();

            foreach (var childId in childIds)
            {
                var child = await _storage.GetCompleteNodeAsync(childId, cancellationToken);
                if (child != null)
                {
                    children.Add(child);
                }
            }

            return children;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get children for parent {ParentId}", parentId);
            throw;
        }
    }

    public async Task<IReadOnlyList<CognitiveNode>> GetAllNodesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var nodes = new List<CognitiveNode>();

            await foreach (var metadata in _storage.GetAllAsync(cancellationToken))
            {
                var node = await _storage.GetCompleteNodeAsync(metadata.Id, cancellationToken);
                if (node != null)
                {
                    nodes.Add(node);
                }
            }

            return nodes;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get all nodes");
            throw;
        }
    }
}
