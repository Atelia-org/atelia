using Atelia.Agent.Core.History;

namespace Atelia.Agent.Core;

/// <summary>
/// 在 Agent 处于 <see cref="AgentRunState.WaitingInput"/> 且事件处理器决定继续推进、
/// 但既未提供 <see cref="WaitingInputEventArgs.InputEntry"/>、也未提供
/// <see cref="WaitingInputEventArgs.AdditionalNotification"/>、且 <see cref="AgentState"/>
/// 中也没有任何待处理通知时被调用的"内源性观测"扩展点。
/// </summary>
/// <remarks>
/// <para>
/// 直接向 LLM 投递一条"完全空"的 user 消息会被多数主流厂商拒绝（例如 Anthropic 的
/// <c>text content blocks must contain non-whitespace text</c>），同时对模型本身也不携带任何信息。
/// 所以宿主必须显式定义"无外部信号时该说什么"的策略，而不是依赖 provider 层兜底。
/// </para>
/// <para>
/// 实现可返回 <c>null</c> 表示"本次不希望推进任何 idle 心跳"，引擎会保持
/// <see cref="StepOutcome.NoProgress"/>，等待真正的外部输入。
/// </para>
/// <para>
/// 这是单实例独占场景下"DMN(默认模式网络)/idle scheduler"的最小接口形态。后续可扩展为
/// 多 provider 排队、按 budget 节流、或升档为反思/记忆巩固等更高层认知活动。
/// </para>
/// </remarks>
public interface IIdleObservationProvider {
    /// <summary>
    /// 在 idle 时刻产生一条"内源性"通知，将被附加到一条新的 <see cref="ObservationEntry"/> 上。
    /// 返回 <c>null</c> 表示"本次不应推进"。
    /// </summary>
    LevelOfDetailContent? CreateIdleNotification(in IdleObservationContext context);
}

/// <summary>
/// 传递给 <see cref="IIdleObservationProvider.CreateIdleNotification"/> 的上下文快照。
/// </summary>
public readonly struct IdleObservationContext {
    /// <summary>
    /// 当前 UTC 时间。来自调用方注入的时间源，便于测试与回放。
    /// </summary>
    public DateTimeOffset UtcNow { get; init; }

    /// <summary>
    /// 当前 Recent History 的只读视图；provider 可基于此判断"距离上一次行动有多久"等。
    /// </summary>
    public IReadOnlyList<HistoryEntry> RecentHistory { get; init; }
}

/// <summary>
/// 默认 idle provider：注入一条"心跳"通知，仅包含当前 UTC 时间。
/// </summary>
/// <remarks>
/// 这是最保守的占位实现，足以让历史合法、provider 不报错、对模型仍携带"时间在流逝"的最小信号。
/// 后续可被替换为反思、目标推进、记忆巩固等更高层策略。
/// </remarks>
public sealed class TimestampHeartbeatObservationProvider : IIdleObservationProvider {
    public LevelOfDetailContent? CreateIdleNotification(in IdleObservationContext context) {
        var text = $"[Heartbeat] No external input. UtcNow={context.UtcNow:O}";
        return new LevelOfDetailContent(text);
    }
}
