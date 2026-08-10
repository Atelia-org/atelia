using Atelia.EventJournal;
using SJ = Atelia.SessionJournal;

namespace Atelia.SessionJournal.HistoryTimeline;

public abstract record OnlineSelectedRawCaptureResult {
    private OnlineSelectedRawCaptureResult() { }

    public sealed record Empty : OnlineSelectedRawCaptureResult {
        internal Empty(
            RefId refId,
            IHistoryTimelineRawFence rawFence,
            TimelineHeadRef expectedTimelineHead
        ) {
            RefId = refId;
            RawFence = rawFence;
            ExpectedTimelineHead = expectedTimelineHead;
        }

        public RefId RefId { get; }
        internal IHistoryTimelineRawFence RawFence { get; }
        internal TimelineHeadRef ExpectedTimelineHead { get; }
    }

    public sealed record Captured(OnlineSelectedRawCapture Capture)
        : OnlineSelectedRawCaptureResult;

    public sealed record StaleTimelineHead(TimelineHeadRef Actual)
        : OnlineSelectedRawCaptureResult;

    public sealed record PartitionPolicyUnavailable(string PolicyDigest)
        : OnlineSelectedRawCaptureResult;

    public sealed record Invalid(string Code, string Detail)
        : OnlineSelectedRawCaptureResult;
}

internal interface IHistoryTimelineLedgerPort {
    TimelineHeadRef ReadSnapshot();
    PartitionPolicyRevision? ReadPolicy(string policyDigest);
    HistorySegmentDescriptor? ReadRow(HistoryRowId rowId);
    HistoryTimelinePolicyPutResult PutPolicy(
        PartitionPolicyRevision policy
    );
    HistoryTimelinePolicyCasResult CompareExchangePolicy(
        TimelineHeadRef expectedWholeHead,
        string nextPolicyDigest
    );
    HistoryTimelineCommitResult CommitRow(
        HistoryRowCommitCandidate candidate
    );
    SelectedHistoryRowResult ReadSelectedRow(
        TimelineHeadRef expectedWholeHead,
        HistoryRowId rowId
    );
    SelectedHistoryBoundaryResult ReadSelectedRowAtBoundary(
        TimelineHeadRef expectedWholeHead,
        EventAddress endInclusive
    );
    HistoryTimelineReconcileResult ReconcileSelectedPath(
        HistoryTimelineReconcileCandidate candidate
    );
}

/// <summary>
/// Opaque online binding to one SessionJournal read view, exact Ref, raw
/// head, and repository-produced bounded Parent prefix.
/// </summary>
public sealed class OnlineSelectedRawCapture : IHistoryTimelineRawFence {
    private readonly SJ.SessionJournalReadView _readView;

    internal OnlineSelectedRawCapture(
        SJ.SessionJournalReadView readView,
        RefId refId,
        EventAddress capturedHead,
        SJ.SessionCurrentLineagePrefix prefix,
        TimelineHeadRef expectedTimelineHead
    ) {
        _readView = readView;
        RefId = refId;
        CapturedHead = capturedHead;
        Prefix = prefix;
        ExpectedTimelineHead = expectedTimelineHead;
    }

    public RefId RefId { get; }
    public EventAddress CapturedHead { get; }
    internal SJ.SessionCurrentLineagePrefix Prefix { get; }
    internal SJ.SessionJournalReadView ReadView => _readView;
    internal TimelineHeadRef ExpectedTimelineHead { get; }

    EventAddress? IHistoryTimelineRawFence.CapturedHead
        => CapturedHead;

    EventAddress? IHistoryTimelineRawFence.ReadCurrentHead()
        => _readView.ReadCurrentHead();
}

internal interface IHistoryTimelineRawFence {
    RefId RefId { get; }
    EventAddress? CapturedHead { get; }
    EventAddress? ReadCurrentHead();
}

/// <summary>
/// Non-forgeable commit input. The proposal remains a canonical semantic
/// value; raw promotion authority stays in the internal repository-bound
/// fence.
/// </summary>
public sealed class HistoryRowCommitCandidate {
    internal HistoryRowCommitCandidate(
        HistoryRowProposal proposal,
        IHistoryTimelineRawFence rawFence
    ) {
        Proposal = proposal;
        RawFence = rawFence;
    }

    public HistoryRowProposal Proposal { get; }
    internal IHistoryTimelineRawFence RawFence { get; }
}

internal sealed class EmptySelectedRawFence
    : IHistoryTimelineRawFence {
    private readonly SJ.SessionJournalReadView _readView;

    internal EmptySelectedRawFence(
        SJ.SessionJournalReadView readView,
        RefId refId
    ) {
        _readView = readView;
        RefId = refId;
    }

    public RefId RefId { get; }
    public EventAddress? CapturedHead => null;
    public EventAddress? ReadCurrentHead()
        => _readView.ReadCurrentHead();
}

internal sealed class OfflineSelectedRawCursorFence(
    SJ.SessionSelectedLineageForwardCursor cursor
) : IHistoryTimelineRawFence {
    public RefId RefId => cursor.Authority.Capture.BranchRefId;
    public EventAddress? CapturedHead
        => cursor.Authority.Capture.CapturedHead;
    public EventAddress? ReadCurrentHead()
        => cursor.ReadCurrentHead();
}

internal sealed class HistoryTimelineReconcileCandidate {
    internal HistoryTimelineReconcileCandidate(
        TimelineHeadRef expectedHead,
        HistoryRowId? selectedRowId,
        IHistoryTimelineRawFence rawFence
    ) {
        ExpectedHead = expectedHead;
        SelectedRowId = selectedRowId;
        RawFence = rawFence;
    }

    internal TimelineHeadRef ExpectedHead { get; }
    internal HistoryRowId? SelectedRowId { get; }
    internal IHistoryTimelineRawFence RawFence { get; }
}

public sealed class HistorySegmentContent {
    internal HistorySegmentContent(
        HistorySegmentDescriptor descriptor,
        SJ.SessionHistoryPlanningWindow window
    ) {
        Descriptor = descriptor;
        Window = window;
    }

    public HistorySegmentDescriptor Descriptor { get; }
    public SJ.SessionHistoryPlanningWindow Window { get; }
}

public abstract record SelectedHistoryRowResult {
    private SelectedHistoryRowResult() { }

    public sealed record Selected(HistorySegmentDescriptor Descriptor)
        : SelectedHistoryRowResult;

    public sealed record NotOnSelectedPath(HistoryRowId RowId)
        : SelectedHistoryRowResult;

    public sealed record StaleTimelineHead(TimelineHeadRef Actual)
        : SelectedHistoryRowResult;

    public sealed record Invalid(string Code, string Detail)
        : SelectedHistoryRowResult;
}

internal abstract record SelectedHistoryBoundaryResult {
    private SelectedHistoryBoundaryResult() { }

    public sealed record Found(HistorySegmentDescriptor Descriptor)
        : SelectedHistoryBoundaryResult;

    public sealed record NotFound : SelectedHistoryBoundaryResult;

    public sealed record StaleTimelineHead(TimelineHeadRef Actual)
        : SelectedHistoryBoundaryResult;

    public sealed record Invalid(string Code, string Detail)
        : SelectedHistoryBoundaryResult;
}

public abstract record HistorySegmentOpenResult {
    private HistorySegmentOpenResult() { }

    public sealed record Opened(HistorySegmentContent Content)
        : HistorySegmentOpenResult;

    public sealed record NotOnSelectedPath(HistoryRowId RowId)
        : HistorySegmentOpenResult;

    public sealed record OfflineBootstrapRequired(
        SJ.SessionCurrentLineageBeyondPrefix Evidence
    ) : HistorySegmentOpenResult;

    public sealed record OffLineage(
        EventAddress RequiredAnchor,
        EventAddress CapturedHead
    ) : HistorySegmentOpenResult;

    public sealed record RawHeadChanged(
        EventAddress Expected,
        EventAddress? Observed
    ) : HistorySegmentOpenResult;

    public sealed record StaleTimelineHead(TimelineHeadRef Actual)
        : HistorySegmentOpenResult;

    public sealed record PartitionPolicyUnavailable(string PolicyDigest)
        : HistorySegmentOpenResult;

    public sealed record HistoryLoadEstimatorUnavailable(string EstimatorId)
        : HistorySegmentOpenResult;

    public sealed record PartitionAlgorithmUnavailable(string AlgorithmId)
        : HistorySegmentOpenResult;

    public sealed record Invalid(string Code, string Detail)
        : HistorySegmentOpenResult;
}

public abstract record HistoryTimelineReconcileResult {
    private HistoryTimelineReconcileResult() { }

    public sealed record Unchanged(TimelineHeadRef Head)
        : HistoryTimelineReconcileResult;

    public sealed record Reconciled(TimelineHeadRef Head)
        : HistoryTimelineReconcileResult;

    public sealed record OfflineBootstrapRequired(
        SJ.SessionCurrentLineageBeyondPrefix Evidence
    ) : HistoryTimelineReconcileResult;

    public sealed record RawHeadChanged(
        EventAddress? Expected,
        EventAddress? Observed
    ) : HistoryTimelineReconcileResult;

    public sealed record StaleTimelineHead(TimelineHeadRef Actual)
        : HistoryTimelineReconcileResult;

    public sealed record PartitionPolicyUnavailable(string PolicyDigest)
        : HistoryTimelineReconcileResult;

    public sealed record Invalid(string Code, string Detail)
        : HistoryTimelineReconcileResult;
}

public abstract record HistoryTimelineOfflineBuilderOpenResult {
    private HistoryTimelineOfflineBuilderOpenResult() { }

    public sealed record Opened(HistoryTimelineOfflineBuilder Builder)
        : HistoryTimelineOfflineBuilderOpenResult;

    public sealed record RawHeadChanged(
        EventAddress Expected,
        EventAddress? Observed
    ) : HistoryTimelineOfflineBuilderOpenResult;

    public sealed record StaleTimelineHead(TimelineHeadRef Actual)
        : HistoryTimelineOfflineBuilderOpenResult;

    public sealed record Invalid(string Code, string Detail)
        : HistoryTimelineOfflineBuilderOpenResult;
}

public abstract record HistoryTimelineOfflineStepResult {
    private HistoryTimelineOfflineStepResult() { }

    public sealed record Committed(
        HistorySegmentDescriptor Descriptor,
        TimelineHeadRef Head
    ) : HistoryTimelineOfflineStepResult;

    public sealed record NotEnough(
        HistoryPartitionResult.NotEnough Partition
    ) : HistoryTimelineOfflineStepResult;

    public sealed record LimitExceeded(
        HistoryPartitionResult.LimitExceeded Partition
    ) : HistoryTimelineOfflineStepResult;

    public sealed record RawHeadChanged(
        EventAddress Expected,
        EventAddress? Observed
    ) : HistoryTimelineOfflineStepResult;

    public sealed record StaleTimelineHead(TimelineHeadRef Actual)
        : HistoryTimelineOfflineStepResult;

    public sealed record PartitionPolicyUnavailable(string PolicyDigest)
        : HistoryTimelineOfflineStepResult;

    public sealed record HistoryLoadEstimatorUnavailable(string EstimatorId)
        : HistoryTimelineOfflineStepResult;

    public sealed record PartitionAlgorithmUnavailable(string AlgorithmId)
        : HistoryTimelineOfflineStepResult;

    public sealed record Invalid(string Code, string Detail)
        : HistoryTimelineOfflineStepResult;
}

public abstract record HistoryTimelinePlanResult {
    private HistoryTimelinePlanResult() { }

    public sealed record Selected(HistoryRowCommitCandidate Candidate)
        : HistoryTimelinePlanResult;

    public sealed record NotEnough(
        HistoryPartitionResult.NotEnough Partition
    ) : HistoryTimelinePlanResult;

    public sealed record LimitExceeded(
        HistoryPartitionResult.LimitExceeded Partition
    ) : HistoryTimelinePlanResult;

    public sealed record OfflineBootstrapRequired(
        SJ.SessionCurrentLineageBeyondPrefix Evidence
    ) : HistoryTimelinePlanResult;

    public sealed record OffLineage(
        EventAddress RequiredAnchor,
        EventAddress CapturedHead
    ) : HistoryTimelinePlanResult;

    public sealed record RawHeadChanged(
        EventAddress Expected,
        EventAddress? Observed
    ) : HistoryTimelinePlanResult;

    public sealed record StaleTimelineHead(TimelineHeadRef Actual)
        : HistoryTimelinePlanResult;

    public sealed record PartitionPolicyUnavailable(string PolicyDigest)
        : HistoryTimelinePlanResult;

    public sealed record HistoryLoadEstimatorUnavailable(string EstimatorId)
        : HistoryTimelinePlanResult;

    public sealed record PartitionAlgorithmUnavailable(string AlgorithmId)
        : HistoryTimelinePlanResult;

    public sealed record Invalid(string Code, string Detail)
        : HistoryTimelinePlanResult;
}

public abstract record HistoryTimelinePolicyCasResult {
    private HistoryTimelinePolicyCasResult() { }

    public sealed record Applied(TimelineHeadRef Head)
        : HistoryTimelinePolicyCasResult;

    public sealed record StaleTimelineHead(TimelineHeadRef Actual)
        : HistoryTimelinePolicyCasResult;

    public sealed record PartitionPolicyUnavailable(string PolicyDigest)
        : HistoryTimelinePolicyCasResult;

    public sealed record Invalid(string Code, string Detail)
        : HistoryTimelinePolicyCasResult;
}

public abstract record HistoryTimelinePolicyPutResult {
    private HistoryTimelinePolicyPutResult() { }

    public sealed record Stored : HistoryTimelinePolicyPutResult;
    public sealed record AlreadyPresent : HistoryTimelinePolicyPutResult;
    public sealed record Invalid(string Code, string Detail)
        : HistoryTimelinePolicyPutResult;
}

public abstract record HistoryTimelineCommitResult {
    private HistoryTimelineCommitResult() { }

    public sealed record Committed(TimelineHeadRef Head)
        : HistoryTimelineCommitResult;

    public sealed record StaleTimelineHead(TimelineHeadRef Actual)
        : HistoryTimelineCommitResult;

    public sealed record RawHeadChanged(
        EventAddress Expected,
        EventAddress? Observed
    ) : HistoryTimelineCommitResult;

    public sealed record PartitionPolicyUnavailable(string PolicyDigest)
        : HistoryTimelineCommitResult;

    public sealed record Invalid(string Code, string Detail)
        : HistoryTimelineCommitResult;
}
