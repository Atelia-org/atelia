using Atelia.TextAdv2.Gym;

namespace Atelia.TextAdv2.DefaultAgent;

public sealed record DefaultAgentTurnContext {
    public DefaultAgentTurnContext(
        AgentTurnInput turnInput,
        DefaultAgentGoalState? goal,
        DefaultAgentMemorySlice memory,
        DefaultAgentTurnBudget budget
    ) {
        ArgumentNullException.ThrowIfNull(turnInput);
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(budget);

        TurnInput = turnInput;
        Goal = goal;
        Memory = memory;
        Budget = budget;
    }

    public AgentTurnInput TurnInput { get; }

    public DefaultAgentGoalState? Goal { get; }

    public DefaultAgentMemorySlice Memory { get; }

    public DefaultAgentTurnBudget Budget { get; }
}
