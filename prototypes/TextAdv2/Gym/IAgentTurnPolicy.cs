namespace Atelia.TextAdv2.Gym;

public interface IAgentTurnPolicy {
    ValueTask<AgentTurnDecision> DecideAsync(AgentTurnInput input, CancellationToken ct = default);
}
