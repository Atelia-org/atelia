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
        string userMessage,
        CancellationToken cancellationToken
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(userMessage);
        cancellationToken.ThrowIfCancellationRequested();

        string original = userMessage;
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
            LogFallback(exception);
            return original;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!shouldNormalize) {
            return original;
        }

        try {
            string effective = await _normalizer
                .NormalizeAsync(original, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(effective)) {
                effective = original;
            }
            RequireMessageFits(effective, "normalized");
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
            LogFallback(exception);
            return original;
        }
    }

    private static void LogFallback(
        Exception exception
    ) => DebugUtil.Warning(
        DebugCategory,
        "Admission input preprocessing fallback to original: "
        + $"exceptionType={exception.GetType().FullName}"
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

}
