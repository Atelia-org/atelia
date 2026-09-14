using System.Text.Json;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid.Cadence;
using Atelia.SessionJournal.RecapGrid.Control;
using Atelia.SessionJournal.RecapGrid.Manager;
using Atelia.SessionJournal.RecapGrid.Store;
using Xunit;

namespace Atelia.SessionJournal.RecapGrid.AgentControl.Tests;

public sealed class PromotionStoreResetTests : IDisposable {
    private readonly string _path = Path.Combine(Path.GetTempPath(),
        "atelia-promotion-store-reset-tests", Guid.NewGuid().ToString("N"));
    private readonly O200kBaseHistoryUnitLoadEstimator _estimator = new();

    [Fact]
    public async Task CommittedPromotionReceipt_ResetStoreBlocksToolRetryBeforeReceiptReplay() {
        using (SessionJournalLegacyImportWriter import = SessionJournalLegacyImportWriter.Create(
                   _path, new SessionCreateOptions("model", "system", "promotion-reset"))) {
            for (int index = 0; index < 2; index++) {
                import.AppendObservation($"observation-{index}");
                import.AppendImportedAgentAction(new ActionMessage([new ActionBlock.Text($"answer-{index}")]),
                    new CompletionDescriptor("import", "v1", "model"));
            }
        }
        using SessionJournalEngine journal = SessionJournalEngine.Open(_path);
        (TimelineHeadRef timelineHead, HistoryTimelineSelectedRow through) = SealHistory(journal);
        Assert.True(RecapGridAgentControlBuiltIns.TryCreateRegistrationBundle(
            RecapGridAgentControlBuiltIns.MysteryInvestigationV4,
            out RecapGridControlRegistrationBundle? builtIn));
        MaintainerDefinitionRevision definition = builtIn!.Definitions[0];
        var admission = new RecapGridControlAdmission(RecapGridControlPermission.All,
            [builtIn.Families[0].Digest], [definition.Capability.CapabilityFingerprint],
            [ContextHeaderCarrier.System], ["case."], maximumBootstrapRows: 64,
            maximumProjectedCalls: 128);
        ControlHeadRef initial = Assert.IsType<RecapGridControlCreateResult.Created>(
            RecapGridControlFactory.Create(_path, journal.BranchRefId, admission)).Head;
        GridBuildRecipe recipe = GridBuildRecipe.CreateFull(timelineHead.TimelineId,
            through.Descriptor.RowId,
            BuildTarget.Create([new BuildTargetColumn(definition.LogicalColumnId, definition.Digest)]));
        using (RecapGridControlHandle control = Assert.IsType<RecapGridControlOpenResult.Opened>(
                   RecapGridControlFactory.Open(_path, journal.BranchRefId, admission)).Handle) {
            ControlHeadRef family = Assert.IsType<RecapGridControlPutResult.Stored>(
                control.Coordinator.PutFamilyDefinition(initial, builtIn.Families[0])).Head;
            ControlHeadRef defined = Assert.IsType<RecapGridControlPutResult.Stored>(
                control.Coordinator.PutMaintainerDefinition(family, definition)).Head;
            Assert.IsType<RecapGridControlPutResult.Stored>(control.Coordinator.PutBuildRecipe(
                defined, timelineHead, recipe, through.Witness));
        }
        Assert.IsType<RecapGridStoreCreateResult.Created>(RecapGridStoreFactory.Create(_path));
        var executor = new RecordingExecutor();
        var request = new RecapGridBuildRequest(new RecapGridBuildSelection.ExplicitCandidate(recipe.Digest),
            timelineHead.HeadRowId, new RecapGridBuildBudget(128, 128, TimeSpan.FromMinutes(1)));
        using (RecapGridManagerHandle manager = Assert.IsType<RecapGridManagerOpenResult.Opened>(
                   RecapGridManagerFactory.Open(journal.ReadView, _estimator)).Handle) {
            Assert.IsType<RecapGridBuildResult.Fulfilled>(await manager.Manager.BuildAsync(request, executor,
                TestContext.Current.CancellationToken));
            RecapGridBuildProgressResult.Complete progress = Assert.IsType<RecapGridBuildProgressResult.Complete>(
                manager.Manager.InspectBuildProgress(request));
            Assert.True(progress.FulfillmentPresent);
            Assert.NotNull(progress.Proof);
        }
        Assert.True(executor.Calls > 0);
        int completedCalls = executor.Calls;
        var call = new RawToolCall("recap_grid_control", "promotion-call",
            JsonSerializer.Serialize(new { action = "promote", recipeDigest = recipe.Digest.Value }));
        const string operationId = "promotion-without-journal-result";
        string operationKey;
        // Invoke the real tool and commit its Control receipt, but do not append
        // its ToolResult to Journal. This proves the proof-before-replay boundary;
        // it is not a Journal failpoint or a process-crash fixture.
        using (RecapGridAgentControlHandle tool = OpenTool(journal, admission)) {
            ToolCallExecutionResult applied = await tool.ToolSession.ExecuteReservedAsync(call, 1, operationId,
                TestContext.Current.CancellationToken);
            Assert.Equal(ToolExecutionStatus.Success, applied.ExecuteResult.Status);
            using JsonDocument result = JsonDocument.Parse(applied.ExecuteResult.GetFlattenedText());
            Assert.Equal("applied", result.RootElement.GetProperty("status").GetString());
            operationKey = Assert.IsType<string>(result.RootElement.GetProperty("operationKey").GetString());
        }
        string controlPath = Assert.Single(Directory.GetFiles(Path.Combine(_path, "control"), "control.json",
            SearchOption.AllDirectories));
        byte[] committedControl = File.ReadAllBytes(controlPath);
        using (JsonDocument state = JsonDocument.Parse(committedControl)) {
            JsonElement receipt = Assert.Single(state.RootElement.GetProperty("operationReceipts").EnumerateArray());
            Assert.Equal(operationKey, receipt.GetProperty("operationKey").GetString());
        }
        var journalHead = journal.ReadCurrentHead();
        RecapGridStorePhysicalWitness witness = Assert.IsType<RecapGridStorePrepareResetResult.Prepared>(
            RecapGridStoreMaintenance.PrepareReset(_path)).Witness;
        Assert.IsType<RecapGridStoreResetResult.Reset>(RecapGridStoreMaintenance.Reset(_path, witness));
        using (RecapGridAgentControlHandle reopened = OpenTool(journal, admission)) {
            ToolCallExecutionResult retried = await reopened.ToolSession.ExecuteReservedAsync(call, 1, operationId,
                TestContext.Current.CancellationToken);
            Assert.Equal(ToolExecutionStatus.Failed, retried.ExecuteResult.Status);
            using JsonDocument result = JsonDocument.Parse(retried.ExecuteResult.GetFlattenedText());
            // Promote inspects with maximumNewCalls: 0. The missing nonempty
            // candidate exceeds that budget before Control can replay its receipt.
            Assert.Equal("budget-exceeded", result.RootElement.GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, result.RootElement.GetProperty("operationKey").ValueKind);
        }
        Assert.Equal(committedControl, File.ReadAllBytes(controlPath));
        Assert.Equal(journalHead, journal.ReadCurrentHead());
        Assert.Equal(completedCalls, executor.Calls);
        RecapGridStoreInfo empty = Assert.IsType<RecapGridStoreInspectResult.Available>(
            RecapGridStoreMaintenance.Inspect(_path)).Info;
        Assert.Equal(0, empty.CellCount);
        Assert.Equal(0, empty.RowViewCount);
        Assert.Equal(0, empty.FulfilledViewCount);
    }

    private RecapGridAgentControlHandle OpenTool(SessionJournalEngine journal, RecapGridControlAdmission admission) =>
        Assert.IsType<RecapGridAgentControlOpenResult.Opened>(RecapGridAgentControlFactory.Open(
            journal.ReadView, admission, _estimator)).Handle;

    private (TimelineHeadRef Head, HistoryTimelineSelectedRow Through) SealHistory(SessionJournalEngine journal) {
        Assert.IsType<HistoryTimelineCreateResult.Created>(HistoryTimelineFactory.Create(journal.ReadView,
            new HistoryTimelineInitialPolicySpec(HistoryPartitionAlgorithms.FirstReplaySafeBoundaryAtTargetV1,
                O200kBaseHistoryUnitLoadEstimator.EstimatorId, new HistoryLoadUnit(1), 64, 1024 * 1024), _estimator));
        Assert.IsType<RecapGridCadenceCreateResult.Created>(RecapGridCadenceFactory.Create(journal,
            new RecapGridCadencePolicySpec(1, HistoryPartitionAlgorithms.FirstReplaySafeBoundaryAtTargetV1,
                O200kBaseHistoryUnitLoadEstimator.EstimatorId, 1, 64, 1024 * 1024)));
        using HistoryTimelineHandle timeline = Assert.IsType<HistoryTimelineOpenResult.Opened>(
            HistoryTimelineFactory.Open(journal.ReadView, _estimator)).Handle;
        using RecapGridCadenceHandle cadence = Assert.IsType<RecapGridCadenceOpenResult.Opened>(
            RecapGridCadenceFactory.OpenMutable(journal)).Handle;
        using RecapGridCadenceTimelineSealOperation seal = Assert.IsType<RecapGridCadenceTimelineSealOpenResult.Opened>(
            cadence.BeginTimelineSeal(timeline)).Operation;
        TimelineHeadRef head = Assert.IsType<HistoryTimelineSnapshotResult.Available>(timeline.Reader.ReadSnapshot()).Head;
        using RecapGridCadenceOfflineAudit audit = Assert.IsType<RecapGridCadenceOfflineAuditCaptureResult.Available>(
            seal.CaptureOfflineAudit(HistoryRecentReserveOperationLimits.MaximumRawEvents)).Audit;
        using RecapGridCadenceOfflineBuilder builder = Assert.IsType<RecapGridCadenceOfflineBuilderOpenResult.Opened>(
            seal.OpenOfflineBuilder(head, audit)).Builder;
        while (true) {
            HistoryTimelineOfflineStepResult step = builder.BuildNextRow(head);
            if (step is HistoryTimelineOfflineStepResult.NotEnough or HistoryTimelineOfflineStepResult.RecentReserveNotReached) {
                Assert.NotNull(head.HeadRowId);
                return (head, Assert.IsType<HistoryTimelineReaderRowResult.Selected>(
                    timeline.Reader.ReadSelectedRow(head, head.HeadRowId.Value)).Row);
            }
            head = Assert.IsType<HistoryTimelineOfflineStepResult.Committed>(step).Head;
        }
    }

    private sealed class RecordingExecutor : IRecapCellBatchExecutor {
        internal int Calls { get; private set; }
        public ValueTask<RecapCellBatchExecutionResult> ExecuteAsync(FrozenRowBatch batch,
            CancellationToken cancellationToken = default) {
            cancellationToken.ThrowIfCancellationRequested();
            Calls += batch.OrderedMissingWork.Count;
            return ValueTask.FromResult<RecapCellBatchExecutionResult>(new RecapCellBatchExecutionResult.Completed(
                batch.OrderedMissingWork.Select(work => (RecapCellExecutionOutcome)
                    new RecapCellExecutionOutcome.Updated(work.Slot, "synthetic recap")).ToArray()));
        }
    }

    public void Dispose() {
        if (Directory.Exists(_path)) {
            Directory.Delete(_path, recursive: true);
        }
    }
}
