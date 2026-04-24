using Atelia.Completion.Abstractions;

namespace Atelia.Agent.Core.History;

/// <summary>
/// 投影层产出的 assistant message。其 <see cref="Blocks"/> 已按当前投影规则过滤，
/// 是面向 provider converter 的富视图，而非 RecentHistory 中的原始条目对象。
/// </summary>
public sealed record ProjectedActionMessage(
    IReadOnlyList<ActionBlock> Blocks
) : IRichActionMessage {
    public string Content => string.Concat(
        Blocks.OfType<ActionBlock.Text>().Select(static block => block.Content)
    );

    public IReadOnlyList<ParsedToolCall> ToolCalls => Blocks
        .OfType<ActionBlock.ToolCall>()
        .Select(static block => block.Call)
        .ToArray();

    public HistoryMessageKind Kind => HistoryMessageKind.Action;
}

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
