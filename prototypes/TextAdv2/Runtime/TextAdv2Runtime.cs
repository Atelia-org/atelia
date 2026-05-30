using Atelia.StateJournal;
using Atelia.TextAdv2.ReadOnlyView;
using Atelia.TextAdv2.WorldTruth;

namespace Atelia.TextAdv2.Runtime;

/// <summary>
/// TextAdv2 的第一版 runtime 会话对象。
///
/// 当前阶段它统一持有：
/// - Repository / WorldState 生命周期；
/// - runtime-owned logical time（进程内易失）；
/// - runtime-owned movement history（进程内易失）；
/// - runtime-owned route acceleration snapshot；
/// - 对 typed runtime API 的直接编排。
///
/// 它故意先保持为单线程、单世界、单会话模型，优先把业务编排从入口程序中抽出，
/// 后续再在此基础上演进到真正的 authoritative GameServer runtime。
/// </summary>
public sealed class TextAdv2Runtime : IDisposable {
    private const string MainBranchName = "main";

    private readonly Repository _repo;
    private readonly WorldState _world;
    private readonly TextAdv2RuntimeOptions _options;
    private readonly TextAdv2RouteAccelerationState _routeAcceleration = new();
    private readonly Dictionary<string, List<ActorMovementHistoryEntry>> _movementHistoryByActor = new(StringComparer.Ordinal);
    private long _logicalTick;
    private bool _disposed;

    private TextAdv2Runtime(string repoDir, Repository repo, WorldState world, TextAdv2RuntimeOptions? options) {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoDir);
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(world);

        RepoDir = repoDir;
        _repo = repo;
        _world = world;
        _options = options ?? TextAdv2RuntimeOptions.Default;
    }

    public string RepoDir { get; }

    internal static TextAdv2Runtime CreateNew(
        string repoDir,
        Func<Revision, WorldState> worldFactory,
        TextAdv2RuntimeOptions? options = null
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
            return new TextAdv2Runtime(repoDir, repo, world, options);
        }
        catch {
            repo.Dispose();
            throw;
        }
    }

    public static TextAdv2Runtime OpenExisting(string repoDir)
        => OpenExisting(repoDir, options: null);

    internal static TextAdv2Runtime OpenExisting(string repoDir, TextAdv2RuntimeOptions? options) {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoDir);

        var repo = Repository.Open(repoDir).Unwrap();
        try {
            var revision = repo.CheckoutBranch(MainBranchName).Unwrap();
            var world = LoadWorldState(revision);
            return new TextAdv2Runtime(repoDir, repo, world, options);
        }
        catch {
            repo.Dispose();
            throw;
        }
    }

    public TextAdv2RuntimeCommandResult DumpWorld() {
        EnsureNotDisposed();
        return Text(WorldDumpRenderer.Render(_world));
    }

    public TextAdv2RuntimeCommandResult DumpLocation(string locationId) {
        EnsureNotDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(locationId);
        return Text(WorldDumpRenderer.RenderLocation(_world, locationId));
    }

    public TextAdv2RuntimeLocationObservation ObserveLocation(string locationId) {
        EnsureNotDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(locationId);
        return TextAdv2RuntimeObservationProjector.ProjectLocation(_world, locationId);
    }

    public TextAdv2RuntimeActorObservation ObserveActor(string actorId) {
        EnsureNotDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        return TextAdv2RuntimeObservationProjector.ProjectActor(_world, actorId);
    }

    public TextAdv2RuntimeLocationNavigationObservation ObserveNavigation(string locationId) {
        EnsureNotDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(locationId);
        return TextAdv2RuntimeObservationProjector.ProjectNavigation(_world, locationId);
    }

    public TextAdv2RuntimeActorNavigationObservation ObserveActorNavigation(string actorId) {
        EnsureNotDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        return TextAdv2RuntimeObservationProjector.ProjectActorNavigation(_world, actorId);
    }

    public TextAdv2RouteAccelerationObservation ObserveRouteAcceleration() {
        EnsureNotDisposed();
        return _routeAcceleration.Observe(_world);
    }

    public TextAdv2LogicalTimeObservation ObserveTime() {
        EnsureNotDisposed();
        return ObserveLogicalTime();
    }

    public TextAdv2LogicalTimeObservation AdvanceTime(long ticks) {
        EnsureNotDisposed();
        ArgumentOutOfRangeException.ThrowIfNegative(ticks);
        return AdvanceLogicalTime(ticks);
    }

    public TextAdv2RuntimeCommandResult PlanActorRoute(string actorId, string toLocationId) {
        EnsureNotDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toLocationId);

        return Text(
            LocationRoutePlanTextRenderer.Render(
                LocationRoutePlanner.PlanShortestRouteForActor(
                    _world,
                    actorId,
                    toLocationId,
                    _routeAcceleration.GetPlanningOptions(_world)
                )
            )
        );
    }

    public TextAdv2RuntimeCommandResult PlanRoute(string fromLocationId, string toLocationId) {
        EnsureNotDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(fromLocationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toLocationId);

        return Text(
            LocationRoutePlanTextRenderer.Render(
                LocationRoutePlanner.PlanShortestRoute(
                    _world,
                    fromLocationId,
                    toLocationId,
                    _routeAcceleration.GetPlanningOptions(_world)
                )
            )
        );
    }

    public TextAdv2RouteAccelerationObservation RebuildRouteAcceleration(string requestedLandmarks) {
        EnsureNotDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedLandmarks);
        return RebuildRouteAccelerationCore(ParseExplicitLandmarkLocationIds(requestedLandmarks), "custom");
    }

    public TextAdv2RuntimeCommandResult TraceActorRoute(string actorId) {
        EnsureNotDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);

        return Text(
            ActorRouteTraceTextRenderer.Render(
                ActorRouteTraceProjector.ObserveActorRouteTrace(
                    _world,
                    actorId,
                    GetMovementHistory(actorId)
                )
            )
        );
    }

    public TextAdv2RuntimeCommandResult MoveActorQuiet(string actorId, string passageId) {
        EnsureNotDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(passageId);

        return Text(RenderCompactMovement(MoveActorCore(actorId, passageId)));
    }

    public TextAdv2RuntimeActorMovementObservation MoveActor(string actorId, string passageId) {
        EnsureNotDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(passageId);

        return TextAdv2RuntimeObservationProjector.ProjectMovement(MoveActorCore(actorId, passageId));
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

    private TextAdv2LogicalTimeObservation ObserveLogicalTime() => new(_logicalTick);

    internal TextAdv2RouteAccelerationObservation RebuildRouteAcceleration(
        IEnumerable<string> landmarkLocationIds,
        string landmarkProfileName
    ) {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(landmarkLocationIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(landmarkProfileName);
        return RebuildRouteAccelerationCore(landmarkLocationIds, landmarkProfileName);
    }

    private TextAdv2LogicalTimeObservation AdvanceLogicalTime(long ticks) {
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

    private static TextAdv2RuntimeCommandResult Text(string output)
        => new(output, TextAdv2RuntimeContentTypes.PlainText);

    private static string RenderCompactMovement(ActorMovementObservation movement)
        => $"{movement.ActorId}: {movement.FromLocationId} --{movement.ExitName}/{movement.PassageId}--> {movement.ToLocationId}"
            + $" | {movement.TravelMode.ToStorageValue()} | cost={movement.TravelCost}";

    internal TextAdv2DefaultLandmarkProfile? ResolveDefaultLandmarkProfile() {
        EnsureNotDisposed();
        return _options.DefaultLandmarkProfileResolver?.Invoke(_world);
    }

    private TextAdv2RouteAccelerationObservation RebuildRouteAccelerationCore(
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
