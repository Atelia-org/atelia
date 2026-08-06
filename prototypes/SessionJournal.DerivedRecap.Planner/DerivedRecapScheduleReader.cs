using Atelia.EventJournal;
using Atelia.SessionJournal.DerivedRecap.Store;

namespace Atelia.SessionJournal.DerivedRecap.Planner;

/// <summary>
/// Performs the shared read-only portion of one new-planning attempt. It
/// stops after exact cadence evaluation and never calls the planning policy,
/// a Maintainer, the Building installer, or the publisher.
/// </summary>
internal sealed class DerivedRecapScheduleReader {
    private readonly SessionJournalReadView _engine;
    private readonly DerivedRecapStore _store;
    private readonly RecapPlanningInputs _inputs;
    private readonly RecapPlanningLimits _limits;

    internal DerivedRecapScheduleReader(
        SessionJournalReadView engine,
        DerivedRecapStore store,
        RecapPlanningInputs inputs,
        RecapPlanningLimits limits,
        RecapProtocolHardCaps? hardCaps = null
    ) {
        _engine = engine
            ?? throw new ArgumentNullException(nameof(engine));
        _store = store
            ?? throw new ArgumentNullException(nameof(store));
        _inputs = inputs
            ?? throw new ArgumentNullException(nameof(inputs));
        _limits = limits
            ?? throw new ArgumentNullException(nameof(limits));
        (hardCaps ?? RecapProtocolHardCaps.V4).ValidatePlanningAuthority(
            inputs,
            limits
        );
        RequireSameBinding(store, engine);
    }

    internal async ValueTask<DerivedRecapScheduleReadResult> ReadAsync(
        DerivedRecapPlanningBaseline baseline,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(baseline);

        SessionCurrentLineagePrefix lineage;
        DerivedRecapSelection selection;
        try {
            DerivedRecapLineageView view =
                DerivedRecapLineageView.Capture(
                    _store,
                    _engine,
                    cancellationToken
                );
            lineage = view.Prefix;
            selection = await view.SelectNthPreviousAsync(
                    nthPrevious: 0,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            IsAvailabilityException(exception)
        ) {
            return Unavailable(
                DerivedRecapExecutionDefectCodes.StoreUnavailable,
                exception.Message
            );
        }
        if (lineage.CapturedHead != baseline.CapturedRawHead) {
            return RetryableRawHead(baseline.CapturedRawHead);
        }
        if (MatchPlanningBaseline(baseline, selection)
            is { } baselineMismatch) {
            return baselineMismatch;
        }

        PublishedRecapDescriptor? latest;
        switch (selection) {
            case DerivedRecapSelection.Selected selected:
                latest = selected.Descriptor;
                break;
            case DerivedRecapSelection.EmptyLineage:
                latest = null;
                break;
            case DerivedRecapSelection.ExactPublishedSetInvalid invalid:
                return Unavailable(invalid.Defects);
            case DerivedRecapSelection.BeyondPrefix beyond:
                return new DerivedRecapScheduleReadResult.BeyondPrefix(
                    DerivedRecapBeyondPrefixStage.NewPlanningSourceAnchor,
                    beyond.Evidence
                );
            case DerivedRecapSelection.StoreUnavailable unavailable:
                return Unavailable(
                    DerivedRecapExecutionDefectCodes.StoreUnavailable,
                    unavailable.Reason
                );
            case DerivedRecapSelection.OrdinalUnavailable:
                return Unavailable(
                    DerivedRecapExecutionDefectCodes.StoreUnavailable,
                    "Latest strict Published ordinal is unavailable."
                );
            default:
                throw new InvalidOperationException(
                    "Unknown DerivedRecap selection result."
                );
        }

        PublishedPlanSnapshot? sourcePlan = null;
        SessionHistoryPlanningSeed? emptyPlanningSeed = null;
        if (latest is null) {
            SessionCreatedPlanningSeedReadResult startRead =
                _engine.ReadSessionCreatedPlanningSeedAtBounded(
                    lineage.CapturedHead,
                    _limits.MaxRawGrowthEventCount,
                    cancellationToken
                );
            if (startRead
                is SessionCreatedPlanningSeedReadResult.BeyondPrefix
                    search) {
                return new DerivedRecapScheduleReadResult.BeyondPrefix(
                    DerivedRecapBeyondPrefixStage.NewPlanningRawGrowth,
                    search.ContinuationEvidence
                );
            }
            emptyPlanningSeed =
                ((SessionCreatedPlanningSeedReadResult.Available)
                    startRead).Seed;
        }
        else {
            PublishedPlanReadResult planRead;
            try {
                planRead = await _store.ReadPublishedPlanAsync(
                        latest,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (
                IsAvailabilityException(exception)
            ) {
                return Unavailable(
                    DerivedRecapExecutionDefectCodes
                        .PublishedSourceUnavailable,
                    exception.Message
                );
            }
            switch (planRead) {
                case PublishedPlanReadResult.Available available:
                    sourcePlan = available.Snapshot;
                    RecapCatalogShapeComparison comparison =
                        RecapCatalogShape.Compare(
                            RecapCatalogShape.ProjectActive(
                                _inputs.OrderedCatalog
                            ),
                            RecapCatalogShape.ProjectFrozen(
                                available.Snapshot.FrozenPlan.Blocks
                            )
                        );
                    if (!comparison.IsExactMatch) {
                        return Unavailable(
                            DerivedRecapExecutionDefectCodes
                                .CatalogMigrationRequired,
                            comparison.Detail
                        );
                    }
                    break;
                case PublishedPlanReadResult.Changed changed:
                    return new DerivedRecapScheduleReadResult.Retryable(
                        DerivedRecapOperationPreparationRetryKind
                            .SourceChanged,
                        $"Latest Published plan changed from "
                        + $"'{changed.Expected}' to "
                        + $"'{changed.Observed}'."
                    );
                case PublishedPlanReadResult.Unavailable unavailable:
                    return Unavailable(unavailable.Defects);
                default:
                    throw new InvalidOperationException(
                        "Unknown Published plan read result."
                    );
            }
        }

        try {
            EarliestSourceBoundaryResolution earliestResolution =
                FindEarliestSourceBoundary(lineage, sourcePlan);
            if (earliestResolution.BeyondPrefix is { } sourceBeyond) {
                return new DerivedRecapScheduleReadResult.BeyondPrefix(
                    DerivedRecapBeyondPrefixStage
                        .NewPlanningSourceAnchor,
                    sourceBeyond
                );
            }
            if (earliestResolution.OffLineageDetail is { } offLineage) {
                return Unavailable(
                    DerivedRecapExecutionDefectCodes
                        .RawPlanningUnavailable,
                    offLineage
                );
            }
            RecapReplayBoundary? earliestSource =
                earliestResolution.Boundary;
            EventAddress? earliestCursor = earliestSource?.Address;
            SessionHistoryPlanningWindow allRelevantRaw;
            if (earliestCursor is null) {
                SessionHistoryPlanningWindowReadResult bounded =
                    _engine.ReadHistoryPlanningWindowAtBounded(
                        lineage.CapturedHead,
                        emptyPlanningSeed!,
                        _limits.MaxRawGrowthEventCount,
                        cancellationToken
                    );
                if (bounded
                    is SessionHistoryPlanningWindowReadResult.BeyondPrefix
                        beyond) {
                    return new DerivedRecapScheduleReadResult.BeyondPrefix(
                        DerivedRecapBeyondPrefixStage
                            .NewPlanningPendingWindow,
                        beyond.Evidence
                    );
                }
                allRelevantRaw =
                    ((SessionHistoryPlanningWindowReadResult.Available)
                        bounded).Window;
            }
            else {
                SessionHistoryPlanningWindowProofResult proofResult =
                    _engine.ProveHistoryPlanningWindowAtBounded(
                        lineage.CapturedHead,
                        earliestCursor.Value,
                        _limits.MaxRawGrowthEventCount,
                        cancellationToken
                    );
                if (proofResult
                    is SessionHistoryPlanningWindowProofResult.BeyondPrefix
                        beyond) {
                    return new DerivedRecapScheduleReadResult.BeyondPrefix(
                        DerivedRecapBeyondPrefixStage
                            .NewPlanningPendingWindow,
                        beyond.Evidence
                    );
                }
                SessionGoverningSetupProofResult setupProofResult =
                    _engine.ProveGoverningSetupInPrefix(
                        lineage,
                        earliestCursor.Value,
                        earliestSource!.Setups
                    );
                if (setupProofResult
                    is SessionGoverningSetupProofResult.BeyondPrefix
                        setupBeyond) {
                    return new DerivedRecapScheduleReadResult.BeyondPrefix(
                        DerivedRecapBeyondPrefixStage
                            .NewPlanningSourceAnchor,
                        setupBeyond.Evidence.ContinuationEvidence
                    );
                }
                SessionHistoryPlanningSeed sourceSeed =
                    _engine.MaterializeHistoryPlanningSeed(
                        ((SessionGoverningSetupProofResult.Available)
                            setupProofResult).Proof,
                        cancellationToken
                    );
                allRelevantRaw =
                    _engine.MaterializeHistoryPlanningWindow(
                        ((SessionHistoryPlanningWindowProofResult.Available)
                            proofResult).Proof,
                        sourceSeed,
                        cancellationToken
                    );
            }
            if (allRelevantRaw.ObservedRawHead
                != lineage.CapturedHead) {
                throw new InvalidDataException(
                    "Exact history window does not match the captured "
                    + "raw head."
                );
            }

            EventAddress cadenceBaseline =
                latest?.SetAdmissionAnchor
                ?? allRelevantRaw.StartExclusive;
            RecapRawSafetyResult rawSafety =
                RecapPlanEvaluator.EvaluateRawSafety(
                    _limits,
                    lineage,
                    cadenceBaseline
                );
            if (rawSafety
                is RecapRawSafetyResult.Unavailable rawUnavailable) {
                if (rawUnavailable.RawGrowthEventCount is { } rawCount) {
                    return new DerivedRecapScheduleReadResult
                        .RawSafetyRejected(
                            lineage.CapturedHead,
                            cadenceBaseline,
                            latest?.SetAdmissionAnchor,
                            _inputs.Cadence,
                            rawCount,
                            _limits.MaxRawGrowthEventCount,
                            Map(rawUnavailable.Defects)
                        );
                }
                return new DerivedRecapScheduleReadResult.Unavailable(
                    Map(rawUnavailable.Defects)
                );
            }

            cancellationToken.ThrowIfCancellationRequested();
            RecapHistoryLoadMeasurement historyLoad =
                RecapHistoryLoadProjector.Measure(
                    allRelevantRaw,
                    cadenceBaseline,
                    _inputs.HistoryUnitLoadEstimator
                );
            cancellationToken.ThrowIfCancellationRequested();
            RecapSchedulingResult exactSchedule =
                RecapPlanEvaluator.EvaluateSchedule(
                    _inputs,
                    _limits,
                    new RecapSchedulingFacts(
                        lineage.CapturedHead,
                        lineage.HeadToOldest,
                        new RecapHistoryWindowFacts(
                            allRelevantRaw.StartExclusive,
                            allRelevantRaw.Units.Count,
                            allRelevantRaw.ReplaySafeBoundaries
                        ),
                        cadenceBaseline,
                        latest?.SetAdmissionAnchor,
                        historyLoad
                    )
                );
            cancellationToken.ThrowIfCancellationRequested();
            return exactSchedule switch {
                RecapSchedulingResult.Ready ready =>
                    new DerivedRecapScheduleReadResult.Ready(
                        lineage,
                        latest,
                        allRelevantRaw.StartExclusive,
                        allRelevantRaw,
                        ready,
                        CreateProgress(
                            lineage.CapturedHead,
                            cadenceBaseline,
                            latest?.SetAdmissionAnchor,
                            ready.Cadence
                        )
                    ),
                RecapSchedulingResult.NoBuild noBuild =>
                    new DerivedRecapScheduleReadResult.NoBuild(
                        noBuild.Reason,
                        CreateProgress(
                            lineage.CapturedHead,
                            cadenceBaseline,
                            latest?.SetAdmissionAnchor,
                            noBuild.Measurement
                        )
                    ),
                RecapSchedulingResult.Unavailable unavailable =>
                    new DerivedRecapScheduleReadResult.Unavailable(
                        Map(unavailable.Defects),
                        unavailable.Measurement is { } measurement
                            ? CreateProgress(
                                lineage.CapturedHead,
                                cadenceBaseline,
                                latest?.SetAdmissionAnchor,
                                measurement
                            )
                            : null
                    ),
                _ => throw new InvalidOperationException(
                    "Unknown exact scheduling result."
                )
            };
        }
        catch (HistoryLoadMeasurementException exception) {
            return Unavailable(exception.Code, exception.Message);
        }
        catch (Exception exception) when (
            IsAvailabilityException(exception)
        ) {
            return Unavailable(
                DerivedRecapExecutionDefectCodes.RawPlanningUnavailable,
                exception.Message
            );
        }
    }

    private DerivedRecapPlanningProgressSnapshot CreateProgress(
        EventAddress capturedRawHead,
        EventAddress cadenceBaseline,
        EventAddress? latestPublishedSetAnchor,
        RecapCadenceFacts cadence
    ) => CreateProgress(
        capturedRawHead,
        cadenceBaseline,
        latestPublishedSetAnchor,
        new RecapExactScheduleMeasurement(
            cadence.HistoryUnitLoadEstimatorId,
            cadence.GrowthHistoryLoad,
            cadence.GrowthHistoryUnitCount,
            cadence.RawGrowthEventCount
        )
    );

    private DerivedRecapPlanningProgressSnapshot CreateProgress(
        EventAddress capturedRawHead,
        EventAddress cadenceBaseline,
        EventAddress? latestPublishedSetAnchor,
        RecapExactScheduleMeasurement measurement
    ) => new(
        capturedRawHead,
        cadenceBaseline,
        latestPublishedSetAnchor,
        _inputs.Cadence,
        measurement
    );

    private static EarliestSourceBoundaryResolution
        FindEarliestSourceBoundary(
        SessionCurrentLineagePrefix lineage,
        PublishedPlanSnapshot? source
    ) {
        if (source is null) {
            return new EarliestSourceBoundaryResolution(null);
        }
        if (source.BlockCommitments.Count == 0) {
            throw new InvalidDataException(
                "Published source has no active frozen inputs."
            );
        }
        RecapReplayBoundary? earliest = null;
        int earliestIndex = -1;
        foreach (RecapBlockCommitment commitment
                 in source.BlockCommitments) {
            int inputIndex;
            switch (lineage.Lookup(commitment.AbsorbedThrough)) {
                case SessionCurrentLineageAnchorLookup.Found found:
                    inputIndex = found.Index;
                    break;
                case SessionCurrentLineageAnchorLookup.BeyondPrefix beyond:
                    return new EarliestSourceBoundaryResolution(
                        Boundary: null,
                        BeyondPrefix: beyond.Evidence
                    );
                case SessionCurrentLineageAnchorLookup.OffLineage:
                    return new EarliestSourceBoundaryResolution(
                        Boundary: null,
                        OffLineageDetail:
                            $"Published source block "
                            + $"'{commitment.RecapBlockId}' cursor "
                            + "is outside the captured raw lineage."
                    );
                default:
                    throw new InvalidOperationException(
                        "Unknown bounded-lineage lookup result."
                    );
            }
            if (inputIndex > earliestIndex) {
                earliest = new RecapReplayBoundary(
                    commitment.AbsorbedThrough,
                    FindFrozenCommitmentSetups(
                        source.FrozenPlan,
                        commitment
                    )
                );
                earliestIndex = inputIndex;
            }
        }
        return new EarliestSourceBoundaryResolution(earliest);
    }

    private static SessionContextAnchorSetupReferences
        FindFrozenCommitmentSetups(
        DerivedRecapSetManifest manifest,
        RecapBlockCommitment commitment
    ) {
        RecapBlockPlan plan = manifest.Blocks.Single(candidate =>
            candidate.RecapBlockId == commitment.RecapBlockId);
        if (plan is InheritRecapBlockPlan inherit) {
            return inherit.SourceAbsorbedThroughSetups;
        }
        if (plan is MaintainRecapBlockPlan
            && commitment.AbsorbedThrough
                == manifest.SetAdmissionAnchor) {
            return manifest.SetAdmissionAnchorSetups;
        }
        throw new InvalidDataException(
            $"Published plan has no frozen setup authority for block "
            + $"'{commitment.RecapBlockId}' at "
            + $"'{commitment.AbsorbedThrough}'."
        );
    }

    private static DerivedRecapScheduleReadResult?
        MatchPlanningBaseline(
        DerivedRecapPlanningBaseline baseline,
        DerivedRecapSelection observed
    ) {
        if (observed is DerivedRecapSelection.StoreUnavailable
                unavailable) {
            return Unavailable(
                DerivedRecapExecutionDefectCodes.StoreUnavailable,
                unavailable.Reason
            );
        }
        if (observed is DerivedRecapSelection.BeyondPrefix beyond) {
            return new DerivedRecapScheduleReadResult.BeyondPrefix(
                DerivedRecapBeyondPrefixStage.NewPlanningSourceAnchor,
                beyond.Evidence
            );
        }
        if (baseline.ExpectedLatestAnchor is null) {
            return observed is DerivedRecapSelection.EmptyLineage
                ? null
                : RetryableSource(
                    "Expected no latest Published recap, but the "
                    + $"latest selection is '{observed.GetType().Name}'."
                );
        }
        if (observed
            is not DerivedRecapSelection.Selected selected) {
            return RetryableSource(
                $"Expected latest Published anchor "
                + $"'{baseline.ExpectedLatestAnchor}' to resolve to a "
                + "healthy exact selection after any Restore, but "
                + $"observed '{observed.GetType().Name}'."
            );
        }
        if (selected.Descriptor.SetAdmissionAnchor
            != baseline.ExpectedLatestAnchor) {
            return RetryableSource(
                $"Expected latest Published anchor "
                + $"'{baseline.ExpectedLatestAnchor}', observed "
                + $"'{selected.Descriptor.SetAdmissionAnchor}'."
            );
        }
        if (baseline.ExpectedLatestPublished is { } exact
            && selected.Descriptor != exact) {
            return RetryableSource(
                $"Expected latest Published identity '{exact}', "
                + $"observed '{selected.Descriptor}'."
            );
        }
        return null;
    }

    private static DerivedRecapScheduleReadResult.Retryable
        RetryableSource(string detail) => new(
            DerivedRecapOperationPreparationRetryKind.SourceChanged,
            detail
        );

    private static DerivedRecapScheduleReadResult.Retryable
        RetryableRawHead(EventAddress expected) => new(
            DerivedRecapOperationPreparationRetryKind.RawHeadChanged,
            $"Raw SessionJournal head changed during planning. Expected "
            + $"'{expected}'."
        );

    private static DerivedRecapScheduleReadResult.Unavailable Unavailable(
        IReadOnlyList<RecapPlanDefect> defects
    ) => new(Map(defects));

    private static DerivedRecapScheduleReadResult.Unavailable Unavailable(
        IReadOnlyList<RecapStructuralDefect> defects
    ) => new([
        .. defects.Select(defect => new DerivedRecapExecutionDefect(
            defect.Code,
            defect.Detail
        ))
    ]);

    private static DerivedRecapScheduleReadResult.Unavailable Unavailable(
        string code,
        string detail
    ) => new([new DerivedRecapExecutionDefect(code, detail)]);

    private static IReadOnlyList<DerivedRecapExecutionDefect> Map(
        IReadOnlyList<RecapPlanDefect> defects
    ) => Array.AsReadOnly([
        .. defects.Select(defect => new DerivedRecapExecutionDefect(
            defect.Code,
            defect.Detail
        ))
    ]);

    private static bool IsAvailabilityException(Exception exception)
        => exception is RecapRawHeadChangedException
            or InvalidDataException
            or InvalidOperationException
            or ArgumentException
            or IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or KeyNotFoundException;

    private static void RequireSameBinding(
        DerivedRecapStore store,
        SessionJournalReadView engine
    ) {
        string storePath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(store.SessionRepositoryPath)
        );
        string enginePath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(engine.Path)
        );
        if (!string.Equals(
                storePath,
                enginePath,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal
            )
            || store.RefId != engine.BranchRefId) {
            throw new ArgumentException(
                "DerivedRecap schedule reader, Store, and "
                + "SessionJournalReadView must bind the same repository "
                + "and RefId."
            );
        }
    }

    private sealed record EarliestSourceBoundaryResolution(
        RecapReplayBoundary? Boundary,
        SessionCurrentLineageBeyondPrefix? BeyondPrefix = null,
        string? OffLineageDetail = null
    );
}

internal abstract record DerivedRecapScheduleReadResult {
    private DerivedRecapScheduleReadResult() { }

    internal sealed record Ready(
        SessionCurrentLineagePrefix Lineage,
        PublishedRecapDescriptor? Latest,
        EventAddress EmptyReplayStartExclusive,
        SessionHistoryPlanningWindow PlanningWindow,
        RecapSchedulingResult.Ready Schedule,
        DerivedRecapPlanningProgressSnapshot Progress
    ) : DerivedRecapScheduleReadResult;

    internal sealed record NoBuild(
        string Reason,
        DerivedRecapPlanningProgressSnapshot Progress
    ) : DerivedRecapScheduleReadResult;

    internal sealed record RawSafetyRejected(
        EventAddress CapturedRawHead,
        EventAddress CadenceBaseline,
        EventAddress? LatestPublishedSetAnchor,
        RecapCadenceConfig Cadence,
        int RawGrowthEventCount,
        int MaxRawGrowthEventCount,
        IReadOnlyList<DerivedRecapExecutionDefect> Defects
    ) : DerivedRecapScheduleReadResult;

    internal sealed record Retryable(
        DerivedRecapOperationPreparationRetryKind Kind,
        string Detail
    ) : DerivedRecapScheduleReadResult {
        internal string Code => Kind switch {
            DerivedRecapOperationPreparationRetryKind.RawHeadChanged =>
                DerivedRecapExecutionDefectCodes.RawHeadChanged,
            DerivedRecapOperationPreparationRetryKind.SourceChanged =>
                DerivedRecapExecutionDefectCodes.SourceChanged,
            _ => throw new InvalidOperationException(
                "Unknown preparation retry kind."
            )
        };
    }

    internal sealed record Unavailable(
        IReadOnlyList<DerivedRecapExecutionDefect> Defects,
        DerivedRecapPlanningProgressSnapshot? Progress = null
    ) : DerivedRecapScheduleReadResult;

    internal sealed record BeyondPrefix(
        DerivedRecapBeyondPrefixStage Stage,
        SessionCurrentLineageBeyondPrefix Evidence
    ) : DerivedRecapScheduleReadResult;
}
