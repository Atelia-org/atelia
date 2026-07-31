using System.Text.Json.Serialization;
using Atelia.Completion;

namespace Atelia.Galatea.Server;

/// <summary>
/// Merged runtime configuration. Users (identity + session history + behavior) are
/// loaded from config.json; LLM connections are loaded from a sibling connections.json.
/// The two are intentionally decoupled: a user account owns a session history, while a
/// connection describes an LLM endpoint that can be chosen (and switched) at runtime.
/// </summary>
public sealed record GalateaConfig(
    IReadOnlyList<GalateaUserConfig> Users,
    IReadOnlyList<CompletionConnectionConfig> Connections,
    string DefaultConnectionId,
    IReadOnlyList<string>? ListenUrls = null,
    string? CallLogDir = null
);

/// <summary>Shape of config.json: user accounts + server settings, with no LLM binding.</summary>
public sealed record GalateaUsersFileConfig(
    IReadOnlyList<GalateaUserConfig> Users,
    IReadOnlyList<string>? ListenUrls = null,
    string? CallLogDir = null
);

public sealed record GalateaUserConfig(
    string UserId,
    string Password,
    string SessionDir,
    string SystemPrompt = "",
    // Optional path to a markdown (or plain text) file whose content overrides the
    // inline SystemPrompt. Resolved relative to the config file's directory when not
    // absolute. Convenient for authoring long system prompts.
    string? SystemPromptFile = null
);

public sealed record GalateaConnectionInfoDto(
    string Id,
    string ModelId
);

public sealed record GalateaMeDto(
    string UserId
);

public sealed record RecentTurnDto(
    string UserText,
    AssistantMessageDto Assistant
);

public sealed record RecentTurnsResponseDto(
    IReadOnlyList<RecentTurnDto> Turns,
    string? RewindLatestToken
);

public sealed record AssistantMessageDto(
    string Text,
    string? ReasoningText
);

public sealed record ChatStreamRequest(
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("connectionId")] string? ConnectionId = null
);

public sealed record PopLatestTurnResponseDto(
    RecentTurnDto Turn,
    RecentTurnsResponseDto Recent
);

public sealed record PopLatestTurnRequestDto(
    [property: JsonPropertyName("rewindLatestToken")]
    string RewindLatestToken
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
    string? ConnectionId = null,
    string? DurablePhase = null,
    bool RecoveryRequired = false,
    bool RestartRequired = false,
    string? RecoveryHead = null
);

public sealed record ResumeTurnRequest(
    [property: JsonPropertyName("expectedHead")]
    string ExpectedHead,
    [property: JsonPropertyName("connectionId")]
    string? ConnectionId = null,
    [property: JsonPropertyName("restartUncertainCompletion")]
    bool RestartUncertainCompletion = false
);

public sealed record StreamEventDto(
    string Type,
    object? Payload
);
