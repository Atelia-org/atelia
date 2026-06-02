using Atelia.TextAdv2.Gym;

namespace Atelia.TextAdv2.DefaultAgent;

public sealed class KeepDefaultAgentPolicy : IDefaultAgentTurnPolicy {
    public ValueTask<AgentTurnDecision> DecideAsync(DefaultAgentTurnContext context, CancellationToken ct = default) {
        ArgumentNullException.ThrowIfNull(context);

        return ValueTask.FromResult(AgentTurnDecision.Keep(reasoning: "default-keep"));
    }
}
