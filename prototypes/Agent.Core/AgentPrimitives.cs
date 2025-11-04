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
    PromptEntry? Input,
    ModelEntry? Output,
    ToolEntry? ToolResults
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
    ToolEntry? Entry,
    string? FailureMessage,
    string? ToolName
) {
    public static AgentToolExecutionResult ToolNotRegistered(string toolName)
        => new(AgentToolExecutionResultStatus.ToolNotRegistered, null, null, toolName);

    public static AgentToolExecutionResult NoResults()
        => new(AgentToolExecutionResultStatus.NoResults, null, null, null);

    public static AgentToolExecutionResult Success(ToolEntry entry)
        => new(AgentToolExecutionResultStatus.Success, entry, null, null);

    public static AgentToolExecutionResult SuccessWithFailure(ToolEntry entry, string? failureMessage)
        => new(AgentToolExecutionResultStatus.Success, entry, failureMessage, null);
}
