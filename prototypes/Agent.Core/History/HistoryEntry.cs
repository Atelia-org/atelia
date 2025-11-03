using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Atelia.LlmProviders;
using Atelia.Agent.Core.Tool;

namespace Atelia.Agent.Core.History;

public enum HistoryEntryKind {
    ModelInput,
    ModelOutput,
    ToolResult
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

public sealed record ModelOutputEntry(
    string Contents,
    IReadOnlyList<ToolCallRequest> ToolCalls,
    ModelInvocationDescriptor Invocation
) : HistoryEntry, IModelOutputMessage {
    public override HistoryEntryKind Kind => HistoryEntryKind.ModelOutput;
    public ContextMessageRole Role => ContextMessageRole.ModelOutput;
    string IModelOutputMessage.Contents => Contents;
    IReadOnlyList<ToolCallRequest> IModelOutputMessage.ToolCalls => ToolCalls;
}


public record class ModelInputEntry(
    LevelOfDetailContent? Notifications = null
) : HistoryEntry {
    public override HistoryEntryKind Kind => HistoryEntryKind.ModelInput;

    public virtual ModelInputMessage GetMessage(LevelOfDetail detailLevel, string? windows) {
        return new ModelInputMessage(
            Timestamp: Timestamp,
            Notifications: Notifications?.GetContent(detailLevel),
            Windows: windows
        );
    }
}
public sealed record ToolResultsEntry(
    IReadOnlyList<LodToolCallResult> Results,
    string? ExecuteError,
    LevelOfDetailContent? Notifications = null
) : ModelInputEntry(Notifications) {
    public override HistoryEntryKind Kind => HistoryEntryKind.ToolResult;

    public override ToolResultsMessage GetMessage(LevelOfDetail detailLevel, string? windows) {
        IReadOnlyList<ToolCallResult> projectedResults = ProjectResults(Results, detailLevel);

        return new ToolResultsMessage(
            Timestamp: Timestamp,
            Notifications: Notifications?.GetContent(detailLevel),
            Windows: windows,
            Results: projectedResults,
            ExecuteError: ExecuteError
        );
    }

    private static IReadOnlyList<ToolCallResult> ProjectResults(
        IReadOnlyList<LodToolCallResult> source,
        LevelOfDetail detailLevel
    ) {
        if (source.Count == 0) { return ImmutableArray<ToolCallResult>.Empty; }

        var builder = ImmutableArray.CreateBuilder<ToolCallResult>(source.Count);

        for (int i = 0; i < source.Count; i++) {
            LodToolCallResult item = source[i];

            builder.Add(
                new ToolCallResult(
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
