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
    private readonly HistoryRecentReservePolicy _reservePolicy;
    private readonly int _reserveForwardRangeEventCap;
    private readonly int _reserveInitialForwardRangeEventCount;
    private SJ.SessionSelectedLineageForwardRange? _pending;
    private bool _terminal;

    internal HistoryTimelineOfflineBuilder(
        HistoryTimelineCoordinator coordinator,
        SJ.SessionSelectedLineageForwardCursor cursor,
        HistoryRecentReservePolicy reservePolicy,
        int reserveForwardRangeEventCap,
        int reserveInitialForwardRangeEventCount
    ) {
        _coordinator = coordinator;
        _cursor = cursor;
        _reservePolicy = reservePolicy;
        if (reserveForwardRangeEventCap is < 1
            or > SJ.SessionSelectedLineageAuditLimits
                .MaximumForwardRangeEventCount) {
            throw new ArgumentOutOfRangeException(
                nameof(reserveForwardRangeEventCap));
        }
        _reserveForwardRangeEventCap = reserveForwardRangeEventCap;
        if (reserveInitialForwardRangeEventCount is < 1
            || reserveInitialForwardRangeEventCount
                > reserveForwardRangeEventCap) {
            throw new ArgumentOutOfRangeException(
                nameof(reserveInitialForwardRangeEventCount));
        }
        _reserveInitialForwardRangeEventCount =
            reserveInitialForwardRangeEventCount;
    }

    public HistoryTimelineOfflineStepResult BuildNextRow(
        TimelineHeadRef expectedWholeHead,
        CancellationToken cancellationToken = default
    ) => ExecuteNextRow(
        expectedWholeHead,
        commitSelectedRow: true,
        cancellationToken);

    /// <summary>
    /// Performs the same exact raw rematerialization and partition checks as
    /// BuildNextRow but never commits a row. Selected means one more row is
    /// available; NotEnough proves the current cursor is terminal.
    /// </summary>
    public HistoryTimelineOfflineStepResult ProbeNextRow(
        TimelineHeadRef expectedWholeHead,
        CancellationToken cancellationToken = default
    ) => ExecuteNextRow(
        expectedWholeHead,
        commitSelectedRow: false,
        cancellationToken);

    private HistoryTimelineOfflineStepResult ExecuteNextRow(
        TimelineHeadRef expectedWholeHead,
        bool commitSelectedRow,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(expectedWholeHead);
        using HistoryTimelineLifetime.Operation? operation =
            _coordinator.TryEnterOperationForOffline();
        if (operation is null) {
            return Fail(new HistoryTimelineOfflineStepResult.Invalid(
                "HistoryTimelineDisposed",
                "The HistoryTimeline handle has been disposed."
            ));
        }
        if (_terminal) {
            return new HistoryTimelineOfflineStepResult.Invalid(
                "OfflineBuilderTerminal",
                "The offline builder must be reopened after a terminal or failed step."
            );
        }
        HistoryTimelineSnapshotResult snapshot =
            _coordinator.ReadSnapshot();
        if (snapshot is HistoryTimelineSnapshotResult.Busy) {
            return Fail(new HistoryTimelineOfflineStepResult
                .BackendBusy());
        }
        if (snapshot is HistoryTimelineSnapshotResult
                .UnsupportedSchema snapshotSchema) {
            return Fail(new HistoryTimelineOfflineStepResult.Invalid(
                "TimelineStoreUnsupportedSchema",
                HistoryTimelineCoordinator.UnsupportedSchemaDetail(
                    snapshotSchema.SchemaVersion
                )
            ));
        }
        if (snapshot is HistoryTimelineSnapshotResult.Invalid
            snapshotInvalid) {
            return Fail(new HistoryTimelineOfflineStepResult.Invalid(
                snapshotInvalid.Code,
                snapshotInvalid.Detail
            ));
        }
        if (snapshot is not HistoryTimelineSnapshotResult
                .Available available) {
            return Fail(new HistoryTimelineOfflineStepResult.Invalid(
                "TimelineHeadUnavailable",
                "The Timeline ledger has no canonical head."
            ));
        }
        TimelineHeadRef actual = available.Head;
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
        HistoryTimelineStoreReadResult<PartitionPolicyRevision>
            policyRead =
            _coordinator.ReadPolicyForOffline(
                expectedWholeHead.ActivePartitionPolicyDigest
            );
        PartitionPolicyRevision policy;
        switch (policyRead) {
            case HistoryTimelineStoreReadResult<
                    PartitionPolicyRevision>.Found found:
                policy = found.Value;
                break;
            case HistoryTimelineStoreReadResult<
                    PartitionPolicyRevision>.Busy:
                return Fail(new HistoryTimelineOfflineStepResult
                    .BackendBusy());
            case HistoryTimelineStoreReadResult<
                    PartitionPolicyRevision>.Invalid invalid:
                return Fail(new HistoryTimelineOfflineStepResult.Invalid(
                    invalid.Code,
                    invalid.Detail
                ));
            case HistoryTimelineStoreReadResult<
                    PartitionPolicyRevision>.UnsupportedSchema policySchema:
                return Fail(new HistoryTimelineOfflineStepResult.Invalid(
                    "TimelineStoreUnsupportedSchema",
                    HistoryTimelineCoordinator.UnsupportedSchemaDetail(
                        policySchema.SchemaVersion
                    )
                ));
            default:
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
        if (!_reservePolicy.IsExactFor(actual, policy)) {
            return Fail(new HistoryTimelineOfflineStepResult.Invalid(
                "RecentReservePolicyMismatch",
                "The recent-reserve policy does not bind the exact Ref, active partition policy, and estimator."
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
            HistoryTimelineStoreReadResult<HistorySegmentDescriptor>
                predecessorRead = _coordinator.ReadRowForOffline(
                    previousRowId
                );
            switch (predecessorRead) {
                case HistoryTimelineStoreReadResult<
                        HistorySegmentDescriptor>.Found found:
                    predecessor = found.Value;
                    break;
                case HistoryTimelineStoreReadResult<
                        HistorySegmentDescriptor>.Busy:
                    return Fail(new HistoryTimelineOfflineStepResult
                        .BackendBusy());
                case HistoryTimelineStoreReadResult<
                        HistorySegmentDescriptor>.Invalid invalid:
                    return Fail(new HistoryTimelineOfflineStepResult
                        .Invalid(invalid.Code, invalid.Detail));
                case HistoryTimelineStoreReadResult<
                        HistorySegmentDescriptor>.UnsupportedSchema rowSchema:
                    return Fail(new HistoryTimelineOfflineStepResult.Invalid(
                        "TimelineStoreUnsupportedSchema",
                        HistoryTimelineCoordinator.UnsupportedSchemaDetail(
                            rowSchema.SchemaVersion
                        )
                    ));
                default:
                    return Fail(new HistoryTimelineOfflineStepResult.Invalid(
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
            RecentReserveMeasurement reserve = MeasureRecentReserve(
                point,
                policy,
                estimator,
                cancellationToken);
            if (reserve is RecentReserveMeasurement.Unavailable
                reserveUnavailable) {
                return FinishTerminal(
                    expectedWholeHead,
                    new HistoryTimelineOfflineStepResult
                        .RecentReserveProofUnavailable(
                            reserveUnavailable.Code,
                            reserveUnavailable.Detail));
            }
            HistoryLoadUnit retained = ((RecentReserveMeasurement.Measured)
                reserve).Retained;
            if (retained.Value
                < _reservePolicy.MinimumRecentHistoryLoad.Value) {
                return FinishTerminal(
                    expectedWholeHead,
                    new HistoryTimelineOfflineStepResult
                        .RecentReserveNotReached(
                            new HistoryRecentReserveShortfall(
                                point.MeasuredHistoryLoad,
                                retained,
                                _reservePolicy
                                    .MinimumRecentHistoryLoad)));
            }
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
            if (!commitSelectedRow) {
                return FinishTerminal(
                    expectedWholeHead,
                    new HistoryTimelineOfflineStepResult.Selected(
                        descriptor));
            }
            var proposal = new HistoryRowProposal(
                expectedWholeHead,
                capturedHead,
                descriptor
            );
            var candidate = new HistoryRowCommitCandidate(
                proposal,
                new OfflineSelectedRawCursorFence(
                    _coordinator.RepositoryPath,
                    _cursor),
                HistoryRecentReserveProof.Create(
                    _reservePolicy,
                    expectedWholeHead,
                    capturedHead,
                    descriptor,
                    retained)
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
                HistoryTimelineCommitResult.BackendBusy
                    => Fail(new HistoryTimelineOfflineStepResult
                        .BackendBusy()),
                HistoryTimelineCommitResult.LimitExceeded limited
                    => Fail(new HistoryTimelineOfflineStepResult
                        .StoreLimitExceeded(limited.Limit)),
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

    private RecentReserveMeasurement MeasureRecentReserve(
        HistoryPartitionPoint point,
        PartitionPolicyRevision policy,
        IHistoryUnitLoadEstimator estimator,
        CancellationToken cancellationToken
    ) {
        using SJ.SessionSelectedLineageForwardCursor fork =
            _cursor.ForkAtBoundary(
                point.EndInclusive,
                point.EndSetups,
                cancellationToken);
        long retained = 0;
        if (!_reservePolicy.IsRequired) {
            return new RecentReserveMeasurement.Measured(
                new HistoryLoadUnit(0));
        }
        SJ.SessionSelectedLineageForwardRange? range = null;
        int rangeCap = _reserveInitialForwardRangeEventCount;
        while (retained < _reservePolicy.MinimumRecentHistoryLoad.Value) {
            range ??= fork.ReadNextRange(rangeCap, cancellationToken);
            if (range is null) {
                break;
            }
            if (!range.IsFinal && range.Entries.Count < rangeCap) {
                range = fork.ExtendPendingRange(
                    range,
                    rangeCap,
                    cancellationToken);
            }
            SJ.SessionHistoryPlanningWindow window;
            try {
                window = fork.Preview(range, cancellationToken);
            }
            catch (SJ.SessionSelectedLineageOpenDependencyException)
                when (!range.IsFinal
                    && rangeCap < _reserveForwardRangeEventCap) {
                rangeCap = Math.Min(
                    _reserveForwardRangeEventCap,
                    checked(rangeCap * 2));
                range = fork.ExtendPendingRange(
                    range,
                    rangeCap,
                    cancellationToken);
                continue;
            }
            catch (SJ.SessionSelectedLineageOpenDependencyException)
                when (!range.IsFinal) {
                return new RecentReserveMeasurement.Unavailable(
                    "RecentReserveForwardRangeLimitExceeded",
                    "An unresolved tool dependency exceeded the bounded selected-lineage forward range.");
            }
            catch (SJ.SessionSelectedLineageOpenDependencyException) {
                return new RecentReserveMeasurement.Unavailable(
                    "RecentReserveTerminalOpenDependency",
                    "The exact terminal selected-lineage range contains an unresolved tool dependency, so recent reserve cannot be proven.");
            }
            long remaining = checked(
                _reservePolicy.MinimumRecentHistoryLoad.Value - retained);
            HistoryLoadThresholdProjection projection =
                HistoryLoadProjector.MeasureAtLeast(
                    window,
                    window.StartExclusive,
                    estimator,
                    new HistoryLoadUnit(remaining));
            if (projection.Reached) {
                return new RecentReserveMeasurement.Measured(
                    _reservePolicy.MinimumRecentHistoryLoad);
            }
            retained = checked(retained + projection.Growth.Value);

            SJ.SessionHistoryPlanningBoundary? lastSafe = window
                .ReplaySafeBoundaries
                .LastOrDefault(boundary =>
                    boundary.Address != window.StartExclusive);
            if (lastSafe is null) {
                if (!range.IsFinal) {
                    return new RecentReserveMeasurement.Unavailable(
                        "RecentReserveForwardRangeLimitExceeded",
                        "No replay-safe boundary was found within the bounded selected-lineage forward range.");
                }
                break;
            }
            SJ.SessionSelectedLineageForwardConsumption consumed =
                fork.ConsumePreviewedPrefix(
                    range,
                    lastSafe.Address,
                    cancellationToken);
            range = consumed.RemainingRange;
        }
        return new RecentReserveMeasurement.Measured(
            new HistoryLoadUnit(retained));
    }

    private abstract record RecentReserveMeasurement {
        private RecentReserveMeasurement() { }

        internal sealed record Measured(HistoryLoadUnit Retained)
            : RecentReserveMeasurement;

        internal sealed record Unavailable(string Code, string Detail)
            : RecentReserveMeasurement;
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
        HistoryTimelineSnapshotResult snapshot =
            _coordinator.ReadSnapshot();
        if (snapshot is HistoryTimelineSnapshotResult.Busy) {
            return Fail(new HistoryTimelineOfflineStepResult
                .BackendBusy());
        }
        if (snapshot is HistoryTimelineSnapshotResult
                .UnsupportedSchema terminalSchema) {
            return Fail(new HistoryTimelineOfflineStepResult.Invalid(
                "TimelineStoreUnsupportedSchema",
                HistoryTimelineCoordinator.UnsupportedSchemaDetail(
                    terminalSchema.SchemaVersion
                )
            ));
        }
        if (snapshot is HistoryTimelineSnapshotResult.Invalid
            snapshotInvalid) {
            return Fail(new HistoryTimelineOfflineStepResult.Invalid(
                snapshotInvalid.Code,
                snapshotInvalid.Detail
            ));
        }
        if (snapshot is not HistoryTimelineSnapshotResult
                .Available available) {
            return Fail(new HistoryTimelineOfflineStepResult.Invalid(
                "TimelineHeadUnavailable",
                "The Timeline ledger has no canonical head."
            ));
        }
        TimelineHeadRef actual = available.Head;
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
