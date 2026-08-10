using System.Collections.Immutable;
using Atelia.EventJournal;

namespace Atelia.SessionJournal.HistoryTimeline;

/// <summary>
/// WP-01B semantic ledger. It deliberately has no durability behavior; WP-01C
/// replaces only this carrier while preserving these whole-head operations.
/// </summary>
internal sealed class InMemoryHistoryTimelineLedger
    : IHistoryTimelineLedgerPort {
    private readonly object _gate = new();
    private readonly Dictionary<string, PartitionPolicyRevision>
        _policies = new(StringComparer.Ordinal);
    private readonly Dictionary<HistoryRowId, HistorySegmentDescriptor>
        _rows = [];
    private readonly Dictionary<HistoryRowId, SelectedPathSnapshot>
        _pathSnapshotsByHead = [];
    private SelectedPathSnapshot _selectedPath =
        SelectedPathSnapshot.Empty;
    private long _selectedPathRowProbeCount;
    private long _selectedPathBoundaryProbeCount;
    private long _selectedPathSwitchCount;
    private TimelineHeadRef _head;

    public InMemoryHistoryTimelineLedger(
        RefId refId,
        PartitionPolicyRevision initialPolicy
    ) {
        ArgumentNullException.ThrowIfNull(initialPolicy);
        _policies.Add(initialPolicy.PolicyDigest, initialPolicy);
        _head = new TimelineHeadRef(
            initialPolicy.TimelineId,
            refId,
            headRowId: null,
            initialPolicy.PolicyDigest,
            selectedRawHeadAtCommit: null,
            generation: 0
        );
    }

    public TimelineHeadRef ReadSnapshot() {
        lock (_gate) {
            return _head;
        }
    }

    internal long SelectedPathRowProbeCount {
        get {
            lock (_gate) {
                return _selectedPathRowProbeCount;
            }
        }
    }

    internal long SelectedPathBoundaryProbeCount {
        get {
            lock (_gate) {
                return _selectedPathBoundaryProbeCount;
            }
        }
    }

    internal long SelectedPathSwitchCount {
        get {
            lock (_gate) {
                return _selectedPathSwitchCount;
            }
        }
    }

    public PartitionPolicyRevision? ReadPolicy(string policyDigest) {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyDigest);
        lock (_gate) {
            return _policies.GetValueOrDefault(policyDigest);
        }
    }

    public HistorySegmentDescriptor? ReadRow(HistoryRowId rowId) {
        lock (_gate) {
            return _rows.GetValueOrDefault(rowId);
        }
    }

    public HistoryTimelinePolicyPutResult PutPolicy(
        PartitionPolicyRevision policy
    ) {
        ArgumentNullException.ThrowIfNull(policy);
        lock (_gate) {
            if (policy.TimelineId != _head.TimelineId) {
                return new HistoryTimelinePolicyPutResult.Invalid(
                    "TimelineMismatch",
                    "A partition policy belongs to another Timeline."
                );
            }
            if (_policies.TryGetValue(
                    policy.PolicyDigest,
                    out PartitionPolicyRevision? existing)) {
                return existing.ToCanonicalBytes().AsSpan()
                    .SequenceEqual(policy.ToCanonicalBytes())
                    ? new HistoryTimelinePolicyPutResult.AlreadyPresent()
                    : new HistoryTimelinePolicyPutResult.Invalid(
                        "PolicyDigestCollision",
                        "The policy digest is already bound to different canonical bytes."
                    );
            }
            _policies.Add(policy.PolicyDigest, policy);
            return new HistoryTimelinePolicyPutResult.Stored();
        }
    }

    public HistoryTimelinePolicyCasResult CompareExchangePolicy(
        TimelineHeadRef expectedWholeHead,
        string nextPolicyDigest
    ) {
        ArgumentNullException.ThrowIfNull(expectedWholeHead);
        ArgumentException.ThrowIfNullOrWhiteSpace(nextPolicyDigest);
        lock (_gate) {
            if (_head != expectedWholeHead) {
                return new HistoryTimelinePolicyCasResult
                    .StaleTimelineHead(_head);
            }
            if (!_policies.TryGetValue(
                    nextPolicyDigest,
                    out PartitionPolicyRevision? nextPolicy)
                || nextPolicy.TimelineId != _head.TimelineId) {
                return new HistoryTimelinePolicyCasResult
                    .PartitionPolicyUnavailable(nextPolicyDigest);
            }
            long nextGeneration;
            try {
                nextGeneration = checked(_head.Generation + 1);
            }
            catch (OverflowException exception) {
                return new HistoryTimelinePolicyCasResult.Invalid(
                    "GenerationOverflow",
                    exception.Message
                );
            }
            var next = new TimelineHeadRef(
                _head.TimelineId,
                _head.RefId,
                _head.HeadRowId,
                nextPolicyDigest,
                _head.SelectedRawHeadAtCommit,
                nextGeneration
            );
            _head = next;
            return new HistoryTimelinePolicyCasResult.Applied(next);
        }
    }

    public HistoryTimelineCommitResult CommitRow(
        HistoryRowCommitCandidate candidate
    ) {
        ArgumentNullException.ThrowIfNull(candidate);
        lock (_gate) {
            HistoryRowProposal proposal = candidate.Proposal;
            if (_head != proposal.ExpectedHead) {
                return new HistoryTimelineCommitResult
                    .StaleTimelineHead(_head);
            }
            HistorySegmentDescriptor descriptor = proposal.Descriptor;
            if (candidate.RawFence.RefId != _head.RefId
                || candidate.RawFence.CapturedHead
                    != proposal.CapturedSelectedRawHead
                || descriptor.TimelineId != _head.TimelineId
                || descriptor.RefId != _head.RefId
                || descriptor.PreviousRowId != _head.HeadRowId) {
                return new HistoryTimelineCommitResult.Invalid(
                    "CommitCandidateMismatch",
                    "The opaque candidate does not extend the exact ledger/raw scope."
                );
            }
            if (!_policies.TryGetValue(
                    descriptor.PartitionPolicyDigestAtCreation,
                    out PartitionPolicyRevision? policy)
                || policy.TimelineId != _head.TimelineId) {
                return new HistoryTimelineCommitResult
                    .PartitionPolicyUnavailable(
                        descriptor.PartitionPolicyDigestAtCreation
                    );
            }
            if (!string.Equals(
                    _head.ActivePartitionPolicyDigest,
                    descriptor.PartitionPolicyDigestAtCreation,
                    StringComparison.Ordinal)) {
                return new HistoryTimelineCommitResult
                    .StaleTimelineHead(_head);
            }

            EventAddress? observedRawHead =
                candidate.RawFence.ReadCurrentHead();
            if (observedRawHead
                != proposal.CapturedSelectedRawHead) {
                return new HistoryTimelineCommitResult.RawHeadChanged(
                    proposal.CapturedSelectedRawHead,
                    observedRawHead
                );
            }
            if (_rows.TryGetValue(
                    descriptor.RowId,
                    out HistorySegmentDescriptor? existing)
                && !existing.ToCanonicalBytes().AsSpan().SequenceEqual(
                    proposal.CanonicalDescriptorBytes.Span
                )) {
                return new HistoryTimelineCommitResult.Invalid(
                    "RowIdCollision",
                    "The row ID is already bound to different canonical bytes."
                );
            }

            long nextGeneration;
            try {
                nextGeneration = checked(_head.Generation + 1);
            }
            catch (OverflowException exception) {
                return new HistoryTimelineCommitResult.Invalid(
                    "GenerationOverflow",
                    exception.Message
                );
            }
            var next = new TimelineHeadRef(
                _head.TimelineId,
                _head.RefId,
                descriptor.RowId,
                _head.ActivePartitionPolicyDigest,
                proposal.CapturedSelectedRawHead,
                nextGeneration
            );
            if (_selectedPath.RowsByEnd.TryGetValue(
                    descriptor.EndInclusive,
                    out HistorySegmentDescriptor? existingSelected)
                && existingSelected.RowId != descriptor.RowId) {
                return new HistoryTimelineCommitResult.Invalid(
                    "SelectedBoundaryCollision",
                    "The selected path already binds this raw boundary to another row."
                );
            }
            SelectedPathSnapshot nextPath;
            if (_pathSnapshotsByHead.TryGetValue(
                    descriptor.RowId,
                    out SelectedPathSnapshot? existingPath)) {
                if (!existingPath.RowsById.TryGetValue(
                        descriptor.RowId,
                        out HistorySegmentDescriptor? existingDescriptor)
                    || !existingDescriptor.ToCanonicalBytes().AsSpan()
                        .SequenceEqual(
                            descriptor.ToCanonicalBytes()
                        )) {
                    return new HistoryTimelineCommitResult.Invalid(
                        "SelectedPathSnapshotCollision",
                        "The row ID is already bound to a different selected-path snapshot."
                    );
                }
                nextPath = existingPath;
            }
            else {
                nextPath = _selectedPath.Append(descriptor);
            }
            _rows.TryAdd(descriptor.RowId, descriptor);
            _pathSnapshotsByHead.TryAdd(
                descriptor.RowId,
                nextPath
            );
            _selectedPath = nextPath;
            _head = next;
            return new HistoryTimelineCommitResult.Committed(next);
        }
    }

    public SelectedHistoryRowResult ReadSelectedRow(
        TimelineHeadRef expectedWholeHead,
        HistoryRowId rowId
    ) {
        ArgumentNullException.ThrowIfNull(expectedWholeHead);
        lock (_gate) {
            if (_head != expectedWholeHead) {
                return new SelectedHistoryRowResult
                    .StaleTimelineHead(_head);
            }
            return ReadSelectedRowUnderLock(
                expectedWholeHead,
                rowId
            );
        }
    }

    public HistoryTimelineReconcileResult ReconcileSelectedPath(
        HistoryTimelineReconcileCandidate candidate
    ) {
        ArgumentNullException.ThrowIfNull(candidate);
        lock (_gate) {
            if (_head != candidate.ExpectedHead) {
                return new HistoryTimelineReconcileResult
                    .StaleTimelineHead(_head);
            }
            if (candidate.RawFence.RefId != _head.RefId
                || candidate.ExpectedHead.TimelineId
                    != _head.TimelineId
                || candidate.ExpectedHead.RefId != _head.RefId) {
                return new HistoryTimelineReconcileResult.Invalid(
                    "ReconcileScopeMismatch",
                    "The reconciliation candidate belongs to another Timeline or raw Ref."
                );
            }
            if (candidate.SelectedRowId is { } selectedRowId) {
                SelectedHistoryRowResult membership =
                    ReadSelectedRowUnderLock(
                        candidate.ExpectedHead,
                        selectedRowId
                    );
                if (membership
                    is not SelectedHistoryRowResult.Selected) {
                    return new HistoryTimelineReconcileResult.Invalid(
                        "ReconcileTargetNotSelected",
                        "The reconciliation target is not on the exact expected selected path."
                    );
                }
                if (candidate.RawFence.CapturedHead is null) {
                    return new HistoryTimelineReconcileResult.Invalid(
                        "ReconcileRawHeadMissing",
                        "A non-empty selected Timeline target requires a captured raw head."
                    );
                }
            }
            SelectedPathSnapshot nextPath;
            if (candidate.SelectedRowId is { } targetRowId) {
                if (!_pathSnapshotsByHead.TryGetValue(
                        targetRowId,
                        out SelectedPathSnapshot? targetPath)
                    || !_selectedPath.RowsById.ContainsKey(
                        targetRowId)) {
                    return new HistoryTimelineReconcileResult.Invalid(
                        "ReconcileTargetSnapshotUnavailable",
                        "The reconciliation target has no exact selected-path snapshot."
                    );
                }
                nextPath = targetPath;
            }
            else {
                nextPath = SelectedPathSnapshot.Empty;
            }

            EventAddress? observedRawHead =
                candidate.RawFence.ReadCurrentHead();
            if (observedRawHead != candidate.RawFence.CapturedHead) {
                return new HistoryTimelineReconcileResult.RawHeadChanged(
                    candidate.RawFence.CapturedHead,
                    observedRawHead
                );
            }
            EventAddress? selectedFence =
                candidate.SelectedRowId is null
                    ? null
                    : candidate.RawFence.CapturedHead;
            if (_head.HeadRowId == candidate.SelectedRowId
                && _head.SelectedRawHeadAtCommit == selectedFence) {
                return new HistoryTimelineReconcileResult.Unchanged(
                    _head
                );
            }

            long nextGeneration;
            try {
                nextGeneration = checked(_head.Generation + 1);
            }
            catch (OverflowException exception) {
                return new HistoryTimelineReconcileResult.Invalid(
                    "GenerationOverflow",
                    exception.Message
                );
            }
            var next = new TimelineHeadRef(
                _head.TimelineId,
                _head.RefId,
                candidate.SelectedRowId,
                _head.ActivePartitionPolicyDigest,
                selectedFence,
                nextGeneration
            );
            _selectedPath = nextPath;
            if (_selectedPathSwitchCount < long.MaxValue) {
                _selectedPathSwitchCount++;
            }
            _head = next;
            return new HistoryTimelineReconcileResult.Reconciled(next);
        }
    }

    public SelectedHistoryBoundaryResult ReadSelectedRowAtBoundary(
        TimelineHeadRef expectedWholeHead,
        EventAddress endInclusive
    ) {
        ArgumentNullException.ThrowIfNull(expectedWholeHead);
        lock (_gate) {
            if (_head != expectedWholeHead) {
                return new SelectedHistoryBoundaryResult
                    .StaleTimelineHead(_head);
            }
            if (_selectedPathBoundaryProbeCount < long.MaxValue) {
                _selectedPathBoundaryProbeCount++;
            }
            if (!_selectedPath.RowsByEnd.TryGetValue(
                    endInclusive,
                    out HistorySegmentDescriptor? descriptor)) {
                return new SelectedHistoryBoundaryResult.NotFound();
            }
            return new SelectedHistoryBoundaryResult.Found(
                descriptor
            );
        }
    }

    private SelectedHistoryRowResult ReadSelectedRowUnderLock(
        TimelineHeadRef expectedWholeHead,
        HistoryRowId requiredRowId
    ) {
        if (_selectedPathRowProbeCount < long.MaxValue) {
            _selectedPathRowProbeCount++;
        }
        if (_selectedPath.RowsById.TryGetValue(
                requiredRowId,
                out HistorySegmentDescriptor? descriptor)) {
            return new SelectedHistoryRowResult.Selected(
                descriptor
            );
        }
        return new SelectedHistoryRowResult.NotOnSelectedPath(
            requiredRowId
        );
    }

    private sealed record SelectedPathSnapshot(
        ImmutableDictionary<
            HistoryRowId,
            HistorySegmentDescriptor
        > RowsById,
        ImmutableDictionary<
            EventAddress,
            HistorySegmentDescriptor
        > RowsByEnd
    ) {
        internal static SelectedPathSnapshot Empty { get; } = new(
            ImmutableDictionary<
                HistoryRowId,
                HistorySegmentDescriptor
            >.Empty,
            ImmutableDictionary<
                EventAddress,
                HistorySegmentDescriptor
            >.Empty
        );

        internal SelectedPathSnapshot Append(
            HistorySegmentDescriptor descriptor
        ) => new(
            RowsById.Add(descriptor.RowId, descriptor),
            RowsByEnd.Add(descriptor.EndInclusive, descriptor)
        );
    }
}
