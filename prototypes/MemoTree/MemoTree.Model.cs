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
/// 节点上的轻量标签。
/// </summary>
public readonly record struct MemoTag(string Value) {
    public override string ToString() => Value ?? string.Empty;
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
    Gist,
    Summary,
    Body,
    Tags,
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
/// <paramref name="Gist"/> 表示该节点在 <see cref="MemoNodeViewLevel.Gist"/> 下保留的一句话印象。
/// <paramref name="Summary"/> 只概括本节点自己的正文内容，不包含子节点内容。
/// 统一节点既可以同时拥有正文，也可以同时拥有子节点；不区分 directory/file 两类本体。
/// </remarks>
public sealed record MemoNodeSnapshot(
    MemoNodeId Id,
    MemoNodeId? ParentId,
    string Title,
    string? Gist,
    string? Summary,
    IReadOnlyList<MemoTag>? Tags,
    bool IsPinned,
    long BodyVersion,
    long SummaryBodyVersion,
    int ChildCount,
    int BodyBlockCount
) {
    public bool IsSummaryStale => SummaryBodyVersion < BodyVersion;
}

/// <summary>
/// 节点路径快照。
/// </summary>
public sealed record MemoNodePath(IReadOnlyList<MemoNodeId> NodeIds);

/// <summary>
/// 正文块快照。
/// </summary>
public sealed record MemoBodyBlockSnapshot(MemoBlockId Id, string Content);

/// <summary>
/// 一次整段正文重写请求。
/// </summary>
/// <remarks>
/// 这是危险度较高的全量入口。实现可以选择重建正文 block，因此旧 <see cref="MemoBlockId"/> 引用不一定保留。
/// 推荐仅用于初始化、导入、测试夹具或明确接受“整段重写”后果的场景。
/// </remarks>
public sealed record MemoBodyRewriteRequest(
    MemoNodeId NodeId,
    string Text,
    string? Reason = null
);

/// <summary>
/// Window index 区中的一项结构条目。
/// </summary>
public sealed record MemoTreeIndexEntry(
    MemoNodeId NodeId,
    int Depth,
    string Title,
    bool IsPinned = false,
    bool IsExpanded = false
);

/// <summary>
/// Window 的 index 区。
/// </summary>
public sealed record MemoTreeIndexSection(
    string Text,
    IReadOnlyList<MemoTreeIndexEntry>? Entries = null
);

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
    long BasedOnBodyVersion,
    string? Notes = null
);

/// <summary>
/// 节点收起与记忆维护的结果。
/// </summary>
public sealed record MemoNodeCollapseResult(
    MemoNodeId NodeId,
    MemoNodeCollapseLevel TargetLevel,
    long AppliedBodyVersion,
    MemoNodeSnapshot Node
);

/// <summary>
/// 搜索请求。
/// </summary>
public sealed record MemoTreeSearchQuery(
    string Text,
    bool SearchTitle = true,
    bool SearchGist = true,
    bool SearchSummary = true,
    bool SearchBody = true,
    bool SearchTags = true,
    int MaxResults = 20
);

/// <summary>
/// 搜索命中。
/// </summary>
public sealed record MemoTreeSearchHit(
    MemoNodeId NodeId,
    MemoSearchField Field,
    string Snippet,
    MemoNodePath Path
);

/// <summary>
/// Window 渲染请求。
/// </summary>
/// <remarks>
/// 推荐把 Window 渲染成“index + flatten 节点卡片”的纯文本投影，而不是一整篇扁平大文档。
/// </remarks>
public sealed record MemoTreeRenderRequest(
    int VisibleCharacterBudget,
    IReadOnlyList<MemoNodeId>? PreferredNodeIds = null,
    IReadOnlyList<MemoNodeId>? ExpandedNodeIds = null,
    bool IncludePinnedNodes = true,
    string? TopicHint = null
);

/// <summary>
/// Window 中平铺展示的单个节点卡片。
/// </summary>
/// <remarks>
/// 推荐呈现为一个低噪音的节点卡片：标题、gist/summary、状态与必要的子节点骨架。
/// 当节点已出现在 Window 中时，应优先压缩正文 LOD，而不是先丢掉结构骨架。
/// </remarks>
public sealed record MemoTreeNodeCard(
    MemoNodeId NodeId,
    MemoNodePath Path,
    MemoNodeViewLevel ViewLevel,
    string RenderedText,
    bool WasAutoCollapsed
);

/// <summary>
/// Window 渲染结果。
/// </summary>
public sealed record MemoTreeRenderResult(
    string Window,
    int UsedCharacters,
    int BudgetCharacters,
    MemoTreeIndexSection? IndexSection = null,
    IReadOnlyList<MemoTreeNodeCard>? FlattenCards = null,
    IReadOnlyList<MemoNodeId>? HiddenNodeIds = null
);
