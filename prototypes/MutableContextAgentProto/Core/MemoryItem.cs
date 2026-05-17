namespace Atelia.MutableContextAgentProto.Core;

public sealed record MemoryItem(
    DateTimeOffset TimestampUtc,
    MemoryKind Kind,
    string Content,
    string? Source = null,
    string? Key = null
) {
    public static MemoryItem Create(
        string content,
        MemoryKind kind = MemoryKind.Fact,
        string? source = null,
        string? key = null
    ) => new(DateTimeOffset.UtcNow, kind, content, source, key);
}
