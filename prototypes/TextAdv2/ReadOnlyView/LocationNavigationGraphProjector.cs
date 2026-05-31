using Atelia.TextAdv2.WorldTruth;

namespace Atelia.TextAdv2.ReadOnlyView;

/// <summary>
/// 派生 planner、heuristic 与 graph signature 共享的 canonical location navigation graph seam。
/// </summary>
internal static class LocationNavigationGraphProjector {
    public static LocationNavigationGraph Project(WorldState world, string locationId) {
        ArgumentNullException.ThrowIfNull(world);
        WorldState.ValidateEntityId(locationId, nameof(locationId));

        _ = world.GetLocation(locationId);
        var edges = world.EnumeratePassagesTouching(locationId)
            .Select(passage => TryProjectEdge(locationId, passage))
            .Where(edge => edge is not null)
            .Select(edge => edge!)
            .OrderBy(edge => edge.TargetLocationId, StringComparer.Ordinal)
            .ThenBy(edge => edge.PassageId, StringComparer.Ordinal)
            .ThenBy(edge => edge.TravelCost, Comparer<int>.Default)
            .ToArray();

        return new LocationNavigationGraph(locationId, edges);
    }

    private static LocationNavigationGraphEdge? TryProjectEdge(string locationId, PassageView passage) {
        var direction = passage.GetDirectionFrom(locationId);
        if (!direction.IsEnabled) { return null; }

        return new LocationNavigationGraphEdge(
            passage.Id,
            passage.GetOtherLocationId(locationId),
            passage.TravelMode,
            direction.TotalTravelCost(passage)
        );
    }
}
