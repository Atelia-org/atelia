using Atelia.StateJournal;

namespace Atelia.TextAdv2.WorldTruth;

/// <summary>
/// Passage 是跨地点连接的唯一真相。
///
/// 字段边界固定如下：
/// - Endpoint 只管“某一端怎么看这条路”，例如本地出口名、端点提示。
/// - Shared 只管两端共享且应只维护一份的事实，例如交通方式、基础路程、整条路共用的局部状况。
/// - Direction 只管方向性规则，例如能否通行、顺逆向额外代价、单向条件说明。
///
/// 不要把共享事实复制到两个 Endpoint，也不要把方向规则误当成整条路的共享状态。
/// 例如“整条山道都在下雨”通常应属于更高层天气系统；只有明确是这条 Passage 独有的局部异常，才放进 SharedConditionNote。
/// </summary>
internal sealed class Passage {
    private const string KindValue = "passage";
    private const string IdKey = "id";
    private const string EndpointAKey = "endpointA";
    private const string EndpointBKey = "endpointB";
    private const string SharedKey = "shared";
    private const string FromAToBKey = "fromAToB";
    private const string FromBToAKey = "fromBToA";
    private const string TravelModeKey = "travelMode";
    private const string BaseTravelCostKey = "baseTravelCost";
    private const string SharedConditionNoteKey = "sharedConditionNote";

    private readonly DurableDict<string> _data;

    internal Passage(DurableDict<string> data) {
        ArgumentNullException.ThrowIfNull(data);
        _data = data;
        WorldState.EnsureKind(data, KindValue);

        _ = Id;
        _ = EndpointA;
        _ = EndpointB;
        _ = FromAToB;
        _ = FromBToA;
        _ = TravelMode;
        _ = BaseTravelCost;
        _ = SharedConditionNote;
    }

    public DurableDict<string> Data => _data;

    public string Id => _data.GetOrThrow<string>(IdKey)!;

    /// <summary>
    /// 端点 A 代表“从 A 这一端看这条路”的本地锚点和命名，不承载整条路的共享事实。
    /// </summary>
    public PassageEndpoint EndpointA => new(_data.GetOrThrow<DurableDict<string>>(EndpointAKey)!);

    /// <summary>
    /// 端点 B 代表“从 B 这一端看这条路”的本地锚点和命名，不承载整条路的共享事实。
    /// </summary>
    public PassageEndpoint EndpointB => new(_data.GetOrThrow<DurableDict<string>>(EndpointBKey)!);

    /// <summary>
    /// A -> B 的方向规则。
    /// 这里只放方向差异，不复制整条路共有的事实。
    /// </summary>
    public PassageDirectionRule FromAToB => new(_data.GetOrThrow<DurableDict<string>>(FromAToBKey)!);

    /// <summary>
    /// B -> A 的方向规则。
    /// reversible 不是单独的主字段；只要这个方向 enabled，就表示当前可逆。
    /// </summary>
    public PassageDirectionRule FromBToA => new(_data.GetOrThrow<DurableDict<string>>(FromBToAKey)!);

    public TravelMode TravelMode {
        get => TravelModeCodec.FromStorageValue(SharedData.GetOrThrow<string>(TravelModeKey)!);
        set => SharedData.Upsert(TravelModeKey, value.ToStorageValue());
    }

    public int BaseTravelCost {
        get => SharedData.GetOrThrow<int>(BaseTravelCostKey);
        set {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            SharedData.Upsert(BaseTravelCostKey, value);
        }
    }

    /// <summary>
    /// Passage 级别、且两端共享的局部状况说明。
    /// 这里只留给“这条路本身”的共享异常，不用于承载更高层的全局天气系统。
    /// </summary>
    public string SharedConditionNote {
        get => SharedData.GetOrThrow<string>(SharedConditionNoteKey)!;
        set {
            ArgumentNullException.ThrowIfNull(value);
            SharedData.Upsert(SharedConditionNoteKey, value);
        }
    }

    public bool Connects(string locationId)
        => string.Equals(EndpointA.LocationId, locationId, StringComparison.Ordinal)
            || string.Equals(EndpointB.LocationId, locationId, StringComparison.Ordinal);

    public PassageEndpoint GetEndpointFor(string locationId)
        => string.Equals(EndpointA.LocationId, locationId, StringComparison.Ordinal)
            ? EndpointA
            : string.Equals(EndpointB.LocationId, locationId, StringComparison.Ordinal)
                ? EndpointB
                : throw new InvalidOperationException($"Passage '{Id}' does not connect location '{locationId}'.");

    public PassageEndpoint GetOppositeEndpoint(string locationId)
        => string.Equals(EndpointA.LocationId, locationId, StringComparison.Ordinal)
            ? EndpointB
            : string.Equals(EndpointB.LocationId, locationId, StringComparison.Ordinal)
                ? EndpointA
                : throw new InvalidOperationException($"Passage '{Id}' does not connect location '{locationId}'.");

    public PassageDirectionRule GetDirectionFrom(string locationId)
        => string.Equals(EndpointA.LocationId, locationId, StringComparison.Ordinal)
            ? FromAToB
            : string.Equals(EndpointB.LocationId, locationId, StringComparison.Ordinal)
                ? FromBToA
                : throw new InvalidOperationException($"Passage '{Id}' does not connect location '{locationId}'.");

    public string GetOtherLocationId(string locationId) => GetOppositeEndpoint(locationId).LocationId;

    internal static Passage Create(
        Revision revision,
        string id,
        string locationAId,
        string exitNameFromA,
        string locationBId,
        string exitNameFromB,
        TravelMode travelMode,
        int baseTravelCost
    ) {
        ArgumentNullException.ThrowIfNull(revision);
        WorldState.ValidateEntityId(id, nameof(id));
        WorldState.ValidateEntityId(locationAId, nameof(locationAId));
        WorldState.ValidateEntityId(locationBId, nameof(locationBId));
        WorldState.ValidateRequiredText(exitNameFromA, nameof(exitNameFromA));
        WorldState.ValidateRequiredText(exitNameFromB, nameof(exitNameFromB));
        ArgumentOutOfRangeException.ThrowIfLessThan(baseTravelCost, 1);

        var data = revision.CreateDict<string>();
        data.Upsert(WorldState.KindKey, KindValue);
        data.Upsert(IdKey, id);
        data.Upsert(EndpointAKey, PassageEndpoint.CreateData(revision, locationAId, exitNameFromA));
        data.Upsert(EndpointBKey, PassageEndpoint.CreateData(revision, locationBId, exitNameFromB));
        data.Upsert(SharedKey, CreateSharedData(revision, travelMode, baseTravelCost));
        data.Upsert(FromAToBKey, PassageDirectionRule.CreateData(revision));
        data.Upsert(FromBToAKey, PassageDirectionRule.CreateData(revision));
        return new Passage(data);
    }

    private DurableDict<string> SharedData => _data.GetOrThrow<DurableDict<string>>(SharedKey)!;

    private static DurableDict<string> CreateSharedData(Revision revision, TravelMode travelMode, int baseTravelCost) {
        var shared = revision.CreateDict<string>();
        shared.Upsert(TravelModeKey, travelMode.ToStorageValue());
        shared.Upsert(BaseTravelCostKey, baseTravelCost);
        shared.Upsert(SharedConditionNoteKey, string.Empty);
        return shared;
    }
}

/// <summary>
/// PassageEndpoint 只保存某个地点对这条 Passage 的本地命名与端点提示。
/// 它不能承载跨地点共享事实，也不负责声明另一端怎么称呼这条路。
/// </summary>
internal sealed class PassageEndpoint {
    private const string LocationIdKey = "locationId";
    private const string ExitNameKey = "exitName";
    private const string LocalViewNoteKey = "localViewNote";

    private readonly DurableDict<string> _data;

    internal PassageEndpoint(DurableDict<string> data) {
        ArgumentNullException.ThrowIfNull(data);
        _data = data;

        _ = LocationId;
        _ = ExitName;
        _ = LocalViewNote;
    }

    public string LocationId {
        get => _data.GetOrThrow<string>(LocationIdKey)!;
        set {
            WorldState.ValidateEntityId(value, nameof(value));
            _data.Upsert(LocationIdKey, value);
        }
    }

    public string ExitName {
        get => _data.GetOrThrow<string>(ExitNameKey)!;
        set {
            WorldState.ValidateRequiredText(value, nameof(value));
            _data.Upsert(ExitNameKey, value);
        }
    }

    public string LocalViewNote {
        get => _data.GetOrThrow<string>(LocalViewNoteKey)!;
        set {
            ArgumentNullException.ThrowIfNull(value);
            _data.Upsert(LocalViewNoteKey, value);
        }
    }

    internal static DurableDict<string> CreateData(Revision revision, string locationId, string exitName) {
        ArgumentNullException.ThrowIfNull(revision);
        WorldState.ValidateEntityId(locationId, nameof(locationId));
        WorldState.ValidateRequiredText(exitName, nameof(exitName));

        var data = revision.CreateDict<string>();
        data.Upsert(LocationIdKey, locationId);
        data.Upsert(ExitNameKey, exitName);
        data.Upsert(LocalViewNoteKey, string.Empty);
        return data;
    }
}

/// <summary>
/// PassageDirectionRule 只保存某个方向独有的规则和额外代价。
/// 基础路程、交通方式等共享事实必须留在 Passage shared 段里，只能在这里做增量修正。
/// </summary>
internal sealed class PassageDirectionRule {
    private const string EnabledKey = "enabled";
    private const string TravelCostModifierKey = "travelCostModifier";
    private const string DirectionConditionNoteKey = "directionConditionNote";

    private readonly DurableDict<string> _data;

    internal PassageDirectionRule(DurableDict<string> data) {
        ArgumentNullException.ThrowIfNull(data);
        _data = data;

        _ = IsEnabled;
        _ = TravelCostModifier;
        _ = DirectionConditionNote;
    }

    public bool IsEnabled {
        get => _data.GetOrThrow<bool>(EnabledKey);
        set => _data.Upsert(EnabledKey, value);
    }

    public int TravelCostModifier {
        get => _data.GetOrThrow<int>(TravelCostModifierKey);
        set => _data.Upsert(TravelCostModifierKey, value);
    }

    public string DirectionConditionNote {
        get => _data.GetOrThrow<string>(DirectionConditionNoteKey)!;
        set {
            ArgumentNullException.ThrowIfNull(value);
            _data.Upsert(DirectionConditionNoteKey, value);
        }
    }

    internal static DurableDict<string> CreateData(Revision revision) {
        ArgumentNullException.ThrowIfNull(revision);

        var data = revision.CreateDict<string>();
        data.Upsert(EnabledKey, true);
        data.Upsert(TravelCostModifierKey, 0);
        data.Upsert(DirectionConditionNoteKey, string.Empty);
        return data;
    }
}

internal enum TravelMode {
    Land,
    Water,
    Air,
    Portal,
}

internal static class TravelModeCodec {
    public static TravelMode FromStorageValue(string value)
        => value switch {
            "land" => TravelMode.Land,
            "water" => TravelMode.Water,
            "air" => TravelMode.Air,
            "portal" => TravelMode.Portal,
            _ => throw new InvalidOperationException($"Unsupported travel mode '{value}'."),
        };

    public static string ToStorageValue(this TravelMode value)
        => value switch {
            TravelMode.Land => "land",
            TravelMode.Water => "water",
            TravelMode.Air => "air",
            TravelMode.Portal => "portal",
            _ => throw new InvalidOperationException($"Unsupported travel mode '{value}'."),
        };
}
