using Atelia.TextAdv2.Routing;
using Atelia.TextAdv2.WorldTruth;

namespace Atelia.TextAdv2.Runtime;

public sealed record RouteAccelerationSnapshot(
    string PlannerMode,
    string SnapshotStatus,
    string SnapshotKind,
    string LandmarkProfileName,
    bool IsPersistent,
    int LocationCount,
    int PassageCount,
    int LandmarkCount,
    string[] LandmarkLocationIds
);

internal sealed class RouteAccelerationCache {
    private const string NoneLandmarkProfileName = "none";

    private string? _graphSignature;
    private string _landmarkProfileName = NoneLandmarkProfileName;
    private LocationLandmarkHeuristicSnapshot? _landmarkSnapshot;
    private LocationRoutePlanningOptions? _planningOptions;
    private string[] _landmarkLocationIds = [];

    public RouteAccelerationSnapshot Observe(WorldState world) {
        ArgumentNullException.ThrowIfNull(world);

        string snapshotStatus = GetSnapshotStatus(world);
        var effectivePlanningOptions = GetPlanningOptions(world);

        return new RouteAccelerationSnapshot(
            effectivePlanningOptions?.Heuristic.Observe().Name ?? "zero",
            snapshotStatus,
            _planningOptions is null ? "none" : "landmark",
            _planningOptions is null ? NoneLandmarkProfileName : _landmarkProfileName,
            IsPersistent: false,
            LocationCount: world.EnumerateLocations().Count(),
            PassageCount: world.EnumeratePassages().Count(),
            LandmarkCount: _landmarkLocationIds.Length,
            LandmarkLocationIds: [.. _landmarkLocationIds]
        );
    }

    public RouteAccelerationSnapshot Rebuild(
        WorldState world,
        IEnumerable<string> landmarkLocationIds,
        string landmarkProfileName = "custom"
    ) {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(landmarkLocationIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(landmarkProfileName);

        _landmarkLocationIds = landmarkLocationIds
            .Select(landmarkLocationId => landmarkLocationId.Trim())
            .Where(landmarkLocationId => !string.IsNullOrWhiteSpace(landmarkLocationId))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(landmarkLocationId => landmarkLocationId, StringComparer.Ordinal)
            .ToArray();

        if (_landmarkLocationIds.Length == 0) { throw new InvalidOperationException("RebuildRouteAcceleration requires at least one landmark location ID."); }

        _landmarkSnapshot = LocationLandmarkHeuristicSnapshot.Create(world, _landmarkLocationIds);
        _landmarkProfileName = landmarkProfileName;
        _planningOptions = new LocationRoutePlanningOptions(_landmarkSnapshot);
        _graphSignature = LocationNavigationGraphSignature.Build(world);
        return Observe(world);
    }

    public LocationRoutePlanningOptions? GetPlanningOptions(WorldState world) {
        ArgumentNullException.ThrowIfNull(world);
        return string.Equals(GetSnapshotStatus(world), "active", StringComparison.Ordinal) ? _planningOptions : null;
    }

    public void Clear() {
        _graphSignature = null;
        _landmarkProfileName = NoneLandmarkProfileName;
        _landmarkSnapshot = null;
        _planningOptions = null;
        _landmarkLocationIds = [];
    }

    private string GetSnapshotStatus(WorldState world) {
        ArgumentNullException.ThrowIfNull(world);

        if (_planningOptions is null || _graphSignature is null) { return "inactive"; }

        return string.Equals(_graphSignature, LocationNavigationGraphSignature.Build(world), StringComparison.Ordinal)
            ? "active"
            : "stale";
    }
}
