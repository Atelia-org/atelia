using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.SessionJournal;
using Atelia.SessionJournal.HistoryTimeline;

namespace Atelia.SessionJournal.HistoryTimeline.Tests;

public sealed class HistoryTimelineOnlineRawIntegrationTests
    : IDisposable {
    private readonly List<string> _paths = [];
    private readonly IHistoryUnitLoadEstimator _estimator =
        new O200kBaseHistoryUnitLoadEstimator();

    [Fact]
    public void FreshPlan_ExactRematerializationCommitsOpaqueCandidate() {
        string path = NewPath();
        using var writer = CreateWriter(path);
        EventAddress observation = writer.AppendObservation(
            "first observation"
        );
        _ = writer.AppendImportedAgentAction(
            new ActionMessage([new ActionBlock.Text("first answer")]),
            new CompletionDescriptor("import", "v1", "model-A")
        );
        PartitionPolicyRevision policy = Policy('a', maxRawEvents: 8);
        var ledger = new InMemoryHistoryTimelineLedger(
            writer.BranchRefId,
            policy
        );
        var coordinator = new HistoryTimelineCoordinator(
            path,
            ledger,
            _estimator
        );
        TimelineHeadRef before = coordinator.ReadSnapshot();
        OnlineSelectedRawCapture capture = Capture(
            coordinator,
            before,
            writer.ReadView
        );

        HistoryTimelinePlanResult.Selected selected = Assert.IsType<
            HistoryTimelinePlanResult.Selected
        >(coordinator.PlanNextRow(
            before,
            capture
        ));

        HistorySegmentDescriptor descriptor =
            selected.Candidate.Proposal.Descriptor;
        Assert.Equal(observation, descriptor.EndInclusive);
        Assert.Equal(1, descriptor.RawEventCount);
        Assert.False(string.IsNullOrWhiteSpace(
            descriptor.RawRangeSha256
        ));
        HistoryTimelineCommitResult.Committed committed = Assert.IsType<
            HistoryTimelineCommitResult.Committed
        >(coordinator.CommitRow(selected.Candidate));
        Assert.Equal(descriptor.RowId, committed.Head.HeadRowId);
        Assert.Equal(policy.PolicyDigest,
            committed.Head.ActivePartitionPolicyDigest);
        Assert.Equal(capture.CapturedHead,
            committed.Head.SelectedRawHeadAtCommit);
        Assert.Same(descriptor, ledger.ReadRow(descriptor.RowId));
    }

    [Fact]
    public void CaptureEmpty_IsTypedAndDoesNotForgeRawAuthority() {
        string path = NewPath();
        RefId emptyRef;
        using (EventJournal.EventJournal journal =
               EventJournal.EventJournal.CreateNew(path)) {
            emptyRef = journal.CreateBranch("empty", null).Unwrap();
        }
        using SessionJournalEngine empty =
            SessionJournalEngine.OpenReadOnly(path, "empty");
        PartitionPolicyRevision policy = Policy('0', 8);
        var ledger = new InMemoryHistoryTimelineLedger(
            emptyRef,
            policy
        );
        var coordinator = new HistoryTimelineCoordinator(
            path,
            ledger,
            _estimator
        );

        var result = Assert.IsType<
            OnlineSelectedRawCaptureResult.Empty
        >(coordinator.CaptureOnline(
            coordinator.ReadSnapshot(),
            empty.ReadView
        ));

        Assert.Equal(emptyRef, result.RefId);
    }

    [Fact]
    public void CaptureOnline_RejectsWrongRefForEmptyAndNonEmptyRaw() {
        string ownerPath = NewPath();
        RefId ownerRef;
        EventAddress ownerHead;
        using (SessionJournalEngine owner = CreateWriter(ownerPath)) {
            ownerRef = owner.BranchRefId;
            ownerHead = owner.ReadCurrentHead()!.Value;
        }
        RefId emptyRef;
        RefId nonEmptyRef;
        using (EventJournal.EventJournal journal =
               EventJournal.EventJournal.OpenExisting(ownerPath)) {
            emptyRef = journal.CreateBranch(
                "wrong-empty",
                null
            ).Unwrap();
            nonEmptyRef = journal.ForkBranch(
                "wrong-nonempty",
                ownerRef,
                ownerHead
            ).Unwrap();
        }
        PartitionPolicyRevision policy = Policy('1', 8);
        var coordinator = new HistoryTimelineCoordinator(
            ownerPath,
            new InMemoryHistoryTimelineLedger(
                ownerRef,
                policy
            ),
            _estimator
        );
        TimelineHeadRef expected = coordinator.ReadSnapshot();

        using (SessionJournalEngine empty =
               SessionJournalEngine.OpenReadOnly(
                   ownerPath,
                   "wrong-empty"
               )) {
            var rejected = Assert.IsType<
                OnlineSelectedRawCaptureResult.Invalid
            >(coordinator.CaptureOnline(expected, empty.ReadView));
            Assert.Equal("RawRefMismatch", rejected.Code);
            Assert.NotEqual(ownerRef, emptyRef);
        }

        using var other = SessionJournalEngine.OpenReadOnly(
            ownerPath,
            "wrong-nonempty"
        );
        var nonEmptyRejected = Assert.IsType<
            OnlineSelectedRawCaptureResult.Invalid
        >(coordinator.CaptureOnline(expected, other.ReadView));
        Assert.Equal("RawRefMismatch", nonEmptyRejected.Code);
        Assert.NotEqual(ownerRef, nonEmptyRef);
    }

    [Fact]
    public void CaptureOnline_RejectsClonedRepositoryWithSameRefAndHead() {
        string ownerPath = NewPath();
        string clonePath = NewPath();
        using var owner = CreateWriter(ownerPath);
        using var clone = CreateWriter(clonePath);
        Assert.Equal(owner.BranchRefId, clone.BranchRefId);
        Assert.Equal(owner.ReadCurrentHead(), clone.ReadCurrentHead());
        PartitionPolicyRevision policy = Policy('5', 8);
        var coordinator = new HistoryTimelineCoordinator(
            ownerPath,
            new InMemoryHistoryTimelineLedger(
                owner.BranchRefId,
                policy
            ),
            _estimator
        );

        var rejected = Assert.IsType<
            OnlineSelectedRawCaptureResult.Invalid
        >(coordinator.CaptureOnline(
            coordinator.ReadSnapshot(),
            clone.ReadView
        ));

        Assert.Equal("RawRepositoryMismatch", rejected.Code);
    }

    [Fact]
    public void CaptureBoundToOldWholeHead_IsRejectedAfterPolicyCas() {
        string path = NewPath();
        using var writer = CreateWriter(path);
        _ = writer.AppendObservation("first");
        PartitionPolicyRevision initial = Policy('2', 8);
        PartitionPolicyRevision next = PartitionPolicyRevision.Create(
            initial.TimelineId,
            HistoryPartitionAlgorithms.FirstReplaySafeBoundaryAtTargetV1,
            O200kBaseHistoryUnitLoadEstimator.EstimatorId,
            new HistoryLoadUnit(2),
            8,
            1024 * 1024
        );
        var coordinator = new HistoryTimelineCoordinator(
            path,
            new InMemoryHistoryTimelineLedger(
                writer.BranchRefId,
                initial
            ),
            _estimator
        );
        TimelineHeadRef oldHead = coordinator.ReadSnapshot();
        OnlineSelectedRawCapture oldCapture = Capture(
            coordinator,
            oldHead,
            writer.ReadView
        );
        _ = coordinator.PutPolicy(next);
        TimelineHeadRef newHead = Assert.IsType<
            HistoryTimelinePolicyCasResult.Applied
        >(coordinator.CompareExchangePolicy(
            oldHead,
            next.PolicyDigest
        )).Head;

        var invalid = Assert.IsType<HistoryTimelinePlanResult.Invalid>(
            coordinator.PlanNextRow(newHead, oldCapture)
        );
        Assert.Equal("RawScopeMismatch", invalid.Code);
    }

    [Fact]
    public void ExactRepartitionMismatch_IsClosedInvalid() {
        string path = NewPath();
        using var writer = CreateWriter(path);
        _ = writer.AppendObservation("first");
        PartitionPolicyRevision policy = Policy('3', 8);
        var estimator = new ChangingEstimator();
        var coordinator = new HistoryTimelineCoordinator(
            path,
            new InMemoryHistoryTimelineLedger(
                writer.BranchRefId,
                policy
            ),
            estimator
        );
        TimelineHeadRef expected = coordinator.ReadSnapshot();

        var invalid = Assert.IsType<HistoryTimelinePlanResult.Invalid>(
            coordinator.PlanNextRow(
                expected,
                Capture(coordinator, expected, writer.ReadView)
            )
        );

        Assert.Equal("ExactRematerializationMismatch", invalid.Code);
        Assert.True(estimator.MeasureCount >= 2);
    }

    [Fact]
    public void PlanTerminal_RechecksWholeTimelineHead() {
        string path = NewPath();
        using var writer = CreateWriter(path);
        _ = writer.AppendObservation("first");
        PartitionPolicyRevision initial = Policy('4', 8);
        PartitionPolicyRevision next = PartitionPolicyRevision.Create(
            initial.TimelineId,
            HistoryPartitionAlgorithms.FirstReplaySafeBoundaryAtTargetV1,
            O200kBaseHistoryUnitLoadEstimator.EstimatorId,
            new HistoryLoadUnit(2),
            8,
            1024 * 1024
        );
        var ledger = new InMemoryHistoryTimelineLedger(
            writer.BranchRefId,
            initial
        );
        HistoryTimelineCoordinator? coordinator = null;
        TimelineHeadRef? expected = null;
        var estimator = new CallbackEstimator(() => {
            _ = coordinator!.CompareExchangePolicy(
                expected!,
                next.PolicyDigest
            );
        });
        coordinator = new HistoryTimelineCoordinator(
            path,
            ledger,
            estimator
        );
        expected = coordinator.ReadSnapshot();
        _ = coordinator.PutPolicy(next);
        OnlineSelectedRawCapture capture = Capture(
            coordinator,
            expected,
            writer.ReadView
        );

        var stale = Assert.IsType<
            HistoryTimelinePlanResult.StaleTimelineHead
        >(coordinator.PlanNextRow(expected, capture));

        Assert.Equal(next.PolicyDigest,
            stale.Actual.ActivePartitionPolicyDigest);
        Assert.Null(stale.Actual.HeadRowId);
    }

    [Fact]
    public void SuccessorPrefix_DistinguishesFoundBeyondAndOffLineage() {
        string path = NewPath();
        using var writer = CreateWriter(path);
        _ = writer.AppendObservation("first");
        _ = writer.AppendImportedAgentAction(
            new ActionMessage([new ActionBlock.Text("one")]),
            new CompletionDescriptor("import", "v1", "model-A")
        );
        PartitionPolicyRevision policy = Policy('b', maxRawEvents: 4);
        var ledger = new InMemoryHistoryTimelineLedger(
            writer.BranchRefId,
            policy
        );
        var coordinator = new HistoryTimelineCoordinator(
            path,
            ledger,
            _estimator
        );
        TimelineHeadRef initialHead = coordinator.ReadSnapshot();
        HistoryTimelinePlanResult.Selected firstPlan = Assert.IsType<
            HistoryTimelinePlanResult.Selected
        >(coordinator.PlanNextRow(
            initialHead,
            Capture(coordinator, initialHead, writer.ReadView)
        ));
        HistorySegmentDescriptor first = firstPlan.Candidate.Proposal
            .Descriptor;
        _ = Assert.IsType<HistoryTimelineCommitResult.Committed>(
            coordinator.CommitRow(firstPlan.Candidate)
        );

        _ = writer.AppendObservation("second");
        _ = writer.AppendImportedAgentAction(
            new ActionMessage([new ActionBlock.Text("two")]),
            new CompletionDescriptor("import", "v1", "model-A")
        );
        TimelineHeadRef selectedHead = coordinator.ReadSnapshot();
        HistoryTimelinePlanResult found = coordinator.PlanNextRow(
            selectedHead,
            Capture(coordinator, selectedHead, writer.ReadView)
        );
        Assert.IsType<HistoryTimelinePlanResult.Selected>(found);

        for (int index = 0; index < 5; index++) {
            _ = writer.AppendSystemPromptSetup($"later-{index}");
        }
        HistoryTimelinePlanResult beyond = coordinator.PlanNextRow(
            selectedHead,
            Capture(coordinator, selectedHead, writer.ReadView)
        );
        Assert.IsType<
            HistoryTimelinePlanResult.OfflineBootstrapRequired
        >(beyond);

        EventAddress current = writer.ReadCurrentHead()!.Value;
        Assert.True(writer.MoveCurrentHeadForTest(
            current,
            first.StartExclusive
        ));
        _ = writer.AppendSystemPromptSetup("sibling");
        HistoryTimelinePlanResult offLineage = coordinator.PlanNextRow(
            selectedHead,
            Capture(coordinator, selectedHead, writer.ReadView)
        );
        Assert.IsType<HistoryTimelinePlanResult.OffLineage>(offLineage);
    }

    [Fact]
    public void RawDriftBeforeCommit_IsTypedAndLedgerRemainsUnchanged() {
        string path = NewPath();
        using var writer = CreateWriter(path);
        EventAddress observation = writer.AppendObservation("first");
        EventAddress action = writer.AppendImportedAgentAction(
            new ActionMessage([new ActionBlock.Text("one")]),
            new CompletionDescriptor("import", "v1", "model-A")
        );
        PartitionPolicyRevision policy = Policy('c', maxRawEvents: 8);
        var ledger = new InMemoryHistoryTimelineLedger(
            writer.BranchRefId,
            policy
        );
        var coordinator = new HistoryTimelineCoordinator(
            path,
            ledger,
            _estimator
        );
        TimelineHeadRef before = coordinator.ReadSnapshot();
        HistoryTimelinePlanResult.Selected selected = Assert.IsType<
            HistoryTimelinePlanResult.Selected
        >(coordinator.PlanNextRow(
            before,
            Capture(coordinator, before, writer.ReadView)
        ));
        Assert.True(writer.MoveCurrentHeadForTest(action, observation));

        var changed = Assert.IsType<
            HistoryTimelineCommitResult.RawHeadChanged
        >(coordinator.CommitRow(selected.Candidate));

        Assert.Equal(action, changed.Expected);
        Assert.Equal(observation, changed.Observed);
        Assert.Equal(before, ledger.ReadSnapshot());
        Assert.Null(ledger.ReadRow(
            selected.Candidate.Proposal.Descriptor.RowId
        ));
    }

    [Fact]
    public void RawDriftBeforePlan_IsTyped() {
        string path = NewPath();
        using var writer = CreateWriter(path);
        EventAddress observation = writer.AppendObservation("first");
        EventAddress action = writer.AppendImportedAgentAction(
            new ActionMessage([new ActionBlock.Text("one")]),
            new CompletionDescriptor("import", "v1", "model-A")
        );
        PartitionPolicyRevision policy = Policy('6', 8);
        var coordinator = new HistoryTimelineCoordinator(
            path,
            new InMemoryHistoryTimelineLedger(
                writer.BranchRefId,
                policy
            ),
            _estimator
        );
        TimelineHeadRef expected = coordinator.ReadSnapshot();
        OnlineSelectedRawCapture capture = Capture(
            coordinator,
            expected,
            writer.ReadView
        );
        Assert.True(writer.MoveCurrentHeadForTest(
            action,
            observation
        ));

        var changed = Assert.IsType<
            HistoryTimelinePlanResult.RawHeadChanged
        >(coordinator.PlanNextRow(expected, capture));

        Assert.Equal(action, changed.Expected);
        Assert.Equal(observation, changed.Observed);
    }

    [Fact]
    public void RawDriftDuringOuterToExactRematerialization_IsTyped() {
        string path = NewPath();
        using var writer = CreateWriter(path);
        EventAddress observation = writer.AppendObservation("first");
        EventAddress action = writer.AppendImportedAgentAction(
            new ActionMessage([new ActionBlock.Text("one")]),
            new CompletionDescriptor("import", "v1", "model-A")
        );
        PartitionPolicyRevision policy = Policy('7', 8);
        var estimator = new CallbackEstimator(() => {
            Assert.True(writer.MoveCurrentHeadForTest(
                action,
                observation
            ));
        });
        var coordinator = new HistoryTimelineCoordinator(
            path,
            new InMemoryHistoryTimelineLedger(
                writer.BranchRefId,
                policy
            ),
            estimator
        );
        TimelineHeadRef expected = coordinator.ReadSnapshot();
        OnlineSelectedRawCapture capture = Capture(
            coordinator,
            expected,
            writer.ReadView
        );

        var changed = Assert.IsType<
            HistoryTimelinePlanResult.RawHeadChanged
        >(coordinator.PlanNextRow(expected, capture));

        Assert.Equal(action, changed.Expected);
        Assert.Equal(observation, changed.Observed);
    }

    [Fact]
    public void PolicyCas_IsSeparateFromAppendAndComparesWholeHead() {
        string path = NewPath();
        using var writer = CreateWriter(path);
        _ = writer.AppendObservation("first");
        _ = writer.AppendImportedAgentAction(
            new ActionMessage([new ActionBlock.Text("one")]),
            new CompletionDescriptor("import", "v1", "model-A")
        );
        PartitionPolicyRevision initial = Policy('d', 8);
        PartitionPolicyRevision next = PartitionPolicyRevision.Create(
            initial.TimelineId,
            HistoryPartitionAlgorithms.FirstReplaySafeBoundaryAtTargetV1,
            O200kBaseHistoryUnitLoadEstimator.EstimatorId,
            new HistoryLoadUnit(2),
            maxRawEvents: 8,
            maxRenderedBytes: 1024 * 1024
        );
        var ledger = new InMemoryHistoryTimelineLedger(
            writer.BranchRefId,
            initial
        );
        var coordinator = new HistoryTimelineCoordinator(
            path,
            ledger,
            _estimator
        );
        TimelineHeadRef generationZero = coordinator.ReadSnapshot();
        Assert.IsType<HistoryTimelinePolicyPutResult.Stored>(
            coordinator.PutPolicy(next)
        );
        TimelineHeadRef policyHead = Assert.IsType<
            HistoryTimelinePolicyCasResult.Applied
        >(coordinator.CompareExchangePolicy(
            generationZero,
            next.PolicyDigest
        )).Head;
        Assert.Null(policyHead.HeadRowId);
        Assert.Null(policyHead.SelectedRawHeadAtCommit);
        Assert.Equal(1, policyHead.Generation);

        HistoryTimelinePlanResult.Selected selected = Assert.IsType<
            HistoryTimelinePlanResult.Selected
        >(coordinator.PlanNextRow(
            policyHead,
            Capture(coordinator, policyHead, writer.ReadView)
        ));
        TimelineHeadRef committed = Assert.IsType<
            HistoryTimelineCommitResult.Committed
        >(coordinator.CommitRow(selected.Candidate)).Head;

        Assert.Equal(next.PolicyDigest,
            committed.ActivePartitionPolicyDigest);
        Assert.IsType<
            HistoryTimelinePolicyCasResult.StaleTimelineHead
        >(coordinator.CompareExchangePolicy(
            policyHead,
            initial.PolicyDigest
        ));
    }

    [Fact]
    public void SamePolicyCasAdvancesGenerationAndInvalidatesOldCapture() {
        string path = NewPath();
        using var writer = CreateWriter(path);
        _ = writer.AppendObservation("first");
        PartitionPolicyRevision policy = Policy('8', 8);
        var coordinator = new HistoryTimelineCoordinator(
            path,
            new InMemoryHistoryTimelineLedger(
                writer.BranchRefId,
                policy
            ),
            _estimator
        );
        TimelineHeadRef generationZero = coordinator.ReadSnapshot();
        OnlineSelectedRawCapture oldCapture = Capture(
            coordinator,
            generationZero,
            writer.ReadView
        );

        TimelineHeadRef generationOne = Assert.IsType<
            HistoryTimelinePolicyCasResult.Applied
        >(coordinator.CompareExchangePolicy(
            generationZero,
            policy.PolicyDigest
        )).Head;

        Assert.Equal(1, generationOne.Generation);
        Assert.Equal(policy.PolicyDigest,
            generationOne.ActivePartitionPolicyDigest);
        Assert.IsType<
            HistoryTimelinePolicyCasResult.StaleTimelineHead
        >(coordinator.CompareExchangePolicy(
            generationZero,
            policy.PolicyDigest
        ));
        var invalid = Assert.IsType<HistoryTimelinePlanResult.Invalid>(
            coordinator.PlanNextRow(generationOne, oldCapture)
        );
        Assert.Equal("RawScopeMismatch", invalid.Code);
    }

    [Fact]
    public void SessionCreatedOnlyAndBoundedTailReturnTypedPartitionTerminals() {
        string createdOnlyPath = NewPath();
        using var createdOnly = CreateWriter(createdOnlyPath);
        var estimator = new FixedEstimator();
        PartitionPolicyRevision createdPolicy = Policy(
            new TimelineId(new string('9', 32)),
            estimator.Id,
            target: 2,
            maxRawEvents: 2
        );
        var createdCoordinator = new HistoryTimelineCoordinator(
            createdOnlyPath,
            new InMemoryHistoryTimelineLedger(
                createdOnly.BranchRefId,
                createdPolicy
            ),
            estimator
        );
        TimelineHeadRef createdHead = createdCoordinator.ReadSnapshot();

        var createdNotEnough = Assert.IsType<
            HistoryTimelinePlanResult.NotEnough
        >(createdCoordinator.PlanNextRow(
            createdHead,
            Capture(
                createdCoordinator,
                createdHead,
                createdOnly.ReadView
            )
        ));
        Assert.Equal(0, createdNotEnough.Partition.RawEventCount);

        string notEnoughPath = NewPath();
        using var notEnoughWriter = CreateWriter(notEnoughPath);
        _ = notEnoughWriter.AppendObservation("one");
        var notEnoughCoordinator = new HistoryTimelineCoordinator(
            notEnoughPath,
            new InMemoryHistoryTimelineLedger(
                notEnoughWriter.BranchRefId,
                createdPolicy
            ),
            estimator
        );
        TimelineHeadRef notEnoughHead =
            notEnoughCoordinator.ReadSnapshot();
        var notEnough = Assert.IsType<
            HistoryTimelinePlanResult.NotEnough
        >(notEnoughCoordinator.PlanNextRow(
            notEnoughHead,
            Capture(
                notEnoughCoordinator,
                notEnoughHead,
                notEnoughWriter.ReadView
            )
        ));
        Assert.Equal(1, notEnough.Partition.RawEventCount);

        string limitPath = NewPath();
        using var limitWriter = CreateWriter(limitPath);
        _ = limitWriter.AppendObservation("one");
        _ = limitWriter.AppendImportedAgentAction(
            new ActionMessage([new ActionBlock.Text("one")]),
            new CompletionDescriptor("import", "v1", "model-A")
        );
        PartitionPolicyRevision limitPolicy = Policy(
            new TimelineId(new string('a', 32)),
            estimator.Id,
            target: 3,
            maxRawEvents: 2
        );
        var limitCoordinator = new HistoryTimelineCoordinator(
            limitPath,
            new InMemoryHistoryTimelineLedger(
                limitWriter.BranchRefId,
                limitPolicy
            ),
            estimator
        );
        TimelineHeadRef limitHead = limitCoordinator.ReadSnapshot();
        var limit = Assert.IsType<
            HistoryTimelinePlanResult.LimitExceeded
        >(limitCoordinator.PlanNextRow(
            limitHead,
            Capture(
                limitCoordinator,
                limitHead,
                limitWriter.ReadView
            )
        ));
        Assert.Equal(HistoryPartitionLimitKind.MaxRawEvents,
            limit.Partition.Limit);
    }

    [Fact]
    public void UnknownPartitionAlgorithmIsTypedAcrossPlanOfflineAndOpen() {
        string path = NewPath();
        RefId refId;
        using (SessionJournalEngine writer = CreateWriter(path)) {
            refId = writer.BranchRefId;
            _ = writer.AppendObservation("one");
        }
        var estimator = new FixedEstimator();
        TimelineId timelineId = new(new string('e', 32));
        PartitionPolicyRevision unknown = PartitionPolicyRevision.Create(
            timelineId,
            "test.partition.unknown.v1",
            estimator.Id,
            new HistoryLoadUnit(1),
            maxRawEvents: 4,
            maxRenderedBytes: 1024 * 1024
        );
        var ledger = new InMemoryHistoryTimelineLedger(refId, unknown);
        var coordinator = new HistoryTimelineCoordinator(
            path,
            ledger,
            estimator
        );
        TimelineHeadRef expected = coordinator.ReadSnapshot();
        using var offline = SessionJournalEngine.OpenReadOnly(path);
        OnlineSelectedRawCapture unknownCapture = Capture(
            coordinator,
            expected,
            offline.ReadView
        );

        var planUnavailable = Assert.IsType<
            HistoryTimelinePlanResult.PartitionAlgorithmUnavailable
        >(coordinator.PlanNextRow(expected, unknownCapture));
        Assert.Equal(unknown.PartitionAlgorithmId,
            planUnavailable.AlgorithmId);
        Assert.Equal(expected, coordinator.ReadSnapshot());

        using SessionSelectedLineageForwardCursor cursor =
            OpenCursor(offline, pageSize: 2);
        HistoryTimelineOfflineBuilder builder = Assert.IsType<
            HistoryTimelineOfflineBuilderOpenResult.Opened
        >(coordinator.OpenOfflineBuilder(expected, cursor)).Builder;
        var offlineUnavailable = Assert.IsType<
            HistoryTimelineOfflineStepResult
                .PartitionAlgorithmUnavailable
        >(builder.BuildNextRow(expected));
        Assert.Equal(unknown.PartitionAlgorithmId,
            offlineUnavailable.AlgorithmId);
        Assert.Equal(expected, coordinator.ReadSnapshot());

        PartitionPolicyRevision supported = PartitionPolicyRevision.Create(
            timelineId,
            HistoryPartitionAlgorithms
                .FirstReplaySafeBoundaryAtTargetV1,
            estimator.Id,
            new HistoryLoadUnit(1),
            maxRawEvents: 4,
            maxRenderedBytes: 1024 * 1024
        );
        var supportedCoordinator = new HistoryTimelineCoordinator(
            path,
            new InMemoryHistoryTimelineLedger(refId, supported),
            estimator
        );
        TimelineHeadRef supportedHead =
            supportedCoordinator.ReadSnapshot();
        OnlineSelectedRawCapture supportedCapture = Capture(
            supportedCoordinator,
            supportedHead,
            offline.ReadView
        );
        HistorySegmentDescriptor template = Assert.IsType<
            HistoryTimelinePlanResult.Selected
        >(supportedCoordinator.PlanNextRow(
            supportedHead,
            supportedCapture
        )).Candidate.Proposal.Descriptor;
        var point = new HistoryPartitionPoint(
            timelineId,
            unknown.PolicyDigest,
            template.StartExclusive,
            template.EndInclusive,
            template.StartSetups,
            template.EndSetups,
            baselineCompletedUnitCount: 0,
            endCompletedUnitCount: 1,
            template.MeasuredHistoryLoad,
            template.RawEventCount,
            template.MeasuredRenderedUtf8Bytes
        );
        var bound = new BoundHistorySegmentRange(
            refId,
            point.StartExclusive,
            point.EndInclusive,
            point.StartSetups,
            point.EndSetups,
            point.BaselineCompletedUnitCount,
            point.EndCompletedUnitCount,
            point.RawEventCount,
            template.RawRangeSha256
        );
        HistorySegmentDescriptor unknownDescriptor =
            HistorySegmentDescriptorFactory.Create(
                point,
                bound,
                unknown,
                predecessor: null
            );
        var unknownProposal = new HistoryRowProposal(
            expected,
            unknownCapture.CapturedHead,
            unknownDescriptor
        );
        var commitCandidate = new HistoryRowCommitCandidate(
            unknownProposal,
            unknownCapture
        );
        TimelineHeadRef committed = Assert.IsType<
            HistoryTimelineCommitResult.Committed
        >(coordinator.CommitRow(commitCandidate)).Head;
        OnlineSelectedRawCapture openCapture = Capture(
            coordinator,
            committed,
            offline.ReadView
        );

        var openUnavailable = Assert.IsType<
            HistorySegmentOpenResult.PartitionAlgorithmUnavailable
        >(coordinator.OpenSegment(
            committed,
            openCapture,
            unknownDescriptor.RowId
        ));
        Assert.Equal(unknown.PartitionAlgorithmId,
            openUnavailable.AlgorithmId);
        Assert.Equal(committed, coordinator.ReadSnapshot());
    }

    public void Dispose() {
        foreach (string path in _paths) {
            if (Directory.Exists(path)) {
                Directory.Delete(path, recursive: true);
            }
        }
    }

    private SessionJournalEngine CreateWriter(string path)
        => SessionJournalEngine.Create(
            path,
            new SessionCreateOptions(
                "model-A",
                "system-A",
                "surface-A"
            )
        );

    private static OnlineSelectedRawCapture Capture(
        HistoryTimelineCoordinator coordinator,
        TimelineHeadRef expectedWholeHead,
        SessionJournalReadView readView
    ) => Assert.IsType<OnlineSelectedRawCaptureResult.Captured>(
        coordinator.CaptureOnline(
            expectedWholeHead,
            readView
        )
    ).Capture;

    private static PartitionPolicyRevision Policy(
        char timelineDigit,
        int maxRawEvents
    ) => PartitionPolicyRevision.Create(
        new TimelineId(new string(timelineDigit, 32)),
        HistoryPartitionAlgorithms.FirstReplaySafeBoundaryAtTargetV1,
        O200kBaseHistoryUnitLoadEstimator.EstimatorId,
        new HistoryLoadUnit(1),
        maxRawEvents,
        maxRenderedBytes: 1024 * 1024
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
        maxRenderedBytes: 1024 * 1024
    );

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

    private string NewPath() {
        string path = Path.Combine(
            Directory.Exists("/dev/shm")
                ? "/dev/shm"
                : Path.GetTempPath(),
            "atelia-history-timeline-online-tests",
            Guid.NewGuid().ToString("N")
        );
        _paths.Add(path);
        return path;
    }

    private sealed class ChangingEstimator
        : IHistoryUnitLoadEstimator {
        private readonly O200kBaseHistoryUnitLoadEstimator _inner =
            new();

        public string Id =>
            O200kBaseHistoryUnitLoadEstimator.EstimatorId;
        public int MeasureCount { get; private set; }

        public HistoryUnitLoadMeasurement Measure(
            SessionHistoryPlanningUnit unit,
            int maxRenderedUtf8Bytes
        ) {
            HistoryUnitLoadMeasurement measured = _inner.Measure(
                unit,
                maxRenderedUtf8Bytes
            );
            MeasureCount++;
            return MeasureCount == 1
                ? measured
                : measured with {
                    Load = new HistoryLoadUnit(
                        measured.Load.Value + 1
                    )
                };
        }
    }

    private sealed class CallbackEstimator(Action callback)
        : IHistoryUnitLoadEstimator {
        private readonly O200kBaseHistoryUnitLoadEstimator _inner =
            new();
        private bool _called;

        public string Id =>
            O200kBaseHistoryUnitLoadEstimator.EstimatorId;

        public HistoryUnitLoadMeasurement Measure(
            SessionHistoryPlanningUnit unit,
            int maxRenderedUtf8Bytes
        ) {
            if (!_called) {
                _called = true;
                callback();
            }
            return _inner.Measure(unit, maxRenderedUtf8Bytes);
        }
    }

    private sealed class FixedEstimator : IHistoryUnitLoadEstimator {
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
