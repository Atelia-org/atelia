using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid.Store;
using Atelia.EventJournal;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Atelia.SessionJournal.RecapGrid.Store.Tests;

public sealed class StoreAuthorityRegressionTests : IDisposable {
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "atelia-recap-grid-store-authority-tests",
        Guid.NewGuid().ToString("N")
    );

    [Fact]
    public void PutCellRejectsEvaluationDigestBoundToDifferentCanonicalKey() {
        Create();
        RecapCellArtifact stored = Cell('b', "stored");
        RecapCellArtifact proposed = Cell('c', "proposed");
        using RecapGridStoreHandle handle = Open();
        Assert.IsType<RecapGridCellPutResult.Inserted>(
            handle.Writer.PutCell(stored)
        );
        Corrupt(
            "UPDATE cell_artifact SET evaluation_key_digest = $value;",
            proposed.EvaluationKey.Digest.Value
        );
        byte[] before = DatabaseBytes();

        Assert.IsType<RecapGridCellPutResult.Invalid>(
            handle.Writer.PutCell(proposed)
        );
        Assert.Equal(before, DatabaseBytes());
    }

    [Fact]
    public void EvaluateAssignmentDigestCollisionIsStickyInvalid() {
        Create();
        RecapCellArtifact stored = Cell('b', "stored");
        (RowBuildSpec spec, RecapCellArtifact proposed, RecapRowView view) =
            FirstRow('c', "proposed");
        using RecapGridStoreHandle handle = Open();
        Assert.IsType<RecapGridCellPutResult.Inserted>(
            handle.Writer.PutCell(stored)
        );
        Corrupt(
            "UPDATE cell_artifact SET evaluation_key_digest = $value;",
            proposed.EvaluationKey.Digest.Value
        );
        byte[] before = DatabaseBytes();

        RecapGridRowViewPutResult.Invalid invalid = Assert.IsType<
            RecapGridRowViewPutResult.Invalid
        >(handle.Writer.PutRowView(spec, view));
        Assert.Equal(before, DatabaseBytes());
        Assert.Equal(
            invalid.Code,
            Assert.IsType<RecapGridCellPutResult.Invalid>(
                handle.Writer.PutCell(Cell('d', "after latch"))
            ).Code
        );
    }

    [Fact]
    public void ReuseAssignmentDigestCollisionIsStickyInvalid() {
        Create();
        var timeline = new TimelineId("00112233445566778899aabbccddeeff");
        var columnX = new LogicalColumnId("case.culprit");
        var columnY = new LogicalColumnId("case.motive");
        var definitionX = new MaintainerDefinitionDigest(new string('a', 64));
        var definitionY = new MaintainerDefinitionDigest(new string('b', 64));
        BuildTarget target = BuildTarget.Create([
            new BuildTargetColumn(columnX, definitionX),
            new BuildTargetColumn(columnY, definitionY)
        ]);
        GridBuildRecipe full = GridBuildRecipe.CreateFull(
            timeline,
            new HistoryRowId(new string('c', 64)),
            target
        );
        GridBuildRecipe overlay = GridBuildRecipe.CreateOverlay(
            full,
            new HistoryRowId(new string('d', 64)),
            target,
            [columnX]
        );
        var descriptor = new HistorySegmentDescriptorDigest(new string('d', 64));
        EvaluationKey evaluationX = EvaluationKey.Create(
            descriptor,
            definitionX,
            PriorInputReference.FirstRow.Value
        );
        EvaluationKey evaluationY = EvaluationKey.Create(
            descriptor,
            definitionY,
            PriorInputReference.FirstRow.Value
        );
        RecapCellArtifact cellX = RecapCellArtifact.Create(
            columnX,
            definitionX,
            evaluationX,
            RecapCellOutcome.Updated,
            "x",
            RecapGridLimits.MaximumContentUtf8Bytes
        );
        RecapCellArtifact storedY = RecapCellArtifact.Create(
            columnY,
            definitionY,
            evaluationY,
            RecapCellOutcome.Updated,
            "stored-y",
            RecapGridLimits.MaximumContentUtf8Bytes
        );
        RecapCellArtifact intendedY = RecapCellArtifact.Create(
            columnY,
            definitionY,
            evaluationY,
            RecapCellOutcome.Updated,
            "intended-y",
            RecapGridLimits.MaximumContentUtf8Bytes
        );
        RowBuildSpec spec = RowBuildSpec.CreateOverlayBootstrap(
            overlay,
            new RowViewCoordinate(
                new RefId(1),
                timeline,
                new HistoryRowId(new string('d', 64)),
                descriptor,
                overlay.Digest,
                target.Digest,
                previousHistoryRowId: null,
                previousViewDigest: null,
                bootstrapCompleted: true
            ),
            PriorInputReference.FirstRow.Value,
            [
                new RowBuildAssignment.Evaluate(columnX, evaluationX),
                new RowBuildAssignment.Reuse(columnY, intendedY)
            ]
        );
        RecapRowView view = RecapRowView.Create(spec, [cellX, intendedY]);
        using RecapGridStoreHandle handle = Open();
        Assert.IsType<RecapGridCellPutResult.Inserted>(
            handle.Writer.PutCell(cellX)
        );
        Assert.IsType<RecapGridCellPutResult.Inserted>(
            handle.Writer.PutCell(storedY)
        );
        Corrupt(
            "UPDATE cell_artifact SET cell_digest = $value WHERE logical_column_id = 'case.motive';",
            intendedY.CellDigest.Value
        );
        byte[] before = DatabaseBytes();

        Assert.IsType<RecapGridRowViewPutResult.Invalid>(
            handle.Writer.PutRowView(spec, view)
        );
        Assert.Equal(before, DatabaseBytes());
    }

    [Fact]
    public void BootstrapCompletionCannotRegressAlongExactAssignmentChain() {
        Create();
        var timeline = new TimelineId("00112233445566778899aabbccddeeff");
        var column = new LogicalColumnId("case.culprit");
        var definition = new MaintainerDefinitionDigest(new string('a', 64));
        BuildTarget target = BuildTarget.Create([
            new BuildTargetColumn(column, definition)
        ]);
        GridBuildRecipe baseRecipe = GridBuildRecipe.CreateFull(
            timeline,
            RowId('1'),
            target
        );
        GridBuildRecipe overlay = GridBuildRecipe.CreateOverlay(
            baseRecipe,
            RowId('3'),
            target,
            [column]
        );
        var bootstrapDescriptor = new HistorySegmentDescriptorDigest(
            new string('3', 64)
        );
        EvaluationKey bootstrapEvaluation = EvaluationKey.Create(
            bootstrapDescriptor,
            definition,
            PriorInputReference.FirstRow.Value
        );
        RecapCellArtifact bootstrapCell = RecapCellArtifact.Create(
            column,
            definition,
            bootstrapEvaluation,
            RecapCellOutcome.Updated,
            "bootstrap",
            RecapGridLimits.MaximumContentUtf8Bytes
        );
        RowBuildSpec bootstrapSpec = RowBuildSpec.CreateOverlayBootstrap(
            overlay,
            new RowViewCoordinate(
                new RefId(1), timeline, RowId('3'), bootstrapDescriptor,
                overlay.Digest, target.Digest, null, null,
                bootstrapCompleted: true
            ),
            PriorInputReference.FirstRow.Value,
            [new RowBuildAssignment.Evaluate(column, bootstrapEvaluation)]
        );
        RecapRowView bootstrapView = RecapRowView.Create(
            bootstrapSpec,
            [bootstrapCell]
        );
        var projection = new PriorInputReference.Projection(
            PriorInputProjection.Create([
                new PriorProjectedContent(column, bootstrapCell.ContentDigest)
            ]).Digest
        );
        var earlierDescriptor = new HistorySegmentDescriptorDigest(
            new string('2', 64)
        );
        EvaluationKey earlierEvaluation = EvaluationKey.Create(
            earlierDescriptor,
            definition,
            projection
        );
        RecapCellArtifact earlierCell = RecapCellArtifact.Create(
            column,
            definition,
            earlierEvaluation,
            RecapCellOutcome.Updated,
            "earlier",
            RecapGridLimits.MaximumContentUtf8Bytes
        );
        RowBuildSpec regressingSpec = RowBuildSpec.CreateOverlayBootstrap(
            overlay,
            new RowViewCoordinate(
                new RefId(1), timeline, RowId('2'), earlierDescriptor,
                overlay.Digest, target.Digest, RowId('3'),
                bootstrapView.Digest, bootstrapCompleted: false
            ),
            projection,
            [new RowBuildAssignment.Evaluate(column, earlierEvaluation)]
        );
        RecapRowView regressingView = RecapRowView.Create(
            regressingSpec,
            [earlierCell]
        );

        using RecapGridStoreHandle handle = Open();
        Assert.IsType<RecapGridCellPutResult.Inserted>(
            handle.Writer.PutCell(bootstrapCell)
        );
        Assert.IsType<RecapGridRowViewPutResult.Inserted>(
            handle.Writer.PutRowView(bootstrapSpec, bootstrapView)
        );
        Assert.IsType<RecapGridCellPutResult.Inserted>(
            handle.Writer.PutCell(earlierCell)
        );
        Assert.Equal(
            "BootstrapRecurrenceMismatch",
            Assert.IsType<RecapGridRowViewPutResult.Rejected>(
                handle.Writer.PutRowView(regressingSpec, regressingView)
            ).Code
        );
    }

    [Theory]
    [InlineData("cell-locator")]
    [InlineData("view-locator")]
    [InlineData("member-ordinal-gap")]
    [InlineData("member-extra")]
    [InlineData("member-orphan")]
    [InlineData("fulfilled-physical")]
    [InlineData("unknown-schema")]
    [InlineData("truncate")]
    public void VerifyAndExportFailClosedForPhysicalCorruption(string kind) {
        Create();
        (RowBuildSpec spec, RecapCellArtifact cell, RecapRowView view) =
            FirstRow('c', "graph");
        GridBuildRecipe recipe = FullRecipe();
        var head = new TimelineHeadRef(
            recipe.TimelineId,
            new RefId(1),
            null,
            new string('d', 64),
            null,
            generation: 1
        );
        FulfilledViewKey key = FulfilledViewKey.Create(
            head.RefId,
            head,
            view.RowDescriptorDigest,
            recipe
        );
        using (RecapGridStoreHandle handle = Open()) {
            Assert.IsType<RecapGridCellPutResult.Inserted>(
                handle.Writer.PutCell(cell)
            );
            Assert.IsType<RecapGridRowViewPutResult.Inserted>(
                handle.Writer.PutRowView(spec, view)
            );
            Assert.IsType<RecapGridFulfilledPutResult.Inserted>(
                handle.Writer.PutFulfilled(key, view.Digest)
            );
        }
        ApplyGraphCorruption(kind, cell, view, key);

        if (kind == "unknown-schema") {
            Assert.IsType<RecapGridStoreVerifyResult.UnsupportedSchema>(
                RecapGridStoreMaintenance.Verify(_root)
            );
            Assert.IsType<RecapGridStoreExportResult.UnsupportedSchema>(
                RecapGridStoreMaintenance.Export(_root)
            );
            return;
        }
        Assert.IsType<RecapGridStoreVerifyResult.Unhealthy>(
            RecapGridStoreMaintenance.Verify(_root)
        );
        Assert.IsType<RecapGridStoreExportResult.Invalid>(
            RecapGridStoreMaintenance.Export(_root)
        );
    }

    private void Create() {
        Directory.CreateDirectory(_root);
        Assert.IsType<RecapGridStoreCreateResult.Created>(
            RecapGridStoreFactory.Create(_root)
        );
    }

    private RecapGridStoreHandle Open() => Assert.IsType<
        RecapGridStoreOpenResult.Opened
    >(RecapGridStoreFactory.Open(_root)).Handle;

    private void Corrupt(string sql, string value) {
        using var connection = new SqliteConnection(
            $"Data Source={new StorePaths(_root).DatabasePath};Mode=ReadWrite;Pooling=False;Foreign Keys=False"
        );
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$value", value);
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    private void ApplyGraphCorruption(
        string kind,
        RecapCellArtifact cell,
        RecapRowView view,
        FulfilledViewKey key
    ) {
        if (kind == "truncate") {
            string path = new StorePaths(_root).DatabasePath;
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Write,
                FileShare.None
            );
            stream.SetLength(Math.Max(1, stream.Length / 2));
            stream.Flush(flushToDisk: true);
            return;
        }
        using var connection = new SqliteConnection(
            $"Data Source={new StorePaths(_root).DatabasePath};Mode=ReadWrite;Pooling=False;Foreign Keys=False"
        );
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = kind switch {
            "cell-locator" =>
                "UPDATE cell_artifact SET logical_column_id = 'case.other';",
            "view-locator" =>
                $"UPDATE row_view SET target_digest = '{new string('0', 64)}';",
            "member-ordinal-gap" =>
                "UPDATE row_view_member SET column_ordinal = 1;",
            "member-extra" => """
                INSERT INTO row_view_member(
                    view_digest, column_ordinal, logical_column_id,
                    definition_digest, cell_digest
                ) VALUES (
                    $view, 1, 'case.extra', $definition, $cell
                );
                """,
            "member-orphan" => "DELETE FROM cell_artifact;",
            "fulfilled-physical" =>
                "UPDATE fulfilled_view_ref SET key_canonical = $canonical;",
            "unknown-schema" => "PRAGMA user_version = 99;",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        command.Parameters.AddWithValue("$view", view.Digest.Value);
        command.Parameters.AddWithValue("$definition", cell.DefinitionDigest.Value);
        command.Parameters.AddWithValue("$cell", cell.CellDigest.Value);
        var otherHead = new TimelineHeadRef(
            key.TimelineId,
            new RefId(2),
            null,
            new string('d', 64),
            null,
            key.TimelineHeadGeneration
        );
        command.Parameters.AddWithValue(
            "$canonical",
            FulfilledViewKey.Create(
                otherHead.RefId,
                otherHead,
                key.ThroughRowDescriptorDigest,
                FullRecipe()
            ).ToCanonicalBytes()
        );
        Assert.True(command.ExecuteNonQuery() >= 0);
    }

    private byte[] DatabaseBytes() => File.ReadAllBytes(
        new StorePaths(_root).DatabasePath
    );

    private static RecapCellArtifact Cell(char descriptor, string content) {
        var definition = new MaintainerDefinitionDigest(new string('a', 64));
        EvaluationKey evaluation = EvaluationKey.Create(
            new HistorySegmentDescriptorDigest(new string(descriptor, 64)),
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

    private static (RowBuildSpec Spec, RecapCellArtifact Cell,
        RecapRowView View) FirstRow(char descriptorToken, string content) {
        GridBuildRecipe recipe = FullRecipe();
        var definition = new MaintainerDefinitionDigest(new string('a', 64));
        var column = new LogicalColumnId("case.culprit");
        var descriptor = new HistorySegmentDescriptorDigest(
            new string(descriptorToken, 64)
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
                recipe.TimelineId,
                new HistoryRowId(new string(descriptorToken, 64)),
                descriptor,
                recipe.Digest,
                recipe.Target.Digest,
                previousHistoryRowId: null,
                previousViewDigest: null,
                bootstrapCompleted: true
            ),
            PriorInputReference.FirstRow.Value,
            [new RowBuildAssignment.Evaluate(column, evaluation)]
        );
        return (spec, cell, RecapRowView.Create(spec, [cell]));
    }

    private static GridBuildRecipe FullRecipe() {
        var definition = new MaintainerDefinitionDigest(new string('a', 64));
        return GridBuildRecipe.CreateFull(
            new TimelineId("00112233445566778899aabbccddeeff"),
            new HistoryRowId(new string('c', 64)),
            BuildTarget.Create([
                new BuildTargetColumn(
                    new LogicalColumnId("case.culprit"),
                    definition
                )
            ])
        );
    }

    private static HistoryRowId RowId(char value)
        => new(new string(value, 64));

    public void Dispose() {
        if (Directory.Exists(_root)) {
            Directory.Delete(_root, recursive: true);
        }
    }
}
