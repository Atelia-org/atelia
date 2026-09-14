using Atelia.SessionJournal.RecapGrid.Manager;
using Atelia.SessionJournal.RecapGrid.Store;
using Xunit;

namespace Atelia.SessionJournal.RecapGrid.Manager.Tests;

public sealed partial class ManagerVerticalTests {
    [Fact]
    public async Task ExplicitPrefixBeforeOverlayBootstrapPersistsAndReopensWithoutWork() {
        Fixture fixture = CreateOverlayFixture(initialTurns: 2, laterTurns: 1);
        using (fixture.Journal) {
            Assert.True(fixture.BootstrapRowCount > 1);
            var through = fixture.Rows[0].Descriptor.RowId;
            Assert.NotEqual(fixture.Recipe.BootstrapThroughRowId, through);
            var selection = new RecapGridBuildSelection.ExplicitCandidate(fixture.Recipe.Digest);
            var request = new RecapGridBuildRequest(selection, through, Request().Budget);
            RecapGridBuildResult.FulfilledThrough first;
            using (RecapGridManagerHandle manager = OpenManager(fixture)) {
                var executor = new RecordingExecutor();
                first = Assert.IsType<RecapGridBuildResult.FulfilledThrough>(
                    await manager.Manager.BuildAsync(request, executor));
                Assert.Equal(through, first.Receipt.ThroughRowId);
                Assert.Equal(2, first.Metrics.RecipeRowSteps);
                Assert.All(executor.Batches, batch => Assert.Equal(through, batch.Spec.HistoryRowId));
            }

            // The requested prefix is fulfilled even though the overlay's later
            // bootstrap frontier is still pending. Reopen from SQL, not a batch object.
            using (RecapGridStoreReaderHandle reader = OpenStoreReader(fixture)) {
                RecapGridFulfilledView fulfilled = Assert.IsType<
                    RecapGridStoreReadResult<RecapGridFulfilledView>.Found>(
                        reader.Reader.ReadFulfilled(first.Receipt.FulfilledKey)).Value;
                Assert.Equal(first.Receipt.RowResultId, fulfilled.RowResultId);
                RecapRowView stored = Assert.IsType<RecapGridStoreReadResult<RecapRowView>.Found>(
                    reader.Reader.ReadView(fulfilled.RowResultId)).Value;
                Assert.Equal(through, stored.HistoryRowId);
                Assert.Equal(fixture.Recipe.Digest, stored.RecipeDigest);
                Assert.False(stored.BootstrapCompleted);
            }
            Assert.IsType<RecapGridStoreVerifyResult.Healthy>(
                RecapGridStoreMaintenance.Verify(fixture.Path));

            var zeroWork = new RecapGridBuildRequest(selection, through,
                new RecapGridBuildBudget(0, 0, TimeSpan.FromMinutes(1)));
            using RecapGridManagerHandle reopened = OpenManager(fixture);
            var cachedExecutor = new RecordingExecutor();
            RecapGridBuildResult.FulfilledThrough cached = Assert.IsType<
                RecapGridBuildResult.FulfilledThrough>(
                    await reopened.Manager.BuildAsync(zeroWork, cachedExecutor));
            Assert.Equal(first.Receipt.RowResultId, cached.Receipt.RowResultId);
            Assert.Equal(first.Receipt.FulfilledKey, cached.Receipt.FulfilledKey);
            Assert.Equal(0, cached.Metrics.RecipeRowSteps);
            Assert.Equal(0, cached.Metrics.NewCalls);
            Assert.Empty(cachedExecutor.Batches);
        }
    }
}
