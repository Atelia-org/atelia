using Atelia.EventJournal;
using SJ = Atelia.SessionJournal;

namespace Atelia.SessionJournal.HistoryTimeline;

public static class HistoryRecentReserveAnchorLimits {
    public const int MaximumTimelineRows = 4_097;
}

public sealed record HistoryRecentReserveRequirement {
    public HistoryRecentReserveRequirement(
        string expectedPartitionPolicyDigest,
        string expectedEstimatorId,
        HistoryLoadUnit minimumRecentHistoryLoad
    ) {
        ExpectedPartitionPolicyDigest = HistoryTimelineSyntax.RequireSha256(
            expectedPartitionPolicyDigest,
            nameof(expectedPartitionPolicyDigest));
        ExpectedEstimatorId = HistoryTimelineSyntax.RequireIdentifier(
            expectedEstimatorId,
            nameof(expectedEstimatorId));
        if (minimumRecentHistoryLoad.Value < 1) {
            throw new ArgumentOutOfRangeException(
                nameof(minimumRecentHistoryLoad));
        }
        MinimumRecentHistoryLoad = minimumRecentHistoryLoad;
    }

    public string ExpectedPartitionPolicyDigest { get; }
    public string ExpectedEstimatorId { get; }
    public HistoryLoadUnit MinimumRecentHistoryLoad { get; }
}

public sealed record HistoryRecentReserveAnchorMetrics(
    int ExaminedTimelineRows,
    int ExaminedRawEvents,
    int ExaminedHistoryUnits,
    int ExaminedRenderedUtf8Bytes
);

public abstract record HistoryRecentReserveAnchorResult {
    private HistoryRecentReserveAnchorResult() { }

    public sealed record Eligible(
        HistoryTimelineSelectedRow Anchor,
        IReadOnlyList<HistoryTimelineSelectedRow> HeadThroughAnchor,
        HistoryLoadUnit RetainedHistoryLoad,
        HistoryRecentReserveAnchorMetrics Metrics
    ) : HistoryRecentReserveAnchorResult;

    public sealed record ReserveBootstrapRequired(
        IReadOnlyList<HistoryTimelineSelectedRow> HeadThroughRoot,
        HistoryLoadUnit RetainedHistoryLoad,
        HistoryRecentReserveAnchorMetrics Metrics
    ) : HistoryRecentReserveAnchorResult;

    public sealed record NoRows(HistoryRecentReserveAnchorMetrics Metrics)
        : HistoryRecentReserveAnchorResult;

    public sealed record LimitExceeded(
        string Limit,
        HistoryRecentReserveAnchorMetrics Metrics
    ) : HistoryRecentReserveAnchorResult;

    public sealed record StaleTimelineHead(TimelineHeadRef Actual)
        : HistoryRecentReserveAnchorResult;

    public sealed record RawHeadChanged(
        EventAddress Expected,
        EventAddress? Observed
    ) : HistoryRecentReserveAnchorResult;

    public sealed record Busy : HistoryRecentReserveAnchorResult;

    public sealed record UnsupportedSchema(int SchemaVersion)
        : HistoryRecentReserveAnchorResult;

    public sealed record Invalid(string Code, string Detail)
        : HistoryRecentReserveAnchorResult;
}

internal sealed record HistoryRecentReserveAnchorReadLimits(
    int MaximumRawEvents,
    int MaximumTimelineRows
) {
    internal static HistoryRecentReserveAnchorReadLimits Production { get; }
        = new(
            HistoryRecentReserveOperationLimits.MaximumRawEvents,
            HistoryRecentReserveAnchorLimits.MaximumTimelineRows);

    internal void Validate() {
        if (MaximumRawEvents is < 1
            or > HistoryRecentReserveOperationLimits.MaximumRawEvents) {
            throw new ArgumentOutOfRangeException(nameof(MaximumRawEvents));
        }
        if (MaximumTimelineRows is < 1
            or > HistoryRecentReserveAnchorLimits.MaximumTimelineRows) {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumTimelineRows));
        }
    }
}

internal static class HistoryRecentReserveAnchorFinder {
    internal static HistoryRecentReserveAnchorResult Find(
        HistoryTimelineCoordinator coordinator,
        HistoryTimelineReader reader,
        SJ.SessionJournalReadView selectedRef,
        TimelineHeadRef expectedWholeHead,
        EventAddress completionBoundary,
        HistoryRecentReserveRequirement requirement,
        HistoryRecentReserveAnchorReadLimits limits,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(selectedRef);
        ArgumentNullException.ThrowIfNull(expectedWholeHead);
        ArgumentNullException.ThrowIfNull(requirement);
        if (completionBoundary == default) {
            return Invalid(
                "RecentReserveCompletionBoundaryInvalid",
                "The completion boundary cannot be default.");
        }
        try {
            cancellationToken.ThrowIfCancellationRequested();
            HistoryTimelineSnapshotResult snapshot = reader.ReadSnapshot();
            if (snapshot is not HistoryTimelineSnapshotResult.Available
                    available) {
                return MapSnapshot(snapshot);
            }
            if (available.Head != expectedWholeHead) {
                return new HistoryRecentReserveAnchorResult
                    .StaleTimelineHead(available.Head);
            }
            HistoryTimelineStoreReadResult<PartitionPolicyRevision>
                policyRead = coordinator.ReadPolicyForOffline(
                    expectedWholeHead.ActivePartitionPolicyDigest);
            if (policyRead is not HistoryTimelineStoreReadResult<
                    PartitionPolicyRevision>.Found policyFound) {
                return MapPolicy(policyRead);
            }
            PartitionPolicyRevision policy = policyFound.Value;
            if (!string.Equals(
                    requirement.ExpectedPartitionPolicyDigest,
                    expectedWholeHead.ActivePartitionPolicyDigest,
                    StringComparison.Ordinal)
                || !string.Equals(
                    requirement.ExpectedPartitionPolicyDigest,
                    policy.PolicyDigest,
                    StringComparison.Ordinal)
                || !string.Equals(
                    requirement.ExpectedEstimatorId,
                    policy.HistoryLoadEstimatorId,
                    StringComparison.Ordinal)) {
                return Invalid(
                    "RecentReservePolicyMismatch",
                    "The recent-reserve requirement differs from the exact active Timeline policy or estimator.");
            }
            IHistoryUnitLoadEstimator? estimator =
                coordinator.ResolveEstimatorForOffline(
                    policy.HistoryLoadEstimatorId);
            if (estimator is null) {
                return Invalid(
                    "HistoryLoadEstimatorUnavailable",
                    $"No estimator is registered for '{policy.HistoryLoadEstimatorId}'.");
            }
            EventAddress? observedBefore = selectedRef.ReadCurrentHead();
            if (observedBefore != completionBoundary) {
                return new HistoryRecentReserveAnchorResult.RawHeadChanged(
                    completionBoundary,
                    observedBefore);
            }
            if (expectedWholeHead.HeadRowId is null) {
                return FinishStable(
                    reader,
                    selectedRef,
                    expectedWholeHead,
                    completionBoundary,
                    new HistoryRecentReserveAnchorResult.NoRows(
                        Metrics(0, 0, 0, 0)));
            }

            int examinedRows = 0;
            int examinedRawEvents = 0;
            int examinedUnits = 0;
            int examinedBytes = 0;
            long retained = 0;
            var crossed = new List<HistoryTimelineSelectedRow>();
            HistoryTimelinePathCursor? cursor = null;
            HistoryTimelineSelectedRow? newer = null;
            while (true) {
                HistoryTimelinePathPageResult pageRead =
                    reader.ReadSelectedPathPage(
                        expectedWholeHead,
                        cursor,
                        HistoryTimelineStoreLimits.MaximumPathPageRows);
                if (pageRead is not HistoryTimelinePathPageResult.Page page) {
                    return MapPath(pageRead);
                }
                foreach (HistoryTimelineSelectedRow row in page.Value.Rows) {
                    cancellationToken.ThrowIfCancellationRequested();
                    examinedRows = checked(examinedRows + 1);
                    if (examinedRows > limits.MaximumTimelineRows) {
                        return new HistoryRecentReserveAnchorResult
                            .LimitExceeded(
                                nameof(HistoryRecentReserveAnchorLimits
                                    .MaximumTimelineRows),
                                Metrics(examinedRows - 1,
                                    examinedRawEvents,
                                    examinedUnits,
                                    examinedBytes));
                    }
                    crossed.Add(row);
                    SJ.SessionHistoryPlanningWindow window;
                    if (newer is null) {
                        HistoryRecentReserveAnchorResult? readFailure =
                            ReadWindow(
                                selectedRef,
                                completionBoundary,
                                row.Descriptor.EndInclusive,
                                row.Descriptor.EndSetups,
                                limits.MaximumRawEvents,
                                ref examinedRawEvents,
                                out window,
                                cancellationToken);
                        if (readFailure is not null) {
                            return readFailure;
                        }
                    }
                    else {
                        if (newer.Descriptor.PreviousRowId
                                != row.Descriptor.RowId
                            || newer.Descriptor.StartExclusive
                                != row.Descriptor.EndInclusive
                            || newer.Descriptor.StartSetups
                                != row.Descriptor.EndSetups) {
                            return Invalid(
                                "RecentReserveTimelineChainInvalid",
                                "The selected Timeline predecessor chain is not exact.");
                        }
                        int remainingRaw = limits.MaximumRawEvents
                            - examinedRawEvents;
                        if (newer.Descriptor.RawEventCount
                            > remainingRaw) {
                            return new HistoryRecentReserveAnchorResult
                                .LimitExceeded(
                                    nameof(HistoryRecentReserveOperationLimits
                                        .MaximumRawEvents),
                                    Metrics(examinedRows,
                                        examinedRawEvents,
                                        examinedUnits,
                                        examinedBytes));
                        }
                        HistoryRecentReserveAnchorResult? readFailure =
                            ReadWindow(
                                selectedRef,
                                newer.Descriptor.EndInclusive,
                                newer.Descriptor.StartExclusive,
                                newer.Descriptor.StartSetups,
                                newer.Descriptor.RawEventCount,
                                ref examinedRawEvents,
                                out window,
                                cancellationToken);
                        if (readFailure is not null) {
                            return readFailure;
                        }
                        if (window.RawAddresses.Count
                                != newer.Descriptor.RawEventCount
                            || !string.Equals(
                                window.RawRangeSha256,
                                newer.Descriptor.RawRangeSha256,
                                StringComparison.Ordinal)
                            || window.EndSetups
                                != newer.Descriptor.EndSetups) {
                            return Invalid(
                                "RecentReserveTimelineRangeMismatch",
                                "A selected Timeline row differs from exact raw rematerialization.");
                        }
                    }
                    examinedUnits = checked(
                        examinedUnits + window.Units.Count);
                    long remainingLoad = requirement
                        .MinimumRecentHistoryLoad.Value - retained;
                    HistoryLoadThresholdProjection projection =
                        HistoryLoadProjector.MeasureAtLeast(
                            window,
                            window.StartExclusive,
                            estimator,
                            new HistoryLoadUnit(remainingLoad));
                    examinedBytes = checked(
                        examinedBytes + projection.RenderedUtf8Bytes);
                    if (projection.Reached) {
                        retained = requirement
                            .MinimumRecentHistoryLoad.Value;
                        return FinishStable(
                            reader,
                            selectedRef,
                            expectedWholeHead,
                            completionBoundary,
                            new HistoryRecentReserveAnchorResult.Eligible(
                                row,
                                Array.AsReadOnly(crossed.ToArray()),
                                new HistoryLoadUnit(retained),
                                Metrics(examinedRows,
                                    examinedRawEvents,
                                    examinedUnits,
                                    examinedBytes)));
                    }
                    retained = checked(retained + projection.Growth.Value);
                    newer = row;
                }
                if (page.Value.Next is null) {
                    return FinishStable(
                        reader,
                        selectedRef,
                        expectedWholeHead,
                        completionBoundary,
                        new HistoryRecentReserveAnchorResult
                            .ReserveBootstrapRequired(
                                Array.AsReadOnly(crossed.ToArray()),
                                new HistoryLoadUnit(retained),
                                Metrics(examinedRows,
                                    examinedRawEvents,
                                    examinedUnits,
                                    examinedBytes)));
                }
                cursor = page.Value.Next;
            }
        }
        catch (OperationCanceledException) {
            throw;
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or InvalidDataException
                or InvalidOperationException
                or IOException
                or OverflowException
                or HistoryLoadMeasurementException) {
            return Invalid(
                exception is HistoryLoadMeasurementException measurement
                    ? measurement.Code
                    : "RecentReserveAuthorityInvalid",
                exception.Message);
        }
    }

    private static HistoryRecentReserveAnchorResult? ReadWindow(
        SJ.SessionJournalReadView selectedRef,
        EventAddress endInclusive,
        EventAddress startExclusive,
        SJ.SessionContextAnchorSetupReferences expectedStartSetups,
        int maximumRawEvents,
        ref int examinedRawEvents,
        out SJ.SessionHistoryPlanningWindow window,
        CancellationToken cancellationToken
    ) {
        window = null!;
        if (maximumRawEvents < 0) {
            return new HistoryRecentReserveAnchorResult.LimitExceeded(
                nameof(HistoryRecentReserveOperationLimits.MaximumRawEvents),
                Metrics(0, examinedRawEvents, 0, 0));
        }
        SJ.SessionHistoryPlanningWindowReadResult read = selectedRef
            .ReadHistoryPlanningWindowAtBounded(
                endInclusive,
                startExclusive,
                maximumRawEvents,
                cancellationToken);
        if (read is SJ.SessionHistoryPlanningWindowReadResult.BeyondPrefix) {
            return new HistoryRecentReserveAnchorResult.LimitExceeded(
                nameof(HistoryRecentReserveOperationLimits.MaximumRawEvents),
                Metrics(0, examinedRawEvents, 0, 0));
        }
        window = ((SJ.SessionHistoryPlanningWindowReadResult.Available)read)
            .Window;
        if (window.ObservedRawHead != endInclusive
            || window.StartExclusive != startExclusive
            || window.StartSetups != expectedStartSetups) {
            return Invalid(
                "RecentReserveRawWindowMismatch",
                "The exact raw planning window differs from the selected Timeline boundary.");
        }
        examinedRawEvents = checked(
            examinedRawEvents + window.RawAddresses.Count);
        return null;
    }

    private static HistoryRecentReserveAnchorResult FinishStable(
        HistoryTimelineReader reader,
        SJ.SessionJournalReadView selectedRef,
        TimelineHeadRef expectedWholeHead,
        EventAddress completionBoundary,
        HistoryRecentReserveAnchorResult result
    ) {
        EventAddress? rawAfter = selectedRef.ReadCurrentHead();
        if (rawAfter != completionBoundary) {
            return new HistoryRecentReserveAnchorResult.RawHeadChanged(
                completionBoundary,
                rawAfter);
        }
        HistoryTimelineSnapshotResult after = reader.ReadSnapshot();
        return after switch {
            HistoryTimelineSnapshotResult.Available available
                when available.Head == expectedWholeHead => result,
            HistoryTimelineSnapshotResult.Available available
                => new HistoryRecentReserveAnchorResult
                    .StaleTimelineHead(available.Head),
            _ => MapSnapshot(after)
        };
    }

    private static HistoryRecentReserveAnchorMetrics Metrics(
        int rows,
        int rawEvents,
        int units,
        int bytes
    ) => new(rows, rawEvents, units, bytes);

    private static HistoryRecentReserveAnchorResult MapSnapshot(
        HistoryTimelineSnapshotResult result
    ) => result switch {
        HistoryTimelineSnapshotResult.Busy
            => new HistoryRecentReserveAnchorResult.Busy(),
        HistoryTimelineSnapshotResult.UnsupportedSchema schema
            => new HistoryRecentReserveAnchorResult.UnsupportedSchema(
                schema.SchemaVersion),
        HistoryTimelineSnapshotResult.Invalid invalid
            => Invalid(invalid.Code, invalid.Detail),
        _ => Invalid(
            "RecentReserveTimelineSnapshotInvalid",
            "HistoryTimeline returned an unknown snapshot outcome.")
    };

    private static HistoryRecentReserveAnchorResult MapPolicy(
        HistoryTimelineStoreReadResult<PartitionPolicyRevision> result
    ) => result switch {
        HistoryTimelineStoreReadResult<PartitionPolicyRevision>.Busy
            => new HistoryRecentReserveAnchorResult.Busy(),
        HistoryTimelineStoreReadResult<PartitionPolicyRevision>
            .UnsupportedSchema schema
            => new HistoryRecentReserveAnchorResult.UnsupportedSchema(
                schema.SchemaVersion),
        HistoryTimelineStoreReadResult<PartitionPolicyRevision>.Invalid invalid
            => Invalid(invalid.Code, invalid.Detail),
        _ => Invalid(
            "PartitionPolicyUnavailable",
            "The exact active Timeline partition policy is unavailable.")
    };

    private static HistoryRecentReserveAnchorResult MapPath(
        HistoryTimelinePathPageResult result
    ) => result switch {
        HistoryTimelinePathPageResult.StaleTimelineHead stale
            => new HistoryRecentReserveAnchorResult
                .StaleTimelineHead(stale.Actual),
        HistoryTimelinePathPageResult.Busy
            => new HistoryRecentReserveAnchorResult.Busy(),
        HistoryTimelinePathPageResult.Invalid invalid
            => Invalid(invalid.Code, invalid.Detail),
        _ => Invalid(
            "RecentReserveTimelinePathInvalid",
            "HistoryTimeline returned an unknown path outcome.")
    };

    private static HistoryRecentReserveAnchorResult.Invalid Invalid(
        string code,
        string detail
    ) => new(code, detail);
}
