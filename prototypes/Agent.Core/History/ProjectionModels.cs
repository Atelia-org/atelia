using Atelia.Completion.Abstractions;

namespace Atelia.Agent.Core.History;

public sealed record ContextProjectionOptions(
    CompletionDescriptor? TargetInvocation = null,
    string? Windows = null,
    ThinkingProjectionMode ThinkingMode = ThinkingProjectionMode.CurrentTurnOnly
);

public enum ThinkingProjectionMode {
    None,
    CurrentTurnOnly
}

public sealed record ProjectedInvocationContext(
    IReadOnlyList<IHistoryMessage> StablePrefix,
    IReadOnlyList<IHistoryMessage> ActiveTurnTail
) {
    public IReadOnlyList<IHistoryMessage> ToFlat() => [.. StablePrefix, .. ActiveTurnTail];
}
