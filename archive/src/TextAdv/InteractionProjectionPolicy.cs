using Atelia.StateJournal;

namespace Atelia.TextAdv;

internal static class InteractionProjectionPolicy {
    private const string WorldKey = "world";
    private const string ItemsKey = "items";
    private const string OwnerActorIdKey = "ownerActorId";

    internal static bool ShouldProject(
        DurableDict<string> root,
        string targetKind,
        string targetId,
        string actionKind
    ) {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actionKind);

        if (!string.Equals(targetKind, "item", StringComparison.OrdinalIgnoreCase)) {
            return true;
        }

        return ShouldProjectItemInteraction(root, targetId, actionKind);
    }

    private static bool ShouldProjectItemInteraction(
        DurableDict<string> root,
        string itemId,
        string actionKind
    ) {
        var world = root.GetOrThrow<DurableDict<string>>(WorldKey)!;
        if (!world.TryGet(ItemsKey, out DurableDict<string>? items)
            || items is null
            || !items.TryGet(itemId, out DurableDict<string>? item)
            || item is null) {
            return true;
        }

        var ownedByActor = item.TryGet(OwnerActorIdKey, out string? ownerActorId)
            && !string.IsNullOrWhiteSpace(ownerActorId);
        if (ownedByActor && InteractionActionKinds.IsPickup(actionKind)) {
            return false;
        }

        return true;
    }
}
