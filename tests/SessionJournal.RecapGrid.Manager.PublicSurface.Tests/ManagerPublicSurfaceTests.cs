using Atelia.Completion.Abstractions;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid;
using Atelia.SessionJournal.RecapGrid.Cadence;
using Atelia.SessionJournal.RecapGrid.Control;
using Atelia.SessionJournal.RecapGrid.Manager;
using Atelia.SessionJournal.RecapGrid.Store;
using Xunit;

namespace Atelia.SessionJournal.RecapGrid.Manager.PublicSurface.Tests;

public sealed class ManagerPublicSurfaceTests {
    [Fact]
    public void ExternalExecutorCanConstructOrdinaryResults() {
        RecapCellExecutionOutcome[] outcomes = [
            new RecapCellExecutionOutcome.Updated(
                new EvaluationKeyDigest(new string('a', 64)),
                "updated"
            ),
            new RecapCellExecutionOutcome.KeepUnchanged(
                new EvaluationKeyDigest(new string('b', 64))
            ),
            new RecapCellExecutionOutcome.Failed(
                new EvaluationKeyDigest(new string('c', 64)),
                "provider-failed",
                "Provider did not return a usable result."
            )
        ];
        var completed = new RecapCellBatchExecutionResult.Completed(outcomes);
        IRecapCellBatchExecutor executor = new ConstructingExecutor(completed);

        Assert.IsType<ConstructingExecutor>(executor);
        Assert.Equal(outcomes, completed.OrderedOutcomes.ToArray());
        Assert.Collection(
            completed.OrderedOutcomes,
            static outcome => Assert.IsType<
                RecapCellExecutionOutcome.Updated
            >(outcome),
            static outcome => Assert.IsType<
                RecapCellExecutionOutcome.KeepUnchanged
            >(outcome),
            static outcome => Assert.IsType<
                RecapCellExecutionOutcome.Failed
            >(outcome)
        );

        var rejected = new RecapCellBatchExecutionResult
            .RejectedBeforeDispatch(
                "not-dispatched",
                "The batch was rejected before any provider call."
            );
        Assert.Equal("not-dispatched", rejected.Code);
        Assert.NotEmpty(rejected.Detail);
    }

    [Fact]
    public void ProgressRecordsArePublicReadOnlyAndNotPubliclyConstructible() {
        AssertProgressRecordShape(
            typeof(RecapGridBuildProgressAuthority),
            [
                ("TimelineHead", typeof(TimelineHeadRef)),
                ("ControlHead", typeof(ControlHeadRef)),
                ("StoreIdentity", typeof(RecapGridStoreIdentity)),
                ("RecipeDigest", typeof(GridBuildRecipeDigest)),
                ("ThroughRowId", typeof(HistoryRowId)),
                ("ThroughDescriptorDigest", typeof(
                    HistorySegmentDescriptorDigest))
            ]
        );
        AssertProgressRecordShape(
            typeof(RecapGridMissingAssignmentProgress),
            [
                ("Ordinal", typeof(int)),
                ("RowId", typeof(HistoryRowId)),
                ("RecipeDigest", typeof(GridBuildRecipeDigest)),
                ("LogicalColumnId", typeof(LogicalColumnId)),
                ("EvaluationKey", typeof(EvaluationKeyDigest))
            ]
        );
        AssertProgressRecordShape(
            typeof(RecapGridRecipeRowWork),
            [
                ("RowId", typeof(HistoryRowId)),
                ("RecipeDigest", typeof(GridBuildRecipeDigest)),
                ("IsOverlayBootstrap", typeof(bool))
            ]
        );

        Assert.True(typeof(RecapGridBuildProgressMetrics).IsValueType);
        System.Reflection.PropertyInfo? metricsProperty =
            typeof(RecapGridBuildProgressResult).GetProperty("Metrics");
        Assert.NotNull(metricsProperty);
        System.Reflection.PropertyInfo metrics = metricsProperty!;
        Assert.True(metrics.GetMethod!.IsPublic);
        Assert.NotNull(metrics.SetMethod);
        Assert.True(metrics.SetMethod!.IsAssembly);
        Assert.Contains(
            typeof(System.Runtime.CompilerServices.IsExternalInit),
            metrics.SetMethod.ReturnParameter.GetRequiredCustomModifiers()
        );
    }

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
                typeof(RecapGridManager).Assembly.GetExportedTypes().Where(
                    static type =>
                        type.Namespace
                            is "Atelia.SessionJournal.RecapGrid.Manager"
                        || type.Namespace?.StartsWith(
                            "Atelia.SessionJournal.RecapGrid.Manager.",
                            StringComparison.Ordinal
                        ) is true
                ),
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

    private static void AssertProgressRecordShape(
        Type type,
        (string Name, Type Type)[] expectedProperties
    ) {
        const System.Reflection.BindingFlags PublicDeclaredInstance =
            System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.DeclaredOnly;
        const System.Reflection.BindingFlags NonPublicInstance =
            System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Instance;

        Assert.True(type.IsPublic);
        Assert.True(type.IsSealed);
        Assert.Empty(type.GetConstructors(PublicDeclaredInstance));
        Assert.DoesNotContain(
            type.GetMethods(PublicDeclaredInstance),
            static method => method.Name == "Deconstruct"
        );

        System.Reflection.PropertyInfo[] properties = type.GetProperties(
            PublicDeclaredInstance
        );
        Assert.Equal(
            expectedProperties.Select(static property => property.Name),
            properties.Select(static property => property.Name)
        );
        Assert.Equal(
            expectedProperties.Select(static property => property.Type),
            properties.Select(static property => property.PropertyType)
        );
        Assert.All(properties, static property => {
            Assert.NotNull(property.GetMethod);
            Assert.True(property.GetMethod!.IsPublic);
            Assert.Null(property.SetMethod);
        });

        Type[] argumentTypes = expectedProperties
            .Select(static property => property.Type)
            .ToArray();
        System.Reflection.ConstructorInfo? argumentConstructor = type
            .GetConstructor(
                NonPublicInstance,
                binder: null,
                argumentTypes,
                modifiers: null
            );
        Assert.NotNull(argumentConstructor);
        Assert.True(argumentConstructor!.IsAssembly);
    }

    private sealed class NoCallExecutor : IRecapCellBatchExecutor {
        public ValueTask<RecapCellBatchExecutionResult> ExecuteAsync(
            FrozenRowBatch batch,
            CancellationToken cancellationToken
        ) => throw new InvalidOperationException(
            $"Zero-column recipe dispatched {batch.OrderedMissingWork.Count} work items."
        );
    }

    private sealed class ConstructingExecutor(
        RecapCellBatchExecutionResult result
    ) : IRecapCellBatchExecutor {
        public ValueTask<RecapCellBatchExecutionResult> ExecuteAsync(
            FrozenRowBatch batch,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult(result);
    }
}
