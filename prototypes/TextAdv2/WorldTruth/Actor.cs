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

    internal void MoveTo(string locationId) {
        WorldState.ValidateEntityId(locationId, nameof(locationId));
        _data.Upsert(CurrentLocationIdKey, locationId);
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
        return new Actor(id, data);
    }
}
