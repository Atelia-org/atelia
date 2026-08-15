using Atelia.TextAdv2.Gym;

namespace Atelia.TextAdv2.DefaultAgent;

/// <summary>
/// 把 Gym 的最小 `AgentTurnInput` contract 适配到 DefaultAgent 的 richer context seam。
///
/// 这样可以同时保持：
/// - Gym 边界尽量薄；
/// - DefaultAgent 可以独立演化 goal / memory / budget / dynamic context。
/// </summary>
public sealed class DefaultAgentTurnPolicyAdapter : IAgentTurnPolicy {
    private readonly IDefaultAgentTurnPolicy _innerPolicy;
    private readonly IDefaultAgentContextBuilder _contextBuilder;

    public DefaultAgentTurnPolicyAdapter(
        IDefaultAgentTurnPolicy innerPolicy,
        IDefaultAgentContextBuilder? contextBuilder = null
    ) {
        ArgumentNullException.ThrowIfNull(innerPolicy);

        _innerPolicy = innerPolicy;
        _contextBuilder = contextBuilder ?? new PassthroughDefaultAgentContextBuilder();
    }

    public async ValueTask<AgentTurnDecision> DecideAsync(AgentTurnInput input, CancellationToken ct = default) {
        ArgumentNullException.ThrowIfNull(input);

        var context = await _contextBuilder.BuildAsync(input, ct);
        return await _innerPolicy.DecideAsync(context, ct);
    }
}
