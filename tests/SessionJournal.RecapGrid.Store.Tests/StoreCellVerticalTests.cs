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
        Assert.Equal(2, created.Identity.SchemaVersion);
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
        Directory.CreateDirectory(_root);
        Assert.IsType<RecapGridStoreCreateResult.Created>(
            RecapGridStoreFactory.Create(_root)
        );
        RecapCellArtifact first = Cell("first answer");
        RecapCellArtifact loser = Cell("different answer");

        using (RecapGridStoreHandle handle = Assert.IsType<
               RecapGridStoreOpenResult.Opened
               >(RecapGridStoreFactory.Open(_root)).Handle) {
            Assert.IsType<RecapGridCellPutResult.Inserted>(
                handle.Writer.PutCell(first)
            );
            RecapGridCellPutResult.AlreadyFilled same = Assert.IsType<
                RecapGridCellPutResult.AlreadyFilled
            >(handle.Writer.PutCell(first));
            Assert.Equal(first.ToCanonicalBytes(), same.Winner.ToCanonicalBytes());
            RecapGridCellPutResult.AlreadyFilled competing = Assert.IsType<
                RecapGridCellPutResult.AlreadyFilled
            >(handle.Writer.PutCell(loser));
            Assert.Equal(first.CellDigest, competing.Winner.CellDigest);
            Assert.Equal(
                first.CellDigest,
                Assert.IsType<RecapGridStoreReadResult<RecapCellArtifact>.Found>(
                    handle.Reader.TryReadCell(first.EvaluationKey)
                ).Value.CellDigest
            );
        }

        using RecapGridStoreReaderHandle reopened = Assert.IsType<
            RecapGridStoreReaderOpenResult.Opened
        >(RecapGridStoreFactory.OpenReader(_root)).Handle;
        Assert.Equal(
            first.ToCanonicalBytes(),
            Assert.IsType<RecapGridStoreReadResult<RecapCellArtifact>.Found>(
                reopened.Reader.ReadCell(first.CellDigest)
            ).Value.ToCanonicalBytes()
        );
    }

    [Fact]
    public void DisposeRejectsFurtherReadAndWrite() {
        Directory.CreateDirectory(_root);
        Assert.IsType<RecapGridStoreCreateResult.Created>(
            RecapGridStoreFactory.Create(_root)
        );
        RecapCellArtifact cell = Cell("answer");
        RecapGridStoreHandle handle = Assert.IsType<
            RecapGridStoreOpenResult.Opened
        >(RecapGridStoreFactory.Open(_root)).Handle;
        handle.Dispose();

        Assert.IsType<RecapGridCellPutResult.Disposed>(
            handle.Writer.PutCell(cell)
        );
        Assert.IsType<RecapGridStoreReadResult<RecapCellArtifact>.Disposed>(
            handle.Reader.TryReadCell(cell.EvaluationKey)
        );
    }

    [Fact]
    public void MissingAndRowViewCommitUseExactStoreWinners() {
        Directory.CreateDirectory(_root);
        Assert.IsType<RecapGridStoreCreateResult.Created>(
            RecapGridStoreFactory.Create(_root)
        );
        (RowBuildSpec spec, RecapCellArtifact cell, RecapRowView view, _) =
            RowValues("row answer");
        using RecapGridStoreHandle handle = Assert.IsType<
            RecapGridStoreOpenResult.Opened
        >(RecapGridStoreFactory.Open(_root)).Handle;

        RecapGridMissingResult.Missing missing = Assert.IsType<
            RecapGridMissingResult.Missing
        >(handle.Reader.FindMissingAssignments(spec));
        Assert.Equal(
            cell.EvaluationKey.Digest,
            Assert.Single(missing.OrderedKeys).Digest
        );
        Assert.IsType<RecapGridRowViewPutResult.PrerequisiteMissing>(
            handle.Writer.PutRowView(spec, view)
        );
        Assert.IsType<RecapGridStoreReadResult<RecapRowView>.Missing>(
            handle.Reader.ReadViewAt(spec.Coordinate.AssignmentKey)
        );
        Assert.IsType<RecapGridCellPutResult.Inserted>(
            handle.Writer.PutCell(cell)
        );
        Assert.IsType<RecapGridMissingResult.Complete>(
            handle.Reader.FindMissingAssignments(spec)
        );
        Assert.IsType<RecapGridRowViewPutResult.Inserted>(
            handle.Writer.PutRowView(spec, view)
        );
        Assert.IsType<RecapGridRowViewPutResult.AlreadyPresent>(
            handle.Writer.PutRowView(spec, view)
        );
        Assert.Equal(
            view.ToCanonicalBytes(),
            Assert.IsType<RecapGridStoreReadResult<RecapRowView>.Found>(
                handle.Reader.ReadViewAt(spec.Coordinate.AssignmentKey)
            ).Value.ToCanonicalBytes()
        );
        Assert.IsType<RecapGridStoreReadResult<RecapRowView>.Missing>(
            handle.Reader.ReadViewAt(new RowViewAssignmentKey(
                new RefId(2),
                spec.TimelineId,
                spec.RecipeDigest,
                spec.HistoryRowId
            ))
        );
    }

    [Fact]
    public void AssignmentConflictDoesNotBypassExactPriorInputResolution() {
        Directory.CreateDirectory(_root);
        Assert.IsType<RecapGridStoreCreateResult.Created>(
            RecapGridStoreFactory.Create(_root)
        );
        (RowBuildSpec firstSpec, RecapCellArtifact firstCell,
            RecapRowView firstView, _) = RowValues("first");
        var timeline = new TimelineId("00112233445566778899aabbccddeeff");
        var definition = new MaintainerDefinitionDigest(new string('a', 64));
        var column = new LogicalColumnId("case.culprit");
        GridBuildRecipe recipe = GridBuildRecipe.CreateFull(
            timeline,
            new HistoryRowId(new string('c', 64)),
            BuildTarget.Create([new BuildTargetColumn(column, definition)])
        );
        var descriptor = new HistorySegmentDescriptorDigest(
            new string('e', 64)
        );
        var priorA = new PriorInputReference.Projection(
            new PriorInputProjectionDigest(new string('1', 64))
        );
        var priorB = new PriorInputReference.Projection(
            new PriorInputProjectionDigest(new string('2', 64))
        );
        EvaluationKey evaluationA = EvaluationKey.Create(
            descriptor,
            definition,
            priorA
        );
        EvaluationKey evaluationB = EvaluationKey.Create(
            descriptor,
            definition,
            priorB
        );
        RecapCellArtifact cellA = RecapCellArtifact.Create(
            column,
            definition,
            evaluationA,
            RecapCellOutcome.Updated,
            "second",
            RecapGridLimits.MaximumContentUtf8Bytes
        );
        RecapCellArtifact cellB = RecapCellArtifact.Create(
            column,
            definition,
            evaluationB,
            RecapCellOutcome.Updated,
            "competing second",
            RecapGridLimits.MaximumContentUtf8Bytes
        );
        var rowId = new HistoryRowId(new string('e', 64));
        var coordinate = new RowViewCoordinate(
            new RefId(1),
            timeline,
            rowId,
            descriptor,
            recipe.Digest,
            recipe.Target.Digest,
            firstSpec.HistoryRowId,
            firstView.Digest,
            bootstrapCompleted: true
        );
        RowBuildSpec exactSpec = RowBuildSpec.CreateNormal(
            recipe,
            coordinate,
            priorA,
            [new RowBuildAssignment.Evaluate(column, evaluationA)]
        );
        RecapRowView secondView = RecapRowView.Create(exactSpec, [cellA]);
        RowBuildSpec wrongPriorSpec = RowBuildSpec.CreateNormal(
            recipe,
            coordinate,
            priorB,
            [new RowBuildAssignment.Evaluate(column, evaluationB)]
        );
        RecapRowView competingView = RecapRowView.Create(
            wrongPriorSpec,
            [cellB]
        );

        using RecapGridStoreHandle handle = Assert.IsType<
            RecapGridStoreOpenResult.Opened
        >(RecapGridStoreFactory.Open(_root)).Handle;
        Assert.IsType<RecapGridCellPutResult.Inserted>(
            handle.Writer.PutCell(firstCell)
        );
        Assert.IsType<RecapGridRowViewPutResult.Inserted>(
            handle.Writer.PutRowView(firstSpec, firstView)
        );
        Assert.IsType<RecapGridCellPutResult.Inserted>(
            handle.Writer.PutCell(cellA)
        );
        Assert.IsType<RecapGridRowViewPutResult.Inserted>(
            handle.Writer.PutRowView(exactSpec, secondView)
        );
        Assert.IsType<RecapGridCellPutResult.Inserted>(
            handle.Writer.PutCell(cellB)
        );
        Assert.Equal(
            "RowViewAssignmentConflict",
            Assert.IsType<RecapGridRowViewPutResult.Invalid>(
                handle.Writer.PutRowView(wrongPriorSpec, competingView)
            ).Code
        );
    }

    [Fact]
    public void FulfilledReferenceIsExactAndIdempotent() {
        Directory.CreateDirectory(_root);
        Assert.IsType<RecapGridStoreCreateResult.Created>(
            RecapGridStoreFactory.Create(_root)
        );
        (RowBuildSpec spec, RecapCellArtifact cell, RecapRowView view,
            FulfilledViewKey key) = RowValues("fulfilled answer");
        using RecapGridStoreHandle handle = Assert.IsType<
            RecapGridStoreOpenResult.Opened
        >(RecapGridStoreFactory.Open(_root)).Handle;
        Assert.IsType<RecapGridFulfilledPutResult.PrerequisiteMissing>(
            handle.Writer.PutFulfilled(key, view.Digest)
        );
        Assert.IsType<RecapGridCellPutResult.Inserted>(
            handle.Writer.PutCell(cell)
        );
        Assert.IsType<RecapGridRowViewPutResult.Inserted>(
            handle.Writer.PutRowView(spec, view)
        );
        Assert.IsType<RecapGridFulfilledPutResult.Inserted>(
            handle.Writer.PutFulfilled(key, view.Digest)
        );
        Assert.IsType<RecapGridFulfilledPutResult.AlreadyPresent>(
            handle.Writer.PutFulfilled(key, view.Digest)
        );
        Assert.Equal(
            view.Digest,
            Assert.IsType<
                RecapGridStoreReadResult<RecapGridFulfilledView>.Found
            >(handle.Reader.ReadFulfilled(key)).Value.ViewDigest
        );
    }

    private static RecapCellArtifact Cell(string content) {
        var definition = new MaintainerDefinitionDigest(new string('a', 64));
        var evaluation = EvaluationKey.Create(
            new HistorySegmentDescriptorDigest(new string('b', 64)),
            definition,
            PriorInputReference.FirstRow.Value
        );
        return RecapCellArtifact.Create(
            new LogicalColumnId("case.culprit"),
            definition,
            evaluation,
            RecapCellOutcome.Updated,
            content,
            RecapGridLimits.MaximumContentUtf8Bytes
        );
    }

    private static (
        RowBuildSpec Spec,
        RecapCellArtifact Cell,
        RecapRowView View,
        FulfilledViewKey Fulfilled
    ) RowValues(string content) {
        var timeline = new TimelineId("00112233445566778899aabbccddeeff");
        var definition = new MaintainerDefinitionDigest(new string('a', 64));
        var column = new LogicalColumnId("case.culprit");
        var target = BuildTarget.Create([
            new BuildTargetColumn(column, definition)
        ]);
        GridBuildRecipe recipe = GridBuildRecipe.CreateFull(
            timeline,
            new HistoryRowId(new string('c', 64)),
            target
        );
        var descriptor = new HistorySegmentDescriptorDigest(
            new string('b', 64)
        );
        EvaluationKey evaluation = EvaluationKey.Create(
            descriptor,
            definition,
            PriorInputReference.FirstRow.Value
        );
        RecapCellArtifact cell = RecapCellArtifact.Create(
            column,
            definition,
            evaluation,
            RecapCellOutcome.Updated,
            content,
            RecapGridLimits.MaximumContentUtf8Bytes
        );
        RowBuildSpec spec = RowBuildSpec.CreateFull(
            recipe,
            new RowViewCoordinate(
                new RefId(1),
                timeline,
                new HistoryRowId(new string('c', 64)),
                descriptor,
                recipe.Digest,
                target.Digest,
                previousHistoryRowId: null,
                previousViewDigest: null,
                bootstrapCompleted: true
            ),
            PriorInputReference.FirstRow.Value,
            [new RowBuildAssignment.Evaluate(column, evaluation)]
        );
        RecapRowView view = RecapRowView.Create(spec, [cell]);
        var timelineHead = new TimelineHeadRef(
            timeline,
            new RefId(1),
            null,
            new string('d', 64),
            null,
            generation: 1
        );
        FulfilledViewKey fulfilled = FulfilledViewKey.Create(
            timelineHead.RefId,
            timelineHead,
            view.RowDescriptorDigest,
            recipe
        );
        return (spec, cell, view, fulfilled);
    }

    public void Dispose() {
        if (Directory.Exists(_root)) {
            Directory.Delete(_root, recursive: true);
        }
    }
}
