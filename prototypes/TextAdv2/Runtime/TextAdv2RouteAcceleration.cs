using System.Text;
using Atelia.TextAdv2.AccelerationIndex;
using Atelia.TextAdv2.ReadOnlyView;
using Atelia.TextAdv2.WorldTruth;

namespace Atelia.TextAdv2.Runtime;

public sealed record TextAdv2RouteAccelerationObservation(
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

internal sealed class TextAdv2RouteAccelerationState {
    private const string NoneLandmarkProfileName = "none";

    private string? _graphSignature;
    private string _landmarkProfileName = NoneLandmarkProfileName;
    private LocationLandmarkHeuristicSnapshot? _landmarkSnapshot;
    private LocationRoutePlanningOptions? _planningOptions;
    private string[] _landmarkLocationIds = [];

    public TextAdv2RouteAccelerationObservation Observe(WorldState world) {
        ArgumentNullException.ThrowIfNull(world);

        string snapshotStatus = GetSnapshotStatus(world);
        var effectivePlanningOptions = GetPlanningOptions(world);

        return new TextAdv2RouteAccelerationObservation(
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

    public TextAdv2RouteAccelerationObservation Rebuild(
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
        _graphSignature = BuildNavigationGraphSignature(world);
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

        return string.Equals(_graphSignature, BuildNavigationGraphSignature(world), StringComparison.Ordinal)
            ? "active"
            : "stale";
    }

    private static string BuildNavigationGraphSignature(WorldState world) {
        ArgumentNullException.ThrowIfNull(world);

        var builder = new StringBuilder();
        foreach (var location in world.EnumerateLocations().OrderBy(location => location.Id, StringComparer.Ordinal)) {
            builder.Append("L|").Append(location.Id).AppendLine();

            var navigation = NavigationObservationProjector.ObserveLocationNavigationGraph(world, location.Id);
            foreach (var edge in navigation.Edges) {
                builder.Append("E|")
                    .Append(location.Id)
                    .Append('|')
                    .Append(edge.PassageId)
                    .Append('|')
                    .Append(edge.TargetLocationId)
                    .Append('|')
                    .Append(edge.TravelCost)
                    .AppendLine();
            }
        }

        return builder.ToString();
    }
}
