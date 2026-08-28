using System.Buffers;
using System.Collections.Concurrent;
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
using Atelia.SessionJournal;
using Atelia.SessionJournal.RecapGrid.AgentControl;
using Atelia.SessionJournal.RecapGrid.Control;
using Atelia.SessionJournal.RecapGrid.Hosting;
using Atelia.SessionJournal.RecapGrid.Online;

namespace Atelia.Galatea.Server;

public sealed class GalateaHostService : IAsyncDisposable {
    internal const int RecentTurnLimit = 6;
    internal const int MaximumRecentResponseUtf8Bytes = 4 * 1024 * 1024;
    internal const int MaximumPoppedUserTextUtf8Bytes = 256 * 1024;
    internal const int MaximumPopReceiptUtf8Bytes = 2 * 1024 * 1024;
    private readonly GalateaInputPreprocessor _inputPreprocessor;
    private readonly IReadOnlyDictionary<string, IOutboundMailExtractor>
        _outboundMailExtractors;
    private readonly bool _maintenanceMode;
    private readonly GalateaRecapGridComposition _recapGrid;
    private readonly GalateaCompletionOwner? _completionOwner;
    private readonly GalateaDelegationSupervisor _delegationSupervisor;
    internal GalateaDisposeTestHooks? DisposeHooksForTest { get; set; }
    internal GalateaSessionProvisioningTestHooks?
        SessionProvisioningHooksForTest { get; set; }
    private readonly ConcurrentDictionary<string, Lazy<Task<UserSessionHost>>> _sessions = new(StringComparer.Ordinal);
    private readonly IReadOnlyDictionary<string, GalateaUserConfig> _users;
    private readonly IReadOnlyDictionary<string, CompletionConnectionConfig>
        _connectionCatalog;
    private readonly IReadOnlyList<GalateaConnectionInfoDto>
        _selectableConnections;
    private readonly string _defaultConnectionId;
    private readonly RecapGridControlAdmission? _sessionBootstrapAdmission;
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
        IGalateaDurableDelegateTransport delegateTransport
    ) : this(
        config,
        CreateProductionComponents(
            config,
            completionClientFactory,
            userMessageNormalizerFactory,
            delegateTransport
        )
    ) { }

    private GalateaHostService(
        GalateaConfig config,
        GalateaProductionComponents components
    ) {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(components);
        _sessionBootstrapAdmission = components.SessionBootstrapAdmission;
        _completionOwner = components.Owner;
        _delegationSupervisor = components.DelegationSupervisor;
        _recapGrid = components.RecapGrid;
        _inputPreprocessor = components.InputPreprocessor;
        _outboundMailExtractors = components.OutboundMailExtractors;
        _maintenanceMode = components.MaintenanceMode;
        _users = components.Users;
        _connectionCatalog = components.ConnectionCatalog;
        _selectableConnections = components.SelectableConnections;
        _defaultConnectionId = components.DefaultConnectionId;
    }

    internal GalateaHostService(
        GalateaConfig config,
        IGalateaUserMessageNormalizer userMessageNormalizer,
        GalateaRecapGridComposition recapGrid
    ) {
        ArgumentNullException.ThrowIfNull(recapGrid);
        ArgumentNullException.ThrowIfNull(userMessageNormalizer);
        _ = GalateaDelegateConfigReader.Validate(config.Delegates);
        GalateaConfigValidation.RequireDistinctUserStorageDirectories(
            config.Users
        );
        CompletionConnectionsFileConfig normalized =
            CompletionConnectionConfigLoader.NormalizeAndValidate(new(
                config.Connections,
                config.DefaultConnectionId,
                config.SelectableConnectionIds,
                new Dictionary<string, string?>(StringComparer.Ordinal) {
                    [GalateaCompletionOwner.InputNormalizerBindingKey] =
                        config.InputNormalizerConnectionId,
                    [GalateaCompletionOwner.OutboundMailExtractorBindingKey] =
                        config.OutboundMailExtractorConnectionId,
                }
            ));
        GalateaCompletionOwner.ValidateGalateaRouting(normalized);
        _recapGrid = recapGrid;
        _completionOwner = null;
        _inputPreprocessor = new GalateaInputPreprocessor(
            userMessageNormalizer
        );
        _maintenanceMode = config.MaintenanceMode;
        _users = config.Users.ToDictionary(
            static value => value.UserId,
            StringComparer.Ordinal
        );
        _outboundMailExtractors = _users.Keys.ToDictionary(
            static userId => userId,
            static _ => (IOutboundMailExtractor)
                DisabledOutboundMailExtractor.Instance,
            StringComparer.Ordinal
        );
        IReadOnlyDictionary<string, CompletionConnectionConfig> fullCatalog =
            normalized.Connections.ToDictionary(
                static value => value.Id,
                StringComparer.Ordinal
            );
        CompletionConnectionConfig[] selectable = normalized
            .SelectableConnectionIds!
            .Select(id => fullCatalog[id])
            .ToArray();
        _connectionCatalog = selectable.ToDictionary(
            static value => value.Id,
            StringComparer.Ordinal
        );
        _selectableConnections = Array.AsReadOnly(selectable
            .Select(static value => new GalateaConnectionInfoDto(
                value.Id,
                value.ModelId
            ))
            .ToArray());
        _defaultConnectionId = normalized.DefaultConnectionId!;
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
        IGalateaDurableDelegateTransport? delegateTransportOverride = null
    ) {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(completionClientFactory);
        ArgumentNullException.ThrowIfNull(normalizerFactory);
        GalateaConfigValidation.RequireDistinctUserStorageDirectories(
            config.Users
        );

        GalateaCompletionOwner? owner = null;
        try {
            owner = new GalateaCompletionOwner(
                config,
                completionClientFactory
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
            IReadOnlyDictionary<string, GalateaUserConfig> users =
                config.Users.ToDictionary(
                    static value => value.UserId,
                    StringComparer.Ordinal
                );
            IReadOnlyDictionary<string, IOutboundMailExtractor>
                outboundMailExtractors = CreateOutboundMailExtractors(
                    users,
                    owner.OutboundMailExtractorConnection,
                    owner.GetOutboundMailExtractorClient
                );
            IReadOnlyDictionary<string, CompletionConnectionConfig>
                fullCatalog = owner.Connections.ToDictionary(
                    static value => value.Id,
                    StringComparer.Ordinal
                );
            CompletionConnectionConfig[] selectable = owner
                .SelectableConnectionIds
                .Select(id => fullCatalog[id])
                .ToArray();
            IReadOnlyDictionary<string, CompletionConnectionConfig>
                connectionCatalog = selectable.ToDictionary(
                    static value => value.Id,
                    StringComparer.Ordinal
                );
            IReadOnlyList<GalateaConnectionInfoDto> selectableConnections =
                Array.AsReadOnly(selectable.Select(static value =>
                    new GalateaConnectionInfoDto(value.Id, value.ModelId)
                ).ToArray());

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
                delegationSupervisor,
                sessionBootstrapAdmission,
                config.MaintenanceMode,
                users,
                connectionCatalog,
                selectableConnections,
                owner.DefaultConnectionId
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

    private sealed record GalateaProductionComponents(
        GalateaCompletionOwner Owner,
        GalateaRecapGridComposition RecapGrid,
        GalateaInputPreprocessor InputPreprocessor,
        IReadOnlyDictionary<string, IOutboundMailExtractor>
            OutboundMailExtractors,
        GalateaDelegationSupervisor DelegationSupervisor,
        RecapGridControlAdmission SessionBootstrapAdmission,
        bool MaintenanceMode,
        IReadOnlyDictionary<string, GalateaUserConfig> Users,
        IReadOnlyDictionary<string, CompletionConnectionConfig>
            ConnectionCatalog,
        IReadOnlyList<GalateaConnectionInfoDto> SelectableConnections,
        string DefaultConnectionId
    );

    internal static IReadOnlyDictionary<string, IOutboundMailExtractor>
        CreateOutboundMailExtractors(
        IReadOnlyDictionary<string, GalateaUserConfig> users,
        CompletionConnectionConfig? connection,
        Func<ICompletionClient> getClient
    ) {
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(getClient);
        return users.ToDictionary(
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

    public bool TryGetUser(string userId, out GalateaUserConfig user)
        => _users.TryGetValue(userId, out user!);

    public IReadOnlyList<GalateaConnectionInfoDto> Connections =>
        _selectableConnections;

    public string DefaultConnectionId => _defaultConnectionId;

    internal GalateaDelegationSupervisor DelegationSupervisor =>
        _delegationSupervisor;

    public bool TryGetConnection(
        string? requestedConnectionId,
        out CompletionConnectionConfig connection
    ) {
        string id = string.IsNullOrWhiteSpace(requestedConnectionId)
            ? _defaultConnectionId
            : requestedConnectionId;
        return _connectionCatalog.TryGetValue(id, out connection!);
    }

    public bool ValidatePassword(GalateaUserConfig user, string password) {
        ArgumentNullException.ThrowIfNull(user);
        password ??= string.Empty;

        byte[] left = SHA256.HashData(Encoding.UTF8.GetBytes(password));
        byte[] right = SHA256.HashData(Encoding.UTF8.GetBytes(user.Password ?? string.Empty));
        return CryptographicOperations.FixedTimeEquals(left, right);
    }

    public async Task<UserSessionHost> GetSessionAsync(string userId, CancellationToken ct) {
        var user = _users.GetValueOrDefault(userId)
            ?? throw new InvalidOperationException($"Unknown user '{userId}'.");

        var lazy = _sessions.GetOrAdd(
            userId,
            static (key, state) => new Lazy<Task<UserSessionHost>>(
                () => state.Service.CreateSessionAsync(state.User, CancellationToken.None),
                LazyThreadSafetyMode.ExecutionAndPublication
            ),
            (Service: this, User: user)
        );

        try {
            var session = await lazy.Value.ConfigureAwait(false);
            DebugUtil.Info(
                "Galatea.Session",
                $"GetSessionAsync: user={userId}"
            );
            return session;
        }
        catch {
            _sessions.TryRemove(new KeyValuePair<
                string,
                Lazy<Task<UserSessionHost>>
            >(userId, lazy));
            throw;
        }
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
            && snapshot.Turns.FirstOrDefault()?.TerminalAction.Address
                == head
            && GalateaPlayerObservationClassifier.TryProject(
                snapshot.Turns.First().ObservationContent,
                out _,
                out _
            )
                ? EventAddressTextCodec.Format(head)
                : null;
        DebugUtil.Info(
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
        UserSessionHost host,
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

    internal Task<CurrentTurnDto> GetCurrentTurnAsync(
        UserSessionHost host,
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
            DebugUtil.Info(
                "Galatea.Session",
                $"GetCurrentTurnAsync: user={host.User.UserId}, status={result.Status}, head={recovery.CapturedHead}"
            );
            return Task.FromResult(result);
        }
        finally {
            host.TurnLock.Release();
        }
    }

    internal CurrentTurnDto BuildLiveCurrentTurn(UserSessionHost host) {
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
        DebugUtil.Info(
            "Galatea.Session",
            $"BuildLiveCurrentTurn: turnId={result.TurnId}, connectionId={result.ConnectionId ?? "<none>"}"
        );
        return result;
    }

    internal async ValueTask<RecentTurnsResponseDto>
        RefreshRecentTurnsBestEffortAsync(
        UserSessionHost host,
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
                $"Stable session snapshot refresh failed: user={host.User.UserId}",
                ex
            );
            return fallback;
        }
    }

    internal async ValueTask<RecentTurnsResponseDto?>
        RefreshRecentTurnsForCompletedStreamAsync(
        UserSessionHost host,
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
                    + $"durable boundary: user={host.User.UserId}",
                ex
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
                + $"user={host.User.UserId}",
                ex
            );
            return null;
        }
    }

    private async ValueTask<RecentTurnsResponseDto>
        RefreshRecentTurnsAsync(
        UserSessionHost host,
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
            .FailedTurnMustBeAbandoned => new CurrentTurnDto("idle"),
        SessionRuntimeRecoveryRequirements.NoRuntimeRequired {
            Phase: SessionExecutionPhase.Empty
        } => new CurrentTurnDto("unprovisioned"),
        SessionRuntimeRecoveryRequirements.NewRequestRequired =>
            RecoveryCurrentTurn(recovery),
        SessionRuntimeRecoveryRequirements.FrozenCompletionRequired {
            DispatchState:
                SessionDurableDispatchState.StartedOutcomeUncertain
        } => RecoveryCurrentTurn(recovery, restartRequired: true),
        SessionRuntimeRecoveryRequirements.FrozenCompletionRequired =>
            RecoveryCurrentTurn(recovery),
        SessionRuntimeRecoveryRequirements.ToolContinuationRequired =>
            RecoveryCurrentTurn(recovery),
        _ => throw new InvalidDataException(
            "Unknown runtime recovery requirement."
        )
    };

    private static CurrentTurnDto RecoveryCurrentTurn(
        SessionRuntimeRecoveryRequirements recovery,
        bool restartRequired = false
    ) => new(
        "recovery-required",
        RecoveryHead: EventAddressTextCodec.FormatNullable(
            recovery.CapturedHead
        ),
        RestartRequired: restartRequired
    );

    /// <summary>
    /// Starts an admitted ordinary player turn and freezes its reply cutoff.
    /// The caller must still own <see cref="UserSessionHost.TurnLock"/> and
    /// must already have accepted the exact recovery boundary and selected
    /// main connection. Keeping the cutoff here makes the HTTP acceptance
    /// instant, rather than the later background task, authoritative.
    /// </summary>
    internal async ValueTask ReconcileDurableAdmissionAsync(
        UserSessionHost host,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(host);
        GalateaDurableReplyLeaseReconcileResult reply =
            ReconcileDurableReplyLease(host, cancellationToken);
        if (reply is GalateaDurableReplyLeaseReconcileResult.RolledBack
                or GalateaDurableReplyLeaseReconcileResult.Consumed) {
            _ = host.DelegationHandle?.Signal();
        }
        _ = await ReconcileOutboundExtractionAsync(
                host,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    internal ValueTask<string> NormalizeUserMessageAtAdmissionAsync(
        string userMessage,
        CancellationToken cancellationToken
    ) => _inputPreprocessor.ProcessAsync(
        userMessage,
        cancellationToken
    );

    internal GalateaLiveTurn StartTurn(
        UserSessionHost host,
        string userMessage,
        GalateaTurnOptions options
    ) {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(userMessage);
        ArgumentNullException.ThrowIfNull(options);
        string? messageError = GalateaHttpV1.ValidateMessage(userMessage);
        if (messageError is not null) {
            throw new ArgumentException(messageError, nameof(userMessage));
        }
        GalateaDurableReplyLeaseBeginResult cutoff =
            host.ReplyLeaseReconciler.BeginCutoff(userMessage);
        if (cutoff is GalateaDurableReplyLeaseBeginResult.Empty) {
            return host.StartTurn(
                new GalateaFreshInput.PlayerAction(userMessage),
                options
            );
        }
        if (cutoff is GalateaDurableReplyLeaseBeginResult.Created created) {
            try {
                return host.StartTurn(
                    new GalateaFreshInput.PlayerAction(
                        userMessage,
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
        throw new InvalidDataException(
            "Unknown durable reply cutoff result."
        );
    }

    /// <summary>
    /// Revalidates the HTTP fresh-turn admission while the caller retains the
    /// session writer lock. A previously failed turn is abandoned at its
    /// exact head here so any lease belonging to that failed Observation can
    /// be rolled back before an ordinary player cutoff is formed.
    /// </summary>
    internal async ValueTask PrepareFreshTurnAdmissionAsync(
        UserSessionHost host,
        SessionRuntimeRecoveryRequirements admitted,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(admitted);
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
                return;
            case SessionRuntimeRecoveryRequirements
                    .FailedTurnMustBeAbandoned admittedFailed
                when current is SessionRuntimeRecoveryRequirements
                    .FailedTurnMustBeAbandoned currentFailed
                && currentFailed.FailedHead == admittedFailed.FailedHead:
                AbandonFailedTurnAndReconcile(
                    host,
                    admittedFailed.FailedHead,
                    cancellationToken
                );
                await ReconcileDurableAdmissionAsync(
                        host,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                return;
            default:
                throw new GalateaTurnException(
                    "会话边界已变化，请刷新后重试。",
                    "stale-session-head"
                );
        }
    }

    internal GalateaLiveTurn StartInboundMailTurn(
        UserSessionHost host,
        MailboxMessage message,
        GalateaTurnOptions options
    ) {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(options);
        return host.StartTurn(
            new GalateaFreshInput.InboundMail(message),
            options
        );
    }

    internal GalateaLiveTurn StartRecovery(
        UserSessionHost host,
        GalateaTurnOptions options
    ) {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(options);
        return host.StartRecovery(options);
    }

    internal GalateaPreparedPopLatestTurn?
        PrepareAndCommitPopLatestTurn(
        UserSessionHost host,
        EventAddress expectedHead,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(host);
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

        if (!GalateaPlayerObservationClassifier.TryProject(
                ready.Value.ObservationContent,
                out string poppedUserText,
                out _)) {
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
                "Popped user text is not valid Unicode.",
                exception
            );
        }
        if (sourceBytes > MaximumPoppedUserTextUtf8Bytes) {
            throw new GalateaRecentProjectionException(
                "popped-user-text-limit-exceeded",
                "Popped user text exceeds the display receipt limit."
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
        host.SetRecentTurns(preparedStaleSnapshot);
        return preparedReceipt;
    }

    internal GalateaLiveTurn? FindTurn(UserSessionHost host, string turnId) {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(turnId);
        return host.FindTurn(turnId);
    }

    internal void FinishTurn(UserSessionHost host, GalateaLiveTurn turn) {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(turn);
        host.FinishTurn(turn);
    }

    internal bool RequestStop(UserSessionHost host, string turnId) {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(turnId);

        var turn = host.FindTurn(turnId);
        return turn?.RequestStop() == true;
    }

    internal async Task RunTurnAsync(
        UserSessionHost host,
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

        liveTurn.PublishStatus(GalateaSseStatusCode.Generating);
        DebugUtil.Info(
            "Galatea.Session",
            $"RunTurnAsync start: user={host.User.UserId}, turnId={liveTurn.TurnId}, input={Preview(liveTurn.UserMessage)}, head={host.Engine.ReadCurrentHead()}",
            eventKind: DebugEventKind.Start
        );

        CompletionStreamObserver observer = liveTurn.Observer;
        var toolLoopStarted = 0;
        observer.ReceivedReasoningDelta += delta => {
            if (!string.IsNullOrEmpty(delta)) {
                liveTurn.PublishReasoningDelta(delta);
            }
        };
        var textFilter = new InlineThinkTextFilter(startInsideThink: false);
        observer.ReceivedTextDelta += delta => {
            var visibleText = textFilter.Filter(delta);
            if (string.IsNullOrEmpty(visibleText)) { return; }
            liveTurn.PublishTextDelta(visibleText);
        };
        observer.ReceivedToolCall += _ => {
            if (Interlocked.Exchange(ref toolLoopStarted, 1) == 0) {
                liveTurn.PublishStatus(GalateaSseStatusCode.UsingTools);
            }
        };

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
            && liveTurn.StopController.Phase
                == GalateaTurnStopPhase.PreDispatch
            && liveTurn.StopRequested
        ) {
            if (liveTurn.Options.Mode == GalateaTurnMode.FreshSend) {
                ReconcileDurableReplyLeaseBestEffort(host, liveTurn);
            }
            throw liveTurn.Options.Mode == GalateaTurnMode.FreshSend
                ? PreDispatchStopped()
                : RecoveryPreDispatchStopped();
        }
        catch (SessionJournalTurnAbortedException ex) {
            DebugUtil.Warning(
                "Galatea.Session",
                $"RunTurnAsync completion aborted: user={host.User.UserId}, turnId={liveTurn.TurnId}, termination={ex.Termination.Kind}, providerReason={ex.Termination.ProviderReason ?? "<none>"}, detail={ex.Termination.Detail ?? "<none>"}"
            );
            if (liveTurn.StopRequested && WasStoppedByObserver(ex.Termination)) {
                AbandonCurrentFailedTurnAndReconcile(host);
                throw new GalateaTurnException(
                    "已停止生成，本轮结果未写入历史。你可以调整开关或修改输入后重试。",
                    "stopped-by-user"
                );
            }
            AbandonCurrentFailedTurnAndReconcile(host);
            throw new GalateaTurnException(
                "模型本次输出未正常结束，本轮结果已放弃写入历史。请刷新页面后重试。",
                ex.Termination.ProviderReason ?? ex.Termination.Kind.ToString()
            );
        }
        catch {
            ReconcileDurableReplyLeaseBestEffort(host, liveTurn);
            throw;
        }
        SessionExecutionBoundaryInspection completedBoundary =
            host.Engine.InspectExecutionBoundary();
        if (completedBoundary.Phase != SessionExecutionPhase.Idle) {
            ReconcileDurableReplyLeaseBestEffort(host, liveTurn);
            throw new InvalidDataException(
                "A completed Galatea operation must leave an Idle durable boundary."
            );
        }
        GalateaDurableReplyLeaseReconcileResult leaseSettlement =
            ReconcileDurableReplyLease(host, ct);
        if (leaseSettlement is GalateaDurableReplyLeaseReconcileResult
                .Retained) {
            throw new GalateaTurnException(
                "Durable reply settlement still requires recovery.",
                "delegation-reply-lease-retained"
            );
        }
        await ReconcileOutboundExtractionAsync(host, ct)
            .ConfigureAwait(false);
        RecentTurnsResponseDto? snapshot =
            await RefreshRecentTurnsForCompletedStreamAsync(
                host,
                ct
            )
            .ConfigureAwait(false);
        DebugUtil.Info(
            "Galatea.Session",
            $"RunTurnAsync send done: user={host.User.UserId}, turnId={liveTurn.TurnId}, errors={completed.Errors?.Count ?? 0}, snapshotTurns={snapshot?.Turns.Count.ToString(CultureInfo.InvariantCulture) ?? "unavailable"}, head={host.Engine.ReadCurrentHead()}"
        );
        liveTurn.PublishDone(snapshot);
    }

    private static async ValueTask<
        GalateaOutboundExtractionReconcileResult>
        ReconcileOutboundExtractionAsync(
        UserSessionHost host,
        CancellationToken cancellationToken
    ) {
        try {
            GalateaOutboundExtractionReconcileResult result =
                await host.OutboundExtractionReconciler.ReconcileAsync(
                        host.Engine,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            if (result is GalateaOutboundExtractionReconcileResult
                    .SelectedHeadChanged) {
                throw new GalateaTurnException(
                    "Durable extraction head changed; retry admission.",
                    "delegation-state-changed"
                );
            }
            if (result is GalateaOutboundExtractionReconcileResult.Captured) {
                _ = host.DelegationHandle?.Signal();
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
        catch (GalateaOutboundExtractionReadException exception) {
            throw exception.Kind switch {
                GalateaOutboundExtractionReadFailureKind.LimitExceeded =>
                    new GalateaTurnException(
                        "Durable extraction exceeded its read bound.",
                        "delegation-proof-limit-exceeded"
                    ),
                GalateaOutboundExtractionReadFailureKind.UnsupportedSchema =>
                    new GalateaTurnException(
                        "Durable extraction uses an unsupported schema.",
                        "delegation-session-schema-unsupported"
                    ),
                GalateaOutboundExtractionReadFailureKind.Corruption =>
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
                    GalateaOutboundExtractionCaptureMismatchException
                    or InvalidDataException
                ? "delegation-state-invalid"
                : "delegation-extraction-unavailable";
            throw new GalateaTurnException(
                reason == "delegation-state-invalid"
                    ? "Durable extraction evidence is invalid."
                    : "Durable outbound extraction is temporarily unavailable.",
                reason,
                exception
            );
        }
    }

    private async Task<GalateaCompletedOperation> RunFreshSendAsync(
        UserSessionHost host,
        GalateaLiveTurn liveTurn,
        CompletionStreamObserver observer,
        CancellationToken cancellationToken
    ) {
        RequireCurrentConnectionSelectable(
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
        UserSessionHost host,
        GalateaLiveTurn liveTurn,
        CompletionStreamObserver observer,
        CancellationToken cancellationToken
    ) {
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

    public async ValueTask DisposeAsync() {
        BeginShutdown();
        List<Exception>? failures = null;
        int sessionIndex = 0;
        foreach (var entry in _sessions.Values) {
            if (!entry.IsValueCreated) { continue; }

            try {
                var session = await entry.Value.ConfigureAwait(false);
                await session.DisposeAsync().ConfigureAwait(false);
                DisposeHooksForTest?.AfterSessionDisposed?.Invoke(
                    sessionIndex);
            }
            catch (Exception exception) when (
                GalateaExceptionClassifier.IsNonFatal(exception)) {
                (failures ??= []).Add(exception);
            }
            sessionIndex++;
        }
        try {
            await _delegationSupervisor.DisposeAsync()
                .ConfigureAwait(false);
            DisposeHooksForTest?.AfterDelegationSupervisorDisposed?.Invoke();
        }
        catch (Exception exception) when (
            GalateaExceptionClassifier.IsNonFatal(exception)) {
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
        catch (Exception exception) when (
            GalateaExceptionClassifier.IsNonFatal(exception)) {
            (failures ??= []).Add(exception);
        }
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

    internal void BeginShutdown() =>
        _delegationSupervisor.BeginShutdown();

    private async Task<GalateaCompletedOperation>
        RunRecapGridFreshSendAsync(
        UserSessionHost host,
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
                    host.User.SystemPrompt),
                cancellationToken);
        if (reconciled is not SessionDesiredSetupReconciliationResult
                .Ready ready) {
            throw new GalateaTurnException(
                "RecapGrid会话设置无法在当前边界安全更新。",
                "recap-grid-desired-setup-unavailable");
        }
        string prompted = liveTurn.FreshInput switch {
            GalateaFreshInput.PlayerAction
                when liveTurn.DurableReplyLease is { } lease =>
                lease.RenderObservation(),
            GalateaFreshInput.PlayerAction player =>
                GalateaPlayerObservationEnvelope.Wrap(
                    new GalateaPlayerObservation(
                        player.Text,
                        player.ReadyNotices
                    )
                ),
            GalateaFreshInput.InboundMail mail =>
                mail.DurableObservation,
            _ => throw new InvalidOperationException(
                "Fresh send requires a typed fresh input."
            )
        };
        await using GalateaRecapGridTurn turn =
            await recapGrid.OpenFreshAsync(
                host.Engine,
                liveTurn.Options.ConnectionId,
                prompted,
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
            SessionUncertainCompletionRecoveryPolicy.Refuse));
        _ = liveTurn.DurableReplyLease?.BindObservationBase(
            host.Engine,
            ready.GoverningSetup.Head,
            prompted
        );
        TurnResult result = await host.Engine.SendAsync(
            ready.GoverningSetup.Head,
            prompted,
            observer,
            cancellationToken).ConfigureAwait(false);
        return new GalateaCompletedOperation(
            result.Message,
            result.Invocation,
            result.Errors);
    }

    private async Task<GalateaCompletedOperation>
        RunRecapGridRecoveryAsync(
        UserSessionHost host,
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
                RequireCurrentConnectionSelectable(
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
                    SessionUncertainCompletionRecoveryPolicy.Refuse));
            }
            else if (requirement is SessionRuntimeRecoveryRequirements
                         .FrozenCompletionRequired frozen) {
                if (frozen.DispatchState
                        == SessionDurableDispatchState
                            .StartedOutcomeUncertain
                    && !liveTurn.Options.RestartUncertainCompletion) {
                    throw new GalateaTurnException(
                        "上次模型调用结果不确定；必须明确选择重新调用。",
                        "uncertain-completion-restart-required");
                }
                turn = recapGrid.BindPrepared(host.Engine, frozen);
                liveTurn.StopController.EnterObserverOnlyOrThrow(
                    cancellationToken);
                host.Engine.UseRuntime(new SessionRuntime(
                    turn.Client,
                    turn.AgentControl?.ToolSession,
                    CompletionTarget: frozen.CompletionTarget,
                    MaxTokens: turn.Connection.MaxTokens,
                    UncertainCompletionRecoveryPolicy:
                        liveTurn.Options.RestartUncertainCompletion
                            ? SessionUncertainCompletionRecoveryPolicy
                                .RestartWithNewAttempt
                            : SessionUncertainCompletionRecoveryPolicy.Refuse,
                    ToolRuntimeIdentity:
                        turn.AgentControl?.RuntimeIdentity));
            }
            else if (requirement is SessionRuntimeRecoveryRequirements
                         .ToolContinuationRequired toolContinuation) {
                turn = await recapGrid.BindToolContinuationAsync(
                    host.Engine,
                    liveTurn.Options.ConnectionId,
                    id => _connectionCatalog.ContainsKey(id),
                    toolContinuation,
                    cancellationToken
                ).ConfigureAwait(false);
                RecapGridOnlineContextHandle online = turn.Online
                    ?? throw new InvalidDataException(
                        "Tool continuation has no Online context."
                    );
                var lifecycle = new GalateaRecoveryLifecycleGate(
                    online.Lifecycle,
                    liveTurn.StopController
                );
                host.Engine.UseRuntime(CreateRecapGridRuntime(
                    turn,
                    online.CandidateSource,
                    lifecycle,
                    SessionUncertainCompletionRecoveryPolicy.Refuse
                ));
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

    private void RequireCurrentConnectionSelectable(string connectionId) {
        if (!_connectionCatalog.ContainsKey(connectionId)) {
            throw new GalateaTurnException(
                "当前模型连接不在Galatea可选连接集合中。",
                "recap-grid-connection-absent"
            );
        }
    }

    private static SessionRuntime CreateRecapGridRuntime(
        GalateaRecapGridTurn turn,
        ICoherentContextCandidateSource candidates,
        ISessionContextLifecycleCoordinator lifecycle,
        SessionUncertainCompletionRecoveryPolicy recoveryPolicy
    ) => new(
        turn.Client,
        turn.AgentControl?.ToolSession,
        CompletionTarget: new SessionCompletionTargetIdentity(
            turn.Identity.ConnectionId,
            turn.Identity.Kind,
            turn.Identity.ConnectionFingerprint,
            turn.Identity.RequestAdapterFingerprint),
        MaxTokens: turn.Connection.MaxTokens,
        UncertainCompletionRecoveryPolicy: recoveryPolicy,
        ToolRuntimeIdentity: turn.AgentControl?.RuntimeIdentity,
        ContextCandidateSource: candidates,
        ContextLifecycle: lifecycle);

    private async Task<UserSessionHost> CreateSessionAsync(
        GalateaUserConfig user,
        CancellationToken ct
    ) {
        ct.ThrowIfCancellationRequested();
        var sessionDir = Path.GetFullPath(user.SessionDir);
        bool directoryExists = Directory.Exists(sessionDir);
        bool fileExists = File.Exists(sessionDir);
        bool createIfMissing = !_maintenanceMode
            && user.SessionProvisioning
                == GalateaSessionProvisioning.CreateIfMissing;

        SessionJournalEngine? engine = null;
        GalateaDelegationSessionHandle? delegationHandle = null;
        UserSessionHost? host = null;
        try {
            if (!directoryExists && !fileExists && createIfMissing) {
                RecapGridControlAdmission admission =
                    _sessionBootstrapAdmission
                    ?? throw new GalateaSessionUnavailableException(
                        "session-unprovisioned",
                        "Galatea first-turn bootstrap has no current "
                        + "Agent Control profile."
                    );
                if ((admission.Permissions
                        & RecapGridControlPermission.Create)
                    != RecapGridControlPermission.Create) {
                    throw new GalateaSessionUnavailableException(
                        "session-unprovisioned",
                        "The current Agent Control profile does not "
                        + "authorize SessionJournal Control creation."
                    );
                }
                CompletionConnectionConfig defaultConnection =
                    _connectionCatalog[_defaultConnectionId];
                engine = GalateaSessionRepositoryProvisioner
                    .CreateAndPublish(
                        sessionDir,
                        new SessionCreateOptions(
                            defaultConnection.ModelId,
                            user.SystemPrompt,
                            defaultConnection.CompletionSurfaceId
                        ),
                        admission,
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
                : SessionJournalEngine.Open(sessionDir);
            SessionExecutionBoundaryInspection boundary =
                engine.InspectExecutionBoundary(ct);
            DebugUtil.Info(
                "Galatea.Session",
                $"CreateSessionAsync: user={user.UserId}, sessionDir={sessionDir}, phase={boundary.Phase}, head={boundary.Head}"
            );
            RecentTurnsResponseDto recent = BuildRecentTurnsResponse(engine)
                .Response;
            delegationHandle = _maintenanceMode
                ? null
                : _delegationSupervisor.AttachWritableSession(
                    user.UserId,
                    engine
                );
            host = new UserSessionHost(
                user,
                engine,
                recent,
                delegationHandle,
                _outboundMailExtractors.TryGetValue(
                    user.UserId,
                    out IOutboundMailExtractor? outboundMailExtractor
                )
                    ? outboundMailExtractor
                    : throw new InvalidDataException(
                        $"Galatea user '{user.UserId}' has no outbound mail extractor binding."
                    )
            );
            if (!_maintenanceMode) {
                await host.TurnLock.WaitAsync(ct).ConfigureAwait(false);
                try {
                    await ReconcileDurableAdmissionAsync(host, ct)
                        .ConfigureAwait(false);
                }
                finally {
                    host.TurnLock.Release();
                }
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
                delegationHandle?.Dispose();
                engine?.Dispose();
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
                delegationHandle?.Dispose();
                engine?.Dispose();
            }
            throw;
        }
    }

    private static RecapGridControlAdmission
        ResolveSessionBootstrapAdmission(
        GalateaRecapGridRuntimeConfig recapGrid
    ) {
        if (!recapGrid.AgentControlProfiles.TryGet(
                recapGrid.CurrentAgentControlProfileId,
                out RecapGridAgentControlProfile? profile)) {
            throw new InvalidOperationException(
                "The current Agent Control profile is unavailable."
            );
        }
        return profile.Admission;
    }

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

    private static void AbandonCurrentFailedTurnAndReconcile(
        UserSessionHost host
    ) {
        SessionExecutionBoundaryInspection boundary =
            host.Engine.InspectExecutionBoundary();
        if (boundary.Phase != SessionExecutionPhase.TurnFailed
            || boundary.Head is not { } failedHead) {
            throw new GalateaTurnException(
                "失败轮次的持久化边界需要恢复，请刷新后处理。",
                "failed-turn-recovery-required"
            );
        }
        AbandonFailedTurnAndReconcile(
            host,
            failedHead,
            CancellationToken.None
        );
    }

    private static void AbandonFailedTurnAndReconcile(
        UserSessionHost host,
        EventAddress failedHead,
        CancellationToken cancellationToken
    ) {
        _ = ReconcileDurableReplyLease(
            host,
            CancellationToken.None
        );
        SessionTurnRetractionResult result =
            host.Engine.AbandonFailedTurn(
                failedHead,
                cancellationToken
            );
        if (result is not SessionTurnRetractionResult.Moved) {
            throw new GalateaTurnException(
                "失败轮次未能在精确边界安全放弃，请刷新后处理。",
                "failed-turn-recovery-required"
            );
        }
        GalateaDurableReplyLeaseReconcileResult settled =
            ReconcileDurableReplyLease(
                host,
                CancellationToken.None
            );
        if (settled is GalateaDurableReplyLeaseReconcileResult.Retained) {
            throw new GalateaTurnException(
                "Abandoned durable reply evidence was not rolled back.",
                "delegation-reply-abandon-incomplete"
            );
        }
        if (settled is GalateaDurableReplyLeaseReconcileResult.RolledBack) {
            _ = host.DelegationHandle?.Signal();
        }
    }

    private static GalateaDurableReplyLeaseReconcileResult
        ReconcileDurableReplyLease(
        UserSessionHost host,
        CancellationToken cancellationToken
    ) {
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

    private static void ReconcileDurableReplyLeaseBestEffort(
        UserSessionHost host,
        GalateaLiveTurn liveTurn
    ) {
        try {
            _ = ReconcileDurableReplyLease(
                host,
                CancellationToken.None
            );
        }
        catch (Exception exception) when (
            GalateaExceptionClassifier.IsNonFatal(exception)) {
            DebugUtil.Warning(
                "Galatea.Delegation",
                "Durable reply settlement deferred to recovery: "
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

    private static bool WasStoppedByObserver(CompletionTermination termination) {
        ArgumentNullException.ThrowIfNull(termination);

        if (termination.Kind is not CompletionTerminationKind.Incomplete) { return false; }

        return termination.Detail?.Contains("Streaming observer stopped", StringComparison.Ordinal) == true;
    }

    internal static string WrapUserMessageForEngine(string userMessage) {
        return GalateaPlayerObservationEnvelope.Wrap(
            new GalateaPlayerObservation(userMessage)
        );
    }

    internal static string NormalizeUserMessageForDisplay(string? storedUserMessage) {
        return GalateaPlayerObservationClassifier.TryProject(
            storedUserMessage,
            out _,
            out string display
        ) ? display : storedUserMessage ?? string.Empty;
    }

    private static string DescribeTurn(RecentTurnDto? turn) {
        if (turn is null) { return "<null>"; }
        return $"user={Preview(turn.UserText)}, assistant={Preview(turn.Assistant.Text)}";
    }

    private static GalateaTurnException PreDispatchStopped() => new(
        "已停止本轮请求；尚未开始模型生成，也未写入会话历史。",
        "stopped-before-dispatch"
    );

    private static GalateaTurnException
        RecoveryPreDispatchStopped() => new(
        "已停止本次恢复尝试；原有持久化轮次仍保持待恢复状态。",
        "recovery-stopped-before-dispatch"
    );

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
        return exception is not (
            OutOfMemoryException
                or StackOverflowException
                or AccessViolationException
        );
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

public sealed class UserSessionHost : IAsyncDisposable {
    private readonly object _turnStateGate = new();
    private GalateaLiveTurn? _currentTurn;
    private GalateaLiveTurn? _lastTurn;
    private RecentTurnsResponseDto _recentTurns;

    internal UserSessionHost(
        GalateaUserConfig user,
        SessionJournalEngine engine,
        RecentTurnsResponseDto recentTurns,
        GalateaDelegationSessionHandle? delegationHandle,
        IOutboundMailExtractor outboundMailExtractor
    ) {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(recentTurns);
        ArgumentNullException.ThrowIfNull(outboundMailExtractor);
        User = user;
        Engine = engine;
        _recentTurns = recentTurns;
        DelegationHandle = delegationHandle;
        if (delegationHandle is not null) {
            ReplyLeaseReconciler =
                new GalateaDurableReplyLeaseReconciler(
                    delegationHandle.Store
                );
            OutboundExtractionReconciler =
                new GalateaOutboundExtractionReconciler(
                    delegationHandle.Store,
                    outboundMailExtractor
                );
        }
    }

    public GalateaUserConfig User { get; }

    public SessionJournalEngine Engine { get; }

    public SemaphoreSlim TurnLock { get; } = new(1, 1);

    internal GalateaDelegationSessionHandle? DelegationHandle { get; }

    internal GalateaDurableReplyLeaseReconciler ReplyLeaseReconciler {
        get;
    } = null!;

    internal GalateaOutboundExtractionReconciler
        OutboundExtractionReconciler { get; } = null!;

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
        await TurnLock.WaitAsync().ConfigureAwait(false);
        try {
            try {
                DelegationHandle?.Dispose();
            }
            finally {
                Engine.Dispose();
            }
        }
        finally {
            TurnLock.Release();
        }
    }
}

internal static class GalateaConfigLoader {
    public const string ConnectionsFileName = "connections.json";
    public const string DelegatesFileName = "delegates.json";
    private const int MaximumAgentControlProfileCount = 256;
    private const int MaximumAgentControlProfileUtf8Bytes = 128 * 1024;

    public static GalateaConfig Load(string configPath) {
        if (string.IsNullOrWhiteSpace(configPath)) { throw new InvalidOperationException("Galatea config path must not be blank."); }

        string resolvedPath = Path.GetFullPath(configPath);
        if (!File.Exists(resolvedPath)) {
            throw new FileNotFoundException(
                $"Galatea config file was not found: {resolvedPath}",
                resolvedPath
            );
        }

        string configDir = Path.GetDirectoryName(resolvedPath)
            ?? throw new InvalidOperationException($"Cannot determine config directory for: {resolvedPath}");
        string connectionsPath = Path.Combine(configDir, ConnectionsFileName);
        string delegatesPath = Path.Combine(configDir, DelegatesFileName);
        byte[] usersBytes = GalateaStrictConfigReader.ReadUsersAndValidate(
            resolvedPath
        );
        GalateaUsersFileConfig? usersFile;
        try {
            usersFile = JsonSerializer.Deserialize(
                usersBytes,
                GalateaJsonContext.Default.GalateaUsersFileConfig
            );
        }
        catch (JsonException exception) {
            throw new InvalidDataException(
                "Galatea config JSON could not be materialized.",
                exception
            );
        }
        if (usersFile is null) { throw new InvalidOperationException($"Failed to deserialize Galatea config: {resolvedPath}"); }

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
        CompletionConnectionsFileConfig connectionsFile =
            CompletionConnectionConfigLoader.Decode(connectionsJson);
        GalateaCompletionOwner.ValidateGalateaRouting(connectionsFile);
        if (!File.Exists(delegatesPath)) {
            throw new FileNotFoundException(
                $"Galatea delegates file was not found: {delegatesPath}",
                delegatesPath
            );
        }
        GalateaDelegateConfig delegates =
            GalateaDelegateConfigReader.Read(delegatesPath);
        if (usersFile.Users is not { Count: > 0 }) { throw new InvalidOperationException("Galatea config must contain at least one user."); }
        IReadOnlyList<GalateaUserConfig> users =
            ResolveUsers(usersFile.Users, configDir);

        var config = new GalateaConfig(
            Users: users,
            Connections: connectionsFile.Connections,
            DefaultConnectionId: connectionsFile.DefaultConnectionId!,
            SelectableConnectionIds:
                connectionsFile.SelectableConnectionIds!,
            InputNormalizerConnectionId: connectionsFile.Bindings![
                GalateaCompletionOwner.InputNormalizerBindingKey
            ],
            OutboundMailExtractorConnectionId: connectionsFile.Bindings[
                GalateaCompletionOwner.OutboundMailExtractorBindingKey
            ],
            Delegates: delegates,
            ListenUrls: usersFile.ListenUrls,
            CallLogDir: ResolveCallLogDirectory(
                usersFile.CallLogDir,
                configDir
            ),
            MaintenanceMode: usersFile.MaintenanceMode,
            RecapGrid: LoadRecapGridConfig(usersFile.RecapGrid, configDir)
        );

        Validate(config);
        return config;
    }

    private static GalateaRecapGridRuntimeConfig LoadRecapGridConfig(
        GalateaRecapGridFileConfig? configured,
        string configDirectory
    ) {
        if (configured is null) {
            throw new InvalidOperationException(
                "Galatea config must contain an exact recapGrid object."
            );
        }
        string routeManifestPath = ResolveRequiredFilePath(
            configured.RouteManifestPath,
            configDirectory,
            "recapGrid.routeManifestPath",
            requireExistingFile: false
        );
        IReadOnlyList<string>? profileFiles =
            configured.AgentControlProfileFiles;
        if (profileFiles is null
            || profileFiles.Count is < 1
                or > MaximumAgentControlProfileCount) {
            throw new InvalidOperationException(
                "recapGrid.agentControlProfileFiles must contain between "
                + "1 and 256 exact profile paths."
            );
        }
        var resolvedProfiles = new HashSet<string>(
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal
        );
        var profiles = new List<RecapGridAgentControlProfile>(
            profileFiles.Count
        );
        for (int index = 0; index < profileFiles.Count; index++) {
            string path = ResolveRequiredFilePath(
                profileFiles[index],
                configDirectory,
                $"recapGrid.agentControlProfileFiles[{index}]",
                requireExistingFile: true
            );
            if (!resolvedProfiles.Add(path)) {
                throw new InvalidOperationException(
                    "recapGrid.agentControlProfileFiles contains a "
                    + "duplicate canonical path."
                );
            }
            profiles.Add(RecapGridAgentControlProfile.DecodeCanonical(
                ReadBoundedFile(
                    path,
                    MaximumAgentControlProfileUtf8Bytes,
                    "Agent Control profile"
                )
            ));
        }
        var registry = new RecapGridAgentControlProfileRegistry(profiles);
        if (string.IsNullOrWhiteSpace(
                configured.CurrentAgentControlProfileId)
            || !registry.TryGet(
                configured.CurrentAgentControlProfileId,
                out _)) {
            throw new InvalidOperationException(
                "recapGrid.currentAgentControlProfileId must exactly name "
                + "one configured profile."
            );
        }
        return new GalateaRecapGridRuntimeConfig(
            routeManifestPath,
            registry,
            configured.CurrentAgentControlProfileId
        );
    }

    private static string ResolveRequiredFilePath(
        string configuredPath,
        string configDirectory,
        string field,
        bool requireExistingFile
    ) {
        if (string.IsNullOrWhiteSpace(configuredPath)) {
            throw new InvalidOperationException(
                $"{field} must be non-empty."
            );
        }
        string resolved = Path.GetFullPath(configuredPath, configDirectory);
        RejectReparsePointsOnExistingPath(resolved, field);
        if (requireExistingFile && !File.Exists(resolved)) {
            throw new FileNotFoundException(
                $"{field} was not found: {resolved}",
                resolved
            );
        }
        return resolved;
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
    ) => RecapGridRouteManifest.DecodeCanonical(ReadBoundedFile(
        canonicalPath,
        RecapGridRouteManifestLimits.MaximumCanonicalUtf8Bytes,
        "RecapGrid route manifest"
    ));

    private static IReadOnlyList<GalateaUserConfig>
        ResolveUsers(
            IReadOnlyList<GalateaUserFileConfig> configuredUsers,
            string configDirectory
        ) {
        var resolvedUsers = new List<GalateaUserConfig>(
            configuredUsers.Count
        );
        for (int index = 0; index < configuredUsers.Count; index++) {
            GalateaUserFileConfig user = configuredUsers[index]
                ?? throw new InvalidOperationException(
                    $"Galatea config user[{index}] must not be null."
                );
            GalateaCharacterName characterName;
            try {
                characterName = new GalateaCharacterName(
                    user.CharacterName
                );
            }
            catch (ArgumentException exception) {
                throw new InvalidOperationException(
                    $"Galatea config user '{user.UserId}' has an invalid "
                    + "characterName.",
                    exception
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
            string delegationStateDirectory = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(
                    user.DelegationStateDir,
                    configDirectory
                )
            );
            RejectReparsePointsOnExistingPath(
                delegationStateDirectory,
                $"delegationStateDir for user '{user.UserId}'"
            );
            string systemPromptTemplate = ResolveSystemPromptTemplate(
                user,
                configDirectory
            );
            string systemPrompt;
            try {
                systemPrompt = GalateaPromptTemplate.Render(
                    systemPromptTemplate,
                    characterName,
                    GalateaStrictConfigReader.MaximumSystemPromptUtf8Bytes
                );
            }
            catch (ArgumentException exception) {
                throw new InvalidOperationException(
                    $"Galatea config user '{user.UserId}' has an invalid "
                    + "system prompt template.",
                    exception
                );
            }
            resolvedUsers.Add(new GalateaUserConfig(
                user.UserId,
                user.Password,
                characterName,
                Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(user.SessionDir, configDirectory)
                ),
                delegationStateDirectory,
                user.SessionProvisioning,
                systemPrompt
            ));
        }
        return resolvedUsers;
    }

    private static string ResolveSystemPromptTemplate(
        GalateaUserFileConfig user,
        string configDirectory
    ) {
        if (string.IsNullOrWhiteSpace(user.SystemPromptTemplateFile)) {
            return user.SystemPromptTemplate;
        }
        string promptPath = Path.GetFullPath(
            user.SystemPromptTemplateFile,
            configDirectory
        );
        byte[] promptBytes = GalateaStrictConfigReader.ReadBoundedRegularFile(
            promptPath,
            GalateaStrictConfigReader.MaximumSystemPromptUtf8Bytes,
            $"systemPromptTemplateFile for user '{user.UserId}'"
        );
        try {
            return new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true
            ).GetString(promptBytes).Trim();
        }
        catch (DecoderFallbackException exception) {
            throw new InvalidDataException(
                $"Galatea user '{user.UserId}' systemPromptTemplateFile "
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
        GalateaConfigValidation.RequireDistinctUserStorageDirectories(
            config.Users
        );
        GalateaDelegateConfigReader.Validate(config.Delegates);
        if (config.CallLogDir is not null) {
            RejectReparsePointsOnExistingPath(
                config.CallLogDir,
                "callLogDir"
            );
        }
        var userIds = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < config.Users.Count; i++) {
            var user = config.Users[i];
            if (string.IsNullOrWhiteSpace(user.UserId)) { throw new InvalidOperationException($"Galatea config user[{i}] must have a non-empty userId."); }

            if (!userIds.Add(user.UserId)) { throw new InvalidOperationException($"Galatea config contains duplicate userId '{user.UserId}'."); }

            if (string.IsNullOrWhiteSpace(user.Password)) { throw new InvalidOperationException($"Galatea config user '{user.UserId}' must have a non-empty password."); }

            if (string.IsNullOrWhiteSpace(user.SessionDir)) { throw new InvalidOperationException($"Galatea config user '{user.UserId}' must have a non-empty sessionDir."); }

            if (string.IsNullOrWhiteSpace(user.DelegationStateDir)) { throw new InvalidOperationException($"Galatea config user '{user.UserId}' must have a non-empty delegationStateDir."); }

            if (string.IsNullOrWhiteSpace(user.SystemPrompt)) {
                throw new InvalidOperationException(
                    $"Galatea config user '{user.UserId}' must have a "
                    + "non-empty finalized system prompt."
                );
            }

            if (config.CallLogDir is not null) {
                string sessionDirectory =
                    Path.GetFullPath(user.SessionDir);
                RejectReparsePointsOnExistingPath(
                    sessionDirectory,
                    $"sessionDir for user '{user.UserId}'"
                );
                EnsurePathsDoNotNest(
                    config.CallLogDir,
                    sessionDirectory,
                    "sessionDir",
                    user.UserId
                );
                EnsurePathsDoNotNest(
                    config.CallLogDir,
                    Path.GetFullPath(user.DelegationStateDir),
                    "delegationStateDir",
                    user.UserId
                );
            }

        }

        if (config.ListenUrls is not null) {
            for (int i = 0; i < config.ListenUrls.Count; i++) {
                if (string.IsNullOrWhiteSpace(config.ListenUrls[i])) { throw new InvalidOperationException($"Galatea config listenUrls[{i}] must not be blank."); }
            }
        }
    }

    private static void EnsurePathsDoNotNest(
        string callLogDirectory,
        string userStorageDirectory,
        string storageField,
        string userId
    ) {
        string callLogs = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(callLogDirectory)
        );
        string storage = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(userStorageDirectory)
        );
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        GalateaConfigValidation.RequireDisjoint(
            callLogs,
            "callLogDir",
            storage,
            $"{storageField} for user '{userId}'",
            comparison
        );
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
        if (configExists && connectionsExists && delegatesExists) { return; }
        if (configExists) {
            _ = GalateaStrictConfigReader.ReadUsersAndValidate(
                resolvedPath
            );
        }

        Directory.CreateDirectory(parentDir);

        var jsonOptions = new JsonSerializerOptions(GalateaJson.Options) {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        var generated = new List<string>();
        if (!configExists) {
            byte[] document = JsonSerializer.SerializeToUtf8Bytes(
                GalateaConfigTemplateFactory.CreateUsersFile(),
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

        throw new InvalidOperationException(
            "Galatea config templates have been generated at "
            + string.Join(" and ", generated)
            + ". Please replace every delegate path placeholder, update "
            + "listenUrls, the connections' modelId / baseAddress / apiKey, "
            + "and the default account passwords before restarting the server."
        );
    }
}

internal static class GalateaDefaults {
    public const string SystemPromptTemplate =
        "你是${characterName}，一位家庭局域网里的私人助手。"
        + "优先用简洁、直接、可信的中文回答。"
        + "不确定时明确说明不确定，不编造细节。";

}

internal static class GalateaConfigTemplateFactory {
    public const string PlaceholderModelId = "REPLACE_WITH_YOUR_LOCAL_MODEL_ID";
    public const string DefaultConnectionId = "local";

    public static GalateaUsersFileConfig CreateUsersFile() {
        return new GalateaUsersFileConfig(
            Version: GalateaStrictConfigReader.CurrentConfigVersion,
            Users: [
                CreateUser(
                    "alice",
                    "alice123",
                    "Alice",
                    "sessions/alice"
                ),
                CreateUser(
                    "bob",
                    "bob123",
                    "Bob",
                    "sessions/bob"
                ),
            ],
            ListenUrls: ["http://0.0.0.0:3510"],
            RecapGrid: new GalateaRecapGridFileConfig(
                RouteManifestPath: "recap-grid-routes.json",
                AgentControlProfileFiles: [
                    "recap-grid-agent-control-profile.json"
                ],
                CurrentAgentControlProfileId: "default"
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
            writer.WriteNumber("v", 1);
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
            // placeholder key lets the config load out of the box; users can
            // replace each inline source with its corresponding env locator.
            writer.WriteString("baseAddress", "http://localhost:8888/");
            writer.WriteString("apiKey", "sk-local-placeholder");
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteString("defaultConnectionId", DefaultConnectionId);
            writer.WriteStartArray("selectableConnectionIds");
            writer.WriteStringValue(DefaultConnectionId);
            writer.WriteEndArray();
            writer.WriteStartObject("bindings");
            writer.WriteNull(
                GalateaCompletionOwner.InputNormalizerBindingKey
            );
            writer.WriteNull(
                GalateaCompletionOwner.OutboundMailExtractorBindingKey
            );
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        byte[] document = output.WrittenSpan.ToArray();
        _ = CompletionConnectionConfigLoader.Decode(document);
        byte[] terminated = GC.AllocateUninitializedArray<byte>(
            document.Length + 1
        );
        document.CopyTo(terminated, 0);
        terminated[^1] = (byte)'\n';
        return terminated;
    }

    private static GalateaUserFileConfig CreateUser(
        string userId,
        string password,
        string characterName,
        string sessionDir
    ) {
        return new GalateaUserFileConfig(
            UserId: userId,
            Password: password,
            CharacterName: characterName,
            SessionDir: sessionDir,
            DelegationStateDir: $"delegation-state/{userId}",
            SessionProvisioning:
                GalateaSessionProvisioning.CreateIfMissing,
            SystemPromptTemplate: GalateaDefaults.SystemPromptTemplate
        );
    }
}

internal static class GalateaHtml {
    public static string RenderLoginPage(bool invalidCredentials, string assetVersion) {
        string errorHtml = invalidCredentials
            ? "<p class=\"error\">用户名或密码不正确。</p>"
            : string.Empty;

        string stylesheetPath = GalateaStaticAssetVersion.AppendToPath("/assets/galatea.css", assetVersion);

        return $$"""
<!doctype html>
<html lang="zh-CN">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Family Chat Login</title>
    <link rel="stylesheet" href="{{stylesheetPath}}">
</head>
<body class="login-body">
  <main class="login-shell">
    <h1>Family Chat</h1>
    <p class="login-copy">局域网家庭单会话 Chat</p>
    <p class="login-hint">首次启动后，请先确认 <code>.atelia/galatea/config.json</code>。</p>
    {{errorHtml}}
    <form method="post" action="/login" class="login-form">
      <label>用户名<input name="userId" autocomplete="username" required></label>
      <label>密码<input type="password" name="password" autocomplete="current-password" required></label>
      <button type="submit">登录</button>
    </form>
  </main>
</body>
</html>
""";
    }

    public static string RenderAppPage(
        GalateaUserConfig user,
        IReadOnlyList<GalateaConnectionInfoDto> connections,
        string defaultConnectionId,
        bool maintenanceMode,
        string assetVersion
    ) {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultConnectionId);
        string connectionsJson = JsonSerializer.Serialize(
            connections,
            GalateaJson.Options
        );
        string defaultConnectionJson = JsonSerializer.Serialize(
            defaultConnectionId,
            GalateaJson.Options
        );
        string maintenanceBanner = maintenanceMode
            ? "<p class=\"maintenance-banner\" role=\"status\">维护模式：会话只读，发送、恢复、撤销与停止已禁用。</p>"
            : string.Empty;
        string maintenanceDisabled = maintenanceMode
            ? " disabled"
            : string.Empty;
        string stylesheetPath = GalateaStaticAssetVersion.AppendToPath("/assets/galatea.css", assetVersion);
        string scriptPath = GalateaStaticAssetVersion.AppendToPath("/assets/galatea.js", assetVersion);
        string maximumStreamConnectionBytes =
            GalateaSseLimits.BrowserMaximumConnectionBytes.ToString(
                CultureInfo.InvariantCulture
            );
        string maximumStreamFrameBytes =
            GalateaSseLimits.BrowserMaximumFrameBytes.ToString(
                CultureInfo.InvariantCulture
            );

        return $$"""
<!doctype html>
<html lang="zh-CN">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Family Chat</title>
    <link rel="stylesheet" href="{{stylesheetPath}}">
</head>
<body class="app-body">
  <main class="app-shell">

    {{maintenanceBanner}}

    <section class="composer">
      <form id="chat-form">
        <fieldset id="connection-picker" class="connection-picker" aria-label="模型连接">
          <legend>模型连接</legend>
        </fieldset>
        <section id="recap-planning-status" class="recap-planning-status hidden" aria-live="polite" title="HistoryLoad 是 Timeline cadence 的内部度量，不是模型 token 数。">
          <div id="recap-planning-summary" class="recap-planning-summary"></div>
          <progress id="recap-planning-progress" class="recap-planning-progress hidden" max="1" value="0"></progress>
          <div id="recap-planning-detail" class="recap-planning-detail"></div>
          <div class="recap-planning-note">HistoryLoad 不是模型 token 数</div>
        </section>
        <textarea id="message-input" rows="3" placeholder="说点什么……" required{{maintenanceDisabled}}></textarea>
        <div class="composer-actions">
          <div class="composer-status">
            <span id="composer-mode-hint" class="eyebrow hidden"></span>
            <span id="status-text" class="status-text"></span>
          </div>
          <div class="composer-buttons">
            <button id="undo-last-button" type="button" class="ghost-button"{{maintenanceDisabled}}>撤销上一轮</button>
            <button id="stop-button" type="button" class="ghost-button"{{maintenanceDisabled}}>停止</button>
            <button id="send-button" type="submit"{{maintenanceDisabled}}>发送</button>
          </div>
        </div>
      </form>
    </section>

    <section id="live-turn" class="live-turn hidden" aria-live="polite">
      <article class="turn-card assistant live">
        <header>Assistant</header>
        <details class="reasoning-panel hidden" id="live-reasoning-panel">
          <summary>Reasoning</summary>
          <pre id="live-reasoning"></pre>
        </details>
        <pre id="live-text"></pre>
      </article>
    </section>

    <section class="history">
      <div id="turn-list" class="turn-list"></div>
    </section>

    <button id="scroll-to-top" class="scroll-to-top" title="回到顶端">↑ 回到顶端</button>
  </main>

  <script>
    window.galateaBootstrap = {
      userId: {{JsonSerializer.Serialize(user.UserId, GalateaJson.Options)}},
      connections: {{connectionsJson}},
      defaultConnectionId: {{defaultConnectionJson}},
      maintenanceMode: {{JsonSerializer.Serialize(maintenanceMode, GalateaJson.Options)}},
      streamLimits: {
        maximumConnectionBytes: {{maximumStreamConnectionBytes}},
        maximumFrameBytes: {{maximumStreamFrameBytes}}
      }
    };
  </script>
    <script type="module" src="{{scriptPath}}"></script>
</body>
</html>
""";
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
    public const string UserId = "family_chat_user_id";
}

internal static class GalateaJson {
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) {
        WriteIndented = false
    };
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(GalateaUsersFileConfig))]
[JsonSerializable(typeof(GalateaUserFileConfig))]
[JsonSerializable(typeof(GalateaSessionProvisioning))]
[JsonSerializable(typeof(GalateaRecapGridFileConfig))]
internal sealed partial class GalateaJsonContext : JsonSerializerContext;
