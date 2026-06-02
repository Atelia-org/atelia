using Atelia.StateJournal;

namespace Atelia.TextAdv2.WorldTruth;

/// <summary>
/// Actor 是世界真相层里的可移动主体。
///
/// 当前阶段只承载最小空间语义：身份、名字、当前位置。
/// 位置迁移 SHOULD 通过 <see cref="WorldState.MoveActorAlongPassage"/> 这类合法移动 API 完成，
/// 而不是让外层随意改写 currentLocationId 形成“传送式”隐性真相。
/// </summary>
internal sealed class Actor {
    private const string KindValue = "actor";
    private const string NameKey = "name";
    private const string CurrentLocationIdKey = "currentLocationId";
    private const string EmbodiedStateKey = "embodiedState";
    private const string InventoryKey = "inventory";

    private const string IdleEmbodiedStateKind = "idle";
    private const string RouteFollowingEmbodiedStateKind = "route-following";
    private const string MiningEmbodiedStateKind = "mining";
    private const string ProcessKindKey = "processKind";
    private const string IsInterruptibleKey = "isInterruptible";
    private const string DestinationLocationIdKey = "destinationLocationId";
    private const string RemainingPassageIdsKey = "remainingPassageIds";
    private const string RemainingTravelTicksOnCurrentLegKey = "remainingTravelTicksOnCurrentLeg";
    private const string WorksiteIdKey = "worksiteId";
    private const string ProgressTicksInCurrentCycleKey = "progressTicksInCurrentCycle";
    private const string TicksPerYieldKey = "ticksPerYield";
    private const string YieldItemIdKey = "yieldItemId";
    private const string ProducedYieldCountKey = "producedYieldCount";
    private const string YieldAmountKey = "yieldAmount";

    private readonly string _id;
    private readonly DurableDict<string> _data;

    internal Actor(string id, DurableDict<string> data) {
        WorldState.ValidateEntityId(id, nameof(id));
        ArgumentNullException.ThrowIfNull(data);
        _id = id;
        _data = data;
        WorldState.EnsureKind(data, KindValue);

        _ = Name;
        _ = CurrentLocationId;
        _ = EmbodiedState;
        ValidateInventory();
    }

    internal DurableDict<string> Data => _data;

    public string Id => _id;

    public string Name {
        get => _data.GetOrThrow<string>(NameKey)!;
        set {
            WorldState.ValidateRequiredText(value, nameof(value));
            _data.Upsert(NameKey, value);
        }
    }

    public string CurrentLocationId => _data.GetOrThrow<string>(CurrentLocationIdKey)!;

    public ActorEmbodiedState EmbodiedState => ReadEmbodiedState();

    public IEnumerable<ActorInventoryEntry> Inventory => EnumerateInventoryEntries();

    internal void MoveTo(string locationId) {
        WorldState.ValidateEntityId(locationId, nameof(locationId));
        _data.Upsert(CurrentLocationIdKey, locationId);
    }

    internal void SetEmbodiedState(ActorEmbodiedState state) {
        ArgumentNullException.ThrowIfNull(state);
        _data.Upsert(EmbodiedStateKey, CreateEmbodiedStateData(_data.Revision, state));
    }

    internal void AddCarriedResource(string itemId, long quantity) {
        WorldState.ValidateEntityId(itemId, nameof(itemId));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);

        long updated = checked(GetCarriedResourceCount(itemId) + quantity);
        InventoryData.Upsert(itemId, updated);
    }

    public long GetCarriedResourceCount(string itemId) {
        WorldState.ValidateEntityId(itemId, nameof(itemId));
        return InventoryData.GetOr(itemId, 0L);
    }

    internal static Actor Create(Revision revision, string id, string name, string currentLocationId) {
        ArgumentNullException.ThrowIfNull(revision);
        WorldState.ValidateEntityId(id, nameof(id));
        WorldState.ValidateRequiredText(name, nameof(name));
        WorldState.ValidateEntityId(currentLocationId, nameof(currentLocationId));

        var data = revision.CreateDict<string>();
        data.Upsert(WorldState.KindKey, KindValue);
        data.Upsert(NameKey, name);
        data.Upsert(CurrentLocationIdKey, currentLocationId);
        data.Upsert(EmbodiedStateKey, CreateEmbodiedStateData(revision, ActorEmbodiedState.Idle));
        data.Upsert(InventoryKey, revision.CreateDict<string, long>());
        return new Actor(id, data);
    }

    private DurableDict<string> EmbodiedStateData => _data.GetOrThrow<DurableDict<string>>(EmbodiedStateKey)!;

    private DurableDict<string, long> InventoryData => _data.GetOrThrow<DurableDict<string, long>>(InventoryKey)!;

    private ActorEmbodiedState ReadEmbodiedState() {
        var data = EmbodiedStateData;
        string processKind = data.GetOrThrow<string>(ProcessKindKey)!;

        return processKind switch {
            IdleEmbodiedStateKind => ActorEmbodiedState.Idle,
            RouteFollowingEmbodiedStateKind => new RouteFollowingActorProcessState(
                data.GetOrThrow<string>(DestinationLocationIdKey)!,
                ReadOrderedStringValues(data.GetOrThrow<DurableDict<int, string>>(RemainingPassageIdsKey)!),
                data.GetOrThrow<int>(RemainingTravelTicksOnCurrentLegKey),
                data.GetOrThrow<bool>(IsInterruptibleKey)
            ),
            MiningEmbodiedStateKind => new MiningActorProcessState(
                data.GetOrThrow<string>(WorksiteIdKey)!,
                data.GetOrThrow<int>(ProgressTicksInCurrentCycleKey),
                data.GetOrThrow<int>(TicksPerYieldKey),
                data.GetOrThrow<string>(YieldItemIdKey)!,
                data.GetOrThrow<long>(ProducedYieldCountKey),
                data.GetOrThrow<int>(YieldAmountKey),
                data.GetOrThrow<bool>(IsInterruptibleKey)
            ),
            _ => throw new InvalidOperationException(
                $"Actor '{Id}' has unsupported embodied process kind '{processKind}'."
            ),
        };
    }

    private IEnumerable<ActorInventoryEntry> EnumerateInventoryEntries() {
        foreach (string itemId in InventoryData.Keys.OrderBy(static key => key, StringComparer.Ordinal)) {
            if (string.IsNullOrWhiteSpace(itemId)) {
                throw new InvalidOperationException(
                    $"Actor '{Id}' has invalid carried resource item id '{itemId ?? "<null>"}'."
                );
            }

            long quantity = InventoryData.GetOrThrow(itemId);
            if (quantity < 0) {
                throw new InvalidOperationException(
                    $"Actor '{Id}' has negative carried resource quantity {quantity} for '{itemId}'."
                );
            }

            yield return new ActorInventoryEntry(itemId, quantity);
        }
    }

    private void ValidateInventory() {
        foreach (var _ in EnumerateInventoryEntries()) {
        }
    }

    private static DurableDict<string> CreateEmbodiedStateData(Revision revision, ActorEmbodiedState state) {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(state);

        var data = revision.CreateDict<string>();

        switch (state) {
            case IdleActorEmbodiedState:
                data.Upsert(ProcessKindKey, IdleEmbodiedStateKind);
                return data;

            case RouteFollowingActorProcessState routeFollowing:
                data.Upsert(ProcessKindKey, RouteFollowingEmbodiedStateKind);
                data.Upsert(IsInterruptibleKey, routeFollowing.IsInterruptible);
                data.Upsert(DestinationLocationIdKey, routeFollowing.DestinationLocationId);
                data.Upsert(RemainingPassageIdsKey, CreateOrderedStringValues(revision, routeFollowing.RemainingPassageIds));
                data.Upsert(RemainingTravelTicksOnCurrentLegKey, routeFollowing.RemainingTravelTicksOnCurrentLeg);
                return data;

            case MiningActorProcessState mining:
                data.Upsert(ProcessKindKey, MiningEmbodiedStateKind);
                data.Upsert(IsInterruptibleKey, mining.IsInterruptible);
                data.Upsert(WorksiteIdKey, mining.WorksiteId);
                data.Upsert(ProgressTicksInCurrentCycleKey, mining.ProgressTicksInCurrentCycle);
                data.Upsert(TicksPerYieldKey, mining.TicksPerYield);
                data.Upsert(YieldItemIdKey, mining.YieldItemId);
                data.Upsert(ProducedYieldCountKey, mining.ProducedYieldCount);
                data.Upsert(YieldAmountKey, mining.YieldAmount);
                return data;

            default:
                throw new InvalidOperationException(
                    $"Unsupported actor embodied state type '{state.GetType().Name}'."
                );
        }
    }

    private static DurableDict<int, string> CreateOrderedStringValues(Revision revision, IReadOnlyList<string> values) {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(values);

        var data = revision.CreateDict<int, string>();
        for (int i = 0; i < values.Count; i++) {
            WorldState.ValidateEntityId(values[i], $"values[{i}]");
            data.Upsert(i, values[i]);
        }

        return data;
    }

    private static string[] ReadOrderedStringValues(DurableDict<int, string> data) {
        ArgumentNullException.ThrowIfNull(data);

        return data.Keys
            .OrderBy(static key => key)
            .Select(key => data.GetOrThrow(key)!)
            .ToArray();
    }
}

internal sealed record ActorInventoryEntry(string ItemId, long Quantity);
