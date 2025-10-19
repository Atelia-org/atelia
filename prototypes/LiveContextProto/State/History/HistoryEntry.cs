using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Atelia.LiveContextProto.Context;
using Atelia.LiveContextProto.Tools;

namespace Atelia.LiveContextProto.State.History;

internal enum HistoryEntryKind {
    ModelInput,
    ModelOutput,
    ToolResult
}

internal abstract record HistoryEntry {
    public DateTimeOffset Timestamp { get; init; }
    public abstract HistoryEntryKind Kind { get; }
    public ImmutableDictionary<string, object?> Metadata { get; init; } = ImmutableDictionary<string, object?>.Empty;
}

internal abstract record ContextualHistoryEntry : HistoryEntry, IContextMessage {
    public abstract ContextMessageRole Role { get; }
}

internal sealed record ModelInputEntry(
    LevelOfDetailSections ContentSections
) : ContextualHistoryEntry {
    public override HistoryEntryKind Kind => HistoryEntryKind.ModelInput;
    public override ContextMessageRole Role => ContextMessageRole.ModelInput;
    public IReadOnlyList<IContextAttachment> Attachments { get; init; } = Array.Empty<IContextAttachment>();
}

internal sealed record ModelOutputEntry(
    IReadOnlyList<string> Contents,
    IReadOnlyList<ToolCallRequest> ToolCalls,
    ModelInvocationDescriptor Invocation
) : ContextualHistoryEntry, IModelOutputMessage {
    public override HistoryEntryKind Kind => HistoryEntryKind.ModelOutput;
    public override ContextMessageRole Role => ContextMessageRole.ModelOutput;
    IReadOnlyList<ToolCallRequest> IToolCallCarrier.ToolCalls => ToolCalls;
    IReadOnlyList<string> IModelOutputMessage.Contents => Contents;
    ModelInvocationDescriptor IModelOutputMessage.Invocation => Invocation;
}

internal sealed record ToolResultsEntry(
    IReadOnlyList<LodToolCallResult> Results,
    string? ExecuteError
) : ContextualHistoryEntry {
    public override HistoryEntryKind Kind => HistoryEntryKind.ToolResult;
    public override ContextMessageRole Role => ContextMessageRole.ToolResult;
}
