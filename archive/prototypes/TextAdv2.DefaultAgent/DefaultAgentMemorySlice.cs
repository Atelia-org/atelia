namespace Atelia.TextAdv2.DefaultAgent;

public sealed record DefaultAgentMemoryEntry {
    public DefaultAgentMemoryEntry(string key, string summary, string? source = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(summary);

        Key = key;
        Summary = summary;
        Source = source;
    }

    public string Key { get; }

    public string Summary { get; }

    public string? Source { get; }
}

public sealed record DefaultAgentMemorySlice {
    public static DefaultAgentMemorySlice Empty { get; } = new(summary: null, entries: []);

    public DefaultAgentMemorySlice(string? summary, DefaultAgentMemoryEntry[] entries) {
        ArgumentNullException.ThrowIfNull(entries);

        Summary = summary;
        Entries = entries;
    }

    public string? Summary { get; }

    public DefaultAgentMemoryEntry[] Entries { get; }
}
