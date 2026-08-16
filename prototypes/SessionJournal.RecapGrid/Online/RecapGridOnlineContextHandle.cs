using Atelia.EventJournal;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid.Cadence;
using Atelia.SessionJournal.RecapGrid.Getter;
using Atelia.SessionJournal.RecapGrid.Manager;
using System.Diagnostics;
using System.Runtime.ExceptionServices;

namespace Atelia.SessionJournal.RecapGrid.Online;

public sealed class RecapGridOnlineContextHandle :
    ISessionContextLifecycleCoordinator,
    IDisposable,
    IAsyncDisposable {
    private readonly SessionJournalEngine _owner;
    private readonly SessionJournalReadView _selectedRef;
    private readonly RecapGridCadenceHandle _cadence;
    private readonly HistoryTimelineHandle _timeline;
    private readonly RecapGridContextHandle _getter;
    private readonly IRecapCellBatchExecutor _executor;
    private readonly RecapGridOnlineLimits _limits;
    private readonly IHistoryUnitLoadEstimator[] _estimators;
    private readonly object _managerGate = new();
    private readonly OnlineLifetime _lifetime;
    private RecapGridManagerHandle? _manager;

    internal OnlineCleanupTestHooks? CleanupHooksForTest { get; set; }
    internal OnlineOperationTestHooks? OperationHooksForTest { get; set; }
    internal TimeProvider TimeProviderForTest { get; set; }
        = TimeProvider.System;

    internal RecapGridOnlineContextHandle(
        SessionJournalEngine owner,
        SessionJournalReadView selectedRef,
        RecapGridCadenceHandle cadence,
        HistoryTimelineHandle timeline,
        RecapGridContextHandle getter,
        IRecapCellBatchExecutor executor,
        RecapGridOnlineLimits limits,
        IHistoryUnitLoadEstimator[] estimators
    ) {
        _owner = owner;
        _selectedRef = selectedRef;
        _cadence = cadence;
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
        var budget = new OnlineOperationBudget(
            _limits.MaximumNewCalls,
            _limits.SoftMaximumElapsed,
            TimeProviderForTest);
        return PreparePassBudgetedAsync(
            readView, request, budget, cancellationToken);
    }

    private ValueTask<RecapGridOnlinePassResult> PreparePassBudgetedAsync(
        SessionJournalReadView readView,
        SessionContextLifecycleRequest request,
        OnlineOperationBudget budget,
        CancellationToken cancellationToken
    ) {
        if (!_lifetime.TryEnter(out OnlineLifetime.OperationLease? lease)) {
            return ValueTask.FromResult<RecapGridOnlinePassResult>(
                new RecapGridOnlinePassResult.Disposed()
            );
        }
        return PrepareEnteredAsync(
            lease!, readView, request, budget, cancellationToken
        );
    }

    public async ValueTask<RecapGridOnlinePassResult>
        CatchUpMaintenanceAsync(
        string? pendingObservation,
        CancellationToken cancellationToken = default
    ) {
        var budget = new OnlineOperationBudget(
            _limits.MaximumNewCalls,
            _limits.SoftMaximumElapsed,
            TimeProviderForTest);
        RecapGridOnlineMaintenanceEvidence cumulative = EmptyEvidence(
            RecapGridOnlineContinuationKind.Ready);
        for (int pass = 0;
             pass < RecapGridOnlineCatchUpLimits.MaximumPasses;
             pass++) {
            var capture = new MaintenanceCaptureLifecycle(
                this,
                budget.WithMaximumNewCalls(
                    Math.Max(0, _limits.MaximumNewCalls - cumulative.NewCalls)));
            EventAddress expectedHead = _selectedRef.ReadCurrentHead()
                ?? throw new InvalidDataException(
                    "Online lifecycle maintenance requires a raw head."
                );
            try {
                _ = await _owner
                    .PrepareContextLifecycleMaintenanceAsync(
                        expectedHead,
                        capture,
                        pendingObservation,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (SessionJournalNotReadyException) when (
                capture.Result is not null) {
                // SessionJournal surfaces any non-ready lifecycle result as a
                // typed exception. Preserve the captured Online result here;
                // the loop below retries only MaintenanceContinuation.
            }
            if (capture.Result is not { } result) {
                result = Unavailable(
                    RecapGridOnlineComponent.RawAuthority,
                    "MaintenanceCaptureMissing",
                    "SessionJournal returned without an Online lifecycle result."
                );
            }
            RecapGridOnlineMaintenanceEvidence passEvidence =
                ExtractEvidence(result) ?? (EmptyEvidence(
                    result is RecapGridOnlinePassResult.RawHistoryAuthorized
                        ? RecapGridOnlineContinuationKind.RawHistoryAuthorized
                        : RecapGridOnlineContinuationKind.Ready) with {
                            NextRecipeRow = result is
                                    RecapGridOnlinePassResult.Ready
                                    or RecapGridOnlinePassResult
                                        .RawHistoryAuthorized
                                ? null
                                : cumulative.NextRecipeRow
                            ,
                            NextAuthority = result is
                                    RecapGridOnlinePassResult.Ready
                                    or RecapGridOnlinePassResult
                                        .RawHistoryAuthorized
                                ? null
                                : cumulative.NextAuthority
                        });
            cumulative = AccumulateEvidence(cumulative, passEvidence);
            if (cumulative.Passes > RecapGridOnlineCatchUpLimits.MaximumPasses
                || cumulative.NewCalls > _limits.MaximumNewCalls
                || cumulative.TimelineRowsCommitted > cumulative.Passes
                || cumulative.RecipeRowSteps > cumulative.Passes
                || cumulative.RowViewsCommitted > cumulative.Passes) {
                throw new InvalidOperationException(
                    "Online catch-up exceeded its frozen operation budget.");
            }
            result = WithCatchUpEvidence(result, cumulative);
            if (result is not RecapGridOnlinePassResult
                    .MaintenanceContinuation) {
                if (IsOperationBudgetFailure(result, out string budgetCode)) {
                    return CatchUpBudgetExhausted(
                        budgetCode,
                        cumulative);
                }
                return result;
            }
            if (pass + 1 == RecapGridOnlineCatchUpLimits.MaximumPasses) {
                return CatchUpBudgetExhausted(
                    "CatchUpPassBudgetExhausted",
                    cumulative);
            }
        }
        throw new InvalidOperationException(
            "Online catch-up loop escaped its code-owned pass bound.");
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
            RecapGridOnlinePassResult.MaintenanceContinuation value
                => new SessionContextLifecycleResult(
                    SessionContextLifecycleStatus.Backpressure,
                    $"{value.Component}:{value.Code}:{value.Detail}"),
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
        OnlineOperationBudget budget,
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
            if (OperationHooksForTest?.PreparePassOverride?.Invoke()
                    is { } overridden) {
                return overridden;
            }

            // Idle+pending runs before ObservationAccepted is appended and is
            // the only safe seal point for this turn. The immediately
            // following AwaitingAgentAction pass must leave that Observation
            // in the SessionJournal-owned raw tail; sealing it would create
            // an empty prepared raw range and lose the request boundary.
            if (request.Trigger
                    == SessionContextLifecycleTrigger.PreObservation) {
                return await MaintainPreObservationAsync(
                    request,
                    budget,
                    cancellationToken
                ).ConfigureAwait(false);
            }

            RecapGridOnlinePassResult readiness =
                await MaintainGridOnlyAsync(
                    request,
                    budget,
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

    private async ValueTask<RecapGridOnlinePassResult>
        MaintainGridOnlyAsync(
        SessionContextLifecycleRequest request,
        OnlineOperationBudget budget,
        CancellationToken cancellationToken
    ) {
        GridInspection entry = InspectReadiness(
            request, budget, cancellationToken);
        if (entry.Error is { } entryError) {
            return entryError;
        }
        if (!entry.HasDebt) {
            return entry.Readiness!;
        }
        GridBuildStepResult build = await BuildOneAsync(
            entry,
            cancellationToken
        ).ConfigureAwait(false);
        GridInspection terminal = InspectAfterMutation(
            request, budget, cancellationToken);
        RecapGridOnlineMaintenanceEvidence evidence = Evidence(
            entryDebt: true,
            timelineRowsCommitted: 0,
            entry.Coordinate,
            build.Metrics,
            terminal.Coordinate,
            terminal.HasDebt
                ? RecapGridOnlineContinuationKind.GridDebtRemaining
                : RecapGridOnlineContinuationKind.GridDebtCleared,
            entry.Authority,
            terminal.Authority
        );
        if (build.Error is { } buildError) {
            return WithEvidence(buildError, evidence with {
                ContinuationKind =
                    RecapGridOnlineContinuationKind.PostMutationFailure
            });
        }
        if (terminal.Error is { } terminalError) {
            return WithEvidence(terminalError, evidence with {
                ContinuationKind =
                    RecapGridOnlineContinuationKind.PostMutationFailure
            });
        }
        return Maintenance(
            RecapGridOnlineComponent.Manager,
            terminal.HasDebt ? "GridDebtRemaining" : "GridDebtCleared",
            "One recipe-row maintenance unit consumed this lifecycle pass.",
            evidence
        );
    }

    private async ValueTask<RecapGridOnlinePassResult>
        MaintainPreObservationAsync(
        SessionContextLifecycleRequest request,
        OnlineOperationBudget budget,
        CancellationToken cancellationToken
    ) {
        RecapGridCadenceTimelineSealOpenResult sealOpened =
            _cadence.BeginTimelineSeal(_timeline);
        if (sealOpened is not RecapGridCadenceTimelineSealOpenResult
                .Opened available) {
            return MapSealOpen(sealOpened);
        }
        using RecapGridCadenceTimelineSealOperation seal =
            available.Operation;
        TimelineHeadRef expected = seal.HeadAtOpen;
        AuditContext? audit = null;
        bool entryDebtObserved = false;
        int timelineRowsCommitted = 0;
        RecapGridRecipeRowCoordinate? attemptedRecipeRow = null;
        RecapGridBuildMetrics accumulatedBuildMetrics =
            RecapGridBuildMetrics.Empty;
        try {
            HistoryTimelineReconcileResult reconcile = _timeline.Coordinator
                .ReconcileSelectedPath(
                    expected,
                    _selectedRef,
                    cancellationToken);
            if (reconcile is HistoryTimelineReconcileResult
                    .OfflineBootstrapRequired) {
                AuditOpenResult auditOpened = OpenAudit(cancellationToken);
                if (auditOpened.Error is { } auditError) {
                    return auditError;
                }
                audit = auditOpened.Context!;
                reconcile = seal.ReconcileSelectedPathOffline(
                    expected,
                    audit.Snapshot,
                    cancellationToken);
            }
            if (reconcile is HistoryTimelineReconcileResult.Unchanged same) {
                expected = same.Head;
            }
            else if (reconcile is HistoryTimelineReconcileResult
                         .Reconciled moved) {
                expected = moved.Head;
            }
            else {
                return MapReconcile(reconcile);
            }

            GridInspection entry = InspectReadiness(
                request,
                budget,
                cancellationToken);
            if (entry.Error is { } entryError) {
                return entryError;
            }
            entryDebtObserved = entry.HasDebt;
            if (entry.HasDebt) {
                attemptedRecipeRow = entry.Coordinate;
                GridBuildStepResult build = await BuildOneAsync(
                    entry,
                    cancellationToken
                ).ConfigureAwait(false);
                accumulatedBuildMetrics = build.Metrics;
                GridInspection afterDebt = InspectAfterMutation(
                    request,
                    budget,
                    cancellationToken);
                RecapGridOnlineMaintenanceEvidence evidence = Evidence(
                    entryDebt: true,
                    timelineRowsCommitted: 0,
                    entry.Coordinate,
                    build.Metrics,
                    afterDebt.Coordinate,
                    afterDebt.HasDebt
                        ? RecapGridOnlineContinuationKind.GridDebtRemaining
                        : RecapGridOnlineContinuationKind.GridDebtCleared,
                    entry.Authority,
                    afterDebt.Authority);
                if (build.Error is { } buildError) {
                    return WithEvidence(buildError, evidence with {
                        ContinuationKind = RecapGridOnlineContinuationKind
                            .PostMutationFailure
                    });
                }
                if (afterDebt.Error is { } afterDebtError) {
                    return WithEvidence(afterDebtError, evidence with {
                        ContinuationKind = RecapGridOnlineContinuationKind
                            .PostMutationFailure
                    });
                }
                return Maintenance(
                    RecapGridOnlineComponent.Manager,
                    afterDebt.HasDebt
                        ? "GridDebtRemaining"
                        : "GridDebtCleared",
                    "A pre-existing recipe-row debt consumed this lifecycle pass.",
                    evidence
                );
            }

            TimelineStepResult timeline = SealOneTimelineRow(
                seal,
                ref audit,
                expected,
                cancellationToken);
            timelineRowsCommitted = timeline.Committed ? 1 : 0;
            var timelineOnly = Evidence(
                entryDebt: false,
                timeline.Committed ? 1 : 0,
                attemptedRecipeRow: null,
                RecapGridBuildMetrics.Empty,
                nextRecipeRow: null,
                RecapGridOnlineContinuationKind.PostMutationFailure);
            if (timeline.Error is { } timelineError) {
                return timeline.Committed
                    ? WithEvidence(timelineError, timelineOnly)
                    : timelineError;
            }
            if (!timeline.Committed) {
                return entry.Readiness!;
            }

            GridInspection afterSeal = InspectAfterMutation(
                request,
                budget,
                cancellationToken);
            if (afterSeal.Error is { } afterSealError) {
                return WithEvidence(afterSealError, timelineOnly);
            }
            RecapGridBuildMetrics buildMetrics = RecapGridBuildMetrics.Empty;
            RecapGridRecipeRowCoordinate? attempted = null;
            if (afterSeal.HasDebt) {
                attempted = afterSeal.Coordinate;
                attemptedRecipeRow = attempted;
                GridBuildStepResult build = await BuildOneAsync(
                    afterSeal,
                    cancellationToken
                ).ConfigureAwait(false);
                buildMetrics = build.Metrics;
                accumulatedBuildMetrics = buildMetrics;
                if (build.Error is not null) {
                    return WithEvidence(build.Error, Evidence(
                        entryDebt: false,
                        timelineRowsCommitted: 1,
                        attempted,
                        buildMetrics,
                        nextRecipeRow: attempted,
                        RecapGridOnlineContinuationKind
                            .PostMutationFailure,
                        afterSeal.Authority,
                        afterSeal.Authority));
                }
            }

            GridInspection terminal = InspectAfterMutation(
                request,
                budget,
                cancellationToken);
            if (terminal.Error is { } terminalError) {
                return WithEvidence(terminalError, Evidence(
                    entryDebt: false,
                    timelineRowsCommitted: 1,
                    attempted,
                    buildMetrics,
                    terminal.Coordinate,
                    RecapGridOnlineContinuationKind.PostMutationFailure,
                    afterSeal.Authority,
                    terminal.Authority));
            }
            if (timeline.MoreRows || terminal.HasDebt) {
                RecapGridOnlineContinuationKind kind = timeline.MoreRows
                    ? RecapGridOnlineContinuationKind.TimelineDebtRemaining
                    : RecapGridOnlineContinuationKind.GridDebtRemaining;
                return Maintenance(
                    timeline.MoreRows
                        ? RecapGridOnlineComponent.Timeline
                        : RecapGridOnlineComponent.Manager,
                    timeline.MoreRows
                        ? "TimelineDebtRemaining"
                        : "GridDebtRemaining",
                    "The bounded online pass left durable maintenance debt.",
                    Evidence(
                        entryDebt: false,
                        timelineRowsCommitted: 1,
                        attempted,
                        buildMetrics,
                        terminal.Coordinate,
                        kind,
                        afterSeal.Authority,
                        terminal.Authority)
                );
            }
            return terminal.Readiness!;
        }
        catch (SessionSelectedLineageAuditChangedException changed) {
            RecapGridOnlinePassResult error = Backpressure(
                RecapGridOnlineComponent.RawAuthority,
                "RawHeadChanged",
                $"Expected={changed.ExpectedHead};Observed={changed.ObservedHead}"
            );
            return timelineRowsCommitted != 0
                    || accumulatedBuildMetrics != RecapGridBuildMetrics.Empty
                ? WithEvidence(error, Evidence(
                    entryDebtObserved,
                    timelineRowsCommitted,
                    attemptedRecipeRow,
                    accumulatedBuildMetrics,
                    nextRecipeRow: null,
                    RecapGridOnlineContinuationKind.PostMutationFailure))
                : error;
        }
        finally {
            audit?.Dispose();
        }
    }

    private TimelineStepResult SealOneTimelineRow(
        RecapGridCadenceTimelineSealOperation seal,
        ref AuditContext? audit,
        TimelineHeadRef expected,
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        OnlineSelectedRawCaptureResult raw = _timeline.Coordinator
            .CaptureOnline(expected, _selectedRef, cancellationToken);
        if (raw is OnlineSelectedRawCaptureResult.Empty) {
            return new TimelineStepResult(false, false, null);
        }
        if (raw is not OnlineSelectedRawCaptureResult.Captured captured) {
            return new TimelineStepResult(false, false, MapCapture(raw));
        }
        HistoryTimelinePlanResult plan = seal.PlanNextRow(
            expected,
            captured.Capture,
            cancellationToken);
        if (plan is HistoryTimelinePlanResult.NotEnough
            or HistoryTimelinePlanResult.RecentReserveNotReached) {
            return new TimelineStepResult(false, false, null);
        }
        if (plan is HistoryTimelinePlanResult.Selected selected) {
            HistoryTimelineCommitResult commit = seal.CommitRow(
                selected.Candidate);
            if (commit is not HistoryTimelineCommitResult.Committed done) {
                return new TimelineStepResult(
                    false, false, MapCommit(commit));
            }
            OperationHooksForTest?.AfterTimelineCommit?.Invoke();
            try {
                return ProbeNextTimelineRowOnline(
                    seal,
                    ref audit,
                    done.Head,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested) {
                return new TimelineStepResult(
                    true,
                    false,
                    Backpressure(
                        RecapGridOnlineComponent.RawAuthority,
                        "PostMutationCancelled",
                        "Cancellation was observed after a Timeline row committed."));
            }
        }
        if (plan is not HistoryTimelinePlanResult
                .OfflineBootstrapRequired) {
            return new TimelineStepResult(false, false, MapPlan(plan));
        }
        if (audit is null) {
            AuditOpenResult auditOpened = OpenAudit(cancellationToken);
            if (auditOpened.Error is { } auditError) {
                return new TimelineStepResult(false, false, auditError);
            }
            audit = auditOpened.Context!;
        }
        return SealOneTimelineRowOffline(
            seal,
            audit,
            expected,
            cancellationToken);
    }

    private TimelineStepResult SealOneTimelineRowOffline(
        RecapGridCadenceTimelineSealOperation seal,
        AuditContext audit,
        TimelineHeadRef expected,
        CancellationToken cancellationToken
    ) {
        RecapGridCadenceOfflineBuilderOpenResult opened =
            seal.OpenOfflineBuilder(
                expected,
                audit.Snapshot,
                cancellationToken);
        if (opened is not RecapGridCadenceOfflineBuilderOpenResult.Opened ready) {
            return new TimelineStepResult(
                false, false, MapOfflineOpen(opened));
        }
        using RecapGridCadenceOfflineBuilder builder = ready.Builder;
        cancellationToken.ThrowIfCancellationRequested();
        HistoryTimelineOfflineStepResult step = builder
            .BuildNextRow(expected, cancellationToken);
        if (step is HistoryTimelineOfflineStepResult.NotEnough
            or HistoryTimelineOfflineStepResult
                .RecentReserveNotReached) {
            return new TimelineStepResult(false, false, null);
        }
        if (step is not HistoryTimelineOfflineStepResult.Committed done) {
            return new TimelineStepResult(
                false, false, MapOfflineStep(step));
        }
        OperationHooksForTest?.AfterTimelineCommit?.Invoke();
        HistoryTimelineOfflineStepResult probe;
        try {
            probe = builder.ProbeNextRow(done.Head, cancellationToken);
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested) {
            return new TimelineStepResult(
                true,
                false,
                Backpressure(
                    RecapGridOnlineComponent.RawAuthority,
                    "PostMutationCancelled",
                    "Cancellation was observed after a Timeline row committed."));
        }
        return probe switch {
            HistoryTimelineOfflineStepResult.NotEnough
                or HistoryTimelineOfflineStepResult
                    .RecentReserveNotReached
                => new TimelineStepResult(true, false, null),
            HistoryTimelineOfflineStepResult.Selected
                => new TimelineStepResult(true, true, null),
            _ => new TimelineStepResult(
                true, false, MapOfflineStep(probe))
        };
    }

    private TimelineStepResult ProbeNextTimelineRowOnline(
        RecapGridCadenceTimelineSealOperation seal,
        ref AuditContext? audit,
        TimelineHeadRef expected,
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        OnlineSelectedRawCaptureResult raw = _timeline.Coordinator
            .CaptureOnline(expected, _selectedRef, cancellationToken);
        if (raw is OnlineSelectedRawCaptureResult.Empty) {
            return new TimelineStepResult(true, false, null);
        }
        if (raw is not OnlineSelectedRawCaptureResult.Captured captured) {
            return new TimelineStepResult(true, false, MapCapture(raw));
        }
        HistoryTimelinePlanResult plan = seal.PlanNextRow(
            expected, captured.Capture, cancellationToken);
        if (plan is HistoryTimelinePlanResult.NotEnough
            or HistoryTimelinePlanResult.RecentReserveNotReached) {
            return new TimelineStepResult(true, false, null);
        }
        if (plan is HistoryTimelinePlanResult.Selected) {
            return new TimelineStepResult(true, true, null);
        }
        if (plan is not HistoryTimelinePlanResult
                .OfflineBootstrapRequired) {
            return new TimelineStepResult(true, false, MapPlan(plan));
        }
        if (audit is null) {
            AuditOpenResult auditOpened = OpenAudit(cancellationToken);
            if (auditOpened.Error is { } auditError) {
                return new TimelineStepResult(true, false, auditError);
            }
            audit = auditOpened.Context!;
        }
        return ProbeNextTimelineRowOffline(
            seal,
            audit,
            expected,
            cancellationToken);
    }

    private TimelineStepResult ProbeNextTimelineRowOffline(
        RecapGridCadenceTimelineSealOperation seal,
        AuditContext audit,
        TimelineHeadRef expected,
        CancellationToken cancellationToken
    ) {
        RecapGridCadenceOfflineBuilderOpenResult opened =
            seal.OpenOfflineBuilder(
                expected,
                audit.Snapshot,
                cancellationToken);
        if (opened is not RecapGridCadenceOfflineBuilderOpenResult
                .Opened ready) {
            return new TimelineStepResult(
                true, false, MapOfflineOpen(opened));
        }
        using RecapGridCadenceOfflineBuilder builder = ready.Builder;
        HistoryTimelineOfflineStepResult probe = builder
            .ProbeNextRow(expected, cancellationToken);
        return probe switch {
            HistoryTimelineOfflineStepResult.NotEnough
                or HistoryTimelineOfflineStepResult
                    .RecentReserveNotReached
                => new TimelineStepResult(true, false, null),
            HistoryTimelineOfflineStepResult.Selected
                => new TimelineStepResult(true, true, null),
            _ => new TimelineStepResult(
                true, false, MapOfflineStep(probe))
        };
    }

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

    private GridInspection InspectReadiness(
        SessionContextLifecycleRequest request,
        OnlineOperationBudget budget,
        CancellationToken cancellationToken
    ) {
        RecapGridContextResolveResult resolved = _getter.Resolve(
            request.Boundary,
            request.Selection.NthPrevious,
            cancellationToken);
        if (resolved is RecapGridContextResolveResult.RawHistoryAuthorized
            or RecapGridContextResolveResult.ReserveBootstrapRawOnly) {
            return new GridInspection(
                HasDebt: false,
                new RecapGridOnlinePassResult.RawHistoryAuthorized(),
                Manager: null,
                Request: null,
                    Coordinate: null,
                    Authority: null,
                    Error: null);
        }
        if (resolved is RecapGridContextResolveResult.Selected
            or RecapGridContextResolveResult.OrdinalUnavailable) {
            return new GridInspection(
                HasDebt: false,
                new RecapGridOnlinePassResult.Ready(),
                Manager: null,
                Request: null,
                Coordinate: null,
                Authority: null,
                Error: null);
        }
        if (resolved is not RecapGridContextResolveResult.Unfulfilled) {
            return GridInspection.FromError(MapGetterResolve(resolved));
        }

        ManagerOpen managerOpen = OpenManager();
        if (managerOpen.Error is { } managerError) {
            return GridInspection.FromError(managerError);
        }
        RecapGridManager manager = managerOpen.Handle!.Manager;
        var buildRequest = new RecapGridBuildRequest(
            new RecapGridBuildSelection.LiveActive(),
            throughRowId: null,
            OnlineBuildBudget(budget));
        RecapGridBuildProgressResult progress = manager
            .InspectBuildProgress(buildRequest, cancellationToken);
        switch (progress) {
            case RecapGridBuildProgressResult.Complete {
                FulfillmentPresent: true
            }:
                RecapGridOnlinePassResult ready = MapGetterResolve(
                    _getter.Resolve(
                        request.Boundary,
                        request.Selection.NthPrevious,
                        cancellationToken));
                return ready is RecapGridOnlinePassResult.Ready
                        or RecapGridOnlinePassResult.RawHistoryAuthorized
                    ? new GridInspection(
                        HasDebt: false,
                        ready,
                        Manager: null,
                        Request: null,
                        Coordinate: null,
                        Authority: null,
                        Error: null)
                    : GridInspection.FromError(ready);
            case RecapGridBuildProgressResult.NoRows:
            case RecapGridBuildProgressResult.NoActiveRecipe:
                RecapGridOnlinePassResult raw = MapGetterResolve(
                    _getter.Resolve(
                        request.Boundary,
                        request.Selection.NthPrevious,
                        cancellationToken));
                return raw is RecapGridOnlinePassResult.Ready
                        or RecapGridOnlinePassResult.RawHistoryAuthorized
                    ? new GridInspection(
                        HasDebt: false,
                        raw,
                        Manager: null,
                        Request: null,
                        Coordinate: null,
                        Authority: null,
                        Error: null)
                    : GridInspection.FromError(raw);
            case RecapGridBuildProgressResult.Frontier frontier:
                if (budget.HasElapsed()) {
                    return GridInspection.FromError(Backpressure(
                        RecapGridOnlineComponent.Manager,
                        "BuildBudgetExceeded",
                        RecapGridBuildBudgetKind.Elapsed.ToString()));
                }
                return new GridInspection(
                    HasDebt: true,
                    Readiness: null,
                    manager,
                    buildRequest,
                    Coordinate(frontier.NextWork),
                    frontier.Authority,
                    Error: null);
            case RecapGridBuildProgressResult.Complete {
                FulfillmentPresent: false
            } incomplete:
                if (budget.HasElapsed()) {
                    return GridInspection.FromError(Backpressure(
                        RecapGridOnlineComponent.Manager,
                        "BuildBudgetExceeded",
                        RecapGridBuildBudgetKind.Elapsed.ToString()));
                }
                return new GridInspection(
                    HasDebt: true,
                    Readiness: null,
                    manager,
                    buildRequest,
                    new RecapGridRecipeRowCoordinate(
                        incomplete.Authority.ThroughRowId,
                        incomplete.Authority.RecipeDigest),
                    incomplete.Authority,
                    Error: null);
            default:
                return GridInspection.FromError(MapProgress(progress));
        }
    }

    private GridInspection InspectAfterMutation(
        SessionContextLifecycleRequest request,
        OnlineOperationBudget budget,
        CancellationToken cancellationToken
    ) {
        try {
            return InspectReadiness(request, budget, cancellationToken);
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested) {
            return GridInspection.FromError(Backpressure(
                RecapGridOnlineComponent.RawAuthority,
                "PostMutationCancelled",
                "Cancellation was observed after durable maintenance mutation."));
        }
    }

    private async ValueTask<GridBuildStepResult> BuildOneAsync(
        GridInspection inspection,
        CancellationToken cancellationToken
    ) {
        if (!inspection.HasDebt
            || inspection.Manager is null
            || inspection.Request is null) {
            return new GridBuildStepResult(
                RecapGridBuildMetrics.Empty,
                Unavailable(
                    RecapGridOnlineComponent.Manager,
                    "GridInspectionInvalid",
                    "A recipe-row build requires exact inspected debt."
                )
            );
        }
        RecapGridBuildResult built = await inspection.Manager.BuildAsync(
            inspection.Request, _executor, cancellationToken
        ).ConfigureAwait(false);
        RecapGridBuildMetrics metrics = built.Metrics;
        OperationHooksForTest?.AfterBuildResult?.Invoke();
        if (metrics.RecipeRowSteps is < 0 or > 1
            || metrics.RowViewsCommitted is < 0 or > 1
            || metrics.CellsCommitted is < 0
                or > RecapGridLimits.MaximumColumnCount
            || metrics.NewCalls is < 0
                or > RecapGridLimits.MaximumColumnCount) {
            return new GridBuildStepResult(metrics, Unavailable(
                RecapGridOnlineComponent.Manager,
                "RecipeRowStepInvariant",
                "Online Manager metrics exceeded one recipe-row unit."
            ));
        }
        RecapGridOnlinePassResult? error = built switch {
            RecapGridBuildResult.Fulfilled
                or RecapGridBuildResult.NoRows
                or RecapGridBuildResult.NoActiveRecipe
                => null,
            RecapGridBuildResult.BudgetExceeded {
                Kind: RecapGridBuildBudgetKind.RecipeRowSteps
            } value when value.Metrics.RecipeRowSteps == 1
                => null,
            RecapGridBuildResult.Incomplete
                => null,
            _ => MapBuild(built)
        };
        return new GridBuildStepResult(metrics, error);
    }

    private static RecapGridBuildBudget OnlineBuildBudget(
        OnlineOperationBudget budget
    ) => new(
        maximumRecipeRowSteps: 1,
        budget.MaximumNewCalls,
        budget.RemainingElapsedForManager());

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
        DisposeOne(_cadence.Dispose);
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

    private sealed record TimelineStepResult(
        bool Committed,
        bool MoreRows,
        RecapGridOnlinePassResult? Error
    );
    private sealed record GridInspection(
        bool HasDebt,
        RecapGridOnlinePassResult? Readiness,
        RecapGridManager? Manager,
        RecapGridBuildRequest? Request,
        RecapGridRecipeRowCoordinate? Coordinate,
        RecapGridBuildProgressAuthority? Authority,
        RecapGridOnlinePassResult? Error
    ) {
        internal static GridInspection FromError(
            RecapGridOnlinePassResult error
        ) => new(
            HasDebt: false,
            Readiness: null,
            Manager: null,
            Request: null,
            Coordinate: null,
            Authority: null,
            error);
    }
    private sealed record ManagerOpen(
        RecapGridManagerHandle? Handle,
        RecapGridOnlinePassResult? Error
    );
    private sealed record GridBuildStepResult(
        RecapGridBuildMetrics Metrics,
        RecapGridOnlinePassResult? Error
    );
    private sealed record AuditOpenResult(
        AuditContext? Context,
        RecapGridOnlinePassResult? Error
    );

    private sealed class MaintenanceCaptureLifecycle(
        RecapGridOnlineContextHandle owner,
        OnlineOperationBudget budget
    ) : ISessionContextLifecycleCoordinator {
        internal RecapGridOnlinePassResult? Result { get; private set; }

        public async ValueTask<SessionContextLifecycleResult> PrepareAsync(
            SessionJournalReadView readView,
            SessionContextLifecycleRequest request,
            CancellationToken cancellationToken
        ) {
            Result = await owner.PreparePassBudgetedAsync(
                readView,
                request,
                budget,
                cancellationToken).ConfigureAwait(false);
            return Result switch {
                RecapGridOnlinePassResult.Ready
                    => SessionContextLifecycleResult.Ready,
                RecapGridOnlinePassResult.RawHistoryAuthorized
                    => SessionContextLifecycleResult.RawHistoryAuthorized,
                RecapGridOnlinePassResult.MaintenanceContinuation value
                    => new SessionContextLifecycleResult(
                        SessionContextLifecycleStatus.Backpressure,
                        $"{value.Component}:{value.Code}:{value.Detail}"),
                RecapGridOnlinePassResult.Backpressure value
                    => new SessionContextLifecycleResult(
                        SessionContextLifecycleStatus.Backpressure,
                        $"{value.Component}:{value.Code}:{value.Detail}",
                        value.BoundedLineageEvidence),
                RecapGridOnlinePassResult.Unavailable value
                    => new SessionContextLifecycleResult(
                        SessionContextLifecycleStatus.Unavailable,
                        $"{value.Component}:{value.Code}:{value.Detail}"),
                _ => new SessionContextLifecycleResult(
                    SessionContextLifecycleStatus.Unavailable,
                    "Online:Disposed:The online context handle is disposed.")
            };
        }
    }

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

    internal sealed record OnlineOperationTestHooks(
        Action? AfterTimelineCommit = null,
        Action? AfterBuildResult = null,
        Func<RecapGridOnlinePassResult?>? PreparePassOverride = null
    );

    private static RecapGridOnlinePassResult Backpressure(
        RecapGridOnlineComponent component,
        string code,
        string detail,
        SessionCurrentLineageBeyondPrefix? evidence = null
    ) => new RecapGridOnlinePassResult.Backpressure(
        component, code, detail, evidence);

    private static RecapGridOnlinePassResult Maintenance(
        RecapGridOnlineComponent component,
        string code,
        string detail,
        RecapGridOnlineMaintenanceEvidence evidence
    ) => new RecapGridOnlinePassResult.MaintenanceContinuation(
        component,
        code,
        detail,
        evidence);

    private static RecapGridRecipeRowCoordinate Coordinate(
        RecapGridRecipeRowWork work
    ) => new(work.RowId, work.RecipeDigest);

    private static RecapGridOnlineMaintenanceEvidence Evidence(
        bool entryDebt,
        int timelineRowsCommitted,
        RecapGridRecipeRowCoordinate? attemptedRecipeRow,
        RecapGridBuildMetrics metrics,
        RecapGridRecipeRowCoordinate? nextRecipeRow,
        RecapGridOnlineContinuationKind continuationKind,
        RecapGridBuildProgressAuthority? lastAttemptedAuthority = null,
        RecapGridBuildProgressAuthority? nextAuthority = null
    ) {
        if (timelineRowsCommitted is < 0 or > 1
            || metrics.RecipeRowSteps is < 0 or > 1
            || metrics.RowViewsCommitted is < 0 or > 1) {
            throw new InvalidOperationException(
                "Online maintenance exceeded its one-row operation bound.");
        }
        return new RecapGridOnlineMaintenanceEvidence(
            passes: 1,
            entryDebt,
            timelineRowsCommitted,
            lastAttemptedRecipeRow: attemptedRecipeRow,
            lastAttemptedAuthority,
            recipeRowSteps: metrics.RecipeRowSteps,
            rowViewsCommitted: metrics.RowViewsCommitted,
            cellsCommitted: metrics.CellsCommitted,
            newCalls: metrics.NewCalls,
            nextRecipeRow,
            nextAuthority,
            continuationKind);
    }

    private static RecapGridOnlineMaintenanceEvidence EmptyEvidence(
        RecapGridOnlineContinuationKind kind
    ) => new(
        passes: 0,
        entryDebt: false,
        timelineRowsCommitted: 0,
        lastAttemptedRecipeRow: null,
        lastAttemptedAuthority: null,
        recipeRowSteps: 0,
        rowViewsCommitted: 0,
        cellsCommitted: 0,
        newCalls: 0,
        nextRecipeRow: null,
        nextAuthority: null,
        continuationKind: kind);

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
        RecapGridOnlinePassResult.Disposed value => value.Evidence,
        _ => null
    };

    private static RecapGridOnlineMaintenanceEvidence AccumulateEvidence(
        RecapGridOnlineMaintenanceEvidence cumulative,
        RecapGridOnlineMaintenanceEvidence pass
    ) => new(
        passes: checked(cumulative.Passes + Math.Max(1, pass.Passes)),
        entryDebt: cumulative.EntryDebt || pass.EntryDebt,
        timelineRowsCommitted: checked(
            cumulative.TimelineRowsCommitted
                + pass.TimelineRowsCommitted),
        lastAttemptedRecipeRow: pass.LastAttemptedRecipeRow
            ?? cumulative.LastAttemptedRecipeRow,
        lastAttemptedAuthority: pass.LastAttemptedAuthority
            ?? cumulative.LastAttemptedAuthority,
        recipeRowSteps: checked(
            cumulative.RecipeRowSteps + pass.RecipeRowSteps),
        rowViewsCommitted: checked(
            cumulative.RowViewsCommitted + pass.RowViewsCommitted),
        cellsCommitted: checked(
            cumulative.CellsCommitted + pass.CellsCommitted),
        newCalls: checked(cumulative.NewCalls + pass.NewCalls),
        nextRecipeRow: pass.NextRecipeRow,
        nextAuthority: pass.NextAuthority,
        continuationKind: pass.ContinuationKind);

    private static RecapGridOnlinePassResult WithCatchUpEvidence(
        RecapGridOnlinePassResult result,
        RecapGridOnlineMaintenanceEvidence evidence
    ) => result switch {
        RecapGridOnlinePassResult.Ready value => value with {
            Evidence = evidence with {
                ContinuationKind = RecapGridOnlineContinuationKind.Ready
            }
        },
        RecapGridOnlinePassResult.RawHistoryAuthorized value => value with {
            Evidence = evidence with {
                ContinuationKind = RecapGridOnlineContinuationKind
                    .RawHistoryAuthorized
            }
        },
        RecapGridOnlinePassResult.MaintenanceContinuation value => value with {
            Evidence = evidence
        },
        RecapGridOnlinePassResult.Backpressure value => value with {
            MaintenanceEvidence = evidence
        },
        RecapGridOnlinePassResult.Unavailable value => value with {
            MaintenanceEvidence = evidence
        },
        RecapGridOnlinePassResult.Disposed value => value with {
            Evidence = evidence
        },
        _ => result
    };

    private static bool IsOperationBudgetFailure(
        RecapGridOnlinePassResult result,
        out string code
    ) {
        if (result is RecapGridOnlinePassResult.Backpressure {
                Code: "BuildBudgetExceeded",
                Detail: "NewCalls"
            }) {
            code = "CatchUpNewCallBudgetExhausted";
            return true;
        }
        if (result is RecapGridOnlinePassResult.Backpressure {
                Code: "BuildBudgetExceeded",
                Detail: "Elapsed"
            }) {
            code = "CatchUpElapsedBudgetExhausted";
            return true;
        }
        code = string.Empty;
        return false;
    }

    private static RecapGridOnlinePassResult CatchUpBudgetExhausted(
        string code,
        RecapGridOnlineMaintenanceEvidence evidence
    ) => Maintenance(
        RecapGridOnlineComponent.Manager,
        code,
        "The host-owned lifecycle catch-up operation budget was exhausted before further dispatch.",
        evidence with {
            ContinuationKind = RecapGridOnlineContinuationKind
                .CatchUpBudgetExhausted
        });

    private sealed class OnlineOperationBudget {
        private readonly TimeProvider _timeProvider;
        private readonly long _startedAt;
        private readonly TimeSpan _maximumElapsed;

        internal OnlineOperationBudget(
            int maximumNewCalls,
            TimeSpan maximumElapsed,
            TimeProvider timeProvider
        ) : this(
            maximumNewCalls,
            maximumElapsed,
            timeProvider,
            timeProvider.GetTimestamp()) {
        }

        private OnlineOperationBudget(
            int maximumNewCalls,
            TimeSpan maximumElapsed,
            TimeProvider timeProvider,
            long startedAt
        ) {
            MaximumNewCalls = maximumNewCalls;
            _maximumElapsed = maximumElapsed;
            _timeProvider = timeProvider;
            _startedAt = startedAt;
        }

        internal int MaximumNewCalls { get; }

        internal bool HasElapsed() =>
            _timeProvider.GetElapsedTime(_startedAt) >= _maximumElapsed;

        internal OnlineOperationBudget WithMaximumNewCalls(int value)
            => new(value, _maximumElapsed, _timeProvider, _startedAt);

        internal TimeSpan RemainingElapsedForManager() {
            TimeSpan remaining = _maximumElapsed
                - _timeProvider.GetElapsedTime(_startedAt);
            return remaining > TimeSpan.Zero
                ? remaining
                : TimeSpan.FromTicks(1);
        }
    }

    private static RecapGridOnlinePassResult WithEvidence(
        RecapGridOnlinePassResult result,
        RecapGridOnlineMaintenanceEvidence evidence
    ) => result switch {
        RecapGridOnlinePassResult.Backpressure value => value with {
            MaintenanceEvidence = evidence
        },
        RecapGridOnlinePassResult.Unavailable value => value with {
            MaintenanceEvidence = evidence
        },
        RecapGridOnlinePassResult.Disposed value => value with {
            Evidence = evidence
        },
        _ => result
    };

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

    private static RecapGridOnlinePassResult MapSealOpen(
        RecapGridCadenceTimelineSealOpenResult result
    ) => result switch {
        RecapGridCadenceTimelineSealOpenResult.Busy value
            => Backpressure(
                MapSealComponent(value.Component),
                "CadenceTimelineSealBusy",
                $"{value.Component} is busy."),
        RecapGridCadenceTimelineSealOpenResult.UnsupportedSchema value
            => Unavailable(
                MapSealComponent(value.Component),
                "CadenceTimelineSealUnsupportedSchema",
                value.SchemaVersion.ToString()),
        RecapGridCadenceTimelineSealOpenResult.Disposed value
            => Unavailable(
                MapSealComponent(value.Component),
                "CadenceTimelineSealDisposed",
                $"{value.Component} is disposed."),
        RecapGridCadenceTimelineSealOpenResult.Invalid value
            => Unavailable(
                MapSealComponent(value.Component),
                value.Code,
                value.Detail),
        _ => Unavailable(
            RecapGridOnlineComponent.Cadence,
            "CadenceTimelineSealOpenOutcomeInvalid",
            "Unknown Cadence Timeline seal-open outcome.")
    };

    private static RecapGridOnlineComponent MapSealComponent(
        string component
    ) => string.Equals(component, "Timeline", StringComparison.Ordinal)
        ? RecapGridOnlineComponent.Timeline
        : RecapGridOnlineComponent.Cadence;

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
        OnlineSelectedRawCaptureResult.LimitExceeded value
            => Backpressure(RecapGridOnlineComponent.RawAuthority,
                "RecentReserveOperationLimitExceeded", value.Limit),
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
        RecapGridCadenceOfflineBuilderOpenResult result
    ) => result switch {
        RecapGridCadenceOfflineBuilderOpenResult.RawHeadChanged
            => Backpressure(RecapGridOnlineComponent.RawAuthority,
                "RawHeadChanged", "Raw head changed."),
        RecapGridCadenceOfflineBuilderOpenResult.StaleTimelineHead
            => Backpressure(RecapGridOnlineComponent.Timeline,
                "StaleTimelineHead", "Timeline head changed."),
        RecapGridCadenceOfflineBuilderOpenResult.Busy
            => Backpressure(RecapGridOnlineComponent.Timeline,
                "TimelineBusy", "HistoryTimeline is busy."),
        RecapGridCadenceOfflineBuilderOpenResult.Invalid value
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
        HistoryTimelineOfflineStepResult.RecentReserveProofUnavailable value
            => Backpressure(RecapGridOnlineComponent.RawAuthority,
                value.Code, value.Detail),
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
            or RecapGridContextResolveResult.ReserveBootstrapRawOnly
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
