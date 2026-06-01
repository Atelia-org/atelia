using Atelia.TextAdv2.Spatial;
using Atelia.TextAdv2.WorldTruth;

namespace Atelia.TextAdv2.Observation;

/// <summary>
/// 把更丰富的地点观察投影为更轻量的导航读模型。
///
/// 当前阶段它只暴露已启用的邻接边：
/// - 节点：Location
/// - 边：Passage 的可通行方向
/// - 权重：当前 total travel cost
///
/// 重构后直接从 <see cref="WorldSpatialSnapshot"/> 投影，不再经过 LocationNavigationGraphProjector。
/// ExitName / TravelMode / TotalTravelCost 直接从 spatial edge 取，只有 TargetLocationName 仍从 world 读取。
/// </summary>
internal static class NavigationObservationProjector {
    /// <summary>
    /// 从 spatial seam 投影导航观察。
    /// 这是新的 canonical 路径。调用方若需对同一 world state 做多次导航查询，
    /// 应先 Build 一次 snapshot 再传入本重载，避免重复构建。
    /// </summary>
    public static LocationNavigationObservation ObserveLocationNavigation(
        WorldState world,
        WorldSpatialSnapshot spatial,
        string locationId
    ) {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(spatial);
        WorldState.ValidateEntityId(locationId, nameof(locationId));

        var location = world.GetLocation(locationId);
        var adjacency = spatial.Locations[locationId];
        var edges = adjacency.Edges
            .Where(e => e.IsEnabled)
            .Select(e => {
                var targetLocation = world.GetLocation(e.ToLocationId);
                return new NavigationEdgeObservation(
                    e.PassageId,
                    e.ExitName,
                    e.ToLocationId,
                    targetLocation.Name,
                    e.TravelMode,
                    e.TotalTravelCost
                );
            })
            .OrderBy(e => e.ExitName, StringComparer.Ordinal)
            .ThenBy(e => e.PassageId, StringComparer.Ordinal)
            .ThenBy(e => e.TargetLocationId, StringComparer.Ordinal)
            .ToArray();

        return new LocationNavigationObservation(location.Id, location.Name, edges);
    }

    /// <summary>
    /// [convenience wrapper] 从 WorldState 投影导航观察。
    /// 内部会构建 WorldSpatialSnapshot；若调用方在同一请求内需多次导航查询，
    /// 请改用接受 WorldSpatialSnapshot 的重载以共享同一次构建结果。
    /// </summary>
    public static LocationNavigationObservation ObserveLocationNavigation(WorldState world, string locationId) {
        ArgumentNullException.ThrowIfNull(world);
        var spatial = WorldSpatialSnapshotBuilder.Build(world);
        return ObserveLocationNavigation(world, spatial, locationId);
    }

    /// <summary>
    /// 从 spatial seam 投影 actor 导航观察。
    /// </summary>
    public static ActorNavigationObservation ObserveActorNavigation(
        WorldState world,
        WorldSpatialSnapshot spatial,
        string actorId
    ) {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(spatial);
        WorldState.ValidateEntityId(actorId, nameof(actorId));

        var actor = world.GetActor(actorId);
        var navigation = ObserveLocationNavigation(world, spatial, actor.CurrentLocationId);
        return new ActorNavigationObservation(actor.Id, actor.Name, navigation);
    }

    /// <summary>
    /// [convenience wrapper] 从 WorldState 投影 actor 导航观察。
    /// </summary>
    public static ActorNavigationObservation ObserveActorNavigation(WorldState world, string actorId) {
        ArgumentNullException.ThrowIfNull(world);
        var spatial = WorldSpatialSnapshotBuilder.Build(world);
        return ObserveActorNavigation(world, spatial, actorId);
    }
}
