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
    string? ApiKey = null
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
    string? SystemPromptFile = null
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
    [property: JsonPropertyName("message")] string Message
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
    string? Phase = null
);

public sealed record StreamEventDto(
    string Type,
    object? Payload
);
