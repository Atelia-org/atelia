using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid.Manager;
using System.Data.Common;
using Xunit;

namespace Atelia.SessionJournal.RecapGrid.Manager.Tests;

public sealed partial class ManagerVerticalTests {
    [Fact]
    public void SelectedPathPageStartingAtReadsMiddleAnchorAndContinues() {
        Fixture fixture = CreateFullFixture(turns: 24, zeroColumns: true);
        const int startIndex = 40;
        using (fixture.Journal)
        using (HistoryTimelineHandle timeline = Assert.IsType<
                   HistoryTimelineOpenResult.Opened
               >(HistoryTimelineFactory.Open(
                   fixture.Journal.ReadView,
                   _estimator
               )).Handle) {
            HistoryTimelinePathPage first = Assert.IsType<
                HistoryTimelinePathPageResult.Page
            >(timeline.Reader.ReadSelectedPathPageStartingAt(
                fixture.TimelineHead,
                fixture.Rows[startIndex].Descriptor.RowId,
                maximumRows: 2
            )).Value;

            Assert.Equal(
                fixture.Rows.Skip(startIndex - 1).Take(2)
                    .Reverse().Select(row => row.Descriptor.RowId),
                first.Rows.Select(row => row.Descriptor.RowId)
            );
            Assert.NotNull(first.Next);
            HistoryTimelinePathCursor continuationCursor =
                first.Next!.Value;

            HistoryTimelinePathPage continuation = Assert.IsType<
                HistoryTimelinePathPageResult.Page
            >(timeline.Reader.ReadSelectedPathPage(
                fixture.TimelineHead,
                continuationCursor,
                maximumRows: 2
            )).Value;
            Assert.Equal(
                fixture.Rows.Skip(startIndex - 3).Take(2)
                    .Reverse().Select(row => row.Descriptor.RowId),
                continuation.Rows.Select(row => row.Descriptor.RowId)
            );
        }
    }

    [Fact]
    public async Task LongExplicitThroughConsumesOnlyItsExactPagedSuffix() {
        Fixture fixture = CreateFullFixture(turns: 24, zeroColumns: true);
        const int throughIndex = 40;
        var request = new RecapGridBuildRequest(
            new RecapGridBuildSelection.ExplicitCandidate(
                fixture.Recipe.Digest
            ),
            fixture.Rows[throughIndex].Descriptor.RowId,
            new RecapGridBuildBudget(
                maximumRecipeRowSteps: throughIndex + 1,
                maximumNewCalls: 0,
                maximumElapsed: TimeSpan.FromMinutes(1)
            )
        );

        using (fixture.Journal)
        using (RecapGridManagerHandle manager = OpenManager(fixture)) {
            RecapGridBuildResult.FulfilledThrough result = Assert.IsType<
                RecapGridBuildResult.FulfilledThrough
            >(await manager.Manager.BuildAsync(
                request,
                new RecordingExecutor()
            ));

            Assert.Equal(throughIndex + 1, result.Metrics.SelectedRows);
            Assert.Equal(throughIndex + 1, result.Metrics.RecipeRowSteps);
            Assert.Equal(0, result.Metrics.NewCalls);
            Assert.Equal(throughIndex + 1, result.Metrics.RowViewsCommitted);
        }
    }

    [Fact]
    public async Task PagedDiscoveryCountsOnlyRowsConsumedBeforeFirstStoreAnchor() {
        Fixture fixture = CreateFullFixture(turns: 24, zeroColumns: true);
        const int anchorIndex = 31;
        const int throughIndex = 40;
        RecapGridBuildRequest RequestThrough(int index) => new(
            new RecapGridBuildSelection.ExplicitCandidate(
                fixture.Recipe.Digest
            ),
            fixture.Rows[index].Descriptor.RowId,
            new RecapGridBuildBudget(
                maximumRecipeRowSteps: index + 1,
                maximumNewCalls: 0,
                maximumElapsed: TimeSpan.FromMinutes(1)
            )
        );

        using (fixture.Journal) {
            using (RecapGridManagerHandle seed = OpenManager(fixture)) {
                Assert.IsType<RecapGridBuildResult.FulfilledThrough>(
                    await seed.Manager.BuildAsync(
                        RequestThrough(anchorIndex),
                        new RecordingExecutor()
                    )
                );
            }
            using RecapGridManagerHandle manager = OpenManager(fixture);
            RecapGridBuildResult.FulfilledThrough result = Assert.IsType<
                RecapGridBuildResult.FulfilledThrough
            >(await manager.Manager.BuildAsync(
                RequestThrough(throughIndex),
                new RecordingExecutor()
            ));

            // The page prefetches farther than the anchor, but metrics only
            // describe the Through-to-anchor prefix actually consumed.
            Assert.Equal(throughIndex - anchorIndex + 1,
                result.Metrics.SelectedRows);
            Assert.Equal(throughIndex - anchorIndex,
                result.Metrics.RecipeRowSteps);
            Assert.Equal(throughIndex - anchorIndex,
                result.Metrics.RowViewsCommitted);
        }
    }

    [Fact]
    public async Task PagedDiscoveryFailsClosedForPrefetchedRowBeyondStoreAnchor() {
        Fixture fixture = CreateFullFixture(turns: 24, zeroColumns: true);
        const int anchorIndex = 31;
        const int throughIndex = 40;
        RecapGridBuildRequest SeedRequest(int through) => new(
            new RecapGridBuildSelection.ExplicitCandidate(
                fixture.Recipe.Digest
            ),
            fixture.Rows[through].Descriptor.RowId,
            new RecapGridBuildBudget(
                maximumRecipeRowSteps: through + 1,
                maximumNewCalls: 0,
                maximumElapsed: TimeSpan.FromMinutes(1)
            )
        );

        using (fixture.Journal) {
            using (RecapGridManagerHandle seed = OpenManager(fixture)) {
                Assert.IsType<RecapGridBuildResult.FulfilledThrough>(
                    await seed.Manager.BuildAsync(
                        SeedRequest(anchorIndex),
                        new RecordingExecutor()
                    )
                );
            }

            // Open before mutating the Timeline so the reader is already a
            // valid capability. The page starts at row 38 and reaches this
            // row after it has passed the persisted row-31 Store anchor.
            using RecapGridManagerHandle manager = OpenManager(fixture);
            using HistoryTimelineHandle reader = Assert.IsType<
                HistoryTimelineOpenResult.Opened
            >(HistoryTimelineFactory.Open(
                fixture.Journal.ReadView,
                _estimator
            )).Handle;
            DeleteSelectedPathAssignment(
                fixture,
                fixture.Rows[10].Descriptor.RowId
            );
            Assert.IsType<HistoryTimelineReaderRowResult.Selected>(
                reader.Reader.ReadSelectedRow(
                    fixture.TimelineHead,
                    fixture.Rows[throughIndex].Descriptor.RowId
                )
            );
            Assert.IsType<HistoryTimelineReaderRowResult.Selected>(
                reader.Reader.ReadSelectedRow(
                    fixture.TimelineHead,
                    fixture.Rows[throughIndex - 1].Descriptor.RowId
                )
            );
            byte[] storeBefore = ReadStoreDatabase(fixture);
            var executor = new RecordingExecutor();

            RecapGridBuildResult.Unavailable result = Assert.IsType<
                RecapGridBuildResult.Unavailable
            >(await manager.Manager.BuildAsync(
                SeedRequest(throughIndex),
                executor
            ));

            Assert.Equal(RecapGridBuildDependency.Timeline,
                result.Dependency);
            Assert.Equal("TimelineStoreInvalid", result.Code);
            Assert.Equal(2, result.Metrics.SelectedRows);
            Assert.Empty(executor.Batches);
            Assert.Equal(storeBefore, ReadStoreDatabase(fixture));
        }
    }

    private static void DeleteSelectedPathAssignment(
        Fixture fixture,
        HistoryRowId rowId
    ) {
        string database = Path.Combine(
            fixture.Path,
            "derived", "history-timeline", "v2", "refs",
            fixture.Journal.BranchRefId.ToHexString(), "timelines",
            $"{fixture.TimelineHead.TimelineId.Value}.sqlite"
        );
        Type connectionType = Type.GetType(
            "Microsoft.Data.Sqlite.SqliteConnection, Microsoft.Data.Sqlite",
            throwOnError: true
        )!;
        using var connection = (DbConnection)Activator.CreateInstance(
            connectionType,
            $"Data Source={database};Mode=ReadWrite;Pooling=False;Foreign Keys=False"
        )!;
        connection.Open();
        using DbCommand command = connection.CreateCommand();
        command.CommandText =
            "DELETE FROM current_selected_path WHERE row_id = $row;";
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = "$row";
        parameter.Value = rowId.Value;
        command.Parameters.Add(parameter);
        Assert.Equal(1, command.ExecuteNonQuery());

        // The normal mutation guard detects any external write before a page
        // is read. Clear it here only to exercise the per-row proof check for
        // a tampered row that lies beyond the first usable Store anchor.
        command.Parameters.Clear();
        command.CommandText = """
            UPDATE current_selected_path_guard
            SET dirty = 0
            WHERE singleton = 1;
            """;
        Assert.Equal(1, command.ExecuteNonQuery());
    }
}
