namespace Atelia.TextAdv2.DefaultAgent;

public sealed record DefaultAgentTurnBudget {
    public static DefaultAgentTurnBudget Default { get; } = new(
        maxAdditionalObservations: 0,
        maxPlannedActions: 1,
        maxThinkingSteps: 1
    );

    public DefaultAgentTurnBudget(
        int maxAdditionalObservations,
        int maxPlannedActions,
        int maxThinkingSteps
    ) {
        ArgumentOutOfRangeException.ThrowIfNegative(maxAdditionalObservations);
        ArgumentOutOfRangeException.ThrowIfNegative(maxPlannedActions);
        ArgumentOutOfRangeException.ThrowIfNegative(maxThinkingSteps);

        MaxAdditionalObservations = maxAdditionalObservations;
        MaxPlannedActions = maxPlannedActions;
        MaxThinkingSteps = maxThinkingSteps;
    }

    public int MaxAdditionalObservations { get; }

    public int MaxPlannedActions { get; }

    public int MaxThinkingSteps { get; }
}
