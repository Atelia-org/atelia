namespace Atelia.TextAdv;

internal sealed record ActionDescriptor(
    string ActionKind,
    string ActionSummary,
    string? ActionPayload,
    string PreActionReason
);
