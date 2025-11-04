using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Atelia.Completion.Abstractions;
using Atelia.Agent.Core.Tool;

namespace Atelia.Agent.Core.History;

public enum HistoryEntryKind {
    Prompt,
    Model,
    Tool
}

public record ModelInvocationDescriptor(
    string ProviderId,
    string Specification,
    string Model
);

public abstract record class HistoryEntry {
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
    public abstract HistoryEntryKind Kind { get; }
}

public sealed record ModelEntry(
    string Contents,
    IReadOnlyList<ParsedToolCall> ToolCalls,
    ModelInvocationDescriptor Invocation
) : HistoryEntry, IModelMessage {
    public override HistoryEntryKind Kind => HistoryEntryKind.Model;
    public MessageRole Role => MessageRole.Model;
    string IModelMessage.Contents => Contents;
    IReadOnlyList<ParsedToolCall> IModelMessage.ToolCalls => ToolCalls;
}


public record class PromptEntry(
    LevelOfDetailContent? Notifications = null
) : HistoryEntry {
    public override HistoryEntryKind Kind => HistoryEntryKind.Prompt;

    public virtual PromptMessage GetMessage(LevelOfDetail detailLevel, string? windows) {
        return new PromptMessage(
            Timestamp: Timestamp,
            Notifications: Notifications?.GetContent(detailLevel),
            Windows: windows
        );
    }
}
public sealed record ToolEntry(
    IReadOnlyList<LodToolCallResult> Results,
    string? ExecuteError,
    LevelOfDetailContent? Notifications = null
) : PromptEntry(Notifications) {
    public override HistoryEntryKind Kind => HistoryEntryKind.Tool;

    public override ToolMessage GetMessage(LevelOfDetail detailLevel, string? windows) {
        IReadOnlyList<ToolResult> projectedResults = ProjectResults(Results, detailLevel);

        return new ToolMessage(
            Timestamp: Timestamp,
            Notifications: Notifications?.GetContent(detailLevel),
            Windows: windows,
            Results: projectedResults,
            ExecuteError: ExecuteError
        );
    }

    private static IReadOnlyList<ToolResult> ProjectResults(
        IReadOnlyList<LodToolCallResult> source,
        LevelOfDetail detailLevel
    ) {
        if (source.Count == 0) { return ImmutableArray<ToolResult>.Empty; }

        var builder = ImmutableArray.CreateBuilder<ToolResult>(source.Count);

        for (int i = 0; i < source.Count; i++) {
            LodToolCallResult item = source[i];

            builder.Add(
                new ToolResult(
                    item.ToolName ?? string.Empty,
                    item.ToolCallId ?? string.Empty,
                    item.Status,
                    item.Result.GetContent(detailLevel),
                    item.Elapsed
                )
            );
        }

        return builder.MoveToImmutable();
    }
}
