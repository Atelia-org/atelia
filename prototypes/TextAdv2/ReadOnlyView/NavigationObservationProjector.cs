namespace Atelia.TextAdv2.ReadOnlyView;

/// <summary>
/// 把更丰富的地点观察投影为更轻量的导航读模型。
///
/// 当前阶段它只暴露已启用的邻接边：
/// - 节点：Location
/// - 边：Passage 的可通行方向
/// - 权重：当前 total travel cost
/// </summary>
internal static class NavigationObservationProjector {
    public static LocationNavigationObservation ObserveLocationNavigation(WorldTruth.WorldState world, string locationId) {
        ArgumentNullException.ThrowIfNull(world);
        WorldTruth.WorldState.ValidateEntityId(locationId, nameof(locationId));

        var location = LocationObservationProjector.ObserveLocation(world, locationId);
        var edges = location.Exits
            .Where(exit => exit.IsEnabled)
            .Select(exit => new NavigationEdgeObservation(
                exit.PassageId,
                exit.ExitName,
                exit.TargetLocationId,
                exit.TargetLocationName,
                exit.TravelMode,
                exit.TotalTravelCost
            ))
            .ToArray();

        return new LocationNavigationObservation(location.LocationId, location.LocationName, edges);
    }

    public static ActorNavigationObservation ObserveActorNavigation(WorldTruth.WorldState world, string actorId) {
        ArgumentNullException.ThrowIfNull(world);
        WorldTruth.WorldState.ValidateEntityId(actorId, nameof(actorId));

        var actorObservation = LocationObservationProjector.ObserveActorLocation(world, actorId);
        var navigation = ObserveLocationNavigation(world, actorObservation.Location.LocationId);
        return new ActorNavigationObservation(actorObservation.ActorId, actorObservation.ActorName, navigation);
    }
}
