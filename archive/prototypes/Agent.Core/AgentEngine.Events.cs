using Atelia.Agent.Core.History;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;

namespace Atelia.Agent.Core;

public delegate Task PrepareInvocationAsyncHandler(PrepareInvocationEventArgs args, CancellationToken cancellationToken);

/// <summary>
/// 表示宿主在一次 <see cref="AgentRunState.WaitingInput"/> 决议中提交给引擎的 observation 草案。
/// </summary>
/// <remarks>
/// 这是当前 decision boundary 的单一输入对象：宿主可以直接提供一条现成的 <see cref="ObservationEntry"/>，
/// 也可以只补充一段 recent events 文本，由引擎组装为新的 observation。
/// </remarks>
public sealed class IncomingObservation {
    public IncomingObservation(ObservationEntry? entry = null, string? recentEvents = null) {
        Entry = entry;
        RecentEvents = recentEvents;
    }

    /// <summary>
    /// 获取当前轮要提交的显式 observation 条目（可选）。
    /// </summary>
    public ObservationEntry? Entry { get; init; }

    /// <summary>
    /// 获取当前轮新增的 recent events 文本（可选）。
    /// 引擎会把它与队列中的 pending notifications 一起并入最终 observation。
    /// </summary>
    public string? RecentEvents { get; init; }

    public static IncomingObservation FromEntry(ObservationEntry entry) {
        if (entry is null) { throw new ArgumentNullException(nameof(entry)); }
        return new IncomingObservation(entry: entry);
    }

    public static IncomingObservation FromRecentEvents(string recentEvents) {
        if (recentEvents is null) { throw new ArgumentNullException(nameof(recentEvents)); }
        return new IncomingObservation(recentEvents: recentEvents);
    }
}

/// <summary>
/// <see cref="AgentEngine.ResolveProfile"/> 事件的参数。
/// </summary>
public sealed class ResolveProfileEventArgs : EventArgs {
    internal ResolveProfileEventArgs(AgentRunState state, LlmProfile profile) {
        State = state;
        Profile = profile;
    }

    /// <summary>
    /// 获取当前 Agent 运行状态。
    /// </summary>
    public AgentRunState State { get; }

    /// <summary>
    /// 获取或设置本次模型调用的最终 LLM 配置文件。
    /// 引擎会在本事件结束后对该 profile 执行 Turn 锁定校验，并据此构造上下文与请求。
    /// </summary>
    public LlmProfile Profile { get; set; }

    /// <summary>
    /// 获取或设置一个值，指示是否取消本次模型调用。
    /// </summary>
    public bool Cancel { get; set; }
}

/// <summary>
/// <see cref="AgentEngine.PrepareInvocationAsync"/> 钩子的参数。
/// </summary>
public sealed class PrepareInvocationEventArgs : EventArgs {
    internal PrepareInvocationEventArgs(AgentRunState state, LlmProfile profile, ulong estimatedContextTokens) {
        State = state;
        Profile = profile;
        EstimatedContextTokens = estimatedContextTokens;
    }

    /// <summary>
    /// 获取当前 Agent 运行状态。
    /// </summary>
    public AgentRunState State { get; }

    /// <summary>
    /// 获取本次模型调用已决议完成的最终 LLM 配置文件。
    /// 本阶段不得再切换 profile；如需决议模型，请使用 <see cref="AgentEngine.ResolveProfile"/>。
    /// </summary>
    public LlmProfile Profile { get; }

    /// <summary>
    /// 获取当前历史的 token 估算值。
    /// 此值按历史与系统提示词估算，不包含后续 window / tool definitions 注入的额外投影成本。
    /// </summary>
    public ulong EstimatedContextTokens { get; }

    /// <summary>
    /// 获取或设置本次真实模型调用的附加工具可见性限制。
    /// 若设置该值，引擎会将其与 AppHost 投影出的默认可见性求交集；
    /// 因此本属性只能进一步收紧工具暴露范围，不能放宽默认限制。
    /// <c>null</c> 表示沿用 AppHost 投影出的默认可见性。
    /// </summary>
    public ToolAccessSnapshot? ToolAccessOverride { get; set; }

    /// <summary>
    /// 获取或设置一个值，指示是否取消本次模型调用。
    /// </summary>
    public bool Cancel { get; set; }
}

/// <summary>
/// <see cref="AgentEngine.WaitingInput"/> 事件的参数。
/// </summary>
public sealed class WaitingInputEventArgs : EventArgs {
    internal WaitingInputEventArgs(bool hasPendingNotification, HistoryEntry? lastEntry) {
        HasPendingNotification = hasPendingNotification;
        LastEntry = lastEntry;
        ShouldContinue = hasPendingNotification;
    }

    /// <summary>
    /// 获取一个值，指示是否有待处理的主机通知。
    /// </summary>
    public bool HasPendingNotification { get; }

    /// <summary>
    /// 获取历史中的最后一个条目（可能为 <c>null</c>）。
    /// </summary>
    public HistoryEntry? LastEntry { get; }

    /// <summary>
    /// 获取或设置一个值，指示是否应继续执行（默认为 <see cref="HasPendingNotification"/>）。
    /// </summary>
    public bool ShouldContinue { get; set; }

    /// <summary>
    /// 获取或设置当前轮要提交的 observation 草案（可选）。
    /// </summary>
    public IncomingObservation? Observation { get; set; }
}

/// <summary>
/// <see cref="AgentEngine.StateTransition"/> 事件的参数。
/// </summary>
public sealed class StateTransitionEventArgs : EventArgs {
    internal StateTransitionEventArgs(AgentRunState fromState, AgentRunState toState) {
        FromState = fromState;
        ToState = toState;
    }

    /// <summary>
    /// 获取转换前的状态。
    /// </summary>
    public AgentRunState FromState { get; }

    /// <summary>
    /// 获取转换后的状态。
    /// </summary>
    public AgentRunState ToState { get; }
}

/// <summary>
/// <see cref="AgentEngine.ActionProduced"/> 事件的参数。
/// </summary>
public sealed class ActionProducedEventArgs : EventArgs {
    internal ActionProducedEventArgs(ActionEntry action, LlmProfile profile) {
        Action = action ?? throw new ArgumentNullException(nameof(action));
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
    }

    public ActionEntry Action { get; }

    public LlmProfile Profile { get; }

    public bool HasToolCalls => Action.Message.ToolCalls is { Count: > 0 };
}

/// <summary>
/// <see cref="AgentEngine.ToolExecutionCompleted"/> 事件的参数。
/// </summary>
public sealed class ToolExecutionCompletedEventArgs : EventArgs {
    internal ToolExecutionCompletedEventArgs(RawToolCall toolCall, ToolCallExecutionResult result, LlmProfile profile) {
        ToolCall = toolCall ?? throw new ArgumentNullException(nameof(toolCall));
        Result = result ?? throw new ArgumentNullException(nameof(result));
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
    }

    public RawToolCall ToolCall { get; }

    public ToolCallExecutionResult Result { get; }

    public LlmProfile Profile { get; }

    public ToolExecutionStatus Status => Result.ExecuteResult.Status;
}
