using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.SessionJournal;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid.Hosting;
using Atelia.SessionJournal.RecapGrid.Online;
using Atelia.SessionJournal.RecapGrid.AgentControl;

namespace Atelia.Galatea.Server;

/// <summary>
/// Explicit test/candidate-only RecapGrid composition. Production DI does not
/// construct or register this type; the existing DerivedRecap path remains the
/// only public Galatea default until the cutover work package.
/// </summary>
internal sealed class GalateaRecapGridCandidateComposition
    : IAsyncDisposable {
    private readonly RecapGridCompletionHost _completion;
    private readonly RecapGridOnlineLimits _limits;
    private readonly IHistoryUnitLoadEstimator[] _estimators;
    private readonly string? _agentControlProfileId;

    internal GalateaRecapGridCandidateComposition(
        RecapGridCompletionHost completion,
        RecapGridOnlineLimits? limits = null,
        params IHistoryUnitLoadEstimator[] estimators
    ) : this(completion, null, limits, estimators, initialize: true) {
    }

    internal GalateaRecapGridCandidateComposition(
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

    private GalateaRecapGridCandidateComposition(
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
                "候选RecapGrid composition缺少精确模型连接。",
                "candidate-connection-absent"),
        RecapGridAgentConnectionLookupResult.Invalid invalid
            => throw new GalateaTurnException(
                $"候选RecapGrid模型连接无效：{invalid.Code}",
                "candidate-connection-invalid"),
        _ => throw new InvalidDataException(
            "Unknown candidate connection lookup outcome.")
    };

    internal GalateaRecapGridCandidateTurn OpenFresh(
        SessionJournalEngine engine,
        string connectionId
    ) {
        RecapGridAgentConnectionResult agent =
            _completion.BindAgentExact(connectionId);
        if (agent is not RecapGridAgentConnectionResult.Bound bound) {
            throw agent switch {
                RecapGridAgentConnectionResult.Absent
                    => new GalateaTurnException(
                        "候选RecapGrid composition缺少精确模型连接。",
                        "candidate-connection-absent"),
                RecapGridAgentConnectionResult.Invalid invalid
                    => new GalateaTurnException(
                        $"候选RecapGrid模型连接无法构造：{invalid.Code}",
                        "candidate-connection-invalid"),
                _ => new InvalidDataException(
                    "Unknown candidate agent binding outcome.")
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
        RecapGridAgentControlHandle? agentControl = null;
        if (_agentControlProfileId is not null) {
            RecapGridAgentControlOpenResult toolOpened =
                _completion.OpenAgentControl(
                    engine.ReadView,
                    _agentControlProfileId,
                    _estimators
                );
            if (toolOpened is not RecapGridAgentControlOpenResult.Opened tool) {
                available.Handle.Dispose();
                throw AgentControlOpenFailure(toolOpened);
            }
            agentControl = tool.Handle;
        }
        return new GalateaRecapGridCandidateTurn(
            bound.Connection,
            bound.Client,
            bound.Identity,
            available.Handle,
            agentControl);
    }

    internal GalateaRecapGridCandidateTurn BindPrepared(
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
                    "Unknown prepared candidate binding outcome.");
            throw new GalateaTurnException(
                "无法精确绑定候选RecapGrid已冻结模型调用。",
                unavailable.Reason.ToString());
        }
        RecapGridAgentControlHandle? agentControl = BindFrozenAgentControl(
            engine,
            frozen.ToolRuntimeIdentity,
            frozen.VisibleToolSetSha256
        );
        return new GalateaRecapGridCandidateTurn(
            bound.Connection,
            bound.Client,
            required,
            online: null,
            agentControl);
    }

    internal GalateaRecapGridCandidateTurn BindToolContinuation(
        SessionJournalEngine engine,
        string connectionId,
        SessionRuntimeRecoveryRequirements.ToolContinuationRequired frozen
    ) {
        RecapGridAgentControlHandle agentControl =
            BindFrozenAgentControl(
                engine,
                frozen.ToolRuntimeIdentity,
                visibleToolSetSha256: null
            ) ?? throw new GalateaTurnException(
                "冻结工具runtime缺少Agent Control profile。",
                "tool-runtime-profile-absent"
            );
        RecapGridAgentConnectionResult agent =
            _completion.BindAgentExact(connectionId);
        if (agent is not RecapGridAgentConnectionResult.Bound bound) {
            agentControl.Dispose();
            throw new GalateaTurnException(
                "候选RecapGrid无法绑定当前模型连接。",
                "candidate-connection-absent"
            );
        }
        RecapGridOnlineOpenResult onlineOpened = RecapGridOnlineFactory.Open(
            engine,
            _completion.Executor,
            _limits,
            _estimators
        );
        if (onlineOpened is not RecapGridOnlineOpenResult.Opened online) {
            agentControl.Dispose();
            throw CandidateOpenFailure(onlineOpened);
        }
        return new GalateaRecapGridCandidateTurn(
            bound.Connection,
            bound.Client,
            bound.Identity,
            online.Handle,
            agentControl
        );
    }

    public ValueTask DisposeAsync() => _completion.DisposeAsync();

    private static Exception CandidateOpenFailure(
        RecapGridOnlineOpenResult result
    ) => result switch {
        RecapGridOnlineOpenResult.Absent absent
            => new GalateaTurnException(
                $"候选RecapGrid尚未由operator provision：{absent.Component}",
                "candidate-recap-grid-unprovisioned"),
        RecapGridOnlineOpenResult.Busy busy
            => new GalateaTurnException(
                $"候选RecapGrid当前繁忙：{busy.Component}",
                "candidate-recap-grid-busy"),
        RecapGridOnlineOpenResult.UnsupportedSchema unsupported
            => new GalateaTurnException(
                $"候选RecapGrid schema不受支持：{unsupported.Component}",
                "candidate-recap-grid-unsupported-schema"),
        RecapGridOnlineOpenResult.DisposedRawAuthority
            => new GalateaTurnException(
                "候选RecapGrid raw owner已关闭。",
                "candidate-recap-grid-disposed"),
        RecapGridOnlineOpenResult.Invalid invalid
            => new GalateaTurnException(
                $"候选RecapGrid无效：{invalid.Component}:{invalid.Code}",
                "candidate-recap-grid-invalid"),
        _ => new InvalidDataException(
            "Unknown candidate RecapGrid open outcome.")
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
                "候选Agent Control profile不存在。",
                "tool-runtime-profile-absent"),
        RecapGridAgentControlOpenResult.Busy value
            => new GalateaTurnException(
                $"候选Agent Control繁忙：{value.Component}",
                "candidate-agent-control-busy"),
        RecapGridAgentControlOpenResult.UnsupportedSchema value
            => new GalateaTurnException(
                $"候选Agent Control schema不受支持：{value.Component}",
                "candidate-agent-control-unsupported"),
        RecapGridAgentControlOpenResult.ControlAbsent
            or RecapGridAgentControlOpenResult.TimelineAbsent
            => new GalateaTurnException(
                "候选Agent Control尚未provision。",
                "candidate-agent-control-unprovisioned"),
        RecapGridAgentControlOpenResult.Invalid value
            => new GalateaTurnException(
                $"候选Agent Control无效：{value.Component}:{value.Code}",
                "candidate-agent-control-invalid"),
        _ => new InvalidDataException(
            "Unknown Agent Control open outcome.")
    };
}

internal sealed class GalateaRecapGridCandidateTurn : IAsyncDisposable {
    internal GalateaRecapGridCandidateTurn(
        CompletionConnectionConfig connection,
        ICompletionClient client,
        CompletionDispatchIdentity identity,
        RecapGridOnlineContextHandle? online,
        RecapGridAgentControlHandle? agentControl
    ) {
        Connection = connection;
        Client = client;
        Identity = identity;
        Online = online;
        AgentControl = agentControl;
    }

    internal CompletionConnectionConfig Connection { get; }
    internal ICompletionClient Client { get; }
    internal CompletionDispatchIdentity Identity { get; }
    internal RecapGridOnlineContextHandle? Online { get; }
    internal RecapGridAgentControlHandle? AgentControl { get; }

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
