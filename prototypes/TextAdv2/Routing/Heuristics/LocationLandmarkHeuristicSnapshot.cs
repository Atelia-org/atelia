using Atelia.TextAdv2.WorldTruth;

namespace Atelia.TextAdv2.Routing;

/// <summary>
/// 基于显式 landmark 集合的最短路 lower-bound 快照。
///
/// 它通过预计算每个 landmark 的正向 / 反向最短路距离，提供 directed ALT 形式的 admissible heuristic。
/// 这是一个只读派生结构：世界拓扑或 travel cost 变化后必须重建。
/// </summary>
internal sealed class LocationLandmarkHeuristicSnapshot : ILocationRouteHeuristic {
    private readonly LandmarkDistanceTable[] _tables;

    private LocationLandmarkHeuristicSnapshot(LandmarkDistanceTable[] tables) {
        _tables = tables;
    }

    public static LocationLandmarkHeuristicSnapshot Create(WorldState world, IEnumerable<string> landmarkLocationIds) {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(landmarkLocationIds);

        var landmarkIds = landmarkLocationIds
            .Select(landmarkLocationId => ValidateAndNormalizeLandmark(world, landmarkLocationId))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(landmarkLocationId => landmarkLocationId, StringComparer.Ordinal)
            .ToArray();

        if (landmarkIds.Length == 0) { throw new ArgumentException("At least one landmark location ID is required.", nameof(landmarkLocationIds)); }

        var graph = BuildGraph(world);
        var reverseGraph = BuildReverseGraph(graph);

        var tables = new LandmarkDistanceTable[landmarkIds.Length];
        for (int i = 0; i < landmarkIds.Length; i++) {
            string landmarkId = landmarkIds[i];
            tables[i] = new LandmarkDistanceTable(
                RunDijkstra(graph, landmarkId),
                RunDijkstra(reverseGraph, landmarkId)
            );
        }

        return new LocationLandmarkHeuristicSnapshot(tables);
    }

    public int EstimateRemainingCost(string currentLocationId, string targetLocationId) {
        WorldState.ValidateEntityId(currentLocationId, nameof(currentLocationId));
        WorldState.ValidateEntityId(targetLocationId, nameof(targetLocationId));

        int bestLowerBound = 0;
        foreach (var table in _tables) {
            if (table.FromLandmarkCosts.TryGetValue(targetLocationId, out int landmarkToTarget)
                && table.FromLandmarkCosts.TryGetValue(currentLocationId, out int landmarkToCurrent)) {
                bestLowerBound = Math.Max(bestLowerBound, landmarkToTarget - landmarkToCurrent);
            }

            if (table.ToLandmarkCosts.TryGetValue(currentLocationId, out int currentToLandmark)
                && table.ToLandmarkCosts.TryGetValue(targetLocationId, out int targetToLandmark)) {
                bestLowerBound = Math.Max(bestLowerBound, currentToLandmark - targetToLandmark);
            }
        }

        return Math.Max(bestLowerBound, 0);
    }

    public LocationRouteHeuristicObservation Observe() => new("landmark", _tables.Length);

    private static string ValidateAndNormalizeLandmark(WorldState world, string landmarkLocationId) {
        WorldState.ValidateEntityId(landmarkLocationId, nameof(landmarkLocationId));
        _ = world.GetLocation(landmarkLocationId);
        return landmarkLocationId;
    }

    private static Dictionary<string, GraphEdge[]> BuildGraph(WorldState world) {
        var graph = new Dictionary<string, GraphEdge[]>(StringComparer.Ordinal);
        foreach (var location in world.EnumerateLocations().OrderBy(location => location.Id, StringComparer.Ordinal)) {
            var navigationGraph = LocationNavigationGraphProjector.Project(world, location.Id);
            var edges = navigationGraph.Edges
                .Select(edge => ToGraphEdge(location.Id, edge))
                .ToArray();
            graph.Add(location.Id, edges);
        }

        return graph;
    }

    private static Dictionary<string, GraphEdge[]> BuildReverseGraph(IReadOnlyDictionary<string, GraphEdge[]> graph) {
        var reverseGraph = new Dictionary<string, List<GraphEdge>>(StringComparer.Ordinal);
        foreach (string locationId in graph.Keys.OrderBy(locationId => locationId, StringComparer.Ordinal)) {
            reverseGraph[locationId] = [];
        }

        foreach (var entry in graph.OrderBy(entry => entry.Key, StringComparer.Ordinal)) {
            string sourceLocationId = entry.Key;
            foreach (var edge in entry.Value) {
                if (!reverseGraph.TryGetValue(edge.TargetLocationId, out var incomingEdges)) {
                    incomingEdges = [];
                    reverseGraph[edge.TargetLocationId] = incomingEdges;
                }

                incomingEdges.Add(new GraphEdge(sourceLocationId, edge.TravelCost));
            }
        }

        return reverseGraph.ToDictionary(
            entry => entry.Key,
            entry => entry.Value.OrderBy(edge => edge.TargetLocationId, StringComparer.Ordinal).ToArray(),
            StringComparer.Ordinal
        );
    }

    private static GraphEdge ToGraphEdge(string sourceLocationId, LocationNavigationGraphEdge edge) {
        if (edge.TravelCost < 0) {
            throw new InvalidOperationException(
                $"Negative travel cost is not supported for landmark heuristic precomputation: passage '{edge.PassageId}' from '{sourceLocationId}' has cost {edge.TravelCost}."
            );
        }

        return new GraphEdge(edge.TargetLocationId, edge.TravelCost);
    }

    private static Dictionary<string, int> RunDijkstra(IReadOnlyDictionary<string, GraphEdge[]> graph, string startLocationId) {
        var frontier = new PriorityQueue<string, LandmarkSearchPriority>();
        var best = new Dictionary<string, int>(StringComparer.Ordinal) {
            [startLocationId] = 0,
        };
        frontier.Enqueue(startLocationId, new LandmarkSearchPriority(0, startLocationId));

        while (frontier.TryDequeue(out var currentLocationId, out _)) {
            int currentCost = best[currentLocationId];
            foreach (var edge in graph[currentLocationId]) {
                int newCost = currentCost + edge.TravelCost;
                if (best.TryGetValue(edge.TargetLocationId, out int existingCost) && newCost >= existingCost) { continue; }

                best[edge.TargetLocationId] = newCost;
                frontier.Enqueue(edge.TargetLocationId, new LandmarkSearchPriority(newCost, edge.TargetLocationId));
            }
        }

        return best;
    }

    private readonly record struct GraphEdge(string TargetLocationId, int TravelCost);

    private sealed record LandmarkDistanceTable(
        IReadOnlyDictionary<string, int> FromLandmarkCosts,
        IReadOnlyDictionary<string, int> ToLandmarkCosts
    );

    private readonly record struct LandmarkSearchPriority(int CostSoFar, string LocationId) : IComparable<LandmarkSearchPriority> {
        public int CompareTo(LandmarkSearchPriority other) {
            int byCost = CostSoFar.CompareTo(other.CostSoFar);
            if (byCost != 0) { return byCost; }

            return string.CompareOrdinal(LocationId, other.LocationId);
        }
    }
}
