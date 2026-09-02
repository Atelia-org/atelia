using Atelia.MemoPod;

namespace Atelia.Galatea.Server.CharacterMemory;

internal static class GalateaMemoRecallSourceIdCodec {
    internal const string Prefix = "memo-pod:v1/";
    internal const int CanonicalTextLength =
        12 + MemoPodId.TextLength + 1 + MemoId.TextLength;

    internal static string Format(MemoPodId podId, MemoId memoId) {
        if (string.IsNullOrEmpty(podId.Value)) {
            throw new ArgumentException(
                "A recall SourceId requires a canonical MemoPodId.",
                nameof(podId)
            );
        }
        if (string.IsNullOrEmpty(memoId.Value)) {
            throw new ArgumentException(
                "A recall SourceId requires a canonical MemoId.",
                nameof(memoId)
            );
        }

        string sourceId = string.Concat(
            Prefix,
            podId.Value,
            "/",
            memoId.Value
        );
        if (sourceId.Length != CanonicalTextLength
            || GalateaBoundedJson.StrictUtf8.GetByteCount(sourceId)
                > PlayerTurnObservationEnvelope
                    .MaximumRecallSourceIdUtf8Bytes) {
            throw new InvalidOperationException(
                "Canonical Memo recall SourceId exceeds its envelope contract."
            );
        }
        return sourceId;
    }

    internal static bool TryParse(
        string? sourceId,
        out MemoPodId podId,
        out MemoId memoId
    ) {
        podId = default;
        memoId = default;
        if (sourceId is null
            || sourceId.Length != CanonicalTextLength
            || !sourceId.StartsWith(Prefix, StringComparison.Ordinal)) {
            return false;
        }

        int podStart = Prefix.Length;
        int separator = podStart + MemoPodId.TextLength;
        if (sourceId[separator] != '/') {
            return false;
        }
        string podText = sourceId.Substring(
            podStart,
            MemoPodId.TextLength
        );
        string memoText = sourceId.Substring(separator + 1);
        if (!MemoPodId.TryParse(podText, out podId)
            || !MemoId.TryParse(memoText, out memoId)) {
            podId = default;
            memoId = default;
            return false;
        }

        return string.Equals(
            sourceId,
            Format(podId, memoId),
            StringComparison.Ordinal
        );
    }
}
