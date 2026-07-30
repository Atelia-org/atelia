using Atelia.EventJournal;
using Atelia.SessionJournal.DerivedRecap.Store;

namespace Atelia.SessionJournal.DerivedRecap.Planner;

/// <summary>
/// Performs one bounded online Recap maintenance pass and exposes the same
/// bound Store through SessionJournal's neutral candidate contract.
/// </summary>
public sealed class DerivedRecapOnlineLifecycleCoordinator
    : ISessionContextLifecycleCoordinator, ICoherentContextCandidateSource {
    private readonly SessionJournalEngine _engine;
    private readonly ICoherentContextCandidateSource _candidates;
    private readonly Func<
        SessionCurrentLineageSnapshot,
        int,
        CancellationToken,
        ValueTask<DerivedRecapSelection>
    > _select;
    private readonly Func<
        EventAddress,
        EventAddress,
        CancellationToken,
        ValueTask<DerivedRecapRestoreResult>
    > _restore;
    private readonly Func<
        DerivedRecapPlanningBaseline,
        CancellationToken,
        ValueTask<DerivedRecapExecutionResult>
    > _run;
    private readonly Func<DerivedRecapPlanningDiagnostics?>
        _getLastPlanningDiagnostics;
    private readonly Func<
        CancellationToken,
        ValueTask<DerivedRecapExecutionResult>
    >? _runCurrentPlanning;
    private readonly DerivedRecapPlanningBaseline?
        _pinnedPlanningBaseline;
    private readonly bool _isFrozenBuildingMode;
    private bool _frozenBuildingHandled;
    private bool _pinnedPlanningBaselineConsumed;

    public DerivedRecapOnlineLifecycleCoordinator(
        SessionJournalEngine engine,
        DerivedRecapStore store,
        RecapPlanningInputs inputs,
        RecapPlanningLimits limits,
        IRecapBlockMaintainerRegistry maintainers
    ) {
        _engine = engine
            ?? throw new ArgumentNullException(nameof(engine));
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(maintainers);

        _candidates = new DerivedRecapContextCandidateSource(
            store,
            engine
        );
        var planner = new DerivedRecapPlannerExecutor(
            engine,
            store,
            inputs,
            limits,
            maintainers
        );
        var restorer = new DerivedRecapRestoreExecutor(
            engine,
            store,
            maintainers
        );
        _select = store.SelectNthPreviousAsync;
        _restore = restorer.RestoreAsync;
        _run = planner.RunAsync;
        _runCurrentPlanning = planner.RunAsync;
        _getLastPlanningDiagnostics =
            () => planner.LastPlanningDiagnostics;
    }

    /// <summary>
    /// Binds the first new-planning pass to the Host's pre-client readiness
    /// snapshot. If the same online operation reaches lifecycle again after
    /// raw Observation growth, the same Planner authority self-pins the new
    /// current snapshot without reloading active configuration.
    /// </summary>
    public DerivedRecapOnlineLifecycleCoordinator(
        SessionJournalEngine engine,
        DerivedRecapStore store,
        RecapPlanningInputs inputs,
        RecapPlanningLimits limits,
        IRecapBlockMaintainerRegistry maintainers,
        DerivedRecapPlanningBaseline planningBaseline
    ) : this(engine, store, inputs, limits, maintainers) {
        _pinnedPlanningBaseline = planningBaseline
            ?? throw new ArgumentNullException(nameof(planningBaseline));
    }

    /// <summary>
    /// Creates an online lifecycle bound to one already-frozen Building.
    /// Active Planner inputs and repo config are intentionally absent.
    /// </summary>
    public static DerivedRecapOnlineLifecycleCoordinator
        CreateForFrozenBuilding(
        SessionJournalEngine engine,
        DerivedRecapStore store,
        BuildingDescriptor buildingDescriptor,
        IRecapBlockMaintainerRegistry maintainers
    ) {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(buildingDescriptor);
        ArgumentNullException.ThrowIfNull(maintainers);
        var building = new DerivedRecapBuildingExecutor(
            engine,
            store,
            maintainers
        );
        var restorer = new DerivedRecapRestoreExecutor(
            engine,
            store,
            maintainers
        );
        return new DerivedRecapOnlineLifecycleCoordinator(
            engine,
            new DerivedRecapContextCandidateSource(store, engine),
            store.SelectNthPreviousAsync,
            restorer.RestoreAsync,
            (_, cancellationToken) => building.ResumeAsync(
                buildingDescriptor,
                cancellationToken
            ),
            static () => null,
            isFrozenBuildingMode: true
        );
    }

    internal DerivedRecapOnlineLifecycleCoordinator(
        SessionJournalEngine engine,
        ICoherentContextCandidateSource candidates,
        Func<
            SessionCurrentLineageSnapshot,
            int,
            CancellationToken,
            ValueTask<DerivedRecapSelection>
        > select,
        Func<
            EventAddress,
            EventAddress,
            CancellationToken,
            ValueTask<DerivedRecapRestoreResult>
        > restore,
        Func<
            DerivedRecapPlanningBaseline,
            CancellationToken,
            ValueTask<DerivedRecapExecutionResult>
        > run,
        Func<DerivedRecapPlanningDiagnostics?>?
            getLastPlanningDiagnostics = null,
        bool isFrozenBuildingMode = false,
        DerivedRecapPlanningBaseline? pinnedPlanningBaseline = null,
        Func<
            CancellationToken,
            ValueTask<DerivedRecapExecutionResult>
        >? runCurrentPlanning = null
    ) {
        _engine = engine
            ?? throw new ArgumentNullException(nameof(engine));
        _candidates = candidates
            ?? throw new ArgumentNullException(nameof(candidates));
        _select = select
            ?? throw new ArgumentNullException(nameof(select));
        _restore = restore
            ?? throw new ArgumentNullException(nameof(restore));
        _run = run ?? throw new ArgumentNullException(nameof(run));
        _getLastPlanningDiagnostics =
            getLastPlanningDiagnostics ?? (static () => null);
        _isFrozenBuildingMode = isFrozenBuildingMode;
        _pinnedPlanningBaseline = pinnedPlanningBaseline;
        _runCurrentPlanning = runCurrentPlanning;
    }

    public DerivedRecapPlanningDiagnostics? LastPlanningDiagnostics =>
        _getLastPlanningDiagnostics();

    public ValueTask<SessionContextCandidateSelection> SelectAsync(
        SessionContextSelectionRequest request,
        CancellationToken cancellationToken
    ) => _candidates.SelectAsync(request, cancellationToken);

    public ValueTask<SessionContextCandidate> MaterializeAsync(
        SessionContextCandidateDescriptor descriptor,
        CancellationToken cancellationToken
    ) => _candidates.MaterializeAsync(
        descriptor,
        cancellationToken
    );

    public async ValueTask<SessionContextLifecycleResult> PrepareAsync(
        SessionJournalEngine engine,
        SessionContextLifecycleRequest request,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(request);
        if (!ReferenceEquals(engine, _engine)) {
            throw new ArgumentException(
                "DerivedRecap lifecycle must run on its bound "
                + "SessionJournalEngine.",
                nameof(engine)
            );
        }
        request.Selection.ValidateShape();
        RequireSupportedPhase(request.Phase);
        RequireBoundaryAndPhase(engine, request, cancellationToken);

        SessionGoverningSetup governingSetup =
            engine.ResolveGoverningSetup(
                request.Boundary,
                cancellationToken
            );
        if (governingSetup.Head != request.Boundary
            || governingSetup.RuntimeConfig.DerivedContext.NthPrevious
                != request.Selection.NthPrevious) {
            throw new InvalidOperationException(
                "DerivedRecap lifecycle request does not match the "
                + "authoritative governing setup."
            );
        }
        RequireCurrentBoundary(request.Boundary);

        SessionCurrentLineageSnapshot lineage =
            engine.ReadCurrentLineageHeaders(cancellationToken);
        if (lineage.CapturedHead != request.Boundary) {
            throw new InvalidOperationException(
                "DerivedRecap lifecycle lineage capture is stale."
            );
        }

        DerivedRecapSelection latest =
            await SelectAsync(lineage, 0, cancellationToken)
                .ConfigureAwait(false);
        DerivedRecapPlanningBaseline planningBaseline;
        try {
            planningBaseline =
                DerivedRecapPlanningBaseline.FromSelection(
                    lineage.CapturedHead,
                    latest
                );
        }
        catch (ArgumentException) {
            return SelectionUnavailable(
                latest,
                "latest Published recap"
            );
        }
        if (latest
                is DerivedRecapSelection.ExactPublishedSetInvalid
                    invalidLatest) {
            SessionContextLifecycleResult? restoreFailure =
                await RestoreAsync(
                        invalidLatest.SetAdmissionAnchor,
                        request.Boundary,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            if (restoreFailure is not null) {
                return restoreFailure;
            }
            latest = await SelectAsync(
                    lineage,
                    0,
                    cancellationToken
                )
                .ConfigureAwait(false);
            if (latest is not DerivedRecapSelection.Selected) {
                return SelectionUnavailable(
                    latest,
                    "latest Published recap after its one restore"
                );
            }
            if (((DerivedRecapSelection.Selected)latest)
                    .Descriptor.SetAdmissionAnchor
                != invalidLatest.SetAdmissionAnchor) {
                return Unavailable(
                    DerivedRecapExecutionDefectCodes.SourceChanged,
                    "Latest Published recap changed anchor during "
                    + "exact Restore."
                );
            }
        }
        else if (latest is not (
                     DerivedRecapSelection.Selected
                     or DerivedRecapSelection.EmptyLineage
                 )) {
            return SelectionUnavailable(
                latest,
                "latest Published recap"
            );
        }
        bool latestWasEmptyLineage =
            latest is DerivedRecapSelection.EmptyLineage;

        DerivedRecapExecutionResult build =
            await RunAsync(planningBaseline, cancellationToken)
                .ConfigureAwait(false);
        SessionContextLifecycleResult? buildFailure =
            MapBuildFailure(build);
        if (buildFailure is not null) {
            return buildFailure;
        }

        int configuredOrdinal = request.Selection.NthPrevious;
        DerivedRecapSelection configured =
            await SelectAsync(
                    lineage,
                    configuredOrdinal,
                    cancellationToken
                )
                .ConfigureAwait(false);
        if (configured
                is DerivedRecapSelection.ExactPublishedSetInvalid
                    invalidConfigured) {
            SessionContextLifecycleResult? restoreFailure =
                await RestoreAsync(
                        invalidConfigured.SetAdmissionAnchor,
                        request.Boundary,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            if (restoreFailure is not null) {
                return restoreFailure;
            }
            configured = await SelectAsync(
                    lineage,
                    configuredOrdinal,
                    cancellationToken
                )
                .ConfigureAwait(false);
            if (configured is not DerivedRecapSelection.Selected) {
                return SelectionUnavailable(
                    configured,
                    "configured Published recap after its one restore"
                );
            }
        }

        RequireBoundaryAndPhase(engine, request, cancellationToken);
        return configured switch {
            DerivedRecapSelection.Selected =>
                SessionContextLifecycleResult.Ready,
            DerivedRecapSelection.EmptyLineage
                when latestWasEmptyLineage
                    && build is DerivedRecapExecutionResult.NoBuild =>
                SessionContextLifecycleResult.RawHistoryReady,
            _ => SelectionUnavailable(
                configured,
                "configured Published recap"
            )
        };
    }

    private async ValueTask<DerivedRecapSelection> SelectAsync(
        SessionCurrentLineageSnapshot lineage,
        int nthPrevious,
        CancellationToken cancellationToken
    ) {
        RequireCurrentBoundary(lineage.CapturedHead);
        DerivedRecapSelection selection =
            await _select(
                    lineage,
                    nthPrevious,
                    cancellationToken
                )
                .ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(selection);
        RequireCurrentBoundary(lineage.CapturedHead);
        return selection;
    }

    private async ValueTask<SessionContextLifecycleResult?> RestoreAsync(
        EventAddress admissionAnchor,
        EventAddress expectedRawHead,
        CancellationToken cancellationToken
    ) {
        RequireCurrentBoundary(expectedRawHead);
        DerivedRecapRestoreResult result =
            await _restore(
                    admissionAnchor,
                    expectedRawHead,
                    cancellationToken
                )
                .ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(result);
        RequireCurrentBoundary(expectedRawHead);
        return result switch {
            DerivedRecapRestoreResult.Restored => null,
            DerivedRecapRestoreResult.Retryable retryable =>
                Backpressure(retryable.Code, retryable.Detail),
            DerivedRecapRestoreResult.Unavailable unavailable
                when HasOnlyRestoreLimitDefects(unavailable.Defects) =>
                Backpressure(unavailable.Defects),
            DerivedRecapRestoreResult.Unavailable unavailable =>
                Unavailable(unavailable.Defects),
            DerivedRecapRestoreResult.BlockFailed failed =>
                Unavailable(failed.Code, failed.Detail),
            _ => throw new InvalidDataException(
                $"Unknown DerivedRecap restore result "
                + $"'{result.GetType().Name}'."
            )
        };
    }

    private async ValueTask<DerivedRecapExecutionResult> RunAsync(
        DerivedRecapPlanningBaseline baseline,
        CancellationToken cancellationToken
    ) {
        EventAddress expectedRawHead = RequireCurrentHead();
        if (_isFrozenBuildingMode && _frozenBuildingHandled) {
            return new DerivedRecapExecutionResult.NoBuild(
                RecapPlanReasons.FrozenBuildingHandled
            );
        }
        DerivedRecapExecutionResult result;
        bool usedPinnedBaseline = _pinnedPlanningBaseline is not null
            && !_pinnedPlanningBaselineConsumed;
        if (usedPinnedBaseline) {
            result = await _run(
                    _pinnedPlanningBaseline!,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        else if (_pinnedPlanningBaselineConsumed
                 && _runCurrentPlanning is not null) {
            result = await _runCurrentPlanning(cancellationToken)
                .ConfigureAwait(false);
        }
        else {
            result = await _run(baseline, cancellationToken)
                .ConfigureAwait(false);
        }
        ArgumentNullException.ThrowIfNull(result);
        RequireCurrentBoundary(expectedRawHead);
        if (usedPinnedBaseline
            && result is (
                DerivedRecapExecutionResult.NoBuild
                or DerivedRecapExecutionResult.Published
            )) {
            _pinnedPlanningBaselineConsumed = true;
        }
        if (_isFrozenBuildingMode
            && result is DerivedRecapExecutionResult.Published) {
            _frozenBuildingHandled = true;
        }
        return result;
    }

    private static SessionContextLifecycleResult? MapBuildFailure(
        DerivedRecapExecutionResult result
    ) => result switch {
        DerivedRecapExecutionResult.NoBuild => null,
        DerivedRecapExecutionResult.Published => null,
        DerivedRecapExecutionResult.Retryable retryable =>
            Backpressure(retryable.Code, retryable.Detail),
        DerivedRecapExecutionResult.Unavailable unavailable
            when HasOnlyBuildLimitDefects(unavailable.Defects) =>
            Backpressure(unavailable.Defects),
        DerivedRecapExecutionResult.Unavailable unavailable =>
            Unavailable(unavailable.Defects),
        DerivedRecapExecutionResult.BlockFailed failed =>
            Unavailable(failed.Code, failed.Detail),
        _ => throw new InvalidDataException(
            $"Unknown DerivedRecap execution result "
            + $"'{result.GetType().Name}'."
        )
    };

    private static bool HasOnlyBuildLimitDefects(
        IReadOnlyList<DerivedRecapExecutionDefect> defects
    ) => defects.Count != 0
         && defects.All(static defect => defect.Code is
             RecapPlanDefectCodes.MaxRawGrowthEventCountExceeded
             or RecapPlanDefectCodes.RouteLimitExceeded
             or RecapPlanDefectCodes.CallLimitExceeded
             or RecapPlanDefectCodes.RawStepLimitExceeded
             or RecapPlanDefectCodes.RawBuildLimitExceeded);

    private static bool HasOnlyRestoreLimitDefects(
        IReadOnlyList<DerivedRecapRestoreDefect> defects
    ) => defects.Count != 0
         && defects.All(static defect =>
             defect.Code
             == DerivedRecapRestoreDefectCodes
                 .ExecutionLimitExceeded);

    private static SessionContextLifecycleResult Backpressure(
        string code,
        string detail
    ) => new(
        SessionContextLifecycleStatus.Backpressure,
        $"{code}: {detail}"
    );

    private static SessionContextLifecycleResult Backpressure(
        IReadOnlyList<DerivedRecapExecutionDefect> defects
    ) => new(
        SessionContextLifecycleStatus.Backpressure,
        FormatDefects(defects.Select(static defect =>
            (defect.Code, defect.Detail)))
    );

    private static SessionContextLifecycleResult Backpressure(
        IReadOnlyList<DerivedRecapRestoreDefect> defects
    ) => new(
        SessionContextLifecycleStatus.Backpressure,
        FormatDefects(defects.Select(static defect =>
            (defect.Code, defect.Detail)))
    );

    private static SessionContextLifecycleResult Unavailable(
        string code,
        string detail
    ) => new(
        SessionContextLifecycleStatus.Unavailable,
        $"{code}: {detail}"
    );

    private static SessionContextLifecycleResult Unavailable(
        IReadOnlyList<DerivedRecapExecutionDefect> defects
    ) => new(
        SessionContextLifecycleStatus.Unavailable,
        FormatDefects(defects.Select(static defect =>
            (defect.Code, defect.Detail)))
    );

    private static SessionContextLifecycleResult Unavailable(
        IReadOnlyList<DerivedRecapRestoreDefect> defects
    ) => new(
        SessionContextLifecycleStatus.Unavailable,
        FormatDefects(defects.Select(static defect =>
            (defect.Code, defect.Detail)))
    );

    private static SessionContextLifecycleResult SelectionUnavailable(
        DerivedRecapSelection selection,
        string stage
    ) => selection switch {
        DerivedRecapSelection.OrdinalUnavailable => Unavailable(
            "OrdinalUnavailable",
            $"{stage} ordinal is unavailable."
        ),
        DerivedRecapSelection.ExactPublishedSetInvalid invalid =>
            Unavailable(
                invalid.Defects.Select(static defect =>
                    (defect.Code, defect.Detail)),
                $"{stage} remains invalid"
            ),
        DerivedRecapSelection.StoreUnavailable unavailable =>
            Unavailable("StoreUnavailable", unavailable.Reason),
        DerivedRecapSelection.EmptyLineage => Unavailable(
            "UnexpectedEmptyLineage",
            $"{stage} disappeared after exact restore."
        ),
        DerivedRecapSelection.Selected => throw new InvalidOperationException(
            "A selected Recap cannot be mapped to unavailable."
        ),
        _ => throw new InvalidDataException(
            $"Unknown DerivedRecap selection "
            + $"'{selection.GetType().Name}'."
        )
    };

    private static SessionContextLifecycleResult Unavailable(
        IEnumerable<(string Code, string Detail)> defects,
        string prefix
    ) => new(
        SessionContextLifecycleStatus.Unavailable,
        $"{prefix}: {FormatDefects(defects)}"
    );

    private static string FormatDefects(
        IEnumerable<(string Code, string Detail)> defects
    ) => string.Join(
        "; ",
        defects.Select(static defect =>
            $"{defect.Code}: {defect.Detail}")
    );

    private static void RequireSupportedPhase(
        SessionExecutionPhase phase
    ) {
        if (phase is not (
                SessionExecutionPhase.Idle
                or SessionExecutionPhase.TurnFailed
                or SessionExecutionPhase.AwaitingAgentAction
            )) {
            throw new ArgumentException(
                "DerivedRecap online lifecycle requires an idle, "
                + "failed, or unprepared completion boundary.",
                nameof(phase)
            );
        }
    }

    private static void RequireBoundaryAndPhase(
        SessionJournalEngine engine,
        SessionContextLifecycleRequest request,
        CancellationToken cancellationToken
    ) {
        SessionExecutionBoundaryInspection boundary =
            engine.InspectExecutionBoundary(cancellationToken);
        if (boundary.Head != request.Boundary
            || boundary.Phase != request.Phase) {
            throw new InvalidOperationException(
                "DerivedRecap online lifecycle request is stale."
            );
        }
    }

    private EventAddress RequireCurrentHead() =>
        _engine.ReadCurrentHead()
        ?? throw new InvalidOperationException(
            "DerivedRecap online lifecycle requires a non-empty "
            + "SessionJournal."
        );

    private void RequireCurrentBoundary(EventAddress expected) {
        EventAddress? observed = _engine.ReadCurrentHead();
        if (observed != expected) {
            throw new InvalidOperationException(
                "DerivedRecap online lifecycle became stale. "
                + $"Expected current head '{expected}', observed "
                + $"'{observed}'."
            );
        }
    }
}
