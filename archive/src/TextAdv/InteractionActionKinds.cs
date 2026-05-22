namespace Atelia.TextAdv;

internal static class InteractionActionKinds {
    internal const string Take = "take";
    internal const string PickUpLegacy = "pick-up";

    internal static string Canonicalize(string actionKind) {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionKind);

        var normalized = actionKind.Trim().ToLowerInvariant();
        return normalized switch {
            PickUpLegacy => Take,
            _ => normalized
        };
    }

    internal static bool IsPickup(string actionKind)
        => string.Equals(Canonicalize(actionKind), Take, StringComparison.Ordinal);
}
