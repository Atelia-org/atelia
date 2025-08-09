using MemoTree.Core.Types;

namespace MemoTree.Services.Models;

/// <summary>
/// 简化版认知节点 (MVP版本)
/// 只包含核心字段，不支持多级LOD内容
/// </summary>
public record SimpleCognitiveNode
{
    /// <summary>
    /// 节点唯一标识符
    /// </summary>
    public NodeId Id { get; init; }

    /// <summary>
    /// 节点标题
    /// </summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>
    /// 节点内容 (单一内容，无多级LOD)
    /// </summary>
    public string Content { get; init; } = string.Empty;

    /// <summary>
    /// 节点类型
    /// </summary>
    public NodeType Type { get; init; }

    /// <summary>
    /// 创建时间
    /// </summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>
    /// 最后更新时间
    /// </summary>
    public DateTime UpdatedAt { get; init; }

    /// <summary>
    /// 父节点ID (可选)
    /// </summary>
    public NodeId? ParentId { get; init; }

    /// <summary>
    /// 在同级节点中的排序位置
    /// </summary>
    public int Order { get; init; }

    /// <summary>
    /// 创建新的简化认知节点
    /// </summary>
    public static SimpleCognitiveNode Create(string title, string content = "", NodeType type = NodeType.Concept, NodeId? parentId = null, int order = 0)
    {
        var now = DateTime.UtcNow;
        return new SimpleCognitiveNode
        {
            Id = NodeId.Generate(),
            Title = title,
            Content = content,
            Type = type,
            CreatedAt = now,
            UpdatedAt = now,
            ParentId = parentId,
            Order = order
        };
    }

    /// <summary>
    /// 更新节点内容
    /// </summary>
    public SimpleCognitiveNode WithContent(string content)
    {
        return this with 
        { 
            Content = content, 
            UpdatedAt = DateTime.UtcNow 
        };
    }

    /// <summary>
    /// 更新节点标题
    /// </summary>
    public SimpleCognitiveNode WithTitle(string title)
    {
        return this with 
        { 
            Title = title, 
            UpdatedAt = DateTime.UtcNow 
        };
    }

    /// <summary>
    /// 移动到新的父节点
    /// </summary>
    public SimpleCognitiveNode WithParent(NodeId? parentId, int order = 0)
    {
        return this with 
        { 
            ParentId = parentId, 
            Order = order,
            UpdatedAt = DateTime.UtcNow 
        };
    }
}
