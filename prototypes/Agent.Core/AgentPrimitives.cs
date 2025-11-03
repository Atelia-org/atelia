using Atelia.Agent.Core.History;

namespace Atelia.Agent.Core;

public enum AgentRunState {
    WaitingInput,
    PendingInput,
    WaitingToolResults,
    ToolResultsReady,
    PendingToolResults
}

public readonly record struct AgentStepResult(
    bool ProgressMade,
    AgentRunState StateBefore,
    AgentRunState StateAfter,
    ModelInputEntry? Input,
    ModelOutputEntry? Output,
    ToolResultsEntry? ToolResults
) {
    public bool BlockedOnInput => !ProgressMade && StateAfter == AgentRunState.WaitingInput;
}

internal enum AgentToolExecutionResultStatus {
    Success,
    ToolNotRegistered,
    NoResults
}

internal sealed record AgentToolExecutionResult(
    AgentToolExecutionResultStatus Status,
    ToolResultsEntry? Entry,
    string? FailureMessage,
    string? ToolName
) {
    public static AgentToolExecutionResult ToolNotRegistered(string toolName)
        => new(AgentToolExecutionResultStatus.ToolNotRegistered, null, null, toolName);

    public static AgentToolExecutionResult NoResults()
        => new(AgentToolExecutionResultStatus.NoResults, null, null, null);

    public static AgentToolExecutionResult Success(ToolResultsEntry entry)
        => new(AgentToolExecutionResultStatus.Success, entry, null, null);

    public static AgentToolExecutionResult SuccessWithFailure(ToolResultsEntry entry, string? failureMessage)
        => new(AgentToolExecutionResultStatus.Success, entry, failureMessage, null);
}
