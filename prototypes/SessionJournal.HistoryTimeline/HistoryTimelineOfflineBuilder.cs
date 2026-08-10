using Atelia.EventJournal;
using SJ = Atelia.SessionJournal;

namespace Atelia.SessionJournal.HistoryTimeline;

/// <summary>
/// Sequential offline row builder over caller-supplied audited cursor
/// authority. It never begins an audit or opens a forward cursor itself.
/// </summary>
public sealed class HistoryTimelineOfflineBuilder {
    private readonly HistoryTimelineCoordinator _coordinator;
    private readonly SJ.SessionSelectedLineageForwardCursor _cursor;
    private SJ.SessionSelectedLineageForwardRange? _pending;
    private bool _terminal;

    internal HistoryTimelineOfflineBuilder(
        HistoryTimelineCoordinator coordinator,
        SJ.SessionSelectedLineageForwardCursor cursor
    ) {
        _coordinator = coordinator;
        _cursor = cursor;
    }

    public HistoryTimelineOfflineStepResult BuildNextRow(
        TimelineHeadRef expectedWholeHead,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(expectedWholeHead);
        if (_terminal) {
            return new HistoryTimelineOfflineStepResult.Invalid(
                "OfflineBuilderTerminal",
                "The offline builder must be reopened after a terminal or failed step."
            );
        }
        TimelineHeadRef actual = _coordinator.ReadSnapshot();
        if (actual != expectedWholeHead) {
            return Fail(new HistoryTimelineOfflineStepResult
                .StaleTimelineHead(actual));
        }
        EventAddress capturedHead =
            _cursor.Authority.Capture.CapturedHead;
        EventAddress? observedBefore = _cursor.ReadCurrentHead();
        if (observedBefore != capturedHead) {
            return Fail(new HistoryTimelineOfflineStepResult
                .RawHeadChanged(capturedHead, observedBefore));
        }
        PartitionPolicyRevision? policy =
            _coordinator.ReadPolicyForOffline(
                expectedWholeHead.ActivePartitionPolicyDigest
            );
        if (policy is null) {
            return Fail(new HistoryTimelineOfflineStepResult
                .PartitionPolicyUnavailable(
                    expectedWholeHead.ActivePartitionPolicyDigest
                ));
        }
        if (!HistoryPartitionAlgorithms.IsSupported(
                policy.PartitionAlgorithmId)) {
            return Fail(new HistoryTimelineOfflineStepResult
                .PartitionAlgorithmUnavailable(
                    policy.PartitionAlgorithmId
                ));
        }
        IHistoryUnitLoadEstimator? estimator =
            _coordinator.ResolveEstimatorForOffline(
                policy.HistoryLoadEstimatorId
            );
        if (estimator is null) {
            return Fail(new HistoryTimelineOfflineStepResult
                .HistoryLoadEstimatorUnavailable(
                    policy.HistoryLoadEstimatorId
                ));
        }
        HistorySegmentDescriptor? predecessor = null;
        if (expectedWholeHead.HeadRowId is { } previousRowId) {
            predecessor = _coordinator.ReadRowForOffline(
                previousRowId
            );
            if (predecessor is null) {
                return Fail(new HistoryTimelineOfflineStepResult
                    .Invalid(
                        "OfflinePredecessorUnavailable",
                        "The exact selected Timeline predecessor is unavailable."
                    ));
            }
        }
        EventAddress expectedBoundary = predecessor?.EndInclusive
            ?? _cursor.Authority.BootstrapSeed.Address;
        SJ.SessionContextAnchorSetupReferences expectedSetups =
            predecessor?.EndSetups
            ?? _cursor.Authority.BootstrapSeed.Setups;
        if (_cursor.CurrentBoundary != expectedBoundary
            || _cursor.CurrentSetups != expectedSetups) {
            return Fail(new HistoryTimelineOfflineStepResult.Invalid(
                "OfflineCursorBoundaryMismatch",
                "The forward cursor no longer matches the exact expected Timeline boundary."
            ));
        }

        try {
            SJ.SessionSelectedLineageForwardRange? range = _pending
                ?? _cursor.ReadNextRange(
                    policy.MaxRawEvents,
                    cancellationToken
                );
            if (range is null) {
                return FinishTerminal(
                    expectedWholeHead,
                    new HistoryTimelineOfflineStepResult.NotEnough(
                        new HistoryPartitionResult.NotEnough(
                            new HistoryLoadUnit(0),
                            rawEventCount: 0,
                            completedUnitCount: 0,
                            measuredRenderedUtf8Bytes: 0
                        )
                    )
                );
            }
            if (range.Entries.Count > policy.MaxRawEvents) {
                return Fail(new HistoryTimelineOfflineStepResult
                    .Invalid(
                        "OfflinePolicyRangeCapIncompatible",
                        "The retained cursor suffix exceeds the newly active policy raw cap."
                    ));
            }
            if (!range.IsFinal
                && range.Entries.Count < policy.MaxRawEvents) {
                range = _cursor.ExtendPendingRange(
                    range,
                    policy.MaxRawEvents,
                    cancellationToken
                );
            }
            _pending = range;
            SJ.SessionHistoryPlanningWindow outer = _cursor.Preview(
                range,
                cancellationToken
            );
            HistoryPartitionResult partition =
                HistoryTimelineOnlineRawPort.PartitionStartWindow(
                    outer,
                    policy,
                    estimator
                );
            if (partition
                is HistoryPartitionResult.NotEnough notEnough) {
                return FinishTerminal(
                    expectedWholeHead,
                    new HistoryTimelineOfflineStepResult.NotEnough(
                        notEnough
                    )
                );
            }
            if (partition
                is HistoryPartitionResult.LimitExceeded limit) {
                return FinishTerminal(
                    expectedWholeHead,
                    new HistoryTimelineOfflineStepResult
                        .LimitExceeded(limit)
                );
            }

            HistoryPartitionPoint point =
                ((HistoryPartitionResult.Selected)partition).Point;
            SJ.SessionSelectedLineageForwardConsumption consumed =
                _cursor.ConsumePreviewedPrefix(
                    range,
                    point.EndInclusive,
                    cancellationToken
                );
            _pending = consumed.RemainingRange;
            SJ.SessionHistoryPlanningWindow exact = consumed.Window;
            HistoryPartitionResult exactPartition =
                HistoryTimelineOnlineRawPort.PartitionStartWindow(
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
                return Fail(new HistoryTimelineOfflineStepResult.Invalid(
                    "OfflineExactRematerializationMismatch",
                    "The consumed exact cursor prefix differs from its partition point."
                ));
            }

            var bound = new BoundHistorySegmentRange(
                expectedWholeHead.RefId,
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
                expectedWholeHead,
                capturedHead,
                descriptor
            );
            var candidate = new HistoryRowCommitCandidate(
                proposal,
                new OfflineSelectedRawCursorFence(_cursor)
            );
            HistoryTimelineCommitResult commit =
                _coordinator.CommitRow(candidate);
            return commit switch {
                HistoryTimelineCommitResult.Committed committed
                    => new HistoryTimelineOfflineStepResult.Committed(
                        descriptor,
                        committed.Head
                    ),
                HistoryTimelineCommitResult.StaleTimelineHead stale
                    => Fail(new HistoryTimelineOfflineStepResult
                        .StaleTimelineHead(stale.Actual)),
                HistoryTimelineCommitResult.RawHeadChanged changed
                    => Fail(new HistoryTimelineOfflineStepResult
                        .RawHeadChanged(
                            changed.Expected,
                            changed.Observed
                        )),
                HistoryTimelineCommitResult
                    .PartitionPolicyUnavailable unavailable
                    => Fail(new HistoryTimelineOfflineStepResult
                        .PartitionPolicyUnavailable(
                            unavailable.PolicyDigest
                        )),
                HistoryTimelineCommitResult.Invalid invalid
                    => Fail(new HistoryTimelineOfflineStepResult.Invalid(
                        invalid.Code,
                        invalid.Detail
                    )),
                _ => Fail(new HistoryTimelineOfflineStepResult.Invalid(
                    "OfflineCommitOutcomeInvalid",
                    "The ledger returned an unknown offline commit outcome."
                ))
            };
        }
        catch (SJ.SessionSelectedLineageAuditChangedException changed) {
            return Fail(new HistoryTimelineOfflineStepResult
                .RawHeadChanged(
                    changed.ExpectedHead,
                    changed.ObservedHead
                ));
        }
        catch (Exception exception) when (
            exception is InvalidDataException
                or IOException
                or HistoryLoadMeasurementException) {
            return Fail(new HistoryTimelineOfflineStepResult.Invalid(
                exception is HistoryLoadMeasurementException measured
                    ? measured.Code
                    : exception is IOException
                        ? "OfflineRawIoInvalid"
                        : "OfflineRawEvidenceInvalid",
                exception.Message
            ));
        }
    }

    private HistoryTimelineOfflineStepResult FinishTerminal(
        TimelineHeadRef expectedWholeHead,
        HistoryTimelineOfflineStepResult result
    ) {
        EventAddress capturedHead =
            _cursor.Authority.Capture.CapturedHead;
        EventAddress? observed = _cursor.ReadCurrentHead();
        if (observed != capturedHead) {
            return Fail(new HistoryTimelineOfflineStepResult
                .RawHeadChanged(capturedHead, observed));
        }
        TimelineHeadRef actual = _coordinator.ReadSnapshot();
        if (actual != expectedWholeHead) {
            return Fail(new HistoryTimelineOfflineStepResult
                .StaleTimelineHead(actual));
        }
        _terminal = true;
        return result;
    }

    private HistoryTimelineOfflineStepResult Fail(
        HistoryTimelineOfflineStepResult result
    ) {
        _terminal = true;
        return result;
    }

}
