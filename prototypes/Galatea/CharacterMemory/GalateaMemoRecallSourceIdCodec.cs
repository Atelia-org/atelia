using Atelia.MemoPod;

namespace Atelia.Galatea.Server.CharacterMemory;

internal static class GalateaMemoRecallSourceIdCodec {
    internal const string Prefix = Input.GalateaMemoRecallSourceIdCodec.Prefix;
    internal const int CanonicalTextLength = Input.GalateaMemoRecallSourceIdCodec.CanonicalTextLength;
    internal static string Format(MemoPodId podId, MemoId memoId) => Input.GalateaMemoRecallSourceIdCodec.Format(podId, memoId);
    internal static bool TryParse(string? sourceId, out MemoPodId podId, out MemoId memoId)
        => Input.GalateaMemoRecallSourceIdCodec.TryParse(sourceId, out podId, out memoId);
}
