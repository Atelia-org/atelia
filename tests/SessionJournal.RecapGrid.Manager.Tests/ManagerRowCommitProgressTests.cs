using Atelia.SessionJournal.RecapGrid.Manager;
using Atelia.SessionJournal.RecapGrid.Store;
using Xunit;

namespace Atelia.SessionJournal.RecapGrid.Manager.Tests;

public sealed partial class ManagerVerticalTests {
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RowCommitProgress_ReportsOnlyTheConfirmedStoredWinner(
        bool alreadyPresent, bool zeroColumns
    ) {
        Fixture fixture = CreateFullFixture(turns: 1, zeroColumns);
        var progress = new List<RecapGridRowCommitProgress>();
        var executor = new RecordingExecutor();
        var hooks = new ManagerTestHooks(PutRowView: (_, _, next) => {
            RecapGridRowViewPutResult.Inserted inserted =
                Assert.IsType<RecapGridRowViewPutResult.Inserted>(next());
            return alreadyPresent
                ? Assert.IsType<RecapGridRowViewPutResult.AlreadyPresent>(next())
                : inserted;
        });
        using (fixture.Journal)
        using (RecapGridManagerHandle manager = OpenManager(fixture, hooks)) {
            RecapGridBuildRequest request = zeroColumns
                ? CandidateRequest(fixture.Recipe.Digest)
                : Request(fixture.Recipe.Target);
            var result = Assert.IsType<RecapGridBuildResult.Fulfilled>(
                await manager.Manager.BuildAsync(request, executor,
                    rowCommitted: value => {
                        using RecapGridStoreReaderHandle reader = OpenStoreReader(fixture);
                        RecapRowView stored = Assert.IsType<RecapGridStoreReadResult<RecapRowView>.Found>(
                            reader.Reader.ReadView(value.RowResultId)).Value;
                        Assert.Equal(stored.HistoryRowId, value.RowId);
                        Assert.Equal(fixture.Recipe.Digest, value.RecipeDigest);
                        progress.Add(value);
                    }));

            Assert.Equal(fixture.Rows.Select(static row => row.Descriptor.RowId),
                progress.Select(static value => value.RowId));
            Assert.All(progress, value => Assert.Equal(alreadyPresent, value.AlreadyPresent));
            Assert.Equal(result.Proof.RowResultId, progress[^1].RowResultId);
            Assert.Equal(alreadyPresent ? 0 : fixture.Rows.Count, result.Metrics.RowViewsCommitted);
            if (zeroColumns) {
                Assert.Empty(executor.Batches);
                Assert.Equal(0, result.Metrics.NewCalls);
            }
        }
    }

    [Fact]
    public async Task RowCommitProgress_NonfatalObserverFailureDoesNotChangeFulfillment() {
        Fixture fixture = CreateFullFixture(turns: 1, zeroColumns: false);
        int observed = 0;
        using (fixture.Journal)
        using (RecapGridManagerHandle manager = OpenManager(fixture)) {
            var result = Assert.IsType<RecapGridBuildResult.Fulfilled>(
                await manager.Manager.BuildAsync(Request(fixture.Recipe.Target), new RecordingExecutor(),
                    rowCommitted: _ => {
                        observed++;
                        throw new IOException("observer failed");
                    }));

            Assert.Equal(fixture.Rows.Count, observed);
            Assert.Equal(fixture.Rows.Count, result.Metrics.RowViewsCommitted);
            using RecapGridStoreReaderHandle reader = OpenStoreReader(fixture);
            Assert.IsType<RecapGridStoreReadResult<RecapRowView>.Found>(
                reader.Reader.ReadView(result.Proof.RowResultId));
        }
    }
}
