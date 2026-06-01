using Atelia.StateJournal;
using Atelia.TextAdv2.Observation;
using Atelia.TextAdv2.Routing;
using Atelia.TextAdv2.WorldTruth;

namespace Atelia.TextAdv2.Runtime;

/// <summary>
/// TextAdv2 当前单世界串行模型下的 public runtime façade。
///
/// 它保持单线程、单世界、单 runtime 模型，对外继续提供稳定的 typed API，
/// 但内部已明确拆分为 durable world host 与进程内 runtime state。
/// </summary>
public sealed class SerialWorldRuntime : IDisposable {
    private readonly WorldHost _host;
    private readonly WorldRuntime _runtime;
    private bool _disposed;

    private SerialWorldRuntime(WorldHost host, WorldRuntime runtime) {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(runtime);

        _host = host;
        _runtime = runtime;
    }

    public string RepoDir {
        get {
            EnsureNotDisposed();
            return _host.RepoDir;
        }
    }

    internal WorldHost Host {
        get {
            EnsureNotDisposed();
            return _host;
        }
    }

    internal WorldRuntime Runtime {
        get {
            EnsureNotDisposed();
            return _runtime;
        }
    }

    public static SerialWorldRuntime CreateEmpty(string repoDir)
        => new(WorldHost.CreateEmpty(repoDir), new WorldRuntime());

    internal static SerialWorldRuntime CreateNew(
        string repoDir,
        Func<Revision, WorldState> worldFactory
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoDir);
        ArgumentNullException.ThrowIfNull(worldFactory);

        return new SerialWorldRuntime(
            WorldHost.CreateNew(repoDir, worldFactory),
            new WorldRuntime()
        );
    }

    public static SerialWorldRuntime OpenExisting(string repoDir)
        => new(WorldHost.OpenExisting(repoDir), new WorldRuntime());

    public LocationObservation ObserveLocation(string locationId) {
        EnsureNotDisposed();
        return _host.ObserveLocation(locationId);
    }

    public LocationAuthoringSnapshot CreateLocation(string id, string name, string description) {
        EnsureNotDisposed();
        return _host.CreateLocation(id, name, description);
    }

    public ActorLocationObservation ObserveActor(string actorId) {
        EnsureNotDisposed();
        return _host.ObserveActor(actorId);
    }

    public ActorContextObservation ObserveActorContext(string actorId) {
        EnsureNotDisposed();
        return _host.ObserveActorContext(actorId);
    }

    public ActorAuthoringSnapshot CreateActor(string id, string name, string currentLocationId) {
        EnsureNotDisposed();
        return _host.CreateActor(id, name, currentLocationId);
    }

    public LocationNavigationObservation ObserveNavigation(string locationId) {
        EnsureNotDisposed();
        return _host.ObserveNavigation(locationId);
    }

    public ActorNavigationObservation ObserveActorNavigation(string actorId) {
        EnsureNotDisposed();
        return _host.ObserveActorNavigation(actorId);
    }

    public RouteAccelerationSnapshot ObserveRouteAcceleration() {
        EnsureNotDisposed();
        return _runtime.ObserveRouteAcceleration(_host.DurableWorld);
    }

    public LogicalTimeSnapshot ObserveTime() {
        EnsureNotDisposed();
        return _host.ObserveTime();
    }

    public LogicalTimeSnapshot AdvanceTime(long ticks) {
        EnsureNotDisposed();
        return _host.AdvanceTime(ticks);
    }

    public LocationRoutePlanObservation PlanActorRoute(string actorId, string toLocationId) {
        EnsureNotDisposed();
        return _host.PlanActorRoute(actorId, toLocationId, _runtime.GetPlanningOptions(_host.DurableWorld));
    }

    public LocationRoutePlanObservation PlanRoute(string fromLocationId, string toLocationId) {
        EnsureNotDisposed();
        return _host.PlanRoute(fromLocationId, toLocationId, _runtime.GetPlanningOptions(_host.DurableWorld));
    }

    public RouteAccelerationSnapshot RebuildRouteAcceleration(string requestedLandmarks) {
        EnsureNotDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedLandmarks);
        return RebuildRouteAccelerationCore(ParseExplicitLandmarkLocationIds(requestedLandmarks), "custom");
    }

    public ActorRuntimeRouteTrace TraceActorRuntimeRoute(string actorId) {
        EnsureNotDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);

        return ActorRuntimeRouteTraceProjector.Project(
            _runtime.ObserveActorRuntimeRouteTrace(_host.DurableWorld, actorId)
        );
    }

    public ActorMoveResult MoveActor(string actorId, string passageId) {
        EnsureNotDisposed();
        var movement = _host.MoveActor(actorId, passageId);
        _runtime.RecordMovement(movement);
        return RuntimeMovementProjector.Project(movement);
    }

    public PassageAuthoringSnapshot CreatePassage(
        string id,
        string locationAId,
        string exitNameFromA,
        string locationBId,
        string exitNameFromB,
        TravelMode travelMode = TravelMode.Land,
        int baseTravelCost = 1
    ) {
        EnsureNotDisposed();
        return _host.CreatePassage(id, locationAId, exitNameFromA, locationBId, exitNameFromB, travelMode, baseTravelCost);
    }

    public PassageAuthoringSnapshot SetPassageTravelMode(string passageId, TravelMode value) {
        EnsureNotDisposed();
        return _host.SetPassageTravelMode(passageId, value);
    }

    public PassageAuthoringSnapshot SetPassageBaseTravelCost(string passageId, int value) {
        EnsureNotDisposed();
        return _host.SetPassageBaseTravelCost(passageId, value);
    }

    public PassageAuthoringSnapshot SetPassageSharedConditionNote(string passageId, string value) {
        EnsureNotDisposed();
        return _host.SetPassageSharedConditionNote(passageId, value);
    }

    public PassageAuthoringSnapshot SetPassageEndpointLocalViewNote(string passageId, string locationId, string value) {
        EnsureNotDisposed();
        return _host.SetPassageEndpointLocalViewNote(passageId, locationId, value);
    }

    public PassageAuthoringSnapshot SetPassageDirectionEnabledFrom(string passageId, string locationId, bool isEnabled) {
        EnsureNotDisposed();
        return _host.SetPassageDirectionEnabledFrom(passageId, locationId, isEnabled);
    }

    public PassageAuthoringSnapshot SetPassageDirectionTravelCostModifierFrom(string passageId, string locationId, int value) {
        EnsureNotDisposed();
        return _host.SetPassageDirectionTravelCostModifierFrom(passageId, locationId, value);
    }

    public PassageAuthoringSnapshot SetPassageDirectionConditionNoteFrom(string passageId, string locationId, string value) {
        EnsureNotDisposed();
        return _host.SetPassageDirectionConditionNoteFrom(passageId, locationId, value);
    }

    public void Dispose() {
        if (_disposed) { return; }

        _host.Dispose();
        _disposed = true;
    }

    private RouteAccelerationSnapshot RebuildRouteAccelerationCore(
        IEnumerable<string> landmarkLocationIds,
        string landmarkProfileName
    ) {
        return _runtime.RebuildRouteAcceleration(_host.DurableWorld, landmarkLocationIds, landmarkProfileName);
    }

    private static string[] ParseExplicitLandmarkLocationIds(string value) {
        var landmarkLocationIds = value
            .Split([',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToArray();

        if (landmarkLocationIds.Length == 0) {
            throw new InvalidOperationException("RebuildRouteAcceleration requires a comma- or semicolon-separated landmark list.");
        }

        return landmarkLocationIds;
    }

    private void EnsureNotDisposed() {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
