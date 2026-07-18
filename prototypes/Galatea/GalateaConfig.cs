using System.Text.Json.Serialization;

namespace Atelia.Galatea.Server;

/// <summary>
/// Merged runtime configuration. Users (identity + session history + behavior) are
/// loaded from config.json; LLM connections are loaded from a sibling connections.json.
/// The two are intentionally decoupled: a user account owns a session history, while a
/// connection describes an LLM endpoint that can be chosen (and switched) at runtime.
/// </summary>
public sealed record GalateaConfig(
    IReadOnlyList<GalateaUserConfig> Users,
    IReadOnlyList<GalateaConnectionConfig> Connections,
    string DefaultConnectionId,
    IReadOnlyList<string>? ListenUrls = null
);

/// <summary>Shape of config.json: user accounts + server settings, with no LLM binding.</summary>
public sealed record GalateaUsersFileConfig(
    IReadOnlyList<GalateaUserConfig> Users,
    IReadOnlyList<string>? ListenUrls = null
);

/// <summary>Shape of connections.json: the independent LLM connection catalog.</summary>
public sealed record GalateaConnectionsFileConfig(
    IReadOnlyList<GalateaConnectionConfig> Connections,
    string? DefaultConnectionId = null
);

public sealed record GalateaConnectionConfig(
    // Stable identifier used by the UI radio buttons and turn requests.
    string Id,
    // Human-facing label shown in the connection picker.
    string DisplayName,
    // Backend family: openai-chat / openai-responses / anthropic.
    string Kind,
    string ModelId,
    string CompletionSurfaceId,
    string BaseAddress,
    string? ApiKey = null,
    // Name of an environment variable whose value overrides BaseAddress at load time.
    // Useful for keeping machine-specific URLs out of the config file.
    string? BaseAddressEnv = null,
    // Name of an environment variable whose value overrides ApiKey at load time.
    // Preferred over inline ApiKey to keep secrets out of the config file.
    string? ApiKeyEnv = null
);

public sealed record GalateaUserConfig(
    string UserId,
    string DisplayName,
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

public sealed record GalateaConnectionInfoDto(
    string Id,
    string DisplayName,
    string ModelId,
    bool DefaultAutoPrefillThinkOpenTag
);

public sealed record GalateaMeDto(
    string UserId,
    string DisplayName
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
    [property: JsonPropertyName("autoPrefillThinkOpenTag")] bool? AutoPrefillThinkOpenTag = null,
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
    bool? AutoPrefillThinkOpenTag = null,
    string? ConnectionId = null
);

public sealed record StreamEventDto(
    string Type,
    object? Payload
);
