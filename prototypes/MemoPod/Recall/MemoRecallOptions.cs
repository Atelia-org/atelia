namespace Atelia.MemoPod;

public sealed class MemoRecallOptions {
    public MemoRecallOptions(
        int maxResults,
        int maxTokens,
        int maximumFrozenPromptUtf8Bytes,
        int maximumHydratedExactTextUtf8Bytes
    ) {
        MaxResults = RequireInRange(
            maxResults,
            1,
            MemoPodLimits.MaximumRecallResultCount,
            nameof(maxResults)
        );
        MaxTokens = RequireInRange(
            maxTokens,
            1,
            MemoPodLimits.MaximumRecallMaxTokens,
            nameof(maxTokens)
        );
        MaximumFrozenPromptUtf8Bytes = RequireInRange(
            maximumFrozenPromptUtf8Bytes,
            1,
            MemoPodLimits.MaximumRenderedPromptUtf8Bytes,
            nameof(maximumFrozenPromptUtf8Bytes)
        );
        MaximumHydratedExactTextUtf8Bytes = RequireInRange(
            maximumHydratedExactTextUtf8Bytes,
            1,
            MemoPodLimits.MaximumActiveExactTextUtf8Bytes,
            nameof(maximumHydratedExactTextUtf8Bytes)
        );
    }

    public int MaxResults { get; }
    public int MaxTokens { get; }
    public int MaximumFrozenPromptUtf8Bytes { get; }
    public int MaximumHydratedExactTextUtf8Bytes { get; }

    private static int RequireInRange(
        int value,
        int minimum,
        int maximum,
        string parameterName
    ) {
        if (value < minimum || value > maximum) {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                $"Value must be between {minimum} and {maximum}."
            );
        }
        return value;
    }
}
