using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Atelia.LiveContextProto.Context;

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
    IReadOnlyList<KeyValuePair<string, string>> ContentSections
) : ContextualHistoryEntry, IModelInputMessage {
    public override HistoryEntryKind Kind => HistoryEntryKind.ModelInput;
    public override ContextMessageRole Role => ContextMessageRole.ModelInput;
    public IReadOnlyList<IContextAttachment> Attachments { get; init; } = Array.Empty<IContextAttachment>();
    IReadOnlyList<KeyValuePair<string, string>> IModelInputMessage.ContentSections => ContentSections;
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
    IReadOnlyList<ToolCallResult> Results,
    string? ExecuteError
) : ContextualHistoryEntry, IToolResultsMessage {
    public override HistoryEntryKind Kind => HistoryEntryKind.ToolResult;
    public override ContextMessageRole Role => ContextMessageRole.ToolResult;
    IReadOnlyList<ToolCallResult> IToolResultsMessage.Results => Results;
    string? IToolResultsMessage.ExecuteError => ExecuteError;
}

internal sealed record SystemInstructionMessage(string Instruction) : ISystemMessage {
    public ContextMessageRole Role => ContextMessageRole.System;
    public DateTimeOffset Timestamp { get; init; }
    public ImmutableDictionary<string, object?> Metadata { get; init; } = ImmutableDictionary<string, object?>.Empty;
}

internal static class ContextMessageLiveScreenHelper {
    public static IContextMessage AttachLiveScreen(IContextMessage message, string? liveScreen)
        => string.IsNullOrWhiteSpace(liveScreen) ? message : new LiveScreenDecoratedMessage(message, liveScreen);

    private sealed record LiveScreenDecoratedMessage(IContextMessage Inner, string? LiveScreen)
        : IContextMessage, ILiveScreenCarrier {
        public ContextMessageRole Role => Inner.Role;
        public DateTimeOffset Timestamp => Inner.Timestamp;
        public ImmutableDictionary<string, object?> Metadata => Inner.Metadata;
        string? ILiveScreenCarrier.LiveScreen => LiveScreen;
        IContextMessage ILiveScreenCarrier.InnerMessage => Inner;
    }
}
