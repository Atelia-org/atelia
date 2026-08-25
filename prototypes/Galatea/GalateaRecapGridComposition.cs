using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.SessionJournal;
using Atelia.SessionJournal.RecapGrid.Hosting;
using Atelia.SessionJournal.RecapGrid.Online;
using Atelia.SessionJournal.RecapGrid.AgentControl;

namespace Atelia.Galatea.Server;

/// <summary>
/// Formal Galatea RecapGrid composition. It owns the single host-wide
/// completion registry and creates bounded per-turn bindings.
/// </summary>
internal sealed class GalateaRecapGridComposition
    : IAsyncDisposable {
    private readonly RecapGridCompletionHost _completion;
    private readonly RecapGridOnlineLimits _limits;
    private readonly IHistoryUnitLoadEstimator[] _estimators;
    private readonly string? _agentControlProfileId;

    internal GalateaRecapGridComposition(
        RecapGridCompletionHost completion,
        RecapGridOnlineLimits? limits = null,
        params IHistoryUnitLoadEstimator[] estimators
    ) : this(completion, null, limits, estimators, initialize: true) {
    }

    internal GalateaRecapGridComposition(
        RecapGridCompletionHost completion,
        string agentControlProfileId,
        RecapGridOnlineLimits? limits = null,
        params IHistoryUnitLoadEstimator[] estimators
    ) : this(
        completion,
        (string?)agentControlProfileId,
        limits,
        estimators,
        initialize: true
    ) {
        if (string.IsNullOrWhiteSpace(agentControlProfileId)) {
            throw new ArgumentException(
                "Agent Control profile id must be non-empty.",
                nameof(agentControlProfileId)
            );
        }
    }

    private GalateaRecapGridComposition(
        RecapGridCompletionHost completion,
        string? agentControlProfileId,
        RecapGridOnlineLimits? limits,
        IHistoryUnitLoadEstimator[] estimators,
        bool initialize
    ) {
        _ = initialize;
        _completion = completion
            ?? throw new ArgumentNullException(nameof(completion));
        _limits = limits ?? RecapGridOnlineLimits.Production;
        ArgumentNullException.ThrowIfNull(estimators);
        if (estimators.Length == 0
            || estimators.Any(static value => value is null)) {
            throw new ArgumentException(
                "At least one exact HistoryLoad estimator is required.",
                nameof(estimators));
        }
        _estimators = estimators.ToArray();
        _agentControlProfileId = agentControlProfileId;
    }

    internal CompletionConnectionConfig InspectConnectionExact(
        string connectionId
    ) => _completion.InspectAgentExact(connectionId) switch {
        RecapGridAgentConnectionLookupResult.Found found
            => found.Connection,
        RecapGridAgentConnectionLookupResult.Absent
            => throw new GalateaTurnException(
                "RecapGrid composition缺少精确模型连接。",
                "recap-grid-connection-absent"),
        RecapGridAgentConnectionLookupResult.Invalid invalid
            => throw new GalateaTurnException(
                $"RecapGrid模型连接无效：{invalid.Code}",
                "recap-grid-connection-invalid"),
        _ => throw new InvalidDataException(
            "Unknown RecapGrid connection lookup outcome.")
    };

    internal async ValueTask<GalateaRecapGridTurn> OpenFreshAsync(
        SessionJournalEngine engine,
        string connectionId,
        string? pendingObservation,
        CancellationToken cancellationToken
    ) {
        RecapGridAgentConnectionLookupResult lookup =
            _completion.InspectAgentExact(connectionId);
        if (lookup is not RecapGridAgentConnectionLookupResult.Found found) {
            throw lookup switch {
                RecapGridAgentConnectionLookupResult.Absent
                    => new GalateaTurnException(
                        "RecapGrid composition缺少精确模型连接。",
                        "recap-grid-connection-absent"),
                RecapGridAgentConnectionLookupResult.Invalid invalid
                    => new GalateaTurnException(
                        $"RecapGrid模型连接无法构造：{invalid.Code}",
                        "recap-grid-connection-invalid"),
                _ => new InvalidDataException(
                    "Unknown RecapGrid agent binding outcome.")
            };
        }
        RecapGridOnlineOpenResult opened = RecapGridOnlineFactory.Open(
            engine,
            _completion.Executor,
            _limits,
            _estimators);
        if (opened is not RecapGridOnlineOpenResult.Opened available) {
            throw CandidateOpenFailure(opened);
        }
        RecapGridOnlineContextHandle? ownedOnline = available.Handle;
        RecapGridAgentControlHandle? agentControl = null;
        try {
            RecapGridOnlinePassResult caughtUp = await ownedOnline
                .CatchUpMaintenanceAsync(
                    pendingObservation,
                    cancellationToken).ConfigureAwait(false);
            if (caughtUp is not RecapGridOnlinePassResult.Ready
                    and not RecapGridOnlinePassResult.RawHistoryAuthorized) {
                throw CatchUpFailure(caughtUp);
            }
            RecapGridAgentConnectionResult agent =
                _completion.BindAgentExact(connectionId);
            if (agent is not RecapGridAgentConnectionResult.Bound bound) {
                throw new GalateaTurnException(
                    "RecapGrid无法构造精确模型连接。",
                    "recap-grid-connection-invalid");
            }
            if (_agentControlProfileId is not null) {
                RecapGridAgentControlOpenResult toolOpened =
                    _completion.OpenAgentControl(
                        engine.ReadView,
                        _agentControlProfileId,
                        _estimators
                    );
                if (toolOpened is not RecapGridAgentControlOpenResult
                        .Opened tool) {
                    throw AgentControlOpenFailure(toolOpened);
                }
                agentControl = tool.Handle;
            }
            var turn = new GalateaRecapGridTurn(
                bound.Connection,
                bound.Client,
                bound.Identity,
                ownedOnline,
                agentControl,
                maintenanceEvidence: ExtractEvidence(caughtUp));
            ownedOnline = null;
            agentControl = null;
            return turn;
        }
        finally {
            agentControl?.Dispose();
            if (ownedOnline is not null) {
                await ownedOnline.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    internal GalateaRecapGridTurn BindPrepared(
        SessionJournalEngine engine,
        SessionRuntimeRecoveryRequirements.FrozenCompletionRequired frozen
    ) {
        ArgumentNullException.ThrowIfNull(frozen);
        var required = new CompletionDispatchIdentity(
            frozen.CompletionTarget.ConnectionId,
            frozen.CompletionTarget.Kind,
            frozen.CompletionTarget.ConnectionFingerprint,
            frozen.ClientName,
            frozen.ApiSpecId,
            frozen.CompletionTarget.RequestAdapterFingerprint);
        CompletionDispatchBindingResult binding =
            _completion.BindPreparedExact(required);
        if (binding is not CompletionDispatchBindingResult.Bound bound) {
            var unavailable = binding as CompletionDispatchBindingResult
                .Unavailable ?? throw new InvalidDataException(
                    "Unknown prepared RecapGrid binding outcome.");
            throw new GalateaTurnException(
                "无法精确绑定RecapGrid已冻结模型调用。",
                unavailable.Reason.ToString());
        }
        RecapGridAgentControlHandle? agentControl = BindFrozenAgentControl(
            engine,
            frozen.ToolRuntimeIdentity,
            frozen.VisibleToolSetSha256
        );
        return new GalateaRecapGridTurn(
            bound.Connection,
            bound.Client,
            required,
            online: null,
            agentControl);
    }

    internal async ValueTask<GalateaRecapGridTurn> BindToolContinuationAsync(
        SessionJournalEngine engine,
        string connectionId,
        SessionRuntimeRecoveryRequirements.ToolContinuationRequired frozen,
        CancellationToken cancellationToken
    ) {
        using RecapGridAgentControlHandle frozenAgentControl =
            BindFrozenAgentControl(
                engine,
                frozen.ToolRuntimeIdentity,
                visibleToolSetSha256: null
            ) ?? throw new GalateaTurnException(
                "冻结工具runtime缺少Agent Control profile。",
                "tool-runtime-profile-absent"
            );
        EventAddress toolHead = frozen.CapturedHead
            ?? throw new InvalidDataException(
                "Tool continuation has no captured raw head."
            );
        while (true) {
            SessionPendingToolBoundaryResult boundary = await engine
                .ExecutePendingToolToBoundaryAsync(
                    toolHead,
                    frozenAgentControl.ToolSession,
                    frozen.ToolRuntimeIdentity,
                    cancellationToken
                ).ConfigureAwait(false);
            toolHead = boundary switch {
                SessionPendingToolBoundaryResult.Settled value => value.Head,
                SessionPendingToolBoundaryResult.MorePending value
                    => value.Head,
                _ => throw new InvalidDataException(
                    "Unknown pending tool boundary result."
                )
            };
            if (boundary is SessionPendingToolBoundaryResult.Settled) {
                break;
            }
        }
        RecapGridOnlineOpenResult onlineOpened = RecapGridOnlineFactory.Open(
            engine,
            _completion.Executor,
            _limits,
            _estimators
        );
        if (onlineOpened is not RecapGridOnlineOpenResult.Opened online) {
            throw CandidateOpenFailure(onlineOpened);
        }
        RecapGridOnlineContextHandle? ownedOnline = online.Handle;
        RecapGridAgentControlHandle? currentAgentControl = null;
        try {
            RecapGridOnlinePassResult caughtUp = await ownedOnline
                .CatchUpMaintenanceAsync(
                    pendingObservation: null,
                    cancellationToken).ConfigureAwait(false);
            if (caughtUp is not RecapGridOnlinePassResult.Ready
                    and not RecapGridOnlinePassResult.RawHistoryAuthorized) {
                throw CatchUpFailure(caughtUp);
            }
            RecapGridAgentConnectionResult agent =
                _completion.BindAgentExact(connectionId);
            if (agent is not RecapGridAgentConnectionResult.Bound bound) {
                throw new GalateaTurnException(
                    "RecapGrid无法绑定当前模型连接。",
                    "recap-grid-connection-absent"
                );
            }
            if (_agentControlProfileId is not null) {
                RecapGridAgentControlOpenResult current =
                    _completion.OpenAgentControl(
                        engine.ReadView,
                        _agentControlProfileId,
                        _estimators
                    );
                if (current is not RecapGridAgentControlOpenResult
                        .Opened opened) {
                    throw AgentControlOpenFailure(current);
                }
                currentAgentControl = opened.Handle;
            }
            var turn = new GalateaRecapGridTurn(
                bound.Connection,
                bound.Client,
                bound.Identity,
                ownedOnline,
                currentAgentControl,
                toolHead,
                ExtractEvidence(caughtUp));
            ownedOnline = null;
            currentAgentControl = null;
            return turn;
        }
        finally {
            currentAgentControl?.Dispose();
            if (ownedOnline is not null) {
                await ownedOnline.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public ValueTask DisposeAsync() => _completion.DisposeAsync();

    private static RecapGridOnlineMaintenanceEvidence? ExtractEvidence(
        RecapGridOnlinePassResult result
    ) => result switch {
        RecapGridOnlinePassResult.Ready value => value.Evidence,
        RecapGridOnlinePassResult.RawHistoryAuthorized value
            => value.Evidence,
        RecapGridOnlinePassResult.MaintenanceContinuation value
            => value.Evidence,
        RecapGridOnlinePassResult.Backpressure value
            => value.MaintenanceEvidence,
        RecapGridOnlinePassResult.Unavailable value
            => value.MaintenanceEvidence,
        _ => null
    };

    private static Exception CatchUpFailure(
        RecapGridOnlinePassResult result
    ) => result switch {
        RecapGridOnlinePassResult.MaintenanceContinuation value
            => new GalateaTurnException(
                $"RecapGrid维护尚未完成：{value.Code}",
                "recap-grid-maintenance-continuation"),
        RecapGridOnlinePassResult.Backpressure value
            => new GalateaTurnException(
                $"RecapGrid维护受阻：{value.Code}",
                "recap-grid-maintenance-backpressure"),
        RecapGridOnlinePassResult.Unavailable value
            => new GalateaTurnException(
                $"RecapGrid维护不可用：{value.Code}",
                "recap-grid-maintenance-unavailable"),
        _ => new GalateaTurnException(
            "RecapGrid维护已关闭。",
            "recap-grid-maintenance-disposed")
    };

    private static Exception CandidateOpenFailure(
        RecapGridOnlineOpenResult result
    ) => result switch {
        RecapGridOnlineOpenResult.Absent absent
            => new GalateaTurnException(
                $"RecapGrid尚未由operator provision：{absent.Component}",
                "recap-grid-unprovisioned"),
        RecapGridOnlineOpenResult.Busy busy
            => new GalateaTurnException(
                $"RecapGrid当前繁忙：{busy.Component}",
                "recap-grid-busy"),
        RecapGridOnlineOpenResult.UnsupportedSchema unsupported
            => new GalateaTurnException(
                $"RecapGrid schema不受支持：{unsupported.Component}",
                "recap-grid-unsupported-schema"),
        RecapGridOnlineOpenResult.DisposedRawAuthority
            => new GalateaTurnException(
                "RecapGrid raw owner已关闭。",
                "recap-grid-disposed"),
        RecapGridOnlineOpenResult.Invalid invalid
            => new GalateaTurnException(
                $"RecapGrid无效：{invalid.Component}:{invalid.Code}",
                "recap-grid-invalid"),
        _ => new InvalidDataException(
            "Unknown RecapGrid open outcome.")
    };

    private RecapGridAgentControlHandle? BindFrozenAgentControl(
        SessionJournalEngine engine,
        SessionToolRuntimeIdentity? runtimeIdentity,
        string? visibleToolSetSha256
    ) {
        if (runtimeIdentity is null) {
            return null;
        }
        RecapGridAgentControlOpenResult opened =
            _completion.BindAgentControlExact(
                engine.ReadView,
                runtimeIdentity,
                _estimators
            );
        if (opened is not RecapGridAgentControlOpenResult.Opened value) {
            throw AgentControlOpenFailure(opened);
        }
        if (visibleToolSetSha256 is not null
            && !string.Equals(
                SessionVisibleToolSetFingerprint.ComputeSha256(
                    value.Handle.ToolSession.VisibleDefinitions
                ),
                visibleToolSetSha256,
                StringComparison.Ordinal
            )) {
            value.Handle.Dispose();
            throw new GalateaTurnException(
                "冻结工具集合与Agent Control profile不一致。",
                "tool-set-fingerprint-mismatch"
            );
        }
        return value.Handle;
    }

    private static Exception AgentControlOpenFailure(
        RecapGridAgentControlOpenResult result
    ) => result switch {
        RecapGridAgentControlOpenResult.ProfileAbsent
            => new GalateaTurnException(
                "Agent Control profile不存在。",
                "tool-runtime-profile-absent"),
        RecapGridAgentControlOpenResult.Busy value
            => new GalateaTurnException(
                $"Agent Control繁忙：{value.Component}",
                "agent-control-busy"),
        RecapGridAgentControlOpenResult.UnsupportedSchema value
            => new GalateaTurnException(
                $"Agent Control schema不受支持：{value.Component}",
                "agent-control-unsupported"),
        RecapGridAgentControlOpenResult.ControlAbsent
            or RecapGridAgentControlOpenResult.TimelineAbsent
            => new GalateaTurnException(
                "Agent Control尚未provision。",
                "agent-control-unprovisioned"),
        RecapGridAgentControlOpenResult.Invalid value
            => new GalateaTurnException(
                $"Agent Control无效：{value.Component}:{value.Code}",
                "agent-control-invalid"),
        _ => new InvalidDataException(
            "Unknown Agent Control open outcome.")
    };
}

internal sealed class GalateaRecapGridTurn : IAsyncDisposable {
    internal GalateaRecapGridTurn(
        CompletionConnectionConfig connection,
        ICompletionClient client,
        CompletionDispatchIdentity identity,
        RecapGridOnlineContextHandle? online,
        RecapGridAgentControlHandle? agentControl,
        EventAddress? resumeHead = null,
        RecapGridOnlineMaintenanceEvidence? maintenanceEvidence = null
    ) {
        Connection = connection;
        Client = client;
        Identity = identity;
        Online = online;
        AgentControl = agentControl;
        ResumeHead = resumeHead;
        MaintenanceEvidence = maintenanceEvidence;
    }

    internal CompletionConnectionConfig Connection { get; }
    internal ICompletionClient Client { get; }
    internal CompletionDispatchIdentity Identity { get; }
    internal RecapGridOnlineContextHandle? Online { get; }
    internal RecapGridAgentControlHandle? AgentControl { get; }
    internal EventAddress? ResumeHead { get; }
    internal RecapGridOnlineMaintenanceEvidence? MaintenanceEvidence {
        get;
    }

    public async ValueTask DisposeAsync() {
        try {
            if (Online is not null) {
                await Online.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally {
            AgentControl?.Dispose();
        }
    }
}
