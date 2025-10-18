using System.Collections.Immutable;

namespace Atelia.LiveContextProto.Context;

internal interface IContextMessage {
    ContextMessageRole Role { get; }
    DateTimeOffset Timestamp { get; }
    ImmutableDictionary<string, object?> Metadata { get; }
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

internal interface IToolCallCarrier {
    IReadOnlyList<ToolCallRequest> ToolCalls { get; }
}

internal interface ITokenUsageCarrier {
    TokenUsage? Usage { get; }
}

internal interface IContextAttachment {
}

internal enum ContextMessageRole {
    ModelInput,
    ModelOutput,
    ToolResult
}

internal record ModelInvocationDescriptor(
    string ProviderId,
    string Specification,
    string Model
);

internal record TokenUsage(int PromptTokens, int CompletionTokens, int? CachedPromptTokens = null);
