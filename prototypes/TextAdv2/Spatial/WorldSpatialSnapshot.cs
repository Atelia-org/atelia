using Atelia.TextAdv2.WorldTruth;

namespace Atelia.TextAdv2.Spatial;

/// <summary>
/// 从 WorldState 一次性派生的按 location 索引的方向化邻接视图。
///
/// 它是只读派生结构，不是 durable truth。
/// 它是 observation、planner、heuristic、authoring 校验共享的 canonical spatial seam。
///
/// 设计原则：
/// - 输入：WorldState
/// - 输出：按 LocationId 组织的方向化邻接结果
/// - 性质：只读派生、可重建、非 durable、非 authoring DTO、非 host contract
/// </summary>
internal sealed record WorldSpatialSnapshot(
    IReadOnlyDictionary<string, LocationAdjacencySnapshot> Locations
);

/// <summary>
/// 从某个 location 看出去的所有方向化邻接边。
///
/// 即使该地点没有任何 passage，也会有一条 Edges 为空数组的记录。
/// </summary>
internal sealed record LocationAdjacencySnapshot(
    string LocationId,
    LocationAdjacencyEdge[] Edges
);

/// <summary>
/// 从某 location 看出去的一条 direction-ready adjacency edge。
///
/// 字段设计采用"足够丰富的一次派生"策略：
/// - 从 rich edge 投影成轻量 edge 很容易（过滤 IsEnabled，取子集字段）
/// - 反过来从轻量 edge 恢复 rich observation 往往又要回 world 反查
///
/// 因此 canonical seam 偏"足够一次派生、所有上层复用"，而不是"极瘦导致各层反复补查"。
///
/// 它回答的唯一问题是：
/// 当前 world truth 下，location graph 的方向化边语义是什么。
///
/// 它不承载：地点描述文案本体、actor presence、route-plan result、session movement history。
/// </summary>
internal sealed record LocationAdjacencyEdge(
    string PassageId,
    string FromLocationId,
    string ToLocationId,
    string ExitName,
    TravelMode TravelMode,
    int BaseTravelCost,
    int TravelCostModifier,
    int TotalTravelCost,
    string SharedConditionNote,
    string DirectionConditionNote,
    string LocalViewNote,
    bool IsEnabled
);
