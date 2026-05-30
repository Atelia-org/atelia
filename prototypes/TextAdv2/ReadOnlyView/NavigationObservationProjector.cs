using Atelia.TextAdv2.WorldTruth;

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

        var location = world.GetLocation(locationId);
        var graph = ObserveLocationNavigationGraph(world, locationId);
        var edges = graph.Edges
            .Select(edge => {
                var passage = world.GetPassage(edge.PassageId);
                var endpoint = passage.GetEndpointFor(locationId);
                var targetLocation = world.GetLocation(edge.TargetLocationId);

                return new NavigationEdgeObservation(
                    edge.PassageId,
                    endpoint.ExitName,
                    targetLocation.Id,
                    targetLocation.Name,
                    edge.TravelMode,
                    edge.TravelCost
                );
            })
            .OrderBy(edge => edge.ExitName, StringComparer.Ordinal)
            .ThenBy(edge => edge.PassageId, StringComparer.Ordinal)
            .ThenBy(edge => edge.TargetLocationId, StringComparer.Ordinal)
            .ToArray();

        return new LocationNavigationObservation(location.Id, location.Name, edges);
    }

    internal static LocationNavigationGraphObservation ObserveLocationNavigationGraph(WorldTruth.WorldState world, string locationId) {
        ArgumentNullException.ThrowIfNull(world);
        WorldTruth.WorldState.ValidateEntityId(locationId, nameof(locationId));

        _ = world.GetLocation(locationId);
        var edges = world.EnumeratePassagesTouching(locationId)
            .Select(passage => TryProjectGraphEdge(locationId, passage))
            .Where(edge => edge is not null)
            .Select(edge => edge!)
            .OrderBy(edge => edge.TargetLocationId, StringComparer.Ordinal)
            .ThenBy(edge => edge.PassageId, StringComparer.Ordinal)
            .ThenBy(edge => edge.TravelCost, Comparer<int>.Default)
            .ToArray();

        return new LocationNavigationGraphObservation(locationId, edges);
    }

    public static ActorNavigationObservation ObserveActorNavigation(WorldTruth.WorldState world, string actorId) {
        ArgumentNullException.ThrowIfNull(world);
        WorldTruth.WorldState.ValidateEntityId(actorId, nameof(actorId));

        var actor = world.GetActor(actorId);
        var navigation = ObserveLocationNavigation(world, actor.CurrentLocationId);
        return new ActorNavigationObservation(actor.Id, actor.Name, navigation);
    }

    private static NavigationGraphEdgeObservation? TryProjectGraphEdge(string locationId, WorldTruth.Passage passage) {
        var direction = passage.GetDirectionFrom(locationId);
        if (!direction.IsEnabled) { return null; }

        return new NavigationGraphEdgeObservation(
            passage.Id,
            passage.GetOtherLocationId(locationId),
            passage.TravelMode,
            direction.TotalTravelCost(passage)
        );
    }
}
