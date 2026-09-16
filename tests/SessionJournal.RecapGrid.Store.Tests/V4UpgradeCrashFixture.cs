using Atelia.EventJournal;
using Atelia.SessionJournal;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid.Cadence;
using Atelia.SessionJournal.RecapGrid.Control;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Atelia.SessionJournal.RecapGrid.Store.Tests;

internal sealed record V4UpgradeCrashFixture(
    string Repository,
    string DatabasePath,
    byte[] OriginalDatabase,
    RowWork ExpectedWork,
    RecapCellArtifact ExpectedCell,
    RecapRowView ExpectedPriorRow,
    RecapRowView? ExpectedTerminalRow
) {
    internal static V4UpgradeCrashFixture Create(
        string repository,
        bool partial
    ) {
        RefId refId = CreateAuthorityStores(repository);
        Assert.IsType<RecapGridStoreCreateResult.Created>(
            RecapGridStoreFactory.Create(repository));
        var paths = new StorePaths(repository);
        File.Delete(paths.DatabasePath);

        GridBuildRecipe recipe = StoreFixture.Recipe();
        RowBuildSpec priorSpec = StoreFixture.Spec(
            recipe,
            new HistoryRowId(new string('d', 64)),
            refId: refId);
        RecapCellArtifact priorCell = StoreFixture.Proposed(
            priorSpec, "v4 prior content");
        var priorRow = new RecapRowView(
            new RowResultId(Guid.NewGuid().ToString("N")),
            priorSpec.Coordinate,
            [new RecapRowViewCell(StoreFixture.Column,
                StoreFixture.Definition, priorCell.Id)]);
        HistoryRowId terminalHistory = new(new string('e', 64));
        RowWork expectedWork = new(
            new RowWorkKey(refId, StoreFixture.Timeline, recipe.Digest,
                terminalHistory),
            recipe.Target,
            priorRow.HistoryRowId,
            priorRow.Id,
            [new RowWorkAssignment(StoreFixture.Column, null)]);
        RecapCellArtifact terminalCell = new(
            new CellId(Guid.NewGuid().ToString("N")),
            new CellSlot(recipe.Digest, terminalHistory,
                expectedWork.WorkId, StoreFixture.Column),
            StoreFixture.Definition,
            RecapCellOutcome.Updated,
            partial ? "v4 partial content" : "v4 terminal content");
        RecapRowView? terminalRow = partial
            ? null
            : new RecapRowView(
                new RowResultId(Guid.NewGuid().ToString("N")),
                new RowViewCoordinate(refId, StoreFixture.Timeline,
                    terminalHistory, recipe.Digest, recipe.Target.Digest,
                    priorRow.HistoryRowId, priorRow.Id,
                    bootstrapCompleted: true),
                [new RecapRowViewCell(StoreFixture.Column,
                    StoreFixture.Definition, terminalCell.Id)]);

        WriteV4(paths.DatabasePath, priorCell, priorRow, terminalCell,
            terminalRow);
        return new V4UpgradeCrashFixture(
            repository,
            paths.DatabasePath,
            File.ReadAllBytes(paths.DatabasePath),
            expectedWork,
            terminalCell,
            priorRow,
            terminalRow);
    }

    private static RefId CreateAuthorityStores(string repository) {
        var estimator = new O200kBaseHistoryUnitLoadEstimator();
        RefId refId;
        using (SessionJournalEngine journal = SessionJournalEngine.Create(
                   repository,
                   new SessionCreateOptions("model", "system",
                       "recap-grid-v4-crash"))) {
            refId = journal.BranchRefId;
            _ = Assert.IsType<HistoryTimelineCreateResult.Created>(
                HistoryTimelineFactory.Create(
                    journal.ReadView,
                    new HistoryTimelineInitialPolicySpec(
                        HistoryPartitionAlgorithms
                            .FirstReplaySafeBoundaryAtTargetV1,
                        O200kBaseHistoryUnitLoadEstimator.EstimatorId,
                        new HistoryLoadUnit(1),
                        maxRawEvents: 8,
                        maxRenderedBytes: 1024 * 1024),
                    estimator));
            _ = Assert.IsType<RecapGridCadenceCreateResult.Created>(
                RecapGridCadenceFactory.Create(
                    journal,
                    new RecapGridCadencePolicySpec(
                        minimumRecentHistoryLoad: 1,
                        HistoryPartitionAlgorithms
                            .FirstReplaySafeBoundaryAtTargetV1,
                        O200kBaseHistoryUnitLoadEstimator.EstimatorId,
                        targetHistoryLoad: 1,
                        maxRawEvents: 8,
                        maxRenderedBytes: 1024 * 1024)));
        }
        var admission = new RecapGridControlAdmission(
            RecapGridControlPermission.Create,
            [],
            [],
            [],
            ["case."],
            maximumBootstrapRows: 0,
            maximumProjectedCalls: 0);
        _ = Assert.IsType<RecapGridControlCreateResult.Created>(
            RecapGridControlFactory.Create(repository, refId, admission));
        return refId;
    }

    private static void WriteV4(
        string path,
        RecapCellArtifact priorCell,
        RecapRowView priorRow,
        RecapCellArtifact terminalCell,
        RecapRowView? terminalRow
    ) {
        using var connection = new SqliteConnection(
            $"Data Source={path};Mode=ReadWriteCreate;Pooling=False");
        connection.Open();
        using (SqliteCommand schema = connection.CreateCommand()) {
            schema.CommandText = ReadV4Schema()
                + $"PRAGMA application_id = {SqliteRecapGridStore.ApplicationId};"
                + "PRAGMA user_version = 4;";
            _ = schema.ExecuteNonQuery();
        }
        using SqliteTransaction transaction = connection.BeginTransaction();
        int rows = terminalRow is null ? 1 : 2;
        Execute(connection, transaction, """
            INSERT INTO store_metadata(singleton,schema_version,
                store_instance_id,cell_count,row_view_count,
                row_view_member_count,fulfilled_view_count)
            VALUES(1,4,'00112233445566778899aabbccddeeff',2,$rows,$rows,0);
            """, ("$rows", rows));
        InsertCell(connection, transaction, priorCell);
        InsertCell(connection, transaction, terminalCell);
        InsertRow(connection, transaction, priorRow);
        if (terminalRow is not null) {
            InsertRow(connection, transaction, terminalRow);
        }
        transaction.Commit();
    }

    private static void InsertCell(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RecapCellArtifact cell
    ) => Execute(connection, transaction, """
        INSERT INTO cell_artifact(cell_id,recipe_digest,history_row_id,
            logical_column_id,definition_digest,outcome,content)
        VALUES($id,$recipe,$history,$column,$definition,$outcome,$content);
        """, ("$id", cell.Id.Value),
        ("$recipe", cell.Slot.RecipeDigest.Value),
        ("$history", cell.Slot.HistoryRowId.Value),
        ("$column", cell.Slot.LogicalColumnId.Value),
        ("$definition", cell.DefinitionDigest.Value),
        ("$outcome", (int)cell.Outcome),
        ("$content", cell.Content));

    private static void InsertRow(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RecapRowView row
    ) {
        Execute(connection, transaction, """
            INSERT INTO row_view(row_result_id,ref_id,timeline_id,
                history_row_id,recipe_digest,target_digest,
                previous_history_row_id,previous_row_result_id,
                bootstrap_completed)
            VALUES($id,$ref,$timeline,$history,$recipe,$target,
                $previousHistory,$previousResult,$bootstrap);
            """, ("$id", row.Id.Value), ("$ref", row.RefId.ToHexString()),
            ("$timeline", row.TimelineId.Value),
            ("$history", row.HistoryRowId.Value),
            ("$recipe", row.RecipeDigest.Value),
            ("$target", row.TargetDigest.Value),
            ("$previousHistory",
                (object?)row.PreviousHistoryRowId?.Value ?? DBNull.Value),
            ("$previousResult",
                (object?)row.PreviousRowResultId?.Value ?? DBNull.Value),
            ("$bootstrap", row.BootstrapCompleted ? 1 : 0));
        RecapRowViewCell member = Assert.Single(row.OrderedCells);
        Execute(connection, transaction, """
            INSERT INTO row_view_member(row_result_id,column_ordinal,
                logical_column_id,definition_digest,cell_id)
            VALUES($row,0,$column,$definition,$cell);
            """, ("$row", row.Id.Value),
            ("$column", member.LogicalColumnId.Value),
            ("$definition", member.DefinitionDigest.Value),
            ("$cell", member.CellId.Value));
    }

    private static void Execute(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        params (string Name, object Value)[] values
    ) {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach ((string name, object value) in values) {
            command.Parameters.AddWithValue(name, value);
        }
        _ = command.ExecuteNonQuery();
    }

    private static string ReadV4Schema() {
        using Stream stream = typeof(SqliteRecapGridStore).Assembly
            .GetManifestResourceStream(
                "Atelia.SessionJournal.RecapGrid.Store.SchemaV4.sql")
            ?? throw new InvalidOperationException(
                "V4 Store schema is absent.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
