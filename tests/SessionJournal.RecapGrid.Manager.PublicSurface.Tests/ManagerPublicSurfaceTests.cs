using Atelia.Completion.Abstractions;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid.Cadence;
using Atelia.SessionJournal.RecapGrid.Control;
using Atelia.SessionJournal.RecapGrid.Manager;
using Atelia.SessionJournal.RecapGrid.Store;
using Xunit;

namespace Atelia.SessionJournal.RecapGrid.Manager.PublicSurface.Tests;

public sealed class ManagerPublicSurfaceTests {
    [Fact]
    public async Task ExternalCompositionCanOpenBuildDisposeWithoutBackendAccess() {
        string path = Path.Combine(
            Directory.Exists("/dev/shm") ? "/dev/shm" : Path.GetTempPath(),
            "atelia-manager-public-tests",
            Guid.NewGuid().ToString("N")
        );
        var estimator = new O200kBaseHistoryUnitLoadEstimator();
        try {
            using (SessionJournalLegacyImportWriter import =
                   SessionJournalLegacyImportWriter.Create(
                       path,
                       new SessionCreateOptions("model", "system", "surface")
                   )) {
                _ = import.AppendObservation("observation");
                _ = import.AppendImportedAgentAction(
                    new ActionMessage([new ActionBlock.Text("answer")]),
                    new CompletionDescriptor("import", "v1", "model")
                );
                _ = import.AppendObservation("recent-reserve");
                _ = import.AppendImportedAgentAction(
                    new ActionMessage([
                        new ActionBlock.Text("recent-reserve-answer")
                    ]),
                    new CompletionDescriptor("import", "v1", "model")
                );
            }
            using SessionJournalEngine journal = SessionJournalEngine.Open(path);
            Assert.IsType<HistoryTimelineCreateResult.Created>(
                HistoryTimelineFactory.Create(
                    journal.ReadView,
                    new HistoryTimelineInitialPolicySpec(
                        HistoryPartitionAlgorithms
                            .FirstReplaySafeBoundaryAtTargetV1,
                        O200kBaseHistoryUnitLoadEstimator.EstimatorId,
                        new HistoryLoadUnit(1),
                        64,
                        1024 * 1024
                    ),
                    estimator
                )
            );
            Assert.IsType<RecapGridCadenceCreateResult.Created>(
                RecapGridCadenceFactory.Create(
                    journal,
                    new RecapGridCadencePolicySpec(
                        minimumRecentHistoryLoad: 1,
                        HistoryPartitionAlgorithms
                            .FirstReplaySafeBoundaryAtTargetV1,
                        O200kBaseHistoryUnitLoadEstimator.EstimatorId,
                        targetHistoryLoad: 1,
                        maxRawEvents: 64,
                        maxRenderedBytes: 1024 * 1024)));
            (TimelineHeadRef head, HistoryTimelineSelectedRow row) =
                CommitFirstRow(journal, estimator);
            Assert.IsType<RecapGridStoreCreateResult.Created>(
                RecapGridStoreFactory.Create(path)
            );
            var admission = new RecapGridControlAdmission(
                RecapGridControlPermission.All,
                [],
                [],
                [],
                ["case."],
                64,
                64
            );
            ControlHeadRef controlHead = Assert.IsType<
                RecapGridControlCreateResult.Created
            >(RecapGridControlFactory.Create(
                path,
                journal.BranchRefId,
                admission
            )).Head;
            GridBuildRecipe recipe = GridBuildRecipe.CreateFull(
                head.TimelineId,
                row.Descriptor.RowId,
                BuildTarget.Create([])
            );
            using (RecapGridControlHandle control = Assert.IsType<
                       RecapGridControlOpenResult.Opened
                   >(RecapGridControlFactory.Open(
                       path,
                       journal.BranchRefId,
                       admission
                   )).Handle) {
                Assert.IsType<RecapGridControlPutResult.Stored>(
                    control.Coordinator.PutBuildRecipe(
                        controlHead,
                        head,
                        recipe,
                        row.Witness
                    )
                );
            }
            using RecapGridManagerHandle manager = Assert.IsType<
                RecapGridManagerOpenResult.Opened
            >(RecapGridManagerFactory.Open(
                journal.ReadView,
                estimator
            )).Handle;
            var request = new RecapGridBuildRequest(
                new RecapGridBuildSelection.ExplicitCandidate(recipe.Digest),
                null,
                new RecapGridBuildBudget(
                    64,
                    64,
                    TimeSpan.FromMinutes(1)
                )
            );
            RecapGridBuildResult.Fulfilled fulfilled = Assert.IsType<
                RecapGridBuildResult.Fulfilled
            >(
                await manager.Manager.BuildAsync(request, new NoCallExecutor())
            );
            RecapGridBuildProgressResult.Complete progress = Assert.IsType<
                RecapGridBuildProgressResult.Complete
            >(manager.Manager.InspectBuildProgress(request));
            Assert.True(progress.FulfillmentPresent);
            Assert.Equal(fulfilled.Proof.ViewDigest, progress.ThroughViewDigest);
            Assert.IsType<RecapGridPromotableProof>(fulfilled.Proof);
            Assert.Equal(1, fulfilled.Metrics.SelectedRows);
            Assert.Null(typeof(RecapGridFulfillmentReceipt).GetProperty(
                "ControlHead"
            ));
            Assert.NotNull(typeof(RecapGridBuildResult).GetProperty(
                "Metrics"
            ));
            Assert.True(typeof(RecapGridRecipeRowWork).IsPublic);
            Assert.Equal(3, typeof(RecapGridBuildBudget)
                .GetConstructors()
                .Single()
                .GetParameters()
                .Length);
            Assert.Null(typeof(RecapGridBuildBudget).GetProperty(
                "MaximumSelectedRows"
            ));
            Assert.NotNull(typeof(RecapGridBuildProgressResult.Frontier)
                .GetProperty("NextWork"));
            manager.Dispose();
            Assert.IsType<RecapGridBuildResult.Disposed>(
                await manager.Manager.BuildAsync(request, new NoCallExecutor())
            );
            Assert.DoesNotContain(
                typeof(RecapGridManager).Assembly.GetExportedTypes(),
                static type => type.Name.Contains(
                    "Backend",
                    StringComparison.OrdinalIgnoreCase
                ) || type.Name.Contains(
                    "Coordinator",
                    StringComparison.OrdinalIgnoreCase
                )
            );
        }
        finally {
            if (Directory.Exists(path)) {
                Directory.Delete(path, recursive: true);
            }
        }
    }

    private static (TimelineHeadRef, HistoryTimelineSelectedRow)
        CommitFirstRow(
            SessionJournalEngine journal,
            O200kBaseHistoryUnitLoadEstimator estimator
        ) {
        using HistoryTimelineHandle timeline = Assert.IsType<
            HistoryTimelineOpenResult.Opened
        >(HistoryTimelineFactory.Open(
            journal.ReadView,
            estimator
        )).Handle;
        using RecapGridCadenceHandle cadence = Assert.IsType<
            RecapGridCadenceOpenResult.Opened
        >(RecapGridCadenceFactory.OpenMutable(journal)).Handle;
        using RecapGridCadenceTimelineSealOperation seal = Assert.IsType<
            RecapGridCadenceTimelineSealOpenResult.Opened
        >(cadence.BeginTimelineSeal(timeline)).Operation;
        TimelineHeadRef expected = Assert.IsType<
            HistoryTimelineSnapshotResult.Available
        >(timeline.Reader.ReadSnapshot()).Head;
        OnlineSelectedRawCapture capture = Assert.IsType<
            OnlineSelectedRawCaptureResult.Captured
        >(timeline.Coordinator.CaptureOnline(
            expected,
            journal.ReadView
        )).Capture;
        HistoryRowCommitCandidate candidate = Assert.IsType<
            HistoryTimelinePlanResult.Selected
        >(seal.PlanNextRow(expected, capture)).Candidate;
        TimelineHeadRef committed = Assert.IsType<
            HistoryTimelineCommitResult.Committed
        >(seal.CommitRow(candidate)).Head;
        HistoryTimelineSelectedRow row = Assert.IsType<
            HistoryTimelineReaderRowResult.Selected
        >(timeline.Reader.ReadSelectedRow(
            committed,
            committed.HeadRowId!.Value
        )).Row;
        return (committed, row);
    }

    private sealed class NoCallExecutor : IRecapCellBatchExecutor {
        public ValueTask<RecapCellBatchExecutionResult> ExecuteAsync(
            FrozenRowBatch batch,
            CancellationToken cancellationToken
        ) => throw new InvalidOperationException(
            $"Zero-column recipe dispatched {batch.OrderedMissingWork.Count} work items."
        );
    }
}
