using Atelia.TextAdv2.WorldTruth;

namespace Atelia.TextAdv2.Runtime;

internal sealed record TextAdv2DefaultLandmarkProfile(
    string ProfileName,
    IReadOnlyList<string> LandmarkLocationIds
);

internal sealed class TextAdv2RuntimeOptions {
    public static TextAdv2RuntimeOptions Default { get; } = new();

    public Func<WorldState, TextAdv2DefaultLandmarkProfile?>? DefaultLandmarkProfileResolver { get; init; }
}
