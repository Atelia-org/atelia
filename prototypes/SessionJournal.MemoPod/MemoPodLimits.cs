namespace Atelia.SessionJournal.MemoPod;

public static class MemoPodLimits {
    public const int MaximumTopicUtf8Bytes = 4 * 1024;
    public const int MaximumMemoExactTextUtf8Bytes = 256 * 1024;
    public const int MaximumActiveMemoCount = 4_096;
    public const int MaximumActiveExactTextUtf8Bytes = 4 * 1024 * 1024;
    public const int MaximumDocumentUtf8Bytes = 32 * 1024 * 1024;
    public const int MaximumRenderedPromptUtf8Bytes = 32 * 1024 * 1024;
}
