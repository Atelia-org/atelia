namespace Atelia.MutableContextAgentProto.Core;

public sealed record EventLogEntry(
    long SequenceNumber,
    DateTimeOffset TimestampUtc,
    EventLogEntryKind Kind,
    string Summary,
    string? Content = null,
    string? CorrelationId = null,
    IReadOnlyDictionary<string, string>? Metadata = null
) {
    public static EventLogEntry Create(
        long sequenceNumber,
        EventLogEntryKind kind,
        string summary,
        string? content = null,
        string? correlationId = null,
        IReadOnlyDictionary<string, string>? metadata = null
    ) => new(
        sequenceNumber,
        DateTimeOffset.UtcNow,
        kind,
        summary,
        content,
        correlationId,
        metadata
    );
}
