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
        SessionCurrentLineagePrefix,
        int,
        CancellationToken,
        ValueTask<DerivedRecapSelection>
    > _select;
    private readonly Func<
        SessionCurrentLineagePrefix,
        EventAddress,
        CancellationToken,
        ValueTask<PublishedRestoreInspectionResult>
    >? _inspectPublished;
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

    internal DerivedRecapOnlineLifecycleCoordinator(
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
        _select = (lineage, nthPrevious, cancellationToken) =>
            SelectBoundAsync(
                store,
                engine,
                lineage,
                nthPrevious,
                cancellationToken
            );
        _inspectPublished =
            (lineage, admissionAnchor, cancellationToken) =>
                InspectPublishedBoundAsync(
                    store,
                    engine,
                    lineage,
                    admissionAnchor,
                    cancellationToken
                );
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
    internal DerivedRecapOnlineLifecycleCoordinator(
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
    internal static DerivedRecapOnlineLifecycleCoordinator
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
            (lineage, nthPrevious, cancellationToken) =>
                SelectBoundAsync(
                    store,
                    engine,
                    lineage,
                    nthPrevious,
                    cancellationToken
                ),
            (lineage, admissionAnchor, cancellationToken) =>
                InspectPublishedBoundAsync(
                    store,
                    engine,
                    lineage,
                    admissionAnchor,
                    cancellationToken
                ),
            restorer.RestoreAsync,
            (_, cancellationToken) => building.ResumeAsync(
                buildingDescriptor,
                cancellationToken
            ),
            static () => null,
            isFrozenBuildingMode: true
        );
    }

    /// <summary>
    /// Creates the only public production lifecycle from a preparer-issued
    /// authority. New planning is pinned to its captured baseline; frozen
    /// Building execution is pinned to its exact descriptor.
    /// </summary>
    public static DerivedRecapOnlineLifecycleCoordinator Create(
        SessionJournalEngine engine,
        DerivedRecapStore store,
        PreparedRecapOperationAuthority authority,
        IRecapBlockMaintainerRegistry maintainers
    ) {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(maintainers);
        if (!authority.Binding.Matches(
                engine.Path,
                engine.BranchRefId
            )
            || !authority.Binding.Matches(
                store.SessionRepositoryPath,
                store.RefId
            )) {
            throw new ArgumentException(
                "Prepared DerivedRecap authority, Store, and "
                + "SessionJournalEngine must bind the same repository "
                + "and RefId.",
                nameof(authority)
            );
        }

        return authority switch {
            PreparedRecapOperationAuthority.NewPlanning planning =>
                new DerivedRecapOnlineLifecycleCoordinator(
                    engine,
                    store,
                    planning.Configuration.PlanningInputs,
                    planning.Configuration.PlanningLimits,
                    maintainers,
                    planning.Baseline
                ),
            PreparedRecapOperationAuthority.FrozenBuilding frozen =>
                CreateForFrozenBuilding(
                    engine,
                    store,
                    frozen.Descriptor,
                    maintainers
                ),
            _ => throw new InvalidDataException(
                "Unknown prepared DerivedRecap authority."
            )
        };
    }

    internal DerivedRecapOnlineLifecycleCoordinator(
        SessionJournalEngine engine,
        ICoherentContextCandidateSource candidates,
        Func<
            SessionCurrentLineagePrefix,
            int,
            CancellationToken,
            ValueTask<DerivedRecapSelection>
        > select,
        Func<
            SessionCurrentLineagePrefix,
            EventAddress,
            CancellationToken,
            ValueTask<PublishedRestoreInspectionResult>
        >? inspectPublished,
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
        _inspectPublished = inspectPublished;
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

        SessionCurrentLineagePrefix lineage =
            engine.ReadCurrentLineagePrefix(
                RecapProtocolHardCaps.V4.MaxRawGrowthEventCount + 1,
                cancellationToken
            );
        if (lineage.CapturedHead != request.Boundary) {
            throw new InvalidOperationException(
                "DerivedRecap lifecycle lineage capture is stale."
            );
        }

        DerivedRecapSelection latest =
            await SelectAsync(lineage, 0, cancellationToken)
                .ConfigureAwait(false);
        if (latest is DerivedRecapSelection.Selected selectedLatest) {
            SelectedRestoreCheck check =
                await CheckSelectedRestoreAsync(
                        lineage,
                        selectedLatest,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            if (check.Failure is not null) {
                return check.Failure;
            }
            if (check.NeedsRestore) {
                SessionContextLifecycleResult? restoreFailure =
                    await RestoreAsync(
                            selectedLatest.Descriptor
                                .SetAdmissionAnchor,
                            request.Boundary,
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                if (restoreFailure is not null) {
                    return restoreFailure;
                }
                lineage = CaptureFreshLineage(
                    request.Boundary,
                    cancellationToken
                );
                latest = await SelectAsync(
                        lineage,
                        0,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                if (latest is not DerivedRecapSelection.Selected repaired) {
                    return SelectionUnavailable(
                        latest,
                        "latest Published recap after its one restore"
                    );
                }
                if (repaired.Descriptor.SetAdmissionAnchor
                    != selectedLatest.Descriptor.SetAdmissionAnchor) {
                    return Unavailable(
                        DerivedRecapExecutionDefectCodes.SourceChanged,
                        "Latest Published recap changed anchor during "
                        + "exact Restore."
                    );
                }
            }
        }
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
            lineage = CaptureFreshLineage(request.Boundary, cancellationToken);
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

        lineage = CaptureFreshLineage(request.Boundary, cancellationToken);
        DerivedRecapExecutionResult build =
            await RunAsync(planningBaseline, cancellationToken)
                .ConfigureAwait(false);
        SessionContextLifecycleResult? buildFailure =
            MapBuildFailure(build);
        if (buildFailure is not null) {
            return buildFailure;
        }

        lineage = CaptureFreshLineage(request.Boundary, cancellationToken);
        int configuredOrdinal = request.Selection.NthPrevious;
        DerivedRecapSelection configured =
            await SelectAsync(
                    lineage,
                    configuredOrdinal,
                    cancellationToken
                )
                .ConfigureAwait(false);
        if (configured
            is DerivedRecapSelection.Selected selectedConfigured) {
            SelectedRestoreCheck check =
                await CheckSelectedRestoreAsync(
                        lineage,
                        selectedConfigured,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            if (check.Failure is not null) {
                return check.Failure;
            }
            if (check.NeedsRestore) {
                SessionContextLifecycleResult? restoreFailure =
                    await RestoreAsync(
                            selectedConfigured.Descriptor
                                .SetAdmissionAnchor,
                            request.Boundary,
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                if (restoreFailure is not null) {
                    return restoreFailure;
                }
                lineage = CaptureFreshLineage(
                    request.Boundary,
                    cancellationToken
                );
                configured = await SelectAsync(
                        lineage,
                        configuredOrdinal,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                if (configured
                    is not DerivedRecapSelection.Selected repaired) {
                    return SelectionUnavailable(
                        configured,
                        "configured Published recap after its one restore"
                    );
                }
                if (repaired.Descriptor.SetAdmissionAnchor
                    != selectedConfigured.Descriptor
                        .SetAdmissionAnchor) {
                    return Unavailable(
                        DerivedRecapExecutionDefectCodes.SourceChanged,
                        "Configured Published recap changed anchor during "
                        + "exact Restore."
                    );
                }
            }
        }
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
            lineage = CaptureFreshLineage(request.Boundary, cancellationToken);
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
        SessionCurrentLineagePrefix lineage,
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

    private SessionCurrentLineagePrefix CaptureFreshLineage(
        EventAddress expectedHead,
        CancellationToken cancellationToken
    ) {
        SessionCurrentLineagePrefix prefix =
            _engine.ReadCurrentLineagePrefix(
                RecapProtocolHardCaps.V4.MaxRawGrowthEventCount + 1,
                cancellationToken
            );
        if (prefix.CapturedHead != expectedHead) {
            throw new InvalidOperationException(
                "DerivedRecap lifecycle raw head changed between phases."
            );
        }
        return prefix;
    }

    private static async ValueTask<DerivedRecapSelection>
        SelectBoundAsync(
        DerivedRecapStore store,
        SessionJournalEngine engine,
        SessionCurrentLineagePrefix expectedLineage,
        int nthPrevious,
        CancellationToken cancellationToken
    ) {
        DerivedRecapLineageView view =
            DerivedRecapLineageView.Capture(
                store,
                engine,
                cancellationToken
            );
        if (view.CapturedHead != expectedLineage.CapturedHead) {
            throw new InvalidOperationException(
                "DerivedRecap lifecycle lineage changed before "
                + "Store selection."
            );
        }
        return await view.SelectNthPreviousAsync(
                nthPrevious,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private static async ValueTask<PublishedRestoreInspectionResult>
        InspectPublishedBoundAsync(
        DerivedRecapStore store,
        SessionJournalEngine engine,
        SessionCurrentLineagePrefix expectedLineage,
        EventAddress admissionAnchor,
        CancellationToken cancellationToken
    ) {
        DerivedRecapLineageView view =
            DerivedRecapLineageView.Capture(
                store,
                engine,
                cancellationToken
            );
        if (view.CapturedHead != expectedLineage.CapturedHead) {
            throw new InvalidOperationException(
                "DerivedRecap lifecycle lineage changed before "
                + "Published restore inspection."
            );
        }
        return await view.InspectPublishedForRestoreAsync(
                admissionAnchor,
                cancellationToken
            )
            .ConfigureAwait(false);
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
            DerivedRecapRestoreResult.BeyondPrefix beyond =>
                SessionContextLifecycleResult.BeyondPrefix(
                    beyond.Evidence
                ),
            DerivedRecapRestoreResult.BlockFailed failed =>
                Unavailable(failed.Code, failed.Detail),
            _ => throw new InvalidDataException(
                $"Unknown DerivedRecap restore result "
                + $"'{result.GetType().Name}'."
            )
        };
    }

    private async ValueTask<SelectedRestoreCheck>
        CheckSelectedRestoreAsync(
        SessionCurrentLineagePrefix lineage,
        DerivedRecapSelection.Selected selected,
        CancellationToken cancellationToken
    ) {
        if (_inspectPublished is null) {
            return new SelectedRestoreCheck(
                NeedsRestore: false,
                Failure: null
            );
        }
        PublishedRestoreInspectionResult result =
            await _inspectPublished(
                    lineage,
                    selected.Descriptor.SetAdmissionAnchor,
                    cancellationToken
                )
                .ConfigureAwait(false);
        return result switch {
            PublishedRestoreInspectionResult.Available available =>
                new SelectedRestoreCheck(
                    available.Inspection.Blocks.Values.Any(
                        static block => block.Capability
                            is not PublishedBlockRestoreCapability
                                .KeepCommitted
                    ),
                    Failure: null
                ),
            PublishedRestoreInspectionResult.BeyondPrefix beyond =>
                new SelectedRestoreCheck(
                    NeedsRestore: false,
                    SessionContextLifecycleResult.BeyondPrefix(
                        beyond.Evidence
                    )
                ),
            PublishedRestoreInspectionResult.Unavailable unavailable =>
                new SelectedRestoreCheck(
                    NeedsRestore: false,
                    Unavailable(unavailable.Defects.Select(
                        static defect =>
                            new DerivedRecapRestoreDefect(
                                defect.Code,
                                defect.Detail
                            )
                    ).ToArray())
                ),
            _ => throw new InvalidDataException(
                "Unknown Published restore inspection result."
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
        DerivedRecapExecutionResult.BeyondPrefix beyond =>
            SessionContextLifecycleResult.BeyondPrefix(
                beyond.Evidence
            ),
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
        DerivedRecapSelection.BeyondPrefix beyond =>
            SessionContextLifecycleResult.BeyondPrefix(
                beyond.Evidence
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

    private sealed record SelectedRestoreCheck(
        bool NeedsRestore,
        SessionContextLifecycleResult? Failure
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
