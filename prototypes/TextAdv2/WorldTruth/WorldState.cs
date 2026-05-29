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
    private const string LocationsKey = "locations";
    private const string PassagesKey = "passages";

    private readonly DurableDict<string> _root;

    private WorldState(DurableDict<string> root) {
        ArgumentNullException.ThrowIfNull(root);
        _root = root;
        EnsureKind(root, KindValue);

        _ = LocationsLedger;
        _ = PassagesLedger;
    }

    public DurableDict<string> Root => _root;

    public Revision Revision => _root.Revision;

    public DurableDict<string> LocationsLedger => _root.GetOrThrow<DurableDict<string>>(LocationsKey)!;

    public DurableDict<string> PassagesLedger => _root.GetOrThrow<DurableDict<string>>(PassagesKey)!;

    public static WorldState Create(Revision revision) {
        ArgumentNullException.ThrowIfNull(revision);

        var root = revision.CreateDict<string>();
        root.Upsert(KindKey, KindValue);
        root.Upsert(SchemaVersionKey, CurrentSchemaVersion);
        root.Upsert(LocationsKey, revision.CreateDict<string>());
        root.Upsert(PassagesKey, revision.CreateDict<string>());
        return new WorldState(root);
    }

    public static WorldState FromRoot(DurableDict<string> root) => new(root);

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
