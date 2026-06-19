using System.Text.Json.Serialization;

namespace Atelia.FamilyChat.Server;

public sealed record FamilyChatConfig(
    FamilyChatBackendConfig Backend,
    IReadOnlyList<FamilyChatUserConfig> Users,
    IReadOnlyList<string>? ListenUrls = null
);

public sealed record FamilyChatBackendConfig(
    string Kind,
    string BaseAddress,
    string? ApiKey = null,
    // Name of an environment variable whose value overrides BaseAddress at load
    // time. Useful for keeping machine-specific URLs out of the config file.
    string? BaseAddressEnv = null,
    // Name of an environment variable whose value overrides ApiKey at load time.
    // Preferred over inline ApiKey to keep secrets out of the config file.
    string? ApiKeyEnv = null
);

public sealed record FamilyChatUserConfig(
    string UserId,
    string DisplayName,
    string Password,
    string SessionDir,
    string ModelId,
    string CompletionSurfaceId,
    ulong CompactionThresholdTokens,
    string? CompactionSystemPrompt,
    string? CompactionPrompt,
    string SystemPrompt = "",
    // Optional path to a markdown (or plain text) file whose content overrides
    // the inline SystemPrompt. Resolved relative to the config file's directory
    // when not absolute. Convenient for authoring long system prompts.
    string? SystemPromptFile = null,
    // Optional per-user backend override. When non-empty, this user's completion
    // requests are routed to this BaseAddress instead of the global Backend.BaseAddress.
    // Useful when different users should talk to different LLM providers.
    string? BaseAddress = null,
    // Optional per-user API key. When non-empty, used instead of Backend.ApiKey.
    string? ApiKey = null,
    // Name of an environment variable whose value overrides the per-user BaseAddress
    // at load time. Resolved after the inline BaseAddress, so env wins if both set.
    string? BaseAddressEnv = null,
    // Name of an environment variable whose value overrides the per-user ApiKey
    // at load time.
    string? ApiKeyEnv = null
);

public sealed record FamilyChatMeDto(
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
    [property: JsonPropertyName("autoPrefillThinkOpenTag")] bool? AutoPrefillThinkOpenTag = null
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
    bool? AutoPrefillThinkOpenTag = null
);

public sealed record StreamEventDto(
    string Type,
    object? Payload
);
