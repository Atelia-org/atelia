using Atelia.StateJournal;

namespace Atelia.TextAdv2.WorldTruth;

/// <summary>
/// 地点只保存地点本体的世界真相。
/// 任何跨地点连接都必须放在 <see cref="Passage"/>，而不是在 Location 上重复维护 targetLocationId 或邻接缓存。
/// 这样共享路径事实只写一份，避免双端重复后慢慢漂移成不一致。
/// </summary>
internal sealed class Location {
    private const string KindValue = "location";
    private const string NameKey = "name";
    private const string DescriptionKey = "description";
    private const string MiningWorksiteKey = "miningWorksite";
    private const string TicksPerYieldKey = "ticksPerYield";
    private const string YieldItemIdKey = "yieldItemId";
    private const string YieldAmountKey = "yieldAmount";

    private readonly string _id;
    private readonly DurableDict<string> _data;

    internal Location(string id, DurableDict<string> data) {
        WorldState.ValidateEntityId(id, nameof(id));
        ArgumentNullException.ThrowIfNull(data);
        _id = id;
        _data = data;
        WorldState.EnsureKind(data, KindValue);

        _ = Name;
        _ = Description;
        _ = MiningWorksite;
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

    public string Description {
        get => _data.GetOrThrow<string>(DescriptionKey)!;
        set {
            ArgumentNullException.ThrowIfNull(value);
            _data.Upsert(DescriptionKey, value);
        }
    }

    public LocationMiningWorksiteProfile? MiningWorksite => ReadMiningWorksite();

    internal void SetMiningWorksite(LocationMiningWorksiteProfile profile) {
        ArgumentNullException.ThrowIfNull(profile);

        var data = _data.Revision.CreateDict<string>();
        data.Upsert(TicksPerYieldKey, profile.TicksPerYield);
        data.Upsert(YieldItemIdKey, profile.YieldItemId);
        data.Upsert(YieldAmountKey, profile.YieldAmount);
        _data.Upsert(MiningWorksiteKey, data);
    }

    internal void ClearMiningWorksite() {
        _ = _data.Remove(MiningWorksiteKey);
    }

    internal static Location Create(Revision revision, string id, string name, string description) {
        ArgumentNullException.ThrowIfNull(revision);
        WorldState.ValidateEntityId(id, nameof(id));
        WorldState.ValidateRequiredText(name, nameof(name));
        ArgumentNullException.ThrowIfNull(description);

        var data = revision.CreateDict<string>();
        data.Upsert(WorldState.KindKey, KindValue);
        data.Upsert(NameKey, name);
        data.Upsert(DescriptionKey, description);
        return new Location(id, data);
    }

    private LocationMiningWorksiteProfile? ReadMiningWorksite() {
        if (!_data.TryGet(MiningWorksiteKey, out DurableDict<string>? data) || data is null) {
            return null;
        }

        return new LocationMiningWorksiteProfile(
            data.GetOrThrow<int>(TicksPerYieldKey),
            data.GetOrThrow<string>(YieldItemIdKey)!,
            data.GetOrThrow<int>(YieldAmountKey)
        );
    }
}

internal sealed record LocationMiningWorksiteProfile {
    public LocationMiningWorksiteProfile(int ticksPerYield, string yieldItemId, int yieldAmount = 1) {
        ArgumentOutOfRangeException.ThrowIfLessThan(ticksPerYield, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(yieldItemId);
        ArgumentOutOfRangeException.ThrowIfLessThan(yieldAmount, 1);

        TicksPerYield = ticksPerYield;
        YieldItemId = yieldItemId;
        YieldAmount = yieldAmount;
    }

    public int TicksPerYield { get; }

    public string YieldItemId { get; }

    public int YieldAmount { get; }
}
