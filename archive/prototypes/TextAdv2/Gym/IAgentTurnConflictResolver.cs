namespace Atelia.TextAdv2.Gym;

public interface IAgentTurnConflictResolver {
    AgentTurnResolutionPlan Resolve(AgentTurnResolutionRequest request);
}
