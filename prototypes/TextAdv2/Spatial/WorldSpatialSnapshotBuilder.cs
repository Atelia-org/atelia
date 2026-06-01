using Atelia.TextAdv2.WorldTruth;

namespace Atelia.TextAdv2.Spatial;

/// <summary>
/// 从 WorldState 构建 WorldSpatialSnapshot 的唯一入口。
///
/// 职责非常单一：
/// - 枚举所有 Location 和 Passage
/// - 为每个 location 构造它自己的有向 adjacency edges
/// - 保证输出排序稳定
///
/// 它不应该做：route planning、heuristic precompute、actor context 拼装、runtime 缓存。
/// </summary>
internal static class WorldSpatialSnapshotBuilder
{
    /// <summary>
    /// 从当前 world truth 构建完整的空间邻接快照。
    ///
    /// 每个已知 location 都会在结果中出现，即使它没有任何 passage。
    /// 边按 (ExitName, PassageId, ToLocationId) 稳定排序。
    /// </summary>
    public static WorldSpatialSnapshot Build(WorldState world)
    {
        ArgumentNullException.ThrowIfNull(world);

        var locations = new Dictionary<string, LocationAdjacencySnapshot>(StringComparer.Ordinal);

        foreach (var location in world.EnumerateLocations().OrderBy(l => l.Id, StringComparer.Ordinal))
        {
            var edges = world.EnumeratePassagesTouching(location.Id)
                .Select(passage => BuildEdge(location.Id, passage))
                .OrderBy(e => e.ExitName, StringComparer.Ordinal)
                .ThenBy(e => e.PassageId, StringComparer.Ordinal)
                .ThenBy(e => e.ToLocationId, StringComparer.Ordinal)
                .ToArray();

            locations.Add(location.Id, new LocationAdjacencySnapshot(location.Id, edges));
        }

        return new WorldSpatialSnapshot(locations);
    }

    private static LocationAdjacencyEdge BuildEdge(string fromLocationId, Passage passage)
    {
        var endpoint = passage.GetEndpointFor(fromLocationId);
        var direction = passage.GetDirectionFrom(fromLocationId);
        var toLocationId = passage.GetOtherLocationId(fromLocationId);

        return new LocationAdjacencyEdge(
            PassageId: passage.Id,
            FromLocationId: fromLocationId,
            ToLocationId: toLocationId,
            ExitName: endpoint.ExitName,
            TravelMode: passage.TravelMode,
            BaseTravelCost: passage.BaseTravelCost,
            TravelCostModifier: direction.TravelCostModifier,
            TotalTravelCost: passage.GetTotalTravelCostFrom(fromLocationId),
            SharedConditionNote: passage.SharedConditionNote,
            DirectionConditionNote: direction.DirectionConditionNote,
            LocalViewNote: endpoint.LocalViewNote,
            IsEnabled: direction.IsEnabled
        );
    }
}
