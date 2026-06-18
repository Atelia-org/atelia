namespace Atelia.Agent.Core;

/// <summary>
/// 描述一个 LLM surface 对 Agent.Core 高级认知能力的支持事实。
/// </summary>
public sealed record CapabilityProfile(
    bool ThinkingIsVisibleAsText,
    bool ThinkingReplayWithinTurnIsSupported,
    bool RuntimeAuthoredReasoningReplayIsSupported,
    bool RuntimeMayEditActorContinuation,
    bool AssistantPrefixContinuationIsStable,
    string? Notes = null
) {
    /// <summary>
    /// 不满足 Agent.Core 当前运行时的 full-feature 准入。
    /// 可用于描述其他宿主或 future design 中的更弱 surface。
    /// </summary>
    public static CapabilityProfile BasicExecutionOnly { get; } = new(
        ThinkingIsVisibleAsText: false,
        ThinkingReplayWithinTurnIsSupported: false,
        RuntimeAuthoredReasoningReplayIsSupported: false,
        RuntimeMayEditActorContinuation: false,
        AssistantPrefixContinuationIsStable: false,
        Notes: "Basic execution kernel only."
    );

    /// <summary>
    /// 支持 Agent.Core 的 full-feature 认知层。
    /// </summary>
    public static CapabilityProfile FullFeature { get; } = new(
        ThinkingIsVisibleAsText: true,
        ThinkingReplayWithinTurnIsSupported: true,
        RuntimeAuthoredReasoningReplayIsSupported: true,
        RuntimeMayEditActorContinuation: true,
        AssistantPrefixContinuationIsStable: true,
        Notes: "Supports Agent.Core full-feature cognition."
    );

    /// <summary>
    /// 是否支持 Agent.Core 的 full-feature 认知层。
    /// </summary>
    public bool SupportsAgentCoreFullFeatures =>
        ThinkingIsVisibleAsText
        && ThinkingReplayWithinTurnIsSupported
        && RuntimeAuthoredReasoningReplayIsSupported
        && RuntimeMayEditActorContinuation
        && AssistantPrefixContinuationIsStable;
}
