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
    Gist,
    Summary,
    Full,
}

/// <summary>
/// 节点从 <c>Full</c> 收起时的目标层级。
/// </summary>
public enum MemoNodeCollapseLevel {
    Summary,
    Gist,
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
/// <remarks>
/// <paramref name="Impression"/> 表示该节点在 <see cref="MemoNodeViewLevel.Gist"/> 下保留的一句话印象。
/// <paramref name="Summary"/> 只概括本节点自己的正文内容，不包含子节点内容。
/// </remarks>
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
/// 一次显式的节点收起与记忆维护请求。
/// </summary>
/// <remarks>
/// 推荐语义是：调用方刚看过该节点的 <c>Full</c> 内容，现在准备把它收起到较低 LOD，
/// 因而顺手提交新的 <c>Gist</c> 与 <c>Summary</c>。
/// </remarks>
public sealed record MemoNodeCollapseRequest(
    MemoNodeId NodeId,
    MemoNodeCollapseLevel TargetLevel,
    string Gist,
    string Summary,
    long BasedOnContentVersion,
    string? Notes = null
);

/// <summary>
/// 节点收起与记忆维护的结果。
/// </summary>
public sealed record MemoNodeCollapseResult(
    MemoNodeId NodeId,
    MemoNodeCollapseLevel TargetLevel,
    long AppliedContentVersion,
    MemoNodeSnapshot Node
);

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
/// <remarks>
/// 当节点已出现在 Window 中时，推荐实现优先压缩正文 LOD，而不是先丢掉结构骨架。
/// </remarks>
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
