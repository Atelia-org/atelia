namespace Atelia.MutableContextAgentProto.Core;

public sealed record TransientView(
    string Id,
    string Title,
    string Content,
    DateTimeOffset CreatedAtUtc,
    string? Source = null
) {
    public static TransientView Create(
        string id,
        string title,
        string content,
        string? source = null
    ) => new(id, title, content, DateTimeOffset.UtcNow, source);
}
