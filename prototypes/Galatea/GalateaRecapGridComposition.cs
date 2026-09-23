using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.SessionJournal;
using Atelia.SessionJournal.RecapGrid;
using Atelia.SessionJournal.RecapGrid.Control;
using Atelia.SessionJournal.RecapGrid.Hosting;
using Atelia.SessionJournal.RecapGrid.Online;

namespace Atelia.Galatea.Server;

/// <summary>
/// Formal Galatea RecapGrid composition. It owns the single host-wide
/// completion registry and creates bounded per-turn bindings. Fresh/current
/// completions carry no Agent Control tools; only frozen recovery may bind a
/// historical tool runtime.
/// </summary>
internal sealed class GalateaRecapGridComposition
    : IAsyncDisposable {
    private readonly RecapGridCompletionHost _completion;
    private readonly RecapGridOnlineLimits _limits;
    private readonly IHistoryUnitLoadEstimator[] _estimators;

    internal GalateaRecapGridComposition(
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
        SessionInputContent? pendingObservation,
        GalateaRecapGridDefaultPolicy defaultPolicy,
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
        cancellationToken.ThrowIfCancellationRequested();
        RegisterDefaultPolicy(engine, defaultPolicy);
        RecapGridOnlineOpenResult opened = RecapGridOnlineFactory.Open(
            engine,
            _completion.Executor,
            defaultPolicy.Target,
            _limits,
            _estimators);
        if (opened is not RecapGridOnlineOpenResult.Opened available) {
            throw CandidateOpenFailure(opened);
        }
        RecapGridOnlineContextHandle? ownedOnline = available.Handle;
        try {
            RecapGridOnlinePassResult caughtUp = await ownedOnline
                .CatchUpMaintenanceAsync(
                    pendingObservation,
                    cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
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
            var turn = new GalateaRecapGridTurn(
                bound.Connection,
                bound.Client,
                bound.Identity,
                ownedOnline,
                rawHistoryAuthorized:
                    caughtUp is RecapGridOnlinePassResult
                        .RawHistoryAuthorized,
                maintenanceEvidence: ExtractEvidence(caughtUp));
            ownedOnline = null;
            return turn;
        }
        finally {
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
        if (frozen.ToolRuntimeIdentity is not null) {
            throw new GalateaTurnException(
                "冻结回合需要已移除的历史RecapGrid工具运行环境。",
                "tool-runtime-unsupported");
        }
        var required = new CompletionDispatchIdentity(
            frozen.CompletionTarget.ConnectionId,
            frozen.CompletionTarget.Kind,
            frozen.CompletionTarget.ConnectionFingerprint,
            frozen.ClientName,
            frozen.ApiSpecId);
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
        return new GalateaRecapGridTurn(
            bound.Connection,
            bound.Client,
            required,
            online: null);
    }

    public ValueTask DisposeAsync() => _completion.DisposeAsync();

    private static void RegisterDefaultPolicy(
        SessionJournalEngine engine,
        GalateaRecapGridDefaultPolicy defaultPolicy
    ) {
        RecapGridControlRegistrationBundle? bundle =
            defaultPolicy.RegistrationBundle;
        if (bundle is null) {
            return;
        }
        if (IsDefaultPolicyRegistered(engine, bundle)) {
            return;
        }
        var admission = new RecapGridControlAdmission(
            RecapGridControlPermission.RegisterFamily
                | RecapGridControlPermission.RegisterDefinition,
            bundle.Families.Select(static family => family.Digest),
            bundle.Definitions.Select(static definition =>
                definition.Capability.CapabilityFingerprint),
            bundle.Definitions.Select(static definition =>
                definition.Target.Carrier),
            bundle.Definitions.Select(static definition =>
                definition.LogicalColumnId.Value),
            maximumBootstrapRows: 0,
            maximumProjectedCalls: 0
        );
        bool indeterminate = false;
        {
            RecapGridControlOpenResult opened = RecapGridControlFactory.Open(
                engine.Path,
                engine.BranchRefId,
                admission
            );
            if (opened is not RecapGridControlOpenResult.Opened available) {
                throw PolicyRegistrationFailure(opened);
            }
            using RecapGridControlHandle control = available.Handle;
            RecapGridControlSnapshotResult read = control.Reader.ReadSnapshot();
            if (read is not RecapGridControlSnapshotResult.Available current) {
                throw PolicyRegistrationFailure(read);
            }
            ControlHeadRef head = current.Snapshot.Head;
            foreach (FamilyDefinition family in bundle.Families) {
                if (HasExactFamily(current.Snapshot, family)) {
                    continue;
                }
                if (!TryRequireRegistered(
                        control.Coordinator.PutFamilyDefinition(head, family),
                        out head)) {
                    indeterminate = true;
                    break;
                }
            }
            if (!indeterminate) {
                foreach (MaintainerDefinitionRevision definition
                         in bundle.Definitions) {
                    if (HasExactDefinition(current.Snapshot, definition)) {
                        continue;
                    }
                    if (!TryRequireRegistered(
                            control.Coordinator.PutMaintainerDefinition(
                                head,
                                definition
                            ),
                            out head)) {
                        indeterminate = true;
                        break;
                    }
                }
            }
        }
        if (indeterminate) {
            if (IsDefaultPolicyRegistered(engine, bundle)) {
                return;
            }
            throw new GalateaTurnException(
                "RecapGrid默认策略注册结果不确定；必须重新读取后再尝试。",
                "recap-grid-policy-registration-indeterminate"
            );
        }
    }

    private static bool IsDefaultPolicyRegistered(
        SessionJournalEngine engine,
        RecapGridControlRegistrationBundle bundle
    ) {
        RecapGridControlReaderOpenResult opened = RecapGridControlFactory
            .OpenReader(engine.Path, engine.BranchRefId);
        if (opened is not RecapGridControlReaderOpenResult.Opened available) {
            throw PolicyRegistrationFailure(opened);
        }
        using RecapGridControlReaderHandle control = available.Handle;
        RecapGridControlSnapshotResult read = control.Reader.ReadSnapshot();
        if (read is not RecapGridControlSnapshotResult.Available current) {
            throw PolicyRegistrationFailure(read);
        }
        return bundle.Families.All(family =>
                HasExactFamily(current.Snapshot, family))
            && bundle.Definitions.All(definition =>
                HasExactDefinition(current.Snapshot, definition));
    }

    private static bool HasExactFamily(
        RecapGridControlSnapshot snapshot,
        FamilyDefinition expected
    ) {
        FamilyDefinition? existing = snapshot.Families.SingleOrDefault(
            family => family.Digest == expected.Digest);
        if (existing is null) {
            return false;
        }
        if (!existing.ToCanonicalBytes().SequenceEqual(
                expected.ToCanonicalBytes())) {
            throw PolicyRegistrationFailure("FamilyDigestCollision");
        }
        return true;
    }

    private static bool HasExactDefinition(
        RecapGridControlSnapshot snapshot,
        MaintainerDefinitionRevision expected
    ) {
        MaintainerDefinitionRevision? existing = snapshot.Definitions
            .SingleOrDefault(definition =>
                definition.Digest == expected.Digest);
        if (existing is null) {
            return false;
        }
        if (!existing.ToCanonicalBytes().SequenceEqual(
                expected.ToCanonicalBytes())) {
            throw PolicyRegistrationFailure("DefinitionDigestCollision");
        }
        return true;
    }

    private static bool TryRequireRegistered(
        RecapGridControlPutResult result,
        out ControlHeadRef head
    ) {
        switch (result) {
            case RecapGridControlPutResult.Stored stored:
                head = stored.Head;
                return true;
            case RecapGridControlPutResult.AlreadyPresent present:
                head = present.Head;
                return true;
            case RecapGridControlPutResult.CommitIndeterminate value:
                head = value.Intended;
                return false;
            default:
                throw PolicyRegistrationFailure(result);
        }
    }

    private static GalateaTurnException PolicyRegistrationFailure(
        object result
    ) => new(
        $"RecapGrid默认策略注册失败：{result switch {
            string detail => detail,
            RecapGridControlPutResult.Unauthorized value
                => $"Unauthorized:{value.Rule}",
            RecapGridControlPutResult.StaleControlHead
                => "StaleControlHead",
            RecapGridControlPutResult.StaleTimelineHead
                => "StaleTimelineHead",
            RecapGridControlPutResult.Busy => "Busy",
            RecapGridControlPutResult.TimelineUnsupportedSchema value
                => $"TimelineSchema:{value.SchemaVersion}",
            RecapGridControlPutResult.Disposed => "Disposed",
            RecapGridControlPutResult.LimitExceeded value
                => $"Limit:{value.Limit}",
            RecapGridControlPutResult.Invalid value
                => $"Invalid:{value.Code}",
            RecapGridControlOpenResult.TimelineUnsupportedSchema value
                => $"TimelineSchema:{value.SchemaVersion}",
            RecapGridControlOpenResult.UnsupportedSchema value
                => $"ControlSchema:{value.SchemaVersion}",
            RecapGridControlOpenResult.Invalid value
                => $"Invalid:{value.Code}",
            RecapGridControlReaderOpenResult.TimelineUnsupportedSchema value
                => $"TimelineSchema:{value.SchemaVersion}",
            RecapGridControlReaderOpenResult.UnsupportedSchema value
                => $"ControlSchema:{value.SchemaVersion}",
            RecapGridControlReaderOpenResult.Invalid value
                => $"Invalid:{value.Code}",
            RecapGridControlSnapshotResult.UnsupportedSchema value
                => $"ControlSchema:{value.SchemaVersion}",
            RecapGridControlSnapshotResult.Invalid value
                => $"Invalid:{value.Code}",
            _ => result.GetType().Name
        }}",
        "recap-grid-policy-registration-failed"
    );

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


}

internal sealed class GalateaRecapGridTurn : IAsyncDisposable {
    internal GalateaRecapGridTurn(
        CompletionConnectionConfig connection,
        ICompletionClient client,
        CompletionDispatchIdentity identity,
        RecapGridOnlineContextHandle? online,
        EventAddress? resumeHead = null,
        bool rawHistoryAuthorized = false,
        RecapGridOnlineMaintenanceEvidence? maintenanceEvidence = null
    ) {
        Connection = connection;
        Client = client;
        Identity = identity;
        Online = online;
        ResumeHead = resumeHead;
        RawHistoryAuthorized = rawHistoryAuthorized;
        MaintenanceEvidence = maintenanceEvidence;
    }

    internal CompletionConnectionConfig Connection { get; }
    internal ICompletionClient Client { get; }
    internal CompletionDispatchIdentity Identity { get; }
    internal RecapGridOnlineContextHandle? Online { get; }
    internal EventAddress? ResumeHead { get; }
    internal bool RawHistoryAuthorized { get; }
    internal RecapGridOnlineMaintenanceEvidence? MaintenanceEvidence {
        get;
    }

    public async ValueTask DisposeAsync() {
        if (Online is not null) {
            await Online.DisposeAsync().ConfigureAwait(false);
        }
    }
}
