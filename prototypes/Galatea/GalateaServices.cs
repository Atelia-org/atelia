using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Atelia.Completion;
using Atelia.Diagnostics;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;
using Atelia.EventJournal;
using Atelia.SessionJournal;
using Atelia.SessionJournal.DerivedRecap.Planner;

namespace Atelia.Galatea.Server;

public sealed class GalateaHostService : IAsyncDisposable {
    internal const int RecentTurnLimit = 6;

    private readonly CompletionConnectionRegistry _connections;
    private readonly GalateaInputPreprocessor _inputPreprocessor;
    private readonly string? _callLogDirectory;
    private readonly bool _maintenanceMode;
    private readonly ConcurrentDictionary<string, Lazy<Task<UserSessionHost>>> _sessions = new(StringComparer.Ordinal);
    private readonly IReadOnlyDictionary<string, GalateaUserConfig> _users;

    public GalateaHostService(
        GalateaConfig config,
        CompletionConnectionRegistry connections,
        IGalateaUserMessageNormalizer userMessageNormalizer
    ) {
        ArgumentNullException.ThrowIfNull(config);
        GalateaConfigValidation.RequireDistinctSessionDirectories(
            config.Users
        );
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _inputPreprocessor = new GalateaInputPreprocessor(
            userMessageNormalizer
        );
        _callLogDirectory = config.CallLogDir;
        _maintenanceMode = config.MaintenanceMode;
        _users = config.Users.ToDictionary(x => x.UserId, StringComparer.Ordinal);
    }

    public bool TryGetUser(string userId, out GalateaUserConfig user)
        => _users.TryGetValue(userId, out user!);

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

        var session = await lazy.Value.ConfigureAwait(false);
        DebugUtil.Info(
            "Galatea.Session",
            $"GetSessionAsync: user={userId}"
        );
        return session;
    }

    private StableRecentTurnsProjection BuildRecentTurnsResponse(
        SessionJournalEngine engine,
        RecapPlanningSnapshotDto? recapPlanning = null,
        int maxTurns = RecentTurnLimit
    ) {
        ArgumentNullException.ThrowIfNull(engine);
        SessionCompletedTurnsSnapshot snapshot =
            engine.ReadRecentCompletedTurns(maxTurns);
        IReadOnlyList<RecentTurnDto> turns = [
            .. snapshot.Turns.Select(
                GalateaRecentTurnDisplayAdapter.Project
            )
        ];
        string? rewindLatestToken = snapshot.CapturedHead is { } head
            && snapshot.Turns.FirstOrDefault()?.TerminalAction.Address
                == head
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
                recapPlanning ?? NotObservedRecapPlanning()
            ),
            snapshot.CapturedHead
        );
    }

    public async Task<RecentTurnsResponseDto> GetRecentTurnsAsync(
        UserSessionHost host,
        CancellationToken ct
    ) {
        ArgumentNullException.ThrowIfNull(host);

        if (!host.TurnLock.Wait(0)) {
            return host.GetRecentTurns();
        }
        try {
            return await RefreshRecentTurnsAsync(host, ct)
                .ConfigureAwait(false);
        }
        finally {
            host.TurnLock.Release();
        }
    }

    public Task<CurrentTurnDto> GetCurrentTurnAsync(
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
                $"GetCurrentTurnAsync: user={host.User.UserId}, status={result.Status}, phase={result.Phase ?? "<none>"}, head={recovery.CapturedHead}"
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
            liveTurn.UserMessage,
            liveTurn.Phase,
            liveTurn.Options.ConnectionId
        );
        DebugUtil.Info(
            "Galatea.Session",
            $"BuildLiveCurrentTurn: turnId={result.TurnId}, phase={result.Phase ?? "<none>"}"
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

    private async ValueTask<RecentTurnsResponseDto>
        RefreshRecentTurnsAsync(
        UserSessionHost host,
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        StableRecentTurnsProjection projection = BuildRecentTurnsResponse(
            host.Engine
        );
        RecapPlanningSnapshotDto recapPlanning =
            await GalateaRecapComposition.InspectPlanningAsync(
                    host.Engine,
                    cancellationToken
                )
                .ConfigureAwait(false);
        RecentTurnsResponseDto recent = BindRecapPlanningSnapshot(
            projection.Response,
            projection.CapturedHead,
            recapPlanning
        );
        host.SetRecentTurns(recent);
        return recent;
    }

    internal static RecentTurnsResponseDto BindRecapPlanningSnapshot(
        RecentTurnsResponseDto recent,
        EventAddress? recentCapturedHead,
        RecapPlanningSnapshotDto recapPlanning
    ) {
        ArgumentNullException.ThrowIfNull(recent);
        ArgumentNullException.ThrowIfNull(recapPlanning);
        bool headMismatch = recapPlanning.Freshness
                == GalateaRecapComposition.ExactFreshness
            && (recentCapturedHead is not { } capturedHead
                || !string.Equals(
                    recapPlanning.ObservedRawHead,
                    EventAddressTextCodec.Format(capturedHead),
                    StringComparison.Ordinal
                ));
        if (headMismatch) {
            DebugUtil.Warning(
                "Galatea.Session",
                "Discarding exact DerivedRecap progress because its raw "
                + "head differs from the recent-turn projection."
            );
            recapPlanning = new RecapPlanningSnapshotDto(
                GalateaRecapComposition.StaleFreshness,
                GalateaRecapComposition.UnavailableState,
                Code: "session-head-changed",
                Detail: "会话边界在稳定快照读取期间发生变化，请刷新重试。"
            );
        }
        return recent with {
            RewindLatestToken = headMismatch
                ? null
                : recent.RewindLatestToken,
            RecapPlanning = recapPlanning
        };
    }

    private static RecapPlanningSnapshotDto NotObservedRecapPlanning()
        => new(
            GalateaRecapComposition.StaleFreshness,
            GalateaRecapComposition.NotObservedState,
            Detail: "DerivedRecap进度尚未在稳定会话边界读取。"
        );

    private sealed record StableRecentTurnsProjection(
        RecentTurnsResponseDto Response,
        EventAddress? CapturedHead
    );

    private static CurrentTurnDto BuildDurableCurrentTurn(
        SessionRuntimeRecoveryRequirements recovery
    ) => recovery switch {
        SessionRuntimeRecoveryRequirements.NoRuntimeRequired {
            Phase: SessionExecutionPhase.Idle
        } => new CurrentTurnDto(
            "idle",
            DurablePhase: recovery.Phase.ToString(),
            RecoveryHead: EventAddressTextCodec.FormatNullable(
                recovery.CapturedHead
            )
        ),
        SessionRuntimeRecoveryRequirements
            .FailedTurnMustBeAbandoned failed => new CurrentTurnDto(
                "idle",
                DurablePhase: failed.Phase.ToString(),
                RecoveryHead: EventAddressTextCodec.Format(
                    failed.FailedHead
                )
            ),
        SessionRuntimeRecoveryRequirements.NoRuntimeRequired {
            Phase: SessionExecutionPhase.Empty
        } => RecoveryCurrentTurn(recovery, "unprovisioned"),
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
        string status = "recovery-required",
        bool restartRequired = false
    ) => new(
        status,
        DurablePhase: recovery.Phase.ToString(),
        RecoveryRequired: true,
        RecoveryHead: EventAddressTextCodec.FormatNullable(
            recovery.CapturedHead
        ),
        RestartRequired: restartRequired
    );

    internal GalateaLiveTurn StartTurn(UserSessionHost host, string userMessage, GalateaTurnOptions options) {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(userMessage);
        ArgumentNullException.ThrowIfNull(options);
        return host.StartTurn(userMessage, options);
    }

    internal GalateaLiveTurn StartRecovery(
        UserSessionHost host,
        GalateaTurnOptions options
    ) {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(options);
        return host.StartRecovery(options);
    }

    internal async ValueTask<PopLatestTurnResponseDto?>
        PopLatestTurnAsync(
        UserSessionHost host,
        EventAddress expectedHead,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(host);
        host.MarkRecentSnapshotStale();
        SessionTurnRetractionResult result =
            host.Engine.RewindLatestCompletedTurn(
                expectedHead,
                cancellationToken
            );
        RecentTurnsResponseDto recent =
            await RefreshRecentTurnsBestEffortAsync(
                    host,
                    cancellationToken
                )
                .ConfigureAwait(false);
        if (result is not SessionTurnRetractionResult.Moved moved
            || moved.Turn.TerminalAction is null) {
            return null;
        }
        RecentTurnDto turn = GalateaRecentTurnDisplayAdapter.Project(
            moved.Turn
        );
        return new PopLatestTurnResponseDto(turn, recent);
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

        liveTurn.Publish(new StreamEventDto("meta", new { phase = "turn-start" }), phase: "turn-start");
        DebugUtil.Info(
            "Galatea.Session",
            $"RunTurnAsync start: user={host.User.UserId}, turnId={liveTurn.TurnId}, input={Preview(liveTurn.UserMessage)}, head={host.Engine.ReadCurrentHead()}",
            eventKind: DebugEventKind.Start
        );

        CompletionStreamObserver observer = liveTurn.Observer;
        var toolLoopStarted = 0;
        observer.ReceivedThinkingBegin += () => liveTurn.Publish(
            new StreamEventDto("meta", new { phase = "reasoning-start" }),
            phase: "reasoning-start"
        );
        observer.ReceivedThinkingEnd += () => liveTurn.Publish(
            new StreamEventDto("meta", new { phase = "reasoning-end" }),
            phase: "reasoning-end"
        );
        observer.ReceivedReasoningDelta += delta => liveTurn.Publish(new StreamEventDto("reasoning-delta", new { delta }));
        var textFilter = new InlineThinkTextFilter(startInsideThink: false);
        observer.ReceivedTextDelta += delta => {
            var visibleText = textFilter.Filter(delta);
            if (string.IsNullOrEmpty(visibleText)) { return; }
            liveTurn.Publish(new StreamEventDto("text-delta", new { delta = visibleText }));
        };
        observer.ReceivedToolCall += call => {
            if (Interlocked.Exchange(ref toolLoopStarted, 1) == 0) {
                liveTurn.Publish(new StreamEventDto("meta", new { phase = "tool-loop-start" }), phase: "tool-loop-start");
            }

            liveTurn.Publish(
                new StreamEventDto("meta", new { phase = "tool-call", toolName = call.ToolName, toolCallId = call.ToolCallId }),
                phase: "tool-call"
            );
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
                RequireFailedTurnAbandoned(host.Engine);
                throw new GalateaTurnException(
                    "已停止生成，本轮结果未写入历史。你可以调整开关或修改输入后重试。",
                    "stopped-by-user"
                );
            }
            RequireFailedTurnAbandoned(host.Engine);
            throw new GalateaTurnException(
                "模型本次输出未正常结束，本轮结果已放弃写入历史。请刷新页面后重试。",
                ex.Termination.ProviderReason ?? ex.Termination.Kind.ToString()
            );
        }
        var snapshot = await RefreshRecentTurnsBestEffortAsync(
                host,
                ct
            )
            .ConfigureAwait(false);
        DebugUtil.Info(
            "Galatea.Session",
            $"RunTurnAsync send done: user={host.User.UserId}, turnId={liveTurn.TurnId}, errors={completed.Errors?.Count ?? 0}, snapshotTurns={snapshot.Turns.Count}, head={host.Engine.ReadCurrentHead()}"
        );

        if (Volatile.Read(ref toolLoopStarted) == 1) {
            liveTurn.Publish(new StreamEventDto("meta", new { phase = "tool-loop-finish" }), phase: "tool-loop-finish");
        }

        liveTurn.Publish(
            new StreamEventDto(
                "done",
                new {
                    recent = snapshot,
                    errors = completed.Errors
                }
            ),
            status: "completed"
        );
    }

    private async Task<GalateaCompletedOperation> RunFreshSendAsync(
        UserSessionHost host,
        GalateaLiveTurn liveTurn,
        CompletionStreamObserver observer,
        CancellationToken cancellationToken
    ) {
        SessionRuntimeRecoveryRequirements requirement =
            host.Engine.InspectRuntimeRecoveryRequirements(
                cancellationToken
            );
        if (requirement is SessionRuntimeRecoveryRequirements
                .FailedTurnMustBeAbandoned failed) {
            SessionTurnRetractionResult abandoned =
                host.Engine.AbandonFailedTurn(
                    failed.FailedHead,
                    cancellationToken
                );
            if (abandoned is not SessionTurnRetractionResult.Moved) {
                throw new GalateaTurnException(
                    "上一轮失败状态未能安全放弃，请刷新后重试。",
                    "failed-turn-abandon-race"
                );
            }
            requirement = host.Engine
                .InspectRuntimeRecoveryRequirements(
                    cancellationToken
                );
        }
        if (requirement is not SessionRuntimeRecoveryRequirements
                .NoRuntimeRequired
            || requirement.Phase != SessionExecutionPhase.Idle
            || requirement.CapturedHead is not { } capturedHead) {
            throw RecoveryRequired(requirement);
        }

        CompletionConnectionConfig connection =
            _connections.Resolve(liveTurn.Options.ConnectionId);
        SessionDesiredSetupReconciliationResult reconciled =
            host.Engine.ReconcileDesiredSetup(
                capturedHead,
                new SessionDesiredSetup(
                    connection.ModelId,
                    connection.CompletionSurfaceId,
                    host.User.SystemPrompt
                ),
                cancellationToken
            );
        if (reconciled is not SessionDesiredSetupReconciliationResult
                .Ready setupReady) {
            throw new GalateaTurnException(
                "会话设置无法在当前边界安全更新，请刷新后重试。",
                "desired-setup-unavailable"
            );
        }

        GalateaPreparedRecap prepared =
            await GalateaRecapComposition.PrepareAsync(
                    host.Engine,
                    cancellationToken
                )
                .ConfigureAwait(false);
        if (prepared.Authority.Lineage.CapturedHead
            != setupReady.GoverningSetup.Head) {
            throw new GalateaTurnException(
                "会话在前情提要准备前发生变化，请重试。",
                "session-head-changed"
            );
        }
        string effectiveUserMessage = await _inputPreprocessor
            .ProcessAsync(liveTurn, cancellationToken)
            .ConfigureAwait(false);
        string promptedUserMessage = WrapUserMessageForEngine(
            effectiveUserMessage
        );

        ICompletionClient innerClient =
            _connections.GetClient(connection.Id);
        ICompletionClient agentClient =
            GalateaCompletionLogging.CreateAgentClient(
                innerClient,
                connection,
                _callLogDirectory
            );
        DerivedRecapOnlineLifecycleCoordinator recap =
            GalateaRecapComposition.CreateLifecycle(
                host.Engine,
                prepared,
                connection,
                innerClient,
                _callLogDirectory
            );
        var lifecycleGate = new GalateaFreshSendLifecycleGate(
            recap,
            liveTurn.StopController
        );
        host.Engine.UseRuntime(CreateRuntime(
            connection,
            agentClient,
            recap,
            lifecycleGate,
            SessionUncertainCompletionRecoveryPolicy.Refuse
        ));
        TurnResult result = await host.Engine.SendAsync(
                prepared.Authority.Lineage.CapturedHead,
                promptedUserMessage,
                observer,
                cancellationToken
            )
            .ConfigureAwait(false);
        return new GalateaCompletedOperation(
            result.Message,
            result.Invocation,
            result.Errors
        );
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

        CompletionConnectionConfig connection;
        ICompletionClient innerClient;
        if (requirement is SessionRuntimeRecoveryRequirements
                .NewRequestRequired) {
            connection = _connections.Resolve(
                liveTurn.Options.ConnectionId
            );
            ValidateRecoveryConnection(
                host.Engine,
                capturedHead,
                connection
            );
            GalateaPreparedRecap prepared =
                await GalateaRecapComposition.PrepareAsync(
                        host.Engine,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            if (prepared.Authority.Lineage.CapturedHead
                != capturedHead) {
                throw new GalateaTurnException(
                    "会话在恢复准备期间发生变化，请重试。",
                    "recovery-head-changed"
                );
            }
            innerClient = _connections.GetClient(connection.Id);
            DerivedRecapOnlineLifecycleCoordinator recap =
                GalateaRecapComposition.CreateLifecycle(
                    host.Engine,
                    prepared,
                    connection,
                    innerClient,
                    _callLogDirectory
                );
            var lifecycleGate = new GalateaRecoveryLifecycleGate(
                recap,
                liveTurn.StopController
            );
            ICompletionClient agentClient =
                GalateaCompletionLogging.CreateAgentClient(
                    innerClient,
                    connection,
                    _callLogDirectory
                );
            host.Engine.UseRuntime(CreateRuntime(
                connection,
                agentClient,
                recap,
                lifecycleGate,
                SessionUncertainCompletionRecoveryPolicy.Refuse
            ));
        }
        else if (requirement is SessionRuntimeRecoveryRequirements
                     .FrozenCompletionRequired frozen) {
            string emptyToolSetSha256 =
                SessionVisibleToolSetFingerprint.ComputeSha256(
                    System.Collections.Immutable
                        .ImmutableArray<ToolDefinition>.Empty
                );
            if (frozen.ToolRuntimeIdentity is not null
                || !string.Equals(
                    frozen.VisibleToolSetSha256,
                    emptyToolSetSha256,
                    StringComparison.Ordinal
                )) {
                throw new GalateaTurnException(
                    "已冻结请求包含Galatea G1不支持的工具runtime。",
                    "tool-recovery-unsupported"
                );
            }
            if (frozen.DispatchState
                    == SessionDurableDispatchState
                        .StartedOutcomeUncertain
                && !liveTurn.Options.RestartUncertainCompletion) {
                throw new GalateaTurnException(
                    "上次模型调用结果不确定；必须明确选择重新调用，且可能产生重复请求。",
                    "uncertain-completion-restart-required"
                );
            }
            CompletionDispatchBindingResult binding =
                _connections.BindExact(new CompletionDispatchIdentity(
                    frozen.CompletionTarget.ConnectionId,
                    frozen.CompletionTarget.Kind,
                    frozen.CompletionTarget.ConnectionFingerprint,
                    frozen.ClientName,
                    frozen.ApiSpecId,
                    frozen.CompletionTarget.RequestAdapterFingerprint
                ));
            if (binding is not CompletionDispatchBindingResult.Bound bound) {
                var unavailable =
                    (CompletionDispatchBindingResult.Unavailable)binding;
                throw new GalateaTurnException(
                    "无法精确绑定已冻结的模型调用："
                    + unavailable.Detail,
                    unavailable.Reason.ToString()
                );
            }
            connection = bound.Connection;
            innerClient = bound.Client;
            ICompletionClient agentClient =
                GalateaCompletionLogging.CreateAgentClient(
                    innerClient,
                    connection,
                    _callLogDirectory
                );
            liveTurn.StopController.EnterObserverOnlyOrThrow(
                cancellationToken
            );
            host.Engine.UseRuntime(new SessionRuntime(
                agentClient,
                CompletionTarget: frozen.CompletionTarget,
                MaxTokens: connection.MaxTokens,
                UncertainCompletionRecoveryPolicy:
                    liveTurn.Options.RestartUncertainCompletion
                        ? SessionUncertainCompletionRecoveryPolicy
                            .RestartWithNewAttempt
                        : SessionUncertainCompletionRecoveryPolicy.Refuse
            ));
        }
        else if (requirement is SessionRuntimeRecoveryRequirements
                     .ToolContinuationRequired) {
            throw new GalateaTurnException(
                "当前会话停在工具执行阶段；Galatea G1 尚不支持工具恢复。",
                "tool-recovery-unsupported"
            );
        }
        else if (requirement is SessionRuntimeRecoveryRequirements
                     .FailedTurnMustBeAbandoned) {
            throw new GalateaTurnException(
                "失败轮次必须通过新消息入口在精确边界安全放弃。",
                "failed-turn-must-be-abandoned"
            );
        }
        else if (requirement is SessionRuntimeRecoveryRequirements
                     .NoRuntimeRequired) {
            throw RecoveryRequired(requirement);
        }
        else {
            throw new InvalidDataException(
                "Unknown runtime recovery requirement."
            );
        }

        ResumeOutcome outcome = await host.Engine.ResumeAsync(
                capturedHead,
                observer,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (!outcome.Advanced
            || outcome.Message is null
            || outcome.Invocation is null) {
            throw new GalateaTurnException(
                "当前持久化阶段没有可继续的模型调用。",
                "recovery-did-not-advance"
            );
        }
        return new GalateaCompletedOperation(
            outcome.Message,
            outcome.Invocation,
            outcome.Errors
        );
    }

    public async ValueTask DisposeAsync() {
        foreach (var entry in _sessions.Values) {
            if (!entry.IsValueCreated) { continue; }

            try {
                var session = await entry.Value.ConfigureAwait(false);
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch {
                // Ignore teardown failures during host shutdown.
            }
        }
    }

    private Task<UserSessionHost> CreateSessionAsync(
        GalateaUserConfig user,
        CancellationToken ct
    ) {
        ct.ThrowIfCancellationRequested();
        var sessionDir = Path.GetFullPath(user.SessionDir);
        if (!Directory.Exists(sessionDir)
            || !Directory.EnumerateFileSystemEntries(sessionDir).Any()) {
            throw new GalateaSessionUnavailableException(
                "session-unprovisioned",
                "Galatea requires a provisioned SessionJournal repository."
            );
        }
        SessionJournalEngine engine;
        try {
            engine = _maintenanceMode
                ? SessionJournalEngine.OpenReadOnly(sessionDir)
                : SessionJournalEngine.Open(sessionDir);
        }
        catch (Exception exception) when (
            exception is DirectoryNotFoundException
                or FileNotFoundException
        ) {
            throw new GalateaSessionUnavailableException(
                "session-unprovisioned",
                "Galatea SessionJournal repository is incomplete.",
                exception
            );
        }
        SessionExecutionBoundaryInspection boundary =
            engine.InspectExecutionBoundary(ct);
        DebugUtil.Info(
            "Galatea.Session",
            $"CreateSessionAsync: user={user.UserId}, sessionDir={sessionDir}, phase={boundary.Phase}, head={boundary.Head}"
        );
        RecentTurnsResponseDto recent = BuildRecentTurnsResponse(engine)
            .Response;
        return Task.FromResult(new UserSessionHost(user, engine, recent));
    }

    private static SessionRuntime CreateRuntime(
        CompletionConnectionConfig connection,
        ICompletionClient client,
        ICoherentContextCandidateSource candidates,
        ISessionContextLifecycleCoordinator lifecycle,
        SessionUncertainCompletionRecoveryPolicy recoveryPolicy
    ) => new(
        client,
        CompletionTarget:
            GalateaRecapComposition.CreateCompletionTarget(
                connection,
                client
            ),
        MaxTokens: connection.MaxTokens,
        UncertainCompletionRecoveryPolicy: recoveryPolicy,
        ContextCandidateSource: candidates,
        ContextLifecycle: lifecycle
    );

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

    private static void RequireFailedTurnAbandoned(
        SessionJournalEngine engine
    ) {
        SessionExecutionBoundaryInspection boundary =
            engine.InspectExecutionBoundary();
        if (boundary.Phase != SessionExecutionPhase.TurnFailed
            || boundary.Head is not { } failedHead) {
            throw new GalateaTurnException(
                "失败轮次的持久化边界需要恢复，请刷新后处理。",
                "failed-turn-recovery-required"
            );
        }
        SessionTurnRetractionResult result =
            engine.AbandonFailedTurn(failedHead);
        if (result is not SessionTurnRetractionResult.Moved) {
            throw new GalateaTurnException(
                "失败轮次未能在精确边界安全放弃，请刷新后处理。",
                "failed-turn-recovery-required"
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
        return GalateaUserMessageEnvelope.Wrap(userMessage);
    }

    internal static string NormalizeUserMessageForDisplay(string? storedUserMessage) {
        return GalateaUserMessageEnvelope.UnwrapForDisplay(
            storedUserMessage
        );
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

public sealed class UserSessionHost : IAsyncDisposable {
    private readonly object _turnStateGate = new();
    private GalateaLiveTurn? _currentTurn;
    private GalateaLiveTurn? _lastTurn;
    private RecentTurnsResponseDto _recentTurns;

    public UserSessionHost(
        GalateaUserConfig user,
        SessionJournalEngine engine,
        RecentTurnsResponseDto recentTurns
    ) {
        User = user;
        Engine = engine;
        _recentTurns = recentTurns;
    }

    public GalateaUserConfig User { get; }

    public SessionJournalEngine Engine { get; }

    public SemaphoreSlim TurnLock { get; } = new(1, 1);

    internal GalateaLiveTurn StartTurn(string userMessage, GalateaTurnOptions options) {
        ArgumentException.ThrowIfNullOrWhiteSpace(userMessage);
        var liveTurn = new GalateaLiveTurn(userMessage, options);
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
        var liveTurn = new GalateaLiveTurn(null, options);
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

    private static RecentTurnsResponseDto MarkStale(
        RecentTurnsResponseDto recent
    ) => recent with {
        RewindLatestToken = null,
        RecapPlanning = recent.RecapPlanning is { } recap
            ? recap with {
                Freshness = GalateaRecapComposition.StaleFreshness
            }
            : new RecapPlanningSnapshotDto(
                GalateaRecapComposition.StaleFreshness,
                GalateaRecapComposition.NotObservedState,
                Detail: "DerivedRecap进度尚未在稳定会话边界读取。"
            )
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
            Engine.Dispose();
        }
        finally {
            TurnLock.Release();
        }
    }
}

internal static class GalateaConfigLoader {
    public const string ConnectionsFileName = "connections.json";

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
        var usersFile = JsonSerializer.Deserialize(File.ReadAllText(resolvedPath), GalateaJsonContext.Default.GalateaUsersFileConfig);
        if (usersFile is null) { throw new InvalidOperationException($"Failed to deserialize Galatea config: {resolvedPath}"); }

        var connectionsFile = CompletionConnectionConfigLoader.LoadFile(connectionsPath);

        if (usersFile.Users is not { Count: > 0 }) { throw new InvalidOperationException("Galatea config must contain at least one user."); }

        var config = new GalateaConfig(
            Users: usersFile.Users,
            Connections: connectionsFile.Connections,
            DefaultConnectionId: connectionsFile.DefaultConnectionId!,
            ListenUrls: usersFile.ListenUrls,
            CallLogDir: ResolveCallLogDirectory(
                usersFile.CallLogDir,
                configDir
            ),
            MaintenanceMode: usersFile.MaintenanceMode
        );

        config = ResolveSystemPromptFiles(config, resolvedPath);
        Validate(config);
        return config;
    }

    private static GalateaConfig ResolveSystemPromptFiles(GalateaConfig config, string configPath) {
        string configDir = Path.GetDirectoryName(configPath)
            ?? throw new InvalidOperationException($"Cannot determine config directory for: {configPath}");

        var resolvedUsers = new List<GalateaUserConfig>(config.Users.Count);
        foreach (var user in config.Users) {
            if (string.IsNullOrWhiteSpace(user.SystemPromptFile)) {
                resolvedUsers.Add(user);
                continue;
            }

            string promptPath = Path.GetFullPath(user.SystemPromptFile, configDir);
            if (!File.Exists(promptPath)) {
                throw new FileNotFoundException(
                    $"Galatea user '{user.UserId}' systemPromptFile was not found: {promptPath}",
                    promptPath
                );
            }

            string promptText = File.ReadAllText(promptPath).Trim();
            resolvedUsers.Add(user with { SystemPrompt = promptText });
        }

        return config with { Users = resolvedUsers };
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
        GalateaConfigValidation.RequireDistinctSessionDirectories(
            config.Users
        );
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

            if (string.IsNullOrWhiteSpace(user.SystemPrompt)) {
                throw new InvalidOperationException(
                    $"Galatea config user '{user.UserId}' must provide a non-empty systemPrompt "
                    + "(either inline via 'systemPrompt' or by pointing 'systemPromptFile' at a non-empty file)."
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
                    user.UserId
                );
            }

        }

        if (config.ListenUrls is null) { return; }

        for (int i = 0; i < config.ListenUrls.Count; i++) {
            if (string.IsNullOrWhiteSpace(config.ListenUrls[i])) { throw new InvalidOperationException($"Galatea config listenUrls[{i}] must not be blank."); }
        }
    }

    private static void EnsurePathsDoNotNest(
        string callLogDirectory,
        string sessionDirectory,
        string userId
    ) {
        string callLogs = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(callLogDirectory)
        );
        string session = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(sessionDirectory)
        );
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.Equals(callLogs, session, comparison)
            || IsAncestor(callLogs, session, comparison)
            || IsAncestor(session, callLogs, comparison)) {
            throw new InvalidOperationException(
                $"Galatea callLogDir must be disjoint from sessionDir "
                + $"for user '{userId}'."
            );
        }
    }

    private static bool IsAncestor(
        string ancestor,
        string descendant,
        StringComparison comparison
    ) => descendant.StartsWith(
        ancestor + Path.DirectorySeparatorChar,
        comparison
    );

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

        string connectionsPath = Path.Combine(parentDir, GalateaConfigLoader.ConnectionsFileName);
        bool configExists = File.Exists(resolvedPath);
        bool connectionsExists = File.Exists(connectionsPath);
        if (configExists && connectionsExists) { return; }

        Directory.CreateDirectory(parentDir);

        var jsonOptions = new JsonSerializerOptions(GalateaJson.Options) {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        var generated = new List<string>();
        if (!configExists) {
            File.WriteAllText(
                resolvedPath,
                JsonSerializer.Serialize(GalateaConfigTemplateFactory.CreateUsersFile(), jsonOptions) + Environment.NewLine,
                Encoding.UTF8
            );
            generated.Add(resolvedPath);
        }

        if (!connectionsExists) {
            File.WriteAllText(
                connectionsPath,
                JsonSerializer.Serialize(GalateaConfigTemplateFactory.CreateConnectionsFile(), jsonOptions) + Environment.NewLine,
                Encoding.UTF8
            );
            generated.Add(connectionsPath);
        }

        throw new InvalidOperationException(
            "Galatea config templates have been generated at "
            + string.Join(" and ", generated)
            + ". Please update listenUrls, the connections' modelId / baseAddress / apiKey, and the default account passwords before restarting the server."
        );
    }
}

internal static class GalateaDefaults {
    public const string SystemPrompt =
        "你是家庭局域网里的私人助手。优先用简洁、直接、可信的中文回答。"
        + "不确定时明确说明不确定，不编造细节。";

}

internal static class GalateaConfigTemplateFactory {
    public const string PlaceholderModelId = "REPLACE_WITH_YOUR_LOCAL_MODEL_ID";
    public const string DefaultConnectionId = "local";

    public static GalateaUsersFileConfig CreateUsersFile() {
        return new GalateaUsersFileConfig(
            Users: [
                CreateUser("alice", "alice123", ".atelia/galatea/sessions/alice"),
                CreateUser("bob", "bob123", ".atelia/galatea/sessions/bob"),
            ],
            ListenUrls: ["http://0.0.0.0:3510"]
        );
    }

    public static CompletionConnectionsFileConfig CreateConnectionsFile() {
        return new CompletionConnectionsFileConfig(
            Connections: [
                new CompletionConnectionConfig(
                    Id: DefaultConnectionId,
                    Kind: "openai-chat",
                    ModelId: PlaceholderModelId,
                    CompletionSurfaceId: "openai-chat/qwen-sglang",
                    // Points at a local OpenAI-compatible server by default. The inline
                    // placeholder key lets the config load out of the box; swap in a real
                    // key, or move it to an env var via ApiKeyEnv, before going live.
                    BaseAddress: "http://localhost:8888/",
                    ApiKey: "sk-local-placeholder"
                ),
            ],
            DefaultConnectionId: DefaultConnectionId
        );
    }

    private static GalateaUserConfig CreateUser(string userId, string password, string sessionDir) {
        return new GalateaUserConfig(
            UserId: userId,
            Password: password,
            SessionDir: sessionDir,
            SystemPrompt: GalateaDefaults.SystemPrompt
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
        CompletionConnectionRegistry connections,
        bool maintenanceMode,
        string assetVersion
    ) {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(connections);

        var connectionInfos = connections.Connections
            .Select(
            static c => new GalateaConnectionInfoDto(
                c.Id,
                c.ModelId
            )
        )
            .ToArray();
        string connectionsJson = JsonSerializer.Serialize(connectionInfos, GalateaJson.Options);
        string defaultConnectionJson = JsonSerializer.Serialize(connections.DefaultConnectionId, GalateaJson.Options);
        string maintenanceBanner = maintenanceMode
            ? "<p class=\"maintenance-banner\" role=\"status\">维护模式：会话只读，发送、恢复、撤销与停止已禁用。</p>"
            : string.Empty;
        string maintenanceDisabled = maintenanceMode
            ? " disabled"
            : string.Empty;
        string stylesheetPath = GalateaStaticAssetVersion.AppendToPath("/assets/galatea.css", assetVersion);
        string scriptPath = GalateaStaticAssetVersion.AppendToPath("/assets/galatea.js", assetVersion);

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
        <section id="recap-planning-status" class="recap-planning-status hidden" aria-live="polite" title="HistoryLoad 是 DerivedRecap cadence 的内部度量，不是模型 token 数。">
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
      maintenanceMode: {{JsonSerializer.Serialize(maintenanceMode, GalateaJson.Options)}}
    };
  </script>
    <script src="{{scriptPath}}"></script>
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
    public static async Task WriteEventAsync(HttpResponse response, string eventName, object? payload, CancellationToken ct) {
        string json = JsonSerializer.Serialize(payload, GalateaJson.Options);
        await response.WriteAsync($"event: {eventName}\n", ct);
        await response.WriteAsync($"data: {json}\n\n", ct);
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
[JsonSerializable(typeof(GalateaUserConfig))]
internal sealed partial class GalateaJsonContext : JsonSerializerContext;
