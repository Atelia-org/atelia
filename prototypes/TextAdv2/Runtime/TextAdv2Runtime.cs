using System.Text.Json;
using System.Text.Json.Serialization;
using Atelia.StateJournal;
using Atelia.TextAdv2.ReadOnlyView;
using Atelia.TextAdv2.WorldTruth;

namespace Atelia.TextAdv2.Runtime;

/// <summary>
/// TextAdv2 的第一版 runtime 会话对象。
///
/// 当前阶段它统一持有：
/// - Repository / WorldState 生命周期；
/// - runtime-owned logical time；
/// - runtime sidecar state 中的 movement history；
/// - runtime-owned route acceleration snapshot；
/// - 现有 CLI 已支持命令的执行编排。
///
/// 它故意先保持为单线程、单世界、单会话模型，优先把业务编排从入口程序中抽出，
/// 后续再在此基础上演进到真正的 authoritative GameServer runtime。
/// </summary>
public sealed class TextAdv2Runtime : IDisposable {
    private const string MainBranchName = "main";

    private readonly Repository _repo;
    private readonly TextAdv2RuntimePersistentStateStore _persistentStateStore;
    private readonly WorldState _world;
    private readonly TextAdv2RouteAccelerationState _routeAcceleration = new();
    private readonly Dictionary<string, List<ActorMovementObservation>> _movementHistoryByActor = new(StringComparer.Ordinal);
    private readonly JsonSerializerOptions _jsonOptions;
    private long _logicalTick;
    private bool _disposed;

    private TextAdv2Runtime(
        string repoDir,
        Repository repo,
        WorldState world,
        TextAdv2RuntimePersistentStateStore persistentStateStore,
        TextAdv2RuntimePersistentState persistentState
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoDir);
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(persistentStateStore);
        ArgumentNullException.ThrowIfNull(persistentState);

        RepoDir = repoDir;
        _repo = repo;
        _persistentStateStore = persistentStateStore;
        _world = world;
        _jsonOptions = CreateJsonOptions();
        RestorePersistentState(persistentState);
    }

    public string RepoDir { get; }

    public static TextAdv2Runtime CreateTemporarySampleWorld() {
        string repoDir = Path.Combine(Path.GetTempPath(), $"atelia-textadv2-{Guid.NewGuid():N}");
        return CreateSampleWorld(repoDir);
    }

    public static TextAdv2Runtime CreateSampleWorld(string repoDir) {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoDir);

        if (Directory.Exists(repoDir) && Directory.EnumerateFileSystemEntries(repoDir).Any()) {
            throw new InvalidOperationException(
                $"Repository directory '{repoDir}' already exists and is not empty. Use OpenExisting/OpenOrCreateSampleWorld instead."
            );
        }

        var repo = Repository.Create(repoDir).Unwrap();
        try {
            var revision = repo.CreateBranch(MainBranchName).Unwrap();
            var world = TestWorldBuilder.Create(revision);
            TestWorldBuilder.PopulateSampleActors(world);
            repo.Commit(world.Root).Unwrap();

            var persistentStateStore = new TextAdv2RuntimePersistentStateStore(repoDir);
            persistentStateStore.Save(TextAdv2RuntimePersistentState.Empty);
            return new TextAdv2Runtime(repoDir, repo, world, persistentStateStore, TextAdv2RuntimePersistentState.Empty);
        }
        catch {
            repo.Dispose();
            throw;
        }
    }

    public static TextAdv2Runtime OpenExisting(string repoDir) {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoDir);

        var repo = Repository.Open(repoDir).Unwrap();
        try {
            var revision = repo.CheckoutBranch(MainBranchName).Unwrap();
            var world = LoadWorldState(revision);
            var persistentStateStore = new TextAdv2RuntimePersistentStateStore(repoDir);
            var persistentState = persistentStateStore.LoadOrDefault();
            return new TextAdv2Runtime(repoDir, repo, world, persistentStateStore, persistentState);
        }
        catch {
            repo.Dispose();
            throw;
        }
    }

    public static TextAdv2Runtime OpenOrCreateSampleWorld(string repoDir) {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoDir);

        return Directory.Exists(repoDir) && Directory.EnumerateFileSystemEntries(repoDir).Any()
            ? OpenExisting(repoDir)
            : CreateSampleWorld(repoDir);
    }

    public TextAdv2RuntimeCommandResult Execute(TextAdv2RuntimeCommand command) {
        ArgumentNullException.ThrowIfNull(command);
        EnsureNotDisposed();

        return command.Mode switch {
            TextAdv2RuntimeCommandMode.World => Text(WorldDumpRenderer.Render(_world)),
            TextAdv2RuntimeCommandMode.Location => Text(WorldDumpRenderer.RenderLocation(_world, RequireArg(command.Arg1, command.Mode, nameof(command.Arg1)))),
            TextAdv2RuntimeCommandMode.ObserveLocation => Json(LocationObservationProjector.ObserveLocation(_world, RequireArg(command.Arg1, command.Mode, nameof(command.Arg1)))),
            TextAdv2RuntimeCommandMode.ObserveActor => Json(LocationObservationProjector.ObserveActorLocation(_world, RequireArg(command.Arg1, command.Mode, nameof(command.Arg1)))),
            TextAdv2RuntimeCommandMode.ObserveNavigation => Json(NavigationObservationProjector.ObserveLocationNavigation(_world, RequireArg(command.Arg1, command.Mode, nameof(command.Arg1)))),
            TextAdv2RuntimeCommandMode.ObserveActorNavigation => Json(NavigationObservationProjector.ObserveActorNavigation(_world, RequireArg(command.Arg1, command.Mode, nameof(command.Arg1)))),
            TextAdv2RuntimeCommandMode.ObserveRouteAcceleration => Json(_routeAcceleration.Observe(_world)),
            TextAdv2RuntimeCommandMode.ObserveTime => Json(ObserveLogicalTime()),
            TextAdv2RuntimeCommandMode.AdvanceTime => Json(AdvanceLogicalTime(ParseAdvanceTickDelta(RequireArg(command.Arg1, command.Mode, nameof(command.Arg1))))),
            TextAdv2RuntimeCommandMode.PlanActorRoute => Text(
                LocationRoutePlanTextRenderer.Render(
                    LocationRoutePlanner.PlanShortestRouteForActor(
                        _world,
                        RequireArg(command.Arg1, command.Mode, nameof(command.Arg1)),
                        RequireArg(command.Arg2, command.Mode, nameof(command.Arg2)),
                        _routeAcceleration.GetPlanningOptions(_world)
                    )
                )
            ),
            TextAdv2RuntimeCommandMode.PlanRoute => Text(
                LocationRoutePlanTextRenderer.Render(
                    LocationRoutePlanner.PlanShortestRoute(
                        _world,
                        RequireArg(command.Arg1, command.Mode, nameof(command.Arg1)),
                        RequireArg(command.Arg2, command.Mode, nameof(command.Arg2)),
                        _routeAcceleration.GetPlanningOptions(_world)
                    )
                )
            ),
            TextAdv2RuntimeCommandMode.RebuildRouteAcceleration => Json(RebuildRouteAcceleration(command.Arg1)),
            TextAdv2RuntimeCommandMode.TraceActorRoute => Text(
                ActorRouteTraceTextRenderer.Render(
                    ActorRouteTraceProjector.ObserveActorRouteTrace(
                        _world,
                        RequireArg(command.Arg1, command.Mode, nameof(command.Arg1)),
                        GetMovementHistory(RequireArg(command.Arg1, command.Mode, nameof(command.Arg1)))
                    )
                )
            ),
            TextAdv2RuntimeCommandMode.MoveActorQuiet => Text(
                RenderCompactMovement(
                    MoveActor(
                        RequireArg(command.Arg1, command.Mode, nameof(command.Arg1)),
                        RequireArg(command.Arg2, command.Mode, nameof(command.Arg2))
                    )
                )
            ),
            TextAdv2RuntimeCommandMode.MoveActor => Json(
                MoveActor(
                    RequireArg(command.Arg1, command.Mode, nameof(command.Arg1)),
                    RequireArg(command.Arg2, command.Mode, nameof(command.Arg2))
                )
            ),
            _ => throw new InvalidOperationException($"Unsupported command mode '{command.Mode}'."),
        };
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

    private static JsonSerializerOptions CreateJsonOptions() {
        var options = new JsonSerializerOptions {
            WriteIndented = true,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private ActorMovementObservation MoveActor(string actorId, string passageId) {
        var actor = _world.GetActor(actorId);
        var fromLocation = _world.GetLocation(actor.CurrentLocationId);
        var passage = _world.GetPassage(passageId);
        var exit = passage.GetEndpointFor(fromLocation.Id);
        var direction = passage.GetDirectionFrom(fromLocation.Id);
        var toLocation = _world.GetLocation(passage.GetOtherLocationId(fromLocation.Id));

        _world.MoveActorAlongPassage(actorId, passageId);
        _repo.Commit(_world.Root).Unwrap();

        var currentObservation = LocationObservationProjector.ObserveActorLocation(_world, actorId);
        var movement = new ActorMovementObservation(
            currentObservation.ActorId,
            currentObservation.ActorName,
            passage.Id,
            exit.ExitName,
            fromLocation.Id,
            fromLocation.Name,
            toLocation.Id,
            toLocation.Name,
            passage.TravelMode,
            direction.TotalTravelCost(passage),
            currentObservation.Location
        );
        GetOrCreateMovementHistory(actorId).Add(movement);
        PersistRuntimeState();
        return movement;
    }

    private TextAdv2LogicalTimeObservation ObserveLogicalTime() => new(_logicalTick);

    private TextAdv2RouteAccelerationObservation RebuildRouteAcceleration(string? requestedLandmarks) {
        var rebuildRequest = ResolveLandmarkRebuildRequest(_world, requestedLandmarks);
        return _routeAcceleration.Rebuild(_world, rebuildRequest.LandmarkLocationIds, rebuildRequest.LandmarkProfileName);
    }

    private TextAdv2LogicalTimeObservation AdvanceLogicalTime(long ticks) {
        _logicalTick = checked(_logicalTick + ticks);
        PersistRuntimeState();
        return ObserveLogicalTime();
    }

    private IReadOnlyList<ActorMovementObservation> GetMovementHistory(string actorId)
        => _movementHistoryByActor.TryGetValue(actorId, out var history) ? history : [];

    private List<ActorMovementObservation> GetOrCreateMovementHistory(string actorId) {
        if (!_movementHistoryByActor.TryGetValue(actorId, out var history)) {
            history = [];
            _movementHistoryByActor.Add(actorId, history);
        }

        return history;
    }

    private TextAdv2RuntimeCommandResult Json<T>(T value)
        => new(JsonSerializer.Serialize(value, _jsonOptions), TextAdv2RuntimeContentTypes.Json);

    private static TextAdv2RuntimeCommandResult Text(string output)
        => new(output, TextAdv2RuntimeContentTypes.PlainText);

    private static string RenderCompactMovement(ActorMovementObservation movement)
        => $"{movement.ActorId}: {movement.FromLocationId} --{movement.ExitName}/{movement.PassageId}--> {movement.ToLocationId}"
            + $" | {movement.TravelMode.ToStorageValue()} | cost={movement.TravelCost}";

    private static string RequireArg(string? value, TextAdv2RuntimeCommandMode mode, string argumentName)
        => value ?? throw new InvalidOperationException($"Command '{mode}' requires {argumentName}.");

    private static long ParseAdvanceTickDelta(string value) {
        if (!long.TryParse(value, out long ticks)) { throw new InvalidOperationException($"AdvanceTime requires an integer tick delta, but received '{value}'."); }

        ArgumentOutOfRangeException.ThrowIfNegative(ticks);
        return ticks;
    }

    private static LandmarkRebuildRequest ResolveLandmarkRebuildRequest(WorldState world, string? value) {
        ArgumentNullException.ThrowIfNull(world);

        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, "default", StringComparison.OrdinalIgnoreCase)) {
            if (TestWorldBuilder.TryGetRecommendedLandmarkLocationIds(world, out var recommendedLandmarkLocationIds)) {
                return new LandmarkRebuildRequest(
                    recommendedLandmarkLocationIds,
                    TestWorldBuilder.RecommendedLandmarkProfileName
                );
            }

            throw new InvalidOperationException(
                "RebuildRouteAcceleration without an explicit landmark list requires a world with a known recommended landmark profile."
            );
        }

        return new LandmarkRebuildRequest(ParseExplicitLandmarkLocationIds(value), "custom");
    }

    private static string[] ParseExplicitLandmarkLocationIds(string value) {
        var landmarkLocationIds = value
            .Split([',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToArray();

        if (landmarkLocationIds.Length == 0) { throw new InvalidOperationException("RebuildRouteAcceleration requires a comma- or semicolon-separated landmark list."); }

        return landmarkLocationIds;
    }

    private sealed record LandmarkRebuildRequest(string[] LandmarkLocationIds, string LandmarkProfileName);

    private void EnsureNotDisposed() {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private void RestorePersistentState(TextAdv2RuntimePersistentState persistentState) {
        _logicalTick = persistentState.CurrentTick;

        foreach (var entry in persistentState.MovementHistoryByActor) {
            _movementHistoryByActor[entry.Key] = [.. entry.Value];
        }
    }

    private void PersistRuntimeState() {
        var movementHistory = _movementHistoryByActor.ToDictionary(
            entry => entry.Key,
            entry => entry.Value.ToArray(),
            StringComparer.Ordinal
        );

        _persistentStateStore.Save(
            new TextAdv2RuntimePersistentState(
                TextAdv2RuntimePersistentState.CurrentSchemaVersion,
                _logicalTick,
                movementHistory
            )
        );
    }
}
