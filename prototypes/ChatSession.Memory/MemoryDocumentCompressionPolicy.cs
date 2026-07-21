namespace Atelia.ChatSession.Memory;

public sealed record MemoryDocumentCompressionPolicy {
    public MemoryDocumentCompressionPolicy(
        int highWatermarkTokens,
        int targetTokens
    ) {
        if (targetTokens <= 0) { throw new ArgumentOutOfRangeException(nameof(targetTokens), "Target tokens must be positive."); }
        if (highWatermarkTokens <= targetTokens) {
            throw new ArgumentOutOfRangeException(
                nameof(highWatermarkTokens),
                "High watermark tokens must be greater than target tokens."
            );
        }

        HighWatermarkTokens = highWatermarkTokens;
        TargetTokens = targetTokens;
    }

    public int HighWatermarkTokens { get; }
    public int TargetTokens { get; }

    public bool ShouldCompress(string documentText)
        => MemoryDocumentTokenEstimator.Estimate(documentText) >= HighWatermarkTokens;
}
