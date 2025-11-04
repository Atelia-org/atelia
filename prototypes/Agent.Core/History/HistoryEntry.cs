using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Atelia.Completion.Abstractions;
using Atelia.Agent.Core.Tool;

namespace Atelia.Agent.Core.History;

public enum HistoryEntryKind {
    Observation,
    Action,
    ToolResults
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

public sealed record ActionEntry(
    string Contents,
    IReadOnlyList<ParsedToolCall> ToolCalls,
    ModelInvocationDescriptor Invocation
) : HistoryEntry, IActionMessage {
    public override HistoryEntryKind Kind => HistoryEntryKind.Action;
    HistoryMessageKind IHistoryMessage.Kind => HistoryMessageKind.Action;
    string IActionMessage.Contents => Contents;
    IReadOnlyList<ParsedToolCall> IActionMessage.ToolCalls => ToolCalls;
}


public record class ObservationEntry(
    LevelOfDetailContent? Notifications = null
) : HistoryEntry {
    public override HistoryEntryKind Kind => HistoryEntryKind.Observation;

    public virtual ObservationMessage GetMessage(LevelOfDetail detailLevel, string? windows) {
        return new ObservationMessage(
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
) : ObservationEntry(Notifications) {
    public override HistoryEntryKind Kind => HistoryEntryKind.ToolResults;

    public override ToolResultsMessage GetMessage(LevelOfDetail detailLevel, string? windows) {
        IReadOnlyList<ToolResult> projectedResults = ProjectResults(Results, detailLevel);

        return new ToolResultsMessage(
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
