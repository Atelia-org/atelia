using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Atelia.Completion;
using Atelia.Diagnostics;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.Galatea.Prompts;
using Atelia.Galatea.RecapGrid;
using Atelia.Galatea.Server.CharacterMemory;
using Atelia.Galatea.Server.Mailbox;
using Atelia.SessionJournal;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid.Control;
using Atelia.SessionJournal.RecapGrid.Hosting;
using Atelia.SessionJournal.RecapGrid.Online;

namespace Atelia.Galatea.Server;

internal abstract record GalateaReadyReplyTurnStartResult {
    private GalateaReadyReplyTurnStartResult() { }

    internal sealed record Empty : GalateaReadyReplyTurnStartResult;

    internal sealed record Started(GalateaLiveTurn Turn)
        : GalateaReadyReplyTurnStartResult;
}

public sealed partial class GalateaHostService : IAsyncDisposable {
    internal const int RecentTurnLimit = 6;
    internal const int MaximumRecentResponseUtf8Bytes = 4 * 1024 * 1024;
    internal const int MaximumRecapCadenceProgressResponseUtf8Bytes =
        16 * 1024;
    internal const int MaximumPoppedUserTextUtf8Bytes = 256 * 1024;
    internal const int MaximumPopReceiptUtf8Bytes = 2 * 1024 * 1024;
    private readonly GalateaInputPreprocessor _inputPreprocessor;
    private readonly IReadOnlyDictionary<string, IOutboundMailExtractor>
        _outboundMailExtractors;
    private readonly IReadOnlyDictionary<string, ICharacterNoteExtractor>
        _characterNoteExtractors;
    private readonly IReadOnlyDictionary<string, ICharacterConnectionStateExtractor>
        _characterConnectionStateExtractors;
    private readonly IReadOnlyDictionary<string,
        ICharacterNoteDerivedInfoEnricher>
        _characterNoteDerivedInfoEnrichers;
    private readonly bool _characterNoteBindingEnabled;
    private readonly bool _allowMissingCharacterNoteDerivedInfoEnricher;
    private readonly IReadOnlyDictionary<string,
        GalateaRecapGridDefaultPolicy> _defaultPolicies;
    private readonly bool _maintenanceMode;
    private readonly GalateaRecapGridComposition _recapGrid;
    private readonly GalateaCompletionOwner? _completionOwner;
    private readonly GalateaDelegationSupervisor _delegationSupervisor;
    private readonly GalateaCharacterRecipientDirectory?
        _characterRecipientDirectory;
    private readonly GalateaPlayerTurnRecallProviderFactory?
        _playerTurnRecallProviderFactory;
    private readonly TimeProvider _timeProvider;
    private readonly IReadOnlyDictionary<string, int> _completionAttemptTimeoutSeconds;
    internal GalateaDisposeTestHooks? DisposeHooksForTest { get; set; }
    internal GalateaSessionProvisioningTestHooks?
        SessionProvisioningHooksForTest { get; set; }
    internal TimeSpan? CharacterNoteExtractionDeadlineForTest { get; set; }
    internal Func<CharacterSessionHost, Task>? SessionAttachedForTest { get; set; }
    internal Func<string, SessionJournalEngine>? OpenSessionForTest { get; set; }
    internal Func<double>? ColdRecoveryJitterSampleForTest { get; set; }

    internal async Task DelayColdRecoveryAsync(CancellationToken callerToken) {
        double sample = ColdRecoveryJitterSampleForTest?.Invoke() ?? Random.Shared.NextDouble();
        if (!double.IsFinite(sample) || sample is < 0 or > 1) {
            throw new InvalidOperationException("Cold recovery jitter sample must be in [0,1].");
        }
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(callerToken, _admissionStopping.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(250 * sample), _timeProvider, lifetime.Token).ConfigureAwait(false);
        lifetime.Token.ThrowIfCancellationRequested();
    }
    internal Func<Task>? BeforeDelegationAttachForTest { get; set; }
    internal TimeSpan? CharacterNoteDerivedInfoDeadlineForTest { get; set; }
    internal Action<string>? CharacterNoteDiagnosticSinkForTest { get; set; }
    internal Action<string>? MemoRecallDiagnosticSinkForTest { get; set; }
    private readonly ConcurrentDictionary<string, Lazy<Task<CharacterSessionHost>>> _sessions = new(StringComparer.Ordinal);
    private readonly object _lifecycleGate = new();
    private readonly CancellationTokenSource _admissionStopping = new();
    private bool _stopping;
    private GalateaAcceptedTurnRunner? _turnRunner;
    private GalateaCharacterMailRelay? _characterMailRelay;
    private Task? _disposeTask;
    private readonly IReadOnlyDictionary<string, GalateaCharacterConfig> _characters;
    private readonly IReadOnlyDictionary<string, GalateaPlayerConfig> _players;
    private readonly IReadOnlyDictionary<string, CompletionConnectionConfig>
        _connectionCatalog;
    private readonly ConcurrentDictionary<string, RuntimeConnectionSelection>
        _runtimeConnectionOverrides = new(StringComparer.Ordinal);
    private readonly RecapGridControlAdmission? _sessionBootstrapAdmission;
    internal IReadOnlyList<string> AutonomyCharacterIds { get; }
    internal IReadOnlyList<string> CharacterIds { get; }
    public GalateaHostService(
        GalateaConfig config,
        ICompletionClientFactory completionClientFactory,
        IGalateaUserMessageNormalizerFactory userMessageNormalizerFactory
    ) : this(
        config,
        CreateProductionComponents(
            config,
            completionClientFactory,
            userMessageNormalizerFactory
        )
    ) { }

    internal GalateaHostService(
        GalateaConfig config,
        ICompletionClientFactory completionClientFactory,
        IGalateaUserMessageNormalizer userMessageNormalizer
    ) : this(
        config,
        CreateProductionComponents(
            config,
            completionClientFactory,
            new FixedGalateaUserMessageNormalizerFactory(
                userMessageNormalizer
            )
        )
    ) { }

    internal GalateaHostService(
        GalateaConfig config,
        ICompletionClientFactory completionClientFactory,
        IGalateaUserMessageNormalizerFactory userMessageNormalizerFactory,
        IGalateaDurableDelegateTransport? delegateTransport,
        GalateaPlayerTurnRecallProviderFactory?
            playerTurnRecallProviderFactory,
        TimeProvider? timeProvider = null
    ) : this(
        config,
        CreateProductionComponents(
            config,
            completionClientFactory,
            userMessageNormalizerFactory,
            delegateTransport,
            playerTurnRecallProviderFactory,
            timeProvider
        ),
        timeProvider
    ) { }

    private GalateaHostService(
        GalateaConfig config,
        GalateaProductionComponents components,
        TimeProvider? timeProvider = null
    ) {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(components);
        _sessionBootstrapAdmission = components.SessionBootstrapAdmission;
        _completionOwner = components.Owner;
        _delegationSupervisor = components.DelegationSupervisor;
        _characterRecipientDirectory = config.CharacterRecipientDirectory;
        _playerTurnRecallProviderFactory =
            components.PlayerTurnRecallProviderFactory;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _completionAttemptTimeoutSeconds = ValidateAttemptTimeouts(config);
        _recapGrid = components.RecapGrid;
        _inputPreprocessor = components.InputPreprocessor;
        _outboundMailExtractors = components.OutboundMailExtractors;
        _characterNoteExtractors = components.CharacterNoteExtractors;
        _characterConnectionStateExtractors = components.CharacterConnectionStateExtractors;
        _characterNoteDerivedInfoEnrichers =
            components.CharacterNoteDerivedInfoEnrichers;
        _characterNoteBindingEnabled =
            components.CharacterNoteBindingEnabled;
        _allowMissingCharacterNoteDerivedInfoEnricher = false;
        _defaultPolicies = components.DefaultPolicies;
        _maintenanceMode = components.MaintenanceMode;
        _characters = components.Characters;
        GalateaConfigValidation.RequireValidPlayers(config.Players);
        _players = config.Players.ToDictionary(static player => player.PlayerId, StringComparer.Ordinal);
        _connectionCatalog = components.ConnectionCatalog;
        AutonomyCharacterIds = components.AutonomyCharacterIds;
        CharacterIds = _characters.Keys.ToArray();
    }

    internal GalateaHostService(
        GalateaConfig config,
        IGalateaUserMessageNormalizer userMessageNormalizer,
        GalateaRecapGridComposition recapGrid,
        IReadOnlyDictionary<string, GalateaRecapGridDefaultPolicy>?
            defaultPolicies = null,
        TimeProvider? timeProvider = null,
        GalateaPlayerTurnRecallProviderFactory?
            playerTurnRecallProviderFactory = null,
        IReadOnlyDictionary<string, ICharacterNoteDerivedInfoEnricher>?
            characterNoteDerivedInfoEnrichers = null,
        IReadOnlyDictionary<string, ICharacterConnectionStateExtractor>?
            characterConnectionStateExtractors = null
    ) {
        ArgumentNullException.ThrowIfNull(recapGrid);
        ArgumentNullException.ThrowIfNull(userMessageNormalizer);
        AutonomyCharacterIds = GalateaConfigValidation.ReadAutonomyCharacterIds(
            config.Characters
        );
        _ = GalateaDelegateConfigReader.Validate(config.Delegates);
        GalateaConfigValidation.RequireValidStorageTopology(
            config.Characters,
            config.CallLogDir
        );
        CompletionConnectionCatalogConfig normalized =
            CompletionConnectionConfigLoader.NormalizeAndValidateCatalog(new(
                config.Connections,
                SelectableConnectionIds: null,
                new Dictionary<string, string?>(StringComparer.Ordinal) {
                    [GalateaCompletionOwner.InputNormalizerBindingKey] =
                        config.InputNormalizerConnectionId,
                    [GalateaCompletionOwner.OutboundMailExtractorBindingKey] =
                        config.OutboundMailExtractorConnectionId,
                    [GalateaCompletionOwner.CharacterNoteExtractorBindingKey] =
                        config.CharacterNoteExtractorConnectionId,
                    [GalateaCompletionOwner.MemoRecallBindingKey] =
                        config.MemoRecallConnectionId,
                    [GalateaCompletionOwner.CharacterConnectionStateExtractorBindingKey] =
                        config.CharacterConnectionStateExtractorConnectionId,
                }
            ));
        GalateaCompletionOwner.ValidateGalateaRouting(normalized);
        GalateaConfigValidation.RequireValidConnectionDefaults(
            config.Characters,
            normalized
        );
        _recapGrid = recapGrid;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _completionAttemptTimeoutSeconds = ValidateAttemptTimeouts(config);
        _playerTurnRecallProviderFactory = playerTurnRecallProviderFactory;
        _completionOwner = null;
        _inputPreprocessor = new GalateaInputPreprocessor(
            userMessageNormalizer
        );
        _maintenanceMode = config.MaintenanceMode;
        _characterRecipientDirectory = config.CharacterRecipientDirectory;
        _characterNoteBindingEnabled =
            config.CharacterNoteExtractorConnectionId is not null;
        _allowMissingCharacterNoteDerivedInfoEnricher = true;
        _characters = config.Characters.ToDictionary(
            static value => value.CharacterId,
            StringComparer.Ordinal
        );
        CharacterIds = _characters.Keys.ToArray();
        _characterConnectionStateExtractors = characterConnectionStateExtractors
            ?? new Dictionary<string, ICharacterConnectionStateExtractor>(StringComparer.Ordinal);
        GalateaConfigValidation.RequireValidPlayers(config.Players);
        _players = config.Players.ToDictionary(static player => player.PlayerId, StringComparer.Ordinal);
        _outboundMailExtractors = _characters.Keys.ToDictionary(
            static characterId => characterId,
            static _ => (IOutboundMailExtractor)
                DisabledOutboundMailExtractor.Instance,
            StringComparer.Ordinal
        );
        _characterNoteExtractors = _characters.Keys.ToDictionary(
            static characterId => characterId,
            static _ => (ICharacterNoteExtractor)
                DisabledCharacterNoteExtractor.Instance,
            StringComparer.Ordinal
        );
        _characterNoteDerivedInfoEnrichers =
            characterNoteDerivedInfoEnrichers
            ?? new Dictionary<string, ICharacterNoteDerivedInfoEnricher>(
                StringComparer.Ordinal
            );
        _defaultPolicies = defaultPolicies
            ?? CreateDefaultPolicies(_characters);
        _connectionCatalog = SelectCharacterConnectionCatalog(
            config.Characters,
            normalized.Connections
        );
        _sessionBootstrapAdmission = config.RecapGrid is { } configured
            ? ResolveSessionBootstrapAdmission(configured)
            : null;
        // The supervisor may immediately pulse an existing durable outbox,
        // so every other fallible composition step must precede it.
        _delegationSupervisor = new GalateaDelegationSupervisor(config);
    }

    private static GalateaProductionComponents CreateProductionComponents(
        GalateaConfig config,
        ICompletionClientFactory completionClientFactory,
        IGalateaUserMessageNormalizerFactory normalizerFactory,
        IGalateaDurableDelegateTransport? delegateTransportOverride = null,
        GalateaPlayerTurnRecallProviderFactory?
            playerTurnRecallProviderFactory = null,
        TimeProvider? timeProvider = null
    ) {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(completionClientFactory);
        ArgumentNullException.ThrowIfNull(normalizerFactory);
        GalateaConfigValidation.RequireValidPlayers(config.Players);
        IReadOnlyList<string> autonomyCharacterIds =
            GalateaConfigValidation.ReadAutonomyCharacterIds(
                config.Characters
            );
        GalateaConfigValidation.RequireValidStorageTopology(
            config.Characters,
            config.CallLogDir
        );

        GalateaCompletionOwner? owner = null;
        try {
            owner = new GalateaCompletionOwner(
                config,
                completionClientFactory,
                timeProvider
            );
            IGalateaUserMessageNormalizer normalizer =
                normalizerFactory.Create(
                    owner.InputNormalizerConnection,
                    owner.GetInputNormalizerClient
                ) ?? throw new InvalidOperationException(
                    "Galatea input normalizer factory returned null."
                );
            GalateaRecapGridRuntimeConfig recapGridConfig = config.RecapGrid
                ?? throw new InvalidOperationException(
                    "Galatea requires strict RecapGrid runtime configuration."
                );
            RecapGridControlAdmission sessionBootstrapAdmission =
                ResolveSessionBootstrapAdmission(recapGridConfig);
            var inputPreprocessor = new GalateaInputPreprocessor(normalizer);
            IReadOnlyDictionary<string, GalateaCharacterConfig> characters =
                config.Characters.ToDictionary(
                    static value => value.CharacterId,
                    StringComparer.Ordinal
                );
            IReadOnlyDictionary<string, IOutboundMailExtractor>
                outboundMailExtractors = CreateOutboundMailExtractors(
                    characters,
                    owner.OutboundMailExtractorConnection,
                    owner.GetOutboundMailExtractorClient
                );
            IReadOnlyDictionary<string, ICharacterNoteExtractor>
                characterNoteExtractors = CreateCharacterNoteExtractors(
                    characters,
                    owner.CharacterNoteExtractorConnection,
                    owner.GetCharacterNoteExtractorClient
                );
            IReadOnlyDictionary<string,
                ICharacterNoteDerivedInfoEnricher>
                characterNoteDerivedInfoEnrichers =
                    CreateCharacterNoteDerivedInfoEnrichers(
                        characters,
                        owner.CharacterNoteExtractorConnection,
                        owner.GetCharacterNoteExtractorClient
                    );
            IReadOnlyDictionary<string, GalateaRecapGridDefaultPolicy>
                defaultPolicies = CreateDefaultPolicies(characters);
            IReadOnlyDictionary<string, ICharacterConnectionStateExtractor> connectionStateExtractors =
                characters.ToDictionary(
                    entry => entry.Key,
                    entry => config.MaintenanceMode
                        || owner.CharacterConnectionStateExtractorConnection is null
                        || !entry.Value.ConnectionOptions.Any(option => !string.IsNullOrWhiteSpace(option.Trigger))
                        ? (ICharacterConnectionStateExtractor)DisabledCharacterConnectionStateExtractor.Instance
                        : new CharacterConnectionStateExtractor(entry.Value.CharacterName,
                            entry.Value.ConnectionOptions, owner.CharacterConnectionStateExtractorConnection,
                            owner.GetCharacterConnectionStateExtractorClient),
                    StringComparer.Ordinal);
            IReadOnlyDictionary<string, CompletionConnectionConfig>
                connectionCatalog = SelectCharacterConnectionCatalog(
                    config.Characters,
                    owner.Connections
                );
            GalateaPlayerTurnRecallProviderFactory?
                configuredRecallFactory =
                    CreateMemoRecallProviderFactory(owner);
            GalateaPlayerTurnRecallProviderFactory? recallFactory =
                config.MaintenanceMode
                    ? null
                    : playerTurnRecallProviderFactory
                        ?? configuredRecallFactory;

            // No fallible host preflight may remain after this point: an
            // existing durable outbox can be pulsed by construction.
            var delegationSupervisor = new GalateaDelegationSupervisor(
                config,
                delegateTransportOverride
            );
            return new GalateaProductionComponents(
                owner,
                owner.RecapGrid,
                inputPreprocessor,
                outboundMailExtractors,
                characterNoteExtractors,
                connectionStateExtractors,
                characterNoteDerivedInfoEnrichers,
                owner.CharacterNoteExtractorConnection is not null,
                defaultPolicies,
                delegationSupervisor,
                recallFactory,
                sessionBootstrapAdmission,
                config.MaintenanceMode,
                characters,
                connectionCatalog,
                autonomyCharacterIds
            );
        }
        catch (Exception exception) {
            if (owner is not null) {
                DisposeOwnerAfterConstructionFailure(owner, exception);
            }
            throw;
        }
    }

    private static void DisposeOwnerAfterConstructionFailure(
        GalateaCompletionOwner owner,
        Exception original
    ) {
        try {
            owner.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception cleanup) when (
            GalateaExceptionClassifier.IsNonFatal(cleanup)) {
            if (!GalateaExceptionClassifier.IsNonFatal(original)) {
                ExceptionDispatchInfo.Capture(original).Throw();
            }
            throw new AggregateException(
                "Galatea construction and cleanup both failed.",
                original,
                cleanup
            );
        }
        ExceptionDispatchInfo.Capture(original).Throw();
    }

    private static GalateaPlayerTurnRecallProviderFactory?
        CreateMemoRecallProviderFactory(GalateaCompletionOwner owner) {
        ArgumentNullException.ThrowIfNull(owner);
        CompletionConnectionConfig? connection = owner.MemoRecallConnection;
        if (connection is null) { return null; }

        return (_, characterMemory) =>
            new GalateaDefaultMemoPodRecallProvider(
                characterMemory
                    ?? throw new InvalidOperationException(
                        "Enabled Memo recall requires an attached Character Memory session."
                    ),
                connection,
                owner.GetMemoRecallClient
            );
    }

    private sealed record GalateaProductionComponents(
        GalateaCompletionOwner Owner,
        GalateaRecapGridComposition RecapGrid,
        GalateaInputPreprocessor InputPreprocessor,
        IReadOnlyDictionary<string, IOutboundMailExtractor>
            OutboundMailExtractors,
        IReadOnlyDictionary<string, ICharacterNoteExtractor>
            CharacterNoteExtractors,
        IReadOnlyDictionary<string, ICharacterConnectionStateExtractor>
            CharacterConnectionStateExtractors,
        IReadOnlyDictionary<string, ICharacterNoteDerivedInfoEnricher>
            CharacterNoteDerivedInfoEnrichers,
        bool CharacterNoteBindingEnabled,
        IReadOnlyDictionary<string, GalateaRecapGridDefaultPolicy>
            DefaultPolicies,
        GalateaDelegationSupervisor DelegationSupervisor,
        GalateaPlayerTurnRecallProviderFactory?
            PlayerTurnRecallProviderFactory,
        RecapGridControlAdmission SessionBootstrapAdmission,
        bool MaintenanceMode,
        IReadOnlyDictionary<string, GalateaCharacterConfig> Characters,
        IReadOnlyDictionary<string, CompletionConnectionConfig>
            ConnectionCatalog,
        IReadOnlyList<string> AutonomyCharacterIds
    );

    private static IReadOnlyDictionary<string, CompletionConnectionConfig>
        SelectCharacterConnectionCatalog(
            IReadOnlyList<GalateaCharacterConfig> characters,
            IReadOnlyList<CompletionConnectionConfig> connections
        ) {
        IReadOnlyDictionary<string, CompletionConnectionConfig> fullCatalog =
            connections.ToDictionary(static value => value.Id,
                StringComparer.Ordinal);
        return characters
            .SelectMany(static character => character.ConnectionOptions)
            .Select(static option => option.ConnectionId)
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(static id => id, id => fullCatalog[id],
                StringComparer.Ordinal);
    }

    internal static IReadOnlyDictionary<string, IOutboundMailExtractor>
        CreateOutboundMailExtractors(
        IReadOnlyDictionary<string, GalateaCharacterConfig> characters,
        CompletionConnectionConfig? connection,
        Func<ICompletionClient> getClient
    ) {
        ArgumentNullException.ThrowIfNull(characters);
        ArgumentNullException.ThrowIfNull(getClient);
        return characters.ToDictionary(
            static pair => pair.Key,
            pair => connection is null
                ? (IOutboundMailExtractor)
                    DisabledOutboundMailExtractor.Instance
                : new OutboundMailExtractor(
                    pair.Value.CharacterName,
                    connection,
                    getClient
                ),
            StringComparer.Ordinal
        );
    }

    internal static IReadOnlyDictionary<string, ICharacterNoteExtractor>
        CreateCharacterNoteExtractors(
        IReadOnlyDictionary<string, GalateaCharacterConfig> characters,
        CompletionConnectionConfig? connection,
        Func<ICompletionClient> getClient
    ) {
        ArgumentNullException.ThrowIfNull(characters);
        ArgumentNullException.ThrowIfNull(getClient);
        return characters.ToDictionary(
            static pair => pair.Key,
            pair => connection is null
                ? (ICharacterNoteExtractor)
                    DisabledCharacterNoteExtractor.Instance
                : new CharacterNoteExtractor(
                    pair.Value.CharacterName,
                    connection,
                    getClient
                ),
            StringComparer.Ordinal
        );
    }

    internal static IReadOnlyDictionary<string,
        ICharacterNoteDerivedInfoEnricher>
        CreateCharacterNoteDerivedInfoEnrichers(
        IReadOnlyDictionary<string, GalateaCharacterConfig> characters,
        CompletionConnectionConfig? connection,
        Func<ICompletionClient> getClient
    ) {
        ArgumentNullException.ThrowIfNull(characters);
        ArgumentNullException.ThrowIfNull(getClient);
        if (connection is null) {
            return new Dictionary<string,
                ICharacterNoteDerivedInfoEnricher>(StringComparer.Ordinal);
        }
        return characters.ToDictionary(
            static pair => pair.Key,
            pair => (ICharacterNoteDerivedInfoEnricher)
                new CharacterNoteDerivedInfoEnricher(
                    pair.Value.CharacterName,
                    connection,
                    getClient
                ),
            StringComparer.Ordinal
        );
    }

    internal static IReadOnlyDictionary<string,
        GalateaRecapGridDefaultPolicy> CreateDefaultPolicies(
        IReadOnlyDictionary<string, GalateaCharacterConfig> characters
    ) {
        ArgumentNullException.ThrowIfNull(characters);
        return characters.ToDictionary(
            static pair => pair.Key,
            static pair => GalateaRecapGridDefaultPolicy.ForCharacter(
                pair.Value.CharacterName
            ),
            StringComparer.Ordinal
        );
    }

    private sealed class FixedGalateaUserMessageNormalizerFactory(
        IGalateaUserMessageNormalizer normalizer
    ) : IGalateaUserMessageNormalizerFactory {
        private readonly IGalateaUserMessageNormalizer _normalizer =
            normalizer ?? throw new ArgumentNullException(nameof(normalizer));

        public IGalateaUserMessageNormalizer Create(
            CompletionConnectionConfig? connection,
            Func<ICompletionClient> getClient
        ) {
            _ = connection;
            ArgumentNullException.ThrowIfNull(getClient);
            return _normalizer;
        }
    }

    public bool TryGetCharacter(string characterId, out GalateaCharacterConfig character)
        => _characters.TryGetValue(characterId, out character!);

    public bool TryGetPlayer(string playerId, out GalateaPlayerConfig player)
        => _players.TryGetValue(playerId, out player!);

    internal bool MaintenanceMode => _maintenanceMode;
    internal TimeProvider TimeProvider => _timeProvider;
    internal bool IsStopping { get { lock (_lifecycleGate) { return _stopping; } } }
    internal void RequireRunning() {
        if (IsStopping) { throw new OperationCanceledException("The Galatea host is stopping."); }
    }

    internal void RegisterTurnRunner(GalateaAcceptedTurnRunner runner) {
        bool stopping;
        lock (_lifecycleGate) {
            if (_turnRunner is not null && !ReferenceEquals(_turnRunner, runner)) {
                throw new InvalidOperationException("Only one accepted-turn runner may own a host.");
            }
            _turnRunner = runner;
            stopping = _stopping;
        }
        if (stopping) { runner.BeginShutdown(); }
    }

    internal void RegisterCharacterMailRelay(GalateaCharacterMailRelay relay) {
        ArgumentNullException.ThrowIfNull(relay);
        bool stopping;
        lock (_lifecycleGate) {
            if (_characterMailRelay is not null
                && !ReferenceEquals(_characterMailRelay, relay)) {
                throw new InvalidOperationException(
                    "Only one character-mail relay may own a host.");
            }
            _characterMailRelay = relay;
            stopping = _stopping;
        }
        if (stopping) { relay.BeginShutdown(); }
    }

    internal IReadOnlyList<GalateaCharacterRecipient> CharacterRecipients =>
        _characterRecipientDirectory?.Recipients
            ?? Array.Empty<GalateaCharacterRecipient>();

    internal bool IsCurrentInternalMailTarget(
        GalateaInternalMailSourceOutbox source
    ) => _characterRecipientDirectory is { } directory
        && IsCurrentInternalMailTarget(directory, source);

    internal static bool IsCurrentInternalMailTarget(
        GalateaCharacterRecipientDirectory directory,
        GalateaInternalMailSourceOutbox source
    ) {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(source);
        GalateaInternalMailOutboxSnapshot outbox = source.Outbox;
        GalateaCharacterRecipient? target = directory.Recipients
            .SingleOrDefault(recipient =>
                string.Equals(recipient.CharacterId, outbox.TargetCharacterId,
                    StringComparison.Ordinal));
        if (target is null
            || !string.Equals(target.SessionRepositoryId,
                outbox.TargetSessionRepositoryId,
                StringComparison.Ordinal)) {
            return false;
        }
        GalateaOutboundMailSnapshot? mail = source.Store.ReadSnapshot().Mails
            .SingleOrDefault(value => string.Equals(value.DispatchId,
                outbox.DispatchId, StringComparison.Ordinal));
        return mail is not null && string.Equals(mail.Recipient,
            target.CharacterName.Value, StringComparison.Ordinal);
    }

    internal CharacterSessionHost? ReadAttachedSession(string characterId) {
        if (!_sessions.TryGetValue(characterId, out var lazy) || !lazy.IsValueCreated) {
            return null;
        }
        Task<CharacterSessionHost> task = lazy.Value;
        return task.IsCompletedSuccessfully ? task.Result : null;
    }

    public IReadOnlyList<GalateaConnectionInfoDto> ConnectionsFor(string characterId) {
        GalateaCharacterConfig character = _characters[characterId];
        return Array.AsReadOnly(character.ConnectionOptions.Select(option =>
            new GalateaConnectionInfoDto(
                option.ConnectionId,
                _connectionCatalog[option.ConnectionId].ModelId
            )).ToArray());
    }

    internal GalateaDelegationSupervisor DelegationSupervisor =>
        _delegationSupervisor;

    /// <summary>
    /// Reads mailbox progress without GetSessionAsync, TurnLock, extraction,
    /// transport, provider work, or reply-lease admission. This separation
    /// from the automatic admission is intentional. Successful polls
    /// intentionally produce no per-request diagnostic log; failures are
    /// logged at the supervisor boundary without recreating heartbeat noise.
    /// </summary>
    internal GalateaMailboxStatusDto ReadMailboxStatus(string characterId) =>
        GalateaMailboxStatusDto.FromProjection(
            _delegationSupervisor.ReadMailboxStatus(characterId)
        );

    internal bool TryGetRecoveryConnection(
        GalateaCharacterConfig character,
        string? requestedConnectionId,
        out CompletionConnectionConfig connection
    ) {
        ArgumentNullException.ThrowIfNull(character);
        string id = requestedConnectionId is null
            ? character.DefaultConnectionId
            : requestedConnectionId;
        connection = null!;
        return IsSelectableFor(character, id)
            && _connectionCatalog.TryGetValue(id, out connection!);
    }

    // Only fresh turns consult this process-local Character choice. Recovery
    // retains the governing setup or the Prepared target from SessionJournal.
    internal bool TryGetFreshConnection(
        GalateaCharacterConfig character,
        string? diagnosticConnectionId,
        out CompletionConnectionConfig connection
    ) {
        ArgumentNullException.ThrowIfNull(character);
        string id = diagnosticConnectionId
            ?? ReadRuntimeConnectionOverride(character.CharacterId)
            ?? character.DefaultConnectionId;
        connection = null!;
        return IsSelectableFor(character, id)
            && _connectionCatalog.TryGetValue(id, out connection!);
    }

    internal string? ReadRuntimeConnectionOverride(string characterId) =>
        _runtimeConnectionOverrides.GetValueOrDefault(characterId)?.ConnectionId;

    internal void SetRuntimeConnectionOverride(
        string characterId,
        string? connectionId
    ) {
        if (!_characters.ContainsKey(characterId)) {
            throw new ArgumentException("Unknown character.", nameof(characterId));
        }
        if (connectionId is null) {
            _runtimeConnectionOverrides.TryRemove(characterId, out _);
            return;
        }
        if (GalateaHttpV1.ValidateConnectionId(connectionId) is not null
            || !IsSelectableFor(_characters[characterId], connectionId)) {
            throw new ArgumentException(
                "Runtime connection override must exactly match a selectable connection.",
                nameof(connectionId));
        }
        _runtimeConnectionOverrides[characterId] = new RuntimeConnectionSelection(connectionId, null);
    }

    private static bool IsSelectableFor(
        GalateaCharacterConfig character,
        string connectionId
    ) => character.ConnectionOptions.Any(option =>
        string.Equals(option.ConnectionId, connectionId, StringComparison.Ordinal));

    internal GalateaAgentStatusDto WithConnectionSelection(
        string characterId,
        GalateaAgentStatusDto status
    ) {
        if (!_characters.TryGetValue(characterId, out GalateaCharacterConfig? character)) {
            return status;
        }
        string? runtimeOverride = ReadRuntimeConnectionOverride(characterId);
        return status with {
            DefaultConnectionId = character.DefaultConnectionId,
            RuntimeConnectionOverrideId = runtimeOverride,
            EffectiveConnectionId = runtimeOverride ?? character.DefaultConnectionId
        };
    }

    public bool ValidatePassword(GalateaPlayerConfig player, string password) {
        ArgumentNullException.ThrowIfNull(player);
        password ??= string.Empty;

        byte[] left = SHA256.HashData(Encoding.UTF8.GetBytes(password));
        byte[] right = SHA256.HashData(Encoding.UTF8.GetBytes(player.Password ?? string.Empty));
        return CryptographicOperations.FixedTimeEquals(left, right);
    }

    public async Task<CharacterSessionHost> GetSessionAsync(string characterId, CancellationToken ct) {
        var character = _characters.GetValueOrDefault(characterId)
            ?? throw new InvalidOperationException($"Unknown character '{characterId}'.");

        Lazy<Task<CharacterSessionHost>> lazy;
        lock (_lifecycleGate) {
            if (_stopping) { throw new OperationCanceledException("The Galatea host is stopping."); }
            lazy = _sessions.GetOrAdd(
                characterId,
                static (key, state) => new Lazy<Task<CharacterSessionHost>>(
                    () => state.Service.CreateSessionAsync(state.Character, CancellationToken.None),
                    LazyThreadSafetyMode.ExecutionAndPublication
                ),
                (Service: this, Character: character)
            );
        }

        try {
            var session = await lazy.Value.ConfigureAwait(false);
            DebugUtil.Debug(
                "Galatea.Session",
                $"GetSessionAsync: character={characterId}"
            );
            return session;
        }
        catch {
            _sessions.TryRemove(new KeyValuePair<
                string,
                Lazy<Task<CharacterSessionHost>>
            >(characterId, lazy));
            throw;
        }
    }

    private GalateaInternalMailTarget? ResolveInternalMailTarget(
        GalateaCharacterConfig sender,
        SendMailIntent intent
    ) {
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(intent);
        if (_characterRecipientDirectory is null
            || !_characterRecipientDirectory.TryGetExact(
                intent.Recipient, out GalateaCharacterRecipient? target)
            || string.Equals(target.CharacterId, sender.CharacterId,
                StringComparison.Ordinal)) {
            return null;
        }
        return new GalateaInternalMailTarget(
            target.CharacterId,
            target.SessionRepositoryId
                ?? throw new InvalidDataException(
                    "A configured character-mail target has no repository locator."),
            sender.CharacterName.Value
        );
    }

    private StableRecentTurnsProjection BuildRecentTurnsResponse(
        SessionJournalEngine engine,
        int maxTurns = RecentTurnLimit
    ) {
        ArgumentNullException.ThrowIfNull(engine);
        SessionCompletedTurnsReadResult read =
            engine.ReadRecentCompletedTurns(maxTurns);
        SessionCompletedTurnsSnapshot snapshot = read switch {
            SessionCompletedTurnsReadResult.Snapshot available =>
                available.Value,
            SessionCompletedTurnsReadResult.LimitExceeded limit =>
                throw new GalateaRecentProjectionException(
                    "recent-view-limit-exceeded",
                    $"Completed-turn projection exceeded '{limit.Limit}'."
                ),
            SessionCompletedTurnsReadResult.UnsupportedSchema schema =>
                throw new GalateaRecentProjectionException(
                    "session-schema-unsupported",
                    schema.Detail
                ),
            SessionCompletedTurnsReadResult.Corruption corruption =>
                throw new GalateaRecentProjectionException(
                    "session-invalid",
                    corruption.Detail
                ),
            _ => throw new InvalidDataException(
                "Unknown completed-turn projection result."
            )
        };
        IReadOnlyList<RecentTurnDto> turns = [
            .. snapshot.Turns.Select(
                GalateaRecentTurnDisplayAdapter.Project
            )
        ];
        string? rewindLatestToken = snapshot.CapturedHead is { } head
            && snapshot.Turns.FirstOrDefault()?.Outcome.Address
                == head
            && PlayerTurnObservationClassifier.TryProject(
                snapshot.Turns.First().ObservationContent,
                out PlayerTurnObservationClassifier.Projection projection
            )
            && projection.RestorablePlayerText is not null
                ? EventAddressTextCodec.Format(head)
                : null;
        DebugUtil.Debug(
            "Galatea.Session",
            $"BuildRecentTurnsResponse: head={snapshot.CapturedHead}, responseTurns={turns.Count}, rewindEligible={rewindLatestToken is not null}, firstTurn={DescribeTurn(turns.FirstOrDefault())}"
        );
        return new StableRecentTurnsProjection(
            new RecentTurnsResponseDto(
                turns,
                rewindLatestToken,
                ContextHeaderDto.Empty
            ),
            snapshot.CapturedHead,
            snapshot.DerivedContextNthPrevious
        );
    }

    public async Task<RecentTurnsResponseDto> GetRecentTurnsAsync(
        CharacterSessionHost host,
        CancellationToken ct
    ) {
        ArgumentNullException.ThrowIfNull(host);

        if (!host.TurnLock.Wait(0)) {
            throw new GalateaRecentProjectionException(
                "recent-view-busy",
                "The recent view is temporarily unavailable while a turn writer owns the session."
            );
        }
        try {
            return await RefreshRecentTurnsAsync(host, ct)
                .ConfigureAwait(false);
        }
        finally {
            host.TurnLock.Release();
        }
    }

    public Task<RecapCadenceProgressSnapshotDto>
        GetRecapCadenceProgressAsync(
        CharacterSessionHost host,
        CancellationToken ct
    ) {
        ArgumentNullException.ThrowIfNull(host);

        if (!host.TurnLock.Wait(0)) {
            throw new GalateaRecentProjectionException(
                "recap-cadence-progress-busy",
                "Recap cadence progress is temporarily unavailable while "
                    + "a turn writer owns the session."
            );
        }
        try {
            ct.ThrowIfCancellationRequested();
            EventAddress? capturedRawHead =
                host.Engine.ReadCurrentHead();
            RecapCadenceProgressSnapshotDto result =
                capturedRawHead is { } availableHead
                    ? GalateaRecapCadenceProgress.Inspect(
                        host.Engine.ReadView,
                        availableHead,
                        new O200kBaseHistoryUnitLoadEstimator(),
                        ct
                    )
                    : new RecapCadenceProgressSnapshotDto(
                        GalateaRecapCadenceProgress.ExactFreshness,
                        "unprovisioned",
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        "raw-head-absent"
                    );
            ct.ThrowIfCancellationRequested();
            GalateaBoundedJson.RequireFits(
                result,
                MaximumRecapCadenceProgressResponseUtf8Bytes,
                "recap-cadence-progress-limit-exceeded"
            );
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(result);
        }
        finally {
            host.TurnLock.Release();
        }
    }

    internal Task<CurrentTurnDto> GetCurrentTurnAsync(
        CharacterSessionHost host,
        CancellationToken ct
    ) {
        ArgumentNullException.ThrowIfNull(host);

        GalateaLiveTurn? liveTurn = host.GetCurrentTurn();
        if (liveTurn is not null) {
            return Task.FromResult(BuildLiveCurrentTurn(liveTurn));
        }

        if (!host.TurnLock.Wait(0)) {
            return Task.FromResult(new CurrentTurnDto("running"));
        }
        try {
            ct.ThrowIfCancellationRequested();
            liveTurn = host.GetCurrentTurn();
            if (liveTurn is not null) {
                return Task.FromResult(BuildLiveCurrentTurn(liveTurn));
            }

            SessionRuntimeRecoveryRequirements recovery =
                host.Engine.InspectRuntimeRecoveryRequirements(ct);
            CurrentTurnDto result = BuildDurableCurrentTurn(recovery);
            DebugUtil.Debug(
                "Galatea.Session",
                $"GetCurrentTurnAsync: character={host.Character.CharacterId}, status={result.Status}, head={recovery.CapturedHead}"
            );
            return Task.FromResult(result);
        }
        finally {
            host.TurnLock.Release();
        }
    }

    internal CurrentTurnDto BuildLiveCurrentTurn(CharacterSessionHost host) {
        ArgumentNullException.ThrowIfNull(host);
        GalateaLiveTurn? liveTurn = host.GetCurrentTurn();
        return liveTurn is null
            ? new CurrentTurnDto("running")
            : BuildLiveCurrentTurn(liveTurn);
    }

    private static CurrentTurnDto BuildLiveCurrentTurn(
        GalateaLiveTurn liveTurn
    ) {
        CurrentTurnDto result = new(
            "running",
            liveTurn.TurnId,
            liveTurn.Options.ConnectionId
        );
        DebugUtil.Debug(
            "Galatea.Session",
            $"BuildLiveCurrentTurn: turnId={result.TurnId}, connectionId={result.ConnectionId ?? "<none>"}"
        );
        return result;
    }

    internal async ValueTask<RecentTurnsResponseDto>
        RefreshRecentTurnsBestEffortAsync(
        CharacterSessionHost host,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(host);
        try {
            return await RefreshRecentTurnsAsync(
                    host,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (
            GalateaExceptionClassifier.IsNonFatal(ex)
            && ex is not GalateaRecentProjectionException
            && !cancellationToken.IsCancellationRequested
        ) {
            host.MarkRecentSnapshotStale();
            RecentTurnsResponseDto fallback = host.GetRecentTurns();
            DebugUtil.Warning(
                "Galatea.Session",
                $"Stable session snapshot refresh failed: character={host.Character.CharacterId}; exceptionType={ex.GetType().FullName}"
            );
            return fallback;
        }
    }

    internal async ValueTask<RecentTurnsResponseDto?>
        RefreshRecentTurnsForCompletedStreamAsync(
        CharacterSessionHost host,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(host);
        try {
            return await RefreshRecentTurnsAsync(
                    host,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) {
            host.MarkRecentSnapshotStale();
            DebugUtil.Warning(
                "Galatea.Session",
                "Completed turn recent refresh was cancelled after the "
                    + $"durable boundary: character={host.Character.CharacterId}; exceptionType={ex.GetType().FullName}"
            );
            return null;
        }
        catch (Exception ex) when (
            GalateaExceptionClassifier.IsNonFatal(ex)
        ) {
            host.MarkRecentSnapshotStale();
            DebugUtil.Warning(
                "Galatea.Session",
                "Completed turn has no exact bounded recent view: "
                + $"character={host.Character.CharacterId}; exceptionType={ex.GetType().FullName}"
            );
            return null;
        }
    }

    private async ValueTask<RecentTurnsResponseDto>
        RefreshRecentTurnsAsync(
        CharacterSessionHost host,
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        StableRecentTurnsProjection projection = BuildRecentTurnsResponse(
            host.Engine
        );
        GalateaRecentContextInspection inspection =
            projection.CapturedHead is { } capturedHead
                ? GalateaRecapGridReadiness.InspectRecentContext(
                    host.Engine.ReadView,
                    capturedHead,
                    host.DefaultPolicy,
                    projection.DerivedContextNthPrevious
                        ?? throw new InvalidDataException(
                            "Recent projection has no governing derived-context ordinal."
                        ),
                    cancellationToken
                )
                : new GalateaRecentContextInspection(
                    new RecapGridReadinessSnapshotDto(
                        GalateaRecapGridReadiness.ExactFreshness,
                        "unprovisioned",
                        null,
                        Code: "raw-head-absent"
                    ),
                    ContextHeaderDto.Empty
                );
        RecentTurnsResponseDto recent = projection.Response with {
            ContextHeader = inspection.ContextHeader,
            RecapGridReadiness = inspection.Readiness,
            RewindLatestToken = inspection.Readiness.Freshness
                    == GalateaRecapGridReadiness.ExactFreshness
                ? projection.Response.RewindLatestToken
                : null
        };
        GalateaBoundedJson.RequireFits(
            recent,
            MaximumRecentResponseUtf8Bytes,
            "recent-view-limit-exceeded"
        );
        host.SetRecentTurns(recent);
        return recent;
    }

    private sealed record StableRecentTurnsProjection(
        RecentTurnsResponseDto Response,
        EventAddress? CapturedHead,
        int? DerivedContextNthPrevious
    );

    private static CurrentTurnDto BuildDurableCurrentTurn(
        SessionRuntimeRecoveryRequirements recovery
    ) => recovery switch {
        SessionRuntimeRecoveryRequirements.NoRuntimeRequired {
            Phase: SessionExecutionPhase.Idle
        } => new CurrentTurnDto("idle"),
        SessionRuntimeRecoveryRequirements
            .LegacyFailedTurnBlocked => RecoveryCurrentTurn(recovery),
        SessionRuntimeRecoveryRequirements.NoRuntimeRequired {
            Phase: SessionExecutionPhase.Empty
        } => new CurrentTurnDto("unprovisioned"),
        SessionRuntimeRecoveryRequirements.NewRequestRequired =>
            RecoveryCurrentTurn(recovery),
        SessionRuntimeRecoveryRequirements.FrozenCompletionRequired =>
            RecoveryCurrentTurn(recovery),
        SessionRuntimeRecoveryRequirements.ToolContinuationRequired =>
            RecoveryCurrentTurn(recovery),
        _ => throw new InvalidDataException(
            "Unknown runtime recovery requirement."
        )
    };

    private static CurrentTurnDto RecoveryCurrentTurn(
        SessionRuntimeRecoveryRequirements recovery
    ) => new(
        "recovery-required",
        RecoveryHead: EventAddressTextCodec.FormatNullable(
            recovery.CapturedHead
        )
    );

    /// <summary>
    /// Starts an admitted ordinary player turn and freezes its reply cutoff.
    /// The caller must still own <see cref="CharacterSessionHost.TurnLock"/> and
    /// must already have accepted the exact recovery boundary and selected
    /// main connection. Keeping the cutoff here makes caller acceptance
    /// instant, rather than the later background task, authoritative.
    /// </summary>
    internal async ValueTask ReconcileDurableAdmissionAsync(
        CharacterSessionHost host,
        CancellationToken cancellationToken
    ) {
        await using var operation = host.BeginAdmission(cancellationToken, _admissionStopping.Token);
        try {
            await ReconcileDurableAdmissionCoreAsync(host, operation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (operation.StopRequested
            && !cancellationToken.IsCancellationRequested && !_admissionStopping.IsCancellationRequested) {
            throw new GalateaTurnException("本次整理已停止；持久化目标保留供下次续接。", "admission-stopped");
        }
    }

    private async ValueTask ReconcileDurableAdmissionCoreAsync(CharacterSessionHost host, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(host);
        GalateaDurableReplyLeaseReconcileResult reply =
            ReconcileDurableDeliveries(host, cancellationToken);
        if (reply is GalateaDurableReplyLeaseReconcileResult.RolledBack
                or GalateaDurableReplyLeaseReconcileResult.Consumed) {
            _ = host.DelegationHandle?.Signal();
        }

        CharacterNoteDefaultPodReconciler? memory =
            host.CharacterMemoryReconciler;
        if (memory is null) {
            _ = await ReconcileOutboundMailExtractionAsync(
                    host,
                    cancellationToken
                )
                .ConfigureAwait(false);
            return;
        }
        await ReconcileActiveCharacterNoteDerivedInfoPlanAsync(host)
            .ConfigureAwait(false);

        CharacterNotePendingReconcileResult pending = await memory
            .ReconcilePendingAsync()
            .ConfigureAwait(false);
        if (pending is CharacterNotePendingReconcileResult.Reconciled
                reconciled) {
            RequireCharacterNoteAdmissionSettled(reconciled.Result);
        }

        EventAddress? selectedHead = host.Engine.ReadCurrentHead();
        if (selectedHead is null) {
            _ = await ReconcileOutboundMailExtractionAsync(
                    host,
                    cancellationToken
                )
                .ConfigureAwait(false);
            return;
        }
        GalateaTerminalActionExtractionReadResult read =
            GalateaTerminalActionExtractionTargetReader.ReadAt(
                host.Engine,
                selectedHead.Value,
                cancellationToken
            );
        switch (read) {
            case GalateaTerminalActionExtractionReadResult.Available available:
                await ReconcileAdmissionExtractionsAsync(
                        host,
                        memory,
                        available.Target,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                return;
            case GalateaTerminalActionExtractionReadResult
                    .NoTerminalActionAtHead:
                _ = await ReconcileOutboundMailExtractionAsync(
                        host,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                return;
            case GalateaTerminalActionExtractionReadResult.Failed failed:
                throw CreateTerminalActionReadFailure(failed);
            default:
                throw new InvalidDataException(
                    "Unknown terminal Action extraction read result."
                );
        }
    }

    /// <summary>
    /// Recovers only an already durable DerivedInfo plan. This admission
    /// fence never materializes turn context and never calls a provider.
    /// The caller must own <see cref="CharacterSessionHost.TurnLock"/>.
    /// </summary>
    internal static async ValueTask
        ReconcileActiveCharacterNoteDerivedInfoPlanAsync(
        CharacterSessionHost host
    ) {
        ArgumentNullException.ThrowIfNull(host);
        CharacterNoteDefaultPodReconciler? memory =
            host.CharacterMemoryReconciler;
        if (memory is null) { return; }

        CharacterNoteDerivedInfoReconcileResult result = await memory
            .ReconcileActiveDerivedInfoPlanAsync()
            .ConfigureAwait(false);
        switch (result) {
            case CharacterNoteDerivedInfoReconcileResult.NoWork:
            case CharacterNoteDerivedInfoReconcileResult.Applied:
            case CharacterNoteDerivedInfoReconcileResult.Rejected:
                return;
            case CharacterNoteDerivedInfoReconcileResult.Deferred deferred:
                throw new GalateaTurnException(
                    "Character Memory DerivedInfo settlement is temporarily unavailable.",
                    "character-memory-settlement-deferred",
                    new IOException(deferred.Code)
                );
            case CharacterNoteDerivedInfoReconcileResult.Quarantined
                    quarantined:
                throw new GalateaTurnException(
                    "Character Memory authority is quarantined.",
                    "character-memory-quarantined",
                    new InvalidDataException(quarantined.Code)
                );
            default:
                throw new InvalidDataException(
                    "Unknown Character Note DerivedInfo reconciliation result."
                );
        }
    }

    /// <summary>
    /// Applies the complete durable recovery admission fence before the HTTP
    /// recovery endpoint may create a live turn. This includes provider-free
    /// recovery of any active Character Note DerivedInfo plan.
    /// </summary>
    internal ValueTask PrepareRecoveryAdmissionAsync(
        CharacterSessionHost host,
        CancellationToken cancellationToken
    ) => ReconcileDurableAdmissionAsync(host, cancellationToken);

    private async ValueTask ReconcileAdmissionExtractionsAsync(
        CharacterSessionHost host,
        CharacterNoteDefaultPodReconciler memory,
        GalateaTerminalActionExtractionTarget target,
        CancellationToken callerToken
    ) {
        using var deadlineCts = CreateCharacterNoteTestDeadline();
        using var mailAbortCts = new CancellationTokenSource();
        using var noteCts = CancellationTokenSource.CreateLinkedTokenSource(
            callerToken,
            deadlineCts.Token,
            mailAbortCts.Token
        );
        Task<GalateaOutboundMailExtractionReconcileResult> mailTask =
            ReconcileOutboundMailExtractionAsync(
                host,
                callerToken,
                target
            ).AsTask();
        Task<CharacterNoteDefaultPodReconcileResult> noteTask = memory
            .ReconcileTargetAsync(host.Engine, target, noteCts.Token)
            .AsTask();

        Exception? mailFailure = null;
        Exception? cancellationFailure = null;
        bool noteCompletedBeforeMailAbort = false;
        try {
            _ = await mailTask.ConfigureAwait(false);
        }
        catch (Exception exception) {
            noteCompletedBeforeMailAbort = noteTask.IsCompleted;
            mailFailure = exception;
            cancellationFailure = TryCancel(mailAbortCts);
        }

        CharacterNoteDefaultPodReconcileResult? noteResult = null;
        Exception? noteFailure = null;
        try {
            noteResult = await noteTask.ConfigureAwait(false);
        }
        catch (Exception exception) {
            noteFailure = exception;
        }

        var failures = new CharacterNoteFailureSet(
            mailFailure,
            noteFailure,
            cancellationFailure,
            mailAbortCts.IsCancellationRequested,
            noteCompletedBeforeMailAbort,
            deadlineCts.IsCancellationRequested,
            callerToken.IsCancellationRequested
        );
        failures.ThrowIfFatal();
        failures.ThrowIfCallerCanceled(callerToken);
        if (failures.EffectiveNoteFailure is { } effectiveNoteFailure) {
            failures.ThrowNotePrimary(
                CreateCharacterNoteAdmissionFailure(
                    effectiveNoteFailure,
                    deadlineCts.IsCancellationRequested,
                    mailFailure is not null
                )
            );
        }
        if (noteResult is not null) {
            try {
                RequireCharacterNoteAdmissionSettled(noteResult);
            }
            catch (GalateaTurnException authority) {
                failures.ThrowAuthorityPrimary(authority);
            }
            catch (Exception invariant) when (
                GalateaExceptionClassifier.IsNonFatal(invariant)) {
                failures.ThrowAuthorityPrimary(new GalateaTurnException(
                    "Character Memory admission violated its durable boundary.",
                    "character-memory-state-invalid",
                    invariant
                ));
            }
        }
        else if (mailFailure is null) {
            failures.ThrowAuthorityPrimary(new GalateaTurnException(
                "Character Note admission completed without a result.",
                "character-memory-state-invalid"
            ));
        }
        if (mailFailure is not null) {
            failures.ThrowMailPrimary();
        }
    }

    private static void RequireCharacterNoteAdmissionSettled(
        CharacterNoteDefaultPodReconcileResult result
    ) {
        switch (result) {
            case CharacterNoteDefaultPodReconcileResult.BaselineCovered:
            case CharacterNoteDefaultPodReconcileResult.ZeroCaptured:
            case CharacterNoteDefaultPodReconcileResult.AppliedNow:
            case CharacterNoteDefaultPodReconcileResult.AlreadyApplied:
            case CharacterNoteDefaultPodReconcileResult.Rejected:
                return;
            case CharacterNoteDefaultPodReconcileResult.DeferredAfterCapture
                    deferred:
                throw new GalateaTurnException(
                    "Character Memory settlement is temporarily unavailable.",
                    "character-memory-settlement-deferred",
                    new IOException(deferred.Code)
                );
            case CharacterNoteDefaultPodReconcileResult.Quarantined
                    quarantined:
                throw new GalateaTurnException(
                    "Character Memory authority is quarantined.",
                    "character-memory-quarantined",
                    new InvalidDataException(quarantined.Code)
                );
            case CharacterNoteDefaultPodReconcileResult.SelectedHeadChanged:
                throw new GalateaTurnException(
                    "Durable extraction head changed; retry admission.",
                    "delegation-state-changed"
                );
            default:
                throw new InvalidDataException(
                    "Unknown Character Note reconciliation result."
                );
        }
    }

    private static GalateaTurnException CreateCharacterNoteAdmissionFailure(
        Exception failure,
        bool deadlineExpired,
        bool mailAborted
    ) {
        string reason = failure switch {
            OperationCanceledException when deadlineExpired =>
                "character-memory-extraction-timeout",
            OperationCanceledException when mailAborted =>
                "character-memory-extraction-aborted",
            TextExtractionException =>
                "character-memory-extraction-unavailable",
            CharacterNoteDefaultPodAccessException =>
                "character-memory-pod-unavailable",
            _ => "character-memory-state-invalid",
        };
        return new GalateaTurnException(
            "Character Memory reconciliation must settle before admission.",
            reason,
            failure
        );
    }

    private CancellationTokenSource CreateCharacterNoteTestDeadline() {
        // Production generation attempts own their deadlines in the retry
        // decorator. Do not deadline the complete logical extraction/retry.
        if (CharacterNoteExtractionDeadlineForTest is not { } deadline) {
            return new CancellationTokenSource();
        }
        if (deadline <= TimeSpan.Zero) {
            throw new InvalidOperationException(
                "Character Note extraction deadline must be positive."
            );
        }
        return new CancellationTokenSource(deadline);
    }

    private TimeSpan RequireCharacterNoteDerivedInfoDeadline() {
        TimeSpan deadline = CharacterNoteDerivedInfoDeadlineForTest
            ?? CharacterNoteDerivedInfoPump.DefaultProviderDeadline;
        if (deadline <= TimeSpan.Zero) {
            throw new InvalidOperationException(
                "Character Note DerivedInfo deadline must be positive."
            );
        }
        return deadline;
    }

    private static Exception? TryCancel(CancellationTokenSource source) {
        try {
            source.Cancel();
            return null;
        }
        catch (Exception exception) {
            return exception;
        }
    }

    private sealed class CharacterNoteFailureSet {
        private readonly Exception[] _ordered;

        internal CharacterNoteFailureSet(
            Exception? mailFailure,
            Exception? noteFailure,
            Exception? cancellationFailure,
            bool mailAbortRequested,
            bool noteCompletedBeforeMailAbort,
            bool deadlineExpired,
            bool callerCanceled
        ) {
            MailFailure = mailFailure;
            NoteFailure = noteFailure;
            CancellationFailure = cancellationFailure;
            NoteCancellationInducedByMailAbort = mailFailure is not null
                && noteFailure is OperationCanceledException
                && mailAbortRequested
                && !noteCompletedBeforeMailAbort
                && !deadlineExpired
                && !callerCanceled;
            var ordered = new List<Exception>(3);
            if (mailFailure is not null) {
                ordered.Add(mailFailure);
            }
            if (noteFailure is not null
                && !NoteCancellationInducedByMailAbort) {
                ordered.Add(noteFailure);
            }
            if (cancellationFailure is not null) {
                ordered.Add(cancellationFailure);
            }
            _ordered = ordered.ToArray();
        }

        internal Exception? MailFailure { get; }

        private Exception? NoteFailure { get; }

        private Exception? CancellationFailure { get; }

        internal bool NoteCancellationInducedByMailAbort { get; }

        internal Exception? EffectiveNoteFailure =>
            NoteCancellationInducedByMailAbort ? null : NoteFailure;

        internal void ThrowIfFatal() {
            if (_ordered.All(GalateaExceptionClassifier.IsNonFatal)) {
                return;
            }
            ThrowOrdered("Character Note coordination observed a fatal failure.");
        }

        internal void ThrowIfCallerCanceled(CancellationToken callerToken) {
            if (!callerToken.IsCancellationRequested) { return; }
            if (_ordered.Length == 1
                && _ordered[0] is OperationCanceledException canceled
                && canceled.CancellationToken == callerToken) {
                ExceptionDispatchInfo.Capture(canceled).Throw();
            }
            if (_ordered.Length > 0) {
                throw new OperationCanceledException(
                    "Character Note coordination was canceled after draining all operations.",
                    new AggregateException(
                        "Ordered Mail, Note, and cancellation-callback failures.",
                        _ordered
                    ),
                    callerToken
                );
            }
            callerToken.ThrowIfCancellationRequested();
        }

        [DoesNotReturn]
        internal void ThrowMailPrimary() {
            Exception mail = MailFailure
                ?? throw new InvalidOperationException(
                    "Mail-primary arbitration requires a Mail failure."
                );
            if (_ordered.Length == 1) {
                ExceptionDispatchInfo.Capture(mail).Throw();
            }
            if (mail is GalateaTurnException turn) {
                throw new GalateaTurnException(
                    turn.Message,
                    turn.FailureReason,
                    new AggregateException(
                        "Ordered Mail, Note, and cancellation-callback failures.",
                        _ordered
                    )
                );
            }
            ThrowOrdered(
                "Ordered Mail, Note, and cancellation-callback failures."
            );
        }

        [DoesNotReturn]
        internal void ThrowNotePrimary(GalateaTurnException primary) {
            ArgumentNullException.ThrowIfNull(primary);
            var secondary = new List<Exception>(2);
            if (MailFailure is { } mail) { secondary.Add(mail); }
            if (CancellationFailure is { } cancellation) {
                secondary.Add(cancellation);
            }
            if (secondary.Count == 0) { throw primary; }
            ThrowStablePrimary(primary, secondary.ToArray());
        }

        [DoesNotReturn]
        internal void ThrowAuthorityPrimary(GalateaTurnException primary) {
            ArgumentNullException.ThrowIfNull(primary);
            if (_ordered.Length == 0) { throw primary; }
            ThrowStablePrimary(primary, _ordered);
        }

        [DoesNotReturn]
        private static void ThrowStablePrimary(
            GalateaTurnException primary,
            Exception[] secondary
        ) {
            throw new GalateaTurnException(
                primary.Message,
                primary.FailureReason,
                new AggregateException(
                    "Authority failure followed by ordered Mail, Note, and cancellation-callback failures.",
                    [primary, .. secondary]
                )
            );
        }

        [DoesNotReturn]
        private void ThrowOrdered(string message) {
            if (_ordered.Length == 1) {
                ExceptionDispatchInfo.Capture(_ordered[0]).Throw();
            }
            throw new AggregateException(message, _ordered);
        }
    }

    internal ValueTask<string> NormalizeUserMessageAtAdmissionAsync(
        string userMessage,
        CancellationToken cancellationToken
    ) => _inputPreprocessor.ProcessAsync(
        userMessage,
        cancellationToken
    );

    internal GalateaLiveTurn StartTurn(
        CharacterSessionHost host,
        string userMessage,
        GalateaTurnOptions options,
        GalateaSenderSnapshot sender
    ) {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(userMessage);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(sender);
        if (sender.Kind != "player") {
            throw new ArgumentException("Player admission requires a Player identity.", nameof(sender));
        }
        options = FreezeConnectionState(host, options);
        string? messageError = GalateaHttpV1.ValidateMessage(userMessage);
        if (messageError is not null) {
            throw new ArgumentException(messageError, nameof(userMessage));
        }
        bool isDelegateReplyDiscriminator = string.Equals(
            userMessage,
            PlayerTurnObservationEnvelope
                .DelegateReplyLeasePlayerTextDiscriminator,
            StringComparison.Ordinal
        );
        GalateaDurableReplyLeaseBeginResult cutoff =
            host.ReplyLeaseReconciler.BeginCutoff(
                userMessage, ReadPendingReceiptNotice(host));
        if (cutoff is GalateaDurableReplyLeaseBeginResult.Empty) {
            return StartPlayerTurnWithoutReplyLease(
                host,
                userMessage,
                options,
                sender
            );
        }
        if (cutoff is GalateaDurableReplyLeaseBeginResult.Created created) {
            if (isDelegateReplyDiscriminator) {
                created.Lease.RollbackBeforeEffect();
                return StartPlayerTurnWithoutReplyLease(
                    host,
                    userMessage,
                    options,
                    sender
                );
            }
            return StartPlayerTurnWithCreatedCutoff(
                host,
                userMessage,
                options,
                created,
                sender
            );
        }
        throw new InvalidDataException(
            "Unknown durable reply cutoff result."
        );
    }

    private static GalateaLiveTurn StartPlayerTurnWithoutReplyLease(
        CharacterSessionHost host,
        string playerText,
        GalateaTurnOptions options,
        GalateaSenderSnapshot sender
    ) {
        return host.StartTurn(new GalateaFreshInput.PlayerAction(playerText, sender), options);
    }

    private static PlayerTurnNotice.NoteSaveReceipt? ReadPendingReceiptNotice(
        CharacterSessionHost host
    ) => host.CharacterMemoryReconciler?.ReadPendingReceiptDelivery() is { } receipt
        ? CharacterNoteSaveReceipt.SelectForObservation(receipt)
        : null;

    /// <summary>
    /// Conditionally starts a fresh turn from the durable Ready reply prefix.
    /// The caller must own <see cref="CharacterSessionHost.TurnLock"/> and must
    /// already have admitted an exact Idle session boundary and connection.
    /// Empty is side-effect free with respect to the reply lease and live turn.
    /// </summary>
    internal GalateaReadyReplyTurnStartResult StartReadyReplyTurn(
        CharacterSessionHost host,
        GalateaTurnOptions options
    ) {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(options);
        options = FreezeConnectionState(host, options);
        GalateaDurableReplyLeaseBeginResult cutoff = host
            .ReplyLeaseReconciler.BeginCutoff(
                PlayerTurnObservationEnvelope
                    .DelegateReplyLeasePlayerTextDiscriminator,
                ReadPendingReceiptNotice(host)
            );
        return cutoff switch {
            GalateaDurableReplyLeaseBeginResult.Empty =>
                new GalateaReadyReplyTurnStartResult.Empty(),
            GalateaDurableReplyLeaseBeginResult.Created created =>
                new GalateaReadyReplyTurnStartResult.Started(
                    StartDelegateReplyTurnWithCreatedCutoff(
                        host,
                        options,
                        created
                    )
                ),
            _ => throw new InvalidDataException(
                "Unknown durable reply cutoff result."
            )
        };
    }

    private static GalateaLiveTurn StartDelegateReplyTurnWithCreatedCutoff(
        CharacterSessionHost host,
        GalateaTurnOptions options,
        GalateaDurableReplyLeaseBeginResult.Created created
    ) {
        try {
            return host.StartTurn(
                new GalateaFreshInput.DelegateReply(
                    created.Lease.ReadNotices()
                ),
                options,
                created.Lease
            );
        }
        catch (Exception original) {
            try {
                created.Lease.RollbackBeforeEffect();
            }
            catch (Exception cleanup) when (
                GalateaExceptionClassifier.IsNonFatal(cleanup)) {
                if (!GalateaExceptionClassifier.IsNonFatal(original)) {
                    ExceptionDispatchInfo.Capture(original).Throw();
                }
                throw new AggregateException(
                    "Fresh-turn admission and durable cutoff rollback both failed.",
                    original,
                    cleanup
                );
            }
            ExceptionDispatchInfo.Capture(original).Throw();
            throw;
        }
    }

    private static GalateaLiveTurn StartPlayerTurnWithCreatedCutoff(
        CharacterSessionHost host,
        string playerText,
        GalateaTurnOptions options,
        GalateaDurableReplyLeaseBeginResult.Created created,
        GalateaSenderSnapshot sender
    ) {
        try {
            return host.StartTurn(
                new GalateaFreshInput.PlayerAction(
                    playerText,
                    sender,
                    created.Lease.ReadNotices()
                ),
                options,
                created.Lease
            );
        }
        catch (Exception original) {
            try {
                created.Lease.RollbackBeforeEffect();
            }
            catch (Exception cleanup) when (
                GalateaExceptionClassifier.IsNonFatal(cleanup)) {
                if (!GalateaExceptionClassifier.IsNonFatal(original)) {
                    ExceptionDispatchInfo.Capture(original).Throw();
                }
                throw new AggregateException(
                    "Fresh-turn admission and durable cutoff rollback both failed.",
                    original,
                    cleanup
                );
            }
            ExceptionDispatchInfo.Capture(original).Throw();
            throw;
        }
    }

    /// <summary>
    /// Revalidates the HTTP fresh-turn admission while the caller retains the
    /// session writer lock. Pending work must first be resumed or explicitly
    /// ended; fresh input never removes its durable history.
    /// </summary>
    internal async ValueTask PrepareFreshTurnAdmissionAsync(
        CharacterSessionHost host,
        SessionRuntimeRecoveryRequirements admitted,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(admitted);
        await ReconcileActiveCharacterNoteDerivedInfoPlanAsync(host)
            .ConfigureAwait(false);
        SessionRuntimeRecoveryRequirements current =
            host.Engine.InspectRuntimeRecoveryRequirements(
                cancellationToken
            );
        switch (admitted) {
            case SessionRuntimeRecoveryRequirements.NoRuntimeRequired {
                Phase: SessionExecutionPhase.Idle,
                CapturedHead: { } admittedHead
            } when current is SessionRuntimeRecoveryRequirements
                    .NoRuntimeRequired {
                        Phase: SessionExecutionPhase.Idle,
                        CapturedHead: { } currentHead
                    }
                && currentHead == admittedHead:
                break;
            default:
                throw new GalateaTurnException(
                    "会话边界已变化，请刷新后重试。",
                    "stale-session-head"
                );
        }
    }

    internal GalateaLiveTurn StartInboundMailTurn(
        CharacterSessionHost host,
        MailboxMessage message,
        GalateaTurnOptions options,
        GalateaInternalMailDeliveryBinding? internalDelivery = null,
        GalateaSenderSnapshot? injectedBy = null,
        GalateaSenderSnapshot? sender = null
    ) {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(options);
        options = FreezeConnectionState(host, options);
        if (internalDelivery is not null && injectedBy is not null) {
            throw new ArgumentException("Internal relay cannot acquire an external Player injector.", nameof(injectedBy));
        }
        if (injectedBy is not null && injectedBy.Kind != "player") {
            throw new ArgumentException("HTTP injection requires a Player source.", nameof(injectedBy));
        }
        if (sender is not null && (sender.Kind != "character" || internalDelivery is null)) {
            throw new ArgumentException("Trusted Character sender requires an internal delivery binding.", nameof(sender));
        }
        return host.StartTurn(
            new GalateaFreshInput.InboundMail(message, internalDelivery, injectedBy, sender),
            options
        );
    }

    internal GalateaLiveTurn StartRecovery(
        CharacterSessionHost host,
        GalateaTurnOptions options
    ) {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(options);
        return host.StartRecovery(options);
    }

    internal GalateaPreparedPopLatestTurn?
        PrepareAndCommitPopLatestTurn(
        CharacterSessionHost host,
        EventAddress expectedHead,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(host);
        GalateaCharacterMailDeliveryReconciler.Reconcile(
            _delegationSupervisor, host, cancellationToken);
        GalateaNoteReceiptDelivery.Reconcile(
            host.CharacterMemoryReconciler, host.Engine, cancellationToken);
        SessionCompletedTurnRewindPrepareResult preparation =
            host.Engine.PrepareLatestCompletedTurnRewind(
                expectedHead,
                cancellationToken
            );
        if (preparation
            is not SessionCompletedTurnRewindPrepareResult.Prepared ready) {
            return preparation switch {
                SessionCompletedTurnRewindPrepareResult.LimitExceeded limit =>
                    throw new GalateaRecentProjectionException(
                        "recent-view-limit-exceeded",
                        $"Completed-turn rewind exceeded '{limit.Limit}'."
                    ),
                SessionCompletedTurnRewindPrepareResult.UnsupportedSchema schema =>
                    throw new GalateaRecentProjectionException(
                        "session-schema-unsupported",
                        schema.Detail
                    ),
                SessionCompletedTurnRewindPrepareResult.Corruption corruption =>
                    throw new GalateaRecentProjectionException(
                        "session-invalid",
                        corruption.Detail
                    ),
                _ => null
            };
        }

        if (!PlayerTurnObservationClassifier.TryProject(
                ready.Value.ObservationContent,
                out PlayerTurnObservationClassifier.Projection projection)
            || projection.RestorablePlayerText is not { } poppedUserText) {
            return null;
        }
        int sourceBytes;
        try {
            sourceBytes = GalateaBoundedJson.StrictUtf8.GetByteCount(
                poppedUserText
            );
        }
        catch (EncoderFallbackException exception) {
            throw new GalateaRecentProjectionException(
                "session-invalid",
                "Popped character text is not valid Unicode.",
                exception
            );
        }
        if (sourceBytes > MaximumPoppedUserTextUtf8Bytes) {
            throw new GalateaRecentProjectionException(
                "popped-user-text-limit-exceeded",
                "Popped character text exceeds the display receipt limit."
            );
        }
        byte[] receiptBytes = JsonSerializer.SerializeToUtf8Bytes(
            new PopLatestTurnReceiptDto(poppedUserText),
            GalateaJson.Options
        );
        if (receiptBytes.Length > MaximumPopReceiptUtf8Bytes) {
            throw new GalateaRecentProjectionException(
                "popped-user-text-limit-exceeded",
                "Encoded pop receipt exceeds its response limit."
            );
        }
        var preparedReceipt = new GalateaPreparedPopLatestTurn(
            poppedUserText,
            receiptBytes
        );
        RecentTurnsResponseDto preparedStaleSnapshot =
            host.PrepareRecentSnapshotStale();

        SessionTurnRetractionResult committed =
            host.Engine.CommitPreparedCompletedTurnRewind(
                ready.Value,
                cancellationToken
            );
        if (committed is not SessionTurnRetractionResult.Moved) {
            return null;
        }
        SetRuntimeConnectionOverride(host.Character.CharacterId, null);
        host.SetRecentTurns(preparedStaleSnapshot);
        return preparedReceipt;
    }

    internal GalateaLiveTurn? FindTurn(CharacterSessionHost host, string turnId) {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(turnId);
        return host.FindTurn(turnId);
    }

    /// <summary>
    /// Settles one live turn and its process-local autonomy cadence outcome.
    /// The caller must own <see cref="CharacterSessionHost.TurnLock"/>.
    /// </summary>
    internal void FinishTurn(CharacterSessionHost host, GalateaLiveTurn turn) {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(turn);
        bool completed = string.Equals(
            turn.Status,
            "completed",
            StringComparison.Ordinal
        );
        bool closed = completed || turn.Status == "terminated";
        if (!closed
            && turn.FreshInput is GalateaFreshInput.InboundMail {
                InternalDelivery: not null
            }) {
            // A relay-owned inbound mail may already have appended its
            // Observation. Keep automatic admission paused until the normal
            // recovery/settlement path has made that boundary explicit.
            host.AutomaticAdmissionFailed = true;
            host.AutomaticAdmissionFailure = new ApiErrorDto(
                "character-mail-generation-failed",
                "角色站内信生成未完成；请先处理会话恢复。"
            );
        }
        bool settled = host.AutonomyCadence?.SettleMainTurn(
            turn.AutonomyCadenceSettlement,
            turn.FreshInput is GalateaFreshInput.HeartbeatActivation,
            closed,
            turn.AutonomyCadenceClaim
        ) ?? turn.AutonomyCadenceSettlement.TrySettle();
        if (settled && closed) {
            host.GenerationBlocked = false;
            host.AutomaticReplyFailed = false;
            host.AutomaticAdmissionFailed = false;
            host.AutomaticAdmissionFailure = null;
        }
        else if (settled && turn.FreshInput is GalateaFreshInput.DelegateReply) {
            host.AutomaticReplyFailed = true;
        }
        if (settled
            && !closed
            && turn.FreshInput
                is GalateaFreshInput.HeartbeatActivation) {
            DebugUtil.Warning(
                "Galatea.Autonomy",
                $"Autonomous activation paused after non-completed turn: character={host.Character.CharacterId}, turnId={turn.TurnId}"
            );
        }
        host.FinishTurn(turn);
        host.PublishAutonomyStatus();
        _characterMailRelay?.Signal();
    }

    /// <summary>
    /// Creates and claims one already-due server-owned autonomous turn.
    /// The caller must own <see cref="CharacterSessionHost.TurnLock"/> and must have
    /// completed exact Idle admission after finding no Ready reply prefix.
    /// </summary>
    internal GalateaLiveTurn StartHeartbeatActivationTurn(
        CharacterSessionHost host,
        GalateaTurnOptions options
    ) {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(options);
        options = FreezeConnectionState(host, options);
        GalateaAutonomyCadence cadence = host.AutonomyCadence
            ?? throw new InvalidOperationException(
                "Heartbeat activation requires a positive autonomy interval."
            );
        GalateaLiveTurn turn = host.StartTurn(
            new GalateaFreshInput.HeartbeatActivation(
                host.Character.CharacterName,
                host.Character.AutonomyIntervalMinutes
            ),
            options
        );
        if (cadence.TryClaimAutonomousActivationStarted(out
                GalateaAutonomyCadenceClaim? claim)) {
            turn.BindAutonomyCadenceClaim(
                claim ?? throw new InvalidOperationException(
                    "A successful autonomous cadence claim returned no token."
                )
            );
            return turn;
        }
        turn.AbortTransportWithoutTerminal();
        host.FinishTurn(turn);
        throw new InvalidOperationException(
            "A heartbeat activation live turn was created without a due cadence claim."
        );
    }

    /// <summary>
    /// Compensates a heartbeat live turn whose synchronous caller acceptance
    /// failed before writer ownership transferred. The caller must own
    /// <see cref="CharacterSessionHost.TurnLock"/>. Background-owned turns must never
    /// call this method.
    /// </summary>
    internal void RollbackHeartbeatActivationAdmission(
        CharacterSessionHost host,
        GalateaLiveTurn turn
    ) {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(turn);
        if (turn.FreshInput is not GalateaFreshInput.HeartbeatActivation
            || turn.AutonomyCadenceClaim is not { } claim
            || !(host.AutonomyCadence?
                .TryRollbackAutonomousActivationClaim(
                    claim,
                    turn.AutonomyCadenceSettlement
                ) ?? false)) {
            throw new InvalidOperationException(
                "Heartbeat activation admission could not roll back its exact cadence claim."
            );
        }
    }

    internal bool RequestStop(CharacterSessionHost host, string turnId) {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(turnId);

        var turn = host.FindTurn(turnId);
        return turn?.RequestStop() == true;
    }

    internal bool EndPendingTurn(CharacterSessionHost host, EventAddress expectedHead, CancellationToken ct) {
        SessionTurnEndResult result = EndPendingTurnGuarded(host, expectedHead, SessionTurnEndReason.Stopped, ct);
        if (result is not SessionTurnEndResult.Ended) { return false; }
        host.GenerationBlocked = false;
        host.AutomaticAdmissionFailed = false;
        host.AutomaticReplyFailed = false;
        host.AutomaticAdmissionFailure = null;
        host.AutonomyCadence?.ResumeAfterPendingTermination();
        _ = ReconcileDurableDeliveries(host, ct);
        host.PublishAutonomyStatus();
        return true;
    }

    private async Task EndLiveTurnAsync(CharacterSessionHost host, GalateaLiveTurn turn, SessionTurnEndReason reason, CancellationToken ct) {
        SessionExecutionBoundaryInspection boundary = host.Engine.InspectExecutionBoundary();
        if (boundary.Phase is not (SessionExecutionPhase.Idle or SessionExecutionPhase.Empty)) {
            if (boundary.Head is not { } head || EndPendingTurnGuarded(host, head, reason, ct) is not SessionTurnEndResult.Ended) {
                host.GenerationBlocked = true;
                throw new GalateaTurnException("轮次尚未到达安全结束边界。", "recovery-required");
            }
        }
        _ = ReconcileDurableDeliveries(host, ct);
        RecentTurnsResponseDto? recent = await RefreshRecentTurnsForCompletedStreamAsync(host, ct).ConfigureAwait(false);
        turn.PublishTerminated(reason.ToString().ToLowerInvariant(), recent);
    }

    private static SessionTurnEndResult EndPendingTurnGuarded(CharacterSessionHost host, EventAddress head,
        SessionTurnEndReason reason, CancellationToken ct) {
        try { return host.Engine.EndPendingTurn(head, reason, ct); }
        catch (Exception exception) when (GalateaExceptionClassifier.IsNonFatal(exception) && !ct.IsCancellationRequested) {
            // Publication may already have succeeded. Only cold reopen can
            // decide; this live writer must not be reused by a pulse.
            host.GenerationBlocked = true;
            throw;
        }
    }

    internal async Task RunTurnAsync(
        CharacterSessionHost host,
        GalateaLiveTurn liveTurn,
        CancellationToken ct
    ) {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(liveTurn);

        using var preDispatchCts =
            CancellationTokenSource.CreateLinkedTokenSource(
                ct,
                liveTurn.PreDispatchStopToken
            );
        CancellationToken turnCancellationToken =
            preDispatchCts.Token;
        if (liveTurn.Options.Mode == GalateaTurnMode.Resume
            && host.Engine.InspectExecutionBoundary().Phase == SessionExecutionPhase.AwaitingToolExecution) {
            // Binding a recovered runtime already drains committed tools.
            // Even a Stop requested before this runner starts cannot cancel them.
            liveTurn.StopController.ProtectCommittedTools();
            turnCancellationToken = ct;
        }

        liveTurn.PublishStatus(GalateaSseStatusCode.Generating);
        DebugUtil.Debug(
            "Galatea.Session",
            $"RunTurnAsync start: character={host.Character.CharacterId}, turnId={liveTurn.TurnId}, input={Preview(liveTurn.UserMessage)}, head={host.Engine.ReadCurrentHead()}"
        );

        CompletionStreamObserver observer = liveTurn.Observer;

        GalateaCompletedOperation completed;
        try {
            completed = liveTurn.Options.Mode
                == GalateaTurnMode.FreshSend
                ? await RunFreshSendAsync(
                        host,
                        liveTurn,
                        observer,
                        turnCancellationToken
                    )
                    .ConfigureAwait(false)
                : await RunRecoveryAsync(
                        host,
                        liveTurn,
                        observer,
                        turnCancellationToken
                    )
                    .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            !ct.IsCancellationRequested
            && liveTurn.StopRequested
        ) {
            await EndLiveTurnAsync(host, liveTurn, SessionTurnEndReason.Stopped, ct).ConfigureAwait(false);
            return;
        }
        catch (SessionJournalTurnAbortedException ex) {
            DebugUtil.Warning(
                "Galatea.Session",
                $"RunTurnAsync completion aborted: character={host.Character.CharacterId}, turnId={liveTurn.TurnId}, termination={ex.Termination.Kind}, providerReason={ex.Termination.ProviderReason ?? "<none>"}, detail={ex.Termination.Detail ?? "<none>"}"
            );
            if (ClassifyBusinessTermination(ex.Termination) is { } reason) {
                await EndLiveTurnAsync(host, liveTurn, reason, ct).ConfigureAwait(false);
                return;
            }
            host.GenerationBlocked = true;
            ReconcileDurableDeliveriesBestEffort(host, liveTurn);
            throw;
        }
        catch {
            if (!ct.IsCancellationRequested) {
                try {
                    host.GenerationBlocked = host.Engine.InspectExecutionBoundary().Phase
                        is not (SessionExecutionPhase.Idle or SessionExecutionPhase.Empty);
                }
                catch (Exception error) when (GalateaExceptionClassifier.IsNonFatal(error)) {
                    // A poisoned writer must never be redispatched by pulses.
                    host.GenerationBlocked = true;
                }
            }
            ReconcileDurableDeliveriesBestEffort(host, liveTurn);
            throw;
        }
        SessionExecutionBoundaryInspection completedBoundary =
            host.Engine.InspectExecutionBoundary();
        if (completedBoundary.Phase != SessionExecutionPhase.Idle) {
            ReconcileDurableDeliveriesBestEffort(host, liveTurn);
            throw new InvalidDataException(
                "A completed Galatea operation must leave an Idle durable boundary."
            );
        }
        EventAddress completedHead = completedBoundary.Head
            ?? throw new InvalidDataException(
                "A completed Galatea operation must leave a non-empty durable head."
            );
        using (var stateCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, liveTurn.StopController.UserStopToken)) {
            try {
                await ExtractConnectionStateAfterCompletionAsync(host, completedHead, stateCancellation.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && liveTurn.StopRequested) {
                // The Action is committed. A later completed turn can supply state again.
            }
        }
        GalateaDurableReplyLeaseReconcileResult leaseSettlement =
            ReconcileDurableDeliveries(host, ct);
        if (leaseSettlement is GalateaDurableReplyLeaseReconcileResult
                .Retained) {
            throw new GalateaTurnException(
                "Durable reply settlement still requires recovery.",
                "delegation-reply-lease-retained"
            );
        }
        using (var extractionCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, liveTurn.StopController.UserStopToken)) {
            try {
                await ReconcilePostCompletionExtractionsAsync(host, completedHead, extractionCancellation.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && liveTurn.StopRequested) {
                // The Action is already durable. Stop only releases this
                // process-local extraction attempt; reconciliation retains
                // its original durable target for the next admission.
            }
        }
        RecentTurnsResponseDto? snapshot =
            await RefreshRecentTurnsForCompletedStreamAsync(
                host,
                ct
            )
            .ConfigureAwait(false);
        DebugUtil.Debug(
            "Galatea.Session",
            $"RunTurnAsync send done: character={host.Character.CharacterId}, turnId={liveTurn.TurnId}, errors={completed.Errors?.Count ?? 0}, snapshotTurns={snapshot?.Turns.Count.ToString(CultureInfo.InvariantCulture) ?? "unavailable"}, head={host.Engine.ReadCurrentHead()}"
        );
        liveTurn.PublishDone(snapshot);
    }

    private async ValueTask<
        GalateaOutboundMailExtractionReconcileResult>
        ReconcileOutboundMailExtractionAsync(
        CharacterSessionHost host,
        CancellationToken cancellationToken,
        GalateaTerminalActionExtractionTarget? target = null
    ) {
        try {
            GalateaOutboundMailExtractionReconcileResult result =
                target is null
                    ? await host.OutboundMailExtractionReconciler
                        .ReconcileAsync(
                            host.Engine,
                            cancellationToken
                        )
                        .ConfigureAwait(false)
                    : await host.OutboundMailExtractionReconciler
                        .ReconcileTargetAsync(
                            host.Engine,
                            target,
                            cancellationToken
                        )
                        .ConfigureAwait(false);
            if (result is GalateaOutboundMailExtractionReconcileResult
                    .SelectedHeadChanged) {
                throw new GalateaTurnException(
                    "Durable extraction head changed; retry admission.",
                    "delegation-state-changed"
                );
            }
            if (result is GalateaOutboundMailExtractionReconcileResult.Captured) {
                _ = host.DelegationHandle?.Signal();
                _characterMailRelay?.Signal();
            }
            return result;
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested) {
            throw;
        }
        catch (GalateaTurnException) {
            throw;
        }
        catch (GalateaOutboundMailExtractionReadException exception) {
            throw exception.Kind switch {
                GalateaOutboundMailExtractionReadFailureKind.LimitExceeded =>
                    new GalateaTurnException(
                        "Durable extraction exceeded its read bound.",
                        "delegation-proof-limit-exceeded"
                    ),
                GalateaOutboundMailExtractionReadFailureKind.UnsupportedSchema =>
                    new GalateaTurnException(
                        "Durable extraction uses an unsupported schema.",
                        "delegation-session-schema-unsupported"
                    ),
                GalateaOutboundMailExtractionReadFailureKind.Corruption =>
                    new GalateaTurnException(
                        "Durable extraction evidence is invalid.",
                        "delegation-state-invalid"
                    ),
                _ => new GalateaTurnException(
                    "Durable extraction is unavailable.",
                    "delegation-extraction-unavailable"
                )
            };
        }
        catch (GalateaDelegationStoreConflictException exception) {
            throw new GalateaTurnException(
                "Durable delegation state changed; retry admission.",
                "delegation-state-changed",
                exception
            );
        }
        catch (Exception exception) when (
            GalateaExceptionClassifier.IsNonFatal(exception)) {
            string reason = exception is
                    GalateaOutboundMailExtractionCaptureMismatchException
                    or InvalidDataException
                ? "delegation-state-invalid"
                : "delegation-extraction-unavailable";
            throw new GalateaTurnException(
                reason == "delegation-state-invalid"
                    ? "Durable extraction evidence is invalid."
                    : "Durable outbound mail extraction is temporarily unavailable.",
                reason,
                exception
            );
        }
    }

    private async ValueTask ReconcilePostCompletionExtractionsAsync(
        CharacterSessionHost host,
        EventAddress completedHead,
        CancellationToken callerToken
    ) {
        GalateaTerminalActionExtractionReadResult read =
            GalateaTerminalActionExtractionTargetReader.ReadAt(
                host.Engine,
                completedHead,
                callerToken
            );
        GalateaTerminalActionExtractionTarget target = read switch {
            GalateaTerminalActionExtractionReadResult.Available available =>
                available.Target,
            GalateaTerminalActionExtractionReadResult
                    .NoTerminalActionAtHead => throw new InvalidDataException(
                "A completed Galatea operation must end at its terminal Action."
            ),
            GalateaTerminalActionExtractionReadResult.Failed failed =>
                throw CreateTerminalActionReadFailure(failed),
            _ => throw new InvalidDataException(
                "Unknown terminal Action extraction read result."
            )
        };

        CharacterNoteDefaultPodReconciler? memory =
            host.CharacterMemoryReconciler;
        if (memory is null) {
            _ = await ReconcileOutboundMailExtractionAsync(
                    host,
                    callerToken,
                    target
                )
                .ConfigureAwait(false);
            return;
        }
        using var derivedInfoSignal = new PostCompletionDerivedInfoSignal(
            host.CharacterNoteDerivedInfoPump
        );

        using var deadlineCts = CreateCharacterNoteTestDeadline();
        using var mailAbortCts = new CancellationTokenSource();
        using var noteCts = CancellationTokenSource.CreateLinkedTokenSource(
            callerToken,
            deadlineCts.Token,
            mailAbortCts.Token
        );
        long batchStarted = Stopwatch.GetTimestamp();
        long mailMilliseconds = -1;
        long noteMilliseconds = -1;
        Task<GalateaOutboundMailExtractionReconcileResult> mailTask =
            MeasureAsync(
                () => ReconcileOutboundMailExtractionAsync(
                    host,
                    callerToken,
                    target
                ),
                value => mailMilliseconds = value
            );
        Task<CharacterNoteDefaultPodReconcileResult> noteTask =
            MeasureAsync(
                () => memory.ReconcileTargetAsync(
                    host.Engine,
                    target,
                    noteCts.Token
                ),
                value => noteMilliseconds = value
            );

        GalateaOutboundMailExtractionReconcileResult? mailResult = null;
        Exception? mailFailure = null;
        Exception? cancellationFailure = null;
        bool noteCompletedBeforeMailAbort = false;
        try {
            mailResult = await mailTask.ConfigureAwait(false);
        }
        catch (Exception exception) {
            noteCompletedBeforeMailAbort = noteTask.IsCompleted;
            mailFailure = exception;
            cancellationFailure = TryCancel(mailAbortCts);
        }

        CharacterNoteDefaultPodReconcileResult? noteResult = null;
        Exception? noteFailure = null;
        try {
            noteResult = await noteTask.ConfigureAwait(false);
        }
        catch (Exception exception) {
            noteFailure = exception;
        }

        var failures = new CharacterNoteFailureSet(
            mailFailure,
            noteFailure,
            cancellationFailure,
            mailAbortCts.IsCancellationRequested,
            noteCompletedBeforeMailAbort,
            deadlineCts.IsCancellationRequested,
            callerToken.IsCancellationRequested
        );
        failures.ThrowIfFatal();
        failures.ThrowIfCallerCanceled(callerToken);

        string mailOutcome = mailResult is null
            ? "failure-" + mailFailure!.GetType().Name
            : DescribeMailOutcome(mailResult);
        if (failures.EffectiveNoteFailure is { } effectiveNoteFailure) {
            if (!IsBestEffortPreCaptureFailure(
                    effectiveNoteFailure,
                    deadlineCts.IsCancellationRequested,
                    mailAbortCts.IsCancellationRequested
                )) {
                LogCharacterNoteBatch(
                    host,
                    target,
                    mailOutcome,
                    noteOutcome: "fail-closed-"
                        + DescribeNoteFailure(effectiveNoteFailure),
                    durableMemo: false,
                    memoCount: 0,
                    receiptOutcome: "none",
                    mailMilliseconds,
                    noteMilliseconds,
                    ElapsedMilliseconds(batchStarted)
                );
                failures.ThrowNotePrimary(
                    CreateCharacterNoteFailClosed(effectiveNoteFailure)
                );
            }

            LogCharacterNoteBatch(
                host,
                target,
                mailOutcome,
                noteOutcome: DescribeNoteFailure(effectiveNoteFailure),
                durableMemo: false,
                memoCount: 0,
                receiptOutcome: "none",
                mailMilliseconds,
                noteMilliseconds,
                ElapsedMilliseconds(batchStarted)
            );
            if (mailFailure is not null) {
                failures.ThrowMailPrimary();
            }
            return;
        }
        if (failures.NoteCancellationInducedByMailAbort) {
            LogCharacterNoteBatch(
                host,
                target,
                mailOutcome,
                noteOutcome: "pre-capture-mail-abort",
                durableMemo: false,
                memoCount: 0,
                receiptOutcome: "none",
                mailMilliseconds,
                noteMilliseconds,
                ElapsedMilliseconds(batchStarted)
            );
            failures.ThrowMailPrimary();
        }

        if (noteResult is null) {
            failures.ThrowAuthorityPrimary(new GalateaTurnException(
                "Character Note reconciliation completed without a result.",
                "character-memory-state-invalid"
            ));
        }
        CharacterNoteDefaultPodReconcileResult settled = noteResult;
        if (settled is CharacterNoteDefaultPodReconcileResult.Quarantined
                quarantined) {
            failures.ThrowAuthorityPrimary(new GalateaTurnException(
                "Character Memory authority is quarantined.",
                "character-memory-quarantined",
                new InvalidDataException(quarantined.Code)
            ));
        }
        if (settled is CharacterNoteDefaultPodReconcileResult
                .SelectedHeadChanged) {
            LogCharacterNoteBatch(
                host,
                target,
                mailOutcome,
                noteOutcome: "selected-head-changed",
                durableMemo: false,
                memoCount: 0,
                receiptOutcome: "head-changed",
                mailMilliseconds,
                noteMilliseconds,
                ElapsedMilliseconds(batchStarted)
            );
            failures.ThrowAuthorityPrimary(new GalateaTurnException(
                "Durable extraction head changed; retry admission.",
                "delegation-state-changed"
            ));
        }

        if (settled is CharacterNoteDefaultPodReconcileResult.AppliedNow
                applied) {
            if (applied.SourceAction != target.SourceAction) {
                failures.ThrowAuthorityPrimary(new GalateaTurnException(
                    "Character Note AppliedNow source does not match its target.",
                    "character-memory-state-invalid"
                ));
            }
            LogCharacterNoteMemos(host, target, applied.Memos);
            EventAddress? observedHead = host.Engine.ReadCurrentHead();
            if (observedHead != target.SourceAction) {
                LogCharacterNoteBatch(
                    host,
                    target,
                    mailOutcome,
                    noteOutcome: "applied-now",
                    durableMemo: true,
                    memoCount: applied.Memos.Count,
                    receiptOutcome: "head-changed",
                    mailMilliseconds,
                    noteMilliseconds,
                    ElapsedMilliseconds(batchStarted)
                );
                failures.ThrowAuthorityPrimary(new GalateaTurnException(
                    "Durable extraction head changed; retry admission.",
                    "delegation-state-changed"
                ));
            }
            // Notification eligibility was committed atomically with Applied,
            // including admission/recovery and late cancellation paths. Runtime
            // must not create another in-process or durable issuance authority.
            LogCharacterNoteBatch(
                host,
                target,
                mailOutcome,
                noteOutcome: "applied-now",
                durableMemo: true,
                memoCount: applied.Memos.Count,
                receiptOutcome: "durable-pending",
                mailMilliseconds,
                noteMilliseconds,
                ElapsedMilliseconds(batchStarted)
            );
        }
        else {
            LogCharacterNoteBatch(
                host,
                target,
                mailOutcome,
                DescribeCharacterNoteOutcome(settled),
                durableMemo: false,
                memoCount: 0,
                receiptOutcome: "none",
                mailMilliseconds,
                noteMilliseconds,
                ElapsedMilliseconds(batchStarted)
            );
        }

        if (mailFailure is not null) {
            failures.ThrowMailPrimary();
        }
    }

    private sealed class PostCompletionDerivedInfoSignal(
        CharacterNoteDerivedInfoPump? pump
    ) : IDisposable {
        public void Dispose() => _ = pump?.Signal();
    }

    private static bool IsBestEffortPreCaptureFailure(
        Exception exception,
        bool deadlineExpired,
        bool mailAborted
    ) => exception switch {
        OperationCanceledException when deadlineExpired || mailAborted => true,
        TextExtractionException => true,
        CharacterNoteDefaultPodAccessException access when access.Kind is
            CharacterNoteDefaultPodFailureKind.NotFound
                or CharacterNoteDefaultPodFailureKind.IoFailure => true,
        _ => false,
    };

    private static string DescribeCharacterNoteOutcome(
        CharacterNoteDefaultPodReconcileResult result
    ) => result switch {
        CharacterNoteDefaultPodReconcileResult.BaselineCovered =>
            "baseline-covered",
        CharacterNoteDefaultPodReconcileResult.ZeroCaptured =>
            "zero-captured",
        CharacterNoteDefaultPodReconcileResult.AppliedNow => "applied-now",
        CharacterNoteDefaultPodReconcileResult.AlreadyApplied =>
            "already-applied",
        CharacterNoteDefaultPodReconcileResult.Rejected rejected =>
            "rejected-" + rejected.Code,
        CharacterNoteDefaultPodReconcileResult.DeferredAfterCapture
                deferred =>
            "deferred-after-capture-" + deferred.Code,
        CharacterNoteDefaultPodReconcileResult.Quarantined quarantined =>
            "quarantined-" + quarantined.Code,
        CharacterNoteDefaultPodReconcileResult.SelectedHeadChanged =>
            "selected-head-changed",
        _ => result.GetType().Name,
    };

    private static GalateaTurnException CreateCharacterNoteFailClosed(
        Exception failure
    ) => new(
            "Character Memory reconciliation violated its durable boundary.",
            "character-memory-state-invalid",
            failure
        );

    private static async Task<T> MeasureAsync<T>(
        Func<ValueTask<T>> operation,
        Action<long> recordElapsedMilliseconds
    ) {
        long started = Stopwatch.GetTimestamp();
        try {
            return await operation().ConfigureAwait(false);
        }
        finally {
            recordElapsedMilliseconds(ElapsedMilliseconds(started));
        }
    }

    private static GalateaTurnException CreateTerminalActionReadFailure(
        GalateaTerminalActionExtractionReadResult.Failed failure
    ) => failure.Kind switch {
        GalateaTerminalActionExtractionReadFailureKind.LimitExceeded => new(
            "Durable extraction exceeded its read bound.",
            "delegation-proof-limit-exceeded"
        ),
        GalateaTerminalActionExtractionReadFailureKind.UnsupportedSchema =>
            new(
                "Durable extraction uses an unsupported schema.",
                "delegation-session-schema-unsupported"
            ),
        GalateaTerminalActionExtractionReadFailureKind.Corruption => new(
            "Durable extraction evidence is invalid.",
            "delegation-state-invalid"
        ),
        _ => new GalateaTurnException(
            "Durable extraction is unavailable.",
            "delegation-extraction-unavailable"
        )
    };

    private static string DescribeMailOutcome(
        GalateaOutboundMailExtractionReconcileResult result
    ) => result switch {
        GalateaOutboundMailExtractionReconcileResult.BaselineCovered =>
            "baseline-covered",
        GalateaOutboundMailExtractionReconcileResult.AlreadyCaptured =>
            "already-captured",
        GalateaOutboundMailExtractionReconcileResult.Captured => "captured",
        _ => result.GetType().Name
    };

    private static string DescribeNoteFailure(Exception exception) =>
        exception is TextExtractionException extraction
            ? "text-extraction-" + extraction.Kind.ToString()
            : "exception-" + exception.GetType().Name;

    private static long ElapsedMilliseconds(long started) => checked((long)
        Stopwatch.GetElapsedTime(started).TotalMilliseconds);

    [Conditional("DEBUG")]
    private void LogCharacterNoteMemos(
        CharacterSessionHost host,
        GalateaTerminalActionExtractionTarget target,
        IReadOnlyList<CharacterNoteAppliedMemo> memos
    ) {
        for (int index = 0; index < memos.Count; index++) {
            CharacterNoteAppliedMemo memo = memos[index];
            WriteCharacterNoteDiagnostic(
                JsonSerializer.Serialize(new {
                    @event = "character-note-durable-memo",
                    developmentOnly = false,
                    durableMemo = true,
                    characterId = host.Character.CharacterId,
                    sourceAction = EventAddressTextCodec.Format(
                        target.SourceAction
                    ),
                    currentExtractorContractId =
                        host.CharacterNoteExtractor.ContractId,
                    index,
                    artifactOrdinal = memo.ArtifactOrdinal,
                    podId = memo.PodId.Value,
                    memoId = memo.MemoId.Value,
                    exactText = memo.ExactText,
                })
            );
        }
    }

    [Conditional("DEBUG")]
    private void LogCharacterNoteBatch(
        CharacterSessionHost host,
        GalateaTerminalActionExtractionTarget target,
        string mailOutcome,
        string noteOutcome,
        bool durableMemo,
        int memoCount,
        string receiptOutcome,
        long mailMilliseconds,
        long noteMilliseconds,
        long batchMilliseconds
    ) => WriteCharacterNoteDiagnostic(
        JsonSerializer.Serialize(new {
            @event = "character-note-extraction-batch",
            developmentOnly = false,
            durableMemo,
            characterId = host.Character.CharacterId,
            sourceAction = EventAddressTextCodec.Format(target.SourceAction),
            visibleActionSha256 = target.VisibleTextSha256,
            visibleActionUtf8Bytes = target.VisibleTextUtf8Bytes,
            currentExtractorContractId =
                host.CharacterNoteExtractor.ContractId,
            mailOutcome,
            noteOutcome,
            memoCount,
            receiptOutcome,
            mailMs = mailMilliseconds,
            noteMs = noteMilliseconds,
            batchMs = batchMilliseconds,
        })
    );

    [Conditional("DEBUG")]
    private void WriteCharacterNoteDiagnostic(string serializedJson) {
        ArgumentNullException.ThrowIfNull(serializedJson);
        CharacterNoteDiagnosticSinkForTest?.Invoke(serializedJson);
        DebugUtil.Debug("Galatea.CharacterMemory", serializedJson);
    }

    private async Task<GalateaCompletedOperation> RunFreshSendAsync(
        CharacterSessionHost host,
        GalateaLiveTurn liveTurn,
        CompletionStreamObserver observer,
        CancellationToken cancellationToken
    ) {
        RequireCurrentConnectionSelectable(host.Character,
            liveTurn.Options.ConnectionId
        );
        SessionRuntimeRecoveryRequirements requirement =
            host.Engine.InspectRuntimeRecoveryRequirements(
                cancellationToken
            );
        if (requirement is not SessionRuntimeRecoveryRequirements
                .NoRuntimeRequired
            || requirement.Phase != SessionExecutionPhase.Idle
            || requirement.CapturedHead is not { } capturedHead) {
            throw RecoveryRequired(requirement);
        }

        return await RunRecapGridFreshSendAsync(
                host,
                liveTurn,
                observer,
                capturedHead,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<GalateaCompletedOperation> RunRecoveryAsync(
        CharacterSessionHost host,
        GalateaLiveTurn liveTurn,
        CompletionStreamObserver observer,
        CancellationToken cancellationToken
    ) {
        WriteMemoRecallDiagnostic(GalateaMemoRecallDiagnostic.NotScheduled(
            GalateaMemoRecallNotScheduledReason.Recovery
        ));
        SessionRuntimeRecoveryRequirements requirement =
            host.Engine.InspectRuntimeRecoveryRequirements(
                cancellationToken
            );
        EventAddress capturedHead = liveTurn.Options.ExpectedHead
            ?? throw new InvalidDataException(
                "Recovery live turn requires an expected raw head."
            );
        if (requirement.CapturedHead != capturedHead) {
            throw new GalateaTurnException(
                "会话边界已变化，请刷新后重新确认恢复。",
                "stale-session-head"
            );
        }

        return await RunRecapGridRecoveryAsync(
                host,
                liveTurn,
                observer,
                requirement,
                capturedHead,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() {
        BeginShutdown();
        lock (_lifecycleGate) {
            return new ValueTask(_disposeTask ??= Task.Run(DisposeCoreAsync));
        }
    }

    private async Task DisposeCoreAsync() {
        List<Exception>? failures = null;
        if (_characterMailRelay is { } relay) {
            try { await relay.DrainAsync().ConfigureAwait(false); }
            catch (Exception exception) { (failures ??= []).Add(exception); }
        }
        if (_turnRunner is { } runner) {
            try { await runner.DrainAsync().ConfigureAwait(false); }
            catch (Exception exception) { (failures ??= []).Add(exception); }
        }
        int sessionIndex = 0;
        foreach (var entry in _sessions.Values) {
            try {
                var session = await entry.Value.ConfigureAwait(false);
                await session.DisposeAsync().ConfigureAwait(false);
                DisposeHooksForTest?.AfterSessionDisposed?.Invoke(
                    sessionIndex);
            }
            catch (OperationCanceledException) when (IsStopping) { }
            catch (Exception exception) {
                (failures ??= []).Add(exception);
            }
            sessionIndex++;
        }
        try {
            await _delegationSupervisor.DisposeAsync()
                .ConfigureAwait(false);
            DisposeHooksForTest?.AfterDelegationSupervisorDisposed?.Invoke();
        }
        catch (Exception exception) {
            (failures ??= []).Add(exception);
        }
        try {
            if (_completionOwner is not null) {
                await _completionOwner.DisposeAsync().ConfigureAwait(false);
            }
            else {
                await _recapGrid.DisposeAsync().ConfigureAwait(false);
            }
            DisposeHooksForTest?.AfterRecapGridDisposed?.Invoke();
        }
        catch (Exception exception) {
            (failures ??= []).Add(exception);
        }
        Exception? fatal = failures?.FirstOrDefault(static exception =>
            !GalateaExceptionClassifier.IsNonFatal(exception));
        if (fatal is not null) { ExceptionDispatchInfo.Capture(fatal).Throw(); }
        if (failures is { Count: 1 }) {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }
        if (failures is { Count: > 1 }) {
            throw new AggregateException(failures);
        }
    }

    internal sealed record GalateaDisposeTestHooks(
        Action<int>? AfterSessionDisposed = null,
        Action? AfterRecapGridDisposed = null,
        Action? AfterDelegationSupervisorDisposed = null
    );

    internal void BeginShutdown() {
        GalateaAcceptedTurnRunner? runner;
        GalateaCharacterMailRelay? characterMailRelay;
        lock (_lifecycleGate) {
            _stopping = true;
            runner = _turnRunner;
            characterMailRelay = _characterMailRelay;
        }
        characterMailRelay?.BeginShutdown();
        _admissionStopping.Cancel();
        runner?.BeginShutdown();
        _delegationSupervisor.BeginShutdown();
    }

    private async Task<GalateaCompletedOperation>
        RunRecapGridFreshSendAsync(
        CharacterSessionHost host,
        GalateaLiveTurn liveTurn,
        CompletionStreamObserver observer,
        EventAddress capturedHead,
        CancellationToken cancellationToken
    ) {
        GalateaRecapGridComposition recapGrid = _recapGrid;
        CompletionConnectionConfig inspected =
            recapGrid.InspectConnectionExact(liveTurn.Options.ConnectionId);
        SessionDesiredSetupReconciliationResult reconciled =
            host.Engine.ReconcileDesiredSetup(
                capturedHead,
                new SessionDesiredSetup(
                    inspected.ModelId,
                    inspected.CompletionSurfaceId,
                    host.Character.SystemPrompt),
                cancellationToken);
        if (reconciled is not SessionDesiredSetupReconciliationResult
                .Ready ready) {
            throw new GalateaTurnException(
                "RecapGrid会话设置无法在当前边界安全更新。",
                "recap-grid-desired-setup-unavailable");
        }
        GalateaFreshInput fresh = liveTurn.FreshInput
            ?? throw new InvalidOperationException("Fresh send requires typed input.");
        DateTimeOffset observationTimestamp = PlayerTurnObservationEnvelope.TruncateToSecond(_timeProvider.GetLocalNow());
        var characterSnapshot = new GalateaSenderSnapshot("character", host.Character.CharacterId, host.Character.CharacterName.Value);
        PlayerTurnObservation? preliminaryPlayerObservation = fresh switch {
            GalateaFreshInput.PlayerAction player => new PlayerTurnObservation(player.Text, observationTimestamp, player.Notices),
            GalateaFreshInput.DelegateReply reply => PlayerTurnObservation.CreateDelegateReply(observationTimestamp, reply.Notices),
            GalateaFreshInput.HeartbeatActivation activation => PlayerTurnObservation.CreateHeartbeatActivation(
                observationTimestamp, activation.CharacterName,
                intervalMinutes: activation.IntervalMinutes),
            GalateaFreshInput.InboundMail => null,
            _ => throw new InvalidOperationException("Unknown fresh input kind.")
        };
        IReadOnlyList<PlayerTurnNotice> notices = preliminaryPlayerObservation?.Notices ?? [];
        CharacterNoteReceiptDeliverySnapshot? receiptDelivery = preliminaryPlayerObservation is null
            ? null : host.CharacterMemoryReconciler?.ReadPendingReceiptDelivery();
        if (receiptDelivery is not null) {
            PlayerTurnNotice.NoteSaveReceipt selectedReceipt = CharacterNoteSaveReceipt.SelectForObservation(
                receiptDelivery,
                receipt => FitsStructuredObservation(fresh, observationTimestamp, characterSnapshot, [.. notices, receipt], [], liveTurn.Options.ConnectionState));
            notices = [.. notices, selectedReceipt];
            preliminaryPlayerObservation = preliminaryPlayerObservation!.WithNotices(notices);
        }
        SessionInputContent prompted = GalateaObservationContent.Create(fresh,
            observationTimestamp, characterSnapshot, notices, [], liveTurn.Options.ConnectionState);
        await using GalateaRecapGridTurn turn =
            await recapGrid.OpenFreshAsync(
                host.Engine,
                liveTurn.Options.ConnectionId,
                prompted,
                host.DefaultPolicy,
                cancellationToken).ConfigureAwait(false);
        RecapGridOnlineContextHandle online = turn.Online
            ?? throw new InvalidDataException(
                "Fresh RecapGrid binding has no Online context.");
        var lifecycle = new GalateaFreshSendLifecycleGate(
            online.Lifecycle,
            liveTurn.StopController);
        host.Engine.UseRuntime(CreateRecapGridRuntime(
            turn,
            online.CandidateSource,
            lifecycle,
            liveTurn));
        IGalateaPlayerTurnRecallProvider recallProvider =
            host.PlayerTurnRecallProvider;
        if (preliminaryPlayerObservation is null) {
            WriteMemoRecallDiagnostic(
                GalateaMemoRecallDiagnostic.NotScheduled(
                    GalateaMemoRecallNotScheduledReason.UnsupportedTrigger
                )
            );
        }
        else if (recallProvider
                is DisabledGalateaPlayerTurnRecallProvider) {
            WriteMemoRecallDiagnostic(
                GalateaMemoRecallDiagnostic.NotScheduled(
                    _maintenanceMode
                        ? GalateaMemoRecallNotScheduledReason.MaintenanceMode
                        : GalateaMemoRecallNotScheduledReason.ProviderDisabled
                )
            );
        }
        else {
            GalateaPlayerTurnRecallContext recallContext;
            try {
                recallContext = await BuildCurrentRecallContextAsync(
                    host,
                    online.CandidateSource,
                    ready.GoverningSetup,
                    turn.RawHistoryAuthorized,
                    cancellationToken
                ).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException
                && GalateaExceptionClassifier.IsNonFatal(exception)
            ) {
                WriteMemoRecallDiagnostic(
                    GalateaMemoRecallDiagnostic.Failed(
                        GalateaMemoRecallFailureStage.ContextConstruction,
                        GalateaMemoRecallFailureClassifier.Classify(exception)
                    )
                );
                throw;
            }
            IReadOnlyList<PlayerTurnRecall> recalls =
                await SelectPlayerTurnRecallsAsync(
                    host,
                    recallProvider,
                    ready.GoverningSetup.Head,
                    preliminaryPlayerObservation,
                    recallContext,
                    cancellationToken,
                    candidates => FitsStructuredObservation(fresh, observationTimestamp, characterSnapshot, notices, candidates, liveTurn.Options.ConnectionState),
                    prompted
                ).ConfigureAwait(false);
            if (recalls.Count > 0) {
                prompted = GalateaObservationContent.Create(fresh,
                    observationTimestamp, characterSnapshot, notices, recalls, liveTurn.Options.ConnectionState);
            }
        }
        _ = liveTurn.DurableReplyLease?.BindObservationBase(
            host.Engine,
            ready.GoverningSetup.Head,
            prompted
        );
        if (liveTurn.FreshInput is GalateaFreshInput.InboundMail {
                InternalDelivery: { } internalDelivery
            }) {
            internalDelivery.BindObservationBase(
                host.Engine,
                ready.GoverningSetup.Head,
                prompted
            );
        }
        if (receiptDelivery is not null) {
            GalateaNoteReceiptDelivery.Bind(
                host.CharacterMemoryReconciler!, host.Engine,
                receiptDelivery, ready.GoverningSetup.Head, prompted);
        }
        TurnResult result;
        try {
            result = await host.Engine.SendAsync(
                ready.GoverningSetup.Head,
                prompted,
                observer,
                cancellationToken).ConfigureAwait(false);
        }
        finally {
            ConfirmConnectionChangeDelivery(host, liveTurn.Options.ConnectionState?.LastChange,
                ready.GoverningSetup.Head, prompted);
        }
        return new GalateaCompletedOperation(
            result.Message,
            result.Invocation,
            result.Errors);
    }

    private static bool FitsStructuredObservation(
        GalateaFreshInput fresh,
        DateTimeOffset timestamp,
        GalateaSenderSnapshot character,
        IReadOnlyList<PlayerTurnNotice> notices,
        IReadOnlyList<PlayerTurnRecall> recalls,
        GalateaConnectionStateSnapshot? connectionState = null
    ) {
        try {
            _ = GalateaObservationContent.Create(fresh, timestamp, character, notices, recalls, connectionState);
            return true;
        }
        catch (ArgumentOutOfRangeException) {
            return false;
        }
    }

    private async ValueTask<IReadOnlyList<PlayerTurnRecall>>
        SelectPlayerTurnRecallsAsync(
        CharacterSessionHost host,
        IGalateaPlayerTurnRecallProvider recallProvider,
        EventAddress completionBoundary,
        PlayerTurnObservation currentObservation,
        GalateaPlayerTurnRecallContext context,
        CancellationToken cancellationToken,
        Func<IReadOnlyList<PlayerTurnRecall>, bool>? fitsRecalls = null,
        SessionInputContent? currentInput = null
    ) {
        GalateaPlayerTurnRecallRequest request = new(
            host.Character,
            completionBoundary,
            currentObservation,
            context,
            fitsRecalls,
            currentInput
        );
        IReadOnlyList<PlayerTurnRecall> selected;
        GalateaMemoRecallPlanningResult? planning = null;
        try {
            if (recallProvider
                    is IGalateaPlayerTurnRecallPlanningProvider planner) {
                planning = await planner
                    .PlanRecallsAsync(request, cancellationToken)
                    .ConfigureAwait(false);
                if (planning is null) {
                    throw InvalidRecallProviderResult(
                        "Galatea player-turn recall planner returned null."
                    );
                }
                selected = planning.Recalls;
            }
            else {
                selected = await recallProvider
                    .SelectRecallsAsync(request, cancellationToken)
                    .ConfigureAwait(false);
                if (selected is null) {
                    throw InvalidRecallProviderResult(
                        "Galatea player-turn recall provider returned null."
                    );
                }
            }
        }
        catch (GalateaMemoRecallStageException exception) {
            WriteMemoRecallDiagnostic(GalateaMemoRecallDiagnostic.Failed(
                exception.Stage,
                exception.FailureKind
            ));
            throw new GalateaTurnException(
                "Memo recall failed before main completion.",
                "memo-recall-failed",
                exception.InnerException ?? exception
            );
        }
        catch (Exception exception) when (exception is not OperationCanceledException
            && GalateaExceptionClassifier.IsNonFatal(exception)) {
            WriteMemoRecallDiagnostic(GalateaMemoRecallDiagnostic.Failed(
                GalateaMemoRecallFailureStage.SelectorExecution,
                GalateaMemoRecallFailureClassifier.Classify(exception)
            ));
            throw new GalateaTurnException(
                "Memo recall failed before main completion.",
                "memo-recall-failed",
                exception
            );
        }
        IReadOnlyList<PlayerTurnRecall> frozen;
        try {
            frozen = Array.AsReadOnly(selected.Select(static recall =>
                recall ?? throw new InvalidOperationException(
                    "Galatea player-turn recall provider returned a null recall."
                )
            ).ToArray());
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException
            && GalateaExceptionClassifier.IsNonFatal(exception)
        ) {
            WriteMemoRecallDiagnostic(GalateaMemoRecallDiagnostic.Failed(
                GalateaMemoRecallFailureStage.ProviderResultValidation,
                GalateaMemoRecallFailureClassifier.Classify(exception)
            ));
            throw;
        }
        if (planning is not null) {
            WriteMemoRecallDiagnostic(
                GalateaMemoRecallDiagnostic.Completed(planning)
            );
        }
        return frozen;
    }

    private static GalateaMemoRecallStageException
        InvalidRecallProviderResult(string message) => new(
            GalateaMemoRecallFailureStage.ProviderResultValidation,
            GalateaMemoRecallFailureKind.ContractViolation,
            new InvalidOperationException(message)
        );

    private void WriteMemoRecallDiagnostic(
        GalateaMemoRecallDiagnostic diagnostic
    ) {
        string serialized;
        try {
            serialized = GalateaMemoRecallDiagnosticRenderer.Render(
                diagnostic
            );
        }
        catch {
            return;
        }

        try {
            MemoRecallDiagnosticSinkForTest?.Invoke(serialized);
        }
        catch {
            // Diagnostics must never affect recall or turn recovery.
        }

        try {
            DebugUtil.Debug("Galatea.MemoRecall", serialized);
        }
        catch {
            // Diagnostics must never affect recall or turn recovery.
        }
    }

    private async ValueTask<GalateaPlayerTurnRecallContext>
        BuildCurrentRecallContextAsync(
        CharacterSessionHost host,
        ICoherentContextCandidateSource candidates,
        SessionGoverningSetup governingSetup,
        bool allowMatureRawHistory,
        CancellationToken cancellationToken
    ) {
        var request = new SessionContextSelectionRequest(
            governingSetup.Head,
            governingSetup.RuntimeConfig.DerivedContext.NthPrevious
        );
        request.ValidateShape();
        SessionContextCandidateSelection selection = await candidates
            .SelectAsync(request, cancellationToken)
            .ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(selection);
        selection.ValidateShape();
        SessionHistoryPlanningWindow window = selection.Status switch {
            SessionContextCandidateSelectionStatus.EmptyLineage =>
                host.Engine.ReadHistoryPlanningWindowAt(
                    governingSetup.Head,
                    startExclusive: null,
                    cancellationToken
                ),
            SessionContextCandidateSelectionStatus.RawHistoryAuthorized =>
                allowMatureRawHistory
                    ? host.Engine.ReadHistoryPlanningWindowAt(
                        governingSetup.Head,
                        startExclusive: null,
                        cancellationToken
                    )
                    : throw new GalateaTurnException(
                        "RecallBarrier需要的成熟raw history缺少同轮授权。",
                        "recall-barrier-raw-history-unauthorized"
                    ),
            SessionContextCandidateSelectionStatus.Selected =>
                await MaterializeRecallContextWindowAsync(
                    host,
                    candidates,
                    governingSetup.Head,
                    selection.Candidate
                        ?? throw new InvalidDataException(
                            "Selected context candidate has no descriptor."
                        ),
                    cancellationToken
                ).ConfigureAwait(false),
            SessionContextCandidateSelectionStatus.OrdinalUnavailable =>
                throw new GalateaTurnException(
                    "RecallBarrier需要的上下文候选序号不可用。",
                    "recall-barrier-context-unavailable"
                ),
            SessionContextCandidateSelectionStatus.ExactPublishedSetInvalid =>
                throw new GalateaTurnException(
                    selection.Detail
                    ?? "RecallBarrier需要的RecapGrid发布集合无效。",
                    "recall-barrier-context-invalid"
                ),
            SessionContextCandidateSelectionStatus.StoreUnavailable =>
                throw new GalateaTurnException(
                    selection.Detail
                    ?? "RecallBarrier需要的RecapGrid store不可用。",
                    "recall-barrier-context-store-unavailable"
                ),
            SessionContextCandidateSelectionStatus.BeyondPrefix =>
                throw new GalateaTurnException(
                    selection.Detail
                    ?? "RecallBarrier需要的上下文锚点超出有界lineage前缀。",
                    "recall-barrier-context-unavailable"
                ),
            _ => throw new InvalidDataException(
                "Unknown context candidate selection status."
            )
        };
        var recentVisibleActions = new List<GalateaRecentVisibleAction>(1);
        for (int index = window.Units.Count - 1; index >= 0; index--) {
            if (window.Units[index].Message is not ActionMessage action) {
                continue;
            }
            string visible = GalateaVisibleActionTextRenderer.Render(action);
            if (string.IsNullOrWhiteSpace(visible)) {
                continue;
            }
            recentVisibleActions.Add(new GalateaRecentVisibleAction(visible,
                window.Units[index].SourceStartInclusive, window.Units[index].SourceEndInclusive));
            break;
        }
        return new GalateaPlayerTurnRecallContext(
            GalateaRecallBarrierBuilder.BuildFromProviderVisibleMessages(
                window.Units.Select(static unit => unit.Message)
            ),
            GalateaCharacterNoteOriginBarrierBuilder
                .BuildFromProviderVisibleRawUnits(
                    window.Units,
                    host.CharacterMemoryReconciler,
                    cancellationToken
                ),
            recentVisibleActions
        );
    }

    private static async ValueTask<SessionHistoryPlanningWindow>
        MaterializeRecallContextWindowAsync(
        CharacterSessionHost host,
        ICoherentContextCandidateSource candidates,
        EventAddress completionBoundary,
        SessionContextCandidateDescriptor descriptor,
        CancellationToken cancellationToken
    ) {
        SessionContextCandidateMaterializationResult materialization =
            await candidates
                .MaterializeAsync(descriptor, cancellationToken)
                .ConfigureAwait(false);
        SessionContextCandidate candidate = materialization switch {
            SessionContextCandidateMaterializationResult.Materialized value
                when value.Candidate is not null => value.Candidate,
            SessionContextCandidateMaterializationResult.Stale stale
                => throw new GalateaTurnException(
                    RequireContextMaterializationDetail(stale.Detail),
                    "recall-barrier-context-unavailable"
                ),
            SessionContextCandidateMaterializationResult.Busy busy
                => throw new GalateaTurnException(
                    RequireContextMaterializationDetail(busy.Detail),
                    "recall-barrier-context-store-unavailable"
                ),
            SessionContextCandidateMaterializationResult.Disposed disposed
                => throw new GalateaTurnException(
                    RequireContextMaterializationDetail(disposed.Detail),
                    "recall-barrier-context-store-unavailable"
                ),
            SessionContextCandidateMaterializationResult.Invalid invalid
                => throw new GalateaTurnException(
                    RequireContextMaterializationDetail(invalid.Detail),
                    "recall-barrier-context-invalid"
                ),
            _ => throw new InvalidDataException(
                "Unknown context candidate materialization result."
            )
        };
        if (candidate.SetAdmissionAnchor != descriptor.SetAdmissionAnchor
            || candidate.AnchorSetups != descriptor.AnchorSetups) {
            throw new GalateaTurnException(
                "Recall context candidate changed its raw anchor or setup identity during materialization.",
                "recall-barrier-context-invalid"
            );
        }
        SessionHistoryPlanningSeed seed =
            host.Engine.CreateHistoryPlanningSeed(
                descriptor.SetAdmissionAnchor,
                descriptor.AnchorSetups,
                cancellationToken
            );
        return host.Engine.ReadHistoryPlanningWindowAt(
            completionBoundary,
            seed,
            cancellationToken
        );
    }

    private static string RequireContextMaterializationDetail(string detail) {
        if (string.IsNullOrWhiteSpace(detail)) {
            throw new InvalidDataException(
                "A non-materialized context result requires detail."
            );
        }
        return detail;
    }

    private async Task<GalateaCompletedOperation>
        RunRecapGridRecoveryAsync(
        CharacterSessionHost host,
        GalateaLiveTurn liveTurn,
        CompletionStreamObserver observer,
        SessionRuntimeRecoveryRequirements requirement,
        EventAddress capturedHead,
        CancellationToken cancellationToken
    ) {
        GalateaRecapGridComposition recapGrid = _recapGrid;
        GalateaRecapGridTurn? turn = null;
        try {
            if (requirement is SessionRuntimeRecoveryRequirements
                    .NewRequestRequired) {
                RequireCurrentConnectionSelectable(host.Character,
                    liveTurn.Options.ConnectionId
                );
                CompletionConnectionConfig inspected =
                    recapGrid.InspectConnectionExact(
                        liveTurn.Options.ConnectionId);
                ValidateRecoveryConnection(
                    host.Engine,
                    capturedHead,
                    inspected);
                turn = await recapGrid.OpenFreshAsync(
                    host.Engine,
                    liveTurn.Options.ConnectionId,
                    pendingObservation: null,
                    defaultPolicy: host.DefaultPolicy,
                    cancellationToken).ConfigureAwait(false);
                RecapGridOnlineContextHandle online = turn.Online
                    ?? throw new InvalidDataException(
                        "New-request RecapGrid binding has no Online context.");
                var lifecycle = new GalateaRecoveryLifecycleGate(
                    online.Lifecycle,
                    liveTurn.StopController);
                host.Engine.UseRuntime(CreateRecapGridRuntime(
                    turn,
                    online.CandidateSource,
                    lifecycle,
                    liveTurn));
            }
            else if (requirement is SessionRuntimeRecoveryRequirements
                         .FrozenCompletionRequired frozen) {
                turn = recapGrid.BindPrepared(host.Engine, frozen);
                liveTurn.StopController.EnterDispatchOrThrow(
                    cancellationToken);
                host.Engine.UseRuntime(new SessionRuntime(
                    CreateRetryClient(turn.Client, liveTurn),
                    CompletionTarget: frozen.CompletionTarget,
                    InputProjector: GalateaInputProjector.Instance));
                SessionPreparedCompletionBoundaryResult committed = await host.Engine
                    .ResumePreparedCompletionToBoundaryAsync(capturedHead, observer, cancellationToken)
                    .ConfigureAwait(false);
                SessionRuntimeRecoveryRequirements next = host.Engine.InspectRuntimeRecoveryRequirements(cancellationToken);
                if (next is SessionRuntimeRecoveryRequirements.NoRuntimeRequired { Phase: SessionExecutionPhase.Idle }) {
                    return new GalateaCompletedOperation(committed.Message, committed.Invocation, committed.Errors);
                }
                if (next is not SessionRuntimeRecoveryRequirements.ToolContinuationRequired || next.CapturedHead is not { } nextHead) {
                    throw new InvalidDataException("A frozen completion must commit either a terminal Action or a pending tool batch.");
                }
                // The frozen binding carries no current candidate/maintenance
                // authority. Release it before rebinding the committed tools
                // and their successor generation through the normal lifecycle.
                await turn.DisposeAsync().ConfigureAwait(false);
                turn = null;
                return await RunRecapGridRecoveryAsync(host, liveTurn, observer, next, nextHead, cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (requirement is SessionRuntimeRecoveryRequirements
                         .ToolContinuationRequired) {
                throw new GalateaTurnException(
                    "冻结回合需要已移除的历史RecapGrid工具运行环境。",
                    "tool-runtime-unsupported");
            }
            else {
                throw RecoveryRequired(requirement);
            }

            ResumeOutcome outcome = await host.Engine.ResumeAsync(
                turn.ResumeHead ?? capturedHead,
                observer,
                cancellationToken).ConfigureAwait(false);
            if (!outcome.Advanced
                || outcome.Message is null
                || outcome.Invocation is null) {
                throw new GalateaTurnException(
                    "RecapGrid恢复未推进。",
                    "recovery-did-not-advance");
            }
            return new GalateaCompletedOperation(
                outcome.Message,
                outcome.Invocation,
                outcome.Errors);
        }
        finally {
            if (turn is not null) {
                await turn.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private void RequireCurrentConnectionSelectable(
        GalateaCharacterConfig character,
        string connectionId
    ) {
        if (!IsSelectableFor(character, connectionId)) {
            throw new GalateaTurnException(
                "当前模型连接不在Galatea可选连接集合中。",
                "recap-grid-connection-absent"
            );
        }
    }

    private SessionRuntime CreateRecapGridRuntime(
        GalateaRecapGridTurn turn,
        ICoherentContextCandidateSource candidates,
        ISessionContextLifecycleCoordinator lifecycle,
        GalateaLiveTurn liveTurn
    ) => new(
        CreateRetryClient(turn.Client, liveTurn),
        CompletionTarget: new SessionCompletionTargetIdentity(
            turn.Identity.ConnectionId,
            turn.Identity.Kind,
            turn.Identity.ConnectionFingerprint),
        ContextCandidateSource: candidates,
        ContextLifecycle: lifecycle,
        InputProjector: GalateaInputProjector.Instance);

    private ICompletionClient CreateRetryClient(ICompletionClient inner, GalateaLiveTurn turn) =>
        new GalateaCompletionRetryClient(inner, new GalateaCompletionRetryOptions {
            TimeProvider = _timeProvider,
            AttemptTimeout = TimeSpan.FromSeconds(_completionAttemptTimeoutSeconds.TryGetValue(turn.Options.ConnectionId, out int seconds) ? seconds : 1800),
            UserStopToken = turn.StopController.UserStopToken,
            InvocationStarted = () => turn.BeginCompletionInvocation(),
            CreateAttemptObserver = (attempt, _) => {
                turn.PublishAttempt(attempt);
                var observer = new CompletionStreamObserver();
                var filter = new InlineThinkTextFilter(startInsideThink: false);
                int tools = 0;
                observer.ReceivedReasoningDelta += delta => {
                    if (!string.IsNullOrEmpty(delta)) { turn.PublishReasoningDelta(delta); }
                };
                observer.ReceivedTextDelta += delta => {
                    string visible = filter.Filter(delta);
                    if (!string.IsNullOrEmpty(visible)) { turn.PublishTextDelta(visible); }
                };
                observer.ReceivedToolCall += _ => {
                    if (Interlocked.Exchange(ref tools, 1) == 0) { turn.PublishStatus(GalateaSseStatusCode.UsingTools); }
                };
                return observer;
            },
            RetryWaiting = notice => {
                turn.ResetCompletionPreview();
                DateTimeOffset now = _timeProvider.GetUtcNow();
                long next = notice.Delay > DateTimeOffset.MaxValue - now
                    ? DateTimeOffset.MaxValue.ToUnixTimeMilliseconds()
                    : (now + notice.Delay).ToUnixTimeMilliseconds();
                turn.PublishRetry(notice.Attempt, notice.Failure.Kind.ToString(), next);
            },
            AttemptTimedOut = _ => {
                // Cancellation callback may race terminal publication. It is
                // diagnostics only; the original call retains its ownership.
                try { turn.PublishStatus(GalateaSseStatusCode.TransportUnresponsive); }
                catch (InvalidOperationException) { }
            },
        });

    internal static SessionTurnEndReason? ClassifyBusinessTermination(CompletionTermination termination) =>
        termination.Kind == CompletionTerminationKind.Incomplete
            ? termination.ProviderReason switch {
                "response.refusal" or "refusal" or "content_filter" or "SAFETY"
                    or "BLOCKLIST" or "PROHIBITED_CONTENT" or "RECITATION"
                    => SessionTurnEndReason.Rejected,
                "length" or "max_tokens" or "max_output_tokens" or "MAX_TOKENS"
                    => SessionTurnEndReason.Incomplete,
                _ => null,
            } : null;

    internal static IReadOnlyDictionary<string, int> ValidateAttemptTimeouts(GalateaConfig config) {
        var values = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var pair in config.CompletionAttemptTimeoutSeconds ?? new Dictionary<string, int>()) {
            if (!config.Connections.Any(connection => connection.Id == pair.Key) || pair.Value is < 1 or > 86400) {
                throw new InvalidOperationException("Completion attempt timeout requires an existing connection and 1..86400 seconds.");
            }
            values.Add(pair.Key, pair.Value);
        }
        return values;
    }

    private async Task<CharacterSessionHost> CreateSessionAsync(
        GalateaCharacterConfig character,
        CancellationToken ct
    ) {
        ct.ThrowIfCancellationRequested();
        var sessionDir = Path.GetFullPath(character.SessionDir);
        bool directoryExists = Directory.Exists(sessionDir);
        bool fileExists = File.Exists(sessionDir);
        bool createIfMissing = !_maintenanceMode
            && character.SessionProvisioning
                == GalateaSessionProvisioning.CreateIfMissing;

        SessionJournalEngine? engine = null;
        CharacterNoteDefaultPodReconciler? characterMemory = null;
        GalateaDelegationSessionHandle? delegationHandle = null;
        CharacterSessionHost? host = null;
        try {
            if (!directoryExists && !fileExists && createIfMissing) {
                RecapGridControlAdmission admission =
                    _sessionBootstrapAdmission
                    ?? throw new GalateaSessionUnavailableException(
                        "session-unprovisioned",
                        "Galatea first-turn bootstrap has no current "
                        + "Agent Control profile."
                    );
                if ((admission.Permissions & GalateaSessionRepositoryProvisioner
                        .RequiredBootstrapPermissions)
                    != GalateaSessionRepositoryProvisioner
                        .RequiredBootstrapPermissions) {
                    throw new GalateaSessionUnavailableException(
                        "session-unprovisioned",
                        "The current Agent Control profile does not "
                        + "authorize complete first-turn RecapGrid "
                        + "provisioning."
                    );
                }
                CompletionConnectionConfig defaultConnection =
                    _connectionCatalog[character.DefaultConnectionId];
                engine = GalateaSessionRepositoryProvisioner
                    .CreateAndPublish(
                        sessionDir,
                        new SessionCreateOptions(
                            defaultConnection.ModelId,
                            character.SystemPrompt,
                            defaultConnection.CompletionSurfaceId
                        ),
                        admission,
                        new GalateaRecapGridAssetParameters(character.CharacterName),
                        SessionProvisioningHooksForTest
                    );
            }
            else if (!directoryExists
                || !Directory.EnumerateFileSystemEntries(sessionDir).Any()) {
                throw new GalateaSessionUnavailableException(
                    "session-unprovisioned",
                    "Galatea requires a provisioned SessionJournal repository."
                );
            }

            engine ??= _maintenanceMode
                ? SessionJournalEngine.OpenReadOnly(sessionDir)
                : OpenSessionForTest?.Invoke(sessionDir) ?? SessionJournalEngine.Open(sessionDir);
            SessionExecutionBoundaryInspection boundary =
                engine.InspectExecutionBoundary(ct);
            DebugUtil.Debug(
                "Galatea.Session",
                $"CreateSessionAsync: character={character.CharacterId}, sessionDir={sessionDir}, phase={boundary.Phase}, head={boundary.Head}"
            );
            RecentTurnsResponseDto recent = BuildRecentTurnsResponse(engine)
                .Response;
            GalateaRecapGridDefaultPolicy defaultPolicy =
                _defaultPolicies.TryGetValue(
                    character.CharacterId,
                    out GalateaRecapGridDefaultPolicy? configuredTarget
                )
                    ? configuredTarget
                    : throw new InvalidDataException(
                        $"Galatea character '{character.CharacterId}' has no RecapGrid target expectation."
                    );
            IOutboundMailExtractor outboundMailExtractor =
                _outboundMailExtractors.TryGetValue(
                    character.CharacterId,
                    out IOutboundMailExtractor? configuredMailExtractor
                )
                    ? configuredMailExtractor
                    : throw new InvalidDataException(
                        $"Galatea character '{character.CharacterId}' has no outbound mail extractor binding."
                    );
            ICharacterNoteExtractor characterNoteExtractor =
                _characterNoteExtractors.TryGetValue(
                    character.CharacterId,
                    out ICharacterNoteExtractor? configuredNoteExtractor
                )
                    ? configuredNoteExtractor
                    : throw new InvalidDataException(
                        $"Galatea character '{character.CharacterId}' has no character note extractor binding."
                    );
            ICharacterNoteDerivedInfoEnricher? derivedInfoEnricher = null;
            if (!_maintenanceMode && _characterNoteBindingEnabled) {
                bool found = _characterNoteDerivedInfoEnrichers.TryGetValue(
                    character.CharacterId,
                    out derivedInfoEnricher
                );
                if (!found
                    && !_allowMissingCharacterNoteDerivedInfoEnricher) {
                    throw new InvalidDataException(
                        $"Galatea character '{character.CharacterId}' has no Character Note DerivedInfo enricher binding."
                    );
                }
            }
            if (!_maintenanceMode && _characterNoteBindingEnabled) {
                ct.ThrowIfCancellationRequested();
                characterMemory = await CharacterMemorySessionComposition
                    .AttachWritableSessionAsync(
                        character,
                        engine,
                        characterNoteExtractor
                    )
                    .ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested();
            if (BeforeDelegationAttachForTest is { } beforeAttach) {
                await beforeAttach().ConfigureAwait(false);
            }
            RequireRunning();
            try {
                delegationHandle = _maintenanceMode
                    ? null
                    : _delegationSupervisor.AttachWritableSession(character.CharacterId, engine);
            }
            catch (ObjectDisposedException exception) when (IsStopping) {
                throw new OperationCanceledException("Session attachment cancelled by host shutdown.", exception);
            }
            IGalateaPlayerTurnRecallProvider playerTurnRecallProvider =
                _playerTurnRecallProviderFactory?.Invoke(
                    character,
                    characterMemory
                ) ?? DisabledGalateaPlayerTurnRecallProvider.Instance;
            host = new CharacterSessionHost(
                character,
                engine,
                recent,
                defaultPolicy,
                characterMemory,
                delegationHandle,
                outboundMailExtractor,
                characterNoteExtractor,
                derivedInfoEnricher,
                derivedInfoEnricher is null
                    ? null
                    : RequireCharacterNoteDerivedInfoDeadline(),
                playerTurnRecallProvider,
                _timeProvider,
                intent => ResolveInternalMailTarget(character, intent)
            );
            host.ColdRecoveryJitterHead = boundary.Phase is SessionExecutionPhase.AwaitingAgentAction
                or SessionExecutionPhase.AwaitingCompletion or SessionExecutionPhase.AwaitingToolExecution
                    ? boundary.Head : null;
            characterMemory = null;
            delegationHandle = null;
            engine = null;
            if (!_maintenanceMode) {
                await host.TurnLock.WaitAsync(ct).ConfigureAwait(false);
                try {
                    // Attachment must become visible before any repeatable
                    // provider work. Pulses and explicit admission own that
                    // cancellable work under this session's TurnLock.
                    _ = ReconcileDurableDeliveries(host, ct);
                    await ReconcileActiveCharacterNoteDerivedInfoPlanAsync(host).ConfigureAwait(false);
                    host.AutonomyCadence?.Arm();
                    host.PublishAutonomyStatus();
                }
                finally {
                    host.TurnLock.Release();
                }
                _ = host.CharacterNoteDerivedInfoPump?.Signal();
            }
            if (SessionAttachedForTest is { } attachedHook) {
                await attachedHook(host).ConfigureAwait(false);
            }
            return host;
        }
        catch (Exception exception) when (
            exception is DirectoryNotFoundException
                or FileNotFoundException
        ) {
            if (host is not null) {
                await host.DisposeAsync().ConfigureAwait(false);
            }
            else {
                try {
                    characterMemory?.Dispose();
                }
                finally {
                    try {
                        delegationHandle?.Dispose();
                    }
                    finally {
                        engine?.Dispose();
                    }
                }
            }
            throw new GalateaSessionUnavailableException(
                "session-unprovisioned",
                "Galatea SessionJournal repository is incomplete.",
                exception
            );
        }
        catch {
            if (host is not null) {
                await host.DisposeAsync().ConfigureAwait(false);
            }
            else {
                try {
                    characterMemory?.Dispose();
                }
                finally {
                    try {
                        delegationHandle?.Dispose();
                    }
                    finally {
                        engine?.Dispose();
                    }
                }
            }
            throw;
        }
    }

    private static RecapGridControlAdmission
        ResolveSessionBootstrapAdmission(
        GalateaRecapGridRuntimeConfig recapGrid
    ) => GalateaSessionRepositoryProvisioner.CreateBootstrapAdmission();

    private static void ValidateRecoveryConnection(
        SessionJournalEngine engine,
        EventAddress capturedHead,
        CompletionConnectionConfig connection
    ) {
        SessionGoverningSetup governing =
            engine.ResolveGoverningSetup(capturedHead);
        if (!string.Equals(
                governing.RuntimeConfig.ModelId,
                connection.ModelId,
                StringComparison.Ordinal
            )
            || !string.Equals(
                governing.RuntimeConfig.CompletionSurfaceId,
                connection.CompletionSurfaceId,
                StringComparison.Ordinal
            )) {
            throw new GalateaTurnException(
                "已接受输入所绑定的 model/surface 与当前连接不一致，请选择匹配连接恢复。",
                "recovery-connection-mismatch"
            );
        }
    }

    internal void ReconcileAcceptanceCleanup(CharacterSessionHost host) =>
        _ = ReconcileDurableDeliveries(host, CancellationToken.None);

    private GalateaDurableReplyLeaseReconcileResult
        ReconcileDurableDeliveries(
        CharacterSessionHost host,
        CancellationToken cancellationToken
    ) {
        GalateaCharacterMailDeliveryReconciler.Reconcile(
            _delegationSupervisor, host, cancellationToken);
        GalateaNoteReceiptDelivery.Reconcile(
            host.CharacterMemoryReconciler, host.Engine, cancellationToken);
        GalateaDurableReplyLeaseReconcileResult result = host
            .ReplyLeaseReconciler.ReconcileActiveLease(
                host.Engine,
                cancellationToken
            );
        return result switch {
            GalateaDurableReplyLeaseReconcileResult.None
                or GalateaDurableReplyLeaseReconcileResult.RolledBack
                or GalateaDurableReplyLeaseReconcileResult.Retained
                or GalateaDurableReplyLeaseReconcileResult.Consumed => result,
            GalateaDurableReplyLeaseReconcileResult.Quarantined =>
                throw new GalateaTurnException(
                    "Durable reply evidence is quarantined.",
                    "delegation-reply-lease-quarantined"
                ),
            GalateaDurableReplyLeaseReconcileResult.Retryable =>
                throw new GalateaTurnException(
                    "Durable reply evidence changed; retry admission.",
                    "delegation-state-changed"
                ),
            GalateaDurableReplyLeaseReconcileResult.LimitExceeded =>
                throw new GalateaTurnException(
                    "Durable reply evidence exceeded its read bound.",
                    "delegation-proof-limit-exceeded"
                ),
            GalateaDurableReplyLeaseReconcileResult.UnsupportedSchema =>
                throw new GalateaTurnException(
                    "Durable reply evidence uses an unsupported schema.",
                    "delegation-session-schema-unsupported"
                ),
            GalateaDurableReplyLeaseReconcileResult.Corruption =>
                throw new GalateaTurnException(
                    "Durable reply evidence is invalid.",
                    "delegation-state-invalid"
                ),
            _ => throw new InvalidDataException(
                "Unknown durable reply reconciliation result."
            )
        };
    }

    private void ReconcileDurableDeliveriesBestEffort(
        CharacterSessionHost host,
        GalateaLiveTurn liveTurn
    ) {
        try {
            _ = ReconcileDurableDeliveries(
                host,
                CancellationToken.None
            );
        }
        catch (Exception exception) when (
            GalateaExceptionClassifier.IsNonFatal(exception)) {
            DebugUtil.Warning(
                "Galatea.Delegation",
                "Durable receipt/reply settlement deferred to recovery: "
                    + $"turnId={liveTurn.TurnId}, "
                    + $"error={exception.GetType().Name}."
            );
        }
    }

    private static GalateaTurnException RecoveryRequired(
        SessionRuntimeRecoveryRequirements requirement
    ) => new(
        $"会话处于 {requirement.Phase}，必须先使用恢复入口。",
        "recovery-required"
    );

    internal static string WrapUserMessageForEngine(
        string userMessage,
        DateTimeOffset externalLocalTimestamp
    ) {
        return PlayerTurnObservationEnvelope.Wrap(
            new PlayerTurnObservation(
                userMessage,
                externalLocalTimestamp
            )
        );
    }

    internal static string NormalizeUserMessageForDisplay(string? storedUserMessage) {
        return PlayerTurnObservationClassifier.TryProject(
            storedUserMessage,
            out PlayerTurnObservationClassifier.Projection projection
        ) ? projection.DisplayText : storedUserMessage ?? string.Empty;
    }

    private static string DescribeTurn(RecentTurnDto? turn) {
        if (turn is null) { return "<null>"; }
        return $"character={Preview(turn.UserText)}, assistant={Preview(turn.Assistant?.Text)}";
    }

    private static string Preview(string? text) {
        if (string.IsNullOrWhiteSpace(text)) { return "<null>"; }
        string normalized = text.Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
        return normalized.Length <= 120 ? normalized : normalized[..120] + "...";
    }
}

internal sealed record GalateaCompletedOperation(
    ActionMessage Message,
    CompletionDescriptor Invocation,
    IReadOnlyList<string>? Errors
);

internal sealed class GalateaSessionUnavailableException
    : InvalidOperationException {
    internal GalateaSessionUnavailableException(
        string code,
        string message,
        Exception? innerException = null
    ) : base(message, innerException) {
        Code = string.IsNullOrWhiteSpace(code)
            ? throw new ArgumentException(
                "Session availability code cannot be blank.",
                nameof(code)
            )
            : code;
    }

    internal string Code { get; }
}

internal static class GalateaExceptionClassifier {
    internal static bool IsNonFatal(Exception exception) {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is OutOfMemoryException
                or StackOverflowException
                or AccessViolationException) {
            return false;
        }
        if (exception is AggregateException aggregate) {
            return aggregate.InnerExceptions.All(IsNonFatal);
        }
        return exception.InnerException is null
            || IsNonFatal(exception.InnerException);
    }
}

internal sealed class GalateaRecentProjectionException : Exception {
    internal GalateaRecentProjectionException(
        string code,
        string message,
        Exception? innerException = null
    ) : base(message, innerException) {
        Code = code;
    }

    internal string Code { get; }
}

internal sealed record GalateaPreparedPopLatestTurn(
    string PoppedUserText,
    byte[] ReceiptUtf8Bytes
);

internal static class GalateaBoundedJson {
    internal static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true
    );

    internal static void RequireFits<T>(
        T value,
        int maximumUtf8Bytes,
        string code
    ) {
        if (maximumUtf8Bytes <= 0) {
            throw new ArgumentOutOfRangeException(
                nameof(maximumUtf8Bytes)
            );
        }
        using var sink = new CappedCountingStream(maximumUtf8Bytes);
        try {
            JsonSerializer.Serialize(sink, value, GalateaJson.Options);
        }
        catch (GalateaJsonLimitException exception) {
            throw new GalateaRecentProjectionException(
                code,
                $"Encoded JSON exceeds {maximumUtf8Bytes} UTF-8 bytes.",
                exception
            );
        }
    }

    private sealed class CappedCountingStream(int maximumBytes)
        : Stream {
        private long _length;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position {
            get => _length;
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override void Write(byte[] buffer, int offset, int count) {
            ArgumentNullException.ThrowIfNull(buffer);
            WriteCore(count);
        }

        public override void Write(ReadOnlySpan<byte> buffer) =>
            WriteCore(buffer.Length);

        private void WriteCore(int count) {
            if (count < 0 || count > maximumBytes - _length) {
                throw new GalateaJsonLimitException();
            }
            _length += count;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();
    }

    private sealed class GalateaJsonLimitException : Exception;
}

public sealed class CharacterSessionHost : IAsyncDisposable {
    private readonly object _turnStateGate = new();
    private GalateaLiveTurn? _currentTurn;
    private GalateaLiveTurn? _lastTurn;
    private RecentTurnsResponseDto _recentTurns;
    private GalateaAgentStatusDto _agentStatus;

    internal CharacterSessionHost(
        GalateaCharacterConfig character,
        SessionJournalEngine engine,
        RecentTurnsResponseDto recentTurns,
        GalateaRecapGridDefaultPolicy defaultPolicy,
        CharacterNoteDefaultPodReconciler? characterMemoryReconciler,
        GalateaDelegationSessionHandle? delegationHandle,
        IOutboundMailExtractor outboundMailExtractor,
        ICharacterNoteExtractor characterNoteExtractor,
        ICharacterNoteDerivedInfoEnricher? derivedInfoEnricher,
        TimeSpan? derivedInfoProviderDeadline,
        IGalateaPlayerTurnRecallProvider playerTurnRecallProvider,
        TimeProvider? timeProvider = null,
        Func<SendMailIntent, GalateaInternalMailTarget?>?
            resolveInternalMailTarget = null
    ) {
        ArgumentNullException.ThrowIfNull(character);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(recentTurns);
        ArgumentNullException.ThrowIfNull(defaultPolicy);
        ArgumentNullException.ThrowIfNull(outboundMailExtractor);
        ArgumentNullException.ThrowIfNull(characterNoteExtractor);
        ArgumentNullException.ThrowIfNull(playerTurnRecallProvider);
        Character = character;
        _agentStatus = new("starting", character.DefaultConnectionId, null, null, null);
        Engine = engine;
        _recentTurns = recentTurns;
        DefaultPolicy = defaultPolicy;
        CharacterMemoryReconciler = characterMemoryReconciler;
        DelegationHandle = delegationHandle;
        CharacterNoteExtractor = characterNoteExtractor;
        PlayerTurnRecallProvider = playerTurnRecallProvider;
        AutonomyCadence = character.AutonomyIntervalMinutes > 0
            ? new GalateaAutonomyCadence(
                timeProvider ?? TimeProvider.System,
                TimeSpan.FromMinutes(character.AutonomyIntervalMinutes)
            )
            : null;
        if (characterMemoryReconciler is not null
            && derivedInfoEnricher is not null) {
            CharacterNoteDerivedInfoPump =
                new CharacterNoteDerivedInfoPump(
                    characterMemoryReconciler,
                    derivedInfoEnricher,
                    engine,
                    TurnLock,
                    derivedInfoProviderDeadline
                );
        }
        if (delegationHandle is not null) {
            ReplyLeaseReconciler =
                new GalateaDurableReplyLeaseReconciler(
                    delegationHandle.Store
                );
            OutboundMailExtractionReconciler =
                new GalateaOutboundMailExtractionReconciler(
                    delegationHandle.Store,
                    outboundMailExtractor,
                    new GalateaSenderSnapshot("character", character.CharacterId, character.CharacterName.Value),
                    resolveInternalMailTarget
                );
        }
    }

    public GalateaCharacterConfig Character { get; }

    public SessionJournalEngine Engine { get; }

    internal GalateaRecapGridDefaultPolicy DefaultPolicy { get; }

    internal ICharacterNoteExtractor CharacterNoteExtractor { get; }

    internal IGalateaPlayerTurnRecallProvider PlayerTurnRecallProvider {
        get;
    }

    internal CharacterNoteDefaultPodReconciler?
        CharacterMemoryReconciler { get; }

    internal CharacterNoteDerivedInfoPump?
        CharacterNoteDerivedInfoPump { get; }

    internal GalateaAutonomyCadence? AutonomyCadence {
        get;
    }

    // Written only under TurnLock; HTTP reads the immutable cached projection.
    internal bool AutomaticReplyFailed { get; set; }
    internal bool GenerationBlocked { get; set; }
    // Claimed once under TurnLock; never persisted or rearmed by a pulse.
    internal EventAddress? ColdRecoveryJitterHead { get; set; }
    private GalateaAdmissionOperation? _admissionOperation;

    internal GalateaAdmissionOperation BeginAdmission(CancellationToken caller, CancellationToken shutdown) {
        lock (_turnStateGate) {
            if (_admissionOperation is not null) { throw new InvalidOperationException("Admission already owns this session."); }
            return _admissionOperation = new GalateaAdmissionOperation(this, caller, shutdown);
        }
    }

    internal GalateaAdmissionStatusDto ReadAdmissionStatus() {
        lock (_turnStateGate) {
            return _admissionOperation is { } operation
                ? new(operation.Id, operation.StopRequested ? "stopping" : "running")
                : new(null, "idle");
        }
    }

    internal bool StopAdmission(string id) {
        lock (_turnStateGate) {
            if (_admissionOperation is not { } operation || operation.Id != id) { return false; }
            operation.RequestStop();
            return true;
        }
    }

    internal void FinishAdmission(GalateaAdmissionOperation operation) {
        lock (_turnStateGate) {
            if (!ReferenceEquals(_admissionOperation, operation)) { throw new InvalidOperationException("Admission owner changed."); }
            _admissionOperation = null;
        }
    }
    internal bool AutomaticAdmissionFailed { get; set; }
    internal ApiErrorDto? AutomaticAdmissionFailure { get; set; }

    internal GalateaAgentStatusDto ReadAgentStatus() => Volatile.Read(ref _agentStatus);

    internal void SetAgentStatus(string state, string? code = null, ApiErrorDto? admissionFailure = null) {
        GalateaAgentStatusDto previous = ReadAgentStatus();
        Volatile.Write(ref _agentStatus, new GalateaAgentStatusDto(
            state, Character.DefaultConnectionId,
            state is "waiting" ? previous.NextActivationAtUnixTimeMilliseconds : null,
            previous.LastActivationAtUnixTimeMilliseconds, code, admissionFailure
        ));
    }

    internal void PublishAutonomyStatus() {
        if (GenerationBlocked) {
            SetAgentStatus("blocked", "COMPLETION_BLOCKED");
            return;
        }
        if (AutomaticAdmissionFailed) {
            SetAgentStatus("blocked", "AUTOMATIC_ADMISSION_FAILED", AutomaticAdmissionFailure);
            return;
        }
        if (AutomaticReplyFailed) {
            SetAgentStatus("blocked", "AUTOMATIC_REPLY_FAILED");
            return;
        }
        if (AutonomyCadence is not { IsArmed: true } cadence) {
            SetAgentStatus("waiting");
            return;
        }
        GalateaAutonomyCadenceStatus status = cadence.ProjectStatus();
        Volatile.Write(ref _agentStatus, new GalateaAgentStatusDto(
            status.State, Character.DefaultConnectionId,
            status.NextActivationAtUnixTimeMilliseconds,
            status.LastActivationAtUnixTimeMilliseconds, status.Code
        ));
    }

    public SemaphoreSlim TurnLock { get; } = new(1, 1);

    internal GalateaDelegationSessionHandle? DelegationHandle { get; }

    internal GalateaDurableReplyLeaseReconciler ReplyLeaseReconciler {
        get;
    } = null!;

    internal GalateaOutboundMailExtractionReconciler
        OutboundMailExtractionReconciler { get; } = null!;

    internal GalateaLiveTurn StartTurn(
        GalateaFreshInput freshInput,
        GalateaTurnOptions options,
        GalateaDurableReplyLease? durableReplyLease = null
    ) {
        ArgumentNullException.ThrowIfNull(freshInput);
        var liveTurn = new GalateaLiveTurn(
            freshInput,
            options,
            durableReplyLease
        );
        lock (_turnStateGate) {
            _lastTurn = null;
            _currentTurn = liveTurn;
            _recentTurns = MarkStale(_recentTurns);
        }
        SetAgentStatus("running");
        return liveTurn;
    }

    internal GalateaLiveTurn StartRecovery(
        GalateaTurnOptions options
    ) {
        ArgumentNullException.ThrowIfNull(options);
        var liveTurn = new GalateaLiveTurn(
            freshInput: null,
            options
        );
        lock (_turnStateGate) {
            _lastTurn = null;
            _currentTurn = liveTurn;
            _recentTurns = MarkStale(_recentTurns);
        }
        SetAgentStatus("running");
        return liveTurn;
    }

    internal RecentTurnsResponseDto GetRecentTurns() {
        lock (_turnStateGate) {
            return _recentTurns;
        }
    }

    internal void SetRecentTurns(RecentTurnsResponseDto recentTurns) {
        ArgumentNullException.ThrowIfNull(recentTurns);
        lock (_turnStateGate) {
            _recentTurns = recentTurns;
        }
    }

    internal void MarkRecentSnapshotStale() {
        lock (_turnStateGate) {
            _recentTurns = MarkStale(_recentTurns);
        }
    }

    internal RecentTurnsResponseDto PrepareRecentSnapshotStale() {
        lock (_turnStateGate) {
            return MarkStale(_recentTurns);
        }
    }

    private static RecentTurnsResponseDto MarkStale(
        RecentTurnsResponseDto recent
    ) => recent with {
        RewindLatestToken = null,
        RecapGridReadiness = recent.RecapGridReadiness is { } readiness
            ? readiness with {
                Freshness = GalateaRecapGridReadiness.StaleFreshness,
                State = "stale",
                Code = "not-observed",
                Detail = "RecapGrid readiness has not been read at the current raw head."
            }
            : null
    };

    internal GalateaLiveTurn? GetCurrentTurn() {
        lock (_turnStateGate) {
            return _currentTurn;
        }
    }

    internal GalateaLiveTurn? FindTurn(string turnId) {
        ArgumentException.ThrowIfNullOrWhiteSpace(turnId);

        lock (_turnStateGate) {
            if (string.Equals(_currentTurn?.TurnId, turnId, StringComparison.Ordinal)) { return _currentTurn; }

            if (string.Equals(_lastTurn?.TurnId, turnId, StringComparison.Ordinal)) { return _lastTurn; }

            return null;
        }
    }

    internal void FinishTurn(GalateaLiveTurn turn) {
        ArgumentNullException.ThrowIfNull(turn);

        lock (_turnStateGate) {
            if (ReferenceEquals(_currentTurn, turn)) {
                _currentTurn = null;
                _lastTurn = turn;
            }
            else if (ReferenceEquals(_lastTurn, turn)) {
                _lastTurn = turn;
            }
        }
    }

    public async ValueTask DisposeAsync() {
        var failures = new List<Exception>(2);
        try {
            if (CharacterNoteDerivedInfoPump is not null) {
                await CharacterNoteDerivedInfoPump.DisposeAsync()
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception) {
            failures.Add(exception);
        }

        try {
            await TurnLock.WaitAsync().ConfigureAwait(false);
            try {
                try {
                    CharacterMemoryReconciler?.Dispose();
                }
                finally {
                    try {
                        DelegationHandle?.Dispose();
                    }
                    finally {
                        Engine.Dispose();
                    }
                }
            }
            finally {
                TurnLock.Release();
            }
        }
        catch (Exception exception) {
            failures.Add(exception);
        }

        Exception? fatal = failures.FirstOrDefault(static exception =>
            !GalateaExceptionClassifier.IsNonFatal(exception));
        if (fatal is not null) {
            ExceptionDispatchInfo.Capture(fatal).Throw();
        }
        if (failures.Count == 1) {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }
        if (failures.Count > 1) {
            throw new AggregateException(
                "Galatea session disposal encountered multiple failures.",
                failures
            );
        }
    }
}

internal static class GalateaConfigLoader {
    public const string ConnectionsFileName = "connections.json";
    public const string DelegatesFileName = "delegates.json";

    private static IReadOnlyList<GalateaPlayerConfig> ResolvePlayers(IReadOnlyList<GalateaPlayerFileConfig> players) {
        ArgumentNullException.ThrowIfNull(players);
        try {
            var resolved = Array.AsReadOnly(players.Select(player => {
                ArgumentNullException.ThrowIfNull(player);
                return new GalateaPlayerConfig(player.PlayerId, new GalateaPlayerName(player.Name), player.Password);
            }).ToArray());
            GalateaConfigValidation.RequireValidPlayers(resolved);
            return resolved;
        }
        catch (ArgumentException exception) {
            throw new InvalidOperationException("Galatea config contains an invalid Player identity or credential.", exception);
        }
    }

    public static GalateaConfig Load(string configPath) {
        if (string.IsNullOrWhiteSpace(configPath)) { throw new InvalidOperationException("Galatea config path must not be blank."); }

        string resolvedPath = Path.GetFullPath(configPath);
        if (!File.Exists(resolvedPath)) {
            throw new FileNotFoundException(
                $"Galatea config file was not found: {resolvedPath}",
                resolvedPath
            );
        }

        return LoadCore(resolvedPath, ReadRootFile(resolvedPath), projectedSources: null);
    }

    private static GalateaConfig LoadCore(string resolvedPath, GalateaRootFileConfig rootFile,
        IReadOnlyDictionary<string, string>? projectedSources) {
        string configDir = Path.GetDirectoryName(resolvedPath)
            ?? throw new InvalidOperationException($"Cannot determine config directory for: {resolvedPath}");
        string connectionsPath = Path.Combine(configDir, ConnectionsFileName);
        string delegatesPath = Path.Combine(configDir, DelegatesFileName);

        if (!File.Exists(connectionsPath)) {
            throw new FileNotFoundException(
                $"Galatea connections file was not found: {connectionsPath}",
                connectionsPath
            );
        }
        byte[] connectionsJson = GalateaStrictConfigReader
            .ReadBoundedRegularFile(
                connectionsPath,
                CompletionConnectionConfigLoader.MaximumInputUtf8Bytes,
                "Galatea connections"
            );
        CompletionConnectionCatalogConfig connectionsFile =
            CompletionConnectionConfigLoader.DecodeCatalog(connectionsJson);
        if (connectionsFile.SelectableConnectionIds is not null) {
            throw new InvalidDataException(
                "Galatea connections must not define selectableConnectionIds; configure connectionOptions per character."
            );
        }
        GalateaCompletionOwner.ValidateGalateaRouting(connectionsFile);
        string? outboundMailExtractorConnectionId =
            connectionsFile.Bindings![
                GalateaCompletionOwner.OutboundMailExtractorBindingKey
            ];
        string? characterNoteExtractorConnectionId =
            connectionsFile.Bindings[
                GalateaCompletionOwner.CharacterNoteExtractorBindingKey
            ];
        string? memoRecallConnectionId = connectionsFile.Bindings[
            GalateaCompletionOwner.MemoRecallBindingKey
        ];
        string? characterConnectionStateExtractorConnectionId = connectionsFile.Bindings[
            GalateaCompletionOwner.CharacterConnectionStateExtractorBindingKey];
        if (!File.Exists(delegatesPath)) {
            throw new FileNotFoundException(
                $"Galatea delegates file was not found: {delegatesPath}",
                delegatesPath
            );
        }
        GalateaDelegateConfig delegates =
            GalateaDelegateConfigReader.Read(delegatesPath);
        if (rootFile.Characters is not { Count: > 0 }) { throw new InvalidOperationException("Galatea config must contain at least one character."); }
        (
            IReadOnlyList<GalateaCharacterConfig> characters,
            GalateaCharacterRecipientDirectory characterRecipientDirectory
        ) =
            ResolveCharacters(
                rootFile.Characters,
                configDir,
                delegates.AllowedRoots,
                outboundMailExtractorConnectionId is not null,
                characterNoteExtractorConnectionId is not null,
                characterConnectionStateExtractorConnectionId is not null,
                projectedSources
            );
        GalateaConfigValidation.RequireValidConnectionDefaults(
            characters,
            connectionsFile
        );

        var config = new GalateaConfig(
            Characters: characters,
            Players: ResolvePlayers(rootFile.Players),
            Connections: connectionsFile.Connections,
            InputNormalizerConnectionId: connectionsFile.Bindings![
                GalateaCompletionOwner.InputNormalizerBindingKey
            ],
            OutboundMailExtractorConnectionId:
                outboundMailExtractorConnectionId,
            CharacterNoteExtractorConnectionId:
                characterNoteExtractorConnectionId,
            MemoRecallConnectionId: memoRecallConnectionId,
            Delegates: delegates,
            ListenUrls: rootFile.Runtime.ListenUrls,
            CallLogDir: ResolveCallLogDirectory(
                rootFile.Runtime.CallLogDir,
                configDir
            ),
            MaintenanceMode: rootFile.Runtime.MaintenanceMode,
            RecapGrid: LoadRecapGridConfig(rootFile.Runtime.RecapGrid),
            CompletionAttemptTimeoutSeconds: rootFile.Runtime.CompletionAttemptTimeoutSeconds,
            CharacterConnectionStateExtractorConnectionId: characterConnectionStateExtractorConnectionId
        ) with {
            CharacterRecipientDirectory = characterRecipientDirectory
        };

        Validate(config);
        _ = GalateaHostService.ValidateAttemptTimeouts(config);
        return config;
    }

    internal static GalateaRootFileConfig ReadRootFile(
        string resolvedPath
    ) {
        byte[] usersBytes = GalateaStrictConfigReader.ReadAndValidate(
            resolvedPath
        );
        GalateaRootFileConfig? rootFile;
        try {
            rootFile = JsonSerializer.Deserialize(
                usersBytes,
                GalateaJsonContext.Default.GalateaRootFileConfig
            );
        }
        catch (JsonException exception) {
            throw new InvalidDataException(
                "Galatea config JSON could not be materialized.",
                exception
            );
        }
        return rootFile ?? throw new InvalidOperationException(
            $"Failed to deserialize Galatea config: {resolvedPath}"
        );
    }

    private static GalateaRecapGridRuntimeConfig LoadRecapGridConfig(
        GalateaRecapGridFileConfig? configured
    ) {
        if (configured is null) {
            throw new InvalidOperationException(
                "Galatea config must contain an exact recapGrid object."
            );
        }
        GalateaRecapGridMaintenanceFileConfig maintenance = configured
            .Maintenance ?? throw new InvalidOperationException(
                "recapGrid.maintenance must be an exact object."
            );
        if (string.IsNullOrWhiteSpace(maintenance.ConnectionId)
            || maintenance.MaximumConcurrency is < 1 or > 1_024
            || maintenance.DispatchTimeoutMilliseconds is < 1 or > 86_400_000) {
            throw new InvalidOperationException(
                "recapGrid.maintenance is invalid."
            );
        }
        return new GalateaRecapGridRuntimeConfig(
            new GalateaRecapGridMaintenanceConfig(
                maintenance.ConnectionId,
                maintenance.MaximumConcurrency,
                TimeSpan.FromMilliseconds(
                    maintenance.DispatchTimeoutMilliseconds
                )
            )
        );
    }

    private static byte[] ReadBoundedFile(
        string path,
        int maximumBytes,
        string kind
    ) {
        RejectReparsePointsOnExistingPath(path, kind);
        return GalateaStrictConfigReader.ReadBoundedRegularFile(
            path,
            maximumBytes,
            kind
        );
    }

    internal static RecapGridRouteManifest LoadRouteManifest(
        string canonicalPath
    ) => RecapGridRouteManifest.ParseJson(ReadBoundedFile(
        canonicalPath,
        RecapGridRouteManifestLimits.MaximumCanonicalUtf8Bytes,
        "RecapGrid route manifest"
    ));

    private static (
        IReadOnlyList<GalateaCharacterConfig> Characters,
        GalateaCharacterRecipientDirectory CharacterRecipientDirectory
    )
        ResolveCharacters(
            IReadOnlyList<GalateaCharacterFileConfig> configuredCharacters,
            string configDirectory,
            IReadOnlyList<string> allowedRoots,
            bool outboundMailEnabled,
            bool characterNoteRequestEnabled,
            bool characterConnectionStateEnabled,
            IReadOnlyDictionary<string, string>? projectedSources
        ) {
        var namedCharacters = new (string CharacterId, GalateaCharacterName CharacterName)[
            configuredCharacters.Count
        ];
        for (int index = 0; index < configuredCharacters.Count; index++) {
            GalateaCharacterFileConfig character = configuredCharacters[index]
                ?? throw new InvalidOperationException(
                    $"Galatea config character[{index}] must not be null."
                );
            try {
                namedCharacters[index] = (
                    character.CharacterId,
                    new GalateaCharacterName(character.CharacterName)
                );
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidDataException) {
                throw new InvalidOperationException(
                    $"Galatea config character '{character.CharacterId}' has an invalid "
                    + "characterName.",
                    exception
                );
            }
        }
        GalateaCharacterRecipientDirectory characterRecipientDirectory =
            GalateaCharacterRecipientDirectory.Create(namedCharacters);

        var resolvedCharacters = new List<GalateaCharacterConfig>(
            configuredCharacters.Count
        );
        for (int index = 0; index < configuredCharacters.Count; index++) {
            GalateaCharacterFileConfig character = configuredCharacters[index]
                ?? throw new InvalidOperationException(
                    $"Galatea config character[{index}] must not be null."
                );
            GalateaCharacterName characterName = namedCharacters[index].CharacterName;
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
            string delegationStateDirectory = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(
                    character.DelegationStateDir,
                    configDirectory
                )
            );
            RejectReparsePointsOnExistingPath(
                delegationStateDirectory,
                $"delegationStateDir for character '{character.CharacterId}'"
            );
            string characterMemoryStateDirectory =
                Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(
                        character.CharacterMemoryStateDir,
                        configDirectory
                    )
                );
            RejectReparsePointsOnExistingPath(
                characterMemoryStateDirectory,
                $"characterMemoryStateDir for character '{character.CharacterId}'"
            );
            string homeDirectory = GalateaDelegateConfigReader.RequireHomeDirectory(
                character.HomeDir, character.CharacterId, allowedRoots
            );
            string characterContextTemplate = projectedSources is null
                ? ResolveCharacterContextTemplate(character, configDirectory)
                : projectedSources[character.CharacterId];
            SessionInputContent systemPrompt;
            try {
                systemPrompt = GalateaSystemPromptComposer.CreateContent(
                    new GalateaSenderSnapshot("character", character.CharacterId, characterName.Value),
                    characterContextTemplate,
                    outboundMailEnabled,
                    characterNoteRequestEnabled,
                    homeDirectory,
                    characterRecipientDirectory.Recipients
                        .Where(peer => !string.Equals(peer.CharacterId, character.CharacterId, StringComparison.Ordinal))
                        .Select(peer => new GalateaSenderSnapshot("character", peer.CharacterId, peer.CharacterName.Value))
                        .ToArray(),
                    characterConnectionStateEnabled,
                    character.ConnectionOptions
                );
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidDataException) {
                throw new InvalidOperationException(
                    $"Galatea config character '{character.CharacterId}' has an invalid "
                    + "character context template.",
                    exception
                );
            }
            resolvedCharacters.Add(new GalateaCharacterConfig(
                character.CharacterId,
                characterName,
                Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(character.SessionDir, configDirectory)
                ),
                delegationStateDirectory,
                characterMemoryStateDirectory,
                homeDirectory,
                character.SessionProvisioning,
                systemPrompt,
                character.DefaultConnectionId,
                character.ConnectionOptions,
                character.AutonomyIntervalMinutes
            ));
        }
        return (
            resolvedCharacters,
            GalateaCharacterRecipientDirectory.Create(resolvedCharacters)
        );
    }

    private static string ResolveCharacterContextTemplate(
        GalateaCharacterFileConfig character,
        string configDirectory
    ) {
        if (string.IsNullOrWhiteSpace(character.CharacterContextTemplateFile)) {
            return character.CharacterContextTemplate;
        }
        string promptPath = Path.GetFullPath(
            character.CharacterContextTemplateFile,
            configDirectory
        );
        byte[] promptBytes = GalateaStrictConfigReader.ReadBoundedRegularFile(
            promptPath,
            GalateaStrictConfigReader.MaximumSystemPromptUtf8Bytes,
            $"characterContextTemplateFile for character '{character.CharacterId}'"
        );
        try {
            return new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true
            ).GetString(promptBytes).Trim();
        }
        catch (DecoderFallbackException exception) {
            throw new InvalidDataException(
                $"Galatea character '{character.CharacterId}' characterContextTemplateFile "
                + "is not strict UTF-8.",
                exception
            );
        }
    }

    private static string? ResolveCallLogDirectory(
        string? configuredPath,
        string configDirectory
    ) {
        if (configuredPath is null) { return null; }
        if (string.IsNullOrWhiteSpace(configuredPath)) {
            throw new InvalidOperationException(
                "Galatea callLogDir must not be blank."
            );
        }
        return Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(configuredPath, configDirectory)
        );
    }

    private static void Validate(GalateaConfig config) {
        GalateaConfigValidation.RequireValidStorageTopology(
            config.Characters,
            config.CallLogDir
        );
        GalateaDelegateConfigReader.Validate(config.Delegates);
        if (config.CallLogDir is not null) {
            RejectReparsePointsOnExistingPath(
                config.CallLogDir,
                "callLogDir"
            );
        }
        var characterIds = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < config.Characters.Count; i++) {
            var character = config.Characters[i];
            if (string.IsNullOrWhiteSpace(character.CharacterId)) { throw new InvalidOperationException($"Galatea config character[{i}] must have a non-empty characterId."); }

            if (!characterIds.Add(character.CharacterId)) { throw new InvalidOperationException($"Galatea config contains duplicate characterId '{character.CharacterId}'."); }

            if (character.SystemPrompt is null || (!character.SystemPrompt.IsStructured && string.IsNullOrWhiteSpace(character.SystemPrompt.TextValue))) {
                throw new InvalidOperationException(
                    $"Galatea config character '{character.CharacterId}' must have a "
                    + "non-empty finalized system prompt."
                );
            }
            GalateaSystemInstructionContent.Validate(character.SystemPrompt);
        }

        if (config.ListenUrls is not null) {
            for (int i = 0; i < config.ListenUrls.Count; i++) {
                if (string.IsNullOrWhiteSpace(config.ListenUrls[i])) { throw new InvalidOperationException($"Galatea config listenUrls[{i}] must not be blank."); }
            }
        }
    }

    private static void RejectReparsePointsOnExistingPath(
        string path,
        string description
    ) {
        string? current = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(path)
        );
        while (!string.IsNullOrEmpty(current)) {
            try {
                if ((File.GetAttributes(current)
                        & FileAttributes.ReparsePoint) != 0) {
                    throw new InvalidOperationException(
                        $"Galatea {description} must not contain an "
                        + $"existing symlink or reparse point: {current}"
                    );
                }
            }
            catch (Exception exception) when (
                exception is FileNotFoundException
                    or DirectoryNotFoundException
            ) {
                // Missing suffixes are allowed. Existing ancestors are still
                // inspected before any call-log directory can be created.
            }
            string? parent = Path.GetDirectoryName(current);
            if (string.Equals(
                    parent,
                    current,
                    StringComparison.Ordinal
                )) {
                break;
            }
            current = parent;
        }
    }
}

internal static class GalateaConfigBootstrapper {
    public static void EnsureExistsOrBootstrap(string configPath) {
        if (string.IsNullOrWhiteSpace(configPath)) { throw new InvalidOperationException("Galatea config path must not be blank."); }

        string resolvedPath = Path.GetFullPath(configPath);
        string? parentDir = Path.GetDirectoryName(resolvedPath);
        if (string.IsNullOrWhiteSpace(parentDir)) { throw new InvalidOperationException($"Cannot determine parent directory for Galatea config path: {resolvedPath}"); }
        GalateaStrictConfigReader.RequireExistingAncestorsNoReparse(
            resolvedPath,
            "Galatea config bootstrap"
        );

        string connectionsPath = Path.Combine(parentDir, GalateaConfigLoader.ConnectionsFileName);
        string delegatesPath = Path.Combine(
            parentDir,
            GalateaConfigLoader.DelegatesFileName
        );
        bool configExists = File.Exists(resolvedPath);
        bool connectionsExists = File.Exists(connectionsPath);
        bool delegatesExists = File.Exists(delegatesPath);
        GalateaRootFileConfig rootFile = configExists
            ? GalateaConfigLoader.ReadRootFile(resolvedPath)
            : GalateaConfigTemplateFactory.CreateRootFile();

        Directory.CreateDirectory(parentDir);

        var jsonOptions = new JsonSerializerOptions(GalateaJson.Options) {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        var generated = new List<string>();
        if (!configExists) {
            byte[] document = JsonSerializer.SerializeToUtf8Bytes(
                rootFile,
                jsonOptions
            );
            byte[] terminated = GC.AllocateUninitializedArray<byte>(
                document.Length + 1
            );
            document.CopyTo(terminated, 0);
            terminated[^1] = (byte)'\n';
            File.WriteAllBytes(resolvedPath, terminated);
            generated.Add(resolvedPath);
        }

        CreateMissingCharacterContextTemplates(
            rootFile.Characters,
            parentDir,
            generated
        );

        if (!connectionsExists) {
            File.WriteAllBytes(
                connectionsPath,
                GalateaConfigTemplateFactory.CreateConnectionsFileUtf8()
            );
            generated.Add(connectionsPath);
        }

        if (!delegatesExists) {
            File.WriteAllBytes(
                delegatesPath,
                GalateaDelegateConfigReader.CreatePlaceholderTemplateUtf8()
            );
            generated.Add(delegatesPath);
        }

        if (generated.Count == 0) { return; }

        throw new InvalidOperationException(
            "Galatea config templates have been generated at "
            + string.Join(" and ", generated)
            + ". Review every generated character context template and, where "
            + "applicable, replace delegate path placeholders, update "
            + "listenUrls, connection settings, and default account "
            + "passwords before restarting the server."
        );
    }

    private static void CreateMissingCharacterContextTemplates(
        IReadOnlyList<GalateaCharacterFileConfig> characters,
        string configDirectory,
        List<string> generated
    ) {
        foreach (GalateaCharacterFileConfig character in characters) {
            if (string.IsNullOrWhiteSpace(
                    character.CharacterContextTemplateFile)) {
                continue;
            }
            string resolved = Path.GetFullPath(
                character.CharacterContextTemplateFile,
                configDirectory
            );
            if (File.Exists(resolved)
                || !IsWithinDirectory(resolved, configDirectory)) {
                continue;
            }
            GalateaStrictConfigReader.RequireExistingAncestorsNoReparse(
                resolved,
                "Galatea character context template bootstrap"
            );
            string? directory = Path.GetDirectoryName(resolved);
            if (string.IsNullOrWhiteSpace(directory)) {
                throw new InvalidOperationException(
                    "Cannot determine the character context template directory."
                );
            }
            Directory.CreateDirectory(directory);
            GalateaStrictConfigReader.RequireExistingAncestorsNoReparse(
                resolved,
                "Galatea character context template bootstrap"
            );
            using var stream = new FileStream(
                resolved,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None
            );
            stream.Write(
                GalateaBuiltInCharacterContextTemplate.Utf8.Span
            );
            stream.Flush(flushToDisk: true);
            generated.Add(resolved);
        }
    }

    private static bool IsWithinDirectory(
        string path,
        string directory
    ) {
        string relative = Path.GetRelativePath(directory, path);
        return !Path.IsPathRooted(relative)
            && !string.Equals(relative, "..", StringComparison.Ordinal)
            && !relative.StartsWith(
                ".." + Path.DirectorySeparatorChar,
                StringComparison.Ordinal
            )
            && !relative.StartsWith(
                ".." + Path.AltDirectorySeparatorChar,
                StringComparison.Ordinal
            );
    }
}

internal static class GalateaDefaults {
    public const string CharacterContextTemplateFile =
        "prompts/character-context-standard-zh-cn.md";
}

internal static class GalateaConfigTemplateFactory {
    public const string PlaceholderModelId = "REPLACE_WITH_YOUR_LOCAL_MODEL_ID";
    public const string DefaultConnectionId = "local";

    public static GalateaRootFileConfig CreateRootFile() {
        return new GalateaRootFileConfig(
            Version: GalateaStrictConfigReader.CurrentConfigVersion,
            Characters: [
                CreateCharacter(
                    "alice",
                    "Alice",
                    "sessions/alice"
                ),
                CreateCharacter(
                    "bob",
                    "Bob",
                    "sessions/bob"
                ),
            ],
            Players: [new GalateaPlayerFileConfig("player-main", "玩家", "REPLACE_WITH_YOUR_PASSWORD")],
            Runtime: new GalateaRuntimeFileConfig(
              ListenUrls: ["http://0.0.0.0:3510"],
              RecapGrid: new GalateaRecapGridFileConfig(
                Maintenance: new GalateaRecapGridMaintenanceFileConfig(
                    ConnectionId: DefaultConnectionId,
                    MaximumConcurrency: 1,
                    DispatchTimeoutMilliseconds: 900_000
                )
              )
            )
        );
    }

    public static byte[] CreateConnectionsFileUtf8() {
        var output = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(
            output,
            new JsonWriterOptions { Indented = true }
        )) {
            writer.WriteStartObject();
            writer.WriteNumber("v", 3);
            writer.WriteStartArray("connections");
            writer.WriteStartObject();
            writer.WriteString("id", DefaultConnectionId);
            writer.WriteString("kind", "openai-chat");
            writer.WriteString("modelId", PlaceholderModelId);
            writer.WriteString(
                "completionSurfaceId",
                "openai-chat/qwen-sglang"
            );
            // Points at a local OpenAI-compatible server by default. The inline
            // placeholder key lets the config load out of the box; characters can
            // replace each inline source with its corresponding env locator.
            writer.WriteString("baseAddress", "http://localhost:8888/");
            writer.WriteString("apiKey", "sk-local-placeholder");
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteStartObject("bindings");
            writer.WriteNull(
                GalateaCompletionOwner.InputNormalizerBindingKey
            );
            writer.WriteNull(
                GalateaCompletionOwner.OutboundMailExtractorBindingKey
            );
            writer.WriteNull(
                GalateaCompletionOwner.CharacterNoteExtractorBindingKey
            );
            writer.WriteNull(
                GalateaCompletionOwner.MemoRecallBindingKey
            );
            writer.WriteNull(GalateaCompletionOwner.CharacterConnectionStateExtractorBindingKey);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        byte[] document = output.WrittenSpan.ToArray();
        _ = CompletionConnectionConfigLoader.DecodeCatalog(document);
        byte[] terminated = GC.AllocateUninitializedArray<byte>(
            document.Length + 1
        );
        document.CopyTo(terminated, 0);
        terminated[^1] = (byte)'\n';
        return terminated;
    }

    private static GalateaCharacterFileConfig CreateCharacter(
        string characterId,
        string characterName,
        string sessionDir
    ) {
        return new GalateaCharacterFileConfig(
            CharacterId: characterId,
            CharacterName: characterName,
            SessionDir: sessionDir,
            DelegationStateDir: $"delegation-state/{characterId}",
            CharacterMemoryStateDir: $"character-memory/{characterId}",
            HomeDir: $"/galatea-homes/{characterId}",
            SessionProvisioning:
                GalateaSessionProvisioning.CreateIfMissing,
            DefaultConnectionId: DefaultConnectionId,
            ConnectionOptions: [new(DefaultConnectionId, "", "")],
            CharacterContextTemplate: "",
            CharacterContextTemplateFile:
                GalateaDefaults.CharacterContextTemplateFile,
            AutonomyIntervalMinutes: 0
        );
    }
}

internal static class GalateaStaticAssetVersion {
    public static string BuildToken(string contentRootPath) {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);

        string webRootPath = Path.Combine(contentRootPath, "wwwroot", "assets");
        long latestTicks = Math.Max(
            File.GetLastWriteTimeUtc(Path.Combine(webRootPath, "galatea.css")).Ticks,
            File.GetLastWriteTimeUtc(Path.Combine(webRootPath, "galatea.js")).Ticks
        );

        return latestTicks.ToString(CultureInfo.InvariantCulture);
    }

    public static string AppendToPath(string assetPath, string versionToken) {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(versionToken);
        return $"{assetPath}?v={versionToken}";
    }
}

internal static class GalateaSseWriter {
    public static async Task WriteFrameAsync(
        HttpResponse response,
        GalateaSseFrame frame,
        CancellationToken ct
    ) {
        await response.Body.WriteAsync(frame.Utf8, ct);
        await response.Body.FlushAsync(ct);
    }
}

internal static class GalateaClaimTypes {
    public const string PlayerId = "galatea_player_id";
}

internal static class GalateaJson {
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) {
        WriteIndented = false
    };
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(GalateaRootFileConfig))]
[JsonSerializable(typeof(GalateaCharacterFileConfig))]
[JsonSerializable(typeof(GalateaPlayerFileConfig))]
[JsonSerializable(typeof(GalateaRuntimeFileConfig))]
[JsonSerializable(typeof(GalateaSessionProvisioning))]
[JsonSerializable(typeof(GalateaRecapGridFileConfig))]
internal sealed partial class GalateaJsonContext : JsonSerializerContext;
