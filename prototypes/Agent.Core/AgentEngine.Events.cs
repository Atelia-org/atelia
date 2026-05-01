using Atelia.Agent.Core.History;
using Atelia.Completion.Abstractions;

namespace Atelia.Agent.Core;

public delegate Task PrepareInvocationAsyncHandler(PrepareInvocationEventArgs args, CancellationToken cancellationToken);

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
    /// 获取或设置要追加的用户输入条目（可选）。
    /// </summary>
    public ObservationEntry? InputEntry { get; set; }

    /// <summary>
    /// 获取或设置额外的主机通知内容（可选）。
    /// </summary>
    public LevelOfDetailContent? AdditionalNotification { get; set; }
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
