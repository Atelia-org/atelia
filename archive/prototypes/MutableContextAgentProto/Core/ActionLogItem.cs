namespace Atelia.MutableContextAgentProto.Core;

public sealed record ActionLogItem(
    DateTimeOffset TimestampUtc,
    string Title,
    string? Detail = null,
    ActionStatus Status = ActionStatus.Completed
) {
    public static ActionLogItem Create(
        string title,
        string? detail = null,
        ActionStatus status = ActionStatus.Completed
    ) => new(DateTimeOffset.UtcNow, title, detail, status);
}
