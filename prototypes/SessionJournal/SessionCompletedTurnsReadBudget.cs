namespace Atelia.SessionJournal;

internal sealed class SessionCompletedTurnsReadBudget {
    internal const int DefaultMaximumHeaderVisits = 4_096;
    internal const long DefaultMaximumDecodedLogicalPayloadBytes =
        16L * 1024 * 1024;

    private int _headerVisits;
    private long _decodedLogicalPayloadBytes;

    internal SessionCompletedTurnsReadBudget(
        int maximumHeaderVisits = DefaultMaximumHeaderVisits,
        long maximumDecodedLogicalPayloadBytes =
            DefaultMaximumDecodedLogicalPayloadBytes
    ) {
        if (maximumHeaderVisits <= 0) {
            throw new ArgumentOutOfRangeException(
                nameof(maximumHeaderVisits)
            );
        }
        if (maximumDecodedLogicalPayloadBytes <= 0) {
            throw new ArgumentOutOfRangeException(
                nameof(maximumDecodedLogicalPayloadBytes)
            );
        }
        MaximumHeaderVisits = maximumHeaderVisits;
        MaximumDecodedLogicalPayloadBytes =
            maximumDecodedLogicalPayloadBytes;
    }

    internal int MaximumHeaderVisits { get; }
    internal long MaximumDecodedLogicalPayloadBytes { get; }
    internal int HeaderVisits => _headerVisits;
    internal long DecodedLogicalPayloadBytes =>
        _decodedLogicalPayloadBytes;

    internal void ConsumeHeaderVisit() {
        if (_headerVisits >= MaximumHeaderVisits) {
            throw new SessionCompletedTurnsLimitException(
                SessionCompletedTurnsLimit.MaximumExaminedHeaders
            );
        }
        _headerVisits++;
    }

    internal void ReservePayload(long declaredLength) {
        if (declaredLength < 0) {
            throw new InvalidDataException(
                "SessionJournal event declared a negative logical payload length."
            );
        }
        if (declaredLength
            > MaximumDecodedLogicalPayloadBytes
                - _decodedLogicalPayloadBytes) {
            throw new SessionCompletedTurnsLimitException(
                SessionCompletedTurnsLimit
                    .MaximumDecodedLogicalPayloadBytes
            );
        }
        _decodedLogicalPayloadBytes += declaredLength;
    }
}

internal sealed class SessionCompletedTurnsLimitException(
    SessionCompletedTurnsLimit limit
) : Exception($"Completed-turn projection exceeded '{limit}'.") {
    internal SessionCompletedTurnsLimit Limit { get; } = limit;
}

internal sealed class SessionCompletedTurnsUnsupportedSchemaException(
    string message,
    Exception? innerException = null
) : Exception(message, innerException);
