using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Atelia.LlmProviders;

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

    public static LodToolExecuteResult FromContent(
        ToolExecutionStatus status,
        LevelOfDetailContent content
    ) {
        return new LodToolExecuteResult(status, content);
    }
}

public sealed record LodToolCallResult {
    public LodToolCallResult(
        ToolExecutionStatus status,
        LevelOfDetailContent result,
        string? toolName = null,
        string? toolCallId = null,
        TimeSpan? elapsed = null
    ) {
        Status = status;
        Result = result ?? throw new ArgumentNullException(nameof(result));
        ToolName = toolName;
        ToolCallId = toolCallId;
        Elapsed = elapsed;
    }

    public ToolExecutionStatus Status { get; init; }

    public LevelOfDetailContent Result { get; init; }

    public string? ToolName { get; init; }

    public string? ToolCallId { get; init; }

    public TimeSpan? Elapsed { get; init; }

    public LodToolCallResult WithContext(
        string toolName,
        string toolCallId,
        TimeSpan? elapsed = null
    ) {
        if (string.IsNullOrWhiteSpace(toolName)) { throw new ArgumentException("Tool name cannot be empty.", nameof(toolName)); }
        if (string.IsNullOrWhiteSpace(toolCallId)) { throw new ArgumentException("Tool call id cannot be empty.", nameof(toolCallId)); }

        var newResult = Result;
        if (elapsed.HasValue) {
            newResult = new LevelOfDetailContent(Result.Basic, $"ElapsedMs={elapsed.Value.TotalMilliseconds:F0}\n{Result.Detail}");
        }

        return this with {
            Result = newResult,
            ToolName = toolName,
            ToolCallId = toolCallId,
            Elapsed = elapsed ?? Elapsed
        };
    }

    public static LodToolCallResult FromContent(
        ToolExecutionStatus status,
        LevelOfDetailContent content
    ) {
        return new LodToolCallResult(status, content);
    }
}
