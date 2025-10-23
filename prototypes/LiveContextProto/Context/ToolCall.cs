namespace Atelia.LiveContextProto.Context;

internal record ToolCallRequest(
    string ToolName,
    string ToolCallId,
    string RawArguments,
    IReadOnlyDictionary<string, object?>? Arguments,
    string? ParseError,
    string? ParseWarning
);

internal enum ToolExecutionStatus {
    Success,
    Failed,
    Skipped
}

internal record ToolCallResult(
    string ToolName,
    string ToolCallId,
    ToolExecutionStatus Status,
    string Result,
    TimeSpan? Elapsed
);
