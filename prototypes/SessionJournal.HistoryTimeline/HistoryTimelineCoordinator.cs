using Atelia.EventJournal;
using SJ = Atelia.SessionJournal;

namespace Atelia.SessionJournal.HistoryTimeline;

/// <summary>
/// Backend-neutral WP-01B operation coordinator. Construction stays at the
/// composition root; callers cannot select the in-memory semantic carrier.
/// </summary>
public sealed class HistoryTimelineCoordinator {
    private const string NoReserveTestEstimatorId =
        "atelia.history-load.test.no-reserve-v1";

    private string _repositoryPath = null!;
    private IHistoryTimelineLedgerPort _ledger = null!;
    private IHistoryTimelineEstimatorResolver _estimators = null!;
    private HistoryTimelineCoordinatorTestHooks _testHooks = null!;
    private HistoryTimelineLifetime _lifetime = null!;

    internal string RepositoryPath => _repositoryPath;

    internal HistoryTimelineCoordinator(
        string repositoryPath,
        IHistoryTimelineLedgerPort ledger,
        params IHistoryUnitLoadEstimator[] estimators
    ) : this(
        repositoryPath,
        ledger,
        new HistoryTimelineCoordinatorTestHooks(),
        new HistoryTimelineLifetime(),
        estimators
    ) { }

    internal HistoryTimelineCoordinator(
        string repositoryPath,
        IHistoryTimelineLedgerPort ledger,
        HistoryTimelineCoordinatorTestHooks testHooks,
        params IHistoryUnitLoadEstimator[] estimators
    ) {
        Initialize(
            repositoryPath,
            ledger,
            testHooks,
            new HistoryTimelineLifetime(),
            estimators
        );
    }

    internal HistoryTimelineCoordinator(
        string repositoryPath,
        IHistoryTimelineLedgerPort ledger,
        HistoryTimelineLifetime lifetime,
        params IHistoryUnitLoadEstimator[] estimators
    ) : this(
        repositoryPath,
        ledger,
        new HistoryTimelineCoordinatorTestHooks(),
        lifetime,
        estimators
    ) { }

    internal HistoryTimelineCoordinator(
        string repositoryPath,
        IHistoryTimelineLedgerPort ledger,
        HistoryTimelineCoordinatorTestHooks testHooks,
        HistoryTimelineLifetime lifetime,
        params IHistoryUnitLoadEstimator[] estimators
    ) {
        Initialize(
            repositoryPath,
            ledger,
            testHooks,
            lifetime,
            estimators
        );
    }

    private void Initialize(
        string repositoryPath,
        IHistoryTimelineLedgerPort ledger,
        HistoryTimelineCoordinatorTestHooks testHooks,
        HistoryTimelineLifetime lifetime,
        IHistoryUnitLoadEstimator[] estimators
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        _repositoryPath = CanonicalRepositoryPath(repositoryPath);
        _ledger = ledger
            ?? throw new ArgumentNullException(nameof(ledger));
        _testHooks = testHooks
            ?? throw new ArgumentNullException(nameof(testHooks));
        _lifetime = lifetime
            ?? throw new ArgumentNullException(nameof(lifetime));
        _estimators = new HistoryTimelineEstimatorRegistry(
            estimators
        );
    }

    public HistoryTimelineSnapshotResult ReadSnapshot() {
        using HistoryTimelineLifetime.Operation? operation =
            _lifetime.TryEnterOperation();
        if (operation is null) {
            return new HistoryTimelineSnapshotResult.Invalid(
                "HistoryTimelineDisposed",
                "The HistoryTimeline handle has been disposed."
            );
        }
        return _ledger.ReadSnapshot() switch {
            HistoryTimelineStoreReadResult<TimelineHeadRef>.Found found
                => new HistoryTimelineSnapshotResult.Available(
                    found.Value
                ),
            HistoryTimelineStoreReadResult<TimelineHeadRef>.Busy
                => new HistoryTimelineSnapshotResult.Busy(),
            HistoryTimelineStoreReadResult<TimelineHeadRef>
                .UnsupportedSchema unsupported
                => new HistoryTimelineSnapshotResult.UnsupportedSchema(
                    unsupported.SchemaVersion
                ),
            HistoryTimelineStoreReadResult<TimelineHeadRef>.Invalid invalid
                => new HistoryTimelineSnapshotResult.Invalid(
                    invalid.Code,
                    invalid.Detail
                ),
            _ => new HistoryTimelineSnapshotResult.Invalid(
                "TimelineHeadUnavailable",
                "The Timeline ledger has no canonical head."
            )
        };
    }

    public HistoryTimelinePolicyPutResult PutPolicy(
        PartitionPolicyRevision policy
    ) {
        using HistoryTimelineLifetime.Operation? operation =
            _lifetime.TryEnterOperation();
        return operation is null
            ? new HistoryTimelinePolicyPutResult.Invalid(
                "HistoryTimelineDisposed",
                "The HistoryTimeline handle has been disposed."
            )
            : _ledger.PutPolicy(policy);
    }

    public HistoryTimelinePolicyCasResult CompareExchangePolicy(
        TimelineHeadRef expectedWholeHead,
        string nextPolicyDigest
    ) {
        using HistoryTimelineLifetime.Operation? operation =
            _lifetime.TryEnterOperation();
        return operation is null
            ? new HistoryTimelinePolicyCasResult.Invalid(
                "HistoryTimelineDisposed",
                "The HistoryTimeline handle has been disposed."
            )
            : _ledger.CompareExchangePolicy(
            expectedWholeHead,
            nextPolicyDigest
        );
    }

    public OnlineSelectedRawCaptureResult CaptureOnline(
        TimelineHeadRef expectedWholeHead,
        SJ.SessionJournalReadView readView,
        CancellationToken cancellationToken = default
    ) => CaptureRaw(
        expectedWholeHead,
        readView,
        _testHooks.OnlineRawCaptureLimit
            ?? HistoryRecentReserveOperationLimits.MaximumRawEvents,
        requireSelectedHeadBoundary: false,
        cancellationToken);

    internal OnlineSelectedRawCaptureResult CaptureBuildRead(
        TimelineHeadRef expectedWholeHead,
        SJ.SessionJournalReadView readView,
        int maximumRawEvents,
        CancellationToken cancellationToken = default
    ) => CaptureRaw(
        expectedWholeHead,
        readView,
        maximumRawEvents,
        requireSelectedHeadBoundary: true,
        cancellationToken);

    private OnlineSelectedRawCaptureResult CaptureRaw(
        TimelineHeadRef expectedWholeHead,
        SJ.SessionJournalReadView readView,
        int maximumRawEvents,
        bool requireSelectedHeadBoundary,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(expectedWholeHead);
        ArgumentNullException.ThrowIfNull(readView);
        if (maximumRawEvents is < 1
            or > HistoryRecentReserveOperationLimits.MaximumRawEvents) {
            throw new ArgumentOutOfRangeException(
                nameof(maximumRawEvents));
        }
        using HistoryTimelineLifetime.Operation? operation =
            _lifetime.TryEnterOperation();
        if (operation is null) {
            return new OnlineSelectedRawCaptureResult.Invalid(
                "HistoryTimelineDisposed",
                "The HistoryTimeline handle has been disposed."
            );
        }
        HistoryTimelineStoreReadResult<TimelineHeadRef> headRead =
            _ledger.ReadSnapshot();
        if (headRead is HistoryTimelineStoreReadResult<
                TimelineHeadRef>.Busy) {
            return new OnlineSelectedRawCaptureResult.BackendBusy();
        }
        if (headRead is HistoryTimelineStoreReadResult<
                TimelineHeadRef>.UnsupportedSchema headSchema) {
            return new OnlineSelectedRawCaptureResult.Invalid(
                "TimelineStoreUnsupportedSchema",
                UnsupportedSchemaDetail(headSchema.SchemaVersion)
            );
        }
        if (headRead is HistoryTimelineStoreReadResult<
                TimelineHeadRef>.Invalid headInvalid) {
            return new OnlineSelectedRawCaptureResult.Invalid(
                headInvalid.Code,
                headInvalid.Detail
            );
        }
        if (headRead is not HistoryTimelineStoreReadResult<
                TimelineHeadRef>.Found headFound) {
            return new OnlineSelectedRawCaptureResult.Invalid(
                "TimelineHeadUnavailable",
                "The Timeline ledger has no canonical head."
            );
        }
        TimelineHeadRef actual = headFound.Value;
        if (actual != expectedWholeHead) {
            return new OnlineSelectedRawCaptureResult
                .StaleTimelineHead(actual);
        }
        HistoryTimelineStoreReadResult<PartitionPolicyRevision>
            policyRead = _ledger.ReadPolicy(
            actual.ActivePartitionPolicyDigest
        );
        if (policyRead is HistoryTimelineStoreReadResult<
                PartitionPolicyRevision>.Busy) {
            return new OnlineSelectedRawCaptureResult.BackendBusy();
        }
        if (policyRead is HistoryTimelineStoreReadResult<
                PartitionPolicyRevision>.UnsupportedSchema policySchema) {
            return new OnlineSelectedRawCaptureResult.Invalid(
                "TimelineStoreUnsupportedSchema",
                UnsupportedSchemaDetail(policySchema.SchemaVersion)
            );
        }
        if (policyRead is HistoryTimelineStoreReadResult<
                PartitionPolicyRevision>.Invalid policyInvalid) {
            return new OnlineSelectedRawCaptureResult.Invalid(
                policyInvalid.Code,
                policyInvalid.Detail
            );
        }
        if (policyRead is not HistoryTimelineStoreReadResult<
                PartitionPolicyRevision>.Found policyFound) {
            return new OnlineSelectedRawCaptureResult
                .PartitionPolicyUnavailable(
                    actual.ActivePartitionPolicyDigest
                );
        }
        PartitionPolicyRevision policy = policyFound.Value;
        string observedRepositoryPath;
        try {
            observedRepositoryPath = CanonicalRepositoryPath(
                readView.Path
            );
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or IOException
                or NotSupportedException) {
            return new OnlineSelectedRawCaptureResult.Invalid(
                "RawRepositoryBindingInvalid",
                exception.Message
            );
        }
        if (!string.Equals(
                observedRepositoryPath,
                _repositoryPath,
                RepositoryPathComparison)) {
            return new OnlineSelectedRawCaptureResult.Invalid(
                "RawRepositoryMismatch",
                "The SessionJournal read view belongs to another canonical repository."
            );
        }
        OnlineSelectedRawCaptureResult result =
            HistoryTimelineOnlineRawPort.Capture(
            readView,
            expectedWholeHead,
            requireSelectedHeadBoundary
                ? maximumRawEvents
                : Math.Min(maximumRawEvents, policy.MaxRawEvents),
            cancellationToken
        );
        RefId capturedRef = result switch {
            OnlineSelectedRawCaptureResult.Empty empty
                => empty.RefId,
            OnlineSelectedRawCaptureResult.Captured captured
                => captured.Capture.RefId,
            _ => actual.RefId
        };
        if (capturedRef != actual.RefId) {
            return new OnlineSelectedRawCaptureResult.Invalid(
                "RawRefMismatch",
                "The SessionJournal read view belongs to another Ref."
            );
        }
        if (requireSelectedHeadBoundary
            && result is OnlineSelectedRawCaptureResult.Captured buildCaptured
            && actual.HeadRowId is { } headRowId) {
            HistoryTimelineStoreReadResult<HistorySegmentDescriptor>
                rowRead = _ledger.ReadRow(headRowId);
            if (rowRead is HistoryTimelineStoreReadResult<
                    HistorySegmentDescriptor>.Busy) {
                return new OnlineSelectedRawCaptureResult.BackendBusy();
            }
            if (rowRead is HistoryTimelineStoreReadResult<
                    HistorySegmentDescriptor>.UnsupportedSchema rowSchema) {
                return new OnlineSelectedRawCaptureResult.Invalid(
                    "TimelineStoreUnsupportedSchema",
                    UnsupportedSchemaDetail(rowSchema.SchemaVersion));
            }
            if (rowRead is HistoryTimelineStoreReadResult<
                    HistorySegmentDescriptor>.Invalid rowInvalid) {
                return new OnlineSelectedRawCaptureResult.Invalid(
                    rowInvalid.Code,
                    rowInvalid.Detail);
            }
            if (rowRead is not HistoryTimelineStoreReadResult<
                    HistorySegmentDescriptor>.Found rowFound) {
                return new OnlineSelectedRawCaptureResult.Invalid(
                    "TimelineHeadRowUnavailable",
                    "The selected Timeline head row is unavailable.");
            }
            SJ.SessionCurrentLineageAnchorLookup lookup =
                buildCaptured.Capture.Prefix.Lookup(
                    rowFound.Value.EndInclusive);
            if (lookup is SJ.SessionCurrentLineageAnchorLookup
                    .BeyondPrefix) {
                result = new OnlineSelectedRawCaptureResult.LimitExceeded(
                    nameof(HistoryRecentReserveOperationLimits
                        .MaximumRawEvents));
            }
            else if (lookup is SJ.SessionCurrentLineageAnchorLookup
                     .OffLineage) {
                return new OnlineSelectedRawCaptureResult.Invalid(
                    "TimelineHeadRawBoundaryOffLineage",
                    "The selected Timeline head raw boundary is not on the current Ref lineage.");
            }
        }
        HistoryTimelineStoreReadResult<TimelineHeadRef> afterRead =
            _ledger.ReadSnapshot();
        if (afterRead is HistoryTimelineStoreReadResult<
                TimelineHeadRef>.Busy) {
            return new OnlineSelectedRawCaptureResult.BackendBusy();
        }
        if (afterRead is HistoryTimelineStoreReadResult<
                TimelineHeadRef>.UnsupportedSchema afterSchema) {
            return new OnlineSelectedRawCaptureResult.Invalid(
                "TimelineStoreUnsupportedSchema",
                UnsupportedSchemaDetail(afterSchema.SchemaVersion)
            );
        }
        if (afterRead is HistoryTimelineStoreReadResult<
                TimelineHeadRef>.Invalid afterInvalid) {
            return new OnlineSelectedRawCaptureResult.Invalid(
                afterInvalid.Code,
                afterInvalid.Detail
            );
        }
        if (afterRead is not HistoryTimelineStoreReadResult<
                TimelineHeadRef>.Found afterFound) {
            return new OnlineSelectedRawCaptureResult.Invalid(
                "TimelineHeadUnavailable",
                "The Timeline ledger has no canonical head."
            );
        }
        TimelineHeadRef after = afterFound.Value;
        return after == expectedWholeHead
            ? result
            : new OnlineSelectedRawCaptureResult
                .StaleTimelineHead(after);
    }

    internal HistoryTimelinePlanResult PlanNextRowForTests(
        TimelineHeadRef expectedWholeHead,
        OnlineSelectedRawCapture capture,
        CancellationToken cancellationToken = default
    ) => PlanNextRow(
        expectedWholeHead,
        capture,
        CreateNoReservePolicyForTests(expectedWholeHead),
        cancellationToken);

    internal HistoryTimelinePlanResult PlanNextRow(
        TimelineHeadRef expectedWholeHead,
        OnlineSelectedRawCapture capture,
        HistoryRecentReservePolicy reservePolicy,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(expectedWholeHead);
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(reservePolicy);
        using HistoryTimelineLifetime.Operation? operation =
            _lifetime.TryEnterOperation();
        if (operation is null) {
            return new HistoryTimelinePlanResult.Invalid(
                "HistoryTimelineDisposed",
                "The HistoryTimeline handle has been disposed."
            );
        }
        if (!string.Equals(
                capture.ReadView.Path is { } capturePath
                    ? CanonicalRepositoryPath(capturePath)
                    : string.Empty,
                _repositoryPath,
                RepositoryPathComparison)) {
            return new HistoryTimelinePlanResult.Invalid(
                "RawRepositoryMismatch",
                "The raw capture belongs to another canonical repository.");
        }
        HistoryTimelineStoreReadResult<TimelineHeadRef> headRead =
            _ledger.ReadSnapshot();
        if (headRead is HistoryTimelineStoreReadResult<
                TimelineHeadRef>.Busy) {
            return new HistoryTimelinePlanResult.BackendBusy();
        }
        if (headRead is HistoryTimelineStoreReadResult<
                TimelineHeadRef>.UnsupportedSchema headSchema) {
            return new HistoryTimelinePlanResult.Invalid(
                "TimelineStoreUnsupportedSchema",
                UnsupportedSchemaDetail(headSchema.SchemaVersion)
            );
        }
        if (headRead is HistoryTimelineStoreReadResult<
                TimelineHeadRef>.Invalid headInvalid) {
            return new HistoryTimelinePlanResult.Invalid(
                headInvalid.Code,
                headInvalid.Detail
            );
        }
        if (headRead is not HistoryTimelineStoreReadResult<
                TimelineHeadRef>.Found headFound) {
            return new HistoryTimelinePlanResult.Invalid(
                "TimelineHeadUnavailable",
                "The Timeline ledger has no canonical head."
            );
        }
        TimelineHeadRef actual = headFound.Value;
        if (actual != expectedWholeHead) {
            return new HistoryTimelinePlanResult.StaleTimelineHead(
                actual
            );
        }
        HistoryTimelineStoreReadResult<PartitionPolicyRevision>
            policyRead = _ledger.ReadPolicy(
            actual.ActivePartitionPolicyDigest
        );
        if (policyRead is HistoryTimelineStoreReadResult<
                PartitionPolicyRevision>.Busy) {
            return new HistoryTimelinePlanResult.BackendBusy();
        }
        if (policyRead is HistoryTimelineStoreReadResult<
                PartitionPolicyRevision>.UnsupportedSchema policySchema) {
            return new HistoryTimelinePlanResult.Invalid(
                "TimelineStoreUnsupportedSchema",
                UnsupportedSchemaDetail(policySchema.SchemaVersion)
            );
        }
        if (policyRead is HistoryTimelineStoreReadResult<
                PartitionPolicyRevision>.Invalid policyInvalid) {
            return new HistoryTimelinePlanResult.Invalid(
                policyInvalid.Code,
                policyInvalid.Detail
            );
        }
        if (policyRead is not HistoryTimelineStoreReadResult<
                PartitionPolicyRevision>.Found policyFound) {
            return new HistoryTimelinePlanResult
                .PartitionPolicyUnavailable(
                    actual.ActivePartitionPolicyDigest
                );
        }
        PartitionPolicyRevision policy = policyFound.Value;
        if (!string.Equals(
                reservePolicy.CanonicalRepositoryPath,
                _repositoryPath,
                RepositoryPathComparison)
            || !reservePolicy.IsExactFor(actual, policy)) {
            return new HistoryTimelinePlanResult.Invalid(
                "RecentReservePolicyMismatch",
                "The recent-reserve policy does not bind the exact Ref, active partition policy, and estimator."
            );
        }
        if (!HistoryPartitionAlgorithms.IsSupported(
                policy.PartitionAlgorithmId)) {
            return new HistoryTimelinePlanResult
                .PartitionAlgorithmUnavailable(
                    policy.PartitionAlgorithmId
                );
        }
        IHistoryUnitLoadEstimator? estimator = _estimators.Resolve(
            policy.HistoryLoadEstimatorId
        );
        if (estimator is null) {
            return new HistoryTimelinePlanResult
                .HistoryLoadEstimatorUnavailable(
                    policy.HistoryLoadEstimatorId
                );
        }
        HistorySegmentDescriptor? predecessor = null;
        if (actual.HeadRowId is { } headRowId) {
            HistoryTimelineStoreReadResult<HistorySegmentDescriptor>
                rowRead = _ledger.ReadRow(headRowId);
            if (rowRead is HistoryTimelineStoreReadResult<
                    HistorySegmentDescriptor>.Busy) {
                return new HistoryTimelinePlanResult.BackendBusy();
            }
            if (rowRead is HistoryTimelineStoreReadResult<
                    HistorySegmentDescriptor>.UnsupportedSchema rowSchema) {
                return new HistoryTimelinePlanResult.Invalid(
                    "TimelineStoreUnsupportedSchema",
                    UnsupportedSchemaDetail(rowSchema.SchemaVersion)
                );
            }
            if (rowRead is HistoryTimelineStoreReadResult<
                    HistorySegmentDescriptor>.Invalid rowInvalid) {
                return new HistoryTimelinePlanResult.Invalid(
                    rowInvalid.Code,
                    rowInvalid.Detail
                );
            }
            if (rowRead is not HistoryTimelineStoreReadResult<
                    HistorySegmentDescriptor>.Found rowFound) {
                return new HistoryTimelinePlanResult.Invalid(
                    "HeadRowUnavailable",
                    "The selected Timeline head row is unavailable."
                );
            }
            predecessor = rowFound.Value;
        }
        HistoryTimelinePlanResult result =
            HistoryTimelineOnlineRawPort.PlanNextRow(
            capture,
            expectedWholeHead,
            policy,
            reservePolicy,
            estimator,
            predecessor,
            cancellationToken
        );
        EventAddress? observedRawHead =
            capture.ReadView.ReadCurrentHead();
        if (observedRawHead != capture.CapturedHead) {
            return new HistoryTimelinePlanResult.RawHeadChanged(
                capture.CapturedHead,
                observedRawHead
            );
        }
        HistoryTimelineStoreReadResult<TimelineHeadRef> afterRead =
            _ledger.ReadSnapshot();
        if (afterRead is HistoryTimelineStoreReadResult<
                TimelineHeadRef>.Busy) {
            return new HistoryTimelinePlanResult.BackendBusy();
        }
        if (afterRead is HistoryTimelineStoreReadResult<
                TimelineHeadRef>.UnsupportedSchema afterSchema) {
            return new HistoryTimelinePlanResult.Invalid(
                "TimelineStoreUnsupportedSchema",
                UnsupportedSchemaDetail(afterSchema.SchemaVersion)
            );
        }
        if (afterRead is HistoryTimelineStoreReadResult<
                TimelineHeadRef>.Invalid afterInvalid) {
            return new HistoryTimelinePlanResult.Invalid(
                afterInvalid.Code,
                afterInvalid.Detail
            );
        }
        if (afterRead is not HistoryTimelineStoreReadResult<
                TimelineHeadRef>.Found afterFound) {
            return new HistoryTimelinePlanResult.Invalid(
                "TimelineHeadUnavailable",
                "The Timeline ledger has no canonical head."
            );
        }
        TimelineHeadRef after = afterFound.Value;
        return after == expectedWholeHead
            ? result
            : new HistoryTimelinePlanResult.StaleTimelineHead(after);
    }

    internal HistoryTimelineCommitResult CommitRow(
        HistoryRowCommitCandidate candidate
    ) {
        ArgumentNullException.ThrowIfNull(candidate);
        using HistoryTimelineLifetime.Operation? operation =
            _lifetime.TryEnterOperation();
        if (operation is null) {
            return new HistoryTimelineCommitResult.Invalid(
                "HistoryTimelineDisposed",
                "The HistoryTimeline handle has been disposed."
            );
        }
        if (!string.Equals(
                candidate.RawFence.CanonicalRepositoryPath,
                _repositoryPath,
                RepositoryPathComparison)) {
            return new HistoryTimelineCommitResult.Invalid(
                "CommitRepositoryMismatch",
                "The commit candidate belongs to another canonical repository.");
        }
        if (!candidate.ReserveProof.IsExactFor(
                candidate.Proposal,
                candidate.RawFence)) {
            return new HistoryTimelineCommitResult.Invalid(
                "RecentReserveProofInvalid",
                "The commit candidate has no exact recent-reserve proof."
            );
        }
        return _ledger.CommitRow(candidate);
    }

    public SelectedHistoryRowResult ReadSelectedRow(
        TimelineHeadRef expectedWholeHead,
        HistoryRowId rowId
    ) {
        using HistoryTimelineLifetime.Operation? operation =
            _lifetime.TryEnterOperation();
        return operation is null
            ? new SelectedHistoryRowResult.Invalid(
                "HistoryTimelineDisposed",
                "The HistoryTimeline handle has been disposed."
            )
            : _ledger.ReadSelectedRow(expectedWholeHead, rowId);
    }

    public HistorySegmentOpenResult OpenSegment(
        TimelineHeadRef expectedWholeHead,
        OnlineSelectedRawCapture capture,
        HistoryRowId rowId,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(expectedWholeHead);
        ArgumentNullException.ThrowIfNull(capture);
        using HistoryTimelineLifetime.Operation? operation =
            _lifetime.TryEnterOperation();
        if (operation is null) {
            return new HistorySegmentOpenResult.Invalid(
                "HistoryTimelineDisposed",
                "The HistoryTimeline handle has been disposed."
            );
        }
        SelectedHistoryRowResult membership =
            _ledger.ReadSelectedRow(expectedWholeHead, rowId);
        HistorySegmentDescriptor descriptor;
        switch (membership) {
            case SelectedHistoryRowResult.Selected selected:
                descriptor = selected.Descriptor;
                break;
            case SelectedHistoryRowResult.NotOnSelectedPath:
                return new HistorySegmentOpenResult
                    .NotOnSelectedPath(rowId);
            case SelectedHistoryRowResult.StaleTimelineHead stale:
                return new HistorySegmentOpenResult
                    .StaleTimelineHead(stale.Actual);
            case SelectedHistoryRowResult.BackendBusy:
                return new HistorySegmentOpenResult.BackendBusy();
            case SelectedHistoryRowResult.Invalid invalid:
                return new HistorySegmentOpenResult.Invalid(
                    invalid.Code,
                    invalid.Detail
                );
            default:
                return new HistorySegmentOpenResult.Invalid(
                    "SelectedRowOutcomeInvalid",
                    "The ledger returned an unknown selected-row outcome."
                );
        }

        HistoryTimelineStoreReadResult<PartitionPolicyRevision>
            creationPolicyRead = _ledger.ReadPolicy(
            descriptor.PartitionPolicyDigestAtCreation
        );
        if (creationPolicyRead is HistoryTimelineStoreReadResult<
                PartitionPolicyRevision>.Busy) {
            return new HistorySegmentOpenResult.BackendBusy();
        }
        if (creationPolicyRead is HistoryTimelineStoreReadResult<
                PartitionPolicyRevision>.UnsupportedSchema policySchema) {
            return new HistorySegmentOpenResult.Invalid(
                "TimelineStoreUnsupportedSchema",
                UnsupportedSchemaDetail(policySchema.SchemaVersion)
            );
        }
        if (creationPolicyRead is HistoryTimelineStoreReadResult<
                PartitionPolicyRevision>.Invalid policyInvalid) {
            return new HistorySegmentOpenResult.Invalid(
                policyInvalid.Code,
                policyInvalid.Detail
            );
        }
        if (creationPolicyRead is not HistoryTimelineStoreReadResult<
                PartitionPolicyRevision>.Found policyFound) {
            return new HistorySegmentOpenResult
                .PartitionPolicyUnavailable(
                    descriptor.PartitionPolicyDigestAtCreation
                );
        }
        PartitionPolicyRevision creationPolicy = policyFound.Value;
        if (!HistoryPartitionAlgorithms.IsSupported(
                creationPolicy.PartitionAlgorithmId)) {
            return new HistorySegmentOpenResult
                .PartitionAlgorithmUnavailable(
                    creationPolicy.PartitionAlgorithmId
                );
        }
        IHistoryUnitLoadEstimator? estimator = _estimators.Resolve(
            descriptor.HistoryLoadEstimatorId
        );
        if (estimator is null) {
            return new HistorySegmentOpenResult
                .HistoryLoadEstimatorUnavailable(
                    descriptor.HistoryLoadEstimatorId
                );
        }
        HistorySegmentDescriptor? predecessor = null;
        if (descriptor.PreviousRowId is { } previousRowId) {
            HistoryTimelineStoreReadResult<HistorySegmentDescriptor>
                predecessorRead = _ledger.ReadRow(previousRowId);
            if (predecessorRead is HistoryTimelineStoreReadResult<
                    HistorySegmentDescriptor>.Busy) {
                return new HistorySegmentOpenResult.BackendBusy();
            }
            if (predecessorRead is HistoryTimelineStoreReadResult<
                    HistorySegmentDescriptor>.UnsupportedSchema rowSchema) {
                return new HistorySegmentOpenResult.Invalid(
                    "TimelineStoreUnsupportedSchema",
                    UnsupportedSchemaDetail(rowSchema.SchemaVersion)
                );
            }
            if (predecessorRead is HistoryTimelineStoreReadResult<
                    HistorySegmentDescriptor>.Invalid rowInvalid) {
                return new HistorySegmentOpenResult.Invalid(
                    rowInvalid.Code,
                    rowInvalid.Detail
                );
            }
            if (predecessorRead is not HistoryTimelineStoreReadResult<
                    HistorySegmentDescriptor>.Found predecessorFound) {
                return new HistorySegmentOpenResult.Invalid(
                    "SelectedPredecessorUnavailable",
                    "The selected row predecessor is unavailable."
                );
            }
            predecessor = predecessorFound.Value;
        }
        if (expectedWholeHead.HeadRowId is not { }
            selectedHeadRowId) {
            return new HistorySegmentOpenResult.Invalid(
                "SelectedHeadUnavailable",
                "The selected Timeline head row is unavailable."
            );
        }
        HistoryTimelineStoreReadResult<HistorySegmentDescriptor>
            selectedHeadRead = _ledger.ReadRow(selectedHeadRowId);
        if (selectedHeadRead is HistoryTimelineStoreReadResult<
                HistorySegmentDescriptor>.Busy) {
            return new HistorySegmentOpenResult.BackendBusy();
        }
        if (selectedHeadRead is HistoryTimelineStoreReadResult<
                HistorySegmentDescriptor>.UnsupportedSchema headRowSchema) {
            return new HistorySegmentOpenResult.Invalid(
                "TimelineStoreUnsupportedSchema",
                UnsupportedSchemaDetail(headRowSchema.SchemaVersion)
            );
        }
        if (selectedHeadRead is HistoryTimelineStoreReadResult<
                HistorySegmentDescriptor>.Invalid headRowInvalid) {
            return new HistorySegmentOpenResult.Invalid(
                headRowInvalid.Code,
                headRowInvalid.Detail
            );
        }
        if (selectedHeadRead is not HistoryTimelineStoreReadResult<
                HistorySegmentDescriptor>.Found selectedHeadFound) {
            return new HistorySegmentOpenResult.Invalid(
                "SelectedHeadUnavailable",
                "The selected Timeline head row is unavailable."
            );
        }
        HistorySegmentDescriptor selectedPathHead =
            selectedHeadFound.Value;
        HistorySegmentOpenResult result =
            HistoryTimelineOnlineRawPort.OpenSegment(
            capture,
            expectedWholeHead,
            selectedPathHead,
            descriptor,
            predecessor,
            creationPolicy,
            estimator,
            cancellationToken
        );
        EventAddress? observedRawHead =
            capture.ReadView.ReadCurrentHead();
        if (observedRawHead != capture.CapturedHead) {
            return new HistorySegmentOpenResult.RawHeadChanged(
                capture.CapturedHead,
                observedRawHead
            );
        }
        HistoryTimelineStoreReadResult<TimelineHeadRef> afterRead =
            _ledger.ReadSnapshot();
        if (afterRead is HistoryTimelineStoreReadResult<
                TimelineHeadRef>.Busy) {
            return new HistorySegmentOpenResult.BackendBusy();
        }
        if (afterRead is HistoryTimelineStoreReadResult<
                TimelineHeadRef>.UnsupportedSchema afterSchema) {
            return new HistorySegmentOpenResult.Invalid(
                "TimelineStoreUnsupportedSchema",
                UnsupportedSchemaDetail(afterSchema.SchemaVersion)
            );
        }
        if (afterRead is HistoryTimelineStoreReadResult<
                TimelineHeadRef>.Invalid afterInvalid) {
            return new HistorySegmentOpenResult.Invalid(
                afterInvalid.Code,
                afterInvalid.Detail
            );
        }
        if (afterRead is not HistoryTimelineStoreReadResult<
                TimelineHeadRef>.Found afterFound) {
            return new HistorySegmentOpenResult.Invalid(
                "TimelineHeadUnavailable",
                "The Timeline ledger has no canonical head."
            );
        }
        TimelineHeadRef after = afterFound.Value;
        return after == expectedWholeHead
            ? result
            : new HistorySegmentOpenResult.StaleTimelineHead(after);
    }

    public HistoryTimelineReconcileResult ReconcileSelectedPath(
        TimelineHeadRef expectedWholeHead,
        SJ.SessionJournalReadView readView,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(expectedWholeHead);
        ArgumentNullException.ThrowIfNull(readView);
        using HistoryTimelineLifetime.Operation? operation =
            _lifetime.TryEnterOperation();
        if (operation is null) {
            return new HistoryTimelineReconcileResult.Invalid(
                "HistoryTimelineDisposed",
                "The HistoryTimeline handle has been disposed."
            );
        }
        OnlineSelectedRawCaptureResult captureResult = CaptureOnline(
            expectedWholeHead,
            readView,
            cancellationToken
        );
        switch (captureResult) {
            case OnlineSelectedRawCaptureResult.StaleTimelineHead stale:
                return new HistoryTimelineReconcileResult
                    .StaleTimelineHead(stale.Actual);
            case OnlineSelectedRawCaptureResult.Invalid invalid:
                return new HistoryTimelineReconcileResult.Invalid(
                    invalid.Code,
                    invalid.Detail
                );
            case OnlineSelectedRawCaptureResult
                .PartitionPolicyUnavailable unavailable:
                return new HistoryTimelineReconcileResult
                    .PartitionPolicyUnavailable(
                    unavailable.PolicyDigest
                );
            case OnlineSelectedRawCaptureResult.BackendBusy:
                return new HistoryTimelineReconcileResult.BackendBusy();
            case OnlineSelectedRawCaptureResult.LimitExceeded limit:
                return new HistoryTimelineReconcileResult.Invalid(
                    "RecentReserveOperationLimitExceeded",
                    limit.Limit);
            case OnlineSelectedRawCaptureResult.Empty empty:
                return _ledger.ReconcileSelectedPath(
                    new HistoryTimelineReconcileCandidate(
                        expectedWholeHead,
                        selectedRowId: null,
                        empty.RawFence
                    )
                );
            case OnlineSelectedRawCaptureResult.Captured captured:
                return ReconcileCaptured(
                    expectedWholeHead,
                    captured.Capture
                );
            default:
                return new HistoryTimelineReconcileResult.Invalid(
                    "RawCaptureOutcomeInvalid",
                    "The raw capture returned an unknown outcome."
                );
        }
    }

    internal HistoryTimelineOfflineBuilderOpenResult OpenOfflineBuilderForTests(
        TimelineHeadRef expectedWholeHead,
        SJ.SessionSelectedLineageForwardCursor cursor
    ) => OpenOfflineBuilder(
        expectedWholeHead,
        cursor,
        CreateNoReservePolicyForTests(expectedWholeHead));

    internal HistoryTimelineOfflineBuilderOpenResult OpenOfflineBuilder(
        TimelineHeadRef expectedWholeHead,
        SJ.SessionSelectedLineageForwardCursor cursor,
        HistoryRecentReservePolicy reservePolicy
    ) {
        ArgumentNullException.ThrowIfNull(expectedWholeHead);
        ArgumentNullException.ThrowIfNull(cursor);
        ArgumentNullException.ThrowIfNull(reservePolicy);
        using HistoryTimelineLifetime.Operation? operation =
            _lifetime.TryEnterOperation();
        if (operation is null) {
            return new HistoryTimelineOfflineBuilderOpenResult.Invalid(
                "HistoryTimelineDisposed",
                "The HistoryTimeline handle has been disposed."
            );
        }
        HistoryTimelineStoreReadResult<TimelineHeadRef> headRead =
            _ledger.ReadSnapshot();
        if (headRead is HistoryTimelineStoreReadResult<
                TimelineHeadRef>.Busy) {
            return new HistoryTimelineOfflineBuilderOpenResult.BackendBusy();
        }
        if (headRead is HistoryTimelineStoreReadResult<
                TimelineHeadRef>.UnsupportedSchema headSchema) {
            return new HistoryTimelineOfflineBuilderOpenResult.Invalid(
                "TimelineStoreUnsupportedSchema",
                UnsupportedSchemaDetail(headSchema.SchemaVersion)
            );
        }
        if (headRead is HistoryTimelineStoreReadResult<
                TimelineHeadRef>.Invalid headInvalid) {
            return new HistoryTimelineOfflineBuilderOpenResult.Invalid(
                headInvalid.Code,
                headInvalid.Detail
            );
        }
        if (headRead is not HistoryTimelineStoreReadResult<
                TimelineHeadRef>.Found headFound) {
            return new HistoryTimelineOfflineBuilderOpenResult.Invalid(
                "TimelineHeadUnavailable",
                "The Timeline ledger has no canonical head."
            );
        }
        TimelineHeadRef actual = headFound.Value;
        if (actual != expectedWholeHead) {
            return new HistoryTimelineOfflineBuilderOpenResult
                .StaleTimelineHead(actual);
        }
        HistoryTimelineStoreReadResult<PartitionPolicyRevision>
            policyRead = _ledger.ReadPolicy(
                actual.ActivePartitionPolicyDigest);
        if (policyRead is HistoryTimelineStoreReadResult<
                PartitionPolicyRevision>.Busy) {
            return new HistoryTimelineOfflineBuilderOpenResult.BackendBusy();
        }
        if (policyRead is HistoryTimelineStoreReadResult<
                PartitionPolicyRevision>.UnsupportedSchema policySchema) {
            return new HistoryTimelineOfflineBuilderOpenResult.Invalid(
                "TimelineStoreUnsupportedSchema",
                UnsupportedSchemaDetail(policySchema.SchemaVersion));
        }
        if (policyRead is HistoryTimelineStoreReadResult<
                PartitionPolicyRevision>.Invalid policyInvalid) {
            return new HistoryTimelineOfflineBuilderOpenResult.Invalid(
                policyInvalid.Code,
                policyInvalid.Detail);
        }
        if (policyRead is not HistoryTimelineStoreReadResult<
                PartitionPolicyRevision>.Found policyFound) {
            return new HistoryTimelineOfflineBuilderOpenResult.Invalid(
                "PartitionPolicyUnavailable",
                actual.ActivePartitionPolicyDigest);
        }
        if (!reservePolicy.IsExactFor(actual, policyFound.Value)) {
            return new HistoryTimelineOfflineBuilderOpenResult.Invalid(
                "RecentReservePolicyMismatch",
                "The recent-reserve policy does not bind the exact Ref, active partition policy, and estimator."
            );
        }
        EventAddress capturedHead =
            cursor.Authority.Capture.CapturedHead;
        if (!cursor.IsBoundTo(
                _repositoryPath,
                expectedWholeHead.RefId,
                capturedHead)) {
            return new HistoryTimelineOfflineBuilderOpenResult.Invalid(
                "OfflineRawScopeMismatch",
                "The forward cursor belongs to another repository, Ref, or captured raw head."
            );
        }
        EventAddress? observedHead = cursor.ReadCurrentHead();
        if (observedHead != capturedHead) {
            return new HistoryTimelineOfflineBuilderOpenResult
                .RawHeadChanged(capturedHead, observedHead);
        }
        if (expectedWholeHead.HeadRowId is { } headRowId) {
            SelectedHistoryRowResult membership =
                _ledger.ReadSelectedRow(
                    expectedWholeHead,
                    headRowId
                );
            if (membership
                is not SelectedHistoryRowResult.Selected selected) {
                return membership switch {
                    SelectedHistoryRowResult.BackendBusy
                        => new HistoryTimelineOfflineBuilderOpenResult
                            .BackendBusy(),
                    SelectedHistoryRowResult.StaleTimelineHead stale
                        => new HistoryTimelineOfflineBuilderOpenResult
                            .StaleTimelineHead(stale.Actual),
                    SelectedHistoryRowResult.Invalid invalid
                        => new HistoryTimelineOfflineBuilderOpenResult
                            .Invalid(invalid.Code, invalid.Detail),
                    _ => new HistoryTimelineOfflineBuilderOpenResult.Invalid(
                        "OfflineSelectedHeadUnavailable",
                        "The exact selected Timeline head row is unavailable."
                    )
                };
            }
            if (cursor.CurrentBoundary
                    != selected.Descriptor.EndInclusive
                || cursor.CurrentSetups
                    != selected.Descriptor.EndSetups) {
                return new HistoryTimelineOfflineBuilderOpenResult
                    .Invalid(
                        "OfflineCursorBoundaryMismatch",
                        "The forward cursor is not positioned at the selected Timeline head boundary."
                    );
            }
        }
        else if (cursor.CurrentBoundary
                    != cursor.Authority.BootstrapSeed.Address
                 || cursor.CurrentSetups
                    != cursor.Authority.BootstrapSeed.Setups) {
            return new HistoryTimelineOfflineBuilderOpenResult.Invalid(
                "OfflineBootstrapBoundaryMismatch",
                "A fresh offline builder must begin at the audited bootstrap seed."
            );
        }
        return new HistoryTimelineOfflineBuilderOpenResult.Opened(
            new HistoryTimelineOfflineBuilder(
                this,
                cursor,
                reservePolicy,
                _testHooks.RecentReserveForwardRangeEventCap
                    ?? SJ.SessionSelectedLineageAuditLimits
                        .MaximumForwardRangeEventCount,
                Math.Min(
                    _testHooks.RecentReserveInitialForwardRangeEventCount
                        ?? 128,
                    _testHooks.RecentReserveForwardRangeEventCap
                        ?? SJ.SessionSelectedLineageAuditLimits
                            .MaximumForwardRangeEventCount))
        );
    }

    private HistoryRecentReservePolicy CreateNoReservePolicyForTests(
        TimelineHeadRef expectedWholeHead
    ) {
        PartitionPolicyRevision policy = PartitionPolicyRevision.Create(
            expectedWholeHead.TimelineId,
            HistoryPartitionAlgorithms.FirstReplaySafeBoundaryAtTargetV1,
            NoReserveTestEstimatorId,
            new HistoryLoadUnit(1),
            maxRawEvents: 1,
            maxRenderedBytes: 1);
        return new HistoryRecentReservePolicy(
            _repositoryPath,
            expectedWholeHead.RefId,
            cadenceGeneration: 0,
            new string('0', 64),
            policy,
            new HistoryLoadUnit(0),
            new HistoryRecentReserveAuthorityToken());
    }

    public HistoryTimelineReconcileResult ReconcileSelectedPathOffline(
        TimelineHeadRef expectedWholeHead,
        SJ.SessionSelectedLineageForwardCursor cursor,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(expectedWholeHead);
        ArgumentNullException.ThrowIfNull(cursor);
        using HistoryTimelineLifetime.Operation? operation =
            _lifetime.TryEnterOperation();
        if (operation is null) {
            return new HistoryTimelineReconcileResult.Invalid(
                "HistoryTimelineDisposed",
                "The HistoryTimeline handle has been disposed."
            );
        }
        HistoryTimelineStoreReadResult<TimelineHeadRef> headRead =
            _ledger.ReadSnapshot();
        if (headRead is HistoryTimelineStoreReadResult<
                TimelineHeadRef>.Busy) {
            return new HistoryTimelineReconcileResult.BackendBusy();
        }
        if (headRead is HistoryTimelineStoreReadResult<
                TimelineHeadRef>.UnsupportedSchema headSchema) {
            return new HistoryTimelineReconcileResult.Invalid(
                "TimelineStoreUnsupportedSchema",
                UnsupportedSchemaDetail(headSchema.SchemaVersion)
            );
        }
        if (headRead is HistoryTimelineStoreReadResult<
                TimelineHeadRef>.Invalid headInvalid) {
            return new HistoryTimelineReconcileResult.Invalid(
                headInvalid.Code,
                headInvalid.Detail
            );
        }
        if (headRead is not HistoryTimelineStoreReadResult<
                TimelineHeadRef>.Found headFound) {
            return new HistoryTimelineReconcileResult.Invalid(
                "TimelineHeadUnavailable",
                "The Timeline ledger has no canonical head."
            );
        }
        TimelineHeadRef actual = headFound.Value;
        if (actual != expectedWholeHead) {
            return new HistoryTimelineReconcileResult
                .StaleTimelineHead(actual);
        }
        EventAddress capturedHead =
            cursor.Authority.Capture.CapturedHead;
        if (!cursor.IsBoundTo(
                _repositoryPath,
                expectedWholeHead.RefId,
                capturedHead)) {
            return new HistoryTimelineReconcileResult.Invalid(
                "OfflineReconcileRawScopeMismatch",
                "The forward cursor belongs to another repository, Ref, or captured raw head."
            );
        }
        if (cursor.CurrentBoundary
                != cursor.Authority.BootstrapSeed.Address
            || cursor.CurrentSetups
                != cursor.Authority.BootstrapSeed.Setups) {
            return new HistoryTimelineReconcileResult.Invalid(
                "OfflineReconcileCursorPositionInvalid",
                "Offline reconciliation requires a fresh cursor at its audited bootstrap seed."
            );
        }

        HistoryRowId? selectedRowId = null;
        TimelineHeadRef? staleHead = null;
        bool backendBusy = false;
        SelectedHistoryBoundaryResult.Invalid? invalid = null;
        HistoryTimelineBoundaryProbeOpenResult probeOpen =
            _ledger.OpenBoundaryProbe(expectedWholeHead);
        if (probeOpen is HistoryTimelineBoundaryProbeOpenResult
                .StaleTimelineHead probeStale) {
            return new HistoryTimelineReconcileResult
                .StaleTimelineHead(probeStale.Actual);
        }
        if (probeOpen is HistoryTimelineBoundaryProbeOpenResult.Busy) {
            return new HistoryTimelineReconcileResult.BackendBusy();
        }
        if (probeOpen is HistoryTimelineBoundaryProbeOpenResult
                .Invalid probeInvalid) {
            return new HistoryTimelineReconcileResult.Invalid(
                probeInvalid.Code,
                probeInvalid.Detail
            );
        }
        if (probeOpen is not HistoryTimelineBoundaryProbeOpenResult
                .Opened probeAvailable) {
            return new HistoryTimelineReconcileResult.Invalid(
                "BoundaryProbeOpenOutcomeInvalid",
                "The ledger returned an unknown boundary-probe open outcome."
            );
        }
        using IHistoryTimelineBoundaryProbe boundaryProbe =
            probeAvailable.Probe;
        try {
            SJ.SessionSelectedLineageBoundaryProbeResult scan =
                cursor.ProbeBoundaries(
                    address => {
                        _testHooks
                            .BeforeOfflineReconcileBoundaryProbe
                            ?.Invoke(address);
                        SelectedHistoryBoundaryResult lookup =
                            boundaryProbe.Probe(address);
                        switch (lookup) {
                            case SelectedHistoryBoundaryResult.Found found:
                                selectedRowId = found.Descriptor.RowId;
                                return SJ
                                    .SessionSelectedLineageBoundaryProbeDecision
                                    .Match;
                            case SelectedHistoryBoundaryResult.NotFound:
                                return SJ
                                    .SessionSelectedLineageBoundaryProbeDecision
                                    .Continue;
                            case SelectedHistoryBoundaryResult
                                .StaleTimelineHead stale:
                                staleHead = stale.Actual;
                                return SJ
                                    .SessionSelectedLineageBoundaryProbeDecision
                                    .Stop;
                            case SelectedHistoryBoundaryResult.BackendBusy:
                                backendBusy = true;
                                return SJ
                                    .SessionSelectedLineageBoundaryProbeDecision
                                    .Stop;
                            case SelectedHistoryBoundaryResult.Invalid defect:
                                invalid = defect;
                                return SJ
                                    .SessionSelectedLineageBoundaryProbeDecision
                                    .Stop;
                            default:
                                invalid = new SelectedHistoryBoundaryResult
                                    .Invalid(
                                        "SelectedBoundaryOutcomeInvalid",
                                        "The ledger returned an unknown selected-boundary outcome."
                                    );
                                return SJ
                                    .SessionSelectedLineageBoundaryProbeDecision
                                    .Stop;
                        }
                    },
                    cancellationToken
                );
            if (staleHead is not null) {
                return new HistoryTimelineReconcileResult
                    .StaleTimelineHead(staleHead);
            }
            if (backendBusy) {
                return new HistoryTimelineReconcileResult.BackendBusy();
            }
            if (invalid is not null) {
                return new HistoryTimelineReconcileResult.Invalid(
                    invalid.Code,
                    invalid.Detail
                );
            }
            if (scan.Stopped) {
                return new HistoryTimelineReconcileResult.Invalid(
                    "OfflineReconcileProbeStopped",
                    "The offline boundary probe stopped without a typed ledger outcome."
                );
            }
            if ((scan.LatestMatchingBoundary is null)
                != (selectedRowId is null)) {
                return new HistoryTimelineReconcileResult.Invalid(
                    "OfflineReconcileMatchMismatch",
                    "The streaming boundary match and selected row witness differ."
                );
            }
        }
        catch (SJ.SessionSelectedLineageAuditChangedException changed) {
            return new HistoryTimelineReconcileResult.RawHeadChanged(
                changed.ExpectedHead,
                changed.ObservedHead
            );
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException) {
            return new HistoryTimelineReconcileResult.Invalid(
                exception is IOException
                    ? "OfflineReconcileRawIoInvalid"
                    : "OfflineReconcileRawEvidenceInvalid",
                exception.Message
            );
        }

        HistoryTimelineStoreReadResult<TimelineHeadRef> afterRead =
            _ledger.ReadSnapshot();
        if (afterRead is HistoryTimelineStoreReadResult<
                TimelineHeadRef>.Busy) {
            return new HistoryTimelineReconcileResult.BackendBusy();
        }
        if (afterRead is HistoryTimelineStoreReadResult<
                TimelineHeadRef>.UnsupportedSchema afterSchema) {
            return new HistoryTimelineReconcileResult.Invalid(
                "TimelineStoreUnsupportedSchema",
                UnsupportedSchemaDetail(afterSchema.SchemaVersion)
            );
        }
        if (afterRead is HistoryTimelineStoreReadResult<
                TimelineHeadRef>.Invalid afterInvalid) {
            return new HistoryTimelineReconcileResult.Invalid(
                afterInvalid.Code,
                afterInvalid.Detail
            );
        }
        if (afterRead is not HistoryTimelineStoreReadResult<
                TimelineHeadRef>.Found afterFound) {
            return new HistoryTimelineReconcileResult.Invalid(
                "TimelineHeadUnavailable",
                "The Timeline ledger has no canonical head."
            );
        }
        TimelineHeadRef afterScan = afterFound.Value;
        if (afterScan != expectedWholeHead) {
            return new HistoryTimelineReconcileResult
                .StaleTimelineHead(afterScan);
        }
        return _ledger.ReconcileSelectedPath(
            new HistoryTimelineReconcileCandidate(
                expectedWholeHead,
                selectedRowId,
                new OfflineSelectedRawCursorFence(_repositoryPath, cursor)
            )
        );
    }

    private HistoryTimelineReconcileResult ReconcileCaptured(
        TimelineHeadRef expectedWholeHead,
        OnlineSelectedRawCapture capture
    ) {
        HistoryRowId? selectedRowId = null;
        if (expectedWholeHead.HeadRowId is { } headRowId) {
            HistoryTimelineStoreReadResult<HistorySegmentDescriptor>
                rowRead = _ledger.ReadRow(headRowId);
            if (rowRead is HistoryTimelineStoreReadResult<
                    HistorySegmentDescriptor>.Busy) {
                return new HistoryTimelineReconcileResult.BackendBusy();
            }
            if (rowRead is HistoryTimelineStoreReadResult<
                    HistorySegmentDescriptor>.UnsupportedSchema rowSchema) {
                return new HistoryTimelineReconcileResult.Invalid(
                    "TimelineStoreUnsupportedSchema",
                    UnsupportedSchemaDetail(rowSchema.SchemaVersion)
                );
            }
            if (rowRead is HistoryTimelineStoreReadResult<
                    HistorySegmentDescriptor>.Invalid rowInvalid) {
                return new HistoryTimelineReconcileResult.Invalid(
                    rowInvalid.Code,
                    rowInvalid.Detail
                );
            }
            if (rowRead is not HistoryTimelineStoreReadResult<
                    HistorySegmentDescriptor>.Found rowFound) {
                return new HistoryTimelineReconcileResult.Invalid(
                    "SelectedPathRowUnavailable",
                    "The selected Timeline head row is unavailable."
                );
            }
            HistorySegmentDescriptor selectedHead = rowFound.Value;
            switch (capture.Prefix.Lookup(
                    selectedHead.EndInclusive)) {
                case SJ.SessionCurrentLineageAnchorLookup.Found:
                    selectedRowId = headRowId;
                    break;
                case SJ.SessionCurrentLineageAnchorLookup
                    .BeyondPrefix beyond:
                    return new HistoryTimelineReconcileResult
                        .OfflineBootstrapRequired(beyond.Evidence);
                case SJ.SessionCurrentLineageAnchorLookup.OffLineage:
                    HistoryTimelineBoundaryProbeOpenResult probeOpen =
                        _ledger.OpenBoundaryProbe(expectedWholeHead);
                    if (probeOpen is HistoryTimelineBoundaryProbeOpenResult
                            .StaleTimelineHead stale) {
                        return new HistoryTimelineReconcileResult
                            .StaleTimelineHead(stale.Actual);
                    }
                    if (probeOpen is HistoryTimelineBoundaryProbeOpenResult
                            .Busy) {
                        return new HistoryTimelineReconcileResult
                            .BackendBusy();
                    }
                    if (probeOpen is HistoryTimelineBoundaryProbeOpenResult
                            .Invalid invalid) {
                        return new HistoryTimelineReconcileResult.Invalid(
                            invalid.Code,
                            invalid.Detail
                        );
                    }
                    if (probeOpen is not
                        HistoryTimelineBoundaryProbeOpenResult
                            .Opened available) {
                        return new HistoryTimelineReconcileResult.Invalid(
                            "BoundaryProbeOpenOutcomeInvalid",
                            "The ledger returned an unknown boundary-probe open outcome."
                        );
                    }
                    using (available.Probe) {
                        foreach (SJ.SessionCurrentLineageHeader header
                                 in capture.Prefix.HeadToOldest) {
                            SelectedHistoryBoundaryResult lookup =
                                available.Probe.Probe(header.Address);
                            switch (lookup) {
                                case SelectedHistoryBoundaryResult.Found found:
                                    selectedRowId = found.Descriptor.RowId;
                                    break;
                                case SelectedHistoryBoundaryResult
                                    .StaleTimelineHead boundaryStale:
                                    return new HistoryTimelineReconcileResult
                                        .StaleTimelineHead(
                                            boundaryStale.Actual
                                        );
                                case SelectedHistoryBoundaryResult
                                    .Invalid boundaryInvalid:
                                    return new HistoryTimelineReconcileResult
                                        .Invalid(
                                            boundaryInvalid.Code,
                                            boundaryInvalid.Detail
                                        );
                                case SelectedHistoryBoundaryResult.BackendBusy:
                                    return new HistoryTimelineReconcileResult
                                        .BackendBusy();
                            }
                            if (selectedRowId is not null) {
                                break;
                            }
                        }
                    }
                    break;
            }
        }
        return _ledger.ReconcileSelectedPath(
            new HistoryTimelineReconcileCandidate(
                expectedWholeHead,
                selectedRowId,
                capture
            )
        );
    }

    private static string CanonicalRepositoryPath(string path)
        => Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(path)
        );


    internal static string UnsupportedSchemaDetail(int schemaVersion)
        => $"The Timeline store schema version {schemaVersion} is unsupported.";

    private static StringComparison RepositoryPathComparison
        => OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    internal HistoryTimelineStoreReadResult<PartitionPolicyRevision>
        ReadPolicyForOffline(
        string policyDigest
    ) => _ledger.ReadPolicy(policyDigest);

    internal HistoryTimelineLifetime.Operation?
        TryEnterOperationForOffline()
        => _lifetime.TryEnterOperation();

    internal HistoryTimelineStoreReadResult<HistorySegmentDescriptor>
        ReadRowForOffline(
        HistoryRowId rowId
    ) => _ledger.ReadRow(rowId);

    internal IHistoryUnitLoadEstimator? ResolveEstimatorForOffline(
        string estimatorId
    ) => _estimators.Resolve(estimatorId);
}
