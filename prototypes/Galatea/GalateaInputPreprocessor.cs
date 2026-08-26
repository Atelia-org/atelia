using Atelia.Diagnostics;

namespace Atelia.Galatea.Server;

/// <summary>
/// Applies Galatea's optional application-level input normalization before
/// SessionJournal receives the durable observation text.
/// </summary>
internal sealed class GalateaInputPreprocessor {
    private const string DebugCategory = "Galatea.Session";

    private readonly IGalateaUserMessageNormalizer _normalizer;

    internal GalateaInputPreprocessor(
        IGalateaUserMessageNormalizer normalizer
    ) {
        _normalizer = normalizer
            ?? throw new ArgumentNullException(nameof(normalizer));
    }

    internal async ValueTask<string> ProcessAsync(
        GalateaLiveTurn liveTurn,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(liveTurn);
        cancellationToken.ThrowIfCancellationRequested();

        string original = liveTurn.UserMessage
            ?? throw new InvalidOperationException(
                "Input preprocessing requires a fresh user message."
            );
        RequireMessageFits(original, "original");
        bool shouldNormalize;
        try {
            shouldNormalize = _normalizer.ShouldNormalize(original);
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested
        ) {
            throw;
        }
        catch (Exception exception) when (
            GalateaExceptionClassifier.IsNonFatal(exception)) {
            LogFallback(liveTurn, original, exception);
            return original;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!shouldNormalize) {
            return original;
        }

        liveTurn.PublishStatus(
            GalateaSseStatusCode.NormalizingInput
        );

        try {
            string effective = await _normalizer
                .NormalizeAsync(original, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(effective)) {
                effective = original;
            }
            RequireMessageFits(effective, "normalized");
            bool changed = !string.Equals(
                original,
                effective,
                StringComparison.Ordinal
            );
            liveTurn.PublishStatus(
                GalateaSseStatusCode.InputNormalizationFinished,
                changed
            );
            return effective;
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested
        ) {
            throw;
        }
        catch (GalateaTurnException) {
            throw;
        }
        catch (Exception exception) when (
            GalateaExceptionClassifier.IsNonFatal(exception)) {
            LogFallback(liveTurn, original, exception);
            liveTurn.PublishStatus(
                GalateaSseStatusCode.InputNormalizationFinished,
                changed: false
            );
            return original;
        }
    }

    private static void LogFallback(
        GalateaLiveTurn liveTurn,
        string original,
        Exception exception
    ) => DebugUtil.Warning(
        DebugCategory,
        "Input preprocessing fallback to original: "
        + $"turnId={liveTurn.TurnId}, input={Preview(original)}, "
        + $"error={exception.Message}"
    );

    private static void RequireMessageFits(string message, string source) {
        string? error = GalateaHttpV1.ValidateMessage(message);
        if (error is not null) {
            throw new GalateaTurnException(
                $"The {source} user message is invalid: {error}",
                "input-limit-exceeded"
            );
        }
    }

    private static string Preview(string? text) {
        if (string.IsNullOrWhiteSpace(text)) {
            return "<null>";
        }
        string normalized = text
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
        return normalized.Length <= 120
            ? normalized
            : normalized[..120] + "...";
    }
}
