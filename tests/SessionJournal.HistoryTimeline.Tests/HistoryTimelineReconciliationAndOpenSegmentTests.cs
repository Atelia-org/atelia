using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.SessionJournal;
using Atelia.SessionJournal.HistoryTimeline;

namespace Atelia.SessionJournal.HistoryTimeline.Tests;

public sealed class HistoryTimelineReconciliationAndOpenSegmentTests
    : IDisposable {
    private readonly List<string> _paths = [];
    private readonly IHistoryUnitLoadEstimator _estimator =
        new O200kBaseHistoryUnitLoadEstimator();

    [Fact]
    public void OpenSegment_OldSelectedAncestorUsesCreationPolicy() {
        string path = NewPath();
        using var writer = CreateWriter(path);
        PartitionPolicyRevision initial = Policy('a', 4);
        PartitionPolicyRevision next = Policy(
            initial.TimelineId,
            initial.HistoryLoadEstimatorId,
            target: 1,
            maxRawEvents: 8
        );
        PartitionPolicyRevision latest = Policy(
            initial.TimelineId,
            initial.HistoryLoadEstimatorId,
            target: 1,
            maxRawEvents: 7
        );
        var coordinator = Coordinator(
            path,
            writer.BranchRefId,
            initial,
            _estimator
        );
        _ = writer.AppendObservation("first");
        HistorySegmentDescriptor first = PlanAndCommit(
            coordinator,
            writer.ReadView
        );
        _ = coordinator.PutPolicy(next);
        TimelineHeadRef policyHead = Assert.IsType<
            HistoryTimelinePolicyCasResult.Applied
        >(coordinator.CompareExchangePolicy(
            coordinator.ReadSnapshotRequired(),
            next.PolicyDigest
        )).Head;
        _ = writer.AppendImportedAgentAction(
            new ActionMessage([new ActionBlock.Text("answer")]),
            new CompletionDescriptor("import", "v1", "model-A")
        );
        HistorySegmentDescriptor second = PlanAndCommit(
            coordinator,
            writer.ReadView
        );
        Assert.Equal(policyHead.HeadRowId, second.PreviousRowId);
        _ = coordinator.PutPolicy(latest);
        _ = Assert.IsType<HistoryTimelinePolicyCasResult.Applied>(
            coordinator.CompareExchangePolicy(
                coordinator.ReadSnapshotRequired(),
                latest.PolicyDigest
            )
        );
        _ = writer.AppendObservation("third");
        HistorySegmentDescriptor third = PlanAndCommit(
            coordinator,
            writer.ReadView
        );
        Assert.Equal(second.RowId, third.PreviousRowId);
        TimelineHeadRef selectedHead = coordinator.ReadSnapshotRequired();
        OnlineSelectedRawCapture capture = Capture(
            coordinator,
            selectedHead,
            writer.ReadView
        );

        HistorySegmentContent content = Assert.IsType<
            HistorySegmentOpenResult.Opened
        >(coordinator.OpenSegment(
            selectedHead,
            capture,
            first.RowId
        )).Content;

        Assert.Equal(first, content.Descriptor);
        Assert.Equal(first.EndInclusive,
            content.Window.ObservedRawHead);
        Assert.Equal(first.RawRangeSha256,
            content.Window.RawRangeSha256);
        Assert.Equal(initial.PolicyDigest,
            content.Descriptor.PartitionPolicyDigestAtCreation);
        Assert.NotEqual(next.PolicyDigest,
            content.Descriptor.PartitionPolicyDigestAtCreation);

        HistorySegmentContent successor = Assert.IsType<
            HistorySegmentOpenResult.Opened
        >(coordinator.OpenSegment(
            selectedHead,
            capture,
            second.RowId
        )).Content;
        Assert.Equal(next.PolicyDigest,
            successor.Descriptor.PartitionPolicyDigestAtCreation);
        Assert.NotEqual(latest.PolicyDigest,
            successor.Descriptor.PartitionPolicyDigestAtCreation);
    }

    [Fact]
    public void OpenSegment_RejectsSiblingBeforeRawMaterialization() {
        string path = NewPath();
        using var writer = CreateWriter(path);
        PartitionPolicyRevision policy = Policy('b', 8);
        var coordinator = Coordinator(
            path,
            writer.BranchRefId,
            policy,
            _estimator,
            onlineRawCaptureLimit: policy.MaxRawEvents
        );
        _ = writer.AppendObservation("first");
        HistorySegmentDescriptor first = PlanAndCommit(
            coordinator,
            writer.ReadView
        );
        EventAddress oldHead = writer.AppendImportedAgentAction(
            new ActionMessage([new ActionBlock.Text("old")]),
            new CompletionDescriptor("import", "v1", "model-A")
        );
        HistorySegmentDescriptor oldSecond = PlanAndCommit(
            coordinator,
            writer.ReadView
        );
        TimelineHeadRef oldTimelineHead = coordinator.ReadSnapshotRequired();
        Assert.True(writer.MoveCurrentHeadForTest(
            oldHead,
            first.EndInclusive
        ));
        TimelineHeadRef common = Assert.IsType<
            HistoryTimelineReconcileResult.Reconciled
        >(coordinator.ReconcileSelectedPath(
            oldTimelineHead,
            writer.ReadView
        )).Head;
        Assert.Equal(first.RowId, common.HeadRowId);

        _ = writer.AppendImportedAgentAction(
            new ActionMessage([new ActionBlock.Text("sibling")]),
            new CompletionDescriptor("import", "v1", "model-A")
        );
        HistorySegmentDescriptor sibling = PlanAndCommit(
            coordinator,
            writer.ReadView
        );
        TimelineHeadRef siblingHead = coordinator.ReadSnapshotRequired();
        OnlineSelectedRawCapture sameCapture = Capture(
            coordinator,
            siblingHead,
            writer.ReadView
        );
        Assert.IsType<HistorySegmentOpenResult.Opened>(
            coordinator.OpenSegment(
                siblingHead,
                sameCapture,
                sibling.RowId
            )
        );

        var rejected = Assert.IsType<
            HistorySegmentOpenResult.NotOnSelectedPath
        >(coordinator.OpenSegment(
            siblingHead,
            sameCapture,
            oldSecond.RowId
        ));
        Assert.Equal(oldSecond.RowId, rejected.RowId);
    }

    [Fact]
    public void Reconcile_InteriorThenMoveToNullProducesEmptyPath() {
        string path = NewPath();
        using var writer = CreateWriter(path);
        var estimator = new FixedEstimator();
        PartitionPolicyRevision policy = Policy(
            new TimelineId(new string('c', 32)),
            estimator.Id,
            target: 2,
            maxRawEvents: 8
        );
        var coordinator = Coordinator(
            path,
            writer.BranchRefId,
            policy,
            estimator
        );
        EventAddress observation =
            writer.AppendObservation("first");
        EventAddress action = writer.AppendImportedAgentAction(
            new ActionMessage([new ActionBlock.Text("answer")]),
            new CompletionDescriptor("import", "v1", "model-A")
        );
        HistorySegmentDescriptor row = PlanAndCommit(
            coordinator,
            writer.ReadView
        );
        Assert.Equal(action, row.EndInclusive);
        TimelineHeadRef selected = coordinator.ReadSnapshotRequired();

        Assert.True(writer.MoveCurrentHeadForTest(
            action,
            observation
        ));
        TimelineHeadRef emptyTimeline = Assert.IsType<
            HistoryTimelineReconcileResult.Reconciled
        >(coordinator.ReconcileSelectedPath(
            selected,
            writer.ReadView
        )).Head;
        Assert.Null(emptyTimeline.HeadRowId);
        Assert.Null(emptyTimeline.SelectedRawHeadAtCommit);

        Assert.True(writer.MoveCurrentHeadForTest(
            observation,
            newHead: null
        ));
        var unchanged = Assert.IsType<
            HistoryTimelineReconcileResult.Unchanged
        >(coordinator.ReconcileSelectedPath(
            emptyTimeline,
            writer.ReadView
        ));
        Assert.Null(unchanged.Head.HeadRowId);
        Assert.Null(unchanged.Head.SelectedRawHeadAtCommit);
    }

    [Fact]
    public void Reconcile_CommonAncestorBeyondBoundIsOfflineWithoutMutation() {
        string path = NewPath();
        using var writer = CreateWriter(path);
        PartitionPolicyRevision policy = Policy('d', 4);
        var coordinator = Coordinator(
            path,
            writer.BranchRefId,
            policy,
            _estimator,
            onlineRawCaptureLimit: policy.MaxRawEvents
        );
        _ = writer.AppendObservation("first");
        HistorySegmentDescriptor first = PlanAndCommit(
            coordinator,
            writer.ReadView
        );
        EventAddress oldRawHead = writer.AppendImportedAgentAction(
            new ActionMessage([new ActionBlock.Text("old")]),
            new CompletionDescriptor("import", "v1", "model-A")
        );
        _ = PlanAndCommit(coordinator, writer.ReadView);
        TimelineHeadRef before = coordinator.ReadSnapshotRequired();
        Assert.True(writer.MoveCurrentHeadForTest(
            oldRawHead,
            first.EndInclusive
        ));
        _ = writer.AppendImportedAgentAction(
            new ActionMessage([new ActionBlock.Text("sibling")]),
            new CompletionDescriptor("import", "v1", "model-A")
        );
        for (int index = 0; index < 4; index++) {
            _ = writer.AppendSystemPromptSetup($"sibling-{index}");
        }

        var offline = Assert.IsType<
            HistoryTimelineReconcileResult.OfflineBootstrapRequired
        >(coordinator.ReconcileSelectedPath(
            before,
            writer.ReadView
        ));

        Assert.NotNull(offline.Evidence);
        Assert.Equal(before, coordinator.ReadSnapshotRequired());
    }

    [Fact]
    public void PolicyCasWinsAgainstReconcileFromSameExpectedHead() {
        string path = NewPath();
        using var writer = CreateWriter(path);
        PartitionPolicyRevision initial = Policy('e', 4);
        PartitionPolicyRevision next = Policy(
            initial.TimelineId,
            initial.HistoryLoadEstimatorId,
            target: 1,
            maxRawEvents: 8
        );
        var coordinator = Coordinator(
            path,
            writer.BranchRefId,
            initial,
            _estimator
        );
        _ = writer.AppendObservation("first");
        _ = PlanAndCommit(coordinator, writer.ReadView);
        TimelineHeadRef expected = coordinator.ReadSnapshotRequired();
        _ = coordinator.PutPolicy(next);
        TimelineHeadRef winner = Assert.IsType<
            HistoryTimelinePolicyCasResult.Applied
        >(coordinator.CompareExchangePolicy(
            expected,
            next.PolicyDigest
        )).Head;

        var stale = Assert.IsType<
            HistoryTimelineReconcileResult.StaleTimelineHead
        >(coordinator.ReconcileSelectedPath(
            expected,
            writer.ReadView
        ));

        Assert.Equal(winner, stale.Actual);
        Assert.Equal(winner, coordinator.ReadSnapshotRequired());
    }

    [Fact]
    public void OfflineReconcile_LongDivergenceFindsAncestorAndSwitchesIndexedSnapshot() {
        string path = NewPath();
        RefId refId;
        EventAddress oldRawHead;
        HistorySegmentDescriptor ancestor;
        var estimator = new FixedEstimator();
        PartitionPolicyRevision policy = Policy(
            new TimelineId(new string('1', 32)),
            estimator.Id,
            target: 1,
            maxRawEvents: 4
        );
        InMemoryHistoryTimelineLedger? ledger = null;
        HistoryTimelineCoordinator? coordinator = null;
        using (SessionJournalEngine writer = CreateWriter(path)) {
            refId = writer.BranchRefId;
            ledger = new InMemoryHistoryTimelineLedger(refId, policy);
            coordinator = new HistoryTimelineCoordinator(
                path,
                ledger,
                new HistoryTimelineCoordinatorTestHooks(
                    OnlineRawCaptureLimit: policy.MaxRawEvents),
                estimator
            );
            var rows = new List<HistorySegmentDescriptor>();
            for (int index = 0; index < 20; index++) {
                oldRawHead = writer.AppendObservation(
                    $"observation-{index}"
                );
                rows.Add(PlanAndCommit(coordinator, writer.ReadView));
                oldRawHead = writer.AppendImportedAgentAction(
                    new ActionMessage([
                        new ActionBlock.Text($"action-{index}")
                    ]),
                    new CompletionDescriptor(
                        "import",
                        "v1",
                        "model-A"
                    )
                );
                rows.Add(PlanAndCommit(coordinator, writer.ReadView));
            }
            ancestor = rows[19];
            oldRawHead = writer.ReadCurrentHead()!.Value;
            Assert.True(writer.MoveCurrentHeadForTest(
                oldRawHead,
                ancestor.EndInclusive
            ));
            for (int index = 0; index < 8; index++) {
                _ = writer.AppendSystemPromptSetup($"fork-{index}");
            }
            TimelineHeadRef selected = coordinator.ReadSnapshotRequired();
            Assert.IsType<
                HistoryTimelineReconcileResult.OfflineBootstrapRequired
            >(coordinator.ReconcileSelectedPath(
                selected,
                writer.ReadView
            ));
            Assert.Equal(selected, coordinator.ReadSnapshotRequired());
        }

        using var offline = SessionJournalEngine.OpenReadOnly(path);
        using SessionSelectedLineageForwardCursor cursor =
            OpenCursor(offline, pageSize: 7);
        TimelineHeadRef before = coordinator!.ReadSnapshotRequired();
        long rowProbesBefore = ledger!.SelectedPathRowProbeCount;
        long boundaryProbesBefore =
            ledger.SelectedPathBoundaryProbeCount;
        long switchesBefore = ledger.SelectedPathSwitchCount;

        TimelineHeadRef reconciled = Assert.IsType<
            HistoryTimelineReconcileResult.Reconciled
        >(coordinator.ReconcileSelectedPathOffline(before, cursor)).Head;

        Assert.Equal(ancestor.RowId, reconciled.HeadRowId);
        Assert.Equal(cursor.Authority.Capture.CapturedHead,
            reconciled.SelectedRawHeadAtCommit);
        Assert.Equal(1,
            ledger.SelectedPathRowProbeCount - rowProbesBefore);
        Assert.True(
            ledger.SelectedPathBoundaryProbeCount
                - boundaryProbesBefore > policy.MaxRawEvents
        );
        Assert.Equal(1,
            ledger.SelectedPathSwitchCount - switchesBefore);
        Assert.Null(cursor.ReadNextRange(1));
    }

    [Fact]
    public void OfflineReconcile_InteriorSelectsPreviousCompleteRow() {
        string path = NewPath();
        RefId refId;
        EventAddress secondInterior;
        HistorySegmentDescriptor first;
        HistoryTimelineCoordinator? coordinator = null;
        var estimator = new FixedEstimator();
        PartitionPolicyRevision policy = Policy(
            new TimelineId(new string('2', 32)),
            estimator.Id,
            target: 2,
            maxRawEvents: 8
        );
        using (SessionJournalEngine writer = CreateWriter(path)) {
            refId = writer.BranchRefId;
            coordinator = Coordinator(path, refId, policy, estimator);
            _ = writer.AppendObservation("first-observation");
            _ = writer.AppendImportedAgentAction(
                new ActionMessage([new ActionBlock.Text("first-action")]),
                new CompletionDescriptor("import", "v1", "model-A")
            );
            first = PlanAndCommit(coordinator, writer.ReadView);
            secondInterior = writer.AppendObservation(
                "second-observation"
            );
            EventAddress secondEnd = writer.AppendImportedAgentAction(
                new ActionMessage([new ActionBlock.Text("second-action")]),
                new CompletionDescriptor("import", "v1", "model-A")
            );
            _ = PlanAndCommit(coordinator, writer.ReadView);
            Assert.True(writer.MoveCurrentHeadForTest(
                secondEnd,
                secondInterior
            ));
        }
        using var offline = SessionJournalEngine.OpenReadOnly(path);
        using SessionSelectedLineageForwardCursor cursor =
            OpenCursor(offline, pageSize: 2);
        TimelineHeadRef before = coordinator!.ReadSnapshotRequired();

        TimelineHeadRef reconciled = Assert.IsType<
            HistoryTimelineReconcileResult.Reconciled
        >(coordinator.ReconcileSelectedPathOffline(before, cursor)).Head;

        Assert.Equal(first.RowId, reconciled.HeadRowId);
        Assert.Equal(secondInterior,
            reconciled.SelectedRawHeadAtCommit);
    }

    [Fact]
    public void OfflineReconcile_NoSelectedBoundaryProducesEmptyPath() {
        string path = NewPath();
        RefId refId;
        EventAddress rawEnd;
        HistoryTimelineCoordinator? coordinator = null;
        PartitionPolicyRevision policy = Policy('2', 8);
        using (SessionJournalEngine writer = CreateWriter(path)) {
            refId = writer.BranchRefId;
            coordinator = Coordinator(
                path,
                refId,
                policy,
                _estimator
            );
            rawEnd = writer.AppendObservation("first");
            HistorySegmentDescriptor row = PlanAndCommit(
                coordinator,
                writer.ReadView
            );
            Assert.True(writer.MoveCurrentHeadForTest(
                rawEnd,
                row.StartExclusive
            ));
        }
        using var offline = SessionJournalEngine.OpenReadOnly(path);
        using SessionSelectedLineageForwardCursor cursor =
            OpenCursor(offline, pageSize: 2);

        TimelineHeadRef reconciled = Assert.IsType<
            HistoryTimelineReconcileResult.Reconciled
        >(coordinator!.ReconcileSelectedPathOffline(
            coordinator.ReadSnapshotRequired(),
            cursor
        )).Head;

        Assert.Null(reconciled.HeadRowId);
        Assert.Null(reconciled.SelectedRawHeadAtCommit);
    }

    [Fact]
    public void OfflineReconcile_FinalRawFenceDriftIsTypedAndDoesNotMutate() {
        string path = NewPath();
        RefId refId;
        HistoryTimelineCoordinator? coordinator = null;
        PartitionPolicyRevision policy = Policy('3', 8);
        using (SessionJournalEngine writer = CreateWriter(path)) {
            refId = writer.BranchRefId;
            coordinator = Coordinator(
                path,
                refId,
                policy,
                _estimator
            );
            _ = writer.AppendObservation("first");
            _ = PlanAndCommit(coordinator, writer.ReadView);
        }
        EventAddress rewritten = default;
        using var offline = SessionJournalEngine.OpenReadOnlyForTest(
            path,
            new SessionJournalTestHooks(
                RewriteForwardBoundaryProbeObservedHead: observed =>
                    rewritten == default ? observed : rewritten
            )
        );
        using SessionSelectedLineageForwardCursor cursor =
            OpenCursor(offline, pageSize: 2);
        rewritten = cursor.Authority.BootstrapSeed.Address;
        TimelineHeadRef before = coordinator!.ReadSnapshotRequired();

        Assert.IsType<HistoryTimelineReconcileResult.RawHeadChanged>(
            coordinator.ReconcileSelectedPathOffline(before, cursor)
        );
        Assert.Equal(before, coordinator.ReadSnapshotRequired());
    }

    [Fact]
    public void OfflineReconcile_PolicyCasDuringProbeWinsWholeHeadCas() {
        string path = NewPath();
        RefId refId;
        PartitionPolicyRevision initial = Policy('4', 8);
        PartitionPolicyRevision next = Policy(
            initial.TimelineId,
            initial.HistoryLoadEstimatorId,
            target: 2,
            maxRawEvents: 8
        );
        using (SessionJournalEngine writer = CreateWriter(path)) {
            refId = writer.BranchRefId;
            _ = writer.AppendObservation("first");
        }
        var ledger = new InMemoryHistoryTimelineLedger(refId, initial);
        HistoryTimelineCoordinator? coordinator = null;
        TimelineHeadRef? expected = null;
        bool raced = false;
        coordinator = new HistoryTimelineCoordinator(
            path,
            ledger,
            new HistoryTimelineCoordinatorTestHooks(address => {
                if (raced) {
                    return;
                }
                raced = true;
                _ = coordinator!.PutPolicy(next);
                _ = coordinator.CompareExchangePolicy(
                    expected!,
                    next.PolicyDigest
                );
            }),
            _estimator
        );
        expected = coordinator.ReadSnapshotRequired();
        using var offline = SessionJournalEngine.OpenReadOnly(path);
        using SessionSelectedLineageForwardCursor cursor =
            OpenCursor(offline, pageSize: 2);

        var stale = Assert.IsType<
            HistoryTimelineReconcileResult.StaleTimelineHead
        >(coordinator.ReconcileSelectedPathOffline(expected, cursor));

        Assert.Equal(next.PolicyDigest,
            stale.Actual.ActivePartitionPolicyDigest);
        Assert.Equal(stale.Actual, coordinator.ReadSnapshotRequired());
    }

    [Fact]
    public void OfflineReconcile_AppendDuringProbeWinsWholeHeadCas() {
        string path = NewPath();
        RefId refId;
        using (SessionJournalEngine writer = CreateWriter(path)) {
            refId = writer.BranchRefId;
            _ = writer.AppendObservation("first");
        }
        PartitionPolicyRevision policy = Policy('5', 8);
        var ledger = new InMemoryHistoryTimelineLedger(refId, policy);
        HistoryTimelineCoordinator? coordinator = null;
        HistoryRowCommitCandidate? candidate = null;
        bool raced = false;
        coordinator = new HistoryTimelineCoordinator(
            path,
            ledger,
            new HistoryTimelineCoordinatorTestHooks(address => {
                if (raced) {
                    return;
                }
                raced = true;
                Assert.IsType<HistoryTimelineCommitResult.Committed>(
                    coordinator!.CommitRow(candidate!)
                );
            }),
            _estimator
        );
        TimelineHeadRef expected = coordinator.ReadSnapshotRequired();
        using var offline = SessionJournalEngine.OpenReadOnly(path);
        OnlineSelectedRawCapture capture = Capture(
            coordinator,
            expected,
            offline.ReadView
        );
        candidate = Assert.IsType<HistoryTimelinePlanResult.Selected>(
            coordinator.PlanNextRow(expected, capture)
        ).Candidate;
        using SessionSelectedLineageForwardCursor cursor =
            OpenCursor(offline, pageSize: 2);

        Assert.IsType<HistoryTimelineReconcileResult.StaleTimelineHead>(
            coordinator.ReconcileSelectedPathOffline(expected, cursor)
        );
        Assert.NotNull(coordinator.ReadSnapshotRequired().HeadRowId);
    }

    [Fact]
    public void OfflineReconcile_ReconcileDuringProbeWinsWholeHeadCas() {
        string path = NewPath();
        RefId refId;
        HistoryTimelineCoordinator? coordinator = null;
        PartitionPolicyRevision policy = Policy('6', 8);
        InMemoryHistoryTimelineLedger? ledger = null;
        using (SessionJournalEngine writer = CreateWriter(path)) {
            refId = writer.BranchRefId;
            ledger = new InMemoryHistoryTimelineLedger(
                refId,
                policy
            );
            coordinator = new HistoryTimelineCoordinator(
                path,
                ledger,
                _estimator
            );
            _ = writer.AppendObservation("first");
            _ = PlanAndCommit(coordinator, writer.ReadView);
            _ = writer.AppendImportedAgentAction(
                new ActionMessage([new ActionBlock.Text("new raw head")]),
                new CompletionDescriptor("import", "v1", "model-A")
            );
        }
        TimelineHeadRef expected = coordinator!.ReadSnapshotRequired();
        using var offline = SessionJournalEngine.OpenReadOnly(path);
        bool raced = false;
        var racing = new HistoryTimelineCoordinator(
            path,
            ledger!,
            new HistoryTimelineCoordinatorTestHooks(address => {
                if (raced) {
                    return;
                }
                raced = true;
                Assert.IsType<HistoryTimelineReconcileResult.Reconciled>(
                    coordinator.ReconcileSelectedPath(
                        expected,
                        offline.ReadView
                    )
                );
            }),
            _estimator
        );
        using SessionSelectedLineageForwardCursor cursor =
            OpenCursor(offline, pageSize: 2);

        Assert.IsType<HistoryTimelineReconcileResult.StaleTimelineHead>(
            racing.ReconcileSelectedPathOffline(expected, cursor)
        );
        Assert.NotEqual(expected, coordinator.ReadSnapshotRequired());
    }

    public void Dispose() {
        foreach (string path in _paths) {
            if (Directory.Exists(path)) {
                Directory.Delete(path, recursive: true);
            }
        }
    }

    private static HistoryTimelineCoordinator Coordinator(
        string path,
        RefId refId,
        PartitionPolicyRevision policy,
        IHistoryUnitLoadEstimator estimator,
        int? onlineRawCaptureLimit = null
    ) => onlineRawCaptureLimit is null
        ? new HistoryTimelineCoordinator(
            path,
            new InMemoryHistoryTimelineLedger(refId, policy),
            estimator)
        : new HistoryTimelineCoordinator(
            path,
            new InMemoryHistoryTimelineLedger(refId, policy),
            new HistoryTimelineCoordinatorTestHooks(
                OnlineRawCaptureLimit: onlineRawCaptureLimit),
            estimator);

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

    private static HistorySegmentDescriptor PlanAndCommit(
        HistoryTimelineCoordinator coordinator,
        SessionJournalReadView readView
    ) {
        TimelineHeadRef expected = coordinator.ReadSnapshotRequired();
        OnlineSelectedRawCapture capture = Capture(
            coordinator,
            expected,
            readView
        );
        HistoryTimelinePlanResult.Selected selected = Assert.IsType<
            HistoryTimelinePlanResult.Selected
        >(coordinator.PlanNextRow(expected, capture));
        _ = Assert.IsType<HistoryTimelineCommitResult.Committed>(
            coordinator.CommitRow(selected.Candidate)
        );
        return selected.Candidate.Proposal.Descriptor;
    }

    private static OnlineSelectedRawCapture Capture(
        HistoryTimelineCoordinator coordinator,
        TimelineHeadRef expected,
        SessionJournalReadView readView
    ) => Assert.IsType<OnlineSelectedRawCaptureResult.Captured>(
        coordinator.CaptureOnline(expected, readView)
    ).Capture;

    private static PartitionPolicyRevision Policy(
        char timelineDigit,
        int maxRawEvents
    ) => Policy(
        new TimelineId(new string(timelineDigit, 32)),
        O200kBaseHistoryUnitLoadEstimator.EstimatorId,
        target: 1,
        maxRawEvents
    );

    private static PartitionPolicyRevision Policy(
        TimelineId timelineId,
        string estimatorId,
        long target,
        int maxRawEvents
    ) => PartitionPolicyRevision.Create(
        timelineId,
        HistoryPartitionAlgorithms.FirstReplaySafeBoundaryAtTargetV1,
        estimatorId,
        new HistoryLoadUnit(target),
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
            "atelia-history-timeline-reconcile-tests",
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
