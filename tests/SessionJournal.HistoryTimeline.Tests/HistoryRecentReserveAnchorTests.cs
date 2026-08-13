using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.SessionJournal;
using Atelia.SessionJournal.HistoryTimeline;

namespace Atelia.SessionJournal.HistoryTimeline.Tests;

public sealed class HistoryRecentReserveAnchorTests : IDisposable {
    private readonly List<string> _paths = [];
    private readonly FixedEstimator _estimator = new();

    [Fact]
    public void CrossesMultipleRowsBeyondPartitionPrefixAndFindsLatestAnchor() {
        using Fixture fixture = CreateFixture(turns: 3);
        HistoryRecentReserveAnchorResult.Eligible result = Assert.IsType<
            HistoryRecentReserveAnchorResult.Eligible>(
                fixture.Session.FindRecentReserveAnchor(
                    fixture.Head,
                    fixture.RawHead,
                    Requirement(fixture.Policy, minimumRecent: 3)));

        Assert.Equal(
            fixture.Rows[0].RowId,
            result.Anchor.Descriptor.RowId);
        Assert.Equal(3, result.HeadThroughAnchorRowCount);
        Assert.Equal(3, result.RetainedHistoryLoad.Value);
        Assert.Equal(3, result.Metrics.ExaminedTimelineRows);
        Assert.Equal(4, result.Metrics.ExaminedRawEvents);
    }

    [Fact]
    public void ExactGlobalRawCapSucceedsAndCapPlusOneIsTypedLimit() {
        using Fixture exact = CreateFixture(
            turns: 3,
            recentReserveLimits: new(MaximumRawEvents: 2));
        HistoryRecentReserveAnchorResult.Eligible eligible = Assert.IsType<
            HistoryRecentReserveAnchorResult.Eligible>(
                exact.Session.FindRecentReserveAnchor(
                    exact.Head,
                    exact.RawHead,
                    Requirement(exact.Policy, minimumRecent: 2)));
        Assert.Equal(2, eligible.Metrics.ExaminedRawEvents);

        using Fixture capped = CreateFixture(
            turns: 3,
            recentReserveLimits: new(MaximumRawEvents: 1));
        HistoryRecentReserveAnchorResult.LimitExceeded limit = Assert.IsType<
            HistoryRecentReserveAnchorResult.LimitExceeded>(
                capped.Session.FindRecentReserveAnchor(
                    capped.Head,
                    capped.RawHead,
                    Requirement(capped.Policy, minimumRecent: 2)));
        Assert.Equal(
            nameof(HistoryRecentReserveOperationLimits.MaximumRawEvents),
            limit.Limit);
        Assert.Equal(0, limit.Metrics.ExaminedRawEvents);
    }

    [Fact]
    public void BuildReadCaptureAcceptsExactRawTailAndTypesCapPlusOne() {
        using Fixture exact = CreateFixture(
            turns: 1,
            rawCaptureLimit: 2,
            tailTurns: 1);
        Assert.IsType<OnlineSelectedRawCaptureResult.Captured>(
            exact.Session.CaptureRaw(exact.Head));

        using Fixture capped = CreateFixture(
            turns: 1,
            rawCaptureLimit: 1,
            tailTurns: 1);
        OnlineSelectedRawCaptureResult.LimitExceeded limit = Assert.IsType<
            OnlineSelectedRawCaptureResult.LimitExceeded>(
                capped.Session.CaptureRaw(capped.Head));
        Assert.Equal(
            nameof(HistoryRecentReserveOperationLimits.MaximumRawEvents),
            limit.Limit);
    }

    [Fact]
    public void HealthyRowsWithoutEligiblePredecessorAreBootstrapRequired() {
        using Fixture fixture = CreateFixture(turns: 3);
        var result = Assert.IsType<
            HistoryRecentReserveAnchorResult.ReserveBootstrapRequired>(
                fixture.Session.FindRecentReserveAnchor(
                    fixture.Head,
                    fixture.RawHead,
                    Requirement(fixture.Policy, minimumRecent: 5)));

        Assert.Equal(3, result.HeadThroughRootRowCount);
        Assert.Equal(4, result.RetainedHistoryLoad.Value);
        Assert.Equal(4, result.Metrics.ExaminedRawEvents);
    }

    [Fact]
    public void RequirementMustMatchExactActivePolicyAndEstimator() {
        using Fixture fixture = CreateFixture(turns: 1);
        var mismatch = Assert.IsType<
            HistoryRecentReserveAnchorResult.Invalid>(
                fixture.Session.FindRecentReserveAnchor(
                    fixture.Head,
                    fixture.RawHead,
                    new HistoryRecentReserveRequirement(
                        fixture.Policy.PolicyDigest,
                        "test.history-load.other.v1",
                        new HistoryLoadUnit(1))));

        Assert.Equal("RecentReservePolicyMismatch", mismatch.Code);
    }

    private Fixture CreateFixture(
        int turns,
        HistoryRecentReserveAnchorReadLimits? recentReserveLimits = null,
        int? rawCaptureLimit = null,
        int tailTurns = 0
    ) {
        string path = NewPath();
        var writer = SessionJournalEngine.Create(
            path,
            new SessionCreateOptions("model", "system", "reserve-anchor"));
        var initial = new HistoryTimelineInitialPolicySpec(
            HistoryPartitionAlgorithms.FirstReplaySafeBoundaryAtTargetV1,
            _estimator.Id,
            new HistoryLoadUnit(2),
            maxRawEvents: 2,
            maxRenderedBytes: 1024 * 1024);
        Assert.IsType<HistoryTimelineCreateResult.Created>(
            HistoryTimelineFactory.Create(
                writer.ReadView,
                initial,
                _estimator));
        HistoryTimelineHandle handle = Assert.IsType<
            HistoryTimelineOpenResult.Opened>(
                HistoryTimelineFactory.Open(
                    writer.ReadView,
                    _estimator)).Handle;
        var rows = new List<HistorySegmentDescriptor>();
        for (int index = 0; index < turns; index++) {
            _ = writer.AppendObservation($"observation-{index}");
            _ = writer.AppendImportedAgentAction(
                new ActionMessage([
                    new ActionBlock.Text($"answer-{index}")
                ]),
                new CompletionDescriptor("import", "v1", "model"));
            TimelineHeadRef head = Assert.IsType<
                HistoryTimelineSnapshotResult.Available>(
                    handle.Reader.ReadSnapshot()).Head;
            OnlineSelectedRawCapture capture = Assert.IsType<
                OnlineSelectedRawCaptureResult.Captured>(
                    handle.Coordinator.CaptureOnline(
                        head,
                        writer.ReadView)).Capture;
            HistoryTimelinePlanResult.Selected planned = Assert.IsType<
                HistoryTimelinePlanResult.Selected>(
                    handle.Coordinator.PlanNextRow(head, capture));
            rows.Add(planned.Candidate.Proposal.Descriptor);
            Assert.IsType<HistoryTimelineCommitResult.Committed>(
                handle.Coordinator.CommitRow(planned.Candidate));
        }
        for (int index = 0; index < tailTurns; index++) {
            _ = writer.AppendObservation($"tail-{index}");
            _ = writer.AppendImportedAgentAction(
                new ActionMessage([
                    new ActionBlock.Text($"tail-answer-{index}")
                ]),
                new CompletionDescriptor("import", "v1", "model"));
        }
        TimelineHeadRef finalHead = Assert.IsType<
            HistoryTimelineSnapshotResult.Available>(
                handle.Reader.ReadSnapshot()).Head;
        EventAddress rawHead = writer.ReadCurrentHead()!.Value;
        var session = new HistoryTimelineBuildReadSession(
            handle,
            writer.ReadView,
            recentReserveLimits,
            rawCaptureLimit);
        PartitionPolicyRevision policy = PartitionPolicyRevision.Create(
            finalHead.TimelineId,
            initial.PartitionAlgorithmId,
            initial.HistoryLoadEstimatorId,
            initial.TargetHistoryLoad,
            initial.MaxRawEvents,
            initial.MaxRenderedBytes);
        return new Fixture(
            writer,
            session,
            finalHead,
            rawHead,
            policy,
            rows.AsReadOnly());
    }

    private static HistoryRecentReserveRequirement Requirement(
        PartitionPolicyRevision policy,
        long minimumRecent
    ) => new(
        policy.PolicyDigest,
        policy.HistoryLoadEstimatorId,
        new HistoryLoadUnit(minimumRecent));

    private string NewPath() {
        string path = Path.Combine(
            Directory.Exists("/dev/shm")
                ? "/dev/shm"
                : Path.GetTempPath(),
            "atelia-history-recent-reserve-anchor-tests",
            Guid.NewGuid().ToString("N"));
        _paths.Add(path);
        return path;
    }

    public void Dispose() {
        foreach (string path in _paths) {
            if (Directory.Exists(path)) {
                Directory.Delete(path, recursive: true);
            }
        }
    }

    private sealed class FixedEstimator : IHistoryUnitLoadEstimator {
        public string Id => "test.history-load.reserve-anchor.v1";

        public HistoryUnitLoadMeasurement Measure(
            SessionHistoryPlanningUnit unit,
            int maxRenderedUtf8Bytes
        ) => new(new HistoryLoadUnit(1), 1);
    }

    private sealed class Fixture(
        SessionJournalEngine writer,
        HistoryTimelineBuildReadSession session,
        TimelineHeadRef head,
        EventAddress rawHead,
        PartitionPolicyRevision policy,
        IReadOnlyList<HistorySegmentDescriptor> rows
    ) : IDisposable {
        internal HistoryTimelineBuildReadSession Session { get; } = session;
        internal TimelineHeadRef Head { get; } = head;
        internal EventAddress RawHead { get; } = rawHead;
        internal PartitionPolicyRevision Policy { get; } = policy;
        internal IReadOnlyList<HistorySegmentDescriptor> Rows { get; } = rows;

        public void Dispose() {
            Session.Dispose();
            writer.Dispose();
        }
    }
}
