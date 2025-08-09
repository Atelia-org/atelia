using MemoTree.Core.Types;

namespace MemoTree.Services.Models;

/// <summary>
/// 视图统计信息
/// 提供视图的使用情况和性能指标
/// </summary>
public record ViewStats
{
    /// <summary>
    /// 视图名称
    /// </summary>
    public string ViewName { get; init; } = string.Empty;

    /// <summary>
    /// 总节点数
    /// </summary>
    public int TotalNodes { get; init; }

    /// <summary>
    /// 展开的节点数
    /// </summary>
    public int ExpandedNodes { get; init; }

    /// <summary>
    /// 折叠的节点数
    /// </summary>
    public int CollapsedNodes { get; init; }

    /// <summary>
    /// 当前视图的总字符数 (展开节点的内容)
    /// </summary>
    public int TotalCharacters { get; init; }

    /// <summary>
    /// 预估展开所有节点后的字符数
    /// </summary>
    public int EstimatedFullCharacters { get; init; }

    /// <summary>
    /// 根节点ID
    /// </summary>
    public NodeId? RootNodeId { get; init; }

    /// <summary>
    /// 最大深度
    /// </summary>
    public int MaxDepth { get; init; }

    /// <summary>
    /// 最后更新时间
    /// </summary>
    public DateTime LastUpdated { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// 获取展开比例 (0.0 - 1.0)
    /// </summary>
    public double GetExpandedRatio()
    {
        if (TotalNodes == 0) return 0.0;
        return (double)ExpandedNodes / TotalNodes;
    }

    /// <summary>
    /// 获取字符使用比例 (相对于预估的完全展开状态)
    /// </summary>
    public double GetCharacterUsageRatio()
    {
        if (EstimatedFullCharacters == 0) return 0.0;
        return (double)TotalCharacters / EstimatedFullCharacters;
    }

    /// <summary>
    /// 格式化为人类可读的字符串
    /// </summary>
    public string FormatSummary()
    {
        return $"{ExpandedNodes}/{TotalNodes} nodes expanded, {TotalCharacters:N0} chars";
    }

    /// <summary>
    /// 格式化为详细的统计信息
    /// </summary>
    public string FormatDetailed()
    {
        var expandedRatio = GetExpandedRatio() * 100;
        var charUsageRatio = GetCharacterUsageRatio() * 100;

        return $"""
            View: {ViewName}
            Nodes: {ExpandedNodes}/{TotalNodes} expanded ({expandedRatio:F1}%)
            Characters: {TotalCharacters:N0}/{EstimatedFullCharacters:N0} ({charUsageRatio:F1}%)
            Max Depth: {MaxDepth}
            Last Updated: {LastUpdated:yyyy-MM-dd HH:mm:ss}
            """;
    }

    /// <summary>
    /// 创建空的统计信息
    /// </summary>
    public static ViewStats CreateEmpty(string viewName = "default")
    {
        return new ViewStats
        {
            ViewName = viewName,
            LastUpdated = DateTime.UtcNow
        };
    }
}
