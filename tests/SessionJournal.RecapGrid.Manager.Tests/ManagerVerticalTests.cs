using Atelia.Completion.Abstractions;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid.Control;
using Atelia.SessionJournal.RecapGrid.Manager;
using Atelia.SessionJournal.RecapGrid.Store;
using Xunit;

namespace Atelia.SessionJournal.RecapGrid.Manager.Tests;

public sealed class ManagerVerticalTests : IDisposable {
    private readonly List<string> _paths = [];
    private readonly O200kBaseHistoryUnitLoadEstimator _estimator = new();

    [Fact]
    public void ProgressReportsExactFirstFrontierWithoutRawMaterializationOrWrites() {
        Fixture fixture = CreateFullFixture(turns: 2, zeroColumns: false);
        int openSegmentCalls = 0;
        var hooks = new ManagerTestHooks(OpenSelectedSegment:
            (_, _) => {
                openSegmentCalls++;
                throw new InvalidOperationException(
                    "Progress must not materialize raw segment content."
                );
            });
        RecapGridStoreInfo before = Assert.IsType<
            RecapGridStoreInspectResult.Available
        >(RecapGridStoreMaintenance.Inspect(fixture.Path)).Info;
        using (fixture.Journal)
        using (RecapGridManagerHandle manager = OpenManager(fixture, hooks)) {
            RecapGridBuildProgressResult.Frontier progress = Assert.IsType<
                RecapGridBuildProgressResult.Frontier
            >(manager.Manager.InspectBuildProgress(Request()));

            Assert.Equal(0, openSegmentCalls);
            Assert.Equal(fixture.Rows[0].Descriptor.RowId, progress.RowId);
            Assert.Equal(fixture.Recipe.Digest, progress.RecipeDigest);
            RecapGridMissingAssignmentProgress missing = Assert.Single(
                progress.OrderedMissing
            );
            Assert.Equal(0, missing.Ordinal);
            Assert.Equal(progress.RowId, missing.RowId);
            Assert.Equal(1, progress.Metrics.MissingAssignments);
            Assert.True(progress.Metrics.ExaminedAssignments >= 1);
            Assert.Equal(fixture.TimelineHead, progress.Authority.TimelineHead);
        }
        RecapGridStoreInfo after = Assert.IsType<
            RecapGridStoreInspectResult.Available
        >(RecapGridStoreMaintenance.Inspect(fixture.Path)).Info;
        Assert.Equal(before.CellCount, after.CellCount);
        Assert.Equal(before.RowViewCount, after.RowViewCount);
        Assert.Equal(before.FulfilledViewCount, after.FulfilledViewCount);
    }

    [Fact]
    public void ProgressHonorsNewCallBudgetAtZeroAndExactWithoutDurableWrites() {
        Fixture fixture = CreateFullFixture(turns: 1, zeroColumns: false);
        string database = Path.Combine(
            fixture.Path,
            "derived",
            "recap-grid",
            "v1",
            "grid.sqlite"
        );
        byte[] before = File.ReadAllBytes(database);
        using (fixture.Journal)
        using (RecapGridManagerHandle manager = OpenManager(fixture)) {
            RecapGridBuildProgressResult.BudgetExceeded over = Assert.IsType<
                RecapGridBuildProgressResult.BudgetExceeded
            >(manager.Manager.InspectBuildProgress(Request(maximumNewCalls: 0)));
            Assert.Equal(RecapGridBuildBudgetKind.NewCalls, over.Kind);
            Assert.Equal(1, over.Metrics.MissingAssignments);

            RecapGridBuildProgressResult.Frontier exact = Assert.IsType<
                RecapGridBuildProgressResult.Frontier
            >(manager.Manager.InspectBuildProgress(Request(maximumNewCalls: 1)));
            Assert.Single(exact.OrderedMissing);
            Assert.Equal(1, exact.Metrics.MissingAssignments);
        }
        Assert.Equal(before, File.ReadAllBytes(database));
    }

    [Fact]
    public async Task ProgressReusesExactDerivationAndReportsFulfillmentState() {
        Fixture fixture = CreateFullFixture(turns: 1, zeroColumns: false);
        using (fixture.Journal)
        using (RecapGridManagerHandle manager = OpenManager(fixture)) {
            Assert.IsType<RecapGridBuildProgressResult.Frontier>(
                manager.Manager.InspectBuildProgress(Request())
            );
            RecapGridBuildResult.Fulfilled built = Assert.IsType<
                RecapGridBuildResult.Fulfilled
            >(await manager.Manager.BuildAsync(
                Request(),
                new RecordingExecutor()
            ));

            RecapGridBuildProgressResult.Complete complete = Assert.IsType<
                RecapGridBuildProgressResult.Complete
            >(manager.Manager.InspectBuildProgress(Request()));
            Assert.True(complete.FulfillmentPresent);
            Assert.Equal(built.Proof.ViewDigest, complete.ThroughViewDigest);
            Assert.Equal(0, complete.Metrics.MissingAssignments);
            Assert.Equal(fixture.Rows.Count, complete.Metrics.SelectedRows);
        }
    }

    [Fact]
    public async Task FullMultirowBuildAndRepeatedRequestAreExact() {
        Fixture fixture = CreateFullFixture(turns: 2, zeroColumns: false);
        using (fixture.Journal)
        using (RecapGridManagerHandle manager = OpenManager(fixture)) {
            var executor = new RecordingExecutor();
            RecapGridBuildResult.Fulfilled first = Assert.IsType<
                RecapGridBuildResult.Fulfilled
            >(await manager.Manager.BuildAsync(
                Request(),
                executor
            ));

            Assert.Equal(fixture.Rows.Count, first.Metrics.SelectedRows);
            Assert.Equal(fixture.Rows.Count, executor.Batches.Count);
            Assert.Equal(fixture.Rows.Count, first.Metrics.NewCalls);
            Assert.Equal(fixture.Recipe.Digest,
                first.Proof.RecipeDigest);
            Assert.Equal(fixture.Rows[^1].Descriptor.RowId,
                first.Proof.ThroughRowId);

            int calls = executor.Batches.Count;
            RecapGridBuildResult.Fulfilled second = Assert.IsType<
                RecapGridBuildResult.Fulfilled
            >(await manager.Manager.BuildAsync(
                Request(),
                executor
            ));
            Assert.Equal(calls, executor.Batches.Count);
            Assert.Equal(0, second.Metrics.NewCalls);
            Assert.Equal(first.Proof.ViewDigest, second.Proof.ViewDigest);
            RecapGridBuildResult.Fulfilled third = Assert.IsType<
                RecapGridBuildResult.Fulfilled
            >(await manager.Manager.BuildAsync(
                Request(),
                executor
            ));
            Assert.Equal(calls, executor.Batches.Count);
            Assert.Equal(0, third.Metrics.NewCalls);
            Assert.Equal(first.Proof.ViewDigest, third.Proof.ViewDigest);
        }
    }

    [Fact]
    public async Task PartialBuildDisposeReopenFinishesMissingOnlyWithoutLegacySpool() {
        Fixture fixture = CreateFullFixture(turns: 2, zeroColumns: false);
        using (fixture.Journal) {
            var firstExecutor = new RecordingExecutor();
            using (RecapGridManagerHandle manager = OpenManager(fixture)) {
                RecapGridBuildResult.BudgetExceeded partial = Assert.IsType<
                    RecapGridBuildResult.BudgetExceeded
                >(await manager.Manager.BuildAsync(
                    Request(maximumNewCalls: 1),
                    firstExecutor
                ));
                Assert.Equal(RecapGridBuildBudgetKind.NewCalls, partial.Kind);
                Assert.Equal(1, partial.Metrics.NewCalls);
                Assert.Equal(1, partial.Metrics.RowViewsCommitted);
                FrozenRowBatch firstBatch = Assert.Single(
                    firstExecutor.Batches
                );
                Assert.Equal(
                    fixture.Rows[0].Descriptor.RowId,
                    firstBatch.HistorySegment.Descriptor.RowId
                );
            }

            string legacySpool = Path.Combine(
                fixture.Path,
                "derived",
                "recap",
                "rebuild",
                "v1"
            );
            Assert.False(Directory.Exists(legacySpool));

            var resumedExecutor = new RecordingExecutor();
            using (RecapGridManagerHandle reopened = OpenManager(fixture)) {
                RecapGridBuildResult.Fulfilled resumed = Assert.IsType<
                    RecapGridBuildResult.Fulfilled
                >(await reopened.Manager.BuildAsync(
                    Request(),
                    resumedExecutor
                ));
                int remaining = fixture.Rows.Count - 1;
                Assert.Equal(remaining, resumed.Metrics.NewCalls);
                Assert.Equal(remaining, resumed.Metrics.RowViewsCommitted);
                Assert.Equal(
                    fixture.Rows.Skip(1).Select(row => row.Descriptor.RowId),
                    resumedExecutor.Batches.Select(batch =>
                        batch.HistorySegment.Descriptor.RowId)
                );
            }
            Assert.False(Directory.Exists(legacySpool));

            var thirdExecutor = new RecordingExecutor();
            using RecapGridManagerHandle third = OpenManager(fixture);
            RecapGridBuildResult.Fulfilled cached = Assert.IsType<
                RecapGridBuildResult.Fulfilled
            >(await third.Manager.BuildAsync(Request(), thirdExecutor));
            Assert.Equal(0, cached.Metrics.NewCalls);
            Assert.Empty(thirdExecutor.Batches);
            Assert.False(Directory.Exists(legacySpool));
        }
    }

    [Fact]
    public async Task ZeroColumnFullBuildCreatesViewsWithoutExecutorCalls() {
        Fixture fixture = CreateFullFixture(turns: 1, zeroColumns: true);
        using (fixture.Journal)
        using (RecapGridManagerHandle manager = OpenManager(fixture)) {
            var executor = new RecordingExecutor();
            RecapGridBuildResult.Fulfilled result = Assert.IsType<
                RecapGridBuildResult.Fulfilled
            >(await manager.Manager.BuildAsync(
                CandidateRequest(fixture.Recipe.Digest),
                executor
            ));

            Assert.Empty(executor.Batches);
            Assert.Equal(0, result.Metrics.NewCalls);
            Assert.Equal(fixture.Rows.Count,
                result.Metrics.RowViewsCommitted);
        }
    }

    [Fact]
    public async Task WholeBatchBudgetFailureStartsNothing() {
        Fixture fixture = CreateFullFixture(turns: 1, zeroColumns: false);
        using (fixture.Journal)
        using (RecapGridManagerHandle manager = OpenManager(fixture)) {
            var executor = new RecordingExecutor();
            var budget = new RecapGridBuildBudget(
                64,
                256,
                maximumNewCalls: 0,
                TimeSpan.FromMinutes(1)
            );
            var request = new RecapGridBuildRequest(
                new RecapGridBuildSelection.LiveActive(),
                null,
                budget
            );

            RecapGridBuildResult.BudgetExceeded result = Assert.IsType<
                RecapGridBuildResult.BudgetExceeded
            >(await manager.Manager.BuildAsync(request, executor));
            Assert.Equal(RecapGridBuildBudgetKind.NewCalls, result.Kind);
            Assert.Empty(executor.Batches);
        }
    }

    [Fact]
    public async Task DisposedManagerRejectsBuild() {
        Fixture fixture = CreateFullFixture(turns: 1, zeroColumns: true);
        using (fixture.Journal) {
            RecapGridManagerHandle manager = OpenManager(fixture);
            manager.Dispose();
            Assert.IsType<RecapGridBuildResult.Disposed>(
                await manager.Manager.BuildAsync(
                    CandidateRequest(fixture.Recipe.Digest),
                    new RecordingExecutor()
                )
            );
        }
    }

    [Fact]
    public async Task IndeterminateWritesSettleFromSameHandleExactReads() {
        Fixture fixture = CreateFullFixture(turns: 1, zeroColumns: false);
        var hooks = new ManagerTestHooks(
            PutCell: (cell, next) => {
                _ = Assert.IsType<RecapGridCellPutResult.Inserted>(next());
                return new RecapGridCellPutResult.CommitIndeterminate(
                    cell.EvaluationKey.Digest,
                    null
                );
            },
            PutRowView: (spec, view, next) => {
                _ = spec;
                Assert.IsType<RecapGridRowViewPutResult.Inserted>(next());
                return new RecapGridRowViewPutResult.CommitIndeterminate(
                    view.Digest,
                    null
                );
            },
            PutFulfilled: (key, viewDigest, next) => {
                _ = viewDigest;
                Assert.IsType<RecapGridFulfilledPutResult.Inserted>(next());
                return new RecapGridFulfilledPutResult.CommitIndeterminate(
                    key,
                    null
                );
            }
        );
        using (fixture.Journal)
        using (RecapGridManagerHandle manager = OpenManager(fixture, hooks)) {
            RecapGridBuildResult.Fulfilled result = Assert.IsType<
                RecapGridBuildResult.Fulfilled
            >(await manager.Manager.BuildAsync(
                Request(),
                new RecordingExecutor()
            ));
            Assert.Equal(fixture.Rows.Count,
                result.Metrics.CellsCommitted);
            Assert.Equal(fixture.Rows.Count,
                result.Metrics.RowViewsCommitted);
        }
    }

    [Theory]
    [InlineData(RecapGridBuildCommitKind.Cell)]
    [InlineData(RecapGridBuildCommitKind.RowView)]
    [InlineData(RecapGridBuildCommitKind.Fulfilled)]
    public async Task IndeterminateMissingRequiresSettlement(
        RecapGridBuildCommitKind kind
    ) {
        Fixture fixture = CreateFullFixture(turns: 1, zeroColumns: false);
        var hooks = new ManagerTestHooks(
            PutCell: kind == RecapGridBuildCommitKind.Cell
                ? (cell, _) => new RecapGridCellPutResult
                    .CommitIndeterminate(cell.EvaluationKey.Digest, null)
                : null,
            PutRowView: kind == RecapGridBuildCommitKind.RowView
                ? (_, view, _) => new RecapGridRowViewPutResult
                    .CommitIndeterminate(view.Digest, null)
                : null,
            PutFulfilled: kind == RecapGridBuildCommitKind.Fulfilled
                ? (key, _, _) => new RecapGridFulfilledPutResult
                    .CommitIndeterminate(key, null)
                : null
        );
        using (fixture.Journal)
        using (RecapGridManagerHandle manager = OpenManager(fixture, hooks)) {
            RecapGridBuildResult.SettlementRequired result = Assert.IsType<
                RecapGridBuildResult.SettlementRequired
            >(await manager.Manager.BuildAsync(
                Request(),
                new RecordingExecutor()
            ));
            Assert.Equal(kind, result.Kind);
            Assert.Null(result.ObservedIdentity);
        }
    }

    [Theory]
    [InlineData(RecapGridBuildCommitKind.Cell,
        IndeterminateObservedKind.Same)]
    [InlineData(RecapGridBuildCommitKind.Cell,
        IndeterminateObservedKind.Different)]
    [InlineData(RecapGridBuildCommitKind.Cell,
        IndeterminateObservedKind.Null)]
    [InlineData(RecapGridBuildCommitKind.RowView,
        IndeterminateObservedKind.Same)]
    [InlineData(RecapGridBuildCommitKind.RowView,
        IndeterminateObservedKind.Different)]
    [InlineData(RecapGridBuildCommitKind.RowView,
        IndeterminateObservedKind.Null)]
    [InlineData(RecapGridBuildCommitKind.Fulfilled,
        IndeterminateObservedKind.Same)]
    [InlineData(RecapGridBuildCommitKind.Fulfilled,
        IndeterminateObservedKind.Different)]
    [InlineData(RecapGridBuildCommitKind.Fulfilled,
        IndeterminateObservedKind.Null)]
    public async Task IndeterminateWrongIntendedIsInvalidEvenWhenExpectedExists(
        RecapGridBuildCommitKind kind,
        IndeterminateObservedKind observedKind
    ) {
        Fixture fixture = CreateFullFixture(1, zeroColumns: false);
        RecapCellArtifact? cell = null;
        RecapRowView? view = null;
        FulfilledViewKey? fulfilledKey = null;
        var hooks = new ManagerTestHooks(
            PutCell: kind == RecapGridBuildCommitKind.Cell
                ? (proposed, next) => {
                    cell = proposed;
                    Assert.IsType<RecapGridCellPutResult.Inserted>(next());
                    RecapCellArtifact? observed = observedKind switch {
                        IndeterminateObservedKind.Same => proposed,
                        IndeterminateObservedKind.Different
                            => RecapCellArtifact.Create(
                                proposed.LogicalColumnId,
                                proposed.DefinitionDigest,
                                proposed.EvaluationKey,
                                RecapCellOutcome.Updated,
                                proposed.Content + "-different",
                                16 * 1024
                            ),
                        _ => null
                    };
                    return new RecapGridCellPutResult.CommitIndeterminate(
                        DifferentEvaluationKeyDigest(
                            proposed.EvaluationKey.Digest
                        ),
                        observed
                    );
                }
                : null,
            PutRowView: kind == RecapGridBuildCommitKind.RowView
                ? (_, proposed, next) => {
                    view = proposed;
                    Assert.IsType<RecapGridRowViewPutResult.Inserted>(next());
                    return new RecapGridRowViewPutResult.CommitIndeterminate(
                        DifferentRowViewDigest(proposed.Digest),
                        observedKind switch {
                            IndeterminateObservedKind.Same
                                => proposed.Digest,
                            IndeterminateObservedKind.Different
                                => DifferentRowViewDigest(proposed.Digest),
                            _ => null
                        }
                    );
                }
                : null,
            PutFulfilled: kind == RecapGridBuildCommitKind.Fulfilled
                ? (key, viewDigest, next) => {
                    fulfilledKey = key;
                    Assert.IsType<RecapGridFulfilledPutResult.Inserted>(
                        next()
                    );
                    return new RecapGridFulfilledPutResult
                        .CommitIndeterminate(
                            DifferentFulfilledKey(fixture, key),
                            observedKind switch {
                                IndeterminateObservedKind.Same
                                    => viewDigest,
                                IndeterminateObservedKind.Different
                                    => DifferentRowViewDigest(viewDigest),
                                _ => null
                            }
                        );
                }
                : null
        );
        using (fixture.Journal)
        using (RecapGridManagerHandle manager = OpenManager(fixture, hooks)) {
            RecapGridBuildResult.Invalid result = Assert.IsType<
                RecapGridBuildResult.Invalid
            >(await manager.Manager.BuildAsync(
                Request(),
                new RecordingExecutor()
            ));
            Assert.Contains("SettlementIntendedMismatch", result.Code,
                StringComparison.Ordinal);
            using RecapGridStoreReaderHandle reader =
                OpenStoreReader(fixture);
            switch (kind) {
                case RecapGridBuildCommitKind.Cell:
                    Assert.IsType<RecapGridStoreReadResult<
                        RecapCellArtifact>.Found>(
                            reader.Reader.TryReadCell(cell!.EvaluationKey)
                        );
                    break;
                case RecapGridBuildCommitKind.RowView:
                    Assert.IsType<RecapGridStoreReadResult<
                        RecapRowView>.Found>(
                            reader.Reader.ReadView(view!.Digest)
                        );
                    break;
                case RecapGridBuildCommitKind.Fulfilled:
                    Assert.IsType<RecapGridStoreReadResult<
                        RecapGridFulfilledView>.Found>(
                            reader.Reader.ReadFulfilled(fulfilledKey!)
                        );
                    break;
            }
        }
    }

    [Theory]
    [InlineData(RecapGridBuildCommitKind.RowView)]
    [InlineData(RecapGridBuildCommitKind.Fulfilled)]
    public async Task IndeterminateCorrectIntendedDifferentObservedIsInvalid(
        RecapGridBuildCommitKind kind
    ) {
        Fixture fixture = CreateFullFixture(1, zeroColumns: false);
        var hooks = new ManagerTestHooks(
            PutRowView: kind == RecapGridBuildCommitKind.RowView
                ? (_, view, next) => {
                    Assert.IsType<RecapGridRowViewPutResult.Inserted>(next());
                    return new RecapGridRowViewPutResult.CommitIndeterminate(
                        view.Digest,
                        DifferentRowViewDigest(view.Digest)
                    );
                }
                : null,
            PutFulfilled: kind == RecapGridBuildCommitKind.Fulfilled
                ? (key, viewDigest, next) => {
                    Assert.IsType<RecapGridFulfilledPutResult.Inserted>(
                        next()
                    );
                    return new RecapGridFulfilledPutResult
                        .CommitIndeterminate(
                            key,
                            DifferentRowViewDigest(viewDigest)
                        );
                }
                : null
        );
        using (fixture.Journal)
        using (RecapGridManagerHandle manager = OpenManager(fixture, hooks)) {
            RecapGridBuildResult.Invalid result = Assert.IsType<
                RecapGridBuildResult.Invalid
            >(await manager.Manager.BuildAsync(
                Request(),
                new RecordingExecutor()
            ));
            Assert.Contains("SettlementObservedMismatch", result.Code,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task OverlayStopsBaseAtBootstrapAndUsesNormalRowsAfter() {
        Fixture fixture = CreateOverlayFixture(
            initialTurns: 1,
            laterTurns: 2
        );
        using (fixture.Journal)
        using (RecapGridManagerHandle manager = OpenManager(fixture)) {
            var executor = new RecordingExecutor();
            RecapGridBuildResult.Fulfilled result = Assert.IsType<
                RecapGridBuildResult.Fulfilled
            >(await manager.Manager.BuildAsync(
                CandidateRequest(fixture.Recipe.Digest),
                executor
            ));
            Assert.Equal(
                fixture.Rows.Count + fixture.BootstrapRowCount,
                result.Metrics.RecipeRowSteps
            );
            FrozenRowBatch bootstrapOverlay = executor.Batches.Single(batch =>
                batch.Recipe.Digest == fixture.Recipe.Digest
                && batch.Spec.HistoryRowId
                    == fixture.Rows[0].Descriptor.RowId
            );
            Assert.Single(bootstrapOverlay.OrderedMissingWork);
            Assert.Equal("case.evidence",
                bootstrapOverlay.OrderedMissingWork[0]
                    .LogicalColumnId.Value);
        }
    }

    [Fact]
    public async Task NullBootstrapOverlayTreatsAllRowsAsNormal() {
        Fixture fixture = CreateOverlayFixture(
            initialTurns: 0,
            laterTurns: 2
        );
        using (fixture.Journal)
        using (RecapGridManagerHandle manager = OpenManager(fixture)) {
            RecapGridBuildResult.Fulfilled result = Assert.IsType<
                RecapGridBuildResult.Fulfilled
            >(await manager.Manager.BuildAsync(
                CandidateRequest(fixture.Recipe.Digest),
                new RecordingExecutor()
            ));
            Assert.Equal(
                fixture.Rows.Count,
                result.Metrics.RecipeRowSteps
            );
        }
    }

    [Fact]
    public async Task InvalidOutcomeOrderWritesNoBatchCells() {
        Fixture fixture = CreateOverlayFixture(
            initialTurns: 0,
            laterTurns: 1
        );
        FrozenRowBatch? captured = null;
        var executor = new DelegateExecutor((batch, _) => {
            captured = batch;
            Assert.Equal(2, batch.OrderedMissingWork.Count);
            return new RecapCellBatchExecutionResult.Completed([
                new RecapCellExecutionOutcome.Updated(
                    batch.OrderedMissingWork[1].EvaluationKey.Digest,
                    "wrong-order"
                ),
                new RecapCellExecutionOutcome.Updated(
                    batch.OrderedMissingWork[0].EvaluationKey.Digest,
                    "wrong-order"
                )
            ]);
        });
        using (fixture.Journal)
        using (RecapGridManagerHandle manager = OpenManager(fixture)) {
            RecapGridBuildResult.ExecutorFailed result = Assert.IsType<
                RecapGridBuildResult.ExecutorFailed
            >(await manager.Manager.BuildAsync(
                CandidateRequest(fixture.Recipe.Digest),
                executor
            ));
            Assert.Equal("ExecutorOutcomeOrderInvalid", result.Code);
            Assert.NotNull(captured);
            AssertCells(fixture, captured.OrderedMissingWork,
                expectedFoundOrdinal: null);
        }
    }

    [Fact]
    public async Task FailureSettlesSuccessfulSiblingButNoRowView() {
        Fixture fixture = CreateOverlayFixture(
            initialTurns: 0,
            laterTurns: 1
        );
        FrozenRowBatch? captured = null;
        var executor = new DelegateExecutor((batch, _) => {
            captured = batch;
            return new RecapCellBatchExecutionResult.Completed([
                new RecapCellExecutionOutcome.Failed(
                    batch.OrderedMissingWork[0].EvaluationKey.Digest,
                    "failed",
                    "primary failure"
                ),
                new RecapCellExecutionOutcome.Updated(
                    batch.OrderedMissingWork[1].EvaluationKey.Digest,
                    "settled sibling"
                )
            ]);
        });
        using (fixture.Journal)
        using (RecapGridManagerHandle manager = OpenManager(fixture)) {
            RecapGridBuildResult.Incomplete result = Assert.IsType<
                RecapGridBuildResult.Incomplete
            >(await manager.Manager.BuildAsync(
                CandidateRequest(fixture.Recipe.Digest),
                executor
            ));
            Assert.Single(result.Failures);
            Assert.Equal(0, result.Failures[0].Ordinal);
            Assert.NotNull(captured);
            AssertCells(fixture, captured.OrderedMissingWork,
                expectedFoundOrdinal: 1);
            using RecapGridStoreReaderHandle reader =
                OpenStoreReader(fixture);
            Assert.IsType<RecapGridMissingResult.Missing>(
                reader.Reader.FindMissingAssignments(captured.Spec)
            );
        }
    }

    [Fact]
    public async Task InvalidFirstContentStillSettlesLaterStartedSuccess() {
        Fixture fixture = CreateOverlayFixture(0, 1);
        FrozenRowBatch? captured = null;
        var executor = new DelegateExecutor((batch, _) => {
            captured = batch;
            return new RecapCellBatchExecutionResult.Completed([
                new RecapCellExecutionOutcome.Updated(
                    batch.OrderedMissingWork[0].EvaluationKey.Digest,
                    new string('x', 20 * 1024)
                ),
                new RecapCellExecutionOutcome.Updated(
                    batch.OrderedMissingWork[1].EvaluationKey.Digest,
                    "settled sibling"
                )
            ]);
        });
        using (fixture.Journal)
        using (RecapGridManagerHandle manager = OpenManager(fixture)) {
            RecapGridBuildResult.ExecutorFailed result = Assert.IsType<
                RecapGridBuildResult.ExecutorFailed
            >(
                await manager.Manager.BuildAsync(
                    CandidateRequest(fixture.Recipe.Digest),
                    executor
                )
            );
            Assert.Equal(2, result.Metrics.NewCalls);
            Assert.Equal(1, result.Metrics.CellsCommitted);
            Assert.NotNull(captured);
            AssertCells(fixture, captured.OrderedMissingWork, 1);
        }
    }

    [Fact]
    public async Task FirstSettlementStillSettlesLaterStartedSuccess() {
        Fixture fixture = CreateOverlayFixture(0, 1);
        var hooks = new ManagerTestHooks(PutCell: (cell, next) =>
            cell.LogicalColumnId.Value == "case.culprit"
                ? new RecapGridCellPutResult.CommitIndeterminate(
                    cell.EvaluationKey.Digest,
                    null
                )
                : next());
        FrozenRowBatch? captured = null;
        var executor = new DelegateExecutor((batch, _) => {
            captured = batch;
            return new RecapCellBatchExecutionResult.Completed([
                .. batch.OrderedMissingWork.Select(work =>
                    new RecapCellExecutionOutcome.Updated(
                        work.EvaluationKey.Digest,
                        $"settled-{work.Ordinal}"
                    ))
            ]);
        });
        using (fixture.Journal)
        using (RecapGridManagerHandle manager = OpenManager(fixture, hooks)) {
            RecapGridBuildResult.SettlementRequired result = Assert.IsType<
                RecapGridBuildResult.SettlementRequired
            >(await manager.Manager.BuildAsync(
                CandidateRequest(fixture.Recipe.Digest),
                executor
            ));
            Assert.Equal(RecapGridBuildCommitKind.Cell, result.Kind);
            Assert.NotNull(captured);
            AssertCells(fixture, captured.OrderedMissingWork, 0);
        }
    }

    [Fact]
    public async Task ElapsedAfterDispatchCompletesRowButWithholdsProof() {
        Fixture fixture = CreateOverlayFixture(0, 1);
        var clock = new ManualTimeProvider();
        var hooks = new ManagerTestHooks(TimeProvider: clock);
        var executor = new DelegateExecutor((batch, _) => {
            clock.Advance(TimeSpan.FromSeconds(2));
            return new RecapCellBatchExecutionResult.Completed([
                .. batch.OrderedMissingWork.Select(work =>
                    new RecapCellExecutionOutcome.Updated(
                        work.EvaluationKey.Digest,
                        $"elapsed-{work.Ordinal}"
                    ))
            ]);
        });
        var request = new RecapGridBuildRequest(
            new RecapGridBuildSelection.ExplicitCandidate(
                fixture.Recipe.Digest
            ),
            fixture.Rows[0].Descriptor.RowId,
            new RecapGridBuildBudget(
                64,
                1024,
                1024,
                TimeSpan.FromSeconds(1)
            )
        );
        using (fixture.Journal)
        using (RecapGridManagerHandle manager = OpenManager(fixture, hooks)) {
            RecapGridBuildResult.BudgetExceeded first = Assert.IsType<
                RecapGridBuildResult.BudgetExceeded
            >(await manager.Manager.BuildAsync(request, executor));
            Assert.Equal(RecapGridBuildBudgetKind.Elapsed, first.Kind);
            RecapGridBuildResult.FulfilledThrough resumed = Assert.IsType<
                RecapGridBuildResult.FulfilledThrough
            >(await manager.Manager.BuildAsync(
                request,
                new RecordingExecutor()
            ));
            Assert.Equal(0, resumed.Metrics.NewCalls);
        }
    }

    [Fact]
    public async Task RawHeadDriftAfterDispatchLeavesCellsButNoRowView() {
        Fixture fixture = CreateOverlayFixture(0, 1);
        FrozenRowBatch? captured = null;
        var executor = new DelegateExecutor((batch, _) => {
            captured = batch;
            AppendTurn(fixture.Journal, "mid-executor-drift");
            return new RecapCellBatchExecutionResult.Completed([
                .. batch.OrderedMissingWork.Select(work =>
                    new RecapCellExecutionOutcome.Updated(
                        work.EvaluationKey.Digest,
                        $"drift-{work.Ordinal}"
                    ))
            ]);
        });
        using (fixture.Journal)
        using (RecapGridManagerHandle manager = OpenManager(fixture)) {
            RecapGridBuildResult.Unavailable result = Assert.IsType<
                RecapGridBuildResult.Unavailable
            >(await manager.Manager.BuildAsync(
                CandidateRequest(fixture.Recipe.Digest),
                executor
            ));
            Assert.Equal(RecapGridBuildDependency.RawHistory,
                result.Dependency);
            Assert.Equal("RawHeadChanged", result.Code);
            Assert.NotNull(captured);
            using RecapGridStoreReaderHandle reader =
                OpenStoreReader(fixture);
            Assert.All(captured.OrderedMissingWork, work =>
                Assert.IsType<RecapGridStoreReadResult<
                    RecapCellArtifact>.Found>(
                        reader.Reader.TryReadCell(work.EvaluationKey)
                    ));
            Assert.IsType<RecapGridMissingResult.Complete>(
                reader.Reader.FindMissingAssignments(captured.Spec)
            );
            RecapCellArtifact[] cells = [..
                captured.OrderedMissingWork.Select(work => Assert.IsType<
                    RecapGridStoreReadResult<RecapCellArtifact>.Found
                >(reader.Reader.TryReadCell(work.EvaluationKey)).Value)
            ];
            RecapRowView unpublished = RecapRowView.Create(
                captured.Spec,
                cells
            );
            Assert.IsType<RecapGridStoreReadResult<RecapRowView>.Missing>(
                reader.Reader.ReadView(unpublished.Digest)
            );
        }
    }

    [Fact]
    public async Task LiveBuildRejectsControlChangeAfterDispatch() {
        Fixture fixture = CreateFullFixture(1, zeroColumns: false);
        FrozenRowBatch? captured = null;
        var executor = new DelegateExecutor((batch, _) => {
            captured = batch;
            Deactivate(fixture);
            return Updated(batch, "live-stale");
        });
        using (fixture.Journal)
        using (RecapGridManagerHandle manager = OpenManager(fixture)) {
            RecapGridBuildResult.StaleControlAuthority result = Assert.IsType<
                RecapGridBuildResult.StaleControlAuthority
            >(await manager.Manager.BuildAsync(Request(), executor));
            Assert.NotEqual(captured!.ControlHead, result.Actual);
            AssertCells(fixture, captured.OrderedMissingWork, 0);
        }
    }

    [Fact]
    public async Task CandidateBuildAllowsActiveChangeInSameControlInstance() {
        Fixture fixture = CreateFullFixture(1, zeroColumns: false);
        bool changed = false;
        var executor = new DelegateExecutor((batch, _) => {
            if (!changed) {
                Deactivate(fixture);
                changed = true;
            }
            return Updated(batch, "candidate-stable");
        });
        using (fixture.Journal)
        using (RecapGridManagerHandle manager = OpenManager(fixture)) {
            RecapGridBuildResult.Fulfilled built = Assert.IsType<
                RecapGridBuildResult.Fulfilled
            >(await manager.Manager.BuildAsync(
                    CandidateRequest(fixture.Recipe.Digest),
                    executor
                ));
            using RecapGridControlHandle control = OpenControl(fixture);
            Assert.IsType<RecapGridControlActivateResult.StaleControlHead>(
                control.Coordinator.CompareExchangeActiveRecipe(
                    built.Proof.ControlHead,
                    built.Proof.TimelineHead,
                    built.Proof.RecipeDigest,
                    RecapGridControlActivationPurpose.Promotion
                )
            );
        }
    }

    [Fact]
    public async Task TimelineAdvanceAfterDispatchLeavesCellsUnpublished() {
        Fixture fixture = CreateOverlayFixture(0, 1);
        FrozenRowBatch? captured = null;
        var executor = new DelegateExecutor((batch, cancellationToken) => {
            _ = cancellationToken;
            captured = batch;
            AppendTurn(fixture.Journal, "timeline-advance");
            CommitAllRows(fixture.Journal);
            return Updated(batch, "timeline-stale");
        });
        using (fixture.Journal)
        using (RecapGridManagerHandle manager = OpenManager(fixture)) {
            Assert.IsType<RecapGridBuildResult.StaleTimelineHead>(
                await manager.Manager.BuildAsync(
                    CandidateRequest(fixture.Recipe.Digest),
                    executor
                )
            );
            Assert.NotNull(captured);
            using RecapGridStoreReaderHandle reader =
                OpenStoreReader(fixture);
            Assert.All(captured.OrderedMissingWork, work =>
                Assert.IsType<RecapGridStoreReadResult<
                    RecapCellArtifact>.Found>(
                        reader.Reader.TryReadCell(work.EvaluationKey)
                    ));
        }
    }

    [Fact]
    public async Task ThroughAncestorBuildsExactPrefixOnly() {
        Fixture fixture = CreateOverlayFixture(0, 3);
        var request = new RecapGridBuildRequest(
            new RecapGridBuildSelection.ExplicitCandidate(
                fixture.Recipe.Digest
            ),
            fixture.Rows[1].Descriptor.RowId,
            Request().Budget
        );
        using (fixture.Journal)
        using (RecapGridManagerHandle manager = OpenManager(fixture)) {
            RecapGridBuildResult.FulfilledThrough result = Assert.IsType<
                RecapGridBuildResult.FulfilledThrough
            >(await manager.Manager.BuildAsync(
                request,
                new RecordingExecutor()
            ));
            Assert.Equal(fixture.Rows[1].Descriptor.RowId,
                result.Receipt.ThroughRowId);
            Assert.Equal(2, result.Metrics.RecipeRowSteps);
            Assert.Equal(fixture.Rows.Count,
                result.Metrics.SelectedRows);

            RecapGridBuildResult.Fulfilled head = Assert.IsType<
                RecapGridBuildResult.Fulfilled
            >(await manager.Manager.BuildAsync(
                CandidateRequest(fixture.Recipe.Digest),
                new RecordingExecutor()
            ));
            using RecapGridControlHandle control = OpenControl(fixture);
            Assert.IsType<RecapGridControlActivateResult.Applied>(
                control.Coordinator.CompareExchangeActiveRecipe(
                    head.Proof.ControlHead,
                    head.Proof.TimelineHead,
                    head.Proof.RecipeDigest,
                    RecapGridControlActivationPurpose.Promotion
                )
            );
        }
    }

    [Fact]
    public async Task SelectedPathBudgetFailsBeforeDispatch() {
        Fixture fixture = CreateFullFixture(2, zeroColumns: false);
        var executor = new RecordingExecutor();
        var request = new RecapGridBuildRequest(
            new RecapGridBuildSelection.LiveActive(),
            null,
            new RecapGridBuildBudget(
                maximumSelectedRows: fixture.Rows.Count - 1,
                maximumRecipeRowSteps: 1024,
                maximumNewCalls: 1024,
                maximumElapsed: TimeSpan.FromMinutes(1)
            )
        );
        using (fixture.Journal)
        using (RecapGridManagerHandle manager = OpenManager(fixture)) {
            RecapGridBuildResult.BudgetExceeded result = Assert.IsType<
                RecapGridBuildResult.BudgetExceeded
            >(await manager.Manager.BuildAsync(request, executor));
            Assert.Equal(RecapGridBuildBudgetKind.SelectedRows, result.Kind);
            Assert.Empty(executor.Batches);
        }
    }

    [Fact]
    public async Task EmptyTimelineReturnsNoRowsAtGenerationZeroAndLater() {
        Fixture fixture = CreateOverlayFixture(0, 0);
        using (fixture.Journal) {
            using (RecapGridManagerHandle manager = OpenManager(fixture)) {
                RecapGridBuildResult.NoRows first = Assert.IsType<
                    RecapGridBuildResult.NoRows
                >(await manager.Manager.BuildAsync(
                    CandidateRequest(fixture.Recipe.Digest),
                    new RecordingExecutor()
                ));
                Assert.Equal(0, first.TimelineHead.Generation);
            }
            using (HistoryTimelineHandle timeline = Assert.IsType<
                       HistoryTimelineOpenResult.Opened
                   >(HistoryTimelineFactory.Open(
                       fixture.Journal.ReadView,
                       _estimator
                   )).Handle) {
                TimelineHeadRef expected = Assert.IsType<
                    HistoryTimelineSnapshotResult.Available
                >(timeline.Reader.ReadSnapshot()).Head;
                HistoryTimelinePolicyCasResult.Applied applied = Assert.IsType<
                    HistoryTimelinePolicyCasResult.Applied
                >(timeline.Coordinator.CompareExchangePolicy(
                    expected,
                    expected.ActivePartitionPolicyDigest
                ));
                Assert.Equal(1, applied.Head.Generation);
                Assert.Null(applied.Head.HeadRowId);
            }
            using RecapGridManagerHandle reopened = OpenManager(fixture);
            RecapGridBuildResult.NoRows second = Assert.IsType<
                RecapGridBuildResult.NoRows
            >(await reopened.Manager.BuildAsync(
                CandidateRequest(fixture.Recipe.Digest),
                new RecordingExecutor()
            ));
            Assert.Equal(1, second.TimelineHead.Generation);
        }
    }

    [Fact]
    public async Task KeepCopiesExactPriorContentAfterFirstRow() {
        Fixture fixture = CreateFullFixture(2, zeroColumns: false);
        var keys = new List<EvaluationKey>();
        var executor = new DelegateExecutor((batch, _) => {
            FrozenRecapCellWork work = Assert.Single(
                batch.OrderedMissingWork
            );
            keys.Add(work.EvaluationKey);
            RecapCellExecutionOutcome outcome = batch.PreviousCells.Count == 0
                ? new RecapCellExecutionOutcome.Updated(
                    work.EvaluationKey.Digest,
                    "stable hypothesis"
                )
                : new RecapCellExecutionOutcome.KeepUnchanged(
                    work.EvaluationKey.Digest
                );
            return new RecapCellBatchExecutionResult.Completed([outcome]);
        });
        using (fixture.Journal)
        using (RecapGridManagerHandle manager = OpenManager(fixture)) {
            Assert.IsType<RecapGridBuildResult.Fulfilled>(
                await manager.Manager.BuildAsync(Request(), executor)
            );
            Assert.True(keys.Count > 1);
            using RecapGridStoreReaderHandle reader =
                OpenStoreReader(fixture);
            RecapCellArtifact kept = Assert.IsType<
                RecapGridStoreReadResult<RecapCellArtifact>.Found
            >(reader.Reader.TryReadCell(keys[^1])).Value;
            Assert.Equal(RecapCellOutcome.KeepUnchanged, kept.Outcome);
            Assert.Equal("stable hypothesis", kept.Content);
        }
    }

    [Fact]
    public async Task KeepOnFirstRowIsRejectedWithoutCell() {
        Fixture fixture = CreateFullFixture(1, zeroColumns: false);
        FrozenRecapCellWork? work = null;
        var executor = new DelegateExecutor((batch, _) => {
            work = Assert.Single(batch.OrderedMissingWork);
            return new RecapCellBatchExecutionResult.Completed([
                new RecapCellExecutionOutcome.KeepUnchanged(
                    work.EvaluationKey.Digest
                )
            ]);
        });
        using (fixture.Journal)
        using (RecapGridManagerHandle manager = OpenManager(fixture)) {
            RecapGridBuildResult.Invalid result = Assert.IsType<
                RecapGridBuildResult.Invalid
            >(await manager.Manager.BuildAsync(Request(), executor));
            Assert.Equal("KeepUnchangedPriorUnavailable", result.Code);
            Assert.NotNull(work);
            using RecapGridStoreReaderHandle reader =
                OpenStoreReader(fixture);
            Assert.IsType<RecapGridStoreReadResult<
                RecapCellArtifact>.Missing>(
                    reader.Reader.TryReadCell(work.EvaluationKey)
                );
        }
    }

    [Fact]
    public async Task ConcurrentManagersConvergeOnOneEvaluationWinner() {
        Fixture fixture = CreateFullFixture(1, zeroColumns: false);
        using (fixture.Journal)
        using (RecapGridManagerHandle first = OpenManager(fixture))
        using (RecapGridManagerHandle second = OpenManager(fixture)) {
            var executor = new PairBarrierExecutor();
            Task<RecapGridBuildResult> firstBuild = first.Manager
                .BuildAsync(Request(), executor).AsTask();
            Task<RecapGridBuildResult> secondBuild = second.Manager
                .BuildAsync(Request(), executor).AsTask();
            RecapGridBuildResult[] results = await Task.WhenAll(
                firstBuild,
                secondBuild
            );
            RecapGridBuildResult.Fulfilled firstResult = Assert.IsType<
                RecapGridBuildResult.Fulfilled
            >(results[0]);
            RecapGridBuildResult.Fulfilled secondResult = Assert.IsType<
                RecapGridBuildResult.Fulfilled
            >(results[1]);
            Assert.Equal(firstResult.Proof.ViewDigest,
                secondResult.Proof.ViewDigest);
            Assert.True(executor.DispatchCount >= 2);
        }
    }

    [Fact]
    public async Task ExecutorRejectAndThrowAreClosedBeforeArtifacts() {
        Assert.Throws<ArgumentException>(() =>
            new RecapCellBatchExecutionResult.RejectedBeforeDispatch(
                string.Empty,
                "detail"
            ));
        Assert.Throws<ArgumentException>(() =>
            new RecapCellBatchExecutionResult.RejectedBeforeDispatch(
                "code",
                " "
            ));
        Fixture rejectedFixture = CreateFullFixture(1, false);
        using (rejectedFixture.Journal)
        using (RecapGridManagerHandle manager = OpenManager(rejectedFixture)) {
            var rejected = new DelegateExecutor((_, _) =>
                new RecapCellBatchExecutionResult.RejectedBeforeDispatch(
                    "route-unavailable",
                    "No provider route is available."
                ));
            RecapGridBuildResult.ExecutorRejected result = Assert.IsType<
                RecapGridBuildResult.ExecutorRejected
            >(
                await manager.Manager.BuildAsync(Request(), rejected)
            );
            Assert.Equal(0, result.Metrics.NewCalls);
            Assert.Equal(rejectedFixture.Rows.Count,
                result.Metrics.SelectedRows);
        }

        Fixture thrownFixture = CreateFullFixture(1, false);
        using (thrownFixture.Journal)
        using (RecapGridManagerHandle manager = OpenManager(thrownFixture)) {
            var throwing = new DelegateExecutor((_, _) =>
                throw new IOException("executor transport failed"));
            RecapGridBuildResult.ExecutorFailed result = Assert.IsType<
                RecapGridBuildResult.ExecutorFailed
            >(await manager.Manager.BuildAsync(Request(), throwing));
            Assert.Equal(nameof(IOException), result.Code);
            Assert.Equal(0, result.Metrics.NewCalls);
            Assert.Equal(thrownFixture.Rows.Count,
                result.Metrics.SelectedRows);
        }
    }

    [Fact]
    public async Task RecipeRowBudgetFailsBeforeDispatch() {
        Fixture fixture = CreateOverlayFixture(1, 1);
        var executor = new RecordingExecutor();
        var request = new RecapGridBuildRequest(
            new RecapGridBuildSelection.ExplicitCandidate(
                fixture.Recipe.Digest
            ),
            null,
            new RecapGridBuildBudget(
                64,
                maximumRecipeRowSteps: 1,
                1024,
                TimeSpan.FromMinutes(1)
            )
        );
        using (fixture.Journal)
        using (RecapGridManagerHandle manager = OpenManager(fixture)) {
            RecapGridBuildResult.BudgetExceeded result = Assert.IsType<
                RecapGridBuildResult.BudgetExceeded
            >(await manager.Manager.BuildAsync(request, executor));
            Assert.Equal(RecapGridBuildBudgetKind.RecipeRowSteps,
                result.Kind);
            Assert.Empty(executor.Batches);
        }
    }

    [Fact]
    public async Task ManagerLeaseBlocksResetThenNewManagerGetsNewIdentity() {
        Fixture fixture = CreateFullFixture(1, false);
        using (fixture.Journal) {
            RecapGridBuildResult.Fulfilled before;
            using (RecapGridManagerHandle initial = OpenManager(fixture)) {
                before = Assert.IsType<RecapGridBuildResult.Fulfilled>(
                    await initial.Manager.BuildAsync(
                        Request(),
                        new RecordingExecutor()
                    )
                );
            }
            RecapGridStorePhysicalWitness witness = Assert.IsType<
                RecapGridStorePrepareResetResult.Prepared
            >(RecapGridStoreMaintenance.PrepareReset(
                fixture.Path
            )).Witness;
            RecapGridManagerHandle manager = OpenManager(fixture);
            Assert.IsType<RecapGridStoreResetResult.Busy>(
                RecapGridStoreMaintenance.Reset(fixture.Path, witness)
            );
            manager.Dispose();
            RecapGridStoreIdentity resetIdentity = Assert.IsType<
                RecapGridStoreResetResult.Reset
            >(RecapGridStoreMaintenance.Reset(
                fixture.Path,
                witness
            )).Identity;
            Assert.NotEqual(before.Proof.StoreIdentity, resetIdentity);
            using RecapGridManagerHandle reopened = OpenManager(fixture);
            RecapGridBuildResult.Fulfilled after = Assert.IsType<
                RecapGridBuildResult.Fulfilled
            >(await reopened.Manager.BuildAsync(
                Request(),
                new RecordingExecutor()
            ));
            Assert.Equal(resetIdentity, after.Proof.StoreIdentity);
        }
    }

    [Fact]
    public async Task MysteryInquiryCanReachCoherentLaterRecognition() {
        Fixture fixture = CreateOverlayFixture(
            initialTurns: 1,
            laterTurns: 1,
            evidenceLogicalColumnId: "case.x-suspicion"
        );
        bool sawFutureExchange = false;
        var executor = new DelegateExecutor((batch, _) => {
            return new RecapCellBatchExecutionResult.Completed([
                .. batch.OrderedMissingWork.Select(work => {
                    bool exchanged = batch.PreviousCells.Any(cell =>
                        cell.LogicalColumnId.Value == "case.x-suspicion"
                    );
                    if (exchanged && batch.OrderedMissingWork.Count == 2) {
                        sawFutureExchange = true;
                    }
                    string content = work.LogicalColumnId.Value switch {
                        "case.x-suspicion" => "X的行为存在可串联的疑点。",
                        "case.culprit" when exchanged
                            => "原来如此，X的行为疑点与此前线索都对得上了。",
                        _ => "仍然怀疑X，但证据链不完整。"
                    };
                    return new RecapCellExecutionOutcome.Updated(
                        work.EvaluationKey.Digest,
                        content
                    );
                })
            ]);
        });
        using (fixture.Journal)
        using (RecapGridManagerHandle manager = OpenManager(fixture)) {
            Assert.NotNull(fixture.BaseRecipe);
            using (RecapGridControlHandle activation = OpenControl(fixture)) {
                ControlHeadRef expected = Assert.IsType<
                    RecapGridControlSnapshotResult.Available
                >(activation.Reader.ReadSnapshot()).Snapshot.Head;
                Assert.IsType<RecapGridControlActivateResult.Applied>(
                    activation.Coordinator.CompareExchangeActiveRecipe(
                        expected,
                        fixture.TimelineHead,
                        fixture.BaseRecipe.Digest,
                        RecapGridControlActivationPurpose.Direct
                    )
                );
            }
            var baseRequest = new RecapGridBuildRequest(
                new RecapGridBuildSelection.ExplicitCandidate(
                    fixture.BaseRecipe.Digest
                ),
                fixture.Rows[fixture.BootstrapRowCount - 1]
                    .Descriptor.RowId,
                Request().Budget
            );
            Assert.IsType<RecapGridBuildResult.FulfilledThrough>(
                await manager.Manager.BuildAsync(
                    baseRequest,
                    new RecordingExecutor()
                )
            );
            RecapGridBuildResult.Fulfilled result = Assert.IsType<
                RecapGridBuildResult.Fulfilled
            >(await manager.Manager.BuildAsync(
                CandidateRequest(fixture.Recipe.Digest),
                executor
            ));
            Assert.True(sawFutureExchange);
            using RecapGridStoreReaderHandle reader =
                OpenStoreReader(fixture);
            RecapRowView view = Assert.IsType<
                RecapGridStoreReadResult<RecapRowView>.Found
            >(reader.Reader.ReadView(result.Proof.ViewDigest)).Value;
            RecapCellArtifact cell = Assert.IsType<
                RecapGridStoreReadResult<RecapCellArtifact>.Found
            >(reader.Reader.ReadCell(
                view.OrderedCells.Single(item =>
                    item.LogicalColumnId.Value == "case.culprit"
                ).CellDigest
            )).Value;
            Assert.Contains("原来如此", cell.Content,
                StringComparison.Ordinal);
            Assert.Contains("疑点", cell.Content,
                StringComparison.Ordinal);
            using RecapGridControlHandle control = OpenControl(fixture);
            RegisteredGridRecipe active = Assert.IsType<
                RecapGridControlSnapshotResult.Available
            >(control.Reader.ReadSnapshot()).Snapshot.ActiveRecipe!;
            Assert.Equal(fixture.BaseRecipe.Digest,
                active.Recipe.Digest);
        }

        Fixture retroactive = CreateOverlayFixture(
            initialTurns: 1,
            laterTurns: 0,
            evidenceLogicalColumnId: "case.x-suspicion"
        );
        var retroExecutor = new RecordingExecutor();
        using (retroactive.Journal)
        using (RecapGridManagerHandle manager = OpenManager(retroactive)) {
            Assert.NotNull(retroactive.RetroRecipe);
            Assert.IsType<RecapGridBuildResult.Fulfilled>(
                await manager.Manager.BuildAsync(
                    CandidateRequest(retroactive.RetroRecipe.Digest),
                    retroExecutor
                )
            );
            Assert.All(retroExecutor.Batches, batch =>
                Assert.Equal(2, batch.OrderedMissingWork.Count));
        }
    }

    [Fact]
    public async Task NestedOverlayReusesAddsReordersRemovesAndChanges() {
        Fixture fixture = CreateOverlayFixture(
            initialTurns: 1,
            laterTurns: 1,
            nested: true
        );
        var executor = new RecordingExecutor();
        using (fixture.Journal)
        using (RecapGridManagerHandle manager = OpenManager(fixture)) {
            RecapGridBuildResult.Fulfilled result = Assert.IsType<
                RecapGridBuildResult.Fulfilled
            >(await manager.Manager.BuildAsync(
                CandidateRequest(fixture.Recipe.Digest),
                executor
            ));
            Assert.Equal(
                fixture.Rows.Count + (2 * fixture.BootstrapRowCount),
                result.Metrics.RecipeRowSteps
            );
            Assert.Contains(executor.Batches, batch =>
                batch.Recipe.Digest == fixture.Recipe.Digest
                && batch.Recipe.Target.OrderedColumns.Count == 1
                && batch.OrderedMissingWork.Count == 1
                && batch.OrderedMissingWork[0].LogicalColumnId.Value
                    == "case.culprit");
            using RecapGridStoreReaderHandle reader =
                OpenStoreReader(fixture);
            RecapRowView view = Assert.IsType<
                RecapGridStoreReadResult<RecapRowView>.Found
            >(reader.Reader.ReadView(result.Proof.ViewDigest)).Value;
            RecapRowViewCell only = Assert.Single(view.OrderedCells);
            Assert.Equal("case.culprit", only.LogicalColumnId.Value);
            Assert.Equal(
                fixture.Recipe.Target.OrderedColumns[0].DefinitionDigest,
                only.DefinitionDigest
            );
        }
    }

    [Fact]
    public async Task SameRowWorkOverlapsAndNextRowWaitsForCommittedView() {
        Fixture fixture = CreateOverlayFixture(0, 1);
        var executor = new OverlapBarrierExecutor(fixture.Path);
        using (fixture.Journal)
        using (RecapGridManagerHandle manager = OpenManager(fixture)) {
            Task<RecapGridBuildResult> build = manager.Manager.BuildAsync(
                CandidateRequest(fixture.Recipe.Digest),
                executor
            ).AsTask();
            await executor.FirstRowAllStarted.Task.WaitAsync(
                TimeSpan.FromSeconds(5)
            );
            Assert.Equal(1, executor.BatchCount);
            Assert.False(executor.SecondRowStarted.Task.IsCompleted);
            executor.ReleaseFirstRow();
            await executor.SecondRowStarted.Task.WaitAsync(
                TimeSpan.FromSeconds(5)
            );
            Assert.IsType<RecapGridBuildResult.Fulfilled>(await build);
            Assert.True(executor.PreviousViewWasCommitted);
        }
    }

    [Fact]
    public async Task ContentEquivalentPriorAcrossDifferentViewsNeedsNoCall() {
        Fixture fixture = CreateOverlayFixture(
            initialTurns: 2,
            laterTurns: 0,
            equivalentTarget: true
        );
        var executor = new RecordingExecutor();
        using (fixture.Journal)
        using (RecapGridManagerHandle manager = OpenManager(fixture)) {
            RecapGridBuildResult.Fulfilled result = Assert.IsType<
                RecapGridBuildResult.Fulfilled
            >(await manager.Manager.BuildAsync(
                CandidateRequest(fixture.Recipe.Digest),
                executor
            ));
            Assert.Equal(fixture.BootstrapRowCount,
                executor.Batches.Count);
            Assert.DoesNotContain(executor.Batches, batch =>
                batch.Recipe.Digest == fixture.Recipe.Digest);
            FrozenRowBatch baseFinal = executor.Batches[^1];
            using RecapGridStoreReaderHandle reader =
                OpenStoreReader(fixture);
            RecapCellArtifact[] baseCells = [..
                baseFinal.OrderedMissingWork.Select(work => Assert.IsType<
                    RecapGridStoreReadResult<RecapCellArtifact>.Found
                >(reader.Reader.TryReadCell(work.EvaluationKey)).Value)
            ];
            RecapRowView baseView = RecapRowView.Create(
                baseFinal.Spec,
                baseCells
            );
            RecapRowView candidateView = Assert.IsType<
                RecapGridStoreReadResult<RecapRowView>.Found
            >(reader.Reader.ReadView(result.Proof.ViewDigest)).Value;
            Assert.NotEqual(baseView.Digest, candidateView.Digest);
            Assert.Equal(
                baseView.OrderedCells.Select(static cell => cell.CellDigest),
                candidateView.OrderedCells.Select(static cell =>
                    cell.CellDigest)
            );
        }
    }

    [Fact]
    public async Task CancellationSettlesStartedSuccessBeforeReturning() {
        Fixture fixture = CreateOverlayFixture(
            initialTurns: 0,
            laterTurns: 1
        );
        using var cancellation = new CancellationTokenSource();
        FrozenRowBatch? captured = null;
        var executor = new DelegateExecutor((batch, _) => {
            captured = batch;
            cancellation.Cancel();
            return new RecapCellBatchExecutionResult.Completed([
                new RecapCellExecutionOutcome.Updated(
                    batch.OrderedMissingWork[0].EvaluationKey.Digest,
                    "settled before cancel"
                ),
                new RecapCellExecutionOutcome
                    .NotStartedDueToCallerCancellation(
                        batch.OrderedMissingWork[1].EvaluationKey.Digest
                    )
            ]);
        });
        using (fixture.Journal)
        using (RecapGridManagerHandle manager = OpenManager(fixture)) {
            Assert.IsType<RecapGridBuildResult.Cancelled>(
                await manager.Manager.BuildAsync(
                    CandidateRequest(fixture.Recipe.Digest),
                    executor,
                    cancellation.Token
                )
            );
            Assert.NotNull(captured);
            AssertCells(fixture, captured.OrderedMissingWork,
                expectedFoundOrdinal: 0);
        }
    }

    [Fact]
    public async Task ReentrantDisposeClosesAfterOwningOperationReturns() {
        Fixture fixture = CreateFullFixture(1, zeroColumns: false);
        RecapGridManagerHandle? manager = null;
        var executor = new DelegateExecutor((batch, _) => {
            manager!.Dispose();
            return Updated(batch, "reentrant-dispose");
        });
        using (fixture.Journal) {
            manager = OpenManager(fixture);
            Assert.IsType<RecapGridBuildResult.Fulfilled>(
                await manager.Manager.BuildAsync(Request(), executor)
            );
            RecapGridBuildResult.Disposed disposed = Assert.IsType<
                RecapGridBuildResult.Disposed
            >(await manager.Manager.BuildAsync(
                Request(),
                new RecordingExecutor()
            ));
            Assert.Equal(RecapGridBuildMetrics.Empty, disposed.Metrics);
            manager.Dispose();
        }
    }

    [Fact]
    public async Task ExternalDisposeDrainsTwoOperationsAndClosesOnce() {
        Fixture fixture = CreateFullFixture(1, zeroColumns: false);
        var executor = new DisposeDrainExecutor(expectedStarts: 2);
        using (fixture.Journal) {
            RecapGridManagerHandle manager = OpenManager(fixture);
            Task<RecapGridBuildResult> first = manager.Manager.BuildAsync(
                Request(),
                executor
            ).AsTask();
            Task<RecapGridBuildResult> second = manager.Manager.BuildAsync(
                Request(),
                executor
            ).AsTask();
            await executor.AllStarted.Task.WaitAsync(
                TimeSpan.FromSeconds(10)
            );
            Task disposing = Task.Run(manager.Dispose);
            await Task.Delay(50);
            Assert.False(disposing.IsCompleted);
            executor.Release();
            RecapGridBuildResult[] results = await Task.WhenAll(
                first,
                second
            );
            Assert.All(results, result =>
                Assert.IsType<RecapGridBuildResult.Fulfilled>(result));
            await disposing.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsType<RecapGridBuildResult.Disposed>(
                await manager.Manager.BuildAsync(
                    Request(),
                    new RecordingExecutor()
                )
            );
            manager.Dispose();
        }
    }

    [Theory]
    [InlineData(RawCancellationBoundary.Capture)]
    [InlineData(RawCancellationBoundary.OpenSegment)]
    public async Task RawBoundaryCancellationIsTypedAndAudited(
        RawCancellationBoundary boundary
    ) {
        Fixture fixture = CreateFullFixture(1, zeroColumns: false);
        using var cancellation = new CancellationTokenSource();
        var executor = new RecordingExecutor();
        var hooks = new ManagerTestHooks(
            BeforeCaptureRaw: boundary == RawCancellationBoundary.Capture
                ? cancellation.Cancel
                : null,
            OpenSelectedSegment:
                boundary == RawCancellationBoundary.OpenSegment
                    ? (_, next) => {
                        cancellation.Cancel();
                        return next();
                    }
                    : null
        );
        using (fixture.Journal)
        using (RecapGridManagerHandle manager = OpenManager(fixture, hooks)) {
            RecapGridBuildResult.Cancelled result = Assert.IsType<
                RecapGridBuildResult.Cancelled
            >(await manager.Manager.BuildAsync(
                Request(),
                executor,
                cancellation.Token
            ));
            Assert.Equal(fixture.Rows.Count,
                result.Metrics.SelectedRows);
            Assert.Equal(0, result.Metrics.NewCalls);
            Assert.Empty(executor.Batches);
        }
    }

    [Fact]
    public async Task NotStartedWithoutCallerCancellationIsExecutorFailure() {
        Fixture fixture = CreateFullFixture(1, zeroColumns: false);
        var executor = new DelegateExecutor((batch, _) =>
            new RecapCellBatchExecutionResult.Completed([
                new RecapCellExecutionOutcome
                    .NotStartedDueToCallerCancellation(
                        batch.OrderedMissingWork[0].EvaluationKey.Digest
                    )
            ]));
        using (fixture.Journal)
        using (RecapGridManagerHandle manager = OpenManager(fixture)) {
            RecapGridBuildResult.ExecutorFailed result = Assert.IsType<
                RecapGridBuildResult.ExecutorFailed
            >(await manager.Manager.BuildAsync(Request(), executor));
            Assert.Equal("ExecutorCancellationContractInvalid", result.Code);
            Assert.Equal(0, result.Metrics.NewCalls);
        }
    }

    [Fact]
    public async Task PreCancelledBuildStartsNoExecutorWork() {
        Fixture fixture = CreateFullFixture(1, zeroColumns: false);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var executor = new RecordingExecutor();
        using (fixture.Journal)
        using (RecapGridManagerHandle manager = OpenManager(fixture)) {
            RecapGridBuildResult.Cancelled result = Assert.IsType<
                RecapGridBuildResult.Cancelled
            >(await manager.Manager.BuildAsync(
                Request(),
                executor,
                cancellation.Token
            ));
            Assert.Equal(RecapGridBuildMetrics.Empty, result.Metrics);
            Assert.Empty(executor.Batches);
        }
    }

    [Fact]
    public async Task FatalExecutorExceptionsPropagate() {
        Fixture fixture = CreateFullFixture(1, zeroColumns: false);
        Func<Exception>[] fatal = [
            static () => new OutOfMemoryException("fatal-oom"),
            static () => new AccessViolationException("fatal-access"),
            static () => new StackOverflowException("fatal-stack")
        ];
        using (fixture.Journal)
        using (RecapGridManagerHandle manager = OpenManager(fixture)) {
            foreach (Func<Exception> create in fatal) {
                Exception expected = create();
                var executor = new DelegateExecutor((_, _) =>
                    throw expected);
                Exception actual = await Assert.ThrowsAnyAsync<Exception>(
                    () => manager.Manager.BuildAsync(
                        Request(),
                        executor
                    ).AsTask()
                );
                Assert.Same(expected, actual);
            }
        }
    }

    [Fact]
    public async Task TargetOrdinalSurvivesCompactedMissingWork() {
        Fixture fixture = CreateOverlayFixture(0, 1);
        var firstHooks = new ManagerTestHooks(PutCell: (cell, next) =>
            cell.LogicalColumnId.Value == "case.culprit"
                ? new RecapGridCellPutResult.CommitIndeterminate(
                    cell.EvaluationKey.Digest,
                    null
                )
                : next());
        using (fixture.Journal) {
            using (RecapGridManagerHandle first = OpenManager(
                       fixture,
                       firstHooks
                   )) {
                Assert.IsType<RecapGridBuildResult.SettlementRequired>(
                    await first.Manager.BuildAsync(
                        CandidateRequest(fixture.Recipe.Digest),
                        new RecordingExecutor()
                    )
                );
            }
            var failing = new DelegateExecutor((batch, _) => {
                FrozenRecapCellWork only = Assert.Single(
                    batch.OrderedMissingWork
                );
                Assert.Equal(1, only.Ordinal);
                return new RecapCellBatchExecutionResult.Completed([
                    new RecapCellExecutionOutcome.Failed(
                        only.EvaluationKey.Digest,
                        "failed",
                        "target ordinal must survive compaction"
                    )
                ]);
            });
            using RecapGridManagerHandle second = OpenManager(fixture);
            RecapGridBuildResult.Incomplete result = Assert.IsType<
                RecapGridBuildResult.Incomplete
            >(await second.Manager.BuildAsync(
                CandidateRequest(fixture.Recipe.Digest),
                failing
            ));
            Assert.Equal(1, Assert.Single(result.Failures).Ordinal);
        }
    }

    [Fact]
    public async Task EarlierExecutorFailureBeatsLaterLocalInvalid() {
        Fixture fixture = CreateOverlayFixture(0, 1);
        var executor = new DelegateExecutor((batch, _) =>
            new RecapCellBatchExecutionResult.Completed([
                new RecapCellExecutionOutcome.Failed(
                    batch.OrderedMissingWork[0].EvaluationKey.Digest,
                    "first-failed",
                    "the lower target ordinal is primary"
                ),
                new RecapCellExecutionOutcome.Updated(
                    batch.OrderedMissingWork[1].EvaluationKey.Digest,
                    new string('x', 20 * 1024)
                )
            ]));
        using (fixture.Journal)
        using (RecapGridManagerHandle manager = OpenManager(fixture)) {
            RecapGridBuildResult.Incomplete result = Assert.IsType<
                RecapGridBuildResult.Incomplete
            >(await manager.Manager.BuildAsync(
                CandidateRequest(fixture.Recipe.Digest),
                executor
            ));
            Assert.Equal(0, Assert.Single(result.Failures).Ordinal);
            Assert.Equal(2, result.Metrics.NewCalls);
        }
    }

    [Theory]
    [InlineData(AuthorityDriftKind.Raw)]
    [InlineData(AuthorityDriftKind.Control)]
    [InlineData(AuthorityDriftKind.Timeline)]
    public async Task PostFulfilledDriftLeavesOldHeadCacheWithoutProof(
        AuthorityDriftKind drift
    ) {
        Fixture fixture = CreateOverlayFixture(0, 1);
        Assert.NotNull(fixture.BaseRecipe);
        using (RecapGridControlHandle activation = OpenControl(fixture)) {
            ControlHeadRef expected = Assert.IsType<
                RecapGridControlSnapshotResult.Available
            >(activation.Reader.ReadSnapshot()).Snapshot.Head;
            Assert.IsType<RecapGridControlActivateResult.Applied>(
                activation.Coordinator.CompareExchangeActiveRecipe(
                    expected,
                    fixture.TimelineHead,
                    fixture.BaseRecipe.Digest,
                    RecapGridControlActivationPurpose.Direct
                )
            );
        }
        FulfilledViewKey? committedKey = null;
        var hooks = new ManagerTestHooks(PutFulfilled:
            (key, viewDigest, next) => {
                _ = viewDigest;
                committedKey = key;
                RecapGridFulfilledPutResult put = next();
                switch (drift) {
                    case AuthorityDriftKind.Raw:
                        AppendTurn(fixture.Journal, "post-put-raw");
                        break;
                    case AuthorityDriftKind.Control:
                        Deactivate(fixture);
                        break;
                    case AuthorityDriftKind.Timeline:
                        AppendTurn(fixture.Journal, "post-put-timeline");
                        _ = CommitAllRows(fixture.Journal);
                        break;
                }
                return put;
            });
        using (fixture.Journal)
        using (RecapGridManagerHandle manager = OpenManager(fixture, hooks)) {
            RecapGridBuildResult result = await manager.Manager.BuildAsync(
                Request(),
                new RecordingExecutor()
            );
            switch (drift) {
                case AuthorityDriftKind.Raw:
                    RecapGridBuildResult.Unavailable raw = Assert.IsType<
                        RecapGridBuildResult.Unavailable
                    >(result);
                    Assert.Equal("RawHeadChanged", raw.Code);
                    break;
                case AuthorityDriftKind.Control:
                    Assert.IsType<
                        RecapGridBuildResult.StaleControlAuthority>(result);
                    break;
                case AuthorityDriftKind.Timeline:
                    Assert.IsType<
                        RecapGridBuildResult.StaleTimelineHead>(result);
                    break;
            }
            Assert.True(result.Metrics.NewCalls > 0);
            Assert.True(result.Metrics.CellsCommitted > 0);
            Assert.True(result.Metrics.RowViewsCommitted > 0);
            using RecapGridStoreReaderHandle reader =
                OpenStoreReader(fixture);
            Assert.IsType<RecapGridStoreReadResult<
                RecapGridFulfilledView>.Found>(
                    reader.Reader.ReadFulfilled(committedKey!)
                );
        }
    }

    [Fact]
    public async Task OpenedSegmentDescriptorMismatchFailsClosed() {
        Fixture fixture = CreateFullFixture(2, zeroColumns: false);
        using HistoryTimelineBuildReadSession other = Assert.IsType<
            HistoryTimelineBuildReadSessionOpenResult.Opened
        >(HistoryTimelineFactory.OpenBuildReadSession(
            fixture.Journal.ReadView,
            _estimator
        )).Session;
        OnlineSelectedRawCapture raw = Assert.IsType<
            OnlineSelectedRawCaptureResult.Captured
        >(other.CaptureRaw(fixture.TimelineHead)).Capture;
        HistorySegmentContent wrong = Assert.IsType<
            HistorySegmentOpenResult.Opened
        >(other.OpenSelectedSegment(
            fixture.TimelineHead,
            raw,
            fixture.Rows[^1]
        )).Content;
        var hooks = new ManagerTestHooks(OpenSelectedSegment:
            (selected, next) => selected.Descriptor.RowId
                    == fixture.Rows[0].Descriptor.RowId
                ? new HistorySegmentOpenResult.Opened(wrong)
                : next());
        using (fixture.Journal)
        using (RecapGridManagerHandle manager = OpenManager(fixture, hooks)) {
            RecapGridBuildResult.Invalid result = Assert.IsType<
                RecapGridBuildResult.Invalid
            >(await manager.Manager.BuildAsync(
                Request(),
                new RecordingExecutor()
            ));
            Assert.Equal("HistorySegmentDescriptorMismatch", result.Code);
            Assert.Equal(fixture.Rows.Count,
                result.Metrics.SelectedRows);
        }
    }

    private Fixture CreateFullFixture(int turns, bool zeroColumns) {
        string path = NewPath();
        using (SessionJournalLegacyImportWriter import =
               SessionJournalLegacyImportWriter.Create(
            path,
            new SessionCreateOptions("model", "system", "manager")
        )) {
            for (int index = 0; index < turns; index++) {
                _ = import.AppendObservation($"observation-{index}");
                _ = import.AppendImportedAgentAction(
                    new ActionMessage([
                        new ActionBlock.Text($"answer-{index}")
                    ]),
                    new CompletionDescriptor("import", "v1", "model")
                );
            }
        }
        SessionJournalEngine journal =
            SessionJournalEngine.OpenReadOnly(path);
        Assert.IsType<HistoryTimelineCreateResult.Created>(
            HistoryTimelineFactory.Create(
                journal.ReadView,
                new HistoryTimelineInitialPolicySpec(
                    HistoryPartitionAlgorithms
                        .FirstReplaySafeBoundaryAtTargetV1,
                    O200kBaseHistoryUnitLoadEstimator.EstimatorId,
                    new HistoryLoadUnit(1),
                    maxRawEvents: 64,
                    maxRenderedBytes: 1024 * 1024
                ),
                _estimator
            )
        );
        (TimelineHeadRef timelineHead,
            IReadOnlyList<HistoryTimelineSelectedRow> rows) =
            CommitAllRows(journal);
        Assert.NotEmpty(rows);
        Assert.IsType<RecapGridStoreCreateResult.Created>(
            RecapGridStoreFactory.Create(path)
        );

        FamilyDefinition? family = null;
        MaintainerDefinitionRevision? definition = null;
        BuildTarget target;
        if (zeroColumns) {
            target = BuildTarget.Create([]);
        }
        else {
            (family, definition) = Values();
            target = BuildTarget.Create([
                new BuildTargetColumn(
                    definition.LogicalColumnId,
                    definition.Digest
                )
            ]);
        }
        GridBuildRecipe recipe = GridBuildRecipe.CreateFull(
            timelineHead.TimelineId,
            rows[^1].Descriptor.RowId,
            target
        );
        var admission = new RecapGridControlAdmission(
            RecapGridControlPermission.All,
            family is null ? [] : [family.Digest],
            definition is null
                ? []
                : [definition.Capability.CapabilityFingerprint],
            definition is null
                ? []
                : [ContextHeaderCarrier.System],
            ["case."],
            maximumBootstrapRows: 64,
            maximumProjectedCalls: 1024
        );
        ControlHeadRef head = Assert.IsType<
            RecapGridControlCreateResult.Created
        >(RecapGridControlFactory.Create(
            path,
            journal.BranchRefId,
            admission
        )).Head;
        using (RecapGridControlHandle control = Assert.IsType<
                   RecapGridControlOpenResult.Opened
               >(RecapGridControlFactory.Open(
                   path,
                   journal.BranchRefId,
                   admission
               )).Handle) {
            if (family is not null && definition is not null) {
                head = Assert.IsType<RecapGridControlPutResult.Stored>(
                    control.Coordinator.PutFamilyDefinition(head, family)
                ).Head;
                head = Assert.IsType<RecapGridControlPutResult.Stored>(
                    control.Coordinator.PutMaintainerDefinition(
                        head,
                        definition
                    )
                ).Head;
            }
            head = Assert.IsType<RecapGridControlPutResult.Stored>(
                control.Coordinator.PutBuildRecipe(
                    head,
                    timelineHead,
                    recipe,
                    rows[^1].Witness
                )
            ).Head;
            if (!zeroColumns) {
                _ = Assert.IsType<RecapGridControlActivateResult.Applied>(
                    control.Coordinator.CompareExchangeActiveRecipe(
                        head,
                        timelineHead,
                        recipe.Digest,
                        RecapGridControlActivationPurpose.Direct
                    )
                );
            }
        }
        return new Fixture(
            path,
            journal,
            timelineHead,
            rows,
            recipe
        );
    }

    private Fixture CreateOverlayFixture(
        int initialTurns,
        int laterTurns,
        bool nested = false,
        bool equivalentTarget = false,
        string evidenceLogicalColumnId = "case.evidence"
    ) {
        string path = NewPath();
        SessionJournalEngine journal = SessionJournalEngine.Create(
                path,
                new SessionCreateOptions("model", "system", "manager")
            );
        journal.UseRuntime(new SessionRuntime(
            new TextCompletionClient(),
            CompletionTarget: new SessionCompletionTargetIdentity(
                "manager-tests",
                "test",
                "manager-tests-v1",
                "manager-tests-adapter-v1"
            ),
            ContextCandidateSource: new EmptyContextSource(),
            ContextLifecycle: new RawHistoryLifecycle()
        ));
        for (int index = 0; index < initialTurns; index++) {
            AppendTurn(journal, $"initial-{index}");
        }
        Assert.IsType<HistoryTimelineCreateResult.Created>(
            HistoryTimelineFactory.Create(
                journal.ReadView,
                new HistoryTimelineInitialPolicySpec(
                    HistoryPartitionAlgorithms
                        .FirstReplaySafeBoundaryAtTargetV1,
                    O200kBaseHistoryUnitLoadEstimator.EstimatorId,
                    new HistoryLoadUnit(1),
                    maxRawEvents: 64,
                    maxRenderedBytes: 1024 * 1024
                ),
                _estimator
            )
        );
        (TimelineHeadRef earlyHead,
            IReadOnlyList<HistoryTimelineSelectedRow> earlyRows) =
            CommitAllRows(journal);
        Assert.Equal(initialTurns == 0, earlyHead.HeadRowId is null);
        Assert.IsType<RecapGridStoreCreateResult.Created>(
            RecapGridStoreFactory.Create(path)
        );
        FamilyDefinition family = Values().Item1;
        MaintainerDefinitionRevision culprit = Definition(
            family,
            "case.culprit",
            "culprit",
            "Who is the culprit?"
        );
        MaintainerDefinitionRevision evidence = Definition(
            family,
            evidenceLogicalColumnId,
            "evidence",
            "Which evidence matters?"
        );
        MaintainerDefinitionRevision culpritV2 = Definition(
            family,
            "case.culprit",
            "culprit",
            "Who is the culprit, after reconciling the evidence?"
        );
        var admission = new RecapGridControlAdmission(
            RecapGridControlPermission.All,
            [family.Digest],
            [culprit.Capability.CapabilityFingerprint],
            [ContextHeaderCarrier.System],
            ["case."],
            maximumBootstrapRows: 64,
            maximumProjectedCalls: 1024
        );
        ControlHeadRef head = Assert.IsType<
            RecapGridControlCreateResult.Created
        >(RecapGridControlFactory.Create(
            path,
            journal.BranchRefId,
            admission
        )).Head;
        GridBuildRecipe overlay;
        GridBuildRecipe baseRecipe;
        GridBuildRecipe retroRecipe;
        using (RecapGridControlHandle control = Assert.IsType<
                   RecapGridControlOpenResult.Opened
               >(RecapGridControlFactory.Open(
                   path,
                   journal.BranchRefId,
                   admission
               )).Handle) {
            head = Assert.IsType<RecapGridControlPutResult.Stored>(
                control.Coordinator.PutFamilyDefinition(head, family)
            ).Head;
            foreach (MaintainerDefinitionRevision definition
                     in new[] { culprit, evidence, culpritV2 }) {
                head = Assert.IsType<RecapGridControlPutResult.Stored>(
                    control.Coordinator.PutMaintainerDefinition(
                        head,
                        definition
                    )
                ).Head;
            }
            HistoryRowId? bootstrap = earlyHead.HeadRowId;
            HistoryTimelineAncestorWitness? witness = earlyRows.Count == 0
                ? null
                : earlyRows[^1].Witness;
            baseRecipe = GridBuildRecipe.CreateFull(
                earlyHead.TimelineId,
                bootstrap,
                BuildTarget.Create([
                    new BuildTargetColumn(
                        culprit.LogicalColumnId,
                        culprit.Digest
                    )
                ])
            );
            head = Assert.IsType<RecapGridControlPutResult.Stored>(
                control.Coordinator.PutBuildRecipe(
                    head,
                    earlyHead,
                    baseRecipe,
                    witness
                )
            ).Head;
            overlay = equivalentTarget
                ? GridBuildRecipe.CreateOverlay(
                    baseRecipe,
                    bootstrap,
                    baseRecipe.Target,
                    [culprit.LogicalColumnId]
                )
                : GridBuildRecipe.CreateOverlay(
                    baseRecipe,
                    bootstrap,
                    BuildTarget.Create([
                        new BuildTargetColumn(
                            evidence.LogicalColumnId,
                            evidence.Digest
                        ),
                        new BuildTargetColumn(
                            culprit.LogicalColumnId,
                            culprit.Digest
                        )
                    ]),
                    [evidence.LogicalColumnId]
                );
            _ = Assert.IsType<RecapGridControlPutResult.Stored>(
                control.Coordinator.PutBuildRecipe(
                    head,
                    earlyHead,
                    overlay,
                    witness
                )
            );
            if (nested) {
                GridBuildRecipe nestedRecipe = GridBuildRecipe.CreateOverlay(
                    overlay,
                    bootstrap,
                    BuildTarget.Create([
                        new BuildTargetColumn(
                            culpritV2.LogicalColumnId,
                            culpritV2.Digest
                        )
                    ]),
                    [culpritV2.LogicalColumnId]
                );
                _ = Assert.IsType<RecapGridControlPutResult.Stored>(
                    control.Coordinator.PutBuildRecipe(
                        Assert.IsType<
                            RecapGridControlSnapshotResult.Available
                        >(control.Reader.ReadSnapshot()).Snapshot.Head,
                        earlyHead,
                        nestedRecipe,
                        witness
                    )
                );
                overlay = nestedRecipe;
            }
            retroRecipe = GridBuildRecipe.CreateFull(
                earlyHead.TimelineId,
                bootstrap,
                BuildTarget.Create([
                    new BuildTargetColumn(
                        culprit.LogicalColumnId,
                        culprit.Digest
                    ),
                    new BuildTargetColumn(
                        evidence.LogicalColumnId,
                        evidence.Digest
                    )
                ])
            );
            _ = Assert.IsType<RecapGridControlPutResult.Stored>(
                control.Coordinator.PutBuildRecipe(
                    Assert.IsType<
                        RecapGridControlSnapshotResult.Available
                    >(control.Reader.ReadSnapshot()).Snapshot.Head,
                    earlyHead,
                    retroRecipe,
                    witness
                )
            );
        }
        for (int index = 0; index < laterTurns; index++) {
            AppendTurn(journal, $"later-{index}");
        }
        (TimelineHeadRef finalHead,
            IReadOnlyList<HistoryTimelineSelectedRow> laterRows) =
            CommitAllRows(journal);
        IReadOnlyList<HistoryTimelineSelectedRow> allRows = [
            .. earlyRows,
            .. laterRows
        ];
        return new Fixture(
            path,
            journal,
            finalHead,
            allRows,
            overlay,
            earlyRows.Count,
            baseRecipe,
            retroRecipe
        );
    }

    private static void AppendTurn(
        SessionJournalEngine journal,
        string suffix
    ) {
        _ = journal.SendAsync(
            journal.ReadCurrentHead()!.Value,
            $"observation-{suffix}"
        ).GetAwaiter().GetResult();
    }

    private (TimelineHeadRef,
        IReadOnlyList<HistoryTimelineSelectedRow>) CommitAllRows(
        SessionJournalEngine journal
    ) {
        using HistoryTimelineHandle timeline = Assert.IsType<
            HistoryTimelineOpenResult.Opened
        >(HistoryTimelineFactory.Open(
            journal.ReadView,
            _estimator
        )).Handle;
        var rows = new List<HistoryTimelineSelectedRow>();
        while (true) {
            TimelineHeadRef before = Assert.IsType<
                HistoryTimelineSnapshotResult.Available
            >(timeline.Reader.ReadSnapshot()).Head;
            OnlineSelectedRawCapture capture = Assert.IsType<
                OnlineSelectedRawCaptureResult.Captured
            >(timeline.Coordinator.CaptureOnline(
                before,
                journal.ReadView
            )).Capture;
            HistoryTimelinePlanResult plan =
                timeline.Coordinator.PlanNextRow(before, capture);
            if (plan is HistoryTimelinePlanResult.NotEnough) {
                return (before, rows.AsReadOnly());
            }
            HistoryRowCommitCandidate candidate = Assert.IsType<
                HistoryTimelinePlanResult.Selected
            >(plan).Candidate;
            TimelineHeadRef committed = Assert.IsType<
                HistoryTimelineCommitResult.Committed
            >(timeline.Coordinator.CommitRow(candidate)).Head;
            rows.Add(Assert.IsType<
                HistoryTimelineReaderRowResult.Selected
            >(timeline.Reader.ReadSelectedRow(
                committed,
                committed.HeadRowId!.Value
            )).Row);
        }
    }

    private RecapGridManagerHandle OpenManager(Fixture fixture)
        => Assert.IsType<RecapGridManagerOpenResult.Opened>(
            RecapGridManagerFactory.Open(
                fixture.Journal.ReadView,
                _estimator
            )
        ).Handle;

    private RecapGridManagerHandle OpenManager(
        Fixture fixture,
        ManagerTestHooks hooks
    ) => Assert.IsType<RecapGridManagerOpenResult.Opened>(
        RecapGridManagerFactory.OpenForTest(
            fixture.Journal.ReadView,
            hooks,
            _estimator
        )
    ).Handle;

    private static RecapGridStoreReaderHandle OpenStoreReader(
        Fixture fixture
    ) => Assert.IsType<RecapGridStoreReaderOpenResult.Opened>(
        RecapGridStoreFactory.OpenReader(fixture.Path)
    ).Handle;

    private static RecapCellBatchExecutionResult Updated(
        FrozenRowBatch batch,
        string prefix
    ) => new RecapCellBatchExecutionResult.Completed([
        .. batch.OrderedMissingWork.Select(work =>
            new RecapCellExecutionOutcome.Updated(
                work.EvaluationKey.Digest,
                $"{prefix}-{work.Ordinal}"
            ))
    ]);

    private static void Deactivate(Fixture fixture) {
        using RecapGridControlHandle control = OpenControl(fixture);
        ControlHeadRef head = Assert.IsType<
            RecapGridControlSnapshotResult.Available
        >(control.Reader.ReadSnapshot()).Snapshot.Head;
        Assert.IsType<RecapGridControlActivateResult.Applied>(
            control.Coordinator.CompareExchangeActiveRecipe(
                head,
                fixture.TimelineHead,
                nextRecipeDigest: null,
                purpose: RecapGridControlActivationPurpose.Direct
            )
        );
    }

    private static RecapGridControlHandle OpenControl(Fixture fixture) {
        (FamilyDefinition family, MaintainerDefinitionRevision definition) =
            Values();
        var admission = new RecapGridControlAdmission(
            RecapGridControlPermission.All,
            [family.Digest],
            [definition.Capability.CapabilityFingerprint],
            [ContextHeaderCarrier.System],
            ["case."],
            64,
            1024
        );
        return Assert.IsType<
            RecapGridControlOpenResult.Opened
        >(RecapGridControlFactory.Open(
            fixture.Path,
            fixture.Journal.BranchRefId,
            admission
        )).Handle;
    }

    private static void AssertCells(
        Fixture fixture,
        IReadOnlyList<FrozenRecapCellWork> work,
        int? expectedFoundOrdinal
    ) {
        using RecapGridStoreReaderHandle reader = OpenStoreReader(fixture);
        for (int index = 0; index < work.Count; index++) {
            RecapGridStoreReadResult<RecapCellArtifact> result =
                reader.Reader.TryReadCell(work[index].EvaluationKey);
            if (index == expectedFoundOrdinal) {
                Assert.IsType<RecapGridStoreReadResult<
                    RecapCellArtifact>.Found>(result);
            }
            else {
                Assert.IsType<RecapGridStoreReadResult<
                    RecapCellArtifact>.Missing>(result);
            }
        }
    }

    private static RecapGridBuildRequest Request(
        int maximumNewCalls = 1024
    ) => new(
        new RecapGridBuildSelection.LiveActive(),
        throughRowId: null,
        new RecapGridBuildBudget(
            64,
            1024,
            maximumNewCalls,
            TimeSpan.FromMinutes(1)
        )
    );

    private static RecapGridBuildRequest CandidateRequest(
        GridBuildRecipeDigest recipeDigest
    ) => new(
        new RecapGridBuildSelection.ExplicitCandidate(recipeDigest),
        throughRowId: null,
        new RecapGridBuildBudget(
            64,
            1024,
            1024,
            TimeSpan.FromMinutes(1)
        )
    );

    private static EvaluationKeyDigest DifferentEvaluationKeyDigest(
        EvaluationKeyDigest current
    ) => new(current.Value == new string('f', 64)
        ? new string('e', 64)
        : new string('f', 64));

    private static RowViewDigest DifferentRowViewDigest(
        RowViewDigest current
    ) => new(current.Value == new string('f', 64)
        ? new string('e', 64)
        : new string('f', 64));

    private static FulfilledViewKey DifferentFulfilledKey(
        Fixture fixture,
        FulfilledViewKey current
    ) {
        TimelineHeadRef head = fixture.TimelineHead;
        var differentHead = new TimelineHeadRef(
            head.TimelineId,
            head.RefId,
            head.HeadRowId,
            head.ActivePartitionPolicyDigest,
            head.SelectedRawHeadAtCommit,
            checked(head.Generation + 1)
        );
        return FulfilledViewKey.Create(
            current.RefId,
            differentHead,
            current.ThroughRowDescriptorDigest,
            fixture.Recipe
        );
    }

    private static (FamilyDefinition, MaintainerDefinitionRevision)
        Values() {
        FamilyDefinition family = FamilyDefinition.Create(
            "Maintain one line of inquiry.",
            [new FamilyToolDefinition(
                "submit",
                "Submit the recap.",
                new FamilyObjectInputSchema([
                    new FamilyToolProperty(
                        "content",
                        new FamilyScalarInputSchema(
                            FamilyScalarType.String
                        ),
                        true
                    )
                ])
            )],
            new FamilyOutputProtocol(
                "output-v1",
                "submit",
                FamilyToolChoice.Required,
                allowParallel: false
            ),
            new FamilyInputRenderingProtocol(
                "input-v1",
                "prior-v1",
                "history-v1"
            )
        );
        return (family, Definition(
            family,
            "case.culprit",
            "culprit",
            "Who is the culprit?"
        ));
    }

    private static MaintainerDefinitionRevision Definition(
        FamilyDefinition family,
        string logicalColumnId,
        string blockKey,
        string topic
    ) {
        var capability = new MaintainerCapabilitySpec(
            "runtime-v1",
            MaintainerReadableScope
                .FullPriorBuildTargetAndCurrentHistorySegmentV1
        );
        return MaintainerDefinitionRevision.Create(
            new LogicalColumnId(logicalColumnId),
            family.Digest,
            new ContextHeaderBlockPath(
                ContextHeaderCarrier.System,
                blockKey
            ),
            capability,
            new MaintainerDeclarativeSpec(
                topic,
                $"Maintain {topic}"
            ),
            maxContentUtf8Bytes: 16 * 1024
        );
    }

    private string NewPath() {
        string path = Path.Combine(
            Directory.Exists("/dev/shm") ? "/dev/shm" : Path.GetTempPath(),
            "atelia-recap-grid-manager-tests",
            Guid.NewGuid().ToString("N")
        );
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

    private sealed record Fixture(
        string Path,
        SessionJournalEngine Journal,
        TimelineHeadRef TimelineHead,
        IReadOnlyList<HistoryTimelineSelectedRow> Rows,
        GridBuildRecipe Recipe,
        int BootstrapRowCount = 0,
        GridBuildRecipe? BaseRecipe = null,
        GridBuildRecipe? RetroRecipe = null
    );

    public enum IndeterminateObservedKind {
        Same,
        Different,
        Null
    }

    public enum RawCancellationBoundary {
        Capture,
        OpenSegment
    }

    public enum AuthorityDriftKind {
        Raw,
        Control,
        Timeline
    }

    private sealed class RecordingExecutor : IRecapCellBatchExecutor {
        internal List<FrozenRowBatch> Batches { get; } = [];

        public ValueTask<RecapCellBatchExecutionResult> ExecuteAsync(
            FrozenRowBatch batch,
            CancellationToken cancellationToken
        ) {
            Batches.Add(batch);
            RecapCellExecutionOutcome[] results = [..
                batch.OrderedMissingWork.Select(work =>
                    (RecapCellExecutionOutcome)new
                        RecapCellExecutionOutcome.Updated(
                            work.EvaluationKey.Digest,
                            $"{work.LogicalColumnId}:{batch.Spec.HistoryRowId}"
                        ))
            ];
            return ValueTask.FromResult<RecapCellBatchExecutionResult>(
                new RecapCellBatchExecutionResult.Completed(results)
            );
        }
    }

    private sealed class DelegateExecutor(
        Func<FrozenRowBatch, CancellationToken,
            RecapCellBatchExecutionResult> execute
    ) : IRecapCellBatchExecutor {
        public ValueTask<RecapCellBatchExecutionResult> ExecuteAsync(
            FrozenRowBatch batch,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult(execute(batch, cancellationToken));
    }

    private sealed class ManualTimeProvider : TimeProvider {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;

        internal void Advance(TimeSpan elapsed) =>
            _timestamp += elapsed.Ticks;
    }

    private sealed class PairBarrierExecutor : IRecapCellBatchExecutor {
        private readonly object _gate = new();
        private TaskCompletionSource? _waiting;
        private int _dispatchCount;

        internal int DispatchCount => Volatile.Read(ref _dispatchCount);

        public async ValueTask<RecapCellBatchExecutionResult> ExecuteAsync(
            FrozenRowBatch batch,
            CancellationToken cancellationToken
        ) {
            int ordinal = Interlocked.Increment(ref _dispatchCount);
            Task? wait = null;
            lock (_gate) {
                if (_waiting is null) {
                    _waiting = new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously
                    );
                    wait = _waiting.Task;
                }
                else {
                    TaskCompletionSource release = _waiting;
                    _waiting = null;
                    release.SetResult();
                }
            }
            if (wait is not null) {
                await wait.WaitAsync(cancellationToken);
            }
            return Updated(batch, $"concurrent-{ordinal}");
        }
    }

    private sealed class DisposeDrainExecutor(int expectedStarts)
        : IRecapCellBatchExecutor {
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private int _started;

        internal TaskCompletionSource AllStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        internal void Release() => _release.SetResult();

        public async ValueTask<RecapCellBatchExecutionResult> ExecuteAsync(
            FrozenRowBatch batch,
            CancellationToken cancellationToken
        ) {
            if (Interlocked.Increment(ref _started) == expectedStarts) {
                AllStarted.SetResult();
            }
            await _release.Task.WaitAsync(cancellationToken);
            return Updated(batch, "dispose-drain");
        }
    }

    private sealed class OverlapBarrierExecutor(string repositoryPath)
        : IRecapCellBatchExecutor {
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private int _batchCount;
        private int _started;

        internal TaskCompletionSource FirstRowAllStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        internal TaskCompletionSource SecondRowStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        internal int BatchCount => Volatile.Read(ref _batchCount);
        internal bool PreviousViewWasCommitted { get; private set; }

        internal void ReleaseFirstRow() => _release.SetResult();

        public async ValueTask<RecapCellBatchExecutionResult> ExecuteAsync(
            FrozenRowBatch batch,
            CancellationToken cancellationToken
        ) {
            int batchOrdinal = Interlocked.Increment(ref _batchCount);
            if (batchOrdinal == 1) {
                Assert.Equal(2, batch.OrderedMissingWork.Count);
                RecapCellExecutionOutcome[] outcomes = await Task.WhenAll(
                    batch.OrderedMissingWork.Select(work =>
                        ExecuteFirstRowWorkAsync(work, cancellationToken)
                    )
                );
                return new RecapCellBatchExecutionResult.Completed(outcomes);
            }
            Assert.NotNull(batch.PreviousView);
            using RecapGridStoreReaderHandle reader = Assert.IsType<
                RecapGridStoreReaderOpenResult.Opened
            >(RecapGridStoreFactory.OpenReader(repositoryPath)).Handle;
            PreviousViewWasCommitted = reader.Reader.ReadView(
                batch.PreviousView.Digest
            ) is RecapGridStoreReadResult<RecapRowView>.Found;
            SecondRowStarted.SetResult();
            return Updated(batch, $"row-{batchOrdinal}");
        }

        private async Task<RecapCellExecutionOutcome>
            ExecuteFirstRowWorkAsync(
                FrozenRecapCellWork work,
                CancellationToken cancellationToken
            ) {
            if (Interlocked.Increment(ref _started) == 2) {
                FirstRowAllStarted.SetResult();
            }
            await _release.Task.WaitAsync(cancellationToken);
            return new RecapCellExecutionOutcome.Updated(
                work.EvaluationKey.Digest,
                $"overlap-{work.Ordinal}"
            );
        }
    }

    private sealed class TextCompletionClient : ICompletionClient {
        private int _count;

        public string Name => "manager-tests";
        public string ApiSpecId => "manager-tests-v1";

        public Task<CompletionResult> StreamCompletionAsync(
            CompletionRequest request,
            CompletionStreamObserver? observer,
            CancellationToken cancellationToken = default
        ) {
            _ = observer;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new CompletionResult(
                new ActionMessage([
                    new ActionBlock.Text($"answer-{++_count}")
                ]),
                new CompletionDescriptor(Name, ApiSpecId, request.ModelId)
            ));
        }
    }

    private sealed class EmptyContextSource
        : ICoherentContextCandidateSource {
        public ValueTask<SessionContextCandidateSelection> SelectAsync(
            SessionContextSelectionRequest request,
            CancellationToken cancellationToken
        ) {
            request.ValidateShape();
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                new SessionContextCandidateSelection(
                    SessionContextCandidateSelectionStatus.EmptyLineage,
                    null
                )
            );
        }

        public ValueTask<SessionContextCandidateMaterializationResult>
            MaterializeAsync(
            SessionContextCandidateDescriptor descriptor,
            CancellationToken cancellationToken
        ) => throw new InvalidOperationException(
            "The empty context source has no materialized candidate."
        );
    }

    private sealed class RawHistoryLifecycle
        : ISessionContextLifecycleCoordinator {
        public ValueTask<SessionContextLifecycleResult> PrepareAsync(
            SessionJournalReadView readView,
            SessionContextLifecycleRequest request,
            CancellationToken cancellationToken
        ) {
            _ = readView;
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                SessionContextLifecycleResult.RawHistoryAuthorized
            );
        }
    }
}
