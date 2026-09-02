using Atelia.MemoPod;

namespace Atelia.Galatea.Server.CharacterMemory;

internal static class GalateaMemoRecallMvpPolicy {
    internal const int MaxResults = 8;
    internal const int MaximumQueryUtf8Bytes =
        MemoPodLimits.MaximumRecallQueryUtf8Bytes;
    internal const int MaximumRecentVisibleActionCount = 1;
    internal const int MaximumFrozenPromptUtf8Bytes =
        MemoPodLimits.MaximumRenderedPromptUtf8Bytes;
    internal const int MaximumHydratedExactTextUtf8Bytes =
        MaxResults * MemoPodLimits.MaximumMemoExactTextUtf8Bytes;
}
