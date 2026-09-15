using Atelia.MemoPod;

namespace Atelia.Galatea.Input;

/// <summary>Stable bounds of galatea.observation.v1, independent of Markdown presentation.</summary>
internal static class GalateaObservationLimits {
    internal const int MaximumContentUtf8Bytes = 1024 * 1024;
    internal const int MaximumPlayerTextUtf8Bytes = 64 * 1024;
    internal const int ExternalIntervalMinutes = 10;
    internal const int MaximumRecallSourceIdUtf8Bytes = 512;
    // Preserve v1's accepted gist/summary and legacy-body bound, including its historical 21-byte labels.
    internal const int MaximumRecallBodyUtf8Bytes = MemoPodLimits.MaximumMemoTitleUtf8Bytes + MemoPodLimits.MaximumMemoExactTextUtf8Bytes + 21;
    internal const int MaximumRecallCount = 32;
    internal const int MaximumReplyUtf8Bytes = 256 * 1024;
    internal const int MaximumFailureUtf8Bytes = 4 * 1024;
    internal const int MaximumNoteSaveReceiptUtf8Bytes = 512 * 1024;
    internal const int MaximumNoticeCount = 16;
    internal const int MaximumMailSenderUtf8Bytes = 1024;
    internal const int MaximumMailRecipientUtf8Bytes = 1024;
    internal const int MaximumMailSubjectUtf8Bytes = 4 * 1024;
    internal const int MaximumMailBodyUtf8Bytes = 64 * 1024;
    internal const int MaximumNoteExactTextUtf8Bytes = 64 * 1024;
    internal const int MaximumNoteIntentCount = 16;
    internal const int MaximumNoteTotalExactTextUtf8Bytes = 256 * 1024;
    internal const string DefaultNotePodId = "00000000000000000000000000000001";
}
