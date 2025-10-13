using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Atelia.LiveContextProto.State.History;

internal enum HistoryEntryKind {
    ModelInput,
    ModelOutput,
    ToolResult
}

internal enum ContextMessageRole {
    System,
    ModelInput,
    ModelOutput,
    ToolResult
}

internal interface IContextMessage {
    ContextMessageRole Role { get; }
    DateTimeOffset Timestamp { get; }
    ImmutableDictionary<string, object?> Metadata { get; }
}

internal interface ISystemMessage : IContextMessage {
    string Instruction { get; }
}

internal interface IModelInputMessage : IContextMessage {
    IReadOnlyList<KeyValuePair<string, string>> ContentSections { get; }
    IReadOnlyList<IContextAttachment> Attachments { get; }
}

internal interface IModelOutputMessage : IContextMessage, IToolCallCarrier {
    IReadOnlyList<string> Contents { get; }
    ModelInvocationDescriptor Invocation { get; }
}

internal interface IToolResultsMessage : IContextMessage {
    IReadOnlyList<ToolCallResult> Results { get; }
    string? ExecuteError { get; }
}

internal interface ILiveScreenCarrier {
    string? LiveScreen { get; }
    IContextMessage InnerMessage { get; }
}

internal interface IToolCallCarrier {
    IReadOnlyList<ToolCallRequest> ToolCalls { get; }
}

internal interface ITokenUsageCarrier {
    TokenUsage? Usage { get; }
}

internal interface IContextAttachment {
}

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

internal record ModelInvocationDescriptor(
    string ProviderId,
    string Specification,
    string Model
);

internal record TokenUsage(int PromptTokens, int CompletionTokens, int? CachedPromptTokens = null);

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
