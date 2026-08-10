using Atelia.EventJournal;
using SJ = Atelia.SessionJournal;

namespace Atelia.SessionJournal.HistoryTimeline;

/// <summary>
/// Backend-neutral WP-01B operation coordinator. Construction stays at the
/// composition root; callers cannot select the in-memory semantic carrier.
/// </summary>
public sealed class HistoryTimelineCoordinator {
    private readonly string _repositoryPath;
    private readonly IHistoryTimelineLedgerPort _ledger;
    private readonly IHistoryTimelineEstimatorResolver _estimators;
    private readonly HistoryTimelineCoordinatorTestHooks _testHooks;

    internal HistoryTimelineCoordinator(
        string repositoryPath,
        IHistoryTimelineLedgerPort ledger,
        params IHistoryUnitLoadEstimator[] estimators
    ) : this(
        repositoryPath,
        ledger,
        new HistoryTimelineCoordinatorTestHooks(),
        estimators
    ) { }

    internal HistoryTimelineCoordinator(
        string repositoryPath,
        IHistoryTimelineLedgerPort ledger,
        HistoryTimelineCoordinatorTestHooks testHooks,
        params IHistoryUnitLoadEstimator[] estimators
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        _repositoryPath = CanonicalRepositoryPath(repositoryPath);
        _ledger = ledger
            ?? throw new ArgumentNullException(nameof(ledger));
        _testHooks = testHooks
            ?? throw new ArgumentNullException(nameof(testHooks));
        _estimators = new HistoryTimelineEstimatorRegistry(
            estimators
        );
    }

    public TimelineHeadRef ReadSnapshot() => _ledger.ReadSnapshot();

    public HistoryTimelinePolicyPutResult PutPolicy(
        PartitionPolicyRevision policy
    ) => _ledger.PutPolicy(policy);

    public HistoryTimelinePolicyCasResult CompareExchangePolicy(
        TimelineHeadRef expectedWholeHead,
        string nextPolicyDigest
    ) => _ledger.CompareExchangePolicy(
        expectedWholeHead,
        nextPolicyDigest
    );

    public OnlineSelectedRawCaptureResult CaptureOnline(
        TimelineHeadRef expectedWholeHead,
        SJ.SessionJournalReadView readView,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(expectedWholeHead);
        ArgumentNullException.ThrowIfNull(readView);
        TimelineHeadRef actual = _ledger.ReadSnapshot();
        if (actual != expectedWholeHead) {
            return new OnlineSelectedRawCaptureResult
                .StaleTimelineHead(actual);
        }
        PartitionPolicyRevision? policy = _ledger.ReadPolicy(
            actual.ActivePartitionPolicyDigest
        );
        if (policy is null) {
            return new OnlineSelectedRawCaptureResult
                .PartitionPolicyUnavailable(
                    actual.ActivePartitionPolicyDigest
                );
        }
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
            policy.MaxRawEvents,
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
        TimelineHeadRef after = _ledger.ReadSnapshot();
        return after == expectedWholeHead
            ? result
            : new OnlineSelectedRawCaptureResult
                .StaleTimelineHead(after);
    }

    public HistoryTimelinePlanResult PlanNextRow(
        TimelineHeadRef expectedWholeHead,
        OnlineSelectedRawCapture capture,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(expectedWholeHead);
        ArgumentNullException.ThrowIfNull(capture);
        TimelineHeadRef actual = _ledger.ReadSnapshot();
        if (actual != expectedWholeHead) {
            return new HistoryTimelinePlanResult.StaleTimelineHead(
                actual
            );
        }
        PartitionPolicyRevision? policy = _ledger.ReadPolicy(
            actual.ActivePartitionPolicyDigest
        );
        if (policy is null) {
            return new HistoryTimelinePlanResult
                .PartitionPolicyUnavailable(
                    actual.ActivePartitionPolicyDigest
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
            predecessor = _ledger.ReadRow(headRowId);
            if (predecessor is null) {
                return new HistoryTimelinePlanResult.Invalid(
                    "HeadRowUnavailable",
                    "The selected Timeline head row is unavailable."
                );
            }
        }
        HistoryTimelinePlanResult result =
            HistoryTimelineOnlineRawPort.PlanNextRow(
            capture,
            expectedWholeHead,
            policy,
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
        TimelineHeadRef after = _ledger.ReadSnapshot();
        return after == expectedWholeHead
            ? result
            : new HistoryTimelinePlanResult.StaleTimelineHead(after);
    }

    public HistoryTimelineCommitResult CommitRow(
        HistoryRowCommitCandidate candidate
    ) => _ledger.CommitRow(candidate);

    public SelectedHistoryRowResult ReadSelectedRow(
        TimelineHeadRef expectedWholeHead,
        HistoryRowId rowId
    ) => _ledger.ReadSelectedRow(expectedWholeHead, rowId);

    public HistorySegmentOpenResult OpenSegment(
        TimelineHeadRef expectedWholeHead,
        OnlineSelectedRawCapture capture,
        HistoryRowId rowId,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(expectedWholeHead);
        ArgumentNullException.ThrowIfNull(capture);
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

        PartitionPolicyRevision? creationPolicy = _ledger.ReadPolicy(
            descriptor.PartitionPolicyDigestAtCreation
        );
        if (creationPolicy is null) {
            return new HistorySegmentOpenResult
                .PartitionPolicyUnavailable(
                    descriptor.PartitionPolicyDigestAtCreation
                );
        }
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
            predecessor = _ledger.ReadRow(previousRowId);
            if (predecessor is null) {
                return new HistorySegmentOpenResult.Invalid(
                    "SelectedPredecessorUnavailable",
                    "The selected row predecessor is unavailable."
                );
            }
        }
        HistorySegmentDescriptor? selectedPathHead =
            expectedWholeHead.HeadRowId is { } selectedHeadRowId
                ? _ledger.ReadRow(selectedHeadRowId)
                : null;
        if (selectedPathHead is null) {
            return new HistorySegmentOpenResult.Invalid(
                "SelectedHeadUnavailable",
                "The selected Timeline head row is unavailable."
            );
        }
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
        TimelineHeadRef after = _ledger.ReadSnapshot();
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

    public HistoryTimelineOfflineBuilderOpenResult OpenOfflineBuilder(
        TimelineHeadRef expectedWholeHead,
        SJ.SessionSelectedLineageForwardCursor cursor
    ) {
        ArgumentNullException.ThrowIfNull(expectedWholeHead);
        ArgumentNullException.ThrowIfNull(cursor);
        TimelineHeadRef actual = _ledger.ReadSnapshot();
        if (actual != expectedWholeHead) {
            return new HistoryTimelineOfflineBuilderOpenResult
                .StaleTimelineHead(actual);
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
                return new HistoryTimelineOfflineBuilderOpenResult
                    .Invalid(
                        "OfflineSelectedHeadUnavailable",
                        "The exact selected Timeline head row is unavailable."
                    );
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
            new HistoryTimelineOfflineBuilder(this, cursor)
        );
    }

    public HistoryTimelineReconcileResult ReconcileSelectedPathOffline(
        TimelineHeadRef expectedWholeHead,
        SJ.SessionSelectedLineageForwardCursor cursor,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(expectedWholeHead);
        ArgumentNullException.ThrowIfNull(cursor);
        TimelineHeadRef actual = _ledger.ReadSnapshot();
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
        SelectedHistoryBoundaryResult.Invalid? invalid = null;
        try {
            SJ.SessionSelectedLineageBoundaryProbeResult scan =
                cursor.ProbeBoundaries(
                    address => {
                        _testHooks
                            .BeforeOfflineReconcileBoundaryProbe
                            ?.Invoke(address);
                        SelectedHistoryBoundaryResult lookup =
                            _ledger.ReadSelectedRowAtBoundary(
                                expectedWholeHead,
                                address
                            );
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

        TimelineHeadRef afterScan = _ledger.ReadSnapshot();
        if (afterScan != expectedWholeHead) {
            return new HistoryTimelineReconcileResult
                .StaleTimelineHead(afterScan);
        }
        return _ledger.ReconcileSelectedPath(
            new HistoryTimelineReconcileCandidate(
                expectedWholeHead,
                selectedRowId,
                new OfflineSelectedRawCursorFence(cursor)
            )
        );
    }

    private HistoryTimelineReconcileResult ReconcileCaptured(
        TimelineHeadRef expectedWholeHead,
        OnlineSelectedRawCapture capture
    ) {
        HistoryRowId? selectedRowId = null;
        if (expectedWholeHead.HeadRowId is { } headRowId) {
            HistorySegmentDescriptor? selectedHead =
                _ledger.ReadRow(headRowId);
            if (selectedHead is null) {
                return new HistoryTimelineReconcileResult.Invalid(
                    "SelectedPathRowUnavailable",
                    "The selected Timeline head row is unavailable."
                );
            }
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
                    foreach (SJ.SessionCurrentLineageHeader header
                             in capture.Prefix.HeadToOldest) {
                        SelectedHistoryBoundaryResult lookup =
                            _ledger.ReadSelectedRowAtBoundary(
                                expectedWholeHead,
                                header.Address
                            );
                        switch (lookup) {
                            case SelectedHistoryBoundaryResult.Found found:
                                selectedRowId = found.Descriptor.RowId;
                                break;
                            case SelectedHistoryBoundaryResult
                                .StaleTimelineHead stale:
                                return new HistoryTimelineReconcileResult
                                    .StaleTimelineHead(stale.Actual);
                            case SelectedHistoryBoundaryResult.Invalid invalid:
                                return new HistoryTimelineReconcileResult
                                    .Invalid(
                                        invalid.Code,
                                        invalid.Detail
                                    );
                        }
                        if (selectedRowId is not null) {
                            break;
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

    private static StringComparison RepositoryPathComparison
        => OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    internal PartitionPolicyRevision? ReadPolicyForOffline(
        string policyDigest
    ) => _ledger.ReadPolicy(policyDigest);

    internal HistorySegmentDescriptor? ReadRowForOffline(
        HistoryRowId rowId
    ) => _ledger.ReadRow(rowId);

    internal IHistoryUnitLoadEstimator? ResolveEstimatorForOffline(
        string estimatorId
    ) => _estimators.Resolve(estimatorId);
}
