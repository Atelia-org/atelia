using Atelia.Completion.Abstractions;

namespace Atelia.Agent.Core.History;

/// <summary>
/// 指定一条注入内容应以什么 block 形态进入最近的 Action continuation。
/// </summary>
public enum InjectedActionContentMode {
    /// <summary>
    /// 默认策略：参考最近一条 Action 的尾部非 tool-call 内容块。
    /// 若尾部是 thinking，则注入为 thinking；若尾部是正文或不存在可参考块，则注入为正文。
    /// </summary>
    MatchRecentActionTail,

    /// <summary>
    /// 强制注入为正文 text block。
    /// </summary>
    Text,

    /// <summary>
    /// 强制注入为 thinking / reasoning block。
    /// </summary>
    Thinking
}

/// <summary>
/// 向 RecentHistory 事件账本注入一段可续写的 actor-side 内容的请求。
/// </summary>
/// <remarks>
/// 该机制依赖 provider / dialect 对“trailing assistant prefix continuation”的接受程度。
/// 某些模型会自然把它当作 assistant 续写前缀，某些模型可能只把它当作普通历史消息。
/// 因此它是一个强表达力但带 provider 语义差异的原语。
/// 实际发给模型的 assistant/action message 由 <see cref="AgentState.ProjectInvocationContext"/>
/// 在投影时把连续 actor-side entries 动态拼接得到。
/// </remarks>
public sealed record ActionInjectionRequest(
    string Content,
    InjectionSource Source,
    InjectedActionContentMode Mode = InjectedActionContentMode.MatchRecentActionTail
);

/// <summary>
/// 一次 action injection 的结果。
/// </summary>
public sealed record ActionInjectionResult(
    ulong InjectedEntrySerial,
    ActionBlockKind InjectedBlockKind
);
