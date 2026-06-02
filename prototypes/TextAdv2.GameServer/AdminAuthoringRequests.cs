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
        => string.IsNullOrWhiteSpace(TravelMode)
            ? global::Atelia.TextAdv2.WorldTruth.TravelMode.Land
            : TravelModeCodec.TryParseStorageValue(TravelMode, out var value)
            ? value
            : throw new InvalidOperationException(
                $"Unsupported travelMode '{TravelMode}'. Allowed values: land, water, air, portal."
            );
}
