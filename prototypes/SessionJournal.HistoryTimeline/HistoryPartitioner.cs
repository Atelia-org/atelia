using Atelia.EventJournal;
using SJ = Atelia.SessionJournal;

namespace Atelia.SessionJournal.HistoryTimeline;

public static class HistoryPartitioner {
    public static HistoryPartitionResult Partition(
        SJ.SessionHistoryPlanningWindow window,
        HistoryLoadBaseline baseline,
        PartitionPolicyRevision policy,
        IHistoryUnitLoadEstimator estimator
    ) {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(estimator);
        if (!string.Equals(
                policy.PartitionAlgorithmId,
                HistoryPartitionAlgorithms
                    .FirstReplaySafeBoundaryAtTargetV1,
                StringComparison.Ordinal)) {
            throw new NotSupportedException(
                $"Unsupported partition algorithm '{policy.PartitionAlgorithmId}'."
            );
        }

        string estimatorId =
            HistoryLoadMeasurementEngine.RequireEstimatorId(estimator);
        if (!string.Equals(
                estimatorId,
                policy.HistoryLoadEstimatorId,
                StringComparison.Ordinal)) {
            throw new HistoryLoadMeasurementException(
                HistoryLoadMeasurementDefectCodes.MeasurementInvalid,
                "Partition policy and estimator IDs differ."
            );
        }

        PartitionCursor cursor = ValidateConsumedBaselinePrefix(
            window,
            baseline
        );
        int baselineRawPosition = cursor.BaselineRawPosition;
        SJ.SessionContextAnchorSetupReferences startSetups =
            cursor.StartSetups;
        HashSet<EventAddress> consumedRawAddresses =
            cursor.ConsumedRawAddresses;

        long measuredLoad = 0;
        int measuredRenderedBytes = 0;
        int completedUnitCount = baseline.CompletedUnitCount;
        int boundaryIndex = baseline.FirstLaterBoundaryIndex;
        int finalRawEventCount = 0;
        SJ.SessionHistoryPlanningBoundary? pendingBoundary = null;

        for (int rawPosition = baselineRawPosition + 1;
             rawPosition < window.RawAddresses.Count;
             rawPosition++) {
            EventAddress rawAddress = window.RawAddresses[rawPosition];
            try {
                _ = HistoryTimelineSyntax.RequireEventAddress(
                    rawAddress,
                    nameof(window.RawAddresses)
                );
            }
            catch (ArgumentException exception) {
                throw InvalidWindow(
                    "Planning raw range contains an invalid address.",
                    exception
                );
            }
            if (!consumedRawAddresses.Add(rawAddress)) {
                throw InvalidWindow(
                    $"Planning raw range repeats address '{rawAddress}'."
                );
            }
            int rawEventCount = checked(
                rawPosition - baselineRawPosition
            );
            finalRawEventCount = rawEventCount;
            if (rawEventCount > policy.MaxRawEvents) {
                return Limit(
                    HistoryPartitionLimitKind.MaxRawEvents,
                    measuredLoad,
                    policy.MaxRawEvents,
                    completedUnitCount,
                    measuredRenderedBytes
                );
            }

            if (pendingBoundary is null
                && boundaryIndex
                    < window.ReplaySafeBoundaries.Count) {
                pendingBoundary =
                    window.ReplaySafeBoundaries[boundaryIndex]
                    ?? throw InvalidWindow(
                        "Planning window contains a null replay-safe boundary."
                    );
                try {
                    _ = HistoryTimelineSyntax.RequireEventAddress(
                        pendingBoundary.Address,
                        nameof(window.ReplaySafeBoundaries)
                    );
                }
                catch (ArgumentException exception) {
                    throw InvalidWindow(
                        "Planning window contains an invalid replay-safe boundary address.",
                        exception
                    );
                }
            }

            if (pendingBoundary is not null
                && pendingBoundary.Address == rawAddress) {
                SJ.SessionHistoryPlanningBoundary boundary =
                    pendingBoundary;
                if (boundary.CompletedUnitCount < completedUnitCount
                    || boundary.CompletedUnitCount
                        > window.Units.Count) {
                    throw InvalidWindow(
                        "Planning replay-safe boundary has an invalid completed-unit count."
                    );
                }
                while (completedUnitCount
                       < boundary.CompletedUnitCount) {
                    SJ.SessionHistoryPlanningUnit unit =
                        window.Units[completedUnitCount]
                        ?? throw new HistoryLoadMeasurementException(
                            HistoryLoadMeasurementDefectCodes
                                .PlanningWindowInvalid,
                            "Planning window contains a null HistoryUnit."
                        );
                    HistoryUnitLoadMeasurement measured =
                        HistoryLoadMeasurementEngine.MeasureUnit(
                            estimator,
                            unit
                        );
                    if (measured.RenderedUtf8Bytes < 1) {
                        throw new HistoryLoadMeasurementException(
                            HistoryLoadMeasurementDefectCodes
                                .MeasurementInvalid,
                            "Timeline partition measurement requires "
                            + "positive rendered UTF-8 byte evidence."
                        );
                    }
                    try {
                        measuredLoad = checked(
                            measuredLoad + measured.Load.Value
                        );
                        measuredRenderedBytes = checked(
                            measuredRenderedBytes
                            + measured.RenderedUtf8Bytes
                        );
                    }
                    catch (OverflowException exception) {
                        throw new HistoryLoadMeasurementException(
                            HistoryLoadMeasurementDefectCodes
                                .MeasurementOverflow,
                            "Partition HistoryLoad aggregation overflowed.",
                            exception
                        );
                    }
                    completedUnitCount++;
                    if (measuredRenderedBytes
                        > policy.MaxRenderedBytes) {
                        return Limit(
                            HistoryPartitionLimitKind.MaxRenderedBytes,
                            measuredLoad,
                            rawEventCount,
                            completedUnitCount,
                            measuredRenderedBytes
                        );
                    }
                }

                if (measuredLoad >= policy.TargetHistoryLoad.Value) {
                    if (!window.ReplaySafeBoundarySetups.TryGetValue(
                            boundary.Address,
                            out SJ.SessionContextAnchorSetupReferences?
                                endSetups)) {
                        throw new HistoryLoadMeasurementException(
                            HistoryLoadMeasurementDefectCodes
                                .PlanningWindowInvalid,
                            "Selected replay-safe boundary has no setup evidence."
                        );
                    }
                    return new HistoryPartitionResult.Selected(
                        new HistoryPartitionPoint(
                            policy.TimelineId,
                            policy.PolicyDigest,
                            baseline.Address,
                            boundary.Address,
                            startSetups,
                            endSetups,
                            baseline.CompletedUnitCount,
                            boundary.CompletedUnitCount,
                            new HistoryLoadUnit(measuredLoad),
                            rawEventCount,
                            measuredRenderedBytes
                        )
                    );
                }
                if (measuredRenderedBytes
                    == policy.MaxRenderedBytes) {
                    return Limit(
                        HistoryPartitionLimitKind.MaxRenderedBytes,
                        measuredLoad,
                        rawEventCount,
                        completedUnitCount,
                        measuredRenderedBytes
                    );
                }
                boundaryIndex++;
                pendingBoundary = null;
            }
            else if (pendingBoundary is not null
                     && consumedRawAddresses.Contains(
                         pendingBoundary.Address
                     )) {
                throw InvalidWindow(
                    "Planning replay-safe boundaries are out of raw order."
                );
            }

            if (rawEventCount == policy.MaxRawEvents) {
                return Limit(
                    HistoryPartitionLimitKind.MaxRawEvents,
                    measuredLoad,
                    rawEventCount,
                    completedUnitCount,
                    measuredRenderedBytes
                );
            }
        }

        if (pendingBoundary is not null
            || boundaryIndex < window.ReplaySafeBoundaries.Count) {
            throw InvalidWindow(
                "Planning window contains a replay-safe boundary outside its raw evidence."
            );
        }

        return new HistoryPartitionResult.NotEnough(
            new HistoryLoadUnit(measuredLoad),
            finalRawEventCount,
            completedUnitCount,
            measuredRenderedBytes
        );
    }

    private static HistoryPartitionResult.LimitExceeded Limit(
        HistoryPartitionLimitKind limit,
        long measuredLoad,
        int rawEventCount,
        int completedUnitCount,
        int measuredRenderedUtf8Bytes
    ) => new(
        limit,
        new HistoryLoadUnit(measuredLoad),
        rawEventCount,
        completedUnitCount,
        measuredRenderedUtf8Bytes
    );

    private static PartitionCursor ValidateConsumedBaselinePrefix(
        SJ.SessionHistoryPlanningWindow window,
        HistoryLoadBaseline baseline
    ) {
        HistoryLoadWindowValidator.ValidateCollections(
            window,
            requireBoundarySetups: true
        );
        if (baseline.CompletedUnitCount < 0
            || baseline.CompletedUnitCount > window.Units.Count
            || baseline.FirstLaterBoundaryIndex < 0
            || baseline.FirstLaterBoundaryIndex
                > window.ReplaySafeBoundaries.Count) {
            throw InvalidBaseline(
                "HistoryLoad baseline metadata is out of range."
            );
        }

        var consumedRaw = new HashSet<EventAddress>();
        if (baseline.Address == window.StartExclusive) {
            if (baseline.CompletedUnitCount != 0
                || baseline.FirstLaterBoundaryIndex != 0) {
                throw InvalidBaseline(
                    "The planning-window start baseline must use zero unit and boundary offsets."
                );
            }
            return new PartitionCursor(
                -1,
                HistoryTimelineSyntax.RequireSetups(
                    window.StartSetups,
                    nameof(window.StartSetups)
                ),
                consumedRaw
            );
        }

        int baselineRawPosition = -1;
        for (int index = 0;
             index < window.RawAddresses.Count;
             index++) {
            EventAddress address = window.RawAddresses[index];
            if (!consumedRaw.Add(address)) {
                throw InvalidWindow(
                    $"Planning raw range repeats address '{address}'."
                );
            }
            if (address == baseline.Address) {
                baselineRawPosition = index;
                break;
            }
        }
        if (baselineRawPosition < 0) {
            throw InvalidBaseline(
                "HistoryLoad baseline is not in the consumed raw prefix."
            );
        }
        if (baseline.FirstLaterBoundaryIndex < 1) {
            throw InvalidBaseline(
                "A non-start baseline must follow its exact replay-safe boundary."
            );
        }

        int previousRawPosition = -1;
        int previousCompletedUnitCount = 0;
        SJ.SessionHistoryPlanningBoundary? exactBaseline = null;
        for (int index = 0;
             index < baseline.FirstLaterBoundaryIndex;
             index++) {
            SJ.SessionHistoryPlanningBoundary boundary =
                window.ReplaySafeBoundaries[index]
                ?? throw InvalidWindow(
                    "Planning window contains a null replay-safe boundary."
                );
            int rawPosition = IndexOfConsumedRaw(
                window.RawAddresses,
                baselineRawPosition,
                boundary.Address
            );
            if (rawPosition <= previousRawPosition
                || boundary.CompletedUnitCount
                    < previousCompletedUnitCount
                || boundary.CompletedUnitCount > window.Units.Count) {
                throw InvalidWindow(
                    "Planning baseline prefix has malformed boundary order or completed-unit counts."
                );
            }
            previousRawPosition = rawPosition;
            previousCompletedUnitCount =
                boundary.CompletedUnitCount;
            exactBaseline = boundary;
        }
        if (exactBaseline is null
            || exactBaseline.Address != baseline.Address
            || exactBaseline.CompletedUnitCount
                != baseline.CompletedUnitCount) {
            throw InvalidBaseline(
                "HistoryLoad baseline does not match its exact replay-safe boundary."
            );
        }
        if (!window.ReplaySafeBoundarySetups.TryGetValue(
                baseline.Address,
                out SJ.SessionContextAnchorSetupReferences?
                    startSetups)) {
            throw InvalidBaseline(
                "HistoryLoad baseline has no exact setup evidence."
            );
        }
        return new PartitionCursor(
            baselineRawPosition,
            HistoryTimelineSyntax.RequireSetups(
                startSetups,
                nameof(window.ReplaySafeBoundarySetups)
            ),
            consumedRaw
        );
    }

    private static int IndexOfConsumedRaw(
        IReadOnlyList<EventAddress> rawAddresses,
        int lastConsumedIndex,
        EventAddress address
    ) {
        for (int index = 0; index <= lastConsumedIndex; index++) {
            if (rawAddresses[index] == address) {
                return index;
            }
        }
        return -1;
    }

    private static HistoryLoadMeasurementException InvalidBaseline(
        string detail
    ) => new(
        HistoryLoadMeasurementDefectCodes.CadenceBaselineInvalid,
        detail
    );

    private static HistoryLoadMeasurementException InvalidWindow(
        string detail,
        Exception? innerException = null
    ) => new(
        HistoryLoadMeasurementDefectCodes.PlanningWindowInvalid,
        detail,
        innerException
    );

    private sealed record PartitionCursor(
        int BaselineRawPosition,
        SJ.SessionContextAnchorSetupReferences StartSetups,
        HashSet<EventAddress> ConsumedRawAddresses
    );
}
