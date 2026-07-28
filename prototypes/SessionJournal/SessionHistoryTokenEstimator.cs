using Atelia.Completion.Abstractions;

namespace Atelia.SessionJournal;

/// <summary>
/// Shared deterministic estimator used by both derived epoch planning and online raw-suffix
/// selection. It is approximate, but its version and rendering rules are a single contract.
/// </summary>
public static class SessionHistoryTokenEstimator {
    public const string EstimatorId =
        "atelia.session-journal.flattened-text-token-estimator.v1";

    public static long Estimate(IHistoryMessage message) {
        ArgumentNullException.ThrowIfNull(message);
        string text = message switch {
            SessionContextHeader header => string.Join(
                '\n',
                new[] {
                    header.SystemPromptFragment,
                    header.ObservationMessage,
                    header.ActionMessage?.GetFlattenedText()
                }.Where(static value => !string.IsNullOrEmpty(value))
            ),
            ToolResultsMessage results => results.Content ?? string.Empty,
            ObservationMessage observation => observation.Content ?? string.Empty,
            ActionMessage action => action.GetFlattenedText(),
            _ => message.ToString() ?? string.Empty
        };
        return Math.Max(1, text.Length / 3);
    }

    /// <summary>
    /// Measures the exact canonical request representation used for Prepared commitment. Candidate
    /// total-budget checks must construct the final request shape and use this method.
    /// </summary>
    public static long EstimateCanonicalRequest(CompletionRequest request) {
        ArgumentNullException.ThrowIfNull(request);
        return Math.Max(
            1,
            SessionRequestCanonicalizer.Canonicalize(request).Length / 3
        );
    }
}
