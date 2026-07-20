namespace Atelia.ChatSession.Memory;

public static class MemoryDocumentTokenEstimator {
    public static int Estimate(string? text)
        => string.IsNullOrEmpty(text) ? 0 : Math.Max(1, text.Length / 3);
}
