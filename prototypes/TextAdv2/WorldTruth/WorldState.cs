using Atelia.StateJournal;

namespace Atelia.TextAdv2.WorldTruth;

/// <summary>
/// WorldState 是世界真相层的 graph root。
///
/// 它只保存唯一真相：地点表、连接表，以及它们之间通过稳定 ID 建立的关系。
/// 邻接缓存、反向索引、玩家可见投影都不属于这里；它们以后应放进 AccelerationIndex 或 ReadOnlyView 层。
/// </summary>
internal sealed class WorldState {
    internal const string KindKey = "kind";

    private const string KindValue = "world-state";
    private const string SchemaVersionKey = "schemaVersion";
    private const int CurrentSchemaVersion = 3;
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

    public PassageView CreatePassage(
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
        EnsureExitNameAvailable(locationAId, exitNameFromA);
        EnsureExitNameAvailable(locationBId, exitNameFromB);

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
        return passage.AsView();
    }

    public PassageView GetPassage(string id) => GetWritablePassage(id).AsView();

    public bool TryGetPassage(string id, out PassageView? passage) {
        if (TryGetWritablePassage(id, out var writablePassage) && writablePassage is not null) {
            passage = writablePassage.AsView();
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
        ArgumentOutOfRangeException.ThrowIfNegative(ticks);

        if (ticks == 0) {
            return CurrentLogicalTick;
        }

        long updatedTick = checked(CurrentLogicalTick + ticks);
        _root.Upsert(CurrentLogicalTickKey, updatedTick);
        return updatedTick;
    }

    /// <summary>
    /// 按当前 actor 所在地点，沿指定 passage 的合法方向移动，并返回 authoritative move receipt。
    /// 该 API 会校验：
    /// - actor 与 passage 必须存在；
    /// - actor 当前地点必须是该 passage 的一端；
    /// - 从当前位置出发的方向必须 enabled。
    /// </summary>
    public ActorMoveReceipt MoveActorAlongPassage(string actorId, string passageId) {
        var actor = GetActor(actorId);
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

    public IEnumerable<PassageView> EnumeratePassages() {
        foreach (var passage in EnumerateWritablePassages()) {
            yield return passage.AsView();
        }
    }

    public IEnumerable<PassageView> EnumeratePassagesTouching(string locationId) {
        foreach (var passage in EnumerateWritablePassagesTouching(locationId)) {
            yield return passage.AsView();
        }
    }

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
            _ = new Location(locationId, LocationsLedger.GetOrThrow<DurableDict<string>>(locationId)!);
            locationIds.Add(locationId);
        }

        foreach (var actorId in ActorsLedger.Keys) {
            var actor = new Actor(actorId, ActorsLedger.GetOrThrow<DurableDict<string>>(actorId)!);
            if (!locationIds.Contains(actor.CurrentLocationId)) {
                throw new InvalidOperationException(
                    $"Actor '{actor.Id}' points to missing current location '{actor.CurrentLocationId}' during world load."
                );
            }
        }

        var exitNamesByLocation = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
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
            EnsureExitNameUnique(exitNamesByLocation, passage.Id, passage.EndpointA.LocationId, passage.EndpointA.ExitName);
            EnsureExitNameUnique(exitNamesByLocation, passage.Id, passage.EndpointB.LocationId, passage.EndpointB.ExitName);
        }
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

    private void EnsureExitNameAvailable(string locationId, string exitName) {
        ValidateExitName(exitName, nameof(exitName));

        foreach (var passage in EnumerateWritablePassagesTouching(locationId)) {
            var existingExitName = passage.GetEndpointFor(locationId).ExitName;
            if (string.Equals(existingExitName, exitName, StringComparison.Ordinal)) {
                throw new InvalidOperationException(
                    $"Location '{locationId}' already uses exit name '{exitName}' for passage '{passage.Id}'."
                );
            }
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

    private static void EnsureExitNameUnique(
        Dictionary<string, HashSet<string>> exitNamesByLocation,
        string passageId,
        string locationId,
        string exitName
    ) {
        if (!exitNamesByLocation.TryGetValue(locationId, out var exitNames)) {
            exitNames = new HashSet<string>(StringComparer.Ordinal);
            exitNamesByLocation.Add(locationId, exitNames);
        }

        if (!exitNames.Add(exitName)) {
            throw new InvalidOperationException(
                $"Location '{locationId}' reuses exit name '{exitName}' during world load; duplicate detected at passage '{passageId}'."
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
