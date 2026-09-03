namespace Atelia.MemoPod;

/// <summary>
/// Bounds local MemoPod selection and materialization work.
/// </summary>
/// <remarks>
/// This contract intentionally exposes no caller-selected output-token cap.
/// Completion adapters either omit a provider limit when omission means
/// unlimited or the model maximum, or send the selected model's exact
/// provider maximum when the wire requires a number.
/// </remarks>
public sealed class MemoRecallOptions {
    public MemoRecallOptions(
        int maxResults,
        int maximumFrozenPromptUtf8Bytes,
        int maximumHydratedExactTextUtf8Bytes
    ) {
        MaxResults = RequireInRange(
            maxResults,
            1,
            MemoPodLimits.MaximumRecallResultCount,
            nameof(maxResults)
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
