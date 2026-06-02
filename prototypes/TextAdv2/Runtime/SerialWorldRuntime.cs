using System.Diagnostics;
using Atelia.StateJournal;
using Atelia.TextAdv2.Observation;
using Atelia.TextAdv2.ReadModel;
using Atelia.TextAdv2.Routing;
using Atelia.TextAdv2.Spatial;
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

    internal WorldState DurableWorld => _host.DurableWorld;

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
        var spatial = _host.ObserveSpatial();
        return _runtime.ObserveRouteAcceleration(_host.DurableWorld, spatial);
    }

    public LogicalTimeSnapshot ObserveTime() {
        EnsureNotDisposed();
        return _host.ObserveTime();
    }

    public BatchObserveResult ObserveBatch(BatchObserveRequest request) {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(request);

        var spatial = _host.ObserveSpatial();
        var occupancy = _host.ObserveOccupancy();
        return ObserveBatchCore(request.Items, spatial, occupancy);
    }

    public LogicalTimeSnapshot AdvanceTime(long ticks) {
        EnsureNotDisposed();
        return _host.AdvanceTime(ticks);
    }

    public BatchStepResult StepBatch(BatchStepRequest request) {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(request);

        ValidateBatchStepRequest(request);

        // Batch step 明确是“按给定顺序串行执行多个 step”，而不是同时结算。
        // 因为 actor move 不改变 topology，这里可以在整批 step 中复用同一个 spatial snapshot。
        var spatial = _host.ObserveSpatial();
        var stepResults = new BatchStepStepResult[request.Steps.Length];

        for (int i = 0; i < request.Steps.Length; i++) {
            stepResults[i] = ExecuteBatchStep(request.Steps[i]);
        }

        var time = _host.AdvanceTime(request.AdvanceTimeAfterBatchTicks);
        BatchObserveResult? postObservations = null;

        if (request.PostObservations is { Length: > 0 }) {
            postObservations = ObserveBatchCore(request.PostObservations, spatial, _host.ObserveOccupancy());
        }

        return new BatchStepResult {
            Steps = stepResults,
            Time = time,
            PostObservations = postObservations,
        };
    }

    public LocationRoutePlanObservation PlanActorRoute(string actorId, string toLocationId) {
        EnsureNotDisposed();
        var spatial = _host.ObserveSpatial();
        return _host.PlanActorRoute(actorId, toLocationId, spatial, _runtime.GetPlanningOptions(spatial));
    }

    public LocationRoutePlanObservation PlanRoute(string fromLocationId, string toLocationId) {
        EnsureNotDisposed();
        var spatial = _host.ObserveSpatial();
        return _host.PlanRoute(fromLocationId, toLocationId, spatial, _runtime.GetPlanningOptions(spatial));
    }

    public RouteAccelerationSnapshot RebuildRouteAcceleration(string requestedLandmarks) {
        EnsureNotDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedLandmarks);
        return RebuildRouteAccelerationCore(ParseExplicitLandmarkLocationIds(requestedLandmarks), "custom");
    }

    internal RouteAccelerationSnapshot RebuildRouteAcceleration(
        IEnumerable<string> landmarkLocationIds,
        string landmarkProfileName
    ) {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(landmarkLocationIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(landmarkProfileName);
        return RebuildRouteAccelerationCore(landmarkLocationIds, landmarkProfileName);
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
        _runtime.RecordMovement(movement.ToHistoryEntry(), movement.ActorId);
        return movement.ToResult(_host.ObserveLocation(movement.ToLocationId));
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

    internal string RenderWorldDump() {
        EnsureNotDisposed();
        return WorldDumpRenderer.Render(_host.DurableWorld);
    }

    internal string RenderLocationDump(string locationId) {
        EnsureNotDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(locationId);
        return WorldDumpRenderer.RenderLocation(_host.DurableWorld, locationId);
    }

    private RouteAccelerationSnapshot RebuildRouteAccelerationCore(
        IEnumerable<string> landmarkLocationIds,
        string landmarkProfileName
    ) {
        var spatial = _host.ObserveSpatial();
        return _runtime.RebuildRouteAcceleration(_host.DurableWorld, spatial, landmarkLocationIds, landmarkProfileName);
    }

    private BatchObserveResult ObserveBatchCore(
        BatchObserveItem[]? items,
        WorldSpatialSnapshot spatial,
        ActorOccupancyIndex occupancy
    ) {
        ArgumentNullException.ThrowIfNull(spatial);
        ArgumentNullException.ThrowIfNull(occupancy);

        if (items is null) {
            throw new ArgumentException("Batch observe requires a non-null items array.", nameof(items));
        }

        for (int i = 0; i < items.Length; i++) {
            ValidateBatchObserveItemShape(items[i]);
        }

        var results = new BatchObserveResultItem[items.Length];
        for (int i = 0; i < items.Length; i++) {
            results[i] = ObserveBatchItem(items[i], spatial, occupancy);
        }

        return new BatchObserveResult {
            Items = results,
        };
    }

    private BatchObserveResultItem ObserveBatchItem(
        BatchObserveItem item,
        WorldSpatialSnapshot spatial,
        ActorOccupancyIndex occupancy
    ) {
        ValidateBatchObserveItemShape(item);

        var kind = NormalizeObserveKind(item.Kind);

        try {
            return kind switch {
                "actor" => new BatchObserveResultItem {
                    RequestId = item.RequestId,
                    Kind = kind,
                    Actor = LocationObservationProjector.ObserveActorLocation(
                        _host.DurableWorld,
                        spatial,
                        occupancy,
                        item.ActorId!
                    ),
                },
                "actor-context" => new BatchObserveResultItem {
                    RequestId = item.RequestId,
                    Kind = kind,
                    ActorContext = ActorContextObservationProjector.ObserveActorContext(
                        _host.DurableWorld,
                        spatial,
                        occupancy,
                        item.ActorId!
                    ),
                },
                "actor-navigation" => new BatchObserveResultItem {
                    RequestId = item.RequestId,
                    Kind = kind,
                    ActorNavigation = NavigationObservationProjector.ObserveActorNavigation(
                        _host.DurableWorld,
                        spatial,
                        item.ActorId!
                    ),
                },
                "location" => new BatchObserveResultItem {
                    RequestId = item.RequestId,
                    Kind = kind,
                    Location = LocationObservationProjector.ObserveLocation(
                        _host.DurableWorld,
                        spatial,
                        occupancy,
                        item.LocationId!
                    ),
                },
                "location-navigation" => new BatchObserveResultItem {
                    RequestId = item.RequestId,
                    Kind = kind,
                    LocationNavigation = NavigationObservationProjector.ObserveLocationNavigation(
                        _host.DurableWorld,
                        spatial,
                        item.LocationId!
                    ),
                },
                "time" => new BatchObserveResultItem {
                    RequestId = item.RequestId,
                    Kind = kind,
                    Time = _host.ObserveTime(),
                },
                _ => throw new UnreachableException($"Unexpected batch observe kind '{kind}'."),
            };
        }
        catch (ArgumentException ex) {
            return CreateObserveError(item.RequestId, kind, ex.Message);
        }
        catch (InvalidOperationException ex) {
            return CreateObserveError(item.RequestId, kind, ex.Message);
        }
    }

    private BatchStepStepResult ExecuteBatchStep(BatchStepCommand step) {
        ValidateBatchStepCommand(step);

        try {
            return new BatchStepStepResult {
                RequestId = step.RequestId,
                ActorId = step.ActorId,
                PassageId = step.PassageId,
                Move = MoveActor(step.ActorId, step.PassageId),
            };
        }
        catch (ArgumentException ex) {
            return CreateStepError(step, ex.Message);
        }
        catch (InvalidOperationException ex) {
            return CreateStepError(step, ex.Message);
        }
    }

    private static BatchObserveResultItem CreateObserveError(string requestId, string kind, string message)
        => new() {
            RequestId = requestId,
            Kind = kind,
            Error = new BatchRuntimeError(message),
        };

    private static BatchStepStepResult CreateStepError(BatchStepCommand step, string message)
        => new() {
            RequestId = step.RequestId,
            ActorId = step.ActorId,
            PassageId = step.PassageId,
            Error = new BatchRuntimeError(message),
        };

    private static void ValidateBatchStepRequest(BatchStepRequest request) {
        if (request.Steps is null) {
            throw new ArgumentException("Batch step requires a non-null steps array.", nameof(request));
        }

        for (int i = 0; i < request.Steps.Length; i++) {
            ValidateBatchStepCommand(request.Steps[i]);
        }

        ArgumentOutOfRangeException.ThrowIfNegative(request.AdvanceTimeAfterBatchTicks);

        if (request.PostObservations is null) {
            return;
        }

        for (int i = 0; i < request.PostObservations.Length; i++) {
            ValidateBatchObserveItemShape(request.PostObservations[i]);
        }
    }

    private static void ValidateBatchStepCommand(BatchStepCommand step) {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentException.ThrowIfNullOrWhiteSpace(step.RequestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(step.ActorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(step.PassageId);
    }

    private static void ValidateBatchObserveItemShape(BatchObserveItem item) {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentException.ThrowIfNullOrWhiteSpace(item.RequestId);

        string kind = NormalizeObserveKind(item.Kind);
        if (RequiresActorId(kind)) {
            ArgumentException.ThrowIfNullOrWhiteSpace(item.ActorId);
        }

        if (RequiresLocationId(kind)) {
            ArgumentException.ThrowIfNullOrWhiteSpace(item.LocationId);
        }
    }

    private static string NormalizeObserveKind(string kind) {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);

        return kind.Trim().ToLowerInvariant() switch {
            "actor" => "actor",
            "actor-context" => "actor-context",
            "actor-navigation" => "actor-navigation",
            "location" => "location",
            "location-navigation" => "location-navigation",
            "time" => "time",
            _ => throw new InvalidOperationException(
                $"Unsupported batch observe kind '{kind}'. Allowed values: actor, actor-context, actor-navigation, location, location-navigation, time."
            ),
        };
    }

    private static bool RequiresActorId(string kind)
        => kind is "actor" or "actor-context" or "actor-navigation";

    private static bool RequiresLocationId(string kind)
        => kind is "location" or "location-navigation";

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
