using System.Collections.Generic;
using System.Globalization;

namespace Atelia.MemoTree;

/// <summary>
/// MemoTree 节点的稳定标识符。
/// </summary>
public readonly record struct MemoNodeId(string Value) {
    public override string ToString() => Value ?? string.Empty;
}

/// <summary>
/// 节点正文块的稳定标识符。
/// </summary>
public readonly record struct MemoBlockId(uint Value) {
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// MemoTree 中 ATX Heading 的层级。
/// </summary>
public enum MemoHeadingLevel {
    H1 = 1,
    H2 = 2,
    H3 = 3,
    H4 = 4,
    H5 = 5,
    H6 = 6,
}

/// <summary>
/// 节点在 Window 中的展开层级。
/// </summary>
public enum MemoNodeViewLevel {
    TitleAndImpression,
    Summary,
    Full,
}

/// <summary>
/// 搜索命中的字段类型。
/// </summary>
public enum MemoSearchField {
    Title,
    Impression,
    Summary,
    Body,
}

/// <summary>
/// 树级概要快照。
/// </summary>
public sealed record MemoTreeSnapshot(
    long TreeVersion,
    int NodeCount,
    IReadOnlyList<MemoNodeId>? RootNodeIds = null,
    IReadOnlyList<MemoNodeId>? PinnedNodeIds = null
);

/// <summary>
/// 单节点概要快照。
/// </summary>
public sealed record MemoNodeSnapshot(
    MemoNodeId Id,
    MemoNodeId? ParentId,
    MemoHeadingLevel HeadingLevel,
    string Title,
    string? Impression,
    string? Summary,
    bool IsPinned,
    long ContentVersion,
    long SummaryVersion,
    int ChildCount,
    int BodyBlockCount
) {
    public bool IsSummaryStale => SummaryVersion < ContentVersion;
}

/// <summary>
/// 节点路径快照。
/// </summary>
public sealed record MemoNodePath(
    IReadOnlyList<MemoNodeId>? NodeIds = null,
    IReadOnlyList<string>? Titles = null
);

/// <summary>
/// 正文块快照。
/// </summary>
public sealed record MemoBodyBlockSnapshot(MemoBlockId Id, string Content);

/// <summary>
/// 搜索请求。
/// </summary>
public sealed record MemoTreeSearchQuery(
    string Text,
    bool SearchTitle = true,
    bool SearchImpression = true,
    bool SearchSummary = true,
    bool SearchBody = true,
    int MaxResults = 20
);

/// <summary>
/// 搜索命中。
/// </summary>
public sealed record MemoTreeSearchHit(
    MemoNodeId NodeId,
    MemoSearchField Field,
    string Snippet,
    MemoNodePath Path,
    int Score
);

/// <summary>
/// Window 渲染请求。
/// </summary>
public sealed record MemoTreeRenderRequest(
    int VisibleCharacterBudget,
    IReadOnlyList<MemoNodeId>? PreferredNodeIds = null,
    IReadOnlyList<MemoNodeId>? ExpandedNodeIds = null,
    bool IncludePinnedNodes = true,
    string? TopicHint = null
);

/// <summary>
/// 单个渲染节点的结果。
/// </summary>
public sealed record MemoTreeRenderedNode(
    MemoNodeId NodeId,
    MemoNodeViewLevel ViewLevel,
    string Markdown,
    bool WasAutoCollapsed
);

/// <summary>
/// Window 渲染结果。
/// </summary>
public sealed record MemoTreeRenderResult(
    string Window,
    int UsedCharacters,
    int BudgetCharacters,
    IReadOnlyList<MemoTreeRenderedNode>? RenderedNodes = null,
    IReadOnlyList<MemoNodeId>? HiddenNodeIds = null
);
