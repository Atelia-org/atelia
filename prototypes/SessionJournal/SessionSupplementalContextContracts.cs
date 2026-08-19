using System.Text;
using Atelia.EventJournal;

namespace Atelia.SessionJournal;

/// <summary>
/// Selects one provider-neutral supplemental observation for an exact durable
/// observation boundary. Implementations own external lookup; SessionJournal
/// owns request validation, durable capture, and recovery.
/// </summary>
public interface ISessionSupplementalContextSource {
    ValueTask<SessionSupplementalContextSelection> SelectAsync(
        SessionSupplementalContextRequest request,
        CancellationToken cancellationToken
    );
}

/// <summary>
/// Exact durable observation identity and content supplied to a supplemental
/// source. The content is never trimmed or normalized.
/// </summary>
public sealed record SessionSupplementalContextRequest {
    public SessionSupplementalContextRequest(
        EventAddress observationAddress,
        string exactObservationContent
    ) {
        if (observationAddress == default) {
            throw new ArgumentException(
                "Observation address cannot be the default EventAddress.",
                nameof(observationAddress)
            );
        }
        ArgumentNullException.ThrowIfNull(exactObservationContent);
        if (string.IsNullOrWhiteSpace(exactObservationContent)) {
            throw new ArgumentException(
                "Exact observation content cannot be empty or whitespace.",
                nameof(exactObservationContent)
            );
        }
        SessionSupplementalContextText.ValidateUnicodeScalars(
            exactObservationContent,
            nameof(exactObservationContent)
        );
        ObservationAddress = observationAddress;
        ExactObservationContent = exactObservationContent;
    }

    public EventAddress ObservationAddress { get; }

    public string ExactObservationContent { get; }
}

/// <summary>
/// Closed result of one supplemental lookup. Failures and cancellation are
/// represented by exceptions, never by a third result state.
/// </summary>
public abstract record SessionSupplementalContextSelection {
    private SessionSupplementalContextSelection() { }

    public sealed record NoMatch : SessionSupplementalContextSelection;

    public sealed record Selected : SessionSupplementalContextSelection {
        public Selected(string exactObservationContent) {
            ArgumentNullException.ThrowIfNull(exactObservationContent);
            if (exactObservationContent.Length == 0) {
                throw new ArgumentException(
                    "Selected supplemental observation content cannot be empty.",
                    nameof(exactObservationContent)
                );
            }
            SessionSupplementalContextText.ValidateUnicodeScalars(
                exactObservationContent,
                nameof(exactObservationContent)
            );
            ExactObservationContent = exactObservationContent;
        }

        public string ExactObservationContent { get; }
    }
}

internal static class SessionSupplementalContextText {
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true
    );

    public static void ValidateUnicodeScalars(string value, string parameterName) {
        try {
            _ = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception) {
            throw new ArgumentException(
                "Text contains invalid Unicode scalar data.",
                parameterName,
                exception
            );
        }
    }
}
