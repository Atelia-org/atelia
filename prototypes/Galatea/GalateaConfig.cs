using System.Text;
using System.Text.Json.Serialization;
using Atelia.Completion;
using Atelia.Galatea.Prompts;
using Atelia.SessionJournal;

namespace Atelia.Galatea.Server;

/// <summary>
/// Resolved Characters, registered Players and runtime dependencies. Character
/// histories and autonomous activity are independent of external Player accounts.
/// </summary>
public sealed record GalateaConfig(
    IReadOnlyList<GalateaCharacterConfig> Characters,
    IReadOnlyList<GalateaPlayerConfig> Players,
    IReadOnlyList<CompletionConnectionConfig> Connections,
    string? InputNormalizerConnectionId,
    GalateaDelegateConfig Delegates,
    string? OutboundMailExtractorConnectionId = null,
    string? CharacterNoteExtractorConnectionId = null,
    string? MemoRecallConnectionId = null,
    IReadOnlyList<string>? ListenUrls = null,
    string? CallLogDir = null,
    bool MaintenanceMode = false,
    GalateaRecapGridRuntimeConfig? RecapGrid = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyDictionary<string, int>? CompletionAttemptTimeoutSeconds = null,
    string? CharacterConnectionStateExtractorConnectionId = null
) {
    // This directory is derived once from the complete config-file character set.
    // Direct in-process test configurations intentionally leave it unset; the
    // character-mail runtime only accepts the loader-produced directory.
    internal GalateaCharacterRecipientDirectory? CharacterRecipientDirectory {
        get;
        init;
    }

    public IReadOnlyList<string> AutonomyCharacterIds =>
        GalateaConfigValidation.ReadAutonomyCharacterIds(Characters);
}

/// <summary>
/// Immutable, host-owned exact address directory for configured characters.
/// Character names are data for prompt rendering and the sole current
/// character-mail address form; they are not aliases or display-only labels.
/// </summary>
internal sealed class GalateaCharacterRecipientDirectory {
    private const string CodexRecipient = "Codex";
    private readonly IReadOnlyDictionary<string, GalateaCharacterRecipient>
        _byCharacterName;
    private readonly IReadOnlyList<GalateaCharacterRecipient> _recipients;

    private GalateaCharacterRecipientDirectory(
        IReadOnlyDictionary<string, GalateaCharacterRecipient> byCharacterName,
        IReadOnlyList<GalateaCharacterRecipient> recipients
    ) {
        _byCharacterName = byCharacterName;
        _recipients = recipients;
    }

    internal IReadOnlyList<GalateaCharacterRecipient> Recipients => _recipients;

    internal static GalateaCharacterRecipientDirectory Create(
        IReadOnlyList<(string CharacterId, GalateaCharacterName CharacterName)> characters
    ) {
        ArgumentNullException.ThrowIfNull(characters);
        var byCharacterName = new Dictionary<string, GalateaCharacterRecipient>(
            characters.Count,
            StringComparer.Ordinal
        );
        foreach ((string characterId, GalateaCharacterName characterName) in characters) {
            ArgumentNullException.ThrowIfNull(characterName);
            if (string.Equals(
                    characterName.Value,
                    CodexRecipient,
                    StringComparison.Ordinal)) {
                throw new InvalidOperationException(
                    "Galatea config characterName 'Codex' is reserved for "
                    + "the external delegate recipient."
                );
            }
            var recipient = new GalateaCharacterRecipient(
                characterName,
                characterId,
                SessionRepositoryId: null
            );
            if (!byCharacterName.TryAdd(characterName.Value, recipient)) {
                throw new InvalidOperationException(
                    "Galatea config contains duplicate characterName '"
                    + characterName.Value + "'."
                );
            }
        }

        GalateaCharacterRecipient[] recipients = byCharacterName.Values
            .OrderBy(static value => value.CharacterName.Value,
                StringComparer.Ordinal)
            .ToArray();
        return new GalateaCharacterRecipientDirectory(
            new System.Collections.ObjectModel.ReadOnlyDictionary<
                string, GalateaCharacterRecipient>(byCharacterName),
            Array.AsReadOnly(recipients)
        );
    }

    internal static GalateaCharacterRecipientDirectory Create(
        IReadOnlyList<GalateaCharacterConfig> characters
    ) {
        ArgumentNullException.ThrowIfNull(characters);
        var namedCharacters = new (string CharacterId, GalateaCharacterName CharacterName)[
            characters.Count
        ];
        for (int index = 0; index < characters.Count; index++) {
            GalateaCharacterConfig character = characters[index]
                ?? throw new ArgumentException(
                    "Character recipient characters must not contain null.",
                    nameof(characters)
                );
            namedCharacters[index] = (character.CharacterId, character.CharacterName);
        }
        GalateaCharacterRecipientDirectory namesOnly = Create(namedCharacters);
        var recipients = new List<GalateaCharacterRecipient>(characters.Count);
        foreach (GalateaCharacterRecipient recipient in namesOnly.Recipients) {
            GalateaCharacterConfig character = characters.Single(value => string.Equals(
                value.CharacterId,
                recipient.CharacterId,
                StringComparison.Ordinal));
            recipients.Add(recipient with {
                SessionRepositoryId =
                    GalateaDelegationSupervisor.CreateSessionRepositoryId(
                        character.SessionDir
                    )
            });
        }
        var byCharacterName = recipients.ToDictionary(
            static recipient => recipient.CharacterName.Value,
            StringComparer.Ordinal
        );
        return new GalateaCharacterRecipientDirectory(
            new System.Collections.ObjectModel.ReadOnlyDictionary<
                string, GalateaCharacterRecipient>(byCharacterName),
            Array.AsReadOnly(recipients.ToArray())
        );
    }

    internal bool TryGetExact(
        string characterName,
        out GalateaCharacterRecipient recipient
    ) {
        ArgumentNullException.ThrowIfNull(characterName);
        return _byCharacterName.TryGetValue(characterName, out recipient!);
    }

    internal IReadOnlyList<GalateaCharacterName> GetPeerNames(string characterId) {
        ArgumentException.ThrowIfNullOrWhiteSpace(characterId);
        return Array.AsReadOnly(_recipients
            .Where(recipient => !string.Equals(
                recipient.CharacterId,
                characterId,
                StringComparison.Ordinal))
            .Select(static recipient => recipient.CharacterName)
            .ToArray());
    }
}

internal sealed record GalateaCharacterRecipient(
    GalateaCharacterName CharacterName,
    string CharacterId,
    string? SessionRepositoryId
);

public sealed record GalateaRecapGridRuntimeConfig(
    GalateaRecapGridMaintenanceConfig Maintenance
);

public sealed record GalateaRecapGridMaintenanceConfig(
    string ConnectionId,
    int MaximumConcurrency,
    TimeSpan DispatchTimeout
);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record GalateaRecapGridFileConfig(
    GalateaRecapGridMaintenanceFileConfig Maintenance
);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record GalateaRecapGridMaintenanceFileConfig(
    string ConnectionId,
    int MaximumConcurrency,
    long DispatchTimeoutMilliseconds
);

/// <summary>
/// Shape of config.json: character accounts, their default Agent selections, and
/// server settings. Provider connection definitions remain in connections.json.
/// </summary>
internal sealed record GalateaRootFileConfig(
    [property: JsonPropertyName("v")] int Version,
    IReadOnlyList<GalateaCharacterFileConfig> Characters,
    IReadOnlyList<GalateaPlayerFileConfig> Players,
    GalateaRuntimeFileConfig Runtime
);

internal sealed record GalateaRuntimeFileConfig(
    IReadOnlyList<string>? ListenUrls = null,
    string? CallLogDir = null,
    bool MaintenanceMode = false,
    GalateaRecapGridFileConfig? RecapGrid = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyDictionary<string, int>? CompletionAttemptTimeoutSeconds = null
);

/// <summary>
/// Exact per-character shape read from config.json before paths are resolved and
/// character-context templates are materialized.
/// </summary>
internal sealed record GalateaCharacterFileConfig(
    [property: JsonPropertyName("id")] string CharacterId,
    [property: JsonPropertyName("name")] string CharacterName,
    string SessionDir,
    string DelegationStateDir,
    string CharacterMemoryStateDir,
    string HomeDir,
    GalateaSessionProvisioning SessionProvisioning,
    string DefaultConnectionId,
    IReadOnlyList<GalateaCharacterConnectionOption> ConnectionOptions,
    string CharacterContextTemplate = "",
    string? CharacterContextTemplateFile = null,
    int AutonomyIntervalMinutes = 0
);

internal sealed record GalateaPlayerFileConfig(
    [property: JsonPropertyName("id")] string PlayerId,
    string Name,
    string Password
);

public sealed record GalateaPlayerConfig(
    string PlayerId,
    GalateaPlayerName Name,
    string Password
);

public sealed record GalateaCharacterConfig(
    string CharacterId,
    GalateaCharacterName CharacterName,
    string SessionDir,
    string DelegationStateDir,
    string CharacterMemoryStateDir,
    string HomeDir,
    GalateaSessionProvisioning SessionProvisioning,
    SessionInputContent SystemPrompt,
    string DefaultConnectionId,
    IReadOnlyList<GalateaCharacterConnectionOption> ConnectionOptions,
    int AutonomyIntervalMinutes = 0
);

public sealed record GalateaCharacterConnectionOption(
    string ConnectionId,
    string Name,
    string Trigger
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
    internal const int MaximumAutonomyIntervalMinutes = 525_600;

    internal static void RequireValidPlayers(IReadOnlyList<GalateaPlayerConfig> players) {
        ArgumentNullException.ThrowIfNull(players);
        if (players.Count > GalateaStrictConfigReader.MaximumCharacterCount) {
            throw new InvalidOperationException("Galatea config contains too many Players.");
        }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (GalateaPlayerConfig player in players) {
            ArgumentNullException.ThrowIfNull(player);
            GalateaInputContentValidation.RequireText(player.PlayerId, 512, "player id", singleLine: true);
            ArgumentNullException.ThrowIfNull(player.Name);
            GalateaInputContentValidation.RequireText(player.Password,
                GalateaStrictConfigReader.MaximumConfigUtf8Bytes, "player password");
            if (!ids.Add(player.PlayerId)) { throw new InvalidOperationException("Duplicate Player id: " + player.PlayerId); }
        }
    }

    internal static IReadOnlyList<string> ReadAutonomyCharacterIds(
        IReadOnlyList<GalateaCharacterConfig> characters
    ) {
        ArgumentNullException.ThrowIfNull(characters);
        RequireValidAutonomyIntervals(characters);
        return Array.AsReadOnly(characters.Where(static character => character.AutonomyIntervalMinutes > 0)
            .Select(static character => character.CharacterId).ToArray());
    }

    internal static void RequireValidAutonomyIntervals(
        IReadOnlyList<GalateaCharacterConfig> characters
    ) {
        ArgumentNullException.ThrowIfNull(characters);
        for (int index = 0; index < characters.Count; index++) {
            GalateaCharacterConfig character = characters[index]
                ?? throw new InvalidOperationException(
                    $"Galatea config character[{index}] must not be null."
                );
            if (character.AutonomyIntervalMinutes is < 0
                or > MaximumAutonomyIntervalMinutes) {
                throw new InvalidOperationException(
                    $"Galatea config character '{character.CharacterId}' autonomyIntervalMinutes "
                    + $"must be an integer from 0 to {MaximumAutonomyIntervalMinutes}."
                );
            }
        }
    }

    internal static void RequireValidConnectionDefaults(
        IReadOnlyList<GalateaCharacterConfig> characters,
        CompletionConnectionCatalogConfig catalog
    ) {
        ArgumentNullException.ThrowIfNull(characters);
        ArgumentNullException.ThrowIfNull(catalog);
        var connectionIds = catalog.Connections
            .Select(static connection => connection.Id)
            .ToHashSet(StringComparer.Ordinal);
        for (int index = 0; index < characters.Count; index++) {
            GalateaCharacterConfig character = characters[index]
                ?? throw new InvalidOperationException(
                    $"Galatea config character[{index}] must not be null."
                );
            if (string.IsNullOrWhiteSpace(character.DefaultConnectionId)) {
                throw new InvalidOperationException(
                    $"Galatea config character '{character.CharacterId}' must have a "
                    + "non-empty defaultConnectionId."
                );
            }
            if (Encoding.UTF8.GetByteCount(character.DefaultConnectionId)
                > MaximumConnectionIdUtf8Bytes) {
                throw new InvalidOperationException(
                    $"Galatea config character '{character.CharacterId}' defaultConnectionId "
                    + "exceeds its UTF-8 byte bound."
                );
            }
            if (!connectionIds.Contains(character.DefaultConnectionId)) {
                throw new InvalidOperationException(
                    $"Galatea config character '{character.CharacterId}' defaultConnectionId "
                    + $"'{character.DefaultConnectionId}' does not exactly match a "
                    + "catalog connection id."
                );
            }
            if (character.ConnectionOptions is not { Count: > 0 and <= 256 }) {
                throw new InvalidOperationException(
                    $"Galatea config character '{character.CharacterId}' requires 1..256 connectionOptions."
                );
            }
            var selectable = new HashSet<string>(StringComparer.Ordinal);
            foreach (GalateaCharacterConnectionOption option in character.ConnectionOptions) {
                if (option is null || string.IsNullOrWhiteSpace(option.ConnectionId)
                    || Encoding.UTF8.GetByteCount(option.ConnectionId) > MaximumConnectionIdUtf8Bytes
                    || !connectionIds.Contains(option.ConnectionId)
                    || !selectable.Add(option.ConnectionId)) {
                    throw new InvalidOperationException(
                        $"Galatea config character '{character.CharacterId}' has an invalid or duplicate connectionOptions connectionId."
                    );
                }
                if (option.Name is null || option.Trigger is null
                    || Encoding.UTF8.GetByteCount(option.Name) > 4096
                    || Encoding.UTF8.GetByteCount(option.Trigger) > 4096) {
                    throw new InvalidOperationException(
                        $"Galatea config character '{character.CharacterId}' connectionOptions name/trigger must be bounded strings."
                    );
                }
            }
            if (!selectable.Contains(character.DefaultConnectionId)) {
                throw new InvalidOperationException(
                    $"Galatea config character '{character.CharacterId}' defaultConnectionId "
                    + $"'{character.DefaultConnectionId}' is not selectable."
                );
            }
        }
    }

    internal static void RequireValidStorageTopology(
        IReadOnlyList<GalateaCharacterConfig> characters,
        string? callLogDirectory
    ) {
        ArgumentNullException.ThrowIfNull(characters);
        StringComparer comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var sessionOwners = new Dictionary<
            string,
            (string CharacterId, string ConfiguredPath)
        >(comparer);
        var delegationOwners = new Dictionary<
            string,
            (string CharacterId, string ConfiguredPath)
        >(comparer);
        var characterMemoryOwners = new Dictionary<
            string,
            (string CharacterId, string ConfiguredPath)
        >(comparer);
        var normalizedUsers = new List<(
            string CharacterId,
            string SessionDirectory,
            string DelegationStateDirectory,
            string CharacterMemoryStateDirectory,
            string HomeDirectory
        )>(characters.Count);

        for (int index = 0; index < characters.Count; index++) {
            GalateaCharacterConfig character = characters[index]
                ?? throw new InvalidOperationException(
                    $"Galatea config character[{index}] must not be null."
                );
            if (character.CharacterName is null) {
                throw new InvalidOperationException(
                    $"Galatea config character '{character.CharacterId}' must have a "
                    + "validated characterName."
                );
            }
            if (character.SystemPrompt is null || (!character.SystemPrompt.IsStructured && string.IsNullOrWhiteSpace(character.SystemPrompt.TextValue))) {
                throw new InvalidOperationException(
                    $"Galatea config character '{character.CharacterId}' must have a "
                    + "non-empty finalized system prompt."
                );
            }
            GalateaSystemInstructionContent.Validate(character.SystemPrompt);
            if (character.SessionProvisioning is not (
                    GalateaSessionProvisioning.ExistingOnly
                    or GalateaSessionProvisioning.CreateIfMissing)) {
                throw new InvalidOperationException(
                    $"Galatea config character '{character.CharacterId}' has an unknown "
                    + "sessionProvisioning policy."
                );
            }
            if (string.IsNullOrWhiteSpace(character.SessionDir)) {
                throw new InvalidOperationException(
                    $"Galatea config character '{character.CharacterId}' must have a "
                    + "non-empty sessionDir."
                );
            }
            if (string.IsNullOrWhiteSpace(character.DelegationStateDir)) {
                throw new InvalidOperationException(
                    $"Galatea config character '{character.CharacterId}' must have a "
                    + "non-empty delegationStateDir."
                );
            }
            if (string.IsNullOrWhiteSpace(character.CharacterMemoryStateDir)) {
                throw new InvalidOperationException(
                    $"Galatea config character '{character.CharacterId}' must have a "
                    + "non-empty characterMemoryStateDir."
                );
            }
            string normalizedSession = RequireCanonicalAbsoluteDirectory(
                character.SessionDir,
                $"sessionDir for character '{character.CharacterId}'",
                comparison
            );
            string normalizedDelegation = RequireCanonicalAbsoluteDirectory(
                character.DelegationStateDir,
                $"delegationStateDir for character '{character.CharacterId}'",
                comparison
            );
            string normalizedCharacterMemory =
                RequireCanonicalAbsoluteDirectory(
                    character.CharacterMemoryStateDir,
                    $"characterMemoryStateDir for character '{character.CharacterId}'",
                    comparison
                );
            if (sessionOwners.TryGetValue(
                    normalizedSession,
                    out var existingSession)) {
                throw new InvalidOperationException(
                    "Galatea config characters "
                    + $"'{existingSession.CharacterId}' (sessionDir "
                    + $"'{existingSession.ConfiguredPath}') and '{character.CharacterId}' "
                    + $"(sessionDir '{character.SessionDir}') resolve to the "
                    + $"same lexical session path '{normalizedSession}'."
                );
            }
            sessionOwners.Add(
                normalizedSession,
                (character.CharacterId, character.SessionDir)
            );
            if (delegationOwners.TryGetValue(
                    normalizedDelegation,
                    out var existingDelegation)) {
                throw new InvalidOperationException(
                    "Galatea config characters "
                    + $"'{existingDelegation.CharacterId}' (delegationStateDir "
                    + $"'{existingDelegation.ConfiguredPath}') and "
                    + $"'{character.CharacterId}' (delegationStateDir "
                    + $"'{character.DelegationStateDir}') resolve to the same "
                    + $"lexical delegation state path "
                    + $"'{normalizedDelegation}'."
                );
            }
            delegationOwners.Add(
                normalizedDelegation,
                (character.CharacterId, character.DelegationStateDir)
            );
            if (characterMemoryOwners.TryGetValue(
                    normalizedCharacterMemory,
                    out var existingCharacterMemory)) {
                throw new InvalidOperationException(
                    "Galatea config characters "
                    + $"'{existingCharacterMemory.CharacterId}' "
                    + "(characterMemoryStateDir "
                    + $"'{existingCharacterMemory.ConfiguredPath}') and "
                    + $"'{character.CharacterId}' (characterMemoryStateDir "
                    + $"'{character.CharacterMemoryStateDir}') resolve to the "
                    + "same lexical character memory state path "
                    + $"'{normalizedCharacterMemory}'."
                );
            }
            characterMemoryOwners.Add(
                normalizedCharacterMemory,
                (character.CharacterId, character.CharacterMemoryStateDir)
            );
            string normalizedHome = GalateaDelegateConfigReader.RequireCanonicalDirectory(
                character.HomeDir,
                $"homeDir for character '{character.CharacterId}'"
            );
            normalizedUsers.Add((
                character.CharacterId,
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
                    $"delegationStateDir for character '{delegation.CharacterId}'",
                    session.SessionDirectory,
                    $"sessionDir for character '{session.CharacterId}'",
                    comparison
                );
            }
            for (int otherIndex = delegationIndex + 1;
                 otherIndex < normalizedUsers.Count;
                 otherIndex++) {
                var other = normalizedUsers[otherIndex];
                RequireDisjoint(
                    delegation.DelegationStateDirectory,
                    $"delegationStateDir for character '{delegation.CharacterId}'",
                    other.DelegationStateDirectory,
                    $"delegationStateDir for character '{other.CharacterId}'",
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
                    $"characterMemoryStateDir for character '{characterMemory.CharacterId}'",
                    session.SessionDirectory,
                    $"sessionDir for character '{session.CharacterId}'",
                    comparison
                );
            }
            foreach (var delegation in normalizedUsers) {
                RequireDisjoint(
                    characterMemory.CharacterMemoryStateDirectory,
                    $"characterMemoryStateDir for character '{characterMemory.CharacterId}'",
                    delegation.DelegationStateDirectory,
                    $"delegationStateDir for character '{delegation.CharacterId}'",
                    comparison
                );
            }
            for (int otherIndex = characterMemoryIndex + 1;
                 otherIndex < normalizedUsers.Count;
                 otherIndex++) {
                var other = normalizedUsers[otherIndex];
                RequireDisjoint(
                    characterMemory.CharacterMemoryStateDirectory,
                    $"characterMemoryStateDir for character '{characterMemory.CharacterId}'",
                    other.CharacterMemoryStateDirectory,
                    $"characterMemoryStateDir for character '{other.CharacterId}'",
                    comparison
                );
            }
        }

        for (int homeIndex = 0; homeIndex < normalizedUsers.Count; homeIndex++) {
            var home = normalizedUsers[homeIndex];
            foreach (var character in normalizedUsers) {
                RequireDisjoint(home.HomeDirectory, $"homeDir for character '{home.CharacterId}'",
                    character.SessionDirectory, $"sessionDir for character '{character.CharacterId}'", comparison);
                RequireDisjoint(home.HomeDirectory, $"homeDir for character '{home.CharacterId}'",
                    character.DelegationStateDirectory, $"delegationStateDir for character '{character.CharacterId}'", comparison);
                RequireDisjoint(home.HomeDirectory, $"homeDir for character '{home.CharacterId}'",
                    character.CharacterMemoryStateDirectory, $"characterMemoryStateDir for character '{character.CharacterId}'", comparison);
            }
            for (int otherIndex = homeIndex + 1; otherIndex < normalizedUsers.Count; otherIndex++) {
                var other = normalizedUsers[otherIndex];
                RequireDisjoint(home.HomeDirectory, $"homeDir for character '{home.CharacterId}'",
                    other.HomeDirectory, $"homeDir for character '{other.CharacterId}'", comparison);
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
        foreach (var character in normalizedUsers) {
            RequireDisjoint(normalizedCallLogs, "callLogDir", character.HomeDirectory,
                $"homeDir for character '{character.CharacterId}'", comparison);
            RequireDisjoint(
                normalizedCallLogs,
                "callLogDir",
                character.SessionDirectory,
                $"sessionDir for character '{character.CharacterId}'",
                comparison
            );
            RequireDisjoint(
                normalizedCallLogs,
                "callLogDir",
                character.DelegationStateDirectory,
                $"delegationStateDir for character '{character.CharacterId}'",
                comparison
            );
            RequireDisjoint(
                normalizedCallLogs,
                "callLogDir",
                character.CharacterMemoryStateDirectory,
                $"characterMemoryStateDir for character '{character.CharacterId}'",
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
    string PlayerId,
    string Name,
    bool MaintenanceMode
);

public sealed record RecentTurnDto(
    string UserText,
    AssistantMessageDto? Assistant,
    string? EndReason = null
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
    string ThroughRowId
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
    [property: JsonPropertyName("diagnosticConnectionId")]
    string? DiagnosticConnectionId = null
);

internal sealed record InboundMailboxRequest(
    [property: JsonPropertyName("from")] string From,
    [property: JsonPropertyName("body")] string Body,
    [property: JsonPropertyName("subject")] string? Subject = null,
    [property: JsonPropertyName("diagnosticConnectionId")]
    string? DiagnosticConnectionId = null
);

internal sealed record ReadyReplyTurnRequest;

internal sealed record GalateaAgentStatusDto(
    string State,
    string? EffectiveConnectionId,
    long? NextActivationAtUnixTimeMilliseconds,
    long? LastActivationAtUnixTimeMilliseconds,
    string? Code,
    ApiErrorDto? AdmissionFailure = null,
    string? DefaultConnectionId = null,
    string? RuntimeConnectionOverrideId = null
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
    string? RecoveryHead = null
);

internal sealed record ResumeTurnRequest(
    [property: JsonPropertyName("expectedHead")]
    string ExpectedHead,
    [property: JsonPropertyName("diagnosticConnectionId")]
    string? DiagnosticConnectionId = null
);

internal sealed record StopPendingTurnRequest(string ExpectedHead);
