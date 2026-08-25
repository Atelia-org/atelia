using System.Text.Json.Serialization;
using Atelia.Completion;
using Atelia.SessionJournal.RecapGrid.AgentControl;

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
    string? CallLogDir = null,
    bool MaintenanceMode = false,
    GalateaRecapGridRuntimeConfig? RecapGrid = null
);

public sealed record GalateaRecapGridRuntimeConfig(
    string RouteManifestPath,
    RecapGridAgentControlProfileRegistry AgentControlProfiles,
    string CurrentAgentControlProfileId
);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record GalateaRecapGridFileConfig(
    string RouteManifestPath,
    IReadOnlyList<string> AgentControlProfileFiles,
    string CurrentAgentControlProfileId
);

/// <summary>Shape of config.json: user accounts + server settings, with no LLM binding.</summary>
internal sealed record GalateaUsersFileConfig(
    [property: JsonPropertyName("v")] int Version,
    IReadOnlyList<GalateaUserConfig> Users,
    IReadOnlyList<string>? ListenUrls = null,
    string? CallLogDir = null,
    bool MaintenanceMode = false,
    GalateaRecapGridFileConfig? RecapGrid = null
);

public sealed record GalateaUserConfig(
    string UserId,
    string Password,
    string SessionDir,
    GalateaSessionProvisioning SessionProvisioning,
    string SystemPrompt = "",
    // Optional path to a markdown (or plain text) file whose content overrides the
    // inline SystemPrompt. Resolved relative to the config file's directory when not
    // absolute. Convenient for authoring long system prompts.
    string? SystemPromptFile = null
);

[JsonConverter(typeof(JsonStringEnumConverter<GalateaSessionProvisioning>))]
public enum GalateaSessionProvisioning {
    [JsonStringEnumMemberName("existing-only")]
    ExistingOnly,
    [JsonStringEnumMemberName("create-if-missing")]
    CreateIfMissing
}

internal static class GalateaConfigValidation {
    internal static void RequireDistinctSessionDirectories(
        IReadOnlyList<GalateaUserConfig> users
    ) {
        ArgumentNullException.ThrowIfNull(users);
        StringComparer comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var owners = new Dictionary<
            string,
            (string UserId, string ConfiguredPath)
        >(comparer);

        for (int index = 0; index < users.Count; index++) {
            GalateaUserConfig user = users[index]
                ?? throw new InvalidOperationException(
                    $"Galatea config user[{index}] must not be null."
                );
            if (user.SessionProvisioning is not (
                    GalateaSessionProvisioning.ExistingOnly
                    or GalateaSessionProvisioning.CreateIfMissing)) {
                throw new InvalidOperationException(
                    $"Galatea config user '{user.UserId}' has an unknown "
                    + "sessionProvisioning policy."
                );
            }
            if (string.IsNullOrWhiteSpace(user.SessionDir)) {
                throw new InvalidOperationException(
                    $"Galatea config user '{user.UserId}' must have a "
                    + "non-empty sessionDir."
                );
            }
            string normalized = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(user.SessionDir)
            );
            if (owners.TryGetValue(normalized, out var existing)) {
                throw new InvalidOperationException(
                    "Galatea config users "
                    + $"'{existing.UserId}' (sessionDir "
                    + $"'{existing.ConfiguredPath}') and '{user.UserId}' "
                    + $"(sessionDir '{user.SessionDir}') resolve to the "
                    + $"same lexical session path '{normalized}'."
                );
            }
            owners.Add(normalized, (user.UserId, user.SessionDir));
        }
    }
}

public sealed record GalateaConnectionInfoDto(
    string Id,
    string ModelId
);

public sealed record GalateaMeDto(
    string UserId,
    bool MaintenanceMode
);

public sealed record RecentTurnDto(
    string UserText,
    AssistantMessageDto Assistant
);

public sealed record ContextHeaderDto(
    string Observation,
    string Action
) {
    internal static ContextHeaderDto Empty { get; } = new(
        string.Empty,
        string.Empty
    );
}

public sealed record RecapGridReadinessAuthorityDto(
    string RefId,
    string TimelineId,
    long TimelineGeneration,
    string? TimelineHeadRowId,
    long ControlGeneration,
    string ControlStateDigest,
    string StoreInstanceId,
    int StoreSchemaVersion,
    string RecipeDigest,
    string ThroughRowId,
    string ThroughDescriptorDigest
);

public sealed record RecapGridReadinessMetricsDto(
    int SelectedRows,
    int RecipeRowSteps,
    int ExaminedAssignments,
    int MissingAssignments
);

public sealed record RecapGridReserveBootstrapMetricsDto(
    long ExaminedTimelineRows,
    int ExaminedRawEvents,
    int ExaminedHistoryUnits,
    int ExaminedRenderedUtf8Bytes
);

public sealed record RecapGridReserveBootstrapEvidenceDto(
    string RefId,
    string TimelineId,
    long TimelineGeneration,
    string? TimelineHeadRowId,
    long CadenceGeneration,
    string CadenceDomainDigest,
    long ControlGeneration,
    string ControlStateDigest,
    string StoreInstanceId,
    int StoreSchemaVersion,
    long RetainedHistoryLoad,
    long RequiredHistoryLoad,
    long VerifiedRows,
    RecapGridReserveBootstrapMetricsDto Metrics
);

public sealed record RecapGridMissingAssignmentDto(
    int Ordinal,
    string RowId,
    string RecipeDigest,
    string LogicalColumnId,
    string EvaluationKey
);

public sealed record RecapGridReadinessSnapshotDto(
    string Freshness,
    string State,
    string? ObservedRawHead,
    RecapGridReadinessAuthorityDto? Authority = null,
    RecapGridReadinessMetricsDto? Metrics = null,
    IReadOnlyList<RecapGridMissingAssignmentDto>? OrderedMissing = null,
    string? Code = null,
    string? Detail = null,
    RecapGridReserveBootstrapEvidenceDto? ReserveBootstrap = null
);

public sealed record RecentTurnsResponseDto(
    IReadOnlyList<RecentTurnDto> Turns,
    string? RewindLatestToken,
    ContextHeaderDto ContextHeader,
    RecapGridReadinessSnapshotDto? RecapGridReadiness = null
);

public sealed record AssistantMessageDto(
    string Text,
    string? ReasoningText
);

internal sealed record ChatStreamRequest(
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("connectionId")] string? ConnectionId = null
);

internal sealed record PopLatestTurnReceiptDto(
    string PoppedUserText
);

internal sealed record PopLatestTurnRequestDto(
    [property: JsonPropertyName("rewindLatestToken")]
    string RewindLatestToken
);

internal sealed record StartTurnResponseDto(
    string TurnId
);

internal sealed record CurrentTurnDto(
    string Status,
    string? TurnId = null,
    string? ConnectionId = null,
    bool RestartRequired = false,
    string? RecoveryHead = null
);

internal sealed record ResumeTurnRequest(
    [property: JsonPropertyName("expectedHead")]
    string ExpectedHead,
    [property: JsonPropertyName("connectionId")]
    string? ConnectionId = null,
    [property: JsonPropertyName("restartUncertainCompletion")]
    bool RestartUncertainCompletion = false
);
