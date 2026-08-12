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
            coordinator.ReadSnapshotRequired(),
            cursor
        )).Builder;

        HistoryTimelineOfflineStepResult.Committed first =
            Assert.IsType<
                HistoryTimelineOfflineStepResult.Committed
            >(builder.BuildNextRow(coordinator.ReadSnapshotRequired()));
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
            coordinator.ReadSnapshotRequired(),
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
            coordinator.ReadSnapshotRequired(),
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
                coordinator.ReadSnapshotRequired(),
                firstCursor
            )).Builder;
            first = Assert.IsType<
                HistoryTimelineOfflineStepResult.Committed
            >(firstBuilder.BuildNextRow(
                coordinator.ReadSnapshotRequired()
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
        TimelineHeadRef oldHead = coordinator.ReadSnapshotRequired();
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
        TimelineHeadRef before = coordinator.ReadSnapshotRequired();
        HistoryTimelineOfflineBuilder builder = Assert.IsType<
            HistoryTimelineOfflineBuilderOpenResult.Opened
        >(coordinator.OpenOfflineBuilder(before, cursor)).Builder;

        Assert.IsType<HistoryTimelineOfflineStepResult.RawHeadChanged>(
            builder.BuildNextRow(before)
        );
        Assert.Equal(before, coordinator.ReadSnapshotRequired());
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
            coordinator.ReadSnapshotRequired(),
            cursor
        )).Builder;
        HistoryTimelineOfflineStepResult.Committed first =
            Assert.IsType<
                HistoryTimelineOfflineStepResult.Committed
            >(builder.BuildNextRow(coordinator.ReadSnapshotRequired()));
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
        Assert.Equal(scheduled, coordinator.ReadSnapshotRequired());
    }

    [Theory]
    [InlineData("busy")]
    [InlineData("invalid")]
    [InlineData("unsupported")]
    [InlineData("absent")]
    public void OfflineStep_PreservesPolicyReadOutcome(string outcome) {
        string path = NewPath();
        RefId refId;
        using (SessionJournalEngine writer = CreateWriter(path)) {
            refId = writer.BranchRefId;
            _ = writer.AppendObservation("policy outcome");
        }
        using var offline = SessionJournalEngine.OpenReadOnly(path);
        using SessionSelectedLineageForwardCursor cursor =
            OpenCursor(offline, pageSize: 2);
        var estimator = new FixedEstimator();
        PartitionPolicyRevision policy = Policy(
            new TimelineId(new string('7', 32)),
            estimator.Id,
            maxRawEvents: 4
        );
        var ledger = new InMemoryHistoryTimelineLedger(refId, policy);
        var coordinator = new HistoryTimelineCoordinator(
            path,
            ledger,
            estimator
        );
        TimelineHeadRef expected = coordinator.ReadSnapshotRequired();
        HistoryTimelineOfflineBuilder builder = Assert.IsType<
            HistoryTimelineOfflineBuilderOpenResult.Opened
        >(coordinator.OpenOfflineBuilder(expected, cursor)).Builder;
        ledger.ReadPolicyOverride = _ => outcome switch {
            "busy" => new HistoryTimelineStoreReadResult<
                PartitionPolicyRevision>.Busy(),
            "invalid" => new HistoryTimelineStoreReadResult<
                PartitionPolicyRevision>.Invalid(
                    "InjectedPolicyInvalid",
                    "injected"
                ),
            "unsupported" => new HistoryTimelineStoreReadResult<
                PartitionPolicyRevision>.UnsupportedSchema(2),
            _ => new HistoryTimelineStoreReadResult<
                PartitionPolicyRevision>.Absent()
        };

        HistoryTimelineOfflineStepResult result =
            builder.BuildNextRow(expected);

        if (outcome == "busy") {
            Assert.IsType<HistoryTimelineOfflineStepResult.BackendBusy>(
                result
            );
        }
        else if (outcome == "invalid") {
            Assert.Equal(
                "InjectedPolicyInvalid",
                Assert.IsType<HistoryTimelineOfflineStepResult.Invalid>(
                    result
                ).Code
            );
        }
        else if (outcome == "unsupported") {
            Assert.Equal(
                "TimelineStoreUnsupportedSchema",
                Assert.IsType<HistoryTimelineOfflineStepResult.Invalid>(
                    result
                ).Code
            );
        }
        else {
            Assert.IsType<HistoryTimelineOfflineStepResult
                .PartitionPolicyUnavailable>(result);
        }
    }

    [Fact]
    public void OfflineEntryPoints_MapUnsupportedSnapshotSchemaToInvalid() {
        string path = NewPath();
        RefId refId;
        using (SessionJournalEngine writer = CreateWriter(path)) {
            refId = writer.BranchRefId;
            _ = writer.AppendObservation("unsupported snapshot");
        }
        using var offline = SessionJournalEngine.OpenReadOnly(path);
        using SessionSelectedLineageForwardCursor cursor =
            OpenCursor(offline, pageSize: 2);
        var estimator = new FixedEstimator();
        PartitionPolicyRevision policy = Policy(
            new TimelineId(new string('9', 32)),
            estimator.Id,
            maxRawEvents: 4
        );
        var ledger = new InMemoryHistoryTimelineLedger(refId, policy);
        var coordinator = new HistoryTimelineCoordinator(
            path,
            ledger,
            estimator
        );
        TimelineHeadRef expected = coordinator.ReadSnapshotRequired();
        ledger.ReadSnapshotOverride = () =>
            new HistoryTimelineStoreReadResult<TimelineHeadRef>
                .UnsupportedSchema(2);

        Assert.Equal(
            "TimelineStoreUnsupportedSchema",
            Assert.IsType<HistoryTimelineReconcileResult.Invalid>(
                coordinator.ReconcileSelectedPathOffline(expected, cursor)
            ).Code
        );

        ledger.ReadSnapshotOverride = null;
        HistoryTimelineOfflineBuilder builder = Assert.IsType<
            HistoryTimelineOfflineBuilderOpenResult.Opened
        >(coordinator.OpenOfflineBuilder(expected, cursor)).Builder;
        ledger.ReadSnapshotOverride = () =>
            new HistoryTimelineStoreReadResult<TimelineHeadRef>
                .UnsupportedSchema(2);

        Assert.Equal(
            "TimelineStoreUnsupportedSchema",
            Assert.IsType<HistoryTimelineOfflineStepResult.Invalid>(
                builder.BuildNextRow(expected)
            ).Code
        );
    }

    [Fact]
    public void OfflineTerminalFence_MapsUnsupportedSnapshotSchemaToInvalid() {
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
            new TimelineId(new string('a', 32)),
            estimator.Id,
            maxRawEvents: 4
        );
        var ledger = new InMemoryHistoryTimelineLedger(refId, policy);
        var coordinator = new HistoryTimelineCoordinator(
            path,
            ledger,
            estimator
        );
        TimelineHeadRef expected = coordinator.ReadSnapshotRequired();
        HistoryTimelineOfflineBuilder builder = Assert.IsType<
            HistoryTimelineOfflineBuilderOpenResult.Opened
        >(coordinator.OpenOfflineBuilder(expected, cursor)).Builder;
        int reads = 0;
        ledger.ReadSnapshotOverride = () => ++reads == 1
            ? new HistoryTimelineStoreReadResult<TimelineHeadRef>
                .Found(expected)
            : new HistoryTimelineStoreReadResult<TimelineHeadRef>
                .UnsupportedSchema(2);

        Assert.Equal(
            "TimelineStoreUnsupportedSchema",
            Assert.IsType<HistoryTimelineOfflineStepResult.Invalid>(
                builder.BuildNextRow(expected)
            ).Code
        );
        Assert.Equal(2, reads);
    }

    [Theory]
    [InlineData("busy")]
    [InlineData("invalid")]
    [InlineData("unsupported")]
    [InlineData("absent")]
    public void OfflineStep_PreservesPredecessorReadOutcome(
        string outcome
    ) {
        string path = NewPath();
        RefId refId;
        using (SessionJournalEngine writer = CreateWriter(path)) {
            refId = writer.BranchRefId;
            _ = writer.AppendObservation("first");
            _ = writer.AppendImportedAgentAction(
                new ActionMessage([new ActionBlock.Text("first answer")]),
                new CompletionDescriptor("import", "v1", "model-A")
            );
            _ = writer.AppendObservation("second");
        }
        using var offline = SessionJournalEngine.OpenReadOnly(path);
        using SessionSelectedLineageForwardCursor cursor =
            OpenCursor(offline, pageSize: 2);
        var estimator = new FixedEstimator();
        PartitionPolicyRevision policy = Policy(
            new TimelineId(new string('8', 32)),
            estimator.Id,
            maxRawEvents: 3
        );
        var ledger = new InMemoryHistoryTimelineLedger(refId, policy);
        var coordinator = new HistoryTimelineCoordinator(
            path,
            ledger,
            estimator
        );
        HistoryTimelineOfflineBuilder builder = Assert.IsType<
            HistoryTimelineOfflineBuilderOpenResult.Opened
        >(coordinator.OpenOfflineBuilder(
            coordinator.ReadSnapshotRequired(),
            cursor
        )).Builder;
        TimelineHeadRef first = Assert.IsType<
            HistoryTimelineOfflineStepResult.Committed
        >(builder.BuildNextRow(
            coordinator.ReadSnapshotRequired()
        )).Head;
        ledger.ReadRowOverride = _ => outcome switch {
            "busy" => new HistoryTimelineStoreReadResult<
                HistorySegmentDescriptor>.Busy(),
            "invalid" => new HistoryTimelineStoreReadResult<
                HistorySegmentDescriptor>.Invalid(
                    "InjectedRowInvalid",
                    "injected"
                ),
            "unsupported" => new HistoryTimelineStoreReadResult<
                HistorySegmentDescriptor>.UnsupportedSchema(2),
            _ => new HistoryTimelineStoreReadResult<
                HistorySegmentDescriptor>.Absent()
        };

        HistoryTimelineOfflineStepResult result =
            builder.BuildNextRow(first);

        if (outcome == "busy") {
            Assert.IsType<HistoryTimelineOfflineStepResult.BackendBusy>(
                result
            );
        }
        else if (outcome == "invalid") {
            Assert.Equal(
                "InjectedRowInvalid",
                Assert.IsType<HistoryTimelineOfflineStepResult.Invalid>(
                    result
                ).Code
            );
        }
        else if (outcome == "unsupported") {
            Assert.Equal(
                "TimelineStoreUnsupportedSchema",
                Assert.IsType<HistoryTimelineOfflineStepResult.Invalid>(
                    result
                ).Code
            );
        }
        else {
            Assert.Equal(
                "OfflinePredecessorUnavailable",
                Assert.IsType<HistoryTimelineOfflineStepResult.Invalid>(
                    result
                ).Code
            );
        }
    }

    [Theory]
    [InlineData("busy")]
    [InlineData("store-limit")]
    public void OfflineStep_PreservesCommitBackendOutcome(
        string outcome
    ) {
        string path = NewPath();
        RefId refId;
        using (SessionJournalEngine writer = CreateWriter(path)) {
            refId = writer.BranchRefId;
            _ = writer.AppendObservation("commit outcome");
            _ = writer.AppendImportedAgentAction(
                new ActionMessage([new ActionBlock.Text("commit answer")]),
                new CompletionDescriptor("import", "v1", "model-A")
            );
        }
        using var offline = SessionJournalEngine.OpenReadOnly(path);
        using SessionSelectedLineageForwardCursor cursor =
            OpenCursor(offline, pageSize: 2);
        var estimator = new FixedEstimator();
        PartitionPolicyRevision policy = Policy(
            new TimelineId(new string('9', 32)),
            estimator.Id,
            maxRawEvents: 4
        );
        var ledger = new InMemoryHistoryTimelineLedger(refId, policy);
        var coordinator = new HistoryTimelineCoordinator(
            path,
            ledger,
            estimator
        );
        TimelineHeadRef expected = coordinator.ReadSnapshotRequired();
        HistoryTimelineOfflineBuilder builder = Assert.IsType<
            HistoryTimelineOfflineBuilderOpenResult.Opened
        >(coordinator.OpenOfflineBuilder(expected, cursor)).Builder;
        ledger.CommitOverride = _ => outcome == "busy"
            ? new HistoryTimelineCommitResult.BackendBusy()
            : new HistoryTimelineCommitResult.LimitExceeded(
                "MaximumRowCount"
            );

        HistoryTimelineOfflineStepResult result =
            builder.BuildNextRow(expected);

        if (outcome == "busy") {
            Assert.IsType<HistoryTimelineOfflineStepResult.BackendBusy>(
                result
            );
        }
        else {
            Assert.Equal(
                "MaximumRowCount",
                Assert.IsType<HistoryTimelineOfflineStepResult
                    .StoreLimitExceeded>(result).Limit
            );
        }
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
            coordinator.ReadSnapshotRequired(),
            cursor
        )).Builder;

        var notEnough = Assert.IsType<
            HistoryTimelineOfflineStepResult.NotEnough
        >(builder.BuildNextRow(coordinator.ReadSnapshotRequired()));

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
            TimelineHeadRef expected = online.ReadSnapshotRequired();
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
            rebuilt.ReadSnapshotRequired(),
            cursor
        )).Builder;
        var offlineRows = new List<HistorySegmentDescriptor>();
        for (int index = 0; index < 2; index++) {
            HistoryTimelineOfflineStepResult.Committed committed =
                Assert.IsType<
                    HistoryTimelineOfflineStepResult.Committed
                >(builder.BuildNextRow(rebuilt.ReadSnapshotRequired()));
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

    [Fact]
    public void RecentReserve_ExtendsAcrossPageToCloseToolDependency() {
        string path = CreateToolTailFixture(completeDependency: true);
        using var offline = SessionJournalEngine.OpenReadOnly(path);
        using SessionSelectedLineageForwardCursor cursor =
            OpenCursor(offline, pageSize: 2);
        var estimator = new FixedEstimator();
        PartitionPolicyRevision policy = Policy(
            new TimelineId(new string('a', 32)),
            estimator.Id,
            maxRawEvents: 2);
        var coordinator = new HistoryTimelineCoordinator(
            path,
            new InMemoryHistoryTimelineLedger(
                offline.BranchRefId,
                policy),
            new HistoryTimelineCoordinatorTestHooks(
                RecentReserveForwardRangeEventCap: 8,
                RecentReserveInitialForwardRangeEventCount: 2),
            estimator);
        HistoryTimelineOfflineBuilder builder = OpenReserveBuilder(
            path,
            offline.BranchRefId,
            cursor,
            coordinator,
            policy,
            estimator,
            minimumRecent: 1);

        HistoryTimelineOfflineStepResult.Committed committed =
            Assert.IsType<HistoryTimelineOfflineStepResult.Committed>(
                builder.BuildNextRow(
                    coordinator.ReadSnapshotRequired()));

        Assert.Equal(2, committed.Descriptor.RawEventCount);
    }

    [Fact]
    public void RecentReserve_FinalOpenToolDependencyIsProofUnavailable() {
        string path = CreateToolTailFixture(completeDependency: false);
        using var offline = SessionJournalEngine.OpenReadOnly(path);
        using SessionSelectedLineageForwardCursor cursor =
            OpenCursor(offline, pageSize: 2);
        var estimator = new FixedEstimator();
        PartitionPolicyRevision policy = Policy(
            new TimelineId(new string('b', 32)),
            estimator.Id,
            maxRawEvents: 2);
        var coordinator = new HistoryTimelineCoordinator(
            path,
            new InMemoryHistoryTimelineLedger(
                offline.BranchRefId,
                policy),
            new HistoryTimelineCoordinatorTestHooks(
                RecentReserveForwardRangeEventCap: 8,
                RecentReserveInitialForwardRangeEventCount: 2),
            estimator);
        HistoryTimelineOfflineBuilder builder = OpenReserveBuilder(
            path,
            offline.BranchRefId,
            cursor,
            coordinator,
            policy,
            estimator,
            minimumRecent: 1);

        var unavailable = Assert.IsType<
            HistoryTimelineOfflineStepResult.RecentReserveProofUnavailable>(
                builder.BuildNextRow(
                    coordinator.ReadSnapshotRequired()));

        Assert.Equal(
            "RecentReserveTerminalOpenDependency",
            unavailable.Code);
    }

    [Fact]
    public void RecentReserve_NonFinalOpenToolDependencyAtCapIsProofUnavailable() {
        string path = CreateToolTailFixture(completeDependency: true);
        using var offline = SessionJournalEngine.OpenReadOnly(path);
        using SessionSelectedLineageForwardCursor cursor =
            OpenCursor(offline, pageSize: 2);
        var estimator = new FixedEstimator();
        PartitionPolicyRevision policy = Policy(
            new TimelineId(new string('e', 32)),
            estimator.Id,
            maxRawEvents: 2);
        var coordinator = new HistoryTimelineCoordinator(
            path,
            new InMemoryHistoryTimelineLedger(
                offline.BranchRefId,
                policy),
            new HistoryTimelineCoordinatorTestHooks(
                RecentReserveForwardRangeEventCap: 2,
                RecentReserveInitialForwardRangeEventCount: 2),
            estimator);
        HistoryTimelineOfflineBuilder builder = OpenReserveBuilder(
            path,
            offline.BranchRefId,
            cursor,
            coordinator,
            policy,
            estimator,
            minimumRecent: 1);

        var unavailable = Assert.IsType<
            HistoryTimelineOfflineStepResult.RecentReserveProofUnavailable>(
                builder.BuildNextRow(
                    coordinator.ReadSnapshotRequired()));

        Assert.Equal(
            "RecentReserveForwardRangeLimitExceeded",
            unavailable.Code);
    }

    [Fact]
    public void RecentReserve_NearLongMaxThresholdSaturatesWithoutOverflow() {
        string path = NewPath();
        using (SessionJournalEngine writer = CreateWriter(path)) {
            AppendPlainTurn(writer, "first", "first-answer");
            AppendPlainTurn(writer, "small", "small-answer");
            AppendPlainTurn(writer, "huge-reserve", "huge-answer");
        }
        using var offline = SessionJournalEngine.OpenReadOnly(path);
        using SessionSelectedLineageForwardCursor cursor =
            OpenCursor(offline, pageSize: 2);
        var estimator = new SaturatingEstimator();
        PartitionPolicyRevision policy = Policy(
            new TimelineId(new string('c', 32)),
            estimator.Id,
            maxRawEvents: 2);
        var coordinator = new HistoryTimelineCoordinator(
            path,
            new InMemoryHistoryTimelineLedger(
                offline.BranchRefId,
                policy),
            estimator);
        HistoryTimelineOfflineBuilder builder = OpenReserveBuilder(
            path,
            offline.BranchRefId,
            cursor,
            coordinator,
            policy,
            estimator,
            minimumRecent: long.MaxValue - 1);

        HistoryTimelineOfflineStepResult.Committed committed =
            Assert.IsType<HistoryTimelineOfflineStepResult.Committed>(
                builder.BuildNextRow(
                    coordinator.ReadSnapshotRequired()));

        Assert.Equal(2, committed.Descriptor.RawEventCount);
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

    private static HistoryTimelineOfflineBuilder OpenReserveBuilder(
        string path,
        RefId refId,
        SessionSelectedLineageForwardCursor cursor,
        HistoryTimelineCoordinator coordinator,
        PartitionPolicyRevision policy,
        IHistoryUnitLoadEstimator estimator,
        long minimumRecent
    ) {
        var reserve = new HistoryRecentReservePolicy(
            path,
            refId,
            cadenceGeneration: 1,
            new string('d', 64),
            policy,
            new HistoryLoadUnit(minimumRecent),
            new HistoryRecentReserveAuthorityToken());
        return Assert.IsType<
            HistoryTimelineOfflineBuilderOpenResult.Opened>(
                coordinator.OpenOfflineBuilder(
                    coordinator.ReadSnapshotRequired(),
                    cursor,
                    reserve)).Builder;
    }

    private string CreateToolTailFixture(bool completeDependency) {
        string path = NewPath();
        EventAddress firstAction;
        using (SessionJournalEngine writer = CreateWriter(path)) {
            _ = writer.AppendObservation("first");
            firstAction = writer.AppendImportedAgentAction(
                new ActionMessage([new ActionBlock.Text("first-answer")]),
                new CompletionDescriptor("import", "v1", "model-A"));
        }
        using var journal = EventJournal.EventJournal.OpenExisting(path);
        EventAddress observation = Commit(
            journal,
            firstAction,
            SessionEventKind.ObservationAccepted,
            new ObservationAcceptedBody("use tool"));
        var identity = new SessionToolRuntimeIdentity(
            "host",
            "implementations",
            "capabilities");
        EventAddress action = Commit(
            journal,
            observation,
            SessionEventKind.ImportedAgentAction,
            new AgentActionProducedBody(
                new ActionMessage([
                    new ActionBlock.ToolCall(
                        new RawToolCall("lookup", "call-1", "{}"))
                ]),
                new CompletionDescriptor("import", "v1", "model-A"),
                $"atelia.session-journal.turn.v1:{EventAddressTextCodec.Format(observation)}",
                new SessionExecutionCheckpoint(0),
                identity));
        if (completeDependency) {
            EventAddress started = Commit(
                journal,
                action,
                SessionEventKind.ToolExecutionStarted,
                new ToolExecutionStartedBody(
                    "call-1",
                    "lookup",
                    "{}",
                    "operation-1",
                    1,
                    identity));
            _ = Commit(
                journal,
                started,
                SessionEventKind.ToolResultObserved,
                new ToolResultObservedBody(
                    "call-1",
                    "lookup",
                    1,
                    ToolExecutionStatus.Success,
                    [new ToolResultBlock.Text("result")]));
        }
        return path;
    }

    private static void AppendPlainTurn(
        SessionJournalEngine writer,
        string observation,
        string answer
    ) {
        _ = writer.AppendObservation(observation);
        _ = writer.AppendImportedAgentAction(
            new ActionMessage([new ActionBlock.Text(answer)]),
            new CompletionDescriptor("import", "v1", "model-A"));
    }

    private static EventAddress Commit(
        EventJournal.EventJournal journal,
        EventAddress? expectedParent,
        SessionEventKind kind,
        object body
    ) => journal.CommitToRef(
        SessionJournalDefaults.MainBranchName,
        expectedParent,
        SessionEventCodec.Encode(kind, body),
        opaqueEventKind: (uint)kind,
        hint: default).Unwrap().EventAddress;

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

    private sealed class SaturatingEstimator
        : IHistoryUnitLoadEstimator {
        public string Id => "test.history-load.saturating.v1";

        public HistoryUnitLoadMeasurement Measure(
            SessionHistoryPlanningUnit unit,
            int maxRenderedUtf8Bytes
        ) => new(
            new HistoryLoadUnit(
                unit.Message is ObservationMessage {
                    Content: "huge-reserve"
                } ? long.MaxValue : 1),
            1);
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
