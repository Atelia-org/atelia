using Atelia.TextAdv2.WorldTruth;

namespace Atelia.TextAdv2.ReadModel;

/// <summary>
/// 由 durable world truth 派生出的 host-local actor occupancy index。
///
/// 唯一真相仍然是 <see cref="Actor.CurrentLocationId"/>；
/// 本索引只为加速“某地点当前有哪些 actor”这类高频 read path，
/// 不应反向成为 actor 位置的 authoritative source。
/// </summary>
internal sealed class ActorOccupancyIndex {
    private readonly Dictionary<string, string> _locationByActorId;
    private readonly Dictionary<string, SortedSet<string>> _actorIdsByLocationId;

    private ActorOccupancyIndex(
        Dictionary<string, string> locationByActorId,
        Dictionary<string, SortedSet<string>> actorIdsByLocationId
    ) {
        _locationByActorId = locationByActorId ?? throw new ArgumentNullException(nameof(locationByActorId));
        _actorIdsByLocationId = actorIdsByLocationId ?? throw new ArgumentNullException(nameof(actorIdsByLocationId));
    }

    public static ActorOccupancyIndex Build(WorldState world) {
        ArgumentNullException.ThrowIfNull(world);

        var locationByActorId = new Dictionary<string, string>(StringComparer.Ordinal);
        var actorIdsByLocationId = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var occupancy = new ActorOccupancyIndex(locationByActorId, actorIdsByLocationId);

        foreach (var actor in world.EnumerateActors()) {
            occupancy.AddActor(actor.Id, actor.CurrentLocationId);
        }

        return occupancy;
    }

    public IEnumerable<string> EnumerateActorIdsAtLocation(string locationId) {
        WorldState.ValidateEntityId(locationId, nameof(locationId));
        return _actorIdsByLocationId.TryGetValue(locationId, out var actorIds) ? actorIds : [];
    }

    public void AddActor(string actorId, string locationId) {
        WorldState.ValidateEntityId(actorId, nameof(actorId));
        WorldState.ValidateEntityId(locationId, nameof(locationId));

        if (_locationByActorId.TryGetValue(actorId, out var existingLocationId)) {
            throw new InvalidOperationException(
                $"Actor occupancy already tracks actor '{actorId}' at location '{existingLocationId}'."
            );
        }

        // Bucket first: if this throws, _locationByActorId stays clean.
        if (!GetOrCreateActorBucket(locationId).Add(actorId)) {
            throw new InvalidOperationException(
                $"Actor occupancy already lists actor '{actorId}' inside location '{locationId}'."
            );
        }

        _locationByActorId.Add(actorId, locationId);
    }

    public void MoveActor(string actorId, string fromLocationId, string toLocationId) {
        WorldState.ValidateEntityId(actorId, nameof(actorId));
        WorldState.ValidateEntityId(fromLocationId, nameof(fromLocationId));
        WorldState.ValidateEntityId(toLocationId, nameof(toLocationId));

        if (!_locationByActorId.TryGetValue(actorId, out var trackedLocationId)) {
            throw new InvalidOperationException(
                $"Actor occupancy does not track actor '{actorId}' during move '{fromLocationId}' -> '{toLocationId}'."
            );
        }

        if (!string.Equals(trackedLocationId, fromLocationId, StringComparison.Ordinal)) {
            throw new InvalidOperationException(
                $"Actor occupancy tracks actor '{actorId}' at '{trackedLocationId}', not at expected source '{fromLocationId}'."
            );
        }

        if (string.Equals(fromLocationId, toLocationId, StringComparison.Ordinal)) {
            return;
        }

        // Add to target first so a subsequent failure doesn't leave the actor
        // removed from the source bucket with no fallback.
        if (!GetOrCreateActorBucket(toLocationId).Add(actorId)) {
            throw new InvalidOperationException(
                $"Actor occupancy target bucket '{toLocationId}' already contains actor '{actorId}'."
            );
        }

        if (!_actorIdsByLocationId.TryGetValue(fromLocationId, out var sourceActorIds)
            || !sourceActorIds.Remove(actorId)) {
            throw new InvalidOperationException(
                $"Actor occupancy source bucket '{fromLocationId}' does not contain actor '{actorId}'."
            );
        }

        if (sourceActorIds.Count == 0) {
            _actorIdsByLocationId.Remove(fromLocationId);
        }

        _locationByActorId[actorId] = toLocationId;
    }

    private SortedSet<string> GetOrCreateActorBucket(string locationId) {
        if (!_actorIdsByLocationId.TryGetValue(locationId, out var actorIds)) {
            actorIds = new SortedSet<string>(StringComparer.Ordinal);
            _actorIdsByLocationId.Add(locationId, actorIds);
        }

        return actorIds;
    }
}
