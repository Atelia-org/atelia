using Atelia.TextAdv2.WorldTruth;

namespace Atelia.TextAdv2.Session;

internal sealed record LandmarkProfile(
    string ProfileName,
    IReadOnlyList<string> LandmarkLocationIds
);

internal sealed class WorldSessionOptions {
    public static WorldSessionOptions Default { get; } = new();

    public Func<WorldState, LandmarkProfile?>? LandmarkProfileResolver { get; init; }
}
