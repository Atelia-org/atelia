using Atelia.TextAdv2.Routing;
using Atelia.TextAdv2.Spatial;
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
        var spatial = WorldSpatialSnapshotBuilder.Build(world);
        return Observe(world, spatial);
    }

    public RouteAccelerationSnapshot Observe(WorldState world, WorldSpatialSnapshot spatial) {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(spatial);
        return ObserveCore(world, spatial);
    }

    public RouteAccelerationSnapshot Rebuild(
        WorldState world,
        IEnumerable<string> landmarkLocationIds,
        string landmarkProfileName = "custom"
    ) {
        ArgumentNullException.ThrowIfNull(world);
        var spatial = WorldSpatialSnapshotBuilder.Build(world);
        return Rebuild(world, spatial, landmarkLocationIds, landmarkProfileName);
    }

    public RouteAccelerationSnapshot Rebuild(
        WorldState world,
        WorldSpatialSnapshot spatial,
        IEnumerable<string> landmarkLocationIds,
        string landmarkProfileName = "custom"
    ) {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(spatial);
        ArgumentNullException.ThrowIfNull(landmarkLocationIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(landmarkProfileName);

        _landmarkLocationIds = landmarkLocationIds
            .Select(landmarkLocationId => landmarkLocationId.Trim())
            .Where(landmarkLocationId => !string.IsNullOrWhiteSpace(landmarkLocationId))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(landmarkLocationId => landmarkLocationId, StringComparer.Ordinal)
            .ToArray();

        if (_landmarkLocationIds.Length == 0) { throw new InvalidOperationException("RebuildRouteAcceleration requires at least one landmark location ID."); }

        _landmarkSnapshot = LocationLandmarkHeuristicSnapshot.Create(world, spatial, _landmarkLocationIds);
        _landmarkProfileName = landmarkProfileName;
        _planningOptions = new LocationRoutePlanningOptions(_landmarkSnapshot);
        _graphSignature = LocationNavigationGraphSignature.Build(spatial);
        return ObserveCore(world, spatial);
    }

    public LocationRoutePlanningOptions? GetPlanningOptions(WorldState world) {
        ArgumentNullException.ThrowIfNull(world);
        var spatial = WorldSpatialSnapshotBuilder.Build(world);
        return GetPlanningOptions(spatial);
    }

    public LocationRoutePlanningOptions? GetPlanningOptions(WorldSpatialSnapshot spatial) {
        ArgumentNullException.ThrowIfNull(spatial);
        return GetPlanningOptions(GetSnapshotStatus(spatial));
    }

    private RouteAccelerationSnapshot ObserveCore(WorldState world, WorldSpatialSnapshot spatial) {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(spatial);

        string snapshotStatus = GetSnapshotStatus(spatial);
        var effectivePlanningOptions = GetPlanningOptions(snapshotStatus);

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

    public void Clear() {
        _graphSignature = null;
        _landmarkProfileName = NoneLandmarkProfileName;
        _landmarkSnapshot = null;
        _planningOptions = null;
        _landmarkLocationIds = [];
    }

    private LocationRoutePlanningOptions? GetPlanningOptions(string snapshotStatus)
        => string.Equals(snapshotStatus, "active", StringComparison.Ordinal) ? _planningOptions : null;

    private string GetSnapshotStatus(WorldSpatialSnapshot spatial) {
        ArgumentNullException.ThrowIfNull(spatial);
        if (_planningOptions is null || _graphSignature is null) { return "inactive"; }

        return string.Equals(_graphSignature, LocationNavigationGraphSignature.Build(spatial), StringComparison.Ordinal)
            ? "active"
            : "stale";
    }
}
