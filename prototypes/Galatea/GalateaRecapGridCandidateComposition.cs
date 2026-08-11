using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.SessionJournal;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid.Hosting;
using Atelia.SessionJournal.RecapGrid.Online;

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

    internal GalateaRecapGridCandidateComposition(
        RecapGridCompletionHost completion,
        RecapGridOnlineLimits? limits = null,
        params IHistoryUnitLoadEstimator[] estimators
    ) {
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
        return new GalateaRecapGridCandidateTurn(
            bound.Connection,
            bound.Client,
            bound.Identity,
            available.Handle);
    }

    internal GalateaRecapGridCandidateTurn BindPrepared(
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
        return new GalateaRecapGridCandidateTurn(
            bound.Connection,
            bound.Client,
            required,
            online: null);
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
}

internal sealed class GalateaRecapGridCandidateTurn : IAsyncDisposable {
    internal GalateaRecapGridCandidateTurn(
        CompletionConnectionConfig connection,
        ICompletionClient client,
        CompletionDispatchIdentity identity,
        RecapGridOnlineContextHandle? online
    ) {
        Connection = connection;
        Client = client;
        Identity = identity;
        Online = online;
    }

    internal CompletionConnectionConfig Connection { get; }
    internal ICompletionClient Client { get; }
    internal CompletionDispatchIdentity Identity { get; }
    internal RecapGridOnlineContextHandle? Online { get; }

    public ValueTask DisposeAsync()
        => Online?.DisposeAsync() ?? ValueTask.CompletedTask;
}
