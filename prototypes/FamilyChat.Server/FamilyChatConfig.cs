using System.Text.Json.Serialization;
using Atelia.Completion;

namespace Atelia.FamilyChat.Server;

/// <summary>
/// Merged runtime configuration. Users (identity + session history + behavior) are
/// loaded from config.json; LLM connections are loaded from a sibling connections.json.
/// The two are intentionally decoupled: a user account owns a session history, while a
/// connection describes an LLM endpoint that can be chosen (and switched) at runtime.
/// </summary>
public sealed record FamilyChatConfig(
    IReadOnlyList<FamilyChatUserConfig> Users,
    IReadOnlyList<CompletionConnectionConfig> Connections,
    string DefaultConnectionId,
    IReadOnlyList<string>? ListenUrls = null
);

/// <summary>Shape of config.json: user accounts + server settings, with no LLM binding.</summary>
public sealed record FamilyChatUsersFileConfig(
    IReadOnlyList<FamilyChatUserConfig> Users,
    IReadOnlyList<string>? ListenUrls = null
);

public sealed record FamilyChatUserConfig(
    string UserId,
    string Password,
    string SessionDir,
    ulong CompactionThresholdTokens,
    string? CompactionSystemPrompt,
    string? CompactionPrompt,
    string SystemPrompt = "",
    // Optional path to a markdown (or plain text) file whose content overrides the
    // inline SystemPrompt. Resolved relative to the config file's directory when not
    // absolute. Convenient for authoring long system prompts.
    string? SystemPromptFile = null
);

public sealed record FamilyChatConnectionInfoDto(
    string Id,
    string ModelId
);

public sealed record FamilyChatMeDto(
    string UserId
);

public sealed record RecentTurnDto(
    string UserText,
    AssistantMessageDto Assistant,
    bool IsRecap = false
);

public sealed record RecentTurnsResponseDto(
    IReadOnlyList<RecentTurnDto> Turns
);

public sealed record AssistantMessageDto(
    string Text,
    string? ReasoningText,
    bool HasReasoning
);

public sealed record ChatStreamRequest(
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("connectionId")] string? ConnectionId = null
);

public sealed record PopLatestTurnResponseDto(
    RecentTurnDto Turn
);

public sealed record StartTurnResponseDto(
    string TurnId,
    string Status,
    string? Error = null
);

public sealed record CurrentTurnDto(
    string Status,
    string? TurnId = null,
    string? UserMessage = null,
    string? Phase = null,
    string? ConnectionId = null
);

public sealed record StreamEventDto(
    string Type,
    object? Payload
);
