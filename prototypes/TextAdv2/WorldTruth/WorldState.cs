using Atelia.StateJournal;
using Atelia.TextAdv2.Spatial;

namespace Atelia.TextAdv2.WorldTruth;

/// <summary>
/// WorldState 是世界真相层的 graph root。
///
/// 它只保存唯一真相：地点表、连接表，以及它们之间通过稳定 ID 建立的关系。
/// 邻接缓存、反向索引、玩家可见投影都不属于这里；它们以后应放进 acceleration 或 observation 层。
/// </summary>
internal sealed class WorldState {
    internal const string KindKey = "kind";

    private const string KindValue = "world-state";
    private const string SchemaVersionKey = "schemaVersion";
    private const int CurrentSchemaVersion = 5;
    private const string CurrentLogicalTickKey = "currentLogicalTick";
    private const string ActorsKey = "actors";
    private const string LocationsKey = "locations";
    private const string PassagesKey = "passages";

    private readonly DurableDict<string> _root;

    private WorldState(DurableDict<string> root) {
        ArgumentNullException.ThrowIfNull(root);
        _root = root;
        EnsureKind(root, KindValue);
        EnsureSupportedSchemaVersion(root);

        _ = ActorsLedger;
        _ = LocationsLedger;
        _ = PassagesLedger;
        _ = CurrentLogicalTick;
        ValidateIntegrity();
    }

    /// <summary>
    /// 仅用于仓储提交边界；除 commit/reopen 以外，不应绕过 WorldState API 直接改写 root。
    /// </summary>
    public DurableDict<string> Root => _root;

    public Revision Revision => _root.Revision;

    public long CurrentLogicalTick => ReadCurrentLogicalTick();

    private DurableDict<string> ActorsLedger => _root.GetOrThrow<DurableDict<string>>(ActorsKey)!;

    private DurableDict<string> LocationsLedger => _root.GetOrThrow<DurableDict<string>>(LocationsKey)!;

    private DurableDict<string> PassagesLedger => _root.GetOrThrow<DurableDict<string>>(PassagesKey)!;

    public static WorldState Create(Revision revision) {
        ArgumentNullException.ThrowIfNull(revision);

        var root = revision.CreateDict<string>();
        root.Upsert(KindKey, KindValue);
        root.Upsert(SchemaVersionKey, CurrentSchemaVersion);
        root.Upsert(CurrentLogicalTickKey, 0L);
        root.Upsert(ActorsKey, revision.CreateDict<string>());
        root.Upsert(LocationsKey, revision.CreateDict<string>());
        root.Upsert(PassagesKey, revision.CreateDict<string>());
        return new WorldState(root);
    }

    public static WorldState FromRoot(DurableDict<string> root) => new(root);

    public Actor CreateActor(string id, string name, string currentLocationId) {
        ValidateEntityId(id, nameof(id));
        ValidateRequiredText(name, nameof(name));
        ValidateEntityId(currentLocationId, nameof(currentLocationId));

        if (TryGetActor(id, out _)) {
            throw new InvalidOperationException($"Actor '{id}' already exists.");
        }

        EnsureLocationExists(currentLocationId);

        var actor = Actor.Create(Revision, id, name, currentLocationId);
        ActorsLedger.Upsert(id, actor.Data);
        return actor;
    }

    public Actor GetActor(string id) {
        if (TryGetActor(id, out var actor) && actor is not null) {
            return actor;
        }

        throw new InvalidOperationException($"Actor '{id}' does not exist.");
    }

    public bool TryGetActor(string id, out Actor? actor) {
        ValidateEntityId(id, nameof(id));

        if (ActorsLedger.TryGet(id, out DurableDict<string>? data)) {
            actor = new Actor(id, data!);
            return true;
        }

        actor = null;
        return false;
    }

    public Location CreateLocation(string id, string name, string description) {
        ValidateEntityId(id, nameof(id));
        ValidateRequiredText(name, nameof(name));
        ArgumentNullException.ThrowIfNull(description);

        if (TryGetLocation(id, out _)) {
            throw new InvalidOperationException($"Location '{id}' already exists.");
        }

        var location = Location.Create(Revision, id, name, description);
        LocationsLedger.Upsert(id, location.Data);
        return location;
    }

    public Location GetLocation(string id) {
        if (TryGetLocation(id, out var location) && location is not null) {
            return location;
        }

        throw new InvalidOperationException($"Location '{id}' does not exist.");
    }

    public bool TryGetLocation(string id, out Location? location) {
        ValidateEntityId(id, nameof(id));

        if (LocationsLedger.TryGet(id, out DurableDict<string>? data)) {
            location = new Location(id, data!);
            return true;
        }

        location = null;
        return false;
    }

    public Location ConfigureLocationMiningWorksite(
        string locationId,
        int ticksPerYield,
        string yieldItemId,
        int yieldAmount = 1
    ) {
        ValidateEntityId(locationId, nameof(locationId));

        var location = GetLocation(locationId);
        location.SetMiningWorksite(new LocationMiningWorksiteProfile(
            ticksPerYield,
            yieldItemId,
            yieldAmount
        ));
        return location;
    }

    public Location ClearLocationMiningWorksite(string locationId) {
        ValidateEntityId(locationId, nameof(locationId));

        var location = GetLocation(locationId);
        location.ClearMiningWorksite();
        return location;
    }

    public Passage CreatePassage(
        string id,
        string locationAId,
        string exitNameFromA,
        string locationBId,
        string exitNameFromB,
        TravelMode travelMode = TravelMode.Land,
        int baseTravelCost = 1
    ) {
        ValidateEntityId(id, nameof(id));
        ValidateEntityId(locationAId, nameof(locationAId));
        ValidateEntityId(locationBId, nameof(locationBId));
        ValidateExitName(exitNameFromA, nameof(exitNameFromA));
        ValidateExitName(exitNameFromB, nameof(exitNameFromB));
        ArgumentOutOfRangeException.ThrowIfLessThan(baseTravelCost, 1);

        if (string.Equals(locationAId, locationBId, StringComparison.Ordinal)) {
            throw new InvalidOperationException("A passage must connect two different locations.");
        }
        if (TryGetWritablePassage(id, out _)) {
            throw new InvalidOperationException($"Passage '{id}' already exists.");
        }

        EnsureLocationExists(locationAId);
        EnsureLocationExists(locationBId);
        var spatial = WorldSpatialSnapshotBuilder.Build(this);
        WorldSpatialValidation.EnsureExitNameAvailable(spatial, locationAId, exitNameFromA);
        WorldSpatialValidation.EnsureExitNameAvailable(spatial, locationBId, exitNameFromB);

        var passage = Passage.Create(
            Revision,
            id,
            locationAId,
            exitNameFromA,
            locationBId,
            exitNameFromB,
            travelMode,
            baseTravelCost
        );
        PassagesLedger.Upsert(id, passage.Data);
        return passage;
    }

    public Passage GetPassage(string id) => GetWritablePassage(id);

    public bool TryGetPassage(string id, out Passage? passage) {
        if (TryGetWritablePassage(id, out var writablePassage) && writablePassage is not null) {
            passage = writablePassage;
            return true;
        }

        passage = null;
        return false;
    }

    public void SetPassageTravelMode(string passageId, TravelMode value) {
        var passage = GetWritablePassage(passageId);
        passage.SetTravelMode(value);
    }

    public void SetPassageBaseTravelCost(string passageId, int value) {
        var passage = GetWritablePassage(passageId);
        passage.SetBaseTravelCost(value);
    }

    public void SetPassageSharedConditionNote(string passageId, string value) {
        var passage = GetWritablePassage(passageId);
        passage.SetSharedConditionNote(value);
    }

    public void SetPassageEndpointLocalViewNote(string passageId, string locationId, string value) {
        EnsureLocationExists(locationId);
        var passage = GetWritablePassage(passageId);
        passage.SetEndpointLocalViewNote(locationId, value);
    }

    public void SetPassageDirectionEnabledFrom(string passageId, string locationId, bool isEnabled) {
        EnsureLocationExists(locationId);
        var passage = GetWritablePassage(passageId);
        passage.SetDirectionEnabledFrom(locationId, isEnabled);
    }

    public void SetPassageDirectionTravelCostModifierFrom(string passageId, string locationId, int value) {
        EnsureLocationExists(locationId);
        var passage = GetWritablePassage(passageId);
        passage.SetDirectionTravelCostModifierFrom(locationId, value);
    }

    public void SetPassageDirectionConditionNoteFrom(string passageId, string locationId, string value) {
        EnsureLocationExists(locationId);
        var passage = GetWritablePassage(passageId);
        passage.SetDirectionConditionNoteFrom(locationId, value);
    }

    public long AdvanceLogicalTime(long ticks) {
        return AdvanceLogicalTimeWithReport(ticks).CurrentTick;
    }

    internal WorldTimeAdvanceReport AdvanceLogicalTimeWithReport(long ticks) {
        ArgumentOutOfRangeException.ThrowIfNegative(ticks);

        if (ticks == 0) {
            return new WorldTimeAdvanceReport(CurrentLogicalTick, []);
        }

        var movementReceipts = new List<ActorMoveReceipt>();
        for (long i = 0; i < ticks; i++) {
            movementReceipts.AddRange(WorldTurnExecutor.AdvanceOneTick(this));
            _root.Upsert(CurrentLogicalTickKey, checked(CurrentLogicalTick + 1));
        }

        return new WorldTimeAdvanceReport(CurrentLogicalTick, [.. movementReceipts]);
    }

    /// <summary>
    /// 按当前 actor 所在地点，沿指定 passage 的合法方向移动，并返回 authoritative move receipt。
    /// 该 API 会校验：
    /// - actor 与 passage 必须存在；
    /// - actor 当前地点必须是该 passage 的一端；
    /// - 从当前位置出发的方向必须 enabled。
    /// </summary>
    public ActorMoveReceipt MoveActorAlongPassage(string actorId, string passageId) {
        return MoveActorAlongPassageCore(actorId, passageId, clearEmbodiedState: true);
    }

    public ActorEmbodiedState StartActorRouteFollowing(
        string actorId,
        string destinationLocationId,
        IEnumerable<string> remainingPassageIds,
        bool isInterruptible = true
    ) {
        ValidateEntityId(actorId, nameof(actorId));
        ValidateEntityId(destinationLocationId, nameof(destinationLocationId));
        ArgumentNullException.ThrowIfNull(remainingPassageIds);

        var actor = GetActor(actorId);
        EmbodiedProcessRules.EnsureCanInterrupt(actor, "start route-following");
        EnsureLocationExists(destinationLocationId);
        var passageIds = remainingPassageIds.ToArray();
        int remainingTravelTicksOnCurrentLeg = ValidateRouteFollowingPath(
            actor.Id,
            actor.CurrentLocationId,
            destinationLocationId,
            passageIds,
            duringWorldLoad: false
        );
        var state = new RouteFollowingActorProcessState(
            destinationLocationId,
            passageIds,
            remainingTravelTicksOnCurrentLeg,
            isInterruptible
        );
        actor.SetEmbodiedState(state);
        return state;
    }

    public ActorEmbodiedState StartActorMining(
        string actorId,
        string worksiteId,
        bool isInterruptible = true
    ) {
        ValidateEntityId(actorId, nameof(actorId));
        ValidateEntityId(worksiteId, nameof(worksiteId));

        var actor = GetActor(actorId);
        EmbodiedProcessRules.EnsureCanInterrupt(actor, "start mining");
        var location = GetLocation(worksiteId);
        var worksite = location.MiningWorksite ?? throw new InvalidOperationException(
            $"Location '{worksiteId}' is not configured as a mining worksite."
        );

        if (!string.Equals(actor.CurrentLocationId, worksiteId, StringComparison.Ordinal)) {
            throw new InvalidOperationException(
                $"Actor '{actorId}' must be at worksite '{worksiteId}' to start mining, but is currently at '{actor.CurrentLocationId}'."
            );
        }

        var state = new MiningActorProcessState(
            worksiteId,
            progressTicksInCurrentCycle: 0,
            worksite.TicksPerYield,
            worksite.YieldItemId,
            producedYieldCount: 0,
            worksite.YieldAmount,
            isInterruptible
        );
        actor.SetEmbodiedState(state);
        return state;
    }

    public ActorEmbodiedState CancelActorEmbodiedState(string actorId) {
        ValidateEntityId(actorId, nameof(actorId));

        var actor = GetActor(actorId);
        EmbodiedProcessRules.EnsureCanInterrupt(actor, "cancel current process");
        actor.SetEmbodiedState(ActorEmbodiedState.Idle);
        return actor.EmbodiedState;
    }

    internal ActorMoveReceipt MoveActorAlongPassageDuringEmbodiedProcess(string actorId, string passageId) {
        return MoveActorAlongPassageCore(actorId, passageId, clearEmbodiedState: false);
    }

    internal static int ComputeEmbodiedTravelTicks(Passage passage, string fromLocationId) {
        ArgumentNullException.ThrowIfNull(passage);
        ValidateEntityId(fromLocationId, nameof(fromLocationId));

        // passage traversal 作为 embodied process 时，至少消耗 1 tick，
        // 即便方向 modifier 使 total travel cost 降到了 0。
        return Math.Max(1, passage.GetTotalTravelCostFrom(fromLocationId));
    }

    private ActorMoveReceipt MoveActorAlongPassageCore(string actorId, string passageId, bool clearEmbodiedState) {
        var actor = GetActor(actorId);
        if (clearEmbodiedState) {
            EmbodiedProcessRules.EnsureCanInterrupt(actor, $"move manually via passage '{passageId}'");
        }

        var passage = GetWritablePassage(passageId);
        var fromLocationId = actor.CurrentLocationId;

        if (!passage.Connects(fromLocationId)) {
            throw new InvalidOperationException(
                $"Actor '{actorId}' is at location '{fromLocationId}', which is not connected by passage '{passageId}'."
            );
        }

        var direction = passage.GetDirectionFrom(fromLocationId);
        if (!direction.IsEnabled) {
            throw new InvalidOperationException(
                $"Passage '{passageId}' is not traversable from location '{fromLocationId}'."
            );
        }

        var exit = passage.GetEndpointFor(fromLocationId);
        var toLocationId = passage.GetOtherLocationId(fromLocationId);
        EnsureLocationExists(toLocationId);
        actor.MoveTo(toLocationId);
        if (clearEmbodiedState) {
            actor.SetEmbodiedState(ActorEmbodiedState.Idle);
        }

        return new ActorMoveReceipt(
            actor.Id,
            passage.Id,
            exit.ExitName,
            fromLocationId,
            toLocationId,
            passage.TravelMode,
            passage.GetTotalTravelCostFrom(fromLocationId)
        );
    }

    public IEnumerable<Actor> EnumerateActors() {
        foreach (var actorId in ActorsLedger.Keys) {
            yield return new Actor(actorId, ActorsLedger.GetOrThrow<DurableDict<string>>(actorId)!);
        }
    }

    public IEnumerable<Actor> EnumerateActorsAtLocation(string locationId) {
        EnsureLocationExists(locationId);

        foreach (var actor in EnumerateActors()) {
            if (string.Equals(actor.CurrentLocationId, locationId, StringComparison.Ordinal)) {
                yield return actor;
            }
        }
    }

    public IEnumerable<Location> EnumerateLocations() {
        foreach (var locationId in LocationsLedger.Keys) {
            yield return new Location(locationId, LocationsLedger.GetOrThrow<DurableDict<string>>(locationId)!);
        }
    }

    public IEnumerable<Passage> EnumeratePassages() => EnumerateWritablePassages();

    public IEnumerable<Passage> EnumeratePassagesTouching(string locationId)
        => EnumerateWritablePassagesTouching(locationId);

    internal static void EnsureKind(DurableDict<string> data, string expectedKind) {
        ArgumentNullException.ThrowIfNull(data);
        ValidateRequiredText(expectedKind, nameof(expectedKind));

        if (!data.TryGet(KindKey, out string? actualKind)
            || !string.Equals(actualKind, expectedKind, StringComparison.Ordinal)) {
            throw new InvalidOperationException(
                $"Expected durable object kind '{expectedKind}', but found '{actualKind ?? "<missing>"}'."
            );
        }
    }

    internal static void EnsureSupportedSchemaVersion(DurableDict<string> data) {
        ArgumentNullException.ThrowIfNull(data);

        GetIssue issue = data.Get(SchemaVersionKey, out int schemaVersion);
        switch (issue) {
            case GetIssue.None:
                if (schemaVersion != CurrentSchemaVersion) {
                    throw new InvalidOperationException(
                        $"Expected world-state schemaVersion '{CurrentSchemaVersion}', but found '{schemaVersion}'."
                    );
                }

                return;

            case GetIssue.NotFound:
                throw new InvalidOperationException(
                    $"Expected world-state schemaVersion '{CurrentSchemaVersion}', but found '<missing>'."
                );

            default:
                throw new InvalidOperationException(
                    $"Expected world-state schemaVersion '{CurrentSchemaVersion}', but found '<invalid:{issue}>'."
                );
        }
    }

    internal static void ValidateEntityId(string value, string paramName)
        => ArgumentException.ThrowIfNullOrWhiteSpace(value, paramName);

    internal static void ValidateRequiredText(string value, string paramName)
        => ArgumentException.ThrowIfNullOrWhiteSpace(value, paramName);

    internal static void ValidateExitName(string value, string paramName) {
        string? error = GetExitNameValidationError(value);
        if (error is not null) {
            throw new ArgumentException(error, paramName);
        }
    }

    private void ValidateIntegrity() {
        var locationIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var locationId in LocationsLedger.Keys) {
            var location = new Location(locationId, LocationsLedger.GetOrThrow<DurableDict<string>>(locationId)!);
            locationIds.Add(locationId);
            ValidateLocation(location);
        }

        foreach (var actorId in ActorsLedger.Keys) {
            var actor = new Actor(actorId, ActorsLedger.GetOrThrow<DurableDict<string>>(actorId)!);
            if (!locationIds.Contains(actor.CurrentLocationId)) {
                throw new InvalidOperationException(
                    $"Actor '{actor.Id}' points to missing current location '{actor.CurrentLocationId}' during world load."
                );
            }

            ValidateActorEmbodiedState(actor, locationIds);
        }

        foreach (var passageId in PassagesLedger.Keys) {
            var passage = new Passage(passageId, PassagesLedger.GetOrThrow<DurableDict<string>>(passageId)!);

            if (string.Equals(passage.EndpointA.LocationId, passage.EndpointB.LocationId, StringComparison.Ordinal)) {
                throw new InvalidOperationException(
                    $"Passage '{passage.Id}' must connect two different locations, but both endpoints point to '{passage.EndpointA.LocationId}'."
                );
            }

            EnsureReferencedLocationExists(locationIds, passage.Id, passage.EndpointA.LocationId, "endpointA");
            EnsureReferencedLocationExists(locationIds, passage.Id, passage.EndpointB.LocationId, "endpointB");
            EnsureValidExitNameDuringWorldLoad(passage.Id, passage.EndpointA.LocationId, passage.EndpointA.ExitName);
            EnsureValidExitNameDuringWorldLoad(passage.Id, passage.EndpointB.LocationId, passage.EndpointB.ExitName);
        }

        WorldSpatialValidation.EnsureUniqueExitNames(WorldSpatialSnapshotBuilder.Build(this));
    }

    private void ValidateActorEmbodiedState(Actor actor, HashSet<string> locationIds) {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(locationIds);

        switch (actor.EmbodiedState) {
            case IdleActorEmbodiedState:
                return;

            case RouteFollowingActorProcessState routeFollowing:
                if (!locationIds.Contains(routeFollowing.DestinationLocationId)) {
                    throw new InvalidOperationException(
                        $"Actor '{actor.Id}' route-following destination '{routeFollowing.DestinationLocationId}' does not exist during world load."
                    );
                }

                int fullTravelTicksOnCurrentLeg = ValidateRouteFollowingPath(
                    actor.Id,
                    actor.CurrentLocationId,
                    routeFollowing.DestinationLocationId,
                    routeFollowing.RemainingPassageIds,
                    duringWorldLoad: true
                );

                if (routeFollowing.RemainingTravelTicksOnCurrentLeg < 1
                    || routeFollowing.RemainingTravelTicksOnCurrentLeg > fullTravelTicksOnCurrentLeg) {
                    throw new InvalidOperationException(
                        $"Actor '{actor.Id}' route-following state uses invalid remainingTravelTicksOnCurrentLeg '{routeFollowing.RemainingTravelTicksOnCurrentLeg}' during world load; expected 1..{fullTravelTicksOnCurrentLeg}."
                    );
                }

                return;

            case MiningActorProcessState mining:
                if (!locationIds.Contains(mining.WorksiteId)) {
                    throw new InvalidOperationException(
                        $"Actor '{actor.Id}' mining worksite '{mining.WorksiteId}' does not exist during world load."
                    );
                }

                if (GetLocation(mining.WorksiteId).MiningWorksite is null) {
                    throw new InvalidOperationException(
                        $"Actor '{actor.Id}' mining state points to location '{mining.WorksiteId}', but that location is not configured as a mining worksite during world load."
                    );
                }

                if (mining.TicksPerYield < 1) {
                    throw new InvalidOperationException(
                        $"Actor '{actor.Id}' mining state uses invalid ticksPerYield '{mining.TicksPerYield}' during world load."
                    );
                }

                if (!string.Equals(actor.CurrentLocationId, mining.WorksiteId, StringComparison.Ordinal)) {
                    throw new InvalidOperationException(
                        $"Actor '{actor.Id}' mining state requires current location '{mining.WorksiteId}', but world load found actor at '{actor.CurrentLocationId}'."
                    );
                }

                return;

            default:
                throw new InvalidOperationException(
                    $"Actor '{actor.Id}' uses unsupported embodied state type '{actor.EmbodiedState.GetType().Name}' during world load."
                );
        }
    }

    private static void ValidateLocation(Location location) {
        ArgumentNullException.ThrowIfNull(location);

        // mining worksite profile 是 authoring truth 的一部分；
        // 启动 mining process 时需要由世界侧解析它，因此这里提前 fail-fast。
        _ = location.MiningWorksite;
    }

    private int ValidateRouteFollowingPath(
        string actorId,
        string currentLocationId,
        string destinationLocationId,
        IReadOnlyList<string> remainingPassageIds,
        bool duringWorldLoad
    ) {
        ValidateEntityId(actorId, nameof(actorId));
        ValidateEntityId(currentLocationId, nameof(currentLocationId));
        ValidateEntityId(destinationLocationId, nameof(destinationLocationId));
        ArgumentNullException.ThrowIfNull(remainingPassageIds);

        if (remainingPassageIds.Count == 0) {
            throw new InvalidOperationException(
                duringWorldLoad
                    ? $"Actor '{actorId}' route-following state must keep at least one remaining passage during world load."
                    : "Route-following requires at least one remaining passage."
            );
        }

        string cursorLocationId = currentLocationId;
        Passage? firstPassage = null;

        for (int i = 0; i < remainingPassageIds.Count; i++) {
            string passageId = remainingPassageIds[i];
            ValidateEntityId(passageId, $"remainingPassageIds[{i}]");

            if (!TryGetWritablePassage(passageId, out var passage) || passage is null) {
                throw new InvalidOperationException(
                    duringWorldLoad
                        ? $"Actor '{actorId}' route-following state points to missing passage '{passageId}' during world load."
                        : $"Passage '{passageId}' does not exist."
                );
            }

            if (!passage.Connects(cursorLocationId)) {
                throw new InvalidOperationException(
                    $"Passage '{passage.Id}' does not connect route cursor location '{cursorLocationId}' for actor '{actorId}'."
                );
            }

            if (!passage.GetDirectionFrom(cursorLocationId).IsEnabled) {
                throw new InvalidOperationException(
                    $"Passage '{passage.Id}' is not traversable from route cursor location '{cursorLocationId}' for actor '{actorId}'."
                );
            }

            firstPassage ??= passage;
            cursorLocationId = passage.GetOtherLocationId(cursorLocationId);
        }

        if (!string.Equals(cursorLocationId, destinationLocationId, StringComparison.Ordinal)) {
            throw new InvalidOperationException(
                $"Route-following path for actor '{actorId}' ends at '{cursorLocationId}', not requested destination '{destinationLocationId}'."
            );
        }

        return ComputeEmbodiedTravelTicks(firstPassage!, currentLocationId);
    }

    private long ReadCurrentLogicalTick() {
        GetIssue issue = _root.Get(CurrentLogicalTickKey, out long currentLogicalTick);
        switch (issue) {
            case GetIssue.None:
                if (currentLogicalTick < 0) {
                    throw new InvalidOperationException(
                        $"Expected world-state logical tick to be non-negative, but found '{currentLogicalTick}'."
                    );
                }

                return currentLogicalTick;

            case GetIssue.NotFound:
                throw new InvalidOperationException(
                    "Expected world-state currentLogicalTick, but found '<missing>'."
                );

            default:
                throw new InvalidOperationException(
                    $"Expected world-state currentLogicalTick, but found '<invalid:{issue}>'."
                );
        }
    }

    private Passage GetWritablePassage(string id) {
        if (TryGetWritablePassage(id, out var passage) && passage is not null) {
            return passage;
        }

        throw new InvalidOperationException($"Passage '{id}' does not exist.");
    }

    private bool TryGetWritablePassage(string id, out Passage? passage) {
        ValidateEntityId(id, nameof(id));

        if (PassagesLedger.TryGet(id, out DurableDict<string>? data)) {
            passage = new Passage(id, data!);
            return true;
        }

        passage = null;
        return false;
    }

    private IEnumerable<Passage> EnumerateWritablePassages() {
        foreach (var passageId in PassagesLedger.Keys) {
            yield return new Passage(passageId, PassagesLedger.GetOrThrow<DurableDict<string>>(passageId)!);
        }
    }

    private IEnumerable<Passage> EnumerateWritablePassagesTouching(string locationId) {
        EnsureLocationExists(locationId);

        foreach (var passage in EnumerateWritablePassages()) {
            if (passage.Connects(locationId)) {
                yield return passage;
            }
        }
    }

    private void EnsureLocationExists(string locationId) {
        if (!TryGetLocation(locationId, out _)) {
            throw new InvalidOperationException($"Location '{locationId}' does not exist.");
        }
    }

    private static void EnsureReferencedLocationExists(
        HashSet<string> locationIds,
        string passageId,
        string locationId,
        string endpointName
    ) {
        if (!locationIds.Contains(locationId)) {
            throw new InvalidOperationException(
                $"Passage '{passageId}' {endpointName} points to missing location '{locationId}' during world load."
            );
        }
    }

    private static void EnsureValidExitNameDuringWorldLoad(string passageId, string locationId, string exitName) {
        string? error = GetExitNameValidationError(exitName);
        if (error is not null) {
            throw new InvalidOperationException(
                $"Passage '{passageId}' uses invalid exit name '{exitName}' at location '{locationId}' during world load: {error}"
            );
        }
    }

    private static string? GetExitNameValidationError(string value) {
        if (string.IsNullOrWhiteSpace(value)) {
            return "Exit name must contain non-whitespace text.";
        }

        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal)) {
            return "Exit name must not have leading or trailing whitespace.";
        }

        return null;
    }
}

internal sealed record WorldTimeAdvanceReport(long CurrentTick, ActorMoveReceipt[] MovementReceipts);
