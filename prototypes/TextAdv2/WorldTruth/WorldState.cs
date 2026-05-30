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
    private const int CurrentSchemaVersion = 1;
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
    }

    /// <summary>
    /// 仅用于仓储提交边界；除 commit/reopen 以外，不应绕过 WorldState API 直接改写 root。
    /// </summary>
    public DurableDict<string> Root => _root;

    public Revision Revision => _root.Revision;

    private DurableDict<string> ActorsLedger => _root.GetOrThrow<DurableDict<string>>(ActorsKey)!;

    private DurableDict<string> LocationsLedger => _root.GetOrThrow<DurableDict<string>>(LocationsKey)!;

    private DurableDict<string> PassagesLedger => _root.GetOrThrow<DurableDict<string>>(PassagesKey)!;

    public static WorldState Create(Revision revision) {
        ArgumentNullException.ThrowIfNull(revision);

        var root = revision.CreateDict<string>();
        root.Upsert(KindKey, KindValue);
        root.Upsert(SchemaVersionKey, CurrentSchemaVersion);
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
            actor = new Actor(data!);
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
            location = new Location(data!);
            return true;
        }

        location = null;
        return false;
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
        ValidateRequiredText(exitNameFromA, nameof(exitNameFromA));
        ValidateRequiredText(exitNameFromB, nameof(exitNameFromB));
        ArgumentOutOfRangeException.ThrowIfLessThan(baseTravelCost, 1);

        if (string.Equals(locationAId, locationBId, StringComparison.Ordinal)) {
            throw new InvalidOperationException("A passage must connect two different locations.");
        }
        if (TryGetPassage(id, out _)) {
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
        return passage;
    }

    public Passage GetPassage(string id) {
        if (TryGetPassage(id, out var passage) && passage is not null) {
            return passage;
        }

        throw new InvalidOperationException($"Passage '{id}' does not exist.");
    }

    public bool TryGetPassage(string id, out Passage? passage) {
        ValidateEntityId(id, nameof(id));

        if (PassagesLedger.TryGet(id, out DurableDict<string>? data)) {
            passage = new Passage(data!);
            return true;
        }

        passage = null;
        return false;
    }

    /// <summary>
    /// 按当前 actor 所在地点，沿指定 passage 的合法方向移动。
    /// 该 API 会校验：
    /// - actor 与 passage 必须存在；
    /// - actor 当前地点必须是该 passage 的一端；
    /// - 从当前位置出发的方向必须 enabled。
    /// </summary>
    public Actor MoveActorAlongPassage(string actorId, string passageId) {
        var actor = GetActor(actorId);
        var passage = GetPassage(passageId);
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

        var toLocationId = passage.GetOtherLocationId(fromLocationId);
        EnsureLocationExists(toLocationId);
        actor.MoveTo(toLocationId);
        return actor;
    }

    public IEnumerable<Actor> EnumerateActors() {
        foreach (var actorId in ActorsLedger.Keys) {
            yield return new Actor(ActorsLedger.GetOrThrow<DurableDict<string>>(actorId)!);
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
            yield return new Location(LocationsLedger.GetOrThrow<DurableDict<string>>(locationId)!);
        }
    }

    public IEnumerable<Passage> EnumeratePassages() {
        foreach (var passageId in PassagesLedger.Keys) {
            yield return new Passage(PassagesLedger.GetOrThrow<DurableDict<string>>(passageId)!);
        }
    }

    public IEnumerable<Passage> EnumeratePassagesTouching(string locationId) {
        EnsureLocationExists(locationId);

        foreach (var passage in EnumeratePassages()) {
            if (passage.Connects(locationId)) {
                yield return passage;
            }
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

    private void EnsureLocationExists(string locationId) {
        if (!TryGetLocation(locationId, out _)) {
            throw new InvalidOperationException($"Location '{locationId}' does not exist.");
        }
    }

    private void EnsureExitNameAvailable(string locationId, string exitName) {
        foreach (var passage in EnumeratePassagesTouching(locationId)) {
            var existingExitName = passage.GetEndpointFor(locationId).ExitName;
            if (string.Equals(existingExitName, exitName, StringComparison.Ordinal)) {
                throw new InvalidOperationException(
                    $"Location '{locationId}' already uses exit name '{exitName}' for passage '{passage.Id}'."
                );
            }
        }
    }
}
