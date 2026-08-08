using Atelia.EventJournal;
using Atelia.SessionJournal.DerivedRecap.Store;

namespace Atelia.SessionJournal.DerivedRecap.Planner;

/// <summary>
/// Exact-head, read-only cadence progress from one successful schedule read.
/// This is operation-local diagnostic data, not persisted planning authority.
/// </summary>
public sealed class DerivedRecapPlanningProgressSnapshot {
    internal DerivedRecapPlanningProgressSnapshot(
        EventAddress capturedRawHead,
        EventAddress cadenceBaseline,
        EventAddress? latestPublishedSetAnchor,
        RecapCadenceConfig cadence,
        RecapExactScheduleMeasurement measurement
    ) {
        if (capturedRawHead == default) {
            throw new ArgumentException(
                "Captured raw head cannot be default.",
                nameof(capturedRawHead)
            );
        }
        if (cadenceBaseline == default) {
            throw new ArgumentException(
                "Cadence baseline cannot be default.",
                nameof(cadenceBaseline)
            );
        }
        if (latestPublishedSetAnchor is { } latest
            && latest != cadenceBaseline) {
            throw new ArgumentException(
                "Latest Published anchor must equal the cadence baseline.",
                nameof(latestPublishedSetAnchor)
            );
        }
        Cadence = cadence
            ?? throw new ArgumentNullException(nameof(cadence));
        Measurement = measurement
            ?? throw new ArgumentNullException(nameof(measurement));
        if (!string.Equals(
                cadence.HistoryUnitLoadEstimatorId,
                measurement.HistoryUnitLoadEstimatorId,
                StringComparison.Ordinal
            )) {
            throw new ArgumentException(
                "Cadence and measurement estimator identities differ.",
                nameof(measurement)
            );
        }

        CapturedRawHead = capturedRawHead;
        CadenceBaseline = cadenceBaseline;
        LatestPublishedSetAnchor = latestPublishedSetAnchor;
        long remaining = checked(
            cadence.BuildThresholdHistoryLoad.Value
            - Math.Min(
                cadence.BuildThresholdHistoryLoad.Value,
                measurement.GrowthHistoryLoad.Value
            )
        );
        RemainingHistoryLoad = new HistoryLoadUnit(remaining);
    }

    public EventAddress CapturedRawHead { get; }
    public EventAddress CadenceBaseline { get; }
    public EventAddress? LatestPublishedSetAnchor { get; }
    public RecapCadenceConfig Cadence { get; }
    public RecapExactScheduleMeasurement Measurement { get; }
    public HistoryLoadUnit BuildThresholdHistoryLoad =>
        Cadence.BuildThresholdHistoryLoad;
    public HistoryLoadUnit RemainingHistoryLoad { get; }
    public bool IsBuildThresholdReached =>
        RemainingHistoryLoad.Value == 0;
}

/// <summary>
/// Exhaustive result of an exact-head, read-only DerivedRecap progress
/// inspection. A successful cadence read never invokes policy, Maintainers,
/// Building installation, or publication.
/// </summary>
public abstract record DerivedRecapPlanningProgressInspectionResult {
    private DerivedRecapPlanningProgressInspectionResult() { }

    public sealed record FrozenBuilding(
        EventAddress CapturedRawHead,
        BuildingDescriptor Descriptor
    ) : DerivedRecapPlanningProgressInspectionResult;

    public sealed record BelowCadenceThreshold(
        DerivedRecapPlanningProgressSnapshot Snapshot
    ) : DerivedRecapPlanningProgressInspectionResult;

    public sealed record AwaitingReplaySafeAdmission(
        DerivedRecapPlanningProgressSnapshot Snapshot
    ) : DerivedRecapPlanningProgressInspectionResult;

    public sealed record CadenceReady(
        DerivedRecapPlanningProgressSnapshot Snapshot
    ) : DerivedRecapPlanningProgressInspectionResult;

    public sealed record FullRebuildRequired(
        DerivedRecapFullRebuildRequirement Requirement
    ) : DerivedRecapPlanningProgressInspectionResult;

    public sealed record Retryable(
        DerivedRecapOperationPreparationRetryKind Kind,
        string Detail
    ) : DerivedRecapPlanningProgressInspectionResult {
        public string Code => Kind switch {
            DerivedRecapOperationPreparationRetryKind.RawHeadChanged =>
                DerivedRecapExecutionDefectCodes.RawHeadChanged,
            DerivedRecapOperationPreparationRetryKind.SourceChanged =>
                DerivedRecapExecutionDefectCodes.SourceChanged,
            _ => throw new InvalidOperationException(
                "Unknown preparation retry kind."
            )
        };
    }

    public sealed record Unavailable(
        IReadOnlyList<DerivedRecapExecutionDefect> Defects,
        DerivedRecapPlanningProgressSnapshot? Snapshot = null
    ) : DerivedRecapPlanningProgressInspectionResult;

    public sealed record BeyondPrefix(
        DerivedRecapBeyondPrefixStage Stage,
        SessionCurrentLineageBeyondPrefix Evidence
    ) : DerivedRecapPlanningProgressInspectionResult;
}

/// <summary>
/// Inspects current DerivedRecap cadence progress through the same
/// Building-first preparation and exact schedule reader used by production
/// new planning.
/// </summary>
public static class DerivedRecapPlanningProgressInspector {
    public static async ValueTask<
        DerivedRecapPlanningProgressInspectionResult
    > InspectAsync(
        SessionJournalReadView engine,
        DerivedRecapStore store,
        RecapMaintainerCapabilitySnapshot capabilities,
        IRecapActivePlanningConfigurationSource activeConfiguration,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(activeConfiguration);

        DerivedRecapOperationPreparationResult prepared =
            await DerivedRecapOperationPreparer.PrepareAsync(
                    engine,
                    store,
                    capabilities,
                    activeConfiguration,
                    cancellationToken
                )
                .ConfigureAwait(false);
        switch (prepared) {
            case DerivedRecapOperationPreparationResult.Retryable
                retryable:
                return new DerivedRecapPlanningProgressInspectionResult
                    .Retryable(retryable.Kind, retryable.Detail);
            case DerivedRecapOperationPreparationResult.Unavailable
                unavailable:
                return new DerivedRecapPlanningProgressInspectionResult
                    .Unavailable(Map(unavailable.Defects));
            case DerivedRecapOperationPreparationResult.BeyondPrefix
                beyond:
                return new DerivedRecapPlanningProgressInspectionResult
                    .BeyondPrefix(beyond.Stage, beyond.Evidence);
            case DerivedRecapOperationPreparationResult
                .FullRebuildRequired rebuild:
                return new DerivedRecapPlanningProgressInspectionResult
                    .FullRebuildRequired(rebuild.Requirement);
        }

        PreparedRecapOperationAuthority authority =
            ((DerivedRecapOperationPreparationResult.Ready)prepared)
                .Authority;
        if (authority
            is PreparedRecapOperationAuthority.FrozenBuilding frozen) {
            return new DerivedRecapPlanningProgressInspectionResult
                .FrozenBuilding(
                    frozen.Lineage.CapturedHead,
                    frozen.Descriptor
                );
        }

        var planning =
            (PreparedRecapOperationAuthority.NewPlanning)authority;
        var reader = new DerivedRecapScheduleReader(
            engine,
            store,
            planning.Configuration.PlanningInputs,
            planning.Configuration.PlanningLimits
        );
        DerivedRecapScheduleReadResult result = await reader.ReadAsync(
                planning.Baseline,
                cancellationToken
            )
            .ConfigureAwait(false);
        return result switch {
            DerivedRecapScheduleReadResult.NoBuild noBuild
                when noBuild.Reason
                    == RecapPlanReasons.BelowCadenceThreshold =>
                new DerivedRecapPlanningProgressInspectionResult
                    .BelowCadenceThreshold(noBuild.Progress),
            DerivedRecapScheduleReadResult.NoBuild noBuild
                when noBuild.Reason
                    == RecapPlanReasons.AwaitingReplaySafeAdmission =>
                new DerivedRecapPlanningProgressInspectionResult
                    .AwaitingReplaySafeAdmission(noBuild.Progress),
            DerivedRecapScheduleReadResult.NoBuild noBuild =>
                new DerivedRecapPlanningProgressInspectionResult
                    .Unavailable([
                        new DerivedRecapExecutionDefect(
                            RecapPlanDefectCodes.PlanningFactsInvalid,
                            $"Unknown exact schedule NoBuild reason "
                            + $"'{noBuild.Reason}'."
                        )
                    ], noBuild.Progress),
            DerivedRecapScheduleReadResult.Ready ready =>
                new DerivedRecapPlanningProgressInspectionResult
                    .CadenceReady(ready.Progress),
            DerivedRecapScheduleReadResult.FullRebuildRequired rebuild =>
                new DerivedRecapPlanningProgressInspectionResult
                    .FullRebuildRequired(rebuild.Requirement),
            DerivedRecapScheduleReadResult.Retryable retryable =>
                new DerivedRecapPlanningProgressInspectionResult
                    .Retryable(retryable.Kind, retryable.Detail),
            DerivedRecapScheduleReadResult.Unavailable unavailable =>
                new DerivedRecapPlanningProgressInspectionResult
                    .Unavailable(
                        unavailable.Defects,
                        unavailable.Progress
                    ),
            DerivedRecapScheduleReadResult.BeyondPrefix beyond =>
                new DerivedRecapPlanningProgressInspectionResult
                    .BeyondPrefix(beyond.Stage, beyond.Evidence),
            _ => throw new InvalidOperationException(
                "Unknown DerivedRecap schedule read result."
            )
        };
    }

    private static IReadOnlyList<DerivedRecapExecutionDefect> Map(
        IReadOnlyList<DerivedRecapOperationPreparationDefect> defects
    ) => Array.AsReadOnly([
        .. defects.Select(defect => new DerivedRecapExecutionDefect(
            defect.Code,
            defect.Detail
        ))
    ]);
}
