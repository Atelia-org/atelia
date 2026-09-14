using Atelia.EventJournal;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid.Store;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Atelia.SessionJournal.RecapGrid.Store.Tests;

public sealed class StoreCellVerticalTests : IDisposable {
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "atelia-recap-grid-store-tests",
        Guid.NewGuid().ToString("N")
    );

    [Fact]
    public void CreateOpenAndNativePragmasAreExact() {
        Directory.CreateDirectory(_root);
        RecapGridStoreCreateResult.Created created = Assert.IsType<
            RecapGridStoreCreateResult.Created
        >(RecapGridStoreFactory.Create(_root));
        Assert.Equal(3, created.Identity.SchemaVersion);
        Assert.IsType<RecapGridStoreCreateResult.AlreadyExists>(
            RecapGridStoreFactory.Create(_root)
        );

        using RecapGridStoreHandle handle = Assert.IsType<
            RecapGridStoreOpenResult.Opened
        >(RecapGridStoreFactory.Open(_root)).Handle;
        Assert.Equal(created.Identity, handle.Identity);

        var store = new SqliteRecapGridStore(
            new StorePaths(_root),
            StoreStorageLimits.Production,
            readOnly: true
        );
        RecapGridStoreInfo info = store.Inspect();
        Assert.Equal(created.Identity, info.Identity);
        Assert.Equal(0, info.CellCount);
        Assert.NotEmpty(info.SqliteVersion);
        Assert.NotEmpty(info.SqliteSourceId);
        Assert.NotEmpty(info.CompileOptions);
        using var connection = new SqliteConnection(
            $"Data Source={new StorePaths(_root).DatabasePath};Mode=ReadOnly;Pooling=False"
        );
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA max_page_count;";
        Assert.Equal(4_294_967_294L, Convert.ToInt64(command.ExecuteScalar()));
    }

    [Fact]
    public void ProductionCountMathCrossesInt32AndRejectsInt64Overflow() {
        Assert.Equal(
            (long)int.MaxValue + 1L,
            StoreCountMath.Increment(int.MaxValue)
        );
        Assert.Equal(
            (long)int.MaxValue + 17L,
            StoreCountMath.Add(int.MaxValue, 17)
        );
        Assert.Throws<OverflowException>(() =>
            StoreCountMath.Increment(long.MaxValue));
        Assert.Throws<OverflowException>(() =>
            StoreCountMath.Add(long.MaxValue, 1));
    }

    [Fact]
    public void CellFirstWinnerIsDurableAndExact() {
        Create();
        RowBuildSpec spec = StoreFixture.Spec();
        RecapCellArtifact first;
        using (RecapGridStoreHandle handle = Open()) {
            first = StoreFixture.Put(handle, spec, "first answer");
            RecapCellArtifact same = Assert.IsType<RecapGridCellPutResult.AlreadyFilled>(
                handle.Writer.PutCell(spec, StoreFixture.Draft(spec, "different answer"))).Winner;
            Assert.Equal(first.Id, same.Id);
            Assert.Equal("first answer", same.Content);
            Assert.Equal(first.Id, Assert.IsType<RecapGridStoreReadResult<RecapCellArtifact>.Found>(
                handle.Reader.TryReadCell(first.Slot)).Value.Id);
        }
        using RecapGridStoreReaderHandle reopened = Assert.IsType<RecapGridStoreReaderOpenResult.Opened>(
            RecapGridStoreFactory.OpenReader(_root)).Handle;
        RecapCellArtifact stored = Assert.IsType<RecapGridStoreReadResult<RecapCellArtifact>.Found>(
            reopened.Reader.ReadCell(first.Id)).Value;
        Assert.Equal(first.Slot, stored.Slot);
        Assert.Equal(first.Content, stored.Content);
    }

    [Fact]
    public void SameContentAndHistoryInDifferentRecipesHaveIndependentSlots() {
        Create();
        using RecapGridStoreHandle handle = Open();
        RowBuildSpec one = StoreFixture.Spec(StoreFixture.Recipe(bootstrap: 'c'));
        RowBuildSpec two = StoreFixture.Spec(StoreFixture.Recipe(bootstrap: 'd'));
        RecapCellArtifact first = StoreFixture.Put(handle, one, "same");
        Assert.IsType<RecapGridMissingResult.Missing>(handle.Reader.FindMissingAssignments(two));
        RecapCellArtifact second = StoreFixture.Put(handle, two, "same");
        Assert.NotEqual(first.Slot, second.Slot);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(first.Content, second.Content);
        Assert.Equal(2, Assert.IsType<RecapGridStoreInspectResult.Available>(
            RecapGridStoreMaintenance.Inspect(_root)).Info.CellCount);
    }

    [Fact]
    public void DisposeRejectsFurtherReadAndWrite() {
        Create();
        RowBuildSpec spec = StoreFixture.Spec();
        RecapGridStoreHandle handle = Open();
        handle.Dispose();
        Assert.IsType<RecapGridCellPutResult.Disposed>(handle.Writer.PutCell(spec, StoreFixture.Draft(spec)));
        Assert.IsType<RecapGridStoreReadResult<RecapCellArtifact>.Disposed>(
            handle.Reader.TryReadCell(((RowBuildAssignment.Evaluate)spec.OrderedAssignments[0]).Slot));
    }

    [Fact]
    public void PartialRowReopenFindsOnlyMissingSlotsAndPublishesActualWinner() {
        Create();
        RowBuildSpec spec = StoreFixture.Spec(StoreFixture.Recipe(columns: 2));
        RecapCellArtifact first;
        using (RecapGridStoreHandle handle = Open()) {
            Assert.Equal(2, Assert.IsType<RecapGridMissingResult.Missing>(
                handle.Reader.FindMissingAssignments(spec)).OrderedSlots.Count);
            first = StoreFixture.Put(handle, spec, "first", column: 0);
            Assert.IsType<RecapGridStoreReadResult<RecapRowView>.Missing>(
                handle.Reader.ReadViewAt(spec.Coordinate.AssignmentKey));
        }
        using RecapGridStoreHandle reopened = Open();
        CellSlot missing = Assert.Single(Assert.IsType<RecapGridMissingResult.Missing>(
            reopened.Reader.FindMissingAssignments(spec)).OrderedSlots);
        Assert.Equal(((RowBuildAssignment.Evaluate)spec.OrderedAssignments[1]).Slot, missing);
        RecapCellArtifact second = StoreFixture.Put(reopened, spec, "second", column: 1);
        Assert.IsType<RecapGridMissingResult.Complete>(reopened.Reader.FindMissingAssignments(spec));
        RecapRowView view = Assert.IsType<RecapGridRowViewPutResult.Inserted>(
            reopened.Writer.PutRowView(spec, [first, second])).Winner;
        RecapRowView replay = Assert.IsType<RecapGridRowViewPutResult.AlreadyPresent>(
            reopened.Writer.PutRowView(spec, [first, second])).Winner;
        Assert.Equal(view.Id, replay.Id);
        Assert.Equal(new[] { first.Id, second.Id }, view.OrderedCells.Select(m => m.CellId));
        Assert.Equal(view.Id, Assert.IsType<RecapGridStoreReadResult<RecapRowView>.Found>(
            reopened.Reader.ReadViewAt(spec.Coordinate.AssignmentKey)).Value.Id);
    }

    [Fact]
    public void RowRequiresStoredWinnerAndPublishedPredecessor() {
        Create();
        RowBuildSpec firstSpec = StoreFixture.Spec();
        using RecapGridStoreHandle handle = Open();
        RecapCellArtifact proposed = StoreFixture.Proposed(firstSpec);
        Assert.IsType<RecapGridRowViewPutResult.PrerequisiteMissing>(
            handle.Writer.PutRowView(firstSpec, [proposed]));
        RecapCellArtifact first = StoreFixture.Put(handle, firstSpec);
        RecapRowView phantom = RecapRowView.Create(new RowResultId(Guid.NewGuid().ToString("N")), firstSpec, [first]);
        RowBuildSpec next = StoreFixture.Spec(row: new HistoryRowId(new string('d', 64)), previous: phantom);
        Assert.IsNotType<RecapGridCellPutResult.Inserted>(handle.Writer.PutCell(next, StoreFixture.Draft(next)));
        Assert.IsType<RecapGridStoreReadResult<RecapCellArtifact>.Missing>(
            handle.Reader.TryReadCell(((RowBuildAssignment.Evaluate)next.OrderedAssignments[0]).Slot));
        RecapRowView committed = Assert.IsType<RecapGridRowViewPutResult.Inserted>(
            handle.Writer.PutRowView(firstSpec, [first])).Winner;
        next = StoreFixture.Spec(row: next.HistoryRowId, previous: committed);
        RecapCellArtifact nextCell = StoreFixture.Put(handle, next);
        RecapRowView nextView = Assert.IsType<RecapGridRowViewPutResult.Inserted>(
            handle.Writer.PutRowView(next, [nextCell])).Winner;
        Assert.Equal(committed.Id, nextView.PreviousRowResultId);
    }

    [Fact]
    public void WrongSlotOrDefinitionCannotFillExpectedWork() {
        Create();
        using RecapGridStoreHandle handle = Open();
        RowBuildSpec spec = StoreFixture.Spec();
        RowBuildSpec wrong = StoreFixture.Spec(StoreFixture.Recipe(bootstrap: 'd'));
        Assert.IsType<RecapGridCellPutResult.Rejected>(handle.Writer.PutCell(spec, StoreFixture.Draft(wrong)));
        var badDefinition = RecapCellDraft.Create(((RowBuildAssignment.Evaluate)spec.OrderedAssignments[0]).Slot,
            new MaintainerDefinitionDigest(new string('b', 64)), RecapCellOutcome.Updated, "wrong", RecapGridLimits.MaximumContentUtf8Bytes);
        Assert.IsType<RecapGridCellPutResult.Rejected>(handle.Writer.PutCell(spec, badDefinition));
        Assert.IsType<RecapGridMissingResult.Missing>(handle.Reader.FindMissingAssignments(spec));
    }

    [Fact]
    public void FulfilledReferenceIsExactAndIdempotent() {
        Create();
        RowBuildSpec spec = StoreFixture.Spec();
        FulfilledViewKey key = StoreFixture.Fulfilled(spec);
        using RecapGridStoreHandle handle = Open();
        Assert.IsType<RecapGridFulfilledPutResult.PrerequisiteMissing>(handle.Writer.PutFulfilled(
            key, new RowResultId(Guid.NewGuid().ToString("N"))));
        RecapCellArtifact cell = StoreFixture.Put(handle, spec);
        RecapRowView view = Assert.IsType<RecapGridRowViewPutResult.Inserted>(handle.Writer.PutRowView(spec, [cell])).Winner;
        Assert.IsType<RecapGridFulfilledPutResult.Inserted>(handle.Writer.PutFulfilled(key, view.Id));
        Assert.IsType<RecapGridFulfilledPutResult.AlreadyPresent>(handle.Writer.PutFulfilled(key, view.Id));
        Assert.Equal(view.Id, Assert.IsType<RecapGridStoreReadResult<RecapGridFulfilledView>.Found>(
            handle.Reader.ReadFulfilled(StoreFixture.Fulfilled(spec))).Value.RowResultId);
        Assert.IsType<RecapGridStoreReadResult<RecapGridFulfilledView>.Missing>(
            handle.Reader.ReadFulfilled(StoreFixture.Fulfilled(spec, generation: 2)));
    }

    [Fact]
    public void OverlayReusesStoredBaseCellWithoutRewritingItsSlot() {
        Create();
        using RecapGridStoreHandle handle = Open();
        RowBuildSpec full = StoreFixture.Spec(StoreFixture.Recipe(columns: 2));
        RecapCellArtifact baseOne = StoreFixture.Put(handle, full, "base one", 0);
        RecapCellArtifact baseTwo = StoreFixture.Put(handle, full, "base two", 1);
        Assert.IsType<RecapGridRowViewPutResult.Inserted>(handle.Writer.PutRowView(full, [baseOne, baseTwo]));
        GridBuildRecipe overlay = GridBuildRecipe.CreateOverlay(full.Recipe, full.HistoryRowId,
            full.Recipe.Target, [StoreFixture.Column]);
        var coordinate = new RowViewCoordinate(full.RefId, full.TimelineId, full.HistoryRowId,
            full.HistorySegmentDigest, overlay.Digest, overlay.Target.Digest, null, null, bootstrapCompleted: true);
        RowBuildSpec spec = RowBuildSpec.CreateOverlayBootstrap(overlay, coordinate, [
            new RowBuildAssignment.Evaluate(new CellSlot(overlay.Digest, full.HistoryRowId, StoreFixture.Column)),
            new RowBuildAssignment.Reuse(baseTwo.LogicalColumnId, baseTwo)
        ]);
        Assert.Single(Assert.IsType<RecapGridMissingResult.Missing>(handle.Reader.FindMissingAssignments(spec)).OrderedSlots);
        RecapCellArtifact overlayOne = StoreFixture.Put(handle, spec, "overlay one");
        RecapRowView view = Assert.IsType<RecapGridRowViewPutResult.Inserted>(handle.Writer.PutRowView(spec, [overlayOne, baseTwo])).Winner;
        Assert.Equal(baseTwo.Id, view.OrderedCells[1].CellId);
        RecapCellArtifact reused = Assert.IsType<RecapGridStoreReadResult<RecapCellArtifact>.Found>(handle.Reader.ReadCell(baseTwo.Id)).Value;
        Assert.Equal(full.RecipeDigest, reused.Slot.RecipeDigest);
        Assert.Equal(3, Assert.IsType<RecapGridStoreInspectResult.Available>(RecapGridStoreMaintenance.Inspect(_root)).Info.CellCount);
    }

    [Fact]
    public void PartialOverlayFulfillmentRemainsReadableVerifiableAndExportable() {
        Create();
        RowBuildSpec full = StoreFixture.Spec();
        GridBuildRecipe overlay = GridBuildRecipe.CreateOverlay(full.Recipe, new HistoryRowId(new string('d', 64)),
            full.Recipe.Target, [StoreFixture.Column]);
        RowBuildSpec partial = RowBuildSpec.CreateOverlayBootstrap(overlay,
            new RowViewCoordinate(full.RefId, full.TimelineId, full.HistoryRowId, full.HistorySegmentDigest,
                overlay.Digest, overlay.Target.Digest, null, null, bootstrapCompleted: false),
            [new RowBuildAssignment.Evaluate(new CellSlot(overlay.Digest, full.HistoryRowId, StoreFixture.Column))]);
        FulfilledViewKey key = StoreFixture.Fulfilled(partial);
        RowResultId rowId;
        using (RecapGridStoreHandle handle = Open()) {
            RecapCellArtifact cell = StoreFixture.Put(handle, partial, "partial bootstrap");
            RecapRowView row = Assert.IsType<RecapGridRowViewPutResult.Inserted>(handle.Writer.PutRowView(partial, [cell])).Winner;
            Assert.False(row.BootstrapCompleted);
            rowId = row.Id;
            Assert.IsType<RecapGridFulfilledPutResult.Inserted>(handle.Writer.PutFulfilled(key, rowId));
        }
        using RecapGridStoreHandle reopened = Open();
        Assert.Equal(rowId, Assert.IsType<RecapGridStoreReadResult<RecapGridFulfilledView>.Found>(
            reopened.Reader.ReadFulfilled(key)).Value.RowResultId);
        Assert.False(Assert.IsType<RecapGridStoreReadResult<RecapRowView>.Found>(reopened.Reader.ReadView(rowId)).Value.BootstrapCompleted);
        Assert.IsType<RecapGridStoreVerifyResult.Healthy>(RecapGridStoreMaintenance.Verify(_root));
        var export = Assert.IsType<RecapGridStoreExportResult.Page>(RecapGridStoreMaintenance.Export(_root, includeContent: true)).Value;
        Assert.Equal(rowId, Assert.Single(export.Items.Where(item => item.Kind == "fulfilled")).FulfilledRowResultId);
    }

    [Fact]
    public void ZeroColumnPredecessorRetainsItsPublishedIdentity() {
        Create();
        using RecapGridStoreHandle handle = Open();
        RowBuildSpec first = StoreFixture.Spec(StoreFixture.Recipe(columns: 0));
        RecapRowView previous = Assert.IsType<RecapGridRowViewPutResult.Inserted>(handle.Writer.PutRowView(first, [])).Winner;
        RowBuildSpec next = StoreFixture.Spec(first.Recipe, new HistoryRowId(new string('d', 64)), previous);
        RecapRowView current = Assert.IsType<RecapGridRowViewPutResult.Inserted>(handle.Writer.PutRowView(next, [])).Winner;
        Assert.NotEqual(previous.Id, current.Id);
        Assert.Equal(previous.Id, current.PreviousRowResultId);
        Assert.Empty(current.OrderedCells);
    }

    [Fact]
    public void ResetMakesOldIdsMissingAndAllocatesNewResults() {
        Create();
        RowBuildSpec spec = StoreFixture.Spec();
        RecapCellArtifact oldCell;
        RecapRowView oldView;
        RecapGridStoreIdentity oldIdentity;
        using (RecapGridStoreHandle handle = Open()) {
            oldIdentity = handle.Identity;
            oldCell = StoreFixture.Put(handle, spec);
            oldView = Assert.IsType<RecapGridRowViewPutResult.Inserted>(handle.Writer.PutRowView(spec, [oldCell])).Winner;
        }
        var witness = Assert.IsType<RecapGridStorePrepareResetResult.Prepared>(
            RecapGridStoreMaintenance.PrepareReset(_root)).Witness;
        Assert.IsType<RecapGridStoreResetResult.Reset>(RecapGridStoreMaintenance.Reset(_root, witness));
        using RecapGridStoreHandle reset = Open();
        Assert.NotEqual(oldIdentity, reset.Identity);
        Assert.IsType<RecapGridStoreReadResult<RecapCellArtifact>.Missing>(reset.Reader.ReadCell(oldCell.Id));
        Assert.IsType<RecapGridStoreReadResult<RecapRowView>.Missing>(reset.Reader.ReadView(oldView.Id));
        Assert.NotEqual(oldCell.Id, StoreFixture.Put(reset, spec).Id);
    }

    private void Create() {
        Directory.CreateDirectory(_root);
        Assert.IsType<RecapGridStoreCreateResult.Created>(RecapGridStoreFactory.Create(_root));
    }
    private RecapGridStoreHandle Open() => Assert.IsType<RecapGridStoreOpenResult.Opened>(RecapGridStoreFactory.Open(_root)).Handle;
    public void Dispose() {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
