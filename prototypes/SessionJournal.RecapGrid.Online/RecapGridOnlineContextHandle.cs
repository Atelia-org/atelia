using Atelia.EventJournal;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid.Getter;
using Atelia.SessionJournal.RecapGrid.Manager;
using System.Runtime.ExceptionServices;

namespace Atelia.SessionJournal.RecapGrid.Online;

public sealed class RecapGridOnlineContextHandle :
    ISessionContextLifecycleCoordinator,
    IDisposable,
    IAsyncDisposable {
    private readonly SessionJournalEngine _owner;
    private readonly SessionJournalReadView _selectedRef;
    private readonly HistoryTimelineHandle _timeline;
    private readonly RecapGridContextHandle _getter;
    private readonly IRecapCellBatchExecutor _executor;
    private readonly RecapGridOnlineLimits _limits;
    private readonly IHistoryUnitLoadEstimator[] _estimators;
    private readonly object _managerGate = new();
    private readonly OnlineLifetime _lifetime;
    private RecapGridManagerHandle? _manager;

    internal OnlineCleanupTestHooks? CleanupHooksForTest { get; set; }

    internal RecapGridOnlineContextHandle(
        SessionJournalEngine owner,
        SessionJournalReadView selectedRef,
        HistoryTimelineHandle timeline,
        RecapGridContextHandle getter,
        IRecapCellBatchExecutor executor,
        RecapGridOnlineLimits limits,
        IHistoryUnitLoadEstimator[] estimators
    ) {
        _owner = owner;
        _selectedRef = selectedRef;
        _timeline = timeline;
        _getter = getter;
        _executor = executor;
        _limits = limits;
        _estimators = estimators;
        _lifetime = new OnlineLifetime(DisposeOwnedAsync);
    }

    public ICoherentContextCandidateSource CandidateSource => _getter;
    public ISessionContextLifecycleCoordinator Lifecycle => this;

    public ValueTask<SessionContextLifecycleResult> PrepareAsync(
        SessionJournalReadView readView,
        SessionContextLifecycleRequest request,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(readView);
        ArgumentNullException.ThrowIfNull(request);
        return PrepareNeutralAsync(readView, request, cancellationToken);
    }

    public ValueTask<RecapGridOnlinePassResult> PreparePassAsync(
        SessionJournalReadView readView,
        SessionContextLifecycleRequest request,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(readView);
        ArgumentNullException.ThrowIfNull(request);
        if (!_lifetime.TryEnter(out OnlineLifetime.OperationLease? lease)) {
            return ValueTask.FromResult<RecapGridOnlinePassResult>(
                new RecapGridOnlinePassResult.Disposed()
            );
        }
        return PrepareEnteredAsync(
            lease!, readView, request, cancellationToken
        );
    }

    private async ValueTask<SessionContextLifecycleResult>
        PrepareNeutralAsync(
        SessionJournalReadView readView,
        SessionContextLifecycleRequest request,
        CancellationToken cancellationToken
    ) {
        RecapGridOnlinePassResult result = await PreparePassAsync(
            readView, request, cancellationToken
        ).ConfigureAwait(false);
        return result switch {
            RecapGridOnlinePassResult.Ready
                => SessionContextLifecycleResult.Ready,
            RecapGridOnlinePassResult.RawHistoryAuthorized
                => SessionContextLifecycleResult.RawHistoryAuthorized,
            RecapGridOnlinePassResult.Backpressure value
                => new SessionContextLifecycleResult(
                    SessionContextLifecycleStatus.Backpressure,
                    $"{value.Component}:{value.Code}:{value.Detail}",
                    value.BoundedLineageEvidence),
            RecapGridOnlinePassResult.Unavailable value
                => new SessionContextLifecycleResult(
                    SessionContextLifecycleStatus.Unavailable,
                    $"{value.Component}:{value.Code}:{value.Detail}"),
            RecapGridOnlinePassResult.Disposed
                => new SessionContextLifecycleResult(
                    SessionContextLifecycleStatus.Unavailable,
                    "Online:Disposed:The online context handle is disposed."),
            _ => new SessionContextLifecycleResult(
                SessionContextLifecycleStatus.Unavailable,
                "Online:OutcomeInvalid:The online lifecycle returned an unknown outcome.")
        };
    }

    private async ValueTask<RecapGridOnlinePassResult>
        PrepareEnteredAsync(
        OnlineLifetime.OperationLease lease,
        SessionJournalReadView readView,
        SessionContextLifecycleRequest request,
        CancellationToken cancellationToken
    ) {
        using (lease)
        using (_lifetime.EnterOperationScope()) {
            if (!ReferenceEquals(readView, _selectedRef)) {
                return Unavailable(
                    RecapGridOnlineComponent.RawAuthority,
                    "RawAuthorityOwnerMismatch",
                    "The lifecycle callback supplied another SessionJournal read view."
                );
            }
            if (!IsSupportedPhase(request)) {
                return Unavailable(
                    RecapGridOnlineComponent.RawAuthority,
                    "LifecyclePhaseInvalid",
                    "Online lifecycle accepts only Idle with a pending observation or AwaitingAgentAction without one."
                );
            }
            cancellationToken.ThrowIfCancellationRequested();
            EventAddress? currentRawHead;
            try {
                currentRawHead = _selectedRef.ReadCurrentHead();
            }
            catch (ObjectDisposedException) {
                return new RecapGridOnlinePassResult.Disposed();
            }
            if (currentRawHead != request.Boundary) {
                return Backpressure(
                    RecapGridOnlineComponent.RawAuthority,
                    "RawHeadChanged",
                    $"Expected={request.Boundary};Observed={currentRawHead}"
                );
            }

            // Idle+pending runs before ObservationAccepted is appended and is
            // the only safe seal point for this turn. The immediately
            // following AwaitingAgentAction pass must leave that Observation
            // in the SessionJournal-owned raw tail; sealing it would create
            // an empty prepared raw range and lose the request boundary.
            if (request.Trigger
                    == SessionContextLifecycleTrigger.PreObservation) {
                TimelineSyncResult synchronized = SynchronizeTimeline(
                    cancellationToken
                );
                if (synchronized.Error is { } syncError) {
                    return syncError;
                }
            }

            RecapGridOnlinePassResult readiness = await EnsureReadyAsync(
                request,
                cancellationToken
            ).ConfigureAwait(false);
            if (readiness is RecapGridOnlinePassResult.Ready
                or RecapGridOnlinePassResult.RawHistoryAuthorized) {
                EventAddress? after = _selectedRef.ReadCurrentHead();
                if (after != request.Boundary) {
                    return Backpressure(
                        RecapGridOnlineComponent.RawAuthority,
                        "RawHeadChanged",
                        $"Expected={request.Boundary};Observed={after}"
                    );
                }
            }
            return readiness;
        }
    }

    private static bool IsSupportedPhase(
        SessionContextLifecycleRequest request
    ) => request.Trigger switch {
        SessionContextLifecycleTrigger.PreObservation
            => request.Phase == SessionExecutionPhase.Idle
                && request.PendingObservation is not null,
        SessionContextLifecycleTrigger.ObservationAccepted
            or SessionContextLifecycleTrigger.ToolResultObserved
            => request.Phase
                    == SessionExecutionPhase.AwaitingAgentAction
                && request.PendingObservation is null,
        _ => false,
    };

    private TimelineSyncResult SynchronizeTimeline(
        CancellationToken cancellationToken
    ) {
        HistoryTimelineSnapshotResult snapshot = _timeline.Reader.ReadSnapshot();
        if (snapshot is not HistoryTimelineSnapshotResult.Available available) {
            return new TimelineSyncResult(null, MapTimelineSnapshot(snapshot));
        }
        TimelineHeadRef expected = available.Head;
        HistoryTimelineReconcileResult reconcile = _timeline.Coordinator
            .ReconcileSelectedPath(expected, _selectedRef, cancellationToken);
        AuditContext? audit = null;
        try {
            if (reconcile is HistoryTimelineReconcileResult
                    .OfflineBootstrapRequired) {
                AuditOpenResult auditOpened = OpenAudit(cancellationToken);
                if (auditOpened.Error is { } auditError) {
                    return new TimelineSyncResult(null, auditError);
                }
                audit = auditOpened.Context!;
                using SessionSelectedLineageForwardCursor cursor =
                    audit.Snapshot.OpenForwardCursor(cancellationToken);
                reconcile = _timeline.Coordinator
                    .ReconcileSelectedPathOffline(
                        expected, cursor, cancellationToken);
            }
            if (reconcile is HistoryTimelineReconcileResult.Unchanged same) {
                expected = same.Head;
            }
            else if (reconcile is HistoryTimelineReconcileResult
                         .Reconciled moved) {
                expected = moved.Head;
            }
            else {
                return new TimelineSyncResult(
                    null, MapReconcile(reconcile));
            }

            int committed = 0;
            while (committed < _limits.MaximumTimelineRows) {
                cancellationToken.ThrowIfCancellationRequested();
                OnlineSelectedRawCaptureResult raw = _timeline.Coordinator
                    .CaptureOnline(expected, _selectedRef, cancellationToken);
                if (raw is OnlineSelectedRawCaptureResult.Empty) {
                    return new TimelineSyncResult(expected, null);
                }
                if (raw is not OnlineSelectedRawCaptureResult.Captured captured) {
                    return new TimelineSyncResult(null, MapCapture(raw));
                }
                HistoryTimelinePlanResult plan = _timeline.Coordinator
                    .PlanNextRow(
                        expected, captured.Capture, cancellationToken);
                if (plan is HistoryTimelinePlanResult.Selected selected) {
                    HistoryTimelineCommitResult commit = _timeline.Coordinator
                        .CommitRow(selected.Candidate);
                    if (commit is not HistoryTimelineCommitResult.Committed done) {
                        return new TimelineSyncResult(null, MapCommit(commit));
                    }
                    expected = done.Head;
                    committed++;
                    continue;
                }
                if (plan is HistoryTimelinePlanResult.NotEnough) {
                    return new TimelineSyncResult(expected, null);
                }
                if (plan is HistoryTimelinePlanResult
                        .OfflineBootstrapRequired) {
                    if (audit is null) {
                        AuditOpenResult auditOpened = OpenAudit(
                            cancellationToken);
                        if (auditOpened.Error is { } auditError) {
                            return new TimelineSyncResult(null, auditError);
                        }
                        audit = auditOpened.Context!;
                    }
                    return BuildOffline(
                        audit, expected, committed, cancellationToken);
                }
                return new TimelineSyncResult(null, MapPlan(plan));
            }
            return ProbeOnlineAtRowLimit(
                ref audit,
                expected,
                cancellationToken);
        }
        catch (SessionSelectedLineageAuditChangedException changed) {
            return new TimelineSyncResult(null, Backpressure(
                RecapGridOnlineComponent.RawAuthority,
                "RawHeadChanged",
                $"Expected={changed.ExpectedHead};Observed={changed.ObservedHead}"
            ));
        }
        finally {
            audit?.Dispose();
        }
    }

    private TimelineSyncResult BuildOffline(
        AuditContext audit,
        TimelineHeadRef expected,
        int committed,
        CancellationToken cancellationToken
    ) {
        using SessionSelectedLineageForwardCursor cursor =
            audit.Snapshot.OpenForwardCursor(cancellationToken);
        if (expected.HeadRowId is { } rowId) {
            HistoryTimelineReaderRowResult selected = _timeline.Reader
                .ReadSelectedRow(expected, rowId);
            if (selected is not HistoryTimelineReaderRowResult.Selected row) {
                return new TimelineSyncResult(
                    null, MapSelectedRow(selected));
            }
            cursor.SeekToBoundary(
                row.Row.Descriptor.EndInclusive,
                row.Row.Descriptor.EndSetups,
                cancellationToken);
        }
        HistoryTimelineOfflineBuilderOpenResult opened =
            _timeline.Coordinator.OpenOfflineBuilder(expected, cursor);
        if (opened is not HistoryTimelineOfflineBuilderOpenResult.Opened ready) {
            return new TimelineSyncResult(null, MapOfflineOpen(opened));
        }
        while (committed < _limits.MaximumTimelineRows) {
            cancellationToken.ThrowIfCancellationRequested();
            HistoryTimelineOfflineStepResult step = ready.Builder
                .BuildNextRow(expected, cancellationToken);
            if (step is HistoryTimelineOfflineStepResult.Committed done) {
                expected = done.Head;
                committed++;
                continue;
            }
            if (step is HistoryTimelineOfflineStepResult.NotEnough) {
                return new TimelineSyncResult(expected, null);
            }
            return new TimelineSyncResult(null, MapOfflineStep(step));
        }
        HistoryTimelineOfflineStepResult terminalProbe = ready.Builder
            .ProbeNextRow(expected, cancellationToken);
        return terminalProbe switch {
            HistoryTimelineOfflineStepResult.NotEnough
                => new TimelineSyncResult(expected, null),
            HistoryTimelineOfflineStepResult.Selected
                => TimelineRowLimitExceeded(),
            _ => new TimelineSyncResult(
                null, MapOfflineStep(terminalProbe))
        };
    }

    private TimelineSyncResult ProbeOnlineAtRowLimit(
        ref AuditContext? audit,
        TimelineHeadRef expected,
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        OnlineSelectedRawCaptureResult raw = _timeline.Coordinator
            .CaptureOnline(expected, _selectedRef, cancellationToken);
        if (raw is OnlineSelectedRawCaptureResult.Empty) {
            return new TimelineSyncResult(expected, null);
        }
        if (raw is not OnlineSelectedRawCaptureResult.Captured captured) {
            return new TimelineSyncResult(null, MapCapture(raw));
        }
        HistoryTimelinePlanResult plan = _timeline.Coordinator.PlanNextRow(
            expected, captured.Capture, cancellationToken);
        if (plan is HistoryTimelinePlanResult.NotEnough) {
            return new TimelineSyncResult(expected, null);
        }
        if (plan is HistoryTimelinePlanResult.Selected) {
            return TimelineRowLimitExceeded();
        }
        if (plan is not HistoryTimelinePlanResult
                .OfflineBootstrapRequired) {
            return new TimelineSyncResult(null, MapPlan(plan));
        }
        if (audit is null) {
            AuditOpenResult auditOpened = OpenAudit(cancellationToken);
            if (auditOpened.Error is { } auditError) {
                return new TimelineSyncResult(null, auditError);
            }
            audit = auditOpened.Context!;
        }
        return ProbeOfflineAtRowLimit(
            audit,
            expected,
            cancellationToken);
    }

    private TimelineSyncResult ProbeOfflineAtRowLimit(
        AuditContext audit,
        TimelineHeadRef expected,
        CancellationToken cancellationToken
    ) {
        using SessionSelectedLineageForwardCursor cursor =
            audit.Snapshot.OpenForwardCursor(cancellationToken);
        if (expected.HeadRowId is { } rowId) {
            HistoryTimelineReaderRowResult selected = _timeline.Reader
                .ReadSelectedRow(expected, rowId);
            if (selected is not HistoryTimelineReaderRowResult.Selected row) {
                return new TimelineSyncResult(
                    null, MapSelectedRow(selected));
            }
            cursor.SeekToBoundary(
                row.Row.Descriptor.EndInclusive,
                row.Row.Descriptor.EndSetups,
                cancellationToken);
        }
        HistoryTimelineOfflineBuilderOpenResult opened =
            _timeline.Coordinator.OpenOfflineBuilder(expected, cursor);
        if (opened is not HistoryTimelineOfflineBuilderOpenResult
                .Opened ready) {
            return new TimelineSyncResult(null, MapOfflineOpen(opened));
        }
        HistoryTimelineOfflineStepResult probe = ready.Builder
            .ProbeNextRow(expected, cancellationToken);
        return probe switch {
            HistoryTimelineOfflineStepResult.NotEnough
                => new TimelineSyncResult(expected, null),
            HistoryTimelineOfflineStepResult.Selected
                => TimelineRowLimitExceeded(),
            _ => new TimelineSyncResult(null, MapOfflineStep(probe))
        };
    }

    private TimelineSyncResult TimelineRowLimitExceeded()
        => new(null, Backpressure(
            RecapGridOnlineComponent.Timeline,
            "TimelineRowLimitExceeded",
            $"MaximumTimelineRows={_limits.MaximumTimelineRows}"));

    private AuditOpenResult OpenAudit(CancellationToken cancellationToken) {
        try {
            SessionSelectedLineageAuditSnapshotCaptureResult captured =
                _owner.CaptureSelectedLineageAuditSnapshot(
                    _limits.MaximumAuditEvents,
                    cancellationToken);
            return captured switch {
                SessionSelectedLineageAuditSnapshotCaptureResult.Available value
                    => new AuditOpenResult(
                        new AuditContext(value.Snapshot), null),
                SessionSelectedLineageAuditSnapshotCaptureResult.LimitExceeded value
                    => new AuditOpenResult(null, Backpressure(
                        RecapGridOnlineComponent.RawAuthority,
                        "OfflineAuditEventLimitExceeded",
                        $"Maximum={value.MaximumEvents};Observed={value.ObservedEvents}")),
                SessionSelectedLineageAuditSnapshotCaptureResult.Busy
                    => new AuditOpenResult(null, Backpressure(
                        RecapGridOnlineComponent.RawAuthority,
                        "OfflineAuditBusy",
                        "Another owner-bound audit capture is active.")),
                SessionSelectedLineageAuditSnapshotCaptureResult.RawHeadChanged value
                    => new AuditOpenResult(null, Backpressure(
                        RecapGridOnlineComponent.RawAuthority,
                        "OfflineAuditRawHeadChanged",
                        $"Expected={value.Expected};Observed={value.Observed}")),
                _ => new AuditOpenResult(null, Unavailable(
                    RecapGridOnlineComponent.RawAuthority,
                    "OfflineAuditOutcomeInvalid",
                    "SessionJournal returned an unknown audit result."))
            };
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested) {
            throw;
        }
        catch (Exception exception) when (IsCatchable(exception)) {
            return new AuditOpenResult(null, Unavailable(
                RecapGridOnlineComponent.RawAuthority,
                "OfflineAuditUnavailable",
                exception.GetType().Name
            ));
        }
    }

    private async ValueTask<RecapGridOnlinePassResult> EnsureReadyAsync(
        SessionContextLifecycleRequest request,
        CancellationToken cancellationToken
    ) {
        RecapGridContextResolveResult resolved = _getter.Resolve(
            request.Boundary,
            request.Selection.NthPrevious,
            cancellationToken);
        if (resolved is RecapGridContextResolveResult.RawHistoryAuthorized) {
            return new RecapGridOnlinePassResult.RawHistoryAuthorized();
        }
        if (resolved is RecapGridContextResolveResult.Selected
            or RecapGridContextResolveResult.OrdinalUnavailable) {
            return new RecapGridOnlinePassResult.Ready();
        }
        if (resolved is not RecapGridContextResolveResult.Unfulfilled) {
            return MapGetterResolve(resolved);
        }

        ManagerOpen managerOpen = OpenManager();
        if (managerOpen.Error is { } managerError) {
            return managerError;
        }
        RecapGridManager manager = managerOpen.Handle!.Manager;
        var buildRequest = new RecapGridBuildRequest(
            new RecapGridBuildSelection.LiveActive(),
            throughRowId: null,
            _limits.BuildBudget);
        RecapGridBuildProgressResult progress = manager
            .InspectBuildProgress(buildRequest, cancellationToken);
        switch (progress) {
            case RecapGridBuildProgressResult.Complete {
                FulfillmentPresent: true
            }:
                return MapGetterResolve(_getter.Resolve(
                    request.Boundary,
                    request.Selection.NthPrevious,
                    cancellationToken));
            case RecapGridBuildProgressResult.NoRows:
            case RecapGridBuildProgressResult.NoActiveRecipe:
                return MapGetterResolve(_getter.Resolve(
                    request.Boundary,
                    request.Selection.NthPrevious,
                    cancellationToken));
            case RecapGridBuildProgressResult.Frontier:
            case RecapGridBuildProgressResult.Complete:
                break;
            default:
                return MapProgress(progress);
        }

        RecapGridBuildResult built = await manager.BuildAsync(
            buildRequest, _executor, cancellationToken
        ).ConfigureAwait(false);
        switch (built) {
            case RecapGridBuildResult.Fulfilled:
            case RecapGridBuildResult.NoRows:
            case RecapGridBuildResult.NoActiveRecipe:
                return MapGetterResolve(_getter.Resolve(
                    request.Boundary,
                    request.Selection.NthPrevious,
                    cancellationToken));
            default:
                return MapBuild(built);
        }
    }

    private ManagerOpen OpenManager() {
        lock (_managerGate) {
            if (_manager is not null) {
                return new ManagerOpen(_manager, null);
            }
            RecapGridManagerOpenResult opened = RecapGridManagerFactory.Open(
                _selectedRef, _estimators);
            if (opened is RecapGridManagerOpenResult.Opened available) {
                _manager = available.Handle;
                return new ManagerOpen(_manager, null);
            }
            return new ManagerOpen(null, MapManagerOpen(opened));
        }
    }

    public void Dispose() => _lifetime.DisposeAndDrain();
    public ValueTask DisposeAsync() => _lifetime.DisposeAndDrainAsync();

    private ValueTask DisposeOwnedAsync() {
        List<Exception>? failures = null;
        DisposeOne(() => {
            _manager?.Dispose();
            CleanupHooksForTest?.AfterManagerDisposed?.Invoke();
        });
        DisposeOne(() => {
            _getter.Dispose();
            CleanupHooksForTest?.AfterGetterDisposed?.Invoke();
        });
        DisposeOne(() => {
            _timeline.Dispose();
            CleanupHooksForTest?.AfterTimelineDisposed?.Invoke();
        });
        if (failures is { Count: 1 }) {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }
        if (failures is { Count: > 1 }) {
            throw new AggregateException(failures);
        }
        return ValueTask.CompletedTask;

        void DisposeOne(Action action) {
            try {
                action();
            }
            catch (Exception exception) when (IsCatchable(exception)) {
                (failures ??= []).Add(exception);
            }
        }
    }

    private static bool IsCatchable(Exception exception)
        => exception is not OutOfMemoryException
            and not StackOverflowException
            and not AccessViolationException;

    private sealed record TimelineSyncResult(
        TimelineHeadRef? Head,
        RecapGridOnlinePassResult? Error
    );
    private sealed record ManagerOpen(
        RecapGridManagerHandle? Handle,
        RecapGridOnlinePassResult? Error
    );
    private sealed record AuditOpenResult(
        AuditContext? Context,
        RecapGridOnlinePassResult? Error
    );

    private sealed class AuditContext(
        SessionSelectedLineageAuditSnapshot snapshot
    ) : IDisposable {
        internal SessionSelectedLineageAuditSnapshot Snapshot { get; } = snapshot;
        public void Dispose() => Snapshot.Dispose();
    }

    internal sealed record OnlineCleanupTestHooks(
        Action? AfterManagerDisposed = null,
        Action? AfterGetterDisposed = null,
        Action? AfterTimelineDisposed = null
    );

    private static RecapGridOnlinePassResult Backpressure(
        RecapGridOnlineComponent component,
        string code,
        string detail,
        SessionCurrentLineageBeyondPrefix? evidence = null
    ) => new RecapGridOnlinePassResult.Backpressure(
        component, code, detail, evidence);

    private static RecapGridOnlinePassResult Unavailable(
        RecapGridOnlineComponent component,
        string code,
        string detail
    ) => new RecapGridOnlinePassResult.Unavailable(
        component, code, detail);

    private static RecapGridOnlinePassResult MapTimelineSnapshot(
        HistoryTimelineSnapshotResult result
    ) => result switch {
        HistoryTimelineSnapshotResult.Busy
            => Backpressure(RecapGridOnlineComponent.Timeline,
                "TimelineBusy", "HistoryTimeline is busy."),
        HistoryTimelineSnapshotResult.UnsupportedSchema value
            => Unavailable(RecapGridOnlineComponent.Timeline,
                "TimelineUnsupportedSchema", value.SchemaVersion.ToString()),
        HistoryTimelineSnapshotResult.Invalid value
            => Unavailable(RecapGridOnlineComponent.Timeline,
                value.Code, value.Detail),
        _ => Unavailable(RecapGridOnlineComponent.Timeline,
            "TimelineSnapshotOutcomeInvalid", "Unknown Timeline snapshot outcome.")
    };

    private static RecapGridOnlinePassResult MapReconcile(
        HistoryTimelineReconcileResult result
    ) => result switch {
        HistoryTimelineReconcileResult.OfflineBootstrapRequired value
            => Backpressure(RecapGridOnlineComponent.RawAuthority,
                "OfflineBootstrapRequired", "A complete raw audit is required.",
                value.Evidence),
        HistoryTimelineReconcileResult.RawHeadChanged value
            => Backpressure(RecapGridOnlineComponent.RawAuthority,
                "RawHeadChanged", $"Expected={value.Expected};Observed={value.Observed}"),
        HistoryTimelineReconcileResult.StaleTimelineHead
            => Backpressure(RecapGridOnlineComponent.Timeline,
                "StaleTimelineHead", "Timeline head changed."),
        HistoryTimelineReconcileResult.PartitionPolicyUnavailable value
            => Unavailable(RecapGridOnlineComponent.Timeline,
                "PartitionPolicyUnavailable", value.PolicyDigest),
        HistoryTimelineReconcileResult.BackendBusy
            => Backpressure(RecapGridOnlineComponent.Timeline,
                "TimelineBusy", "HistoryTimeline is busy."),
        HistoryTimelineReconcileResult.Invalid value
            => Unavailable(RecapGridOnlineComponent.Timeline,
                value.Code, value.Detail),
        _ => Unavailable(RecapGridOnlineComponent.Timeline,
            "TimelineReconcileOutcomeInvalid", "Unknown Timeline reconcile outcome.")
    };

    private static RecapGridOnlinePassResult MapCapture(
        OnlineSelectedRawCaptureResult result
    ) => result switch {
        OnlineSelectedRawCaptureResult.StaleTimelineHead
            => Backpressure(RecapGridOnlineComponent.Timeline,
                "StaleTimelineHead", "Timeline head changed."),
        OnlineSelectedRawCaptureResult.PartitionPolicyUnavailable value
            => Unavailable(RecapGridOnlineComponent.Timeline,
                "PartitionPolicyUnavailable", value.PolicyDigest),
        OnlineSelectedRawCaptureResult.BackendBusy
            => Backpressure(RecapGridOnlineComponent.Timeline,
                "TimelineBusy", "HistoryTimeline is busy."),
        OnlineSelectedRawCaptureResult.Invalid value
            => Unavailable(RecapGridOnlineComponent.Timeline,
                value.Code, value.Detail),
        _ => Unavailable(RecapGridOnlineComponent.Timeline,
            "RawCaptureOutcomeInvalid", "Unknown raw capture outcome.")
    };

    private static RecapGridOnlinePassResult MapPlan(
        HistoryTimelinePlanResult result
    ) => result switch {
        HistoryTimelinePlanResult.LimitExceeded
            => Backpressure(RecapGridOnlineComponent.Timeline,
                "PartitionLimitExceeded", "Timeline partition limit was exceeded."),
        HistoryTimelinePlanResult.OfflineBootstrapRequired value
            => Backpressure(RecapGridOnlineComponent.RawAuthority,
                "OfflineBootstrapRequired", "A complete raw audit is required.",
                value.Evidence),
        HistoryTimelinePlanResult.OffLineage
            => Unavailable(RecapGridOnlineComponent.RawAuthority,
                "TimelineAnchorOffLineage", "Timeline anchor is off selected lineage."),
        HistoryTimelinePlanResult.RawHeadChanged
            => Backpressure(RecapGridOnlineComponent.RawAuthority,
                "RawHeadChanged", "Raw head changed."),
        HistoryTimelinePlanResult.StaleTimelineHead
            => Backpressure(RecapGridOnlineComponent.Timeline,
                "StaleTimelineHead", "Timeline head changed."),
        HistoryTimelinePlanResult.PartitionPolicyUnavailable value
            => Unavailable(RecapGridOnlineComponent.Timeline,
                "PartitionPolicyUnavailable", value.PolicyDigest),
        HistoryTimelinePlanResult.HistoryLoadEstimatorUnavailable value
            => Unavailable(RecapGridOnlineComponent.Timeline,
                "HistoryLoadEstimatorUnavailable", value.EstimatorId),
        HistoryTimelinePlanResult.PartitionAlgorithmUnavailable value
            => Unavailable(RecapGridOnlineComponent.Timeline,
                "PartitionAlgorithmUnavailable", value.AlgorithmId),
        HistoryTimelinePlanResult.BackendBusy
            => Backpressure(RecapGridOnlineComponent.Timeline,
                "TimelineBusy", "HistoryTimeline is busy."),
        HistoryTimelinePlanResult.Invalid value
            => Unavailable(RecapGridOnlineComponent.Timeline,
                value.Code, value.Detail),
        _ => Unavailable(RecapGridOnlineComponent.Timeline,
            "TimelinePlanOutcomeInvalid", "Unknown Timeline plan outcome.")
    };

    private static RecapGridOnlinePassResult MapCommit(
        HistoryTimelineCommitResult result
    ) => result switch {
        HistoryTimelineCommitResult.StaleTimelineHead
            => Backpressure(RecapGridOnlineComponent.Timeline,
                "StaleTimelineHead", "Timeline head changed."),
        HistoryTimelineCommitResult.RawHeadChanged
            => Backpressure(RecapGridOnlineComponent.RawAuthority,
                "RawHeadChanged", "Raw head changed."),
        HistoryTimelineCommitResult.PartitionPolicyUnavailable value
            => Unavailable(RecapGridOnlineComponent.Timeline,
                "PartitionPolicyUnavailable", value.PolicyDigest),
        HistoryTimelineCommitResult.LimitExceeded value
            => Backpressure(RecapGridOnlineComponent.Timeline,
                "TimelineStoreLimitExceeded", value.Limit),
        HistoryTimelineCommitResult.BackendBusy
            => Backpressure(RecapGridOnlineComponent.Timeline,
                "TimelineBusy", "HistoryTimeline is busy."),
        HistoryTimelineCommitResult.Invalid value
            => Unavailable(RecapGridOnlineComponent.Timeline,
                value.Code, value.Detail),
        _ => Unavailable(RecapGridOnlineComponent.Timeline,
            "TimelineCommitOutcomeInvalid", "Unknown Timeline commit outcome.")
    };

    private static RecapGridOnlinePassResult MapSelectedRow(
        HistoryTimelineReaderRowResult result
    ) => result switch {
        HistoryTimelineReaderRowResult.NotOnSelectedPath
            => Unavailable(RecapGridOnlineComponent.Timeline,
                "TimelineRowNotOnSelectedPath", "Timeline row is not selected."),
        HistoryTimelineReaderRowResult.StaleTimelineHead
            => Backpressure(RecapGridOnlineComponent.Timeline,
                "StaleTimelineHead", "Timeline head changed."),
        HistoryTimelineReaderRowResult.Busy
            => Backpressure(RecapGridOnlineComponent.Timeline,
                "TimelineBusy", "HistoryTimeline is busy."),
        HistoryTimelineReaderRowResult.Invalid value
            => Unavailable(RecapGridOnlineComponent.Timeline,
                value.Code, value.Detail),
        _ => Unavailable(RecapGridOnlineComponent.Timeline,
            "TimelineRowOutcomeInvalid", "Unknown selected row outcome.")
    };

    private static RecapGridOnlinePassResult MapOfflineOpen(
        HistoryTimelineOfflineBuilderOpenResult result
    ) => result switch {
        HistoryTimelineOfflineBuilderOpenResult.RawHeadChanged
            => Backpressure(RecapGridOnlineComponent.RawAuthority,
                "RawHeadChanged", "Raw head changed."),
        HistoryTimelineOfflineBuilderOpenResult.StaleTimelineHead
            => Backpressure(RecapGridOnlineComponent.Timeline,
                "StaleTimelineHead", "Timeline head changed."),
        HistoryTimelineOfflineBuilderOpenResult.BackendBusy
            => Backpressure(RecapGridOnlineComponent.Timeline,
                "TimelineBusy", "HistoryTimeline is busy."),
        HistoryTimelineOfflineBuilderOpenResult.Invalid value
            => Unavailable(RecapGridOnlineComponent.Timeline,
                value.Code, value.Detail),
        _ => Unavailable(RecapGridOnlineComponent.Timeline,
            "OfflineBuilderOpenOutcomeInvalid", "Unknown offline builder open outcome.")
    };

    private static RecapGridOnlinePassResult MapOfflineStep(
        HistoryTimelineOfflineStepResult result
    ) => result switch {
        HistoryTimelineOfflineStepResult.LimitExceeded
            => Backpressure(RecapGridOnlineComponent.Timeline,
                "PartitionLimitExceeded", "Timeline partition limit was exceeded."),
        HistoryTimelineOfflineStepResult.StoreLimitExceeded value
            => Backpressure(RecapGridOnlineComponent.Timeline,
                "TimelineStoreLimitExceeded", value.Limit),
        HistoryTimelineOfflineStepResult.RawHeadChanged
            => Backpressure(RecapGridOnlineComponent.RawAuthority,
                "RawHeadChanged", "Raw head changed."),
        HistoryTimelineOfflineStepResult.StaleTimelineHead
            => Backpressure(RecapGridOnlineComponent.Timeline,
                "StaleTimelineHead", "Timeline head changed."),
        HistoryTimelineOfflineStepResult.PartitionPolicyUnavailable value
            => Unavailable(RecapGridOnlineComponent.Timeline,
                "PartitionPolicyUnavailable", value.PolicyDigest),
        HistoryTimelineOfflineStepResult.HistoryLoadEstimatorUnavailable value
            => Unavailable(RecapGridOnlineComponent.Timeline,
                "HistoryLoadEstimatorUnavailable", value.EstimatorId),
        HistoryTimelineOfflineStepResult.PartitionAlgorithmUnavailable value
            => Unavailable(RecapGridOnlineComponent.Timeline,
                "PartitionAlgorithmUnavailable", value.AlgorithmId),
        HistoryTimelineOfflineStepResult.BackendBusy
            => Backpressure(RecapGridOnlineComponent.Timeline,
                "TimelineBusy", "HistoryTimeline is busy."),
        HistoryTimelineOfflineStepResult.Invalid value
            => Unavailable(RecapGridOnlineComponent.Timeline,
                value.Code, value.Detail),
        _ => Unavailable(RecapGridOnlineComponent.Timeline,
            "OfflineStepOutcomeInvalid", "Unknown offline step outcome.")
    };

    private static RecapGridOnlinePassResult MapGetterResolve(
        RecapGridContextResolveResult result
    ) => result switch {
        RecapGridContextResolveResult.RawHistoryAuthorized
            => new RecapGridOnlinePassResult.RawHistoryAuthorized(),
        RecapGridContextResolveResult.Selected
            or RecapGridContextResolveResult.OrdinalUnavailable
            => new RecapGridOnlinePassResult.Ready(),
        RecapGridContextResolveResult.Unfulfilled
            => Backpressure(RecapGridOnlineComponent.Store,
                "ActiveRecipeUnfulfilled", "The active recipe is not fulfilled."),
        RecapGridContextResolveResult.LimitExceeded value
            => Backpressure(RecapGridOnlineComponent.Getter,
                "GetterLimitExceeded", value.Limit),
        RecapGridContextResolveResult.Stale value
            => Backpressure(RecapGridOnlineFactory.Map(value.Component),
                "GetterAuthorityStale", value.Detail),
        RecapGridContextResolveResult.NotOnSelectedPath
            => Unavailable(RecapGridOnlineComponent.Timeline,
                "GetterRowNotOnSelectedPath", "Getter row is not selected."),
        RecapGridContextResolveResult.Busy value
            => Backpressure(RecapGridOnlineFactory.Map(value.Component),
                "GetterDependencyBusy", "Getter dependency is busy."),
        RecapGridContextResolveResult.Disposed value
            => Unavailable(RecapGridOnlineFactory.Map(value.Component),
                "GetterDependencyDisposed", "Getter dependency is disposed."),
        RecapGridContextResolveResult.UnsupportedSchema value
            => Unavailable(RecapGridOnlineFactory.Map(value.Component),
                "GetterDependencyUnsupportedSchema", value.SchemaVersion.ToString()),
        RecapGridContextResolveResult.Invalid value
            => Unavailable(RecapGridOnlineFactory.Map(value.Component),
                value.Code, value.Detail),
        _ => Unavailable(RecapGridOnlineComponent.Getter,
            "GetterResolveOutcomeInvalid", "Unknown Getter resolve outcome.")
    };

    private static RecapGridOnlinePassResult MapManagerOpen(
        RecapGridManagerOpenResult result
    ) => result switch {
        RecapGridManagerOpenResult.Absent value
            => Unavailable(Map(value.Dependency),
                "ManagerDependencyAbsent", "A Manager dependency is absent."),
        RecapGridManagerOpenResult.Busy value
            => Backpressure(Map(value.Dependency),
                "ManagerDependencyBusy", "A Manager dependency is busy."),
        RecapGridManagerOpenResult.UnsupportedSchema value
            => Unavailable(Map(value.Dependency),
                "ManagerDependencyUnsupportedSchema", value.SchemaVersion.ToString()),
        RecapGridManagerOpenResult.PlatformUnsupported value
            => Unavailable(Map(value.Dependency),
                "ManagerDependencyPlatformUnsupported", "The platform is unsupported."),
        RecapGridManagerOpenResult.Invalid value
            => Unavailable(Map(value.Dependency), value.Code, value.Detail),
        _ => Unavailable(RecapGridOnlineComponent.Manager,
            "ManagerOpenOutcomeInvalid", "Unknown Manager open outcome.")
    };

    private static RecapGridOnlinePassResult MapProgress(
        RecapGridBuildProgressResult result
    ) => result switch {
        RecapGridBuildProgressResult.Blocked value
            => Backpressure(RecapGridOnlineComponent.Manager,
                value.Code, value.Detail),
        RecapGridBuildProgressResult.BudgetExceeded value
            => Backpressure(RecapGridOnlineComponent.Manager,
                "BuildBudgetExceeded", value.Kind.ToString()),
        RecapGridBuildProgressResult.Cancelled
            => Backpressure(RecapGridOnlineComponent.Manager,
                "BuildCancelled", "Build progress was cancelled."),
        RecapGridBuildProgressResult.Unavailable value
            => value.Code.EndsWith("Busy", StringComparison.Ordinal)
                ? Backpressure(Map(value.Dependency), value.Code, value.Detail)
                : Unavailable(Map(value.Dependency), value.Code, value.Detail),
        RecapGridBuildProgressResult.StaleTimelineHead
            => Backpressure(RecapGridOnlineComponent.Timeline,
                "StaleTimelineHead", "Timeline head changed."),
        RecapGridBuildProgressResult.StaleControlAuthority
            => Backpressure(RecapGridOnlineComponent.Control,
                "StaleControlHead", "Control head changed."),
        RecapGridBuildProgressResult.Disposed
            => new RecapGridOnlinePassResult.Disposed(),
        RecapGridBuildProgressResult.Invalid value
            => Unavailable(RecapGridOnlineComponent.Manager,
                value.Code, value.Detail),
        RecapGridBuildProgressResult.RecipeAbsent value
            => Unavailable(RecapGridOnlineComponent.Control,
                "ActiveRecipeAbsent", value.RecipeDigest.Value),
        RecapGridBuildProgressResult.ThroughRowNotSelected
            => Unavailable(RecapGridOnlineComponent.Timeline,
                "ThroughRowNotSelected", "The requested row is not selected."),
        _ => Unavailable(RecapGridOnlineComponent.Manager,
            "BuildProgressOutcomeInvalid", "Unknown build progress outcome.")
    };

    private static RecapGridOnlinePassResult MapBuild(
        RecapGridBuildResult result
    ) => result switch {
        RecapGridBuildResult.BudgetExceeded value
            => Backpressure(RecapGridOnlineComponent.Manager,
                "BuildBudgetExceeded", value.Kind.ToString()),
        RecapGridBuildResult.Cancelled
            => Backpressure(RecapGridOnlineComponent.Manager,
                "BuildCancelled", "Build was cancelled."),
        RecapGridBuildResult.Incomplete value
            => Backpressure(RecapGridOnlineComponent.Manager,
                "BuildIncomplete", value.RowId.Value),
        RecapGridBuildResult.ExecutorRejected value
            => Unavailable(RecapGridOnlineComponent.Manager,
                value.Code, value.Detail),
        RecapGridBuildResult.ExecutorFailed value
            => Backpressure(RecapGridOnlineComponent.Manager,
                value.Code, value.Detail),
        RecapGridBuildResult.Unavailable value
            => value.Code.EndsWith("Busy", StringComparison.Ordinal)
                ? Backpressure(Map(value.Dependency), value.Code, value.Detail)
                : Unavailable(Map(value.Dependency), value.Code, value.Detail),
        RecapGridBuildResult.StaleTimelineHead
            => Backpressure(RecapGridOnlineComponent.Timeline,
                "StaleTimelineHead", "Timeline head changed."),
        RecapGridBuildResult.StaleControlAuthority
            => Backpressure(RecapGridOnlineComponent.Control,
                "StaleControlHead", "Control head changed."),
        RecapGridBuildResult.SettlementRequired value
            => Backpressure(RecapGridOnlineComponent.Store,
                "BuildSettlementRequired",
                $"{value.Kind}:{value.IntendedIdentity}:{value.ObservedIdentity}"),
        RecapGridBuildResult.Disposed
            => new RecapGridOnlinePassResult.Disposed(),
        RecapGridBuildResult.Invalid value
            => Unavailable(RecapGridOnlineComponent.Manager,
                value.Code, value.Detail),
        RecapGridBuildResult.RecipeAbsent value
            => Unavailable(RecapGridOnlineComponent.Control,
                "ActiveRecipeAbsent", value.RecipeDigest.Value),
        RecapGridBuildResult.ThroughRowNotSelected
            => Unavailable(RecapGridOnlineComponent.Timeline,
                "ThroughRowNotSelected", "The requested row is not selected."),
        RecapGridBuildResult.FulfilledThrough
            => Unavailable(RecapGridOnlineComponent.Manager,
                "PartialFulfillmentUnexpected", "Live current-head build returned a partial receipt."),
        _ => Unavailable(RecapGridOnlineComponent.Manager,
            "BuildOutcomeInvalid", "Unknown build outcome.")
    };

    private static RecapGridOnlineComponent Map(
        RecapGridBuildDependency dependency
    ) => dependency switch {
        RecapGridBuildDependency.RawHistory
            => RecapGridOnlineComponent.RawAuthority,
        RecapGridBuildDependency.Timeline
            => RecapGridOnlineComponent.Timeline,
        RecapGridBuildDependency.Control
            => RecapGridOnlineComponent.Control,
        RecapGridBuildDependency.Store
            => RecapGridOnlineComponent.Store,
        _ => RecapGridOnlineComponent.Manager
    };
}
