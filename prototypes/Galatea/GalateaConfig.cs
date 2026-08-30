using System.Text.Json.Serialization;
using Atelia.Completion;
using Atelia.Galatea.Prompts;
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
    IReadOnlyList<string> SelectableConnectionIds,
    string? InputNormalizerConnectionId,
    GalateaDelegateConfig Delegates,
    string? OutboundMailExtractorConnectionId = null,
    string? CharacterNoteExtractorConnectionId = null,
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
    IReadOnlyList<GalateaUserFileConfig> Users,
    IReadOnlyList<string>? ListenUrls = null,
    string? CallLogDir = null,
    bool MaintenanceMode = false,
    GalateaRecapGridFileConfig? RecapGrid = null
);

/// <summary>
/// Exact per-user shape read from config.json before paths are resolved and
/// character-context templates are materialized.
/// </summary>
internal sealed record GalateaUserFileConfig(
    string UserId,
    string Password,
    string CharacterName,
    string PlayerName,
    string SessionDir,
    string DelegationStateDir,
    string CharacterMemoryStateDir,
    GalateaSessionProvisioning SessionProvisioning,
    string CharacterContextTemplate = "",
    string? CharacterContextTemplateFile = null
);

public sealed record GalateaUserConfig(
    string UserId,
    string Password,
    GalateaCharacterName CharacterName,
    GalateaPlayerName PlayerName,
    string SessionDir,
    string DelegationStateDir,
    string CharacterMemoryStateDir,
    GalateaSessionProvisioning SessionProvisioning,
    string SystemPrompt
);

[JsonConverter(typeof(JsonStringEnumConverter<GalateaSessionProvisioning>))]
public enum GalateaSessionProvisioning {
    [JsonStringEnumMemberName("existing-only")]
    ExistingOnly,
    [JsonStringEnumMemberName("create-if-missing")]
    CreateIfMissing
}

internal static class GalateaConfigValidation {
    internal static void RequireValidStorageTopology(
        IReadOnlyList<GalateaUserConfig> users,
        string? callLogDirectory
    ) {
        ArgumentNullException.ThrowIfNull(users);
        StringComparer comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var sessionOwners = new Dictionary<
            string,
            (string UserId, string ConfiguredPath)
        >(comparer);
        var delegationOwners = new Dictionary<
            string,
            (string UserId, string ConfiguredPath)
        >(comparer);
        var characterMemoryOwners = new Dictionary<
            string,
            (string UserId, string ConfiguredPath)
        >(comparer);
        var normalizedUsers = new List<(
            string UserId,
            string SessionDirectory,
            string DelegationStateDirectory,
            string CharacterMemoryStateDirectory
        )>(users.Count);

        for (int index = 0; index < users.Count; index++) {
            GalateaUserConfig user = users[index]
                ?? throw new InvalidOperationException(
                    $"Galatea config user[{index}] must not be null."
                );
            if (user.CharacterName is null) {
                throw new InvalidOperationException(
                    $"Galatea config user '{user.UserId}' must have a "
                    + "validated characterName."
                );
            }
            if (user.PlayerName is null) {
                throw new InvalidOperationException(
                    $"Galatea config user '{user.UserId}' must have a "
                    + "validated playerName."
                );
            }
            if (string.IsNullOrWhiteSpace(user.SystemPrompt)) {
                throw new InvalidOperationException(
                    $"Galatea config user '{user.UserId}' must have a "
                    + "non-empty finalized system prompt."
                );
            }
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
            if (string.IsNullOrWhiteSpace(user.DelegationStateDir)) {
                throw new InvalidOperationException(
                    $"Galatea config user '{user.UserId}' must have a "
                    + "non-empty delegationStateDir."
                );
            }
            if (string.IsNullOrWhiteSpace(user.CharacterMemoryStateDir)) {
                throw new InvalidOperationException(
                    $"Galatea config user '{user.UserId}' must have a "
                    + "non-empty characterMemoryStateDir."
                );
            }
            string normalizedSession = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(user.SessionDir)
            );
            string normalizedDelegation = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(user.DelegationStateDir)
            );
            string normalizedCharacterMemory =
                Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(user.CharacterMemoryStateDir)
                );
            if (sessionOwners.TryGetValue(
                    normalizedSession,
                    out var existingSession)) {
                throw new InvalidOperationException(
                    "Galatea config users "
                    + $"'{existingSession.UserId}' (sessionDir "
                    + $"'{existingSession.ConfiguredPath}') and '{user.UserId}' "
                    + $"(sessionDir '{user.SessionDir}') resolve to the "
                    + $"same lexical session path '{normalizedSession}'."
                );
            }
            sessionOwners.Add(
                normalizedSession,
                (user.UserId, user.SessionDir)
            );
            if (delegationOwners.TryGetValue(
                    normalizedDelegation,
                    out var existingDelegation)) {
                throw new InvalidOperationException(
                    "Galatea config users "
                    + $"'{existingDelegation.UserId}' (delegationStateDir "
                    + $"'{existingDelegation.ConfiguredPath}') and "
                    + $"'{user.UserId}' (delegationStateDir "
                    + $"'{user.DelegationStateDir}') resolve to the same "
                    + $"lexical delegation state path "
                    + $"'{normalizedDelegation}'."
                );
            }
            delegationOwners.Add(
                normalizedDelegation,
                (user.UserId, user.DelegationStateDir)
            );
            if (characterMemoryOwners.TryGetValue(
                    normalizedCharacterMemory,
                    out var existingCharacterMemory)) {
                throw new InvalidOperationException(
                    "Galatea config users "
                    + $"'{existingCharacterMemory.UserId}' "
                    + "(characterMemoryStateDir "
                    + $"'{existingCharacterMemory.ConfiguredPath}') and "
                    + $"'{user.UserId}' (characterMemoryStateDir "
                    + $"'{user.CharacterMemoryStateDir}') resolve to the "
                    + "same lexical character memory state path "
                    + $"'{normalizedCharacterMemory}'."
                );
            }
            characterMemoryOwners.Add(
                normalizedCharacterMemory,
                (user.UserId, user.CharacterMemoryStateDir)
            );
            normalizedUsers.Add((
                user.UserId,
                normalizedSession,
                normalizedDelegation,
                normalizedCharacterMemory
            ));
        }

        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        for (int delegationIndex = 0;
             delegationIndex < normalizedUsers.Count;
             delegationIndex++) {
            var delegation = normalizedUsers[delegationIndex];
            foreach (var session in normalizedUsers) {
                RequireDisjoint(
                    delegation.DelegationStateDirectory,
                    $"delegationStateDir for user '{delegation.UserId}'",
                    session.SessionDirectory,
                    $"sessionDir for user '{session.UserId}'",
                    comparison
                );
            }
            for (int otherIndex = delegationIndex + 1;
                 otherIndex < normalizedUsers.Count;
                 otherIndex++) {
                var other = normalizedUsers[otherIndex];
                RequireDisjoint(
                    delegation.DelegationStateDirectory,
                    $"delegationStateDir for user '{delegation.UserId}'",
                    other.DelegationStateDirectory,
                    $"delegationStateDir for user '{other.UserId}'",
                    comparison
                );
            }
        }

        for (int characterMemoryIndex = 0;
             characterMemoryIndex < normalizedUsers.Count;
             characterMemoryIndex++) {
            var characterMemory = normalizedUsers[characterMemoryIndex];
            foreach (var session in normalizedUsers) {
                RequireDisjoint(
                    characterMemory.CharacterMemoryStateDirectory,
                    $"characterMemoryStateDir for user '{characterMemory.UserId}'",
                    session.SessionDirectory,
                    $"sessionDir for user '{session.UserId}'",
                    comparison
                );
            }
            foreach (var delegation in normalizedUsers) {
                RequireDisjoint(
                    characterMemory.CharacterMemoryStateDirectory,
                    $"characterMemoryStateDir for user '{characterMemory.UserId}'",
                    delegation.DelegationStateDirectory,
                    $"delegationStateDir for user '{delegation.UserId}'",
                    comparison
                );
            }
            for (int otherIndex = characterMemoryIndex + 1;
                 otherIndex < normalizedUsers.Count;
                 otherIndex++) {
                var other = normalizedUsers[otherIndex];
                RequireDisjoint(
                    characterMemory.CharacterMemoryStateDirectory,
                    $"characterMemoryStateDir for user '{characterMemory.UserId}'",
                    other.CharacterMemoryStateDirectory,
                    $"characterMemoryStateDir for user '{other.UserId}'",
                    comparison
                );
            }
        }

        if (callLogDirectory is null) { return; }
        if (string.IsNullOrWhiteSpace(callLogDirectory)) {
            throw new InvalidOperationException(
                "Galatea callLogDir must not be blank."
            );
        }
        string normalizedCallLogs = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(callLogDirectory)
        );
        foreach (var user in normalizedUsers) {
            RequireDisjoint(
                normalizedCallLogs,
                "callLogDir",
                user.SessionDirectory,
                $"sessionDir for user '{user.UserId}'",
                comparison
            );
            RequireDisjoint(
                normalizedCallLogs,
                "callLogDir",
                user.DelegationStateDirectory,
                $"delegationStateDir for user '{user.UserId}'",
                comparison
            );
            RequireDisjoint(
                normalizedCallLogs,
                "callLogDir",
                user.CharacterMemoryStateDirectory,
                $"characterMemoryStateDir for user '{user.UserId}'",
                comparison
            );
        }
    }

    internal static void RequireDisjoint(
        string first,
        string firstDescription,
        string second,
        string secondDescription,
        StringComparison comparison
    ) {
        if (string.Equals(first, second, comparison)
            || IsAncestor(first, second, comparison)
            || IsAncestor(second, first, comparison)) {
            throw new InvalidOperationException(
                $"Galatea {firstDescription} must be disjoint from "
                + $"{secondDescription}."
            );
        }
    }

    private static bool IsAncestor(
        string ancestor,
        string descendant,
        StringComparison comparison
    ) {
        string prefix = Path.EndsInDirectorySeparator(ancestor)
            ? ancestor
            : ancestor + Path.DirectorySeparatorChar;
        return descendant.StartsWith(prefix, comparison);
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

/// <summary>
/// Read-only progress toward the next Recap cadence boundary. HistoryLoad
/// values are canonical non-negative decimal strings because they may exceed
/// JavaScript's exact integer range. HistoryLoad is estimator-scoped and is
/// not a provider token count.
/// </summary>
public sealed record RecapCadenceProgressSnapshotDto(
    string Freshness,
    string State,
    string? ObservedRawHead,
    string? CadenceBaseline,
    int? RecentHistoryPlanningUnitCount,
    string? RecentHistoryLoad,
    string? RecapIntervalHistoryLoad,
    string? MinimumRecentHistoryLoad,
    string? BuildThresholdHistoryLoad,
    string? RemainingHistoryLoad,
    string? HistoryLoadEstimatorId,
    string? Code = null,
    string? Detail = null
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

internal sealed record InboundMailboxRequest(
    [property: JsonPropertyName("from")] string From,
    [property: JsonPropertyName("body")] string Body,
    [property: JsonPropertyName("subject")] string? Subject = null,
    [property: JsonPropertyName("connectionId")] string? ConnectionId = null
);

internal sealed record ReadyReplyTurnRequest(
    [property: JsonPropertyName("connectionId")]
    string? ConnectionId = null
);

internal sealed record InboundMailboxAcceptedDto(
    string TurnId,
    string MessageId
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
