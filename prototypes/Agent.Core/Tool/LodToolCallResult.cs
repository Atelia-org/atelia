using Atelia.Completion.Abstractions;

namespace Atelia.Agent.Core.Tool;

public sealed record LodToolExecuteResult {
    public LodToolExecuteResult(
        ToolExecutionStatus status,
        LevelOfDetailContent result
    ) {
        Status = status;
        Result = result ?? throw new ArgumentNullException(nameof(result));
    }

    public ToolExecutionStatus Status { get; }

    public LevelOfDetailContent Result { get; }
}

public sealed record LodToolCallResult {
    public LodToolCallResult(
        LodToolExecuteResult executeResult,
        string toolName,
        string toolCallId,
        TimeSpan elapsed = default
    ) {
        ExecuteResult = executeResult ?? throw new ArgumentNullException(nameof(executeResult));
        ToolName = toolName ?? throw new ArgumentNullException(nameof(toolName));
        ToolCallId = toolCallId ?? throw new ArgumentNullException(nameof(toolCallId));
        Elapsed = elapsed;
    }

    public LodToolExecuteResult ExecuteResult { get; init; }

    public string ToolName { get; init; }

    public string ToolCallId { get; init; }

    public TimeSpan? Elapsed { get; init; }
}
