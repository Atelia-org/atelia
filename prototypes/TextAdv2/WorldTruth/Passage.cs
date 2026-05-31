using Atelia.StateJournal;

namespace Atelia.TextAdv2.WorldTruth;

internal sealed class PassageView {
    private readonly Passage _passage;

    internal PassageView(Passage passage) {
        ArgumentNullException.ThrowIfNull(passage);
        _passage = passage;
    }

    public string Id => _passage.Id;

    public PassageEndpointView EndpointA => new(_passage.EndpointA);

    public PassageEndpointView EndpointB => new(_passage.EndpointB);

    public PassageDirectionRuleView FromAToB => new(_passage.FromAToB);

    public PassageDirectionRuleView FromBToA => new(_passage.FromBToA);

    public TravelMode TravelMode => _passage.TravelMode;

    public int BaseTravelCost => _passage.BaseTravelCost;

    public string SharedConditionNote => _passage.SharedConditionNote;

    public bool Connects(string locationId) => _passage.Connects(locationId);

    public PassageEndpointView GetEndpointFor(string locationId) => new(_passage.GetEndpointFor(locationId));

    public PassageEndpointView GetOppositeEndpoint(string locationId) => new(_passage.GetOppositeEndpoint(locationId));

    public PassageDirectionRuleView GetDirectionFrom(string locationId) => new(_passage.GetDirectionFrom(locationId));

    public string GetOtherLocationId(string locationId) => _passage.GetOtherLocationId(locationId);
}

internal sealed class PassageEndpointView {
    private readonly PassageEndpoint _endpoint;

    internal PassageEndpointView(PassageEndpoint endpoint) {
        ArgumentNullException.ThrowIfNull(endpoint);
        _endpoint = endpoint;
    }

    public string LocationId => _endpoint.LocationId;

    public string ExitName => _endpoint.ExitName;

    public string LocalViewNote => _endpoint.LocalViewNote;
}

internal sealed class PassageDirectionRuleView {
    private readonly PassageDirectionRule _direction;

    internal PassageDirectionRuleView(PassageDirectionRule direction) {
        ArgumentNullException.ThrowIfNull(direction);
        _direction = direction;
    }

    public bool IsEnabled => _direction.IsEnabled;

    public int TravelCostModifier => _direction.TravelCostModifier;

    public string DirectionConditionNote => _direction.DirectionConditionNote;

    public int TotalTravelCost(PassageView passage) {
        ArgumentNullException.ThrowIfNull(passage);
        return passage.BaseTravelCost + TravelCostModifier;
    }
}

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
        ValidateNonNegativeTotalTravelCosts();
    }

    internal DurableDict<string> Data => _data;

    internal PassageView AsView() => new(this);

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
    }

    public int BaseTravelCost => SharedData.GetOrThrow<int>(BaseTravelCostKey);

    /// <summary>
    /// Passage 级别、且两端共享的局部状况说明。
    /// 这里只留给“这条路本身”的共享异常，不用于承载更高层的全局天气系统。
    /// </summary>
    public string SharedConditionNote => SharedData.GetOrThrow<string>(SharedConditionNoteKey)!;

    internal void SetTravelMode(TravelMode value) => SharedData.Upsert(TravelModeKey, value.ToStorageValue());

    internal void SetBaseTravelCost(int value) {
        ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
        EnsureTotalTravelCostIsNonNegative(EndpointA.LocationId, EndpointB.LocationId, value, FromAToB.TravelCostModifier);
        EnsureTotalTravelCostIsNonNegative(EndpointB.LocationId, EndpointA.LocationId, value, FromBToA.TravelCostModifier);
        SharedData.Upsert(BaseTravelCostKey, value);
    }

    internal void SetSharedConditionNote(string value) {
        ArgumentNullException.ThrowIfNull(value);
        SharedData.Upsert(SharedConditionNoteKey, value);
    }

    internal void SetEndpointLocalViewNote(string locationId, string value) => GetEndpointFor(locationId).SetLocalViewNote(value);

    internal void SetDirectionEnabledFrom(string locationId, bool isEnabled) => GetDirectionFrom(locationId).SetIsEnabled(isEnabled);

    internal void SetDirectionTravelCostModifierFrom(string locationId, int value) {
        EnsureTotalTravelCostIsNonNegative(locationId, GetOtherLocationId(locationId), BaseTravelCost, value);
        GetDirectionFrom(locationId).SetTravelCostModifier(value);
    }

    internal void SetDirectionConditionNoteFrom(string locationId, string value)
        => GetDirectionFrom(locationId).SetDirectionConditionNote(value);

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

    private void ValidateNonNegativeTotalTravelCosts() {
        EnsureTotalTravelCostIsNonNegative(EndpointA.LocationId, EndpointB.LocationId, BaseTravelCost, FromAToB.TravelCostModifier);
        EnsureTotalTravelCostIsNonNegative(EndpointB.LocationId, EndpointA.LocationId, BaseTravelCost, FromBToA.TravelCostModifier);
    }

    private void EnsureTotalTravelCostIsNonNegative(
        string fromLocationId,
        string toLocationId,
        int baseTravelCost,
        int travelCostModifier
    ) {
        int totalTravelCost = baseTravelCost + travelCostModifier;
        if (totalTravelCost < 0) {
            throw new InvalidOperationException(
                $"Negative travel cost is not allowed in WorldTruth: passage '{Id}' from '{fromLocationId}' to '{toLocationId}' has total cost {totalTravelCost} (base={baseTravelCost}, modifier={travelCostModifier})."
            );
        }
    }

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

    public string LocationId => _data.GetOrThrow<string>(LocationIdKey)!;

    public string ExitName => _data.GetOrThrow<string>(ExitNameKey)!;

    public string LocalViewNote => _data.GetOrThrow<string>(LocalViewNoteKey)!;

    internal void SetLocalViewNote(string value) {
        ArgumentNullException.ThrowIfNull(value);
        _data.Upsert(LocalViewNoteKey, value);
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

    public bool IsEnabled => _data.GetOrThrow<bool>(EnabledKey);

    public int TravelCostModifier => _data.GetOrThrow<int>(TravelCostModifierKey);

    public string DirectionConditionNote => _data.GetOrThrow<string>(DirectionConditionNoteKey)!;

    public int TotalTravelCost(Passage passage) {
        ArgumentNullException.ThrowIfNull(passage);
        return passage.BaseTravelCost + TravelCostModifier;
    }

    internal void SetIsEnabled(bool value) => _data.Upsert(EnabledKey, value);

    internal void SetTravelCostModifier(int value) => _data.Upsert(TravelCostModifierKey, value);

    internal void SetDirectionConditionNote(string value) {
        ArgumentNullException.ThrowIfNull(value);
        _data.Upsert(DirectionConditionNoteKey, value);
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

public enum TravelMode {
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

    public static int TotalTravelCost(this PassageDirectionRule direction, Passage passage) {
        ArgumentNullException.ThrowIfNull(direction);
        ArgumentNullException.ThrowIfNull(passage);
        return passage.BaseTravelCost + direction.TravelCostModifier;
    }
}
