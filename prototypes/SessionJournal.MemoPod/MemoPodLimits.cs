namespace Atelia.SessionJournal.MemoPod;

public static class MemoPodLimits {
    public const int MaximumTopicUtf8Bytes = 4 * 1024;
    public const int MaximumMemoExactTextUtf8Bytes = 256 * 1024;
    public const int MaximumMemoTitleUtf8Bytes = 512;
    public const int MaximumMemoGistUtf8Bytes = 2 * 1024;
    public const int MaximumMemoSummaryUtf8Bytes = 8 * 1024;
    public const int MaximumActiveMemoCount = 4_096;
    public const int MaximumActiveExactTextUtf8Bytes = 4 * 1024 * 1024;
    public const int MaximumActiveMemoMetadataUtf8Bytes = 1 * 1024 * 1024;
    public const int MaximumDocumentUtf8Bytes = 32 * 1024 * 1024;
    public const int MaximumRenderedPromptUtf8Bytes = 32 * 1024 * 1024;
    public const int MaximumRecallQueryUtf8Bytes = 64 * 1024;
    public const int MaximumRecallResultCount = 64;
    public const int MaximumRecallMaxTokens = 4_096;
}
