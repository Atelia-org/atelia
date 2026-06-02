using Atelia.StateJournal;
using Atelia.TextAdv2.Observation;
using Atelia.TextAdv2.Routing;
using Atelia.TextAdv2.Spatial;
using Atelia.TextAdv2.WorldTruth;

namespace Atelia.TextAdv2.Runtime;

internal sealed class WorldHost : IDisposable {
    private const string MainBranchName = "main";

    private readonly Repository _repo;
    private readonly WorldState _world;
    private WorldSpatialSnapshot? _spatial;
    private bool _disposed;

    private WorldHost(string repoDir, Repository repo, WorldState world) {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoDir);
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(world);

        RepoDir = repoDir;
        _repo = repo;
        _world = world;
    }

    public string RepoDir { get; }

    internal WorldState DurableWorld {
        get {
            EnsureNotDisposed();
            return _world;
        }
    }

    internal WorldSpatialSnapshot ObserveSpatial() {
        EnsureNotDisposed();
        return _spatial ??= WorldSpatialSnapshotBuilder.Build(_world);
    }

    public static WorldHost CreateEmpty(string repoDir)
        => CreateNew(repoDir, WorldState.Create);

    internal static WorldHost CreateNew(
        string repoDir,
        Func<Revision, WorldState> worldFactory
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
            return new WorldHost(repoDir, repo, world);
        }
        catch {
            repo.Dispose();
            throw;
        }
    }

    public static WorldHost OpenExisting(string repoDir) {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoDir);

        var repo = Repository.Open(repoDir).Unwrap();
        try {
            var revision = repo.CheckoutBranch(MainBranchName).Unwrap();
            var world = LoadWorldState(revision);
            return new WorldHost(repoDir, repo, world);
        }
        catch {
            repo.Dispose();
            throw;
        }
    }

    internal WorldHost Reopen() {
        EnsureNotDisposed();
        return OpenExisting(RepoDir);
    }

    public LocationObservation ObserveLocation(string locationId) {
        EnsureNotDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(locationId);
        return LocationObservationProjector.ObserveLocation(_world, ObserveSpatial(), locationId);
    }

    public ActorLocationObservation ObserveActor(string actorId) {
        EnsureNotDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        return LocationObservationProjector.ObserveActorLocation(_world, ObserveSpatial(), actorId);
    }

    public ActorContextObservation ObserveActorContext(string actorId) {
        EnsureNotDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        return ActorContextObservationProjector.ObserveActorContext(_world, ObserveSpatial(), actorId);
    }

    public LocationNavigationObservation ObserveNavigation(string locationId) {
        EnsureNotDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(locationId);
        return NavigationObservationProjector.ObserveLocationNavigation(_world, ObserveSpatial(), locationId);
    }

    public ActorNavigationObservation ObserveActorNavigation(string actorId) {
        EnsureNotDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        return NavigationObservationProjector.ObserveActorNavigation(_world, ObserveSpatial(), actorId);
    }

    public LogicalTimeSnapshot ObserveTime() {
        EnsureNotDisposed();
        return new LogicalTimeSnapshot(_world.CurrentLogicalTick);
    }

    public LogicalTimeSnapshot AdvanceTime(long ticks) {
        EnsureNotDisposed();
        ArgumentOutOfRangeException.ThrowIfNegative(ticks);

        _ = _world.AdvanceLogicalTime(ticks);
        if (ticks > 0) {
            Commit();
        }

        return ObserveTime();
    }

    public LocationAuthoringSnapshot CreateLocation(string id, string name, string description) {
        EnsureNotDisposed();
        var location = _world.CreateLocation(id, name, description);
        InvalidateSpatial();
        Commit();
        return RuntimeWorldAuthoringProjector.Project(location);
    }

    public ActorAuthoringSnapshot CreateActor(string id, string name, string currentLocationId) {
        EnsureNotDisposed();
        var actor = _world.CreateActor(id, name, currentLocationId);
        Commit();
        return RuntimeWorldAuthoringProjector.Project(actor);
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
        var passage = _world.CreatePassage(
            id,
            locationAId,
            exitNameFromA,
            locationBId,
            exitNameFromB,
            travelMode,
            baseTravelCost
        );
        InvalidateSpatial();
        Commit();
        return RuntimeWorldAuthoringProjector.Project(passage);
    }

    public PassageAuthoringSnapshot SetPassageTravelMode(string passageId, TravelMode value) {
        EnsureNotDisposed();
        return MutateSpatialPassage(passageId, () => _world.SetPassageTravelMode(passageId, value));
    }

    public PassageAuthoringSnapshot SetPassageBaseTravelCost(string passageId, int value) {
        EnsureNotDisposed();
        return MutateSpatialPassage(passageId, () => _world.SetPassageBaseTravelCost(passageId, value));
    }

    public PassageAuthoringSnapshot SetPassageSharedConditionNote(string passageId, string value) {
        EnsureNotDisposed();
        return MutateSpatialPassage(passageId, () => _world.SetPassageSharedConditionNote(passageId, value));
    }

    public PassageAuthoringSnapshot SetPassageEndpointLocalViewNote(string passageId, string locationId, string value) {
        EnsureNotDisposed();
        return MutateSpatialPassage(passageId, () => _world.SetPassageEndpointLocalViewNote(passageId, locationId, value));
    }

    public PassageAuthoringSnapshot SetPassageDirectionEnabledFrom(string passageId, string locationId, bool isEnabled) {
        EnsureNotDisposed();
        return MutateSpatialPassage(passageId, () => _world.SetPassageDirectionEnabledFrom(passageId, locationId, isEnabled));
    }

    public PassageAuthoringSnapshot SetPassageDirectionTravelCostModifierFrom(string passageId, string locationId, int value) {
        EnsureNotDisposed();
        return MutateSpatialPassage(passageId, () => _world.SetPassageDirectionTravelCostModifierFrom(passageId, locationId, value));
    }

    public PassageAuthoringSnapshot SetPassageDirectionConditionNoteFrom(string passageId, string locationId, string value) {
        EnsureNotDisposed();
        return MutateSpatialPassage(passageId, () => _world.SetPassageDirectionConditionNoteFrom(passageId, locationId, value));
    }

    public LocationRoutePlanObservation PlanActorRoute(
        string actorId,
        string toLocationId,
        LocationRoutePlanningOptions? planningOptions
    ) => PlanActorRoute(actorId, toLocationId, ObserveSpatial(), planningOptions);

    public LocationRoutePlanObservation PlanActorRoute(
        string actorId,
        string toLocationId,
        WorldSpatialSnapshot spatial,
        LocationRoutePlanningOptions? planningOptions
    ) {
        EnsureNotDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toLocationId);
        ArgumentNullException.ThrowIfNull(spatial);

        return LocationRoutePlanner.PlanShortestRouteForActor(
            _world,
            spatial,
            actorId,
            toLocationId,
            planningOptions
        );
    }

    public LocationRoutePlanObservation PlanRoute(
        string fromLocationId,
        string toLocationId,
        LocationRoutePlanningOptions? planningOptions
    ) => PlanRoute(fromLocationId, toLocationId, ObserveSpatial(), planningOptions);

    public LocationRoutePlanObservation PlanRoute(
        string fromLocationId,
        string toLocationId,
        WorldSpatialSnapshot spatial,
        LocationRoutePlanningOptions? planningOptions
    ) {
        EnsureNotDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(fromLocationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toLocationId);
        ArgumentNullException.ThrowIfNull(spatial);

        return LocationRoutePlanner.PlanShortestRoute(
            _world,
            spatial,
            fromLocationId,
            toLocationId,
            planningOptions
        );
    }

    public ActorMoveCommit MoveActor(string actorId, string passageId) {
        EnsureNotDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(passageId);

        var receipt = _world.MoveActorAlongPassage(actorId, passageId);
        Commit();

        var actor = _world.GetActor(actorId);
        var fromLocation = _world.GetLocation(receipt.FromLocationId);
        var toLocation = _world.GetLocation(receipt.ToLocationId);

        return new ActorMoveCommit(
            actor.Id,
            actor.Name,
            receipt.PassageId,
            receipt.ExitName,
            receipt.FromLocationId,
            fromLocation.Name,
            receipt.ToLocationId,
            toLocation.Name,
            receipt.TravelMode,
            receipt.TravelCost
        );
    }

    public void Dispose() {
        if (_disposed) { return; }

        _repo.Dispose();
        _disposed = true;
    }

    private PassageAuthoringSnapshot MutateSpatialPassage(string passageId, Action mutation) {
        ArgumentException.ThrowIfNullOrWhiteSpace(passageId);
        ArgumentNullException.ThrowIfNull(mutation);

        mutation();
        InvalidateSpatial();
        Commit();
        return RuntimeWorldAuthoringProjector.Project(_world.GetPassage(passageId));
    }

    private void Commit() => _repo.Commit(_world.Root).Unwrap();

    private void InvalidateSpatial() => _spatial = null;

    private static WorldState LoadWorldState(Revision revision) {
        ArgumentNullException.ThrowIfNull(revision);
        return WorldState.FromRoot(revision.GetGraphRoot<DurableDict<string>>().Unwrap());
    }

    private void EnsureNotDisposed() {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
