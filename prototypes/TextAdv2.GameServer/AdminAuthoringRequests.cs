using Atelia.TextAdv2.WorldTruth;

namespace Atelia.TextAdv2.GameServer;

internal sealed record CreateLocationRequest(
    string Id,
    string Name,
    string Description
);

internal sealed record CreateActorRequest(
    string Id,
    string Name,
    string CurrentLocationId
);

internal sealed record CreatePassageRequest(
    string Id,
    string LocationAId,
    string ExitNameFromA,
    string LocationBId,
    string ExitNameFromB,
    string? TravelMode,
    int? BaseTravelCost
) {
    public TravelMode ResolveTravelMode()
        => TryParseTravelMode(TravelMode, out var value)
            ? value
            : throw new InvalidOperationException(
                $"Unsupported travelMode '{TravelMode}'. Allowed values: land, water, air, portal."
            );

    private static bool TryParseTravelMode(string? value, out TravelMode result) {
        if (string.IsNullOrWhiteSpace(value)) {
            result = WorldTruth.TravelMode.Land;
            return true;
        }

        switch (value.Trim().ToLowerInvariant()) {
            case "land":
                result = WorldTruth.TravelMode.Land;
                return true;
            case "water":
                result = WorldTruth.TravelMode.Water;
                return true;
            case "air":
                result = WorldTruth.TravelMode.Air;
                return true;
            case "portal":
                result = WorldTruth.TravelMode.Portal;
                return true;
            default:
                result = default;
                return false;
        }
    }
}
