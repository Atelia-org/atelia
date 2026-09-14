using System.Text;
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
    IReadOnlyList<string> SelectableConnectionIds,
    string? InputNormalizerConnectionId,
    GalateaDelegateConfig Delegates,
    string? OutboundMailExtractorConnectionId = null,
    string? CharacterNoteExtractorConnectionId = null,
    string? MemoRecallConnectionId = null,
    IReadOnlyList<string>? ListenUrls = null,
    string? CallLogDir = null,
    bool MaintenanceMode = false,
    GalateaRecapGridRuntimeConfig? RecapGrid = null,
    IReadOnlyList<string>? ServerAgentUserIds = null
) {
    public IReadOnlyList<string> ServerAgentUserIds { get; init; } =
        GalateaConfigValidation.NormalizeServerAgentUserIds(
            Users,
            ServerAgentUserIds
        );
}

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

/// <summary>
/// Shape of config.json: user accounts, their default Agent selections, and
/// server settings. Provider connection definitions remain in connections.json.
/// </summary>
internal sealed record GalateaUsersFileConfig(
    [property: JsonPropertyName("v")] int Version,
    IReadOnlyList<GalateaUserFileConfig> Users,
    IReadOnlyList<string>? ListenUrls = null,
    string? CallLogDir = null,
    bool MaintenanceMode = false,
    GalateaRecapGridFileConfig? RecapGrid = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? ServerAgentUserIds = null
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
    string HomeDir,
    GalateaSessionProvisioning SessionProvisioning,
    string DefaultConnectionId,
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
    string HomeDir,
    GalateaSessionProvisioning SessionProvisioning,
    string SystemPrompt,
    string DefaultConnectionId
);

[JsonConverter(typeof(JsonStringEnumConverter<GalateaSessionProvisioning>))]
public enum GalateaSessionProvisioning {
    [JsonStringEnumMemberName("existing-only")]
    ExistingOnly,
    [JsonStringEnumMemberName("create-if-missing")]
    CreateIfMissing
}

internal static class GalateaConfigValidation {
    internal const int MaximumConnectionIdUtf8Bytes = 128;

    internal static IReadOnlyList<string> NormalizeServerAgentUserIds(
        IReadOnlyList<GalateaUserConfig> users,
        IReadOnlyList<string>? serverAgentUserIds
    ) {
        ArgumentNullException.ThrowIfNull(users);
        if (serverAgentUserIds is null or { Count: 0 }) {
            return Array.Empty<string>();
        }
        if (serverAgentUserIds.Count > GalateaStrictConfigReader.MaximumUserCount) {
            throw new InvalidOperationException(
                "Galatea config serverAgentUserIds exceeds its count cap."
            );
        }
        var userIds = users.Select(static user => user.UserId)
            .ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var snapshot = new string[serverAgentUserIds.Count];
        for (int index = 0; index < snapshot.Length; index++) {
            string userId = serverAgentUserIds[index];
            if (string.IsNullOrWhiteSpace(userId)) {
                throw new InvalidOperationException(
                    $"Galatea config serverAgentUserIds[{index}] must not be blank."
                );
            }
            if (!seen.Add(userId)) {
                throw new InvalidOperationException(
                    $"Galatea config serverAgentUserIds contains duplicate userId '{userId}'."
                );
            }
            if (!userIds.Contains(userId)) {
                throw new InvalidOperationException(
                    $"Galatea config serverAgentUserIds '{userId}' must exactly match a configured userId."
                );
            }
            snapshot[index] = userId;
        }
        return Array.AsReadOnly(snapshot);
    }

    internal static void RequireValidConnectionDefaults(
        IReadOnlyList<GalateaUserConfig> users,
        CompletionConnectionCatalogConfig catalog
    ) {
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(catalog);
        if (catalog.SelectableConnectionIds is null) {
            throw new InvalidOperationException(
                "Galatea connections require selectableConnectionIds before "
                + "per-user defaults can be validated."
            );
        }

        var connectionIds = catalog.Connections
            .Select(static connection => connection.Id)
            .ToHashSet(StringComparer.Ordinal);
        var selectable = catalog.SelectableConnectionIds.ToHashSet(
            StringComparer.Ordinal
        );
        for (int index = 0; index < users.Count; index++) {
            GalateaUserConfig user = users[index]
                ?? throw new InvalidOperationException(
                    $"Galatea config user[{index}] must not be null."
                );
            if (string.IsNullOrWhiteSpace(user.DefaultConnectionId)) {
                throw new InvalidOperationException(
                    $"Galatea config user '{user.UserId}' must have a "
                    + "non-empty defaultConnectionId."
                );
            }
            if (Encoding.UTF8.GetByteCount(user.DefaultConnectionId)
                > MaximumConnectionIdUtf8Bytes) {
                throw new InvalidOperationException(
                    $"Galatea config user '{user.UserId}' defaultConnectionId "
                    + "exceeds its UTF-8 byte bound."
                );
            }
            if (!connectionIds.Contains(user.DefaultConnectionId)) {
                throw new InvalidOperationException(
                    $"Galatea config user '{user.UserId}' defaultConnectionId "
                    + $"'{user.DefaultConnectionId}' does not exactly match a "
                    + "catalog connection id."
                );
            }
            if (!selectable.Contains(user.DefaultConnectionId)) {
                throw new InvalidOperationException(
                    $"Galatea config user '{user.UserId}' defaultConnectionId "
                    + $"'{user.DefaultConnectionId}' is not selectable."
                );
            }
        }
    }

    internal static void RequireValidStorageTopology(
        IReadOnlyList<GalateaUserConfig> users,
        string? callLogDirectory
    ) {
        ArgumentNullException.ThrowIfNull(users);
        StringComparer comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
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
            string CharacterMemoryStateDirectory,
            string HomeDirectory
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
            string normalizedSession = RequireCanonicalAbsoluteDirectory(
                user.SessionDir,
                $"sessionDir for user '{user.UserId}'",
                comparison
            );
            string normalizedDelegation = RequireCanonicalAbsoluteDirectory(
                user.DelegationStateDir,
                $"delegationStateDir for user '{user.UserId}'",
                comparison
            );
            string normalizedCharacterMemory =
                RequireCanonicalAbsoluteDirectory(
                    user.CharacterMemoryStateDir,
                    $"characterMemoryStateDir for user '{user.UserId}'",
                    comparison
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
            string normalizedHome = GalateaDelegateConfigReader.RequireCanonicalDirectory(
                user.HomeDir,
                $"homeDir for user '{user.UserId}'"
            );
            normalizedUsers.Add((
                user.UserId,
                normalizedSession,
                normalizedDelegation,
                normalizedCharacterMemory,
                normalizedHome
            ));
        }

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

        for (int homeIndex = 0; homeIndex < normalizedUsers.Count; homeIndex++) {
            var home = normalizedUsers[homeIndex];
            foreach (var user in normalizedUsers) {
                RequireDisjoint(home.HomeDirectory, $"homeDir for user '{home.UserId}'",
                    user.SessionDirectory, $"sessionDir for user '{user.UserId}'", comparison);
                RequireDisjoint(home.HomeDirectory, $"homeDir for user '{home.UserId}'",
                    user.DelegationStateDirectory, $"delegationStateDir for user '{user.UserId}'", comparison);
                RequireDisjoint(home.HomeDirectory, $"homeDir for user '{home.UserId}'",
                    user.CharacterMemoryStateDirectory, $"characterMemoryStateDir for user '{user.UserId}'", comparison);
            }
            for (int otherIndex = homeIndex + 1; otherIndex < normalizedUsers.Count; otherIndex++) {
                var other = normalizedUsers[otherIndex];
                RequireDisjoint(home.HomeDirectory, $"homeDir for user '{home.UserId}'",
                    other.HomeDirectory, $"homeDir for user '{other.UserId}'", comparison);
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
            RequireDisjoint(normalizedCallLogs, "callLogDir", user.HomeDirectory,
                $"homeDir for user '{user.UserId}'", comparison);
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

    private static string RequireCanonicalAbsoluteDirectory(
        string path,
        string description,
        StringComparison comparison
    ) {
        if (!Path.IsPathFullyQualified(path)) {
            throw new InvalidOperationException(
                $"Galatea {description} must be absolute."
            );
        }
        string canonical = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(path)
        );
        if (!string.Equals(path, canonical, comparison)) {
            throw new InvalidOperationException(
                $"Galatea {description} must already be canonical."
            );
        }
        return canonical;
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
    string LogicalColumnId
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

internal sealed record ReadyReplyTurnRequest;

internal sealed record GalateaAgentStatusDto(
    string State,
    string? ConnectionId,
    long? NextActivationAtUnixTimeMilliseconds,
    long? LastActivationAtUnixTimeMilliseconds,
    string? Code,
    ApiErrorDto? AdmissionFailure = null
);

internal sealed record GalateaMailboxStatusDto(
    string State,
    int QueuedCount,
    int ReadyNoticeCount,
    int AttemptCount,
    string? Code,
    long? NextRetryAtUnixTimeMilliseconds
) {
    internal static GalateaMailboxStatusDto FromProjection(
        GalateaMailboxStatusProjection value
    ) {
        ArgumentNullException.ThrowIfNull(value);
        string state = value.State switch {
            GalateaMailboxStatusState.NoMail => "no-mail",
            GalateaMailboxStatusState.Queued => "queued",
            GalateaMailboxStatusState.ActiveRunning => "active-running",
            GalateaMailboxStatusState.Backoff => "backoff",
            GalateaMailboxStatusState.AcceptedHistoryUnavailable =>
                "accepted-history-unavailable",
            GalateaMailboxStatusState.ReadyReply => "ready-reply",
            GalateaMailboxStatusState.Quarantined => "quarantined",
            GalateaMailboxStatusState.Unavailable => "unavailable",
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };
        return new(
            state,
            value.QueuedCount,
            value.ReadyNoticeCount,
            value.AttemptCount,
            value.Code,
            value.NextRetryAtUnixTimeMilliseconds
        );
    }
}

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

internal sealed record LoopPulseAcceptedTurnDto(
    string TurnId,
    string Origin
);

internal sealed record LoopPulseStatusDto(
    string State,
    long? NextActivationAtUnixTimeMilliseconds,
    long? LastActivationAtUnixTimeMilliseconds,
    string? Code
) {
    internal static LoopPulseStatusDto FromProjection(
        GalateaAutonomyCadenceStatus value
    ) {
        ArgumentNullException.ThrowIfNull(value);
        return new(
            value.State,
            value.NextActivationAtUnixTimeMilliseconds,
            value.LastActivationAtUnixTimeMilliseconds,
            value.Code
        );
    }
}

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
