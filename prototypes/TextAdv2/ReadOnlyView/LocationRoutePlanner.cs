using Atelia.TextAdv2.WorldTruth;

namespace Atelia.TextAdv2.ReadOnlyView;

/// <summary>
/// 基于 Location 节点 / Passage 有向边 的最短路规划器。
///
/// 当前实现使用 Dijkstra core：
/// - 默认启发函数固定为 0；
/// - 数据面来自 <see cref="NavigationObservationProjector"/>；
/// - 结果对等成本路径采用稳定 tie-break。
///
/// 这让 MVP 先把 correctness、结果语义和文本可检视性钉住，后续若加入 admissible heuristic，再向真正的 A* 推进。
/// </summary>
internal static class LocationRoutePlanner {
    public static LocationRoutePlanObservation PlanShortestRouteForActor(
        WorldState world,
        string actorId,
        string toLocationId
    ) => PlanShortestRouteForActor(world, actorId, toLocationId, options: null);

    public static LocationRoutePlanObservation PlanShortestRouteForActor(
        WorldState world,
        string actorId,
        string toLocationId,
        LocationRoutePlanningOptions? options
    ) {
        ArgumentNullException.ThrowIfNull(world);
        WorldState.ValidateEntityId(actorId, nameof(actorId));
        WorldState.ValidateEntityId(toLocationId, nameof(toLocationId));

        var actor = world.GetActor(actorId);
        return PlanShortestRoute(world, actor.CurrentLocationId, toLocationId, options);
    }

    public static LocationRoutePlanObservation PlanShortestRoute(
        WorldState world,
        string fromLocationId,
        string toLocationId
    ) => PlanShortestRoute(world, fromLocationId, toLocationId, options: null);

    public static LocationRoutePlanObservation PlanShortestRoute(
        WorldState world,
        string fromLocationId,
        string toLocationId,
        LocationRoutePlanningOptions? options
    ) {
        ArgumentNullException.ThrowIfNull(world);
        WorldState.ValidateEntityId(fromLocationId, nameof(fromLocationId));
        WorldState.ValidateEntityId(toLocationId, nameof(toLocationId));
        var planningOptions = LocationRoutePlanningOptions.Resolve(options);
        var heuristicObservation = planningOptions.Heuristic.Observe();

        var fromLocation = world.GetLocation(fromLocationId);
        var toLocation = world.GetLocation(toLocationId);

        if (string.Equals(fromLocationId, toLocationId, StringComparison.Ordinal)) {
            return new LocationRoutePlanObservation(
                fromLocation.Id,
                fromLocation.Name,
                toLocation.Id,
                toLocation.Name,
                RoutePlanStatus.AlreadyThere,
                0,
                0,
                [],
                CreateSearchStats(heuristicObservation, 0, 0, 0, 0)
            );
        }

        var frontier = new PriorityQueue<SearchState, SearchPriority>();
        int expandedNodeCount = 0;
        int relaxedEdgeCount = 0;
        int staleStateSkipCount = 0;
        int frontierPeakSize = 0;
        var best = new Dictionary<string, BestRouteCandidate>(StringComparer.Ordinal) {
            [fromLocationId] = new BestRouteCandidate(0, string.Empty, PreviousLocationId: null, IncomingEdge: null),
        };
        frontier.Enqueue(
            new SearchState(fromLocationId, 0, string.Empty),
            new SearchPriority(
                EstimateRemainingCost(planningOptions.Heuristic, fromLocationId, toLocationId),
                0,
                fromLocationId,
                string.Empty
            )
        );
        frontierPeakSize = Math.Max(frontierPeakSize, frontier.Count);

        while (frontier.TryDequeue(out var current, out _)) {
            if (!best.TryGetValue(current.LocationId, out var bestForCurrent)
                || current.CostSoFar != bestForCurrent.CostSoFar
                || !string.Equals(current.PathKey, bestForCurrent.PathKey, StringComparison.Ordinal)) {
                staleStateSkipCount++;
                continue;
            }

            expandedNodeCount++;

            if (string.Equals(current.LocationId, toLocationId, StringComparison.Ordinal)) {
                return BuildFoundPlan(
                    world,
                    fromLocation,
                    toLocation,
                    best,
                    CreateSearchStats(heuristicObservation, expandedNodeCount, relaxedEdgeCount, frontierPeakSize, staleStateSkipCount)
                );
            }

            var navigation = NavigationObservationProjector.ObserveLocationNavigation(world, current.LocationId);
            foreach (var edge in navigation.Edges.OrderBy(edge => edge.ExitName, StringComparer.Ordinal)
                         .ThenBy(edge => edge.PassageId, StringComparer.Ordinal)
                         .ThenBy(edge => edge.TargetLocationId, StringComparer.Ordinal)) {
                if (edge.TravelCost < 0) {
                    throw new InvalidOperationException(
                        $"Negative travel cost is not supported for shortest-path planning: passage '{edge.PassageId}' from '{current.LocationId}' has cost {edge.TravelCost}."
                    );
                }

                int newCost = current.CostSoFar + edge.TravelCost;
                string newPathKey = AppendPathKey(current.PathKey, edge);
                if (!ShouldReplace(best, edge.TargetLocationId, newCost, newPathKey)) { continue; }

                best[edge.TargetLocationId] = new BestRouteCandidate(newCost, newPathKey, current.LocationId, edge);
                relaxedEdgeCount++;
                int estimatedRemaining = EstimateRemainingCost(planningOptions.Heuristic, edge.TargetLocationId, toLocationId);
                frontier.Enqueue(
                    new SearchState(edge.TargetLocationId, newCost, newPathKey),
                    new SearchPriority(newCost + estimatedRemaining, newCost, edge.TargetLocationId, newPathKey)
                );
                frontierPeakSize = Math.Max(frontierPeakSize, frontier.Count);
            }
        }

        return new LocationRoutePlanObservation(
            fromLocation.Id,
            fromLocation.Name,
            toLocation.Id,
            toLocation.Name,
            RoutePlanStatus.Unreachable,
            0,
            null,
            [],
            CreateSearchStats(heuristicObservation, expandedNodeCount, relaxedEdgeCount, frontierPeakSize, staleStateSkipCount)
        );
    }

    private static LocationRoutePlanObservation BuildFoundPlan(
        WorldState world,
        Location fromLocation,
        Location toLocation,
        IReadOnlyDictionary<string, BestRouteCandidate> best,
        LocationRoutePlanSearchStatsObservation searchStats
    ) {
        var reversedSegments = new List<RouteSegment>();
        string currentLocationId = toLocation.Id;
        while (!string.Equals(currentLocationId, fromLocation.Id, StringComparison.Ordinal)) {
            var candidate = best[currentLocationId];
            if (candidate.PreviousLocationId is null || candidate.IncomingEdge is null) {
                throw new InvalidOperationException(
                    $"Route reconstruction failed at location '{currentLocationId}' because predecessor information is missing."
                );
            }

            var previousLocation = world.GetLocation(candidate.PreviousLocationId);
            var currentLocation = world.GetLocation(currentLocationId);
            reversedSegments.Add(new RouteSegment(previousLocation, currentLocation, candidate.IncomingEdge));
            currentLocationId = candidate.PreviousLocationId;
        }

        reversedSegments.Reverse();

        int cumulativeCost = 0;
        var steps = new LocationRoutePlanStepObservation[reversedSegments.Count];
        for (int i = 0; i < reversedSegments.Count; i++) {
            var segment = reversedSegments[i];
            cumulativeCost += segment.Edge.TravelCost;
            steps[i] = new LocationRoutePlanStepObservation(
                i + 1,
                segment.Edge.PassageId,
                segment.Edge.ExitName,
                segment.FromLocation.Id,
                segment.FromLocation.Name,
                segment.ToLocation.Id,
                segment.ToLocation.Name,
                segment.Edge.TravelMode,
                segment.Edge.TravelCost,
                cumulativeCost
            );
        }

        return new LocationRoutePlanObservation(
            fromLocation.Id,
            fromLocation.Name,
            toLocation.Id,
            toLocation.Name,
            RoutePlanStatus.Found,
            steps.Length,
            cumulativeCost,
            steps,
            searchStats
        );
    }

    private static LocationRoutePlanSearchStatsObservation CreateSearchStats(
        LocationRouteHeuristicObservation heuristicObservation,
        int expandedNodeCount,
        int relaxedEdgeCount,
        int frontierPeakSize,
        int staleStateSkipCount
    ) {
        ArgumentNullException.ThrowIfNull(heuristicObservation);

        return new LocationRoutePlanSearchStatsObservation(
            heuristicObservation.Name,
            heuristicObservation.LandmarkCount,
            expandedNodeCount,
            relaxedEdgeCount,
            frontierPeakSize,
            staleStateSkipCount
        );
    }

    private static bool ShouldReplace(
        IReadOnlyDictionary<string, BestRouteCandidate> best,
        string locationId,
        int newCost,
        string newPathKey
    ) {
        if (!best.TryGetValue(locationId, out var existing)) { return true; }

        if (newCost < existing.CostSoFar) { return true; }

        return newCost == existing.CostSoFar
            && string.CompareOrdinal(newPathKey, existing.PathKey) < 0;
    }

    private static string AppendPathKey(string pathKey, NavigationEdgeObservation edge) {
        string segment = $"{edge.ExitName}|{edge.PassageId}|{edge.TargetLocationId}";
        return string.IsNullOrEmpty(pathKey) ? segment : $"{pathKey}>{segment}";
    }

    private static int EstimateRemainingCost(
        ILocationRouteHeuristic heuristic,
        string currentLocationId,
        string targetLocationId
    ) {
        ArgumentNullException.ThrowIfNull(heuristic);

        int estimate = heuristic.EstimateRemainingCost(currentLocationId, targetLocationId);
        if (estimate < 0) {
            throw new InvalidOperationException(
                $"Route heuristic returned a negative estimate for '{currentLocationId}' -> '{targetLocationId}': {estimate}."
            );
        }

        return estimate;
    }

    private sealed record SearchState(string LocationId, int CostSoFar, string PathKey);

    private sealed record BestRouteCandidate(
        int CostSoFar,
        string PathKey,
        string? PreviousLocationId,
        NavigationEdgeObservation? IncomingEdge
    );

    private sealed record RouteSegment(Location FromLocation, Location ToLocation, NavigationEdgeObservation Edge);

    private readonly record struct SearchPriority(int EstimatedCost, int CostSoFar, string LocationId, string PathKey)
        : IComparable<SearchPriority> {
        public int CompareTo(SearchPriority other) {
            int byEstimated = EstimatedCost.CompareTo(other.EstimatedCost);
            if (byEstimated != 0) { return byEstimated; }

            int byCost = CostSoFar.CompareTo(other.CostSoFar);
            if (byCost != 0) { return byCost; }

            int byLocation = string.CompareOrdinal(LocationId, other.LocationId);
            if (byLocation != 0) { return byLocation; }

            return string.CompareOrdinal(PathKey, other.PathKey);
        }
    }
}
