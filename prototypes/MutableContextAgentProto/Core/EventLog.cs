namespace Atelia.MutableContextAgentProto.Core;

public sealed class EventLog {
    private readonly List<EventLogEntry> _entries = [];

    public IReadOnlyList<EventLogEntry> Entries => _entries;
    public int Count => _entries.Count;

    public EventLogEntry Append(
        EventLogEntryKind kind,
        string summary,
        string? content = null,
        string? correlationId = null,
        IReadOnlyDictionary<string, string>? metadata = null
    ) {
        var entry = EventLogEntry.Create(
            _entries.Count + 1L,
            kind,
            summary,
            content,
            correlationId,
            metadata
        );
        _entries.Add(entry);
        return entry;
    }

    public EventLogEntry Append(EventLogEntry entry) {
        if (entry.SequenceNumber != _entries.Count + 1L) {
            entry = entry with { SequenceNumber = _entries.Count + 1L };
        }

        _entries.Add(entry);
        return entry;
    }
}
