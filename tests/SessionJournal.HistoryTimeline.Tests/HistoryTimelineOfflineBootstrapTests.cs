using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.SessionJournal;
using Atelia.SessionJournal.HistoryTimeline;

namespace Atelia.SessionJournal.HistoryTimeline.Tests;

public sealed class HistoryTimelineOfflineBootstrapTests : IDisposable {
    private readonly List<string> _paths = [];

    [Fact]
    public void ConsecutiveRows_RefillConsumedRemainderAndHonorPolicySchedule() {
        string path = NewPath();
        RefId refId;
        using (SessionJournalEngine writer = CreateWriter(path)) {
            refId = writer.BranchRefId;
            _ = writer.AppendObservation("one");
            _ = writer.AppendImportedAgentAction(
                new ActionMessage([new ActionBlock.Text("answer-one")]),
                new CompletionDescriptor("import", "v1", "model-A")
            );
            _ = writer.AppendObservation("two");
            _ = writer.AppendImportedAgentAction(
                new ActionMessage([new ActionBlock.Text("answer-two")]),
                new CompletionDescriptor("import", "v1", "model-A")
            );
        }

        using var offline = SessionJournalEngine.OpenReadOnly(path);
        using SessionSelectedLineageForwardCursor cursor =
            OpenCursor(offline, pageSize: 2);
        var estimator = new FixedEstimator();
        TimelineId timelineId = new(new string('d', 32));
        PartitionPolicyRevision initial = Policy(
            timelineId,
            estimator.Id,
            maxRawEvents: 3
        );
        PartitionPolicyRevision next = Policy(
            timelineId,
            estimator.Id,
            maxRawEvents: 4
        );
        var coordinator = new HistoryTimelineCoordinator(
            path,
            new InMemoryHistoryTimelineLedger(refId, initial),
            estimator
        );
        HistoryTimelineOfflineBuilder builder = Assert.IsType<
            HistoryTimelineOfflineBuilderOpenResult.Opened
        >(coordinator.OpenOfflineBuilder(
            coordinator.ReadSnapshot(),
            cursor
        )).Builder;

        HistoryTimelineOfflineStepResult.Committed first =
            Assert.IsType<
                HistoryTimelineOfflineStepResult.Committed
            >(builder.BuildNextRow(coordinator.ReadSnapshot()));
        Assert.Equal(2, first.Descriptor.RawEventCount);
        Assert.Equal(initial.PolicyDigest,
            first.Descriptor.PartitionPolicyDigestAtCreation);
        Assert.NotEqual(
            cursor.Authority.Capture.CapturedHead,
            first.Descriptor.EndInclusive
        );

        _ = coordinator.PutPolicy(next);
        TimelineHeadRef scheduled = Assert.IsType<
            HistoryTimelinePolicyCasResult.Applied
        >(coordinator.CompareExchangePolicy(
            first.Head,
            next.PolicyDigest
        )).Head;
        HistoryTimelineOfflineStepResult.Committed second =
            Assert.IsType<
                HistoryTimelineOfflineStepResult.Committed
            >(builder.BuildNextRow(scheduled));

        Assert.Equal(first.Descriptor.RowId,
            second.Descriptor.PreviousRowId);
        Assert.Equal(first.Descriptor.EndInclusive,
            second.Descriptor.StartExclusive);
        Assert.Equal(2, second.Descriptor.RawEventCount);
        Assert.Equal(next.PolicyDigest,
            second.Descriptor.PartitionPolicyDigestAtCreation);
        Assert.Equal(next.PolicyDigest,
            second.Head.ActivePartitionPolicyDigest);

        HistoryTimelineOfflineStepResult terminal =
            builder.BuildNextRow(second.Head);
        Assert.True(
            terminal is HistoryTimelineOfflineStepResult.NotEnough,
            terminal.ToString()
        );
    }

    [Fact]
    public void OpenOfflineBuilder_RejectsSameIdentityCloneRepository() {
        string ownerPath = NewPath();
        string clonePath = NewPath();
        RefId ownerRef;
        using (SessionJournalEngine owner = CreateWriter(ownerPath)) {
            ownerRef = owner.BranchRefId;
            _ = owner.AppendObservation("same");
        }
        using (SessionJournalEngine clone = CreateWriter(clonePath)) {
            _ = clone.AppendObservation("same");
            Assert.Equal(ownerRef, clone.BranchRefId);
        }
        using var cloneOffline =
            SessionJournalEngine.OpenReadOnly(clonePath);
        using SessionSelectedLineageForwardCursor cloneCursor =
            OpenCursor(cloneOffline, pageSize: 2);
        var estimator = new FixedEstimator();
        PartitionPolicyRevision policy = Policy(
            new TimelineId(new string('e', 32)),
            estimator.Id,
            maxRawEvents: 4
        );
        var coordinator = new HistoryTimelineCoordinator(
            ownerPath,
            new InMemoryHistoryTimelineLedger(ownerRef, policy),
            estimator
        );

        var rejected = Assert.IsType<
            HistoryTimelineOfflineBuilderOpenResult.Invalid
        >(coordinator.OpenOfflineBuilder(
            coordinator.ReadSnapshot(),
            cloneCursor
        ));

        Assert.Equal("OfflineRawScopeMismatch", rejected.Code);
    }

    [Fact]
    public void OpenOfflineBuilder_RejectsInspectionExhaustedCursorButRawFenceRemainsReadable() {
        string path = NewPath();
        RefId refId;
        using (SessionJournalEngine writer = CreateWriter(path)) {
            refId = writer.BranchRefId;
            _ = writer.AppendObservation("one");
        }
        using var offline = SessionJournalEngine.OpenReadOnly(path);
        using SessionSelectedLineageForwardCursor cursor =
            OpenCursor(offline, pageSize: 2);
        var estimator = new FixedEstimator();
        PartitionPolicyRevision policy = Policy(
            new TimelineId(new string('6', 32)),
            estimator.Id,
            maxRawEvents: 4
        );
        var coordinator = new HistoryTimelineCoordinator(
            path,
            new InMemoryHistoryTimelineLedger(refId, policy),
            estimator
        );
        _ = cursor.ProbeBoundaries(_ =>
            SessionSelectedLineageBoundaryProbeDecision.Stop
        );

        var rejected = Assert.IsType<
            HistoryTimelineOfflineBuilderOpenResult.Invalid
        >(coordinator.OpenOfflineBuilder(
            coordinator.ReadSnapshot(),
            cursor
        ));

        Assert.Equal("OfflineRawScopeMismatch", rejected.Code);
        Assert.Equal(
            cursor.Authority.Capture.CapturedHead,
            cursor.ReadCurrentHead()
        );
    }

    [Fact]
    public void Restart_SeeksFreshAuditedCursorAndContinuesFromHead() {
        string path = NewPath();
        RefId refId;
        using (SessionJournalEngine writer = CreateWriter(path)) {
            refId = writer.BranchRefId;
            _ = writer.AppendObservation("one");
            _ = writer.AppendImportedAgentAction(
                new ActionMessage([new ActionBlock.Text("answer-one")]),
                new CompletionDescriptor("import", "v1", "model-A")
            );
            _ = writer.AppendObservation("two");
            _ = writer.AppendImportedAgentAction(
                new ActionMessage([new ActionBlock.Text("answer-two")]),
                new CompletionDescriptor("import", "v1", "model-A")
            );
        }
        using var offline = SessionJournalEngine.OpenReadOnly(path);
        var estimator = new FixedEstimator();
        PartitionPolicyRevision policy = Policy(
            new TimelineId(new string('f', 32)),
            estimator.Id,
            maxRawEvents: 3
        );
        var coordinator = new HistoryTimelineCoordinator(
            path,
            new InMemoryHistoryTimelineLedger(refId, policy),
            estimator
        );
        HistoryTimelineOfflineStepResult.Committed first;
        using (SessionSelectedLineageForwardCursor firstCursor =
               OpenCursor(offline, pageSize: 2)) {
            HistoryTimelineOfflineBuilder firstBuilder = Assert.IsType<
                HistoryTimelineOfflineBuilderOpenResult.Opened
            >(coordinator.OpenOfflineBuilder(
                coordinator.ReadSnapshot(),
                firstCursor
            )).Builder;
            first = Assert.IsType<
                HistoryTimelineOfflineStepResult.Committed
            >(firstBuilder.BuildNextRow(
                coordinator.ReadSnapshot()
            ));
        }

        using SessionSelectedLineageForwardCursor resumedCursor =
            OpenCursor(offline, pageSize: 3);
        resumedCursor.SeekToBoundary(
            first.Descriptor.EndInclusive,
            first.Descriptor.EndSetups
        );
        HistoryTimelineOfflineBuilder resumedBuilder = Assert.IsType<
            HistoryTimelineOfflineBuilderOpenResult.Opened
        >(coordinator.OpenOfflineBuilder(
            first.Head,
            resumedCursor
        )).Builder;

        HistoryTimelineOfflineStepResult.Committed second =
            Assert.IsType<
                HistoryTimelineOfflineStepResult.Committed
            >(resumedBuilder.BuildNextRow(first.Head));

        Assert.Equal(first.Descriptor.RowId,
            second.Descriptor.PreviousRowId);
        Assert.Equal(first.Descriptor.EndInclusive,
            second.Descriptor.StartExclusive);
    }

    [Fact]
    public void StaleExpectedHead_IsTerminalForOpenedBuilder() {
        string path = NewPath();
        RefId refId;
        using (SessionJournalEngine writer = CreateWriter(path)) {
            refId = writer.BranchRefId;
            _ = writer.AppendObservation("one");
            _ = writer.AppendImportedAgentAction(
                new ActionMessage([new ActionBlock.Text("answer")]),
                new CompletionDescriptor("import", "v1", "model-A")
            );
        }
        using var offline = SessionJournalEngine.OpenReadOnly(path);
        using SessionSelectedLineageForwardCursor cursor =
            OpenCursor(offline, pageSize: 2);
        var estimator = new FixedEstimator();
        TimelineId timelineId = new(new string('1', 32));
        PartitionPolicyRevision initial = Policy(
            timelineId,
            estimator.Id,
            maxRawEvents: 3
        );
        PartitionPolicyRevision next = Policy(
            timelineId,
            estimator.Id,
            maxRawEvents: 4
        );
        var coordinator = new HistoryTimelineCoordinator(
            path,
            new InMemoryHistoryTimelineLedger(refId, initial),
            estimator
        );
        TimelineHeadRef oldHead = coordinator.ReadSnapshot();
        HistoryTimelineOfflineBuilder builder = Assert.IsType<
            HistoryTimelineOfflineBuilderOpenResult.Opened
        >(coordinator.OpenOfflineBuilder(oldHead, cursor)).Builder;
        _ = coordinator.PutPolicy(next);
        TimelineHeadRef newHead = Assert.IsType<
            HistoryTimelinePolicyCasResult.Applied
        >(coordinator.CompareExchangePolicy(
            oldHead,
            next.PolicyDigest
        )).Head;

        Assert.IsType<HistoryTimelineOfflineStepResult
            .StaleTimelineHead>(builder.BuildNextRow(oldHead));
        var terminal = Assert.IsType<
            HistoryTimelineOfflineStepResult.Invalid
        >(builder.BuildNextRow(newHead));
        Assert.Equal("OfflineBuilderTerminal", terminal.Code);
    }

    [Fact]
    public void RawHeadDriftAtCommit_IsTypedTerminalAndDoesNotAppend() {
        string path = NewPath();
        RefId refId;
        using (SessionJournalEngine writer = CreateWriter(path)) {
            refId = writer.BranchRefId;
            _ = writer.AppendObservation("one");
            _ = writer.AppendImportedAgentAction(
                new ActionMessage([new ActionBlock.Text("answer")]),
                new CompletionDescriptor("import", "v1", "model-A")
            );
        }
        int rawFenceReads = 0;
        using var offline = SessionJournalEngine.OpenReadOnlyForTest(
            path,
            new SessionJournalTestHooks(
                RewriteForwardCursorObservedHead: observed => {
                    rawFenceReads++;
                    return rawFenceReads >= 3
                        ? default(EventAddress)
                        : observed;
                }
            )
        );
        using SessionSelectedLineageForwardCursor cursor =
            OpenCursor(offline, pageSize: 2);
        var estimator = new FixedEstimator();
        PartitionPolicyRevision policy = Policy(
            new TimelineId(new string('2', 32)),
            estimator.Id,
            maxRawEvents: 3
        );
        var coordinator = new HistoryTimelineCoordinator(
            path,
            new InMemoryHistoryTimelineLedger(refId, policy),
            estimator
        );
        TimelineHeadRef before = coordinator.ReadSnapshot();
        HistoryTimelineOfflineBuilder builder = Assert.IsType<
            HistoryTimelineOfflineBuilderOpenResult.Opened
        >(coordinator.OpenOfflineBuilder(before, cursor)).Builder;

        Assert.IsType<HistoryTimelineOfflineStepResult.RawHeadChanged>(
            builder.BuildNextRow(before)
        );
        Assert.Equal(before, coordinator.ReadSnapshot());
        var terminal = Assert.IsType<
            HistoryTimelineOfflineStepResult.Invalid
        >(builder.BuildNextRow(before));
        Assert.Equal("OfflineBuilderTerminal", terminal.Code);
    }

    [Fact]
    public void PolicyShrinkBelowRetainedSuffix_FailsTyped() {
        string path = NewPath();
        RefId refId;
        using (SessionJournalEngine writer = CreateWriter(path)) {
            refId = writer.BranchRefId;
            _ = writer.AppendObservation("one");
            _ = writer.AppendImportedAgentAction(
                new ActionMessage([new ActionBlock.Text("answer-one")]),
                new CompletionDescriptor("import", "v1", "model-A")
            );
            _ = writer.AppendObservation("two");
        }
        using var offline = SessionJournalEngine.OpenReadOnly(path);
        using SessionSelectedLineageForwardCursor cursor =
            OpenCursor(offline, pageSize: 2);
        var estimator = new FixedEstimator();
        TimelineId timelineId = new(new string('3', 32));
        PartitionPolicyRevision initial =
            PartitionPolicyRevision.Create(
                timelineId,
                HistoryPartitionAlgorithms
                    .FirstReplaySafeBoundaryAtTargetV1,
                estimator.Id,
                new HistoryLoadUnit(1),
                maxRawEvents: 3,
                maxRenderedBytes: 1024 * 1024
            );
        PartitionPolicyRevision shrink =
            PartitionPolicyRevision.Create(
                timelineId,
                HistoryPartitionAlgorithms
                    .FirstReplaySafeBoundaryAtTargetV1,
                estimator.Id,
                new HistoryLoadUnit(1),
                maxRawEvents: 1,
                maxRenderedBytes: 1024 * 1024
            );
        var coordinator = new HistoryTimelineCoordinator(
            path,
            new InMemoryHistoryTimelineLedger(refId, initial),
            estimator
        );
        HistoryTimelineOfflineBuilder builder = Assert.IsType<
            HistoryTimelineOfflineBuilderOpenResult.Opened
        >(coordinator.OpenOfflineBuilder(
            coordinator.ReadSnapshot(),
            cursor
        )).Builder;
        HistoryTimelineOfflineStepResult.Committed first =
            Assert.IsType<
                HistoryTimelineOfflineStepResult.Committed
            >(builder.BuildNextRow(coordinator.ReadSnapshot()));
        _ = coordinator.PutPolicy(shrink);
        TimelineHeadRef scheduled = Assert.IsType<
            HistoryTimelinePolicyCasResult.Applied
        >(coordinator.CompareExchangePolicy(
            first.Head,
            shrink.PolicyDigest
        )).Head;

        var invalid = Assert.IsType<
            HistoryTimelineOfflineStepResult.Invalid
        >(builder.BuildNextRow(scheduled));

        Assert.Equal("OfflinePolicyRangeCapIncompatible",
            invalid.Code);
        Assert.Equal(scheduled, coordinator.ReadSnapshot());
    }

    [Fact]
    public void SessionCreatedOnlyReturnsNotEnoughZero() {
        string path = NewPath();
        RefId refId;
        using (SessionJournalEngine writer = CreateWriter(path)) {
            refId = writer.BranchRefId;
        }
        using var offline = SessionJournalEngine.OpenReadOnly(path);
        using SessionSelectedLineageForwardCursor cursor =
            OpenCursor(offline, pageSize: 2);
        var estimator = new FixedEstimator();
        PartitionPolicyRevision policy = Policy(
            new TimelineId(new string('4', 32)),
            estimator.Id,
            maxRawEvents: 4
        );
        var coordinator = new HistoryTimelineCoordinator(
            path,
            new InMemoryHistoryTimelineLedger(refId, policy),
            estimator
        );
        HistoryTimelineOfflineBuilder builder = Assert.IsType<
            HistoryTimelineOfflineBuilderOpenResult.Opened
        >(coordinator.OpenOfflineBuilder(
            coordinator.ReadSnapshot(),
            cursor
        )).Builder;

        var notEnough = Assert.IsType<
            HistoryTimelineOfflineStepResult.NotEnough
        >(builder.BuildNextRow(coordinator.ReadSnapshot()));

        Assert.Equal(0, notEnough.Partition.RawEventCount);
        Assert.Equal(0, notEnough.Partition.CompletedUnitCount);
        Assert.Equal(0,
            notEnough.Partition.MeasuredRenderedUtf8Bytes);
    }

    [Fact]
    public void OnlineAndOfflineBuildsProduceIdenticalCanonicalChain() {
        string path = NewPath();
        RefId refId;
        using (SessionJournalEngine writer = CreateWriter(path)) {
            refId = writer.BranchRefId;
            _ = writer.AppendObservation("one");
            _ = writer.AppendImportedAgentAction(
                new ActionMessage([new ActionBlock.Text("answer-one")]),
                new CompletionDescriptor("import", "v1", "model-A")
            );
            _ = writer.AppendObservation("two");
            _ = writer.AppendImportedAgentAction(
                new ActionMessage([new ActionBlock.Text("answer-two")]),
                new CompletionDescriptor("import", "v1", "model-A")
            );
        }
        using var offline = SessionJournalEngine.OpenReadOnly(path);
        var estimator = new FixedEstimator();
        PartitionPolicyRevision policy = Policy(
            new TimelineId(new string('5', 32)),
            estimator.Id,
            maxRawEvents: 4
        );
        var online = new HistoryTimelineCoordinator(
            path,
            new InMemoryHistoryTimelineLedger(refId, policy),
            estimator
        );
        var onlineRows = new List<HistorySegmentDescriptor>();
        for (int index = 0; index < 2; index++) {
            TimelineHeadRef expected = online.ReadSnapshot();
            OnlineSelectedRawCapture capture = Assert.IsType<
                OnlineSelectedRawCaptureResult.Captured
            >(online.CaptureOnline(expected, offline.ReadView)).Capture;
            HistoryTimelinePlanResult.Selected selected = Assert.IsType<
                HistoryTimelinePlanResult.Selected
            >(online.PlanNextRow(expected, capture));
            onlineRows.Add(selected.Candidate.Proposal.Descriptor);
            _ = Assert.IsType<HistoryTimelineCommitResult.Committed>(
                online.CommitRow(selected.Candidate)
            );
        }

        var rebuilt = new HistoryTimelineCoordinator(
            path,
            new InMemoryHistoryTimelineLedger(refId, policy),
            estimator
        );
        using SessionSelectedLineageForwardCursor cursor =
            OpenCursor(offline, pageSize: 2);
        HistoryTimelineOfflineBuilder builder = Assert.IsType<
            HistoryTimelineOfflineBuilderOpenResult.Opened
        >(rebuilt.OpenOfflineBuilder(
            rebuilt.ReadSnapshot(),
            cursor
        )).Builder;
        var offlineRows = new List<HistorySegmentDescriptor>();
        for (int index = 0; index < 2; index++) {
            HistoryTimelineOfflineStepResult.Committed committed =
                Assert.IsType<
                    HistoryTimelineOfflineStepResult.Committed
                >(builder.BuildNextRow(rebuilt.ReadSnapshot()));
            offlineRows.Add(committed.Descriptor);
        }

        Assert.Equal(onlineRows.Count, offlineRows.Count);
        for (int index = 0; index < onlineRows.Count; index++) {
            Assert.True(
                onlineRows[index].ToCanonicalBytes().AsSpan()
                    .SequenceEqual(
                        offlineRows[index].ToCanonicalBytes()
                    )
            );
        }
        Assert.Equal(
            onlineRows.Select(static row => row.RowId),
            offlineRows.Select(static row => row.RowId)
        );
        Assert.Equal(
            onlineRows.Select(static row => row.DescriptorDigest),
            offlineRows.Select(static row => row.DescriptorDigest)
        );
    }

    public void Dispose() {
        foreach (string path in _paths) {
            if (Directory.Exists(path)) {
                Directory.Delete(path, recursive: true);
            }
        }
    }

    private static SessionSelectedLineageForwardCursor OpenCursor(
        SessionJournalEngine offline,
        int pageSize
    ) {
        SessionSelectedLineageAuditSession audit =
            offline.BeginSelectedLineageAudit();
        var pages = new List<SessionSelectedLineageAuditPage>();
        while (!audit.IsCaptureComplete) {
            pages.Add(audit.ReadNextPage(pageSize));
        }
        _ = audit.Complete();
        return offline.OpenSelectedLineageForwardCursor(
            new InMemoryPageSnapshot(audit.Capture, pages)
        );
    }

    private static PartitionPolicyRevision Policy(
        TimelineId timelineId,
        string estimatorId,
        int maxRawEvents
    ) => PartitionPolicyRevision.Create(
        timelineId,
        HistoryPartitionAlgorithms.FirstReplaySafeBoundaryAtTargetV1,
        estimatorId,
        new HistoryLoadUnit(2),
        maxRawEvents,
        1024 * 1024
    );

    private SessionJournalEngine CreateWriter(string path)
        => SessionJournalEngine.Create(
            path,
            new SessionCreateOptions(
                "model-A",
                "system-A",
                "surface-A"
            )
        );

    private string NewPath() {
        string path = Path.Combine(
            Directory.Exists("/dev/shm")
                ? "/dev/shm"
                : Path.GetTempPath(),
            "atelia-history-timeline-offline-tests",
            Guid.NewGuid().ToString("N")
        );
        _paths.Add(path);
        return path;
    }

    private sealed class FixedEstimator
        : IHistoryUnitLoadEstimator {
        public string Id => "test.history-load.fixed-one.v1";

        public HistoryUnitLoadMeasurement Measure(
            SessionHistoryPlanningUnit unit,
            int maxRenderedUtf8Bytes
        ) => new(new HistoryLoadUnit(1), 1);
    }

    private sealed class InMemoryPageSnapshot(
        SessionSelectedLineageAuditCapture capture,
        IReadOnlyList<SessionSelectedLineageAuditPage> pages
    ) : ISessionSelectedLineageAuditPageSnapshot {
        public SessionSelectedLineageAuditCapture Capture { get; }
            = capture;
        public long PageCount => pages.Count;

        public IEnumerable<SessionSelectedLineageAuditPage>
            ReadHeadToOldestPages() => pages;

        public IEnumerable<SessionSelectedLineageAuditPage>
            ReadOldestToHeadPages() => pages.Reverse();

        public void Dispose() { }
    }

}
