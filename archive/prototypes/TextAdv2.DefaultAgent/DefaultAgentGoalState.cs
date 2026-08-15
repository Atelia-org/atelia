namespace Atelia.TextAdv2.DefaultAgent;

public sealed record DefaultAgentGoalState {
    public DefaultAgentGoalState(string goalId, string summary) {
        ArgumentException.ThrowIfNullOrWhiteSpace(goalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(summary);

        GoalId = goalId;
        Summary = summary;
    }

    public string GoalId { get; }

    public string Summary { get; }
}
