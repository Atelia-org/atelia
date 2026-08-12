using Atelia.EventJournal;
using SJ = Atelia.SessionJournal;

namespace Atelia.SessionJournal.HistoryTimeline;

internal static class HistoryTimelineOnlineRawPort {
    public static OnlineSelectedRawCaptureResult Capture(
        SJ.SessionJournalReadView readView,
        TimelineHeadRef expectedTimelineHead,
        int maxRawEventCount,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(readView);
        ArgumentNullException.ThrowIfNull(expectedTimelineHead);
        if (maxRawEventCount is < 1
            or > HistoryRecentReserveOperationLimits.MaximumRawEvents) {
            throw new ArgumentOutOfRangeException(
                nameof(maxRawEventCount)
            );
        }
        try {
            RefId refId = readView.BranchRefId;
            EventAddress? capturedHead = readView.ReadCurrentHead();
            if (capturedHead is null) {
                return new OnlineSelectedRawCaptureResult.Empty(
                    refId,
                    new EmptySelectedRawFence(readView, refId),
                    expectedTimelineHead
                );
            }
            SJ.SessionCurrentLineagePrefix prefix =
                readView.ReadLineagePrefixAt(
                    capturedHead.Value,
                    checked(maxRawEventCount + 1),
                    cancellationToken
                );
            return new OnlineSelectedRawCaptureResult.Captured(
                new OnlineSelectedRawCapture(
                    readView,
                    refId,
                    capturedHead.Value,
                    prefix,
                    expectedTimelineHead
                )
            );
        }
        catch (Exception exception) when (IsRawDataFailure(exception)) {
            return InvalidCapture(exception);
        }
    }

    public static HistoryTimelinePlanResult PlanNextRow(
        OnlineSelectedRawCapture capture,
        TimelineHeadRef expectedHead,
        PartitionPolicyRevision policy,
        HistoryRecentReservePolicy reservePolicy,
        IHistoryUnitLoadEstimator estimator,
        HistorySegmentDescriptor? predecessor,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(expectedHead);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(reservePolicy);
        ArgumentNullException.ThrowIfNull(estimator);
        if (capture.RefId != expectedHead.RefId
            || capture.ExpectedTimelineHead != expectedHead
            || policy.TimelineId != expectedHead.TimelineId) {
            return Invalid(
                "RawScopeMismatch",
                "The captured raw Ref and Timeline head scope differ."
            );
        }
        if (!string.Equals(
                expectedHead.ActivePartitionPolicyDigest,
                policy.PolicyDigest,
                StringComparison.Ordinal)) {
            return new HistoryTimelinePlanResult
                .PartitionPolicyUnavailable(
                    expectedHead.ActivePartitionPolicyDigest
                );
        }
        if (!reservePolicy.IsExactFor(expectedHead, policy)) {
            return Invalid(
                "RecentReservePolicyMismatch",
                "The recent-reserve policy does not bind the exact Ref, active partition policy, and estimator."
            );
        }
        if ((expectedHead.HeadRowId is null) != (predecessor is null)
            || predecessor is not null
                && (predecessor.RowId != expectedHead.HeadRowId
                    || predecessor.TimelineId != expectedHead.TimelineId
                    || predecessor.RefId != expectedHead.RefId)) {
            return Invalid(
                "PredecessorMismatch",
                "The predecessor does not match the exact expected Timeline head."
            );
        }

        EventAddress? observedBefore = capture.ReadView.ReadCurrentHead();
        if (observedBefore != capture.CapturedHead) {
            return new HistoryTimelinePlanResult.RawHeadChanged(
                capture.CapturedHead,
                observedBefore
            );
        }

        try {
            string estimatorId =
                HistoryLoadMeasurementEngine.RequireEstimatorId(
                    estimator
                );
            if (!string.Equals(
                    estimatorId,
                    policy.HistoryLoadEstimatorId,
                    StringComparison.Ordinal)) {
                return new HistoryTimelinePlanResult
                    .HistoryLoadEstimatorUnavailable(
                        policy.HistoryLoadEstimatorId
                    );
            }
            SJ.SessionHistoryPlanningSeed seed;
            if (predecessor is null) {
                SJ.SessionCreatedPlanningSeedReadResult seedRead =
                    capture.ReadView
                        .ReadSessionCreatedPlanningSeedAtBounded(
                            capture.CapturedHead,
                            policy.MaxRawEvents,
                            cancellationToken
                        );
                if (seedRead
                    is SJ.SessionCreatedPlanningSeedReadResult
                        .BeyondPrefix seedBeyond) {
                    return new HistoryTimelinePlanResult
                        .OfflineBootstrapRequired(
                            seedBeyond.ContinuationEvidence
                        );
                }
                seed = ((SJ.SessionCreatedPlanningSeedReadResult
                    .Available)seedRead).Seed;
            }
            else {
                SJ.SessionCurrentLineageAnchorLookup lookup =
                    capture.Prefix.Lookup(
                        predecessor.EndInclusive
                    );
                switch (lookup) {
                    case SJ.SessionCurrentLineageAnchorLookup
                        .OffLineage offLineage:
                        return new HistoryTimelinePlanResult.OffLineage(
                            offLineage.RequiredAnchor,
                            offLineage.CapturedHead
                        );
                    case SJ.SessionCurrentLineageAnchorLookup
                        .BeyondPrefix beyond:
                        return new HistoryTimelinePlanResult
                            .OfflineBootstrapRequired(beyond.Evidence);
                }
                seed = capture.ReadView.CreateHistoryPlanningSeed(
                    predecessor.EndInclusive,
                    predecessor.EndSetups,
                    cancellationToken
                );
            }

            SJ.SessionHistoryPlanningWindowReadResult outerRead =
                capture.ReadView.ReadHistoryPlanningWindowAtBounded(
                    capture.CapturedHead,
                    seed,
                    policy.MaxRawEvents,
                    cancellationToken
                );
            if (outerRead
                is SJ.SessionHistoryPlanningWindowReadResult
                    .BeyondPrefix outerBeyond) {
                return new HistoryTimelinePlanResult
                    .OfflineBootstrapRequired(outerBeyond.Evidence);
            }
            SJ.SessionHistoryPlanningWindow outer =
                ((SJ.SessionHistoryPlanningWindowReadResult
                    .Available)outerRead).Window;
            HistoryPartitionResult partition = PartitionStartWindow(
                outer,
                policy,
                estimator
            );
            if (partition is HistoryPartitionResult.NotEnough notEnough) {
                return FinishStable(
                    capture,
                    new HistoryTimelinePlanResult.NotEnough(notEnough)
                );
            }
            if (partition
                is HistoryPartitionResult.LimitExceeded limit) {
                return FinishStable(
                    capture,
                    new HistoryTimelinePlanResult.LimitExceeded(limit)
                );
            }

            HistoryPartitionPoint point =
                ((HistoryPartitionResult.Selected)partition).Point;
            HistoryLoadThresholdProjection retainedProjection =
                !reservePolicy.IsRequired
                ? new HistoryLoadThresholdProjection(
                    policy.HistoryLoadEstimatorId,
                    new HistoryLoadUnit(0),
                    RenderedUtf8Bytes: 0,
                    Reached: true)
                : HistoryLoadProjector.MeasureAtLeast(
                    outer,
                    point.EndInclusive,
                    estimator,
                    reservePolicy.MinimumRecentHistoryLoad);
            if (!retainedProjection.Reached) {
                return FinishStable(
                    capture,
                    new HistoryTimelinePlanResult
                        .RecentReserveNotReached(
                            new HistoryRecentReserveShortfall(
                                point.MeasuredHistoryLoad,
                                retainedProjection.Growth,
                                reservePolicy.MinimumRecentHistoryLoad)));
            }
            SJ.SessionHistoryPlanningWindowReadResult exactRead =
                capture.ReadView.ReadHistoryPlanningWindowAtBounded(
                    point.EndInclusive,
                    seed,
                    point.RawEventCount,
                    cancellationToken
                );
            if (exactRead
                is not SJ.SessionHistoryPlanningWindowReadResult
                    .Available exactAvailable) {
                return Invalid(
                    "ExactRematerializationUnavailable",
                    "The selected raw range no longer fits its exact event count."
                );
            }
            SJ.SessionHistoryPlanningWindow exact = exactAvailable.Window;
            HistoryPartitionResult exactPartition = PartitionStartWindow(
                exact,
                policy,
                estimator
            );
            if (exactPartition
                    is not HistoryPartitionResult.Selected exactSelected
                || exactSelected.Point != point
                || exact.ObservedRawHead != point.EndInclusive
                || exact.StartExclusive != point.StartExclusive
                || exact.StartSetups != point.StartSetups
                || exact.EndSetups != point.EndSetups
                || exact.RawAddresses.Count != point.RawEventCount) {
                return Invalid(
                    "ExactRematerializationMismatch",
                    "The exact selected raw range differs from its partition point."
                );
            }

            var bound = new BoundHistorySegmentRange(
                capture.RefId,
                point.StartExclusive,
                point.EndInclusive,
                point.StartSetups,
                point.EndSetups,
                point.BaselineCompletedUnitCount,
                point.EndCompletedUnitCount,
                point.RawEventCount,
                exact.RawRangeSha256
            );
            HistorySegmentDescriptor descriptor =
                HistorySegmentDescriptorFactory.Create(
                    point,
                    bound,
                    policy,
                    predecessor
                );
            var proposal = new HistoryRowProposal(
                expectedHead,
                capture.CapturedHead,
                descriptor
            );
            HistoryRecentReserveProof reserveProof =
                HistoryRecentReserveProof.Create(
                    reservePolicy,
                    expectedHead,
                    capture.CapturedHead,
                    descriptor,
                    retainedProjection.Growth);
            EventAddress? observedAfter =
                capture.ReadView.ReadCurrentHead();
            if (observedAfter != capture.CapturedHead) {
                return new HistoryTimelinePlanResult.RawHeadChanged(
                    capture.CapturedHead,
                    observedAfter
                );
            }
            return new HistoryTimelinePlanResult.Selected(
                new HistoryRowCommitCandidate(
                    proposal,
                    capture,
                    reserveProof)
            );
        }
        catch (Exception exception) when (IsRawDataFailure(exception)) {
            return Invalid(
                RawFailureCode(exception),
                exception.Message
            );
        }
    }

    internal static HistoryPartitionResult PartitionStartWindow(
        SJ.SessionHistoryPlanningWindow window,
        PartitionPolicyRevision policy,
        IHistoryUnitLoadEstimator estimator
    ) {
        HistoryLoadBaseline baseline =
            HistoryLoadBaselineResolver.Resolve(
                window.StartExclusive,
                window.Units.Count,
                window.ReplaySafeBoundaries,
                window.StartExclusive
            );
        if (baseline.CompletedUnitCount != 0
            || baseline.FirstLaterBoundaryIndex != 0) {
            throw new InvalidDataException(
                "A Timeline start baseline must resolve to zero offsets."
            );
        }
        return HistoryPartitioner.Partition(
            window,
            baseline,
            policy,
            estimator
        );
    }

    internal static HistorySegmentOpenResult OpenSegment(
        OnlineSelectedRawCapture capture,
        TimelineHeadRef expectedTimelineHead,
        HistorySegmentDescriptor selectedPathHead,
        HistorySegmentDescriptor descriptor,
        HistorySegmentDescriptor? predecessor,
        PartitionPolicyRevision creationPolicy,
        IHistoryUnitLoadEstimator estimator,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(expectedTimelineHead);
        ArgumentNullException.ThrowIfNull(selectedPathHead);
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(creationPolicy);
        ArgumentNullException.ThrowIfNull(estimator);
        if (capture.ExpectedTimelineHead != expectedTimelineHead
            || expectedTimelineHead.HeadRowId
                != selectedPathHead.RowId
            || capture.RefId != descriptor.RefId
            || creationPolicy.TimelineId != descriptor.TimelineId
            || !string.Equals(
                creationPolicy.PolicyDigest,
                descriptor.PartitionPolicyDigestAtCreation,
                StringComparison.Ordinal)) {
            return new HistorySegmentOpenResult.Invalid(
                "OpenScopeMismatch",
                "The selected row, raw capture, and creation policy scopes differ."
            );
        }
        if ((descriptor.PreviousRowId is null) != (predecessor is null)
            || predecessor is not null
                && (predecessor.RowId != descriptor.PreviousRowId
                    || predecessor.EndInclusive
                        != descriptor.StartExclusive
                    || predecessor.EndSetups
                        != descriptor.StartSetups)) {
            return new HistorySegmentOpenResult.Invalid(
                "OpenPredecessorMismatch",
                "The selected row predecessor chain does not bind its exact start."
            );
        }

        EventAddress? observedBefore = capture.ReadView.ReadCurrentHead();
        if (observedBefore != capture.CapturedHead) {
            return new HistorySegmentOpenResult.RawHeadChanged(
                capture.CapturedHead,
                observedBefore
            );
        }
        try {
            string estimatorId =
                HistoryLoadMeasurementEngine.RequireEstimatorId(
                    estimator
                );
            if (!string.Equals(
                    estimatorId,
                    descriptor.HistoryLoadEstimatorId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    estimatorId,
                    creationPolicy.HistoryLoadEstimatorId,
                    StringComparison.Ordinal)) {
                return new HistorySegmentOpenResult
                    .HistoryLoadEstimatorUnavailable(
                        descriptor.HistoryLoadEstimatorId
                    );
            }
            switch (capture.Prefix.Lookup(
                    selectedPathHead.EndInclusive)) {
                case SJ.SessionCurrentLineageAnchorLookup
                    .BeyondPrefix beyond:
                    return new HistorySegmentOpenResult
                        .OfflineBootstrapRequired(beyond.Evidence);
                case SJ.SessionCurrentLineageAnchorLookup
                    .OffLineage offLineage:
                    return new HistorySegmentOpenResult.OffLineage(
                        offLineage.RequiredAnchor,
                        offLineage.CapturedHead
                    );
            }

            SJ.SessionHistoryPlanningSeed seed =
                capture.ReadView.CreateHistoryPlanningSeed(
                    descriptor.StartExclusive,
                    descriptor.StartSetups,
                    cancellationToken
                );
            SJ.SessionHistoryPlanningWindowReadResult exactRead =
                capture.ReadView.ReadHistoryPlanningWindowAtBounded(
                    descriptor.EndInclusive,
                    seed,
                    descriptor.RawEventCount,
                    cancellationToken
                );
            if (exactRead
                is not SJ.SessionHistoryPlanningWindowReadResult
                    .Available exactAvailable) {
                return new HistorySegmentOpenResult.Invalid(
                    "OpenExactRangeUnavailable",
                    "The selected row cannot be rematerialized at its exact raw-event count."
                );
            }
            SJ.SessionHistoryPlanningWindow exact =
                exactAvailable.Window;
            HistoryPartitionResult repartition = PartitionStartWindow(
                exact,
                creationPolicy,
                estimator
            );
            if (repartition
                is not HistoryPartitionResult.Selected selected) {
                return new HistorySegmentOpenResult.Invalid(
                    "OpenRepartitionNotSelected",
                    "The exact selected row no longer reaches its creation partition boundary."
                );
            }
            HistoryPartitionPoint point = selected.Point;
            var bound = new BoundHistorySegmentRange(
                descriptor.RefId,
                point.StartExclusive,
                point.EndInclusive,
                point.StartSetups,
                point.EndSetups,
                point.BaselineCompletedUnitCount,
                point.EndCompletedUnitCount,
                point.RawEventCount,
                exact.RawRangeSha256
            );
            HistorySegmentDescriptor rematerialized =
                HistorySegmentDescriptorFactory.Create(
                    point,
                    bound,
                    creationPolicy,
                    predecessor
                );
            if (!rematerialized.ToCanonicalBytes().AsSpan()
                    .SequenceEqual(descriptor.ToCanonicalBytes())) {
                return new HistorySegmentOpenResult.Invalid(
                    "OpenDescriptorMismatch",
                    "The exact raw range does not reproduce every canonical descriptor field."
                );
            }
            EventAddress? observedAfter =
                capture.ReadView.ReadCurrentHead();
            if (observedAfter != capture.CapturedHead) {
                return new HistorySegmentOpenResult.RawHeadChanged(
                    capture.CapturedHead,
                    observedAfter
                );
            }
            return new HistorySegmentOpenResult.Opened(
                new HistorySegmentContent(descriptor, exact)
            );
        }
        catch (Exception exception) when (IsRawDataFailure(exception)) {
            return new HistorySegmentOpenResult.Invalid(
                RawFailureCode(exception),
                exception.Message
            );
        }
    }

    private static HistoryTimelinePlanResult FinishStable(
        OnlineSelectedRawCapture capture,
        HistoryTimelinePlanResult result
    ) {
        EventAddress? observed = capture.ReadView.ReadCurrentHead();
        return observed == capture.CapturedHead
            ? result
            : new HistoryTimelinePlanResult.RawHeadChanged(
                capture.CapturedHead,
                observed
            );
    }

    private static bool IsRawDataFailure(Exception exception)
        => exception is InvalidDataException
            or IOException
            or HistoryLoadMeasurementException;

    private static string RawFailureCode(Exception exception)
        => exception is HistoryLoadMeasurementException measurement
            ? measurement.Code
            : exception is IOException
                ? "RawIoInvalid"
                : "RawEvidenceInvalid";

    private static OnlineSelectedRawCaptureResult.Invalid InvalidCapture(
        Exception exception
    ) => new(RawFailureCode(exception), exception.Message);

    private static HistoryTimelinePlanResult.Invalid Invalid(
        string code,
        string detail
    ) => new(code, detail);
}
