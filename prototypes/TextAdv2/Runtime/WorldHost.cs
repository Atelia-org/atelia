using Atelia.StateJournal;
using Atelia.TextAdv2.ReadOnlyView;
using Atelia.TextAdv2.WorldTruth;

namespace Atelia.TextAdv2.Runtime;

internal sealed class WorldHost : IDisposable {
    private const string MainBranchName = "main";

    private readonly Repository _repo;
    private readonly WorldState _world;
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
        return LocationObservationProjector.ObserveLocation(_world, locationId);
    }

    public ActorLocationObservation ObserveActor(string actorId) {
        EnsureNotDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        return LocationObservationProjector.ObserveActorLocation(_world, actorId);
    }

    public LocationNavigationObservation ObserveNavigation(string locationId) {
        EnsureNotDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(locationId);
        return NavigationObservationProjector.ObserveLocationNavigation(_world, locationId);
    }

    public ActorNavigationObservation ObserveActorNavigation(string actorId) {
        EnsureNotDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        return NavigationObservationProjector.ObserveActorNavigation(_world, actorId);
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
        Commit();
        return RuntimeWorldAuthoringProjector.Project(passage);
    }

    public PassageAuthoringSnapshot SetPassageTravelMode(string passageId, TravelMode value) {
        EnsureNotDisposed();
        return MutatePassage(passageId, () => _world.SetPassageTravelMode(passageId, value));
    }

    public PassageAuthoringSnapshot SetPassageBaseTravelCost(string passageId, int value) {
        EnsureNotDisposed();
        return MutatePassage(passageId, () => _world.SetPassageBaseTravelCost(passageId, value));
    }

    public PassageAuthoringSnapshot SetPassageSharedConditionNote(string passageId, string value) {
        EnsureNotDisposed();
        return MutatePassage(passageId, () => _world.SetPassageSharedConditionNote(passageId, value));
    }

    public PassageAuthoringSnapshot SetPassageEndpointLocalViewNote(string passageId, string locationId, string value) {
        EnsureNotDisposed();
        return MutatePassage(passageId, () => _world.SetPassageEndpointLocalViewNote(passageId, locationId, value));
    }

    public PassageAuthoringSnapshot SetPassageDirectionEnabledFrom(string passageId, string locationId, bool isEnabled) {
        EnsureNotDisposed();
        return MutatePassage(passageId, () => _world.SetPassageDirectionEnabledFrom(passageId, locationId, isEnabled));
    }

    public PassageAuthoringSnapshot SetPassageDirectionTravelCostModifierFrom(string passageId, string locationId, int value) {
        EnsureNotDisposed();
        return MutatePassage(passageId, () => _world.SetPassageDirectionTravelCostModifierFrom(passageId, locationId, value));
    }

    public PassageAuthoringSnapshot SetPassageDirectionConditionNoteFrom(string passageId, string locationId, string value) {
        EnsureNotDisposed();
        return MutatePassage(passageId, () => _world.SetPassageDirectionConditionNoteFrom(passageId, locationId, value));
    }

    public LocationRoutePlanObservation PlanActorRoute(
        string actorId,
        string toLocationId,
        LocationRoutePlanningOptions? planningOptions
    ) {
        EnsureNotDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toLocationId);

        return LocationRoutePlanner.PlanShortestRouteForActor(
            _world,
            actorId,
            toLocationId,
            planningOptions
        );
    }

    public LocationRoutePlanObservation PlanRoute(
        string fromLocationId,
        string toLocationId,
        LocationRoutePlanningOptions? planningOptions
    ) {
        EnsureNotDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(fromLocationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toLocationId);

        return LocationRoutePlanner.PlanShortestRoute(
            _world,
            fromLocationId,
            toLocationId,
            planningOptions
        );
    }

    public ActorMovementObservation MoveActor(string actorId, string passageId) {
        EnsureNotDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(passageId);

        var receipt = _world.MoveActorAlongPassage(actorId, passageId);
        Commit();

        var fromLocation = _world.GetLocation(receipt.FromLocationId);
        var toLocation = _world.GetLocation(receipt.ToLocationId);
        var currentObservation = LocationObservationProjector.ObserveActorLocation(_world, actorId);

        return new ActorMovementObservation(
            currentObservation.ActorId,
            currentObservation.ActorName,
            receipt.PassageId,
            receipt.ExitName,
            receipt.FromLocationId,
            fromLocation.Name,
            receipt.ToLocationId,
            toLocation.Name,
            receipt.TravelMode,
            receipt.TravelCost,
            currentObservation.Location
        );
    }

    internal string RenderWorldDumpForDevSupport() {
        EnsureNotDisposed();
        return WorldDumpRenderer.Render(_world);
    }

    internal string RenderLocationDumpForDevSupport(string locationId) {
        EnsureNotDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(locationId);
        return WorldDumpRenderer.RenderLocation(_world, locationId);
    }

    internal bool TryGetRecommendedLandmarkLocationIdsForDevSupport(out string[] landmarkLocationIds) {
        EnsureNotDisposed();
        return TestWorldBuilder.TryGetRecommendedLandmarkLocationIds(_world, out landmarkLocationIds);
    }

    public void Dispose() {
        if (_disposed) { return; }

        _repo.Dispose();
        _disposed = true;
    }

    private PassageAuthoringSnapshot MutatePassage(string passageId, Action mutation) {
        ArgumentException.ThrowIfNullOrWhiteSpace(passageId);
        ArgumentNullException.ThrowIfNull(mutation);

        mutation();
        Commit();
        return RuntimeWorldAuthoringProjector.Project(_world.GetPassage(passageId));
    }

    private void Commit() => _repo.Commit(_world.Root).Unwrap();

    private static WorldState LoadWorldState(Revision revision) {
        ArgumentNullException.ThrowIfNull(revision);
        return WorldState.FromRoot(revision.GetGraphRoot<DurableDict<string>>().Unwrap());
    }

    private void EnsureNotDisposed() {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
