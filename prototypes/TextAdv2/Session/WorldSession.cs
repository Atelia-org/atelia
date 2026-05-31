using Atelia.StateJournal;
using Atelia.TextAdv2.ReadOnlyView;
using Atelia.TextAdv2.WorldTruth;

namespace Atelia.TextAdv2.Session;

/// <summary>
/// TextAdv2 的第一版 session 会话对象。
///
/// 当前阶段它统一持有：
/// - Repository / WorldState 生命周期；
/// - session-owned logical time（进程内易失）；
/// - session-owned movement history（进程内易失）；
/// - session-owned route acceleration snapshot；
/// - 对 typed session API 的直接编排。
///
/// 它故意先保持为单线程、单世界、单会话模型，优先把业务编排从入口程序中抽出，
/// 后续再在此基础上演进到真正的 authoritative GameServer session。
/// </summary>
public sealed class WorldSession : IDisposable {
    private const string MainBranchName = "main";

    private readonly Repository _repo;
    private readonly WorldState _world;
    private readonly WorldSessionOptions _options;
    private readonly RouteAccelerationCache _routeAcceleration = new();
    private readonly Dictionary<string, List<ActorMovementHistoryEntry>> _movementHistoryByActor = new(StringComparer.Ordinal);
    private long _logicalTick;
    private bool _disposed;

    private WorldSession(string repoDir, Repository repo, WorldState world, WorldSessionOptions? options) {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoDir);
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(world);

        RepoDir = repoDir;
        _repo = repo;
        _world = world;
        _options = options ?? WorldSessionOptions.Default;
    }

    public string RepoDir { get; }

    internal WorldState WorldForDevSupport {
        get {
            EnsureNotDisposed();
            return _world;
        }
    }

    internal static WorldSession CreateNew(
        string repoDir,
        Func<Revision, WorldState> worldFactory,
        WorldSessionOptions? options = null
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoDir);
        ArgumentNullException.ThrowIfNull(worldFactory);

        if (Directory.Exists(repoDir) && Directory.EnumerateFileSystemEntries(repoDir).Any()) {
            throw new InvalidOperationException(
                $"Repository directory '{repoDir}' already exists and is not empty. Use OpenExisting or a dev bootstrap open-or-create flow instead."
            );
        }

        var repo = Repository.Create(repoDir).Unwrap();
        try {
            var revision = repo.CreateBranch(MainBranchName).Unwrap();
            var world = worldFactory(revision);
            repo.Commit(world.Root).Unwrap();
            return new WorldSession(repoDir, repo, world, options);
        }
        catch {
            repo.Dispose();
            throw;
        }
    }

    public static WorldSession OpenExisting(string repoDir)
        => OpenExisting(repoDir, options: null);

    internal static WorldSession OpenExisting(string repoDir, WorldSessionOptions? options) {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoDir);

        var repo = Repository.Open(repoDir).Unwrap();
        try {
            var revision = repo.CheckoutBranch(MainBranchName).Unwrap();
            var world = LoadWorldState(revision);
            return new WorldSession(repoDir, repo, world, options);
        }
        catch {
            repo.Dispose();
            throw;
        }
    }

    public LocationSnapshot ObserveLocation(string locationId) {
        EnsureNotDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(locationId);
        return SessionSnapshotProjector.ProjectLocation(_world, locationId);
    }

    public ActorSnapshot ObserveActor(string actorId) {
        EnsureNotDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        return SessionSnapshotProjector.ProjectActor(_world, actorId);
    }

    public LocationNavigationSnapshot ObserveNavigation(string locationId) {
        EnsureNotDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(locationId);
        return SessionSnapshotProjector.ProjectNavigation(_world, locationId);
    }

    public ActorNavigationSnapshot ObserveActorNavigation(string actorId) {
        EnsureNotDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        return SessionSnapshotProjector.ProjectActorNavigation(_world, actorId);
    }

    public RouteAccelerationSnapshot ObserveRouteAcceleration() {
        EnsureNotDisposed();
        return _routeAcceleration.Observe(_world);
    }

    public LogicalTimeSnapshot ObserveTime() {
        EnsureNotDisposed();
        return ObserveLogicalTime();
    }

    public LogicalTimeSnapshot AdvanceTime(long ticks) {
        EnsureNotDisposed();
        ArgumentOutOfRangeException.ThrowIfNegative(ticks);
        return AdvanceLogicalTime(ticks);
    }

    public RoutePlan PlanActorRoute(string actorId, string toLocationId) {
        EnsureNotDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toLocationId);

        return SessionRoutePlanProjector.Project(
            LocationRoutePlanner.PlanShortestRouteForActor(
                _world,
                actorId,
                toLocationId,
                _routeAcceleration.GetPlanningOptions(_world)
            )
        );
    }

    public RoutePlan PlanRoute(string fromLocationId, string toLocationId) {
        EnsureNotDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(fromLocationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toLocationId);

        return SessionRoutePlanProjector.Project(
            LocationRoutePlanner.PlanShortestRoute(
                _world,
                fromLocationId,
                toLocationId,
                _routeAcceleration.GetPlanningOptions(_world)
            )
        );
    }

    public RouteAccelerationSnapshot RebuildRouteAcceleration(string requestedLandmarks) {
        EnsureNotDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedLandmarks);
        return RebuildRouteAccelerationCore(ParseExplicitLandmarkLocationIds(requestedLandmarks), "custom");
    }

    public ActorRouteTrace TraceActorRoute(string actorId) {
        EnsureNotDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);

        return SessionRouteTraceProjector.Project(
            ActorRouteTraceProjector.ObserveActorRouteTrace(
                _world,
                actorId,
                GetMovementHistory(actorId)
            )
        );
    }

    public ActorMoveResult MoveActor(string actorId, string passageId) {
        EnsureNotDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(passageId);

        return SessionSnapshotProjector.ProjectMovement(MoveActorCore(actorId, passageId));
    }

    public void Dispose() {
        if (_disposed) { return; }

        _repo.Dispose();
        _disposed = true;
    }

    private static WorldState LoadWorldState(Revision revision) {
        ArgumentNullException.ThrowIfNull(revision);
        return WorldState.FromRoot(revision.GetGraphRoot<DurableDict<string>>().Unwrap());
    }

    private ActorMovementObservation MoveActorCore(string actorId, string passageId) {
        var actor = _world.GetActor(actorId);
        var fromLocation = _world.GetLocation(actor.CurrentLocationId);
        var passage = _world.GetPassage(passageId);
        var exit = passage.GetEndpointFor(fromLocation.Id);
        var direction = passage.GetDirectionFrom(fromLocation.Id);
        var toLocation = _world.GetLocation(passage.GetOtherLocationId(fromLocation.Id));

        _world.MoveActorAlongPassage(actorId, passageId);
        _repo.Commit(_world.Root).Unwrap();

        var historyEntry = new ActorMovementHistoryEntry(
            passage.Id,
            exit.ExitName,
            fromLocation.Id,
            fromLocation.Name,
            toLocation.Id,
            toLocation.Name,
            passage.TravelMode,
            direction.TotalTravelCost(passage)
        );
        GetOrCreateMovementHistory(actorId).Add(historyEntry);

        var currentObservation = LocationObservationProjector.ObserveActorLocation(_world, actorId);
        return new ActorMovementObservation(
            currentObservation.ActorId,
            currentObservation.ActorName,
            historyEntry.PassageId,
            historyEntry.ExitName,
            historyEntry.FromLocationId,
            historyEntry.FromLocationName,
            historyEntry.ToLocationId,
            historyEntry.ToLocationName,
            historyEntry.TravelMode,
            historyEntry.TravelCost,
            currentObservation.Location
        );
    }

    private LogicalTimeSnapshot ObserveLogicalTime() => new(_logicalTick);

    internal RouteAccelerationSnapshot RebuildRouteAcceleration(
        IEnumerable<string> landmarkLocationIds,
        string landmarkProfileName
    ) {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(landmarkLocationIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(landmarkProfileName);
        return RebuildRouteAccelerationCore(landmarkLocationIds, landmarkProfileName);
    }

    private LogicalTimeSnapshot AdvanceLogicalTime(long ticks) {
        _logicalTick = checked(_logicalTick + ticks);
        return ObserveLogicalTime();
    }

    private IReadOnlyList<ActorMovementHistoryEntry> GetMovementHistory(string actorId)
        => _movementHistoryByActor.TryGetValue(actorId, out var history) ? history : [];

    private List<ActorMovementHistoryEntry> GetOrCreateMovementHistory(string actorId) {
        if (!_movementHistoryByActor.TryGetValue(actorId, out var history)) {
            history = [];
            _movementHistoryByActor.Add(actorId, history);
        }

        return history;
    }

    internal LandmarkProfile? ResolveLandmarkProfile() {
        EnsureNotDisposed();
        return _options.LandmarkProfileResolver?.Invoke(_world);
    }

    private RouteAccelerationSnapshot RebuildRouteAccelerationCore(
        IEnumerable<string> landmarkLocationIds,
        string landmarkProfileName
    ) {
        return _routeAcceleration.Rebuild(_world, landmarkLocationIds, landmarkProfileName);
    }

    private static string[] ParseExplicitLandmarkLocationIds(string value) {
        var landmarkLocationIds = value
            .Split([',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToArray();

        if (landmarkLocationIds.Length == 0) { throw new InvalidOperationException("RebuildRouteAcceleration requires a comma- or semicolon-separated landmark list."); }

        return landmarkLocationIds;
    }
    private void EnsureNotDisposed() {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
