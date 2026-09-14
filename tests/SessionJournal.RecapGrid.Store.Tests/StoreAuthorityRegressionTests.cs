using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid.Store;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Atelia.SessionJournal.RecapGrid.Store.Tests;

public sealed class StoreAuthorityRegressionTests : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), "atelia-recap-grid-authority", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("member-ordinal-gap")]
    [InlineData("member-orphan")]
    [InlineData("member-column")]
    [InlineData("cell-outcome")]
    [InlineData("fulfilled-orphan")]
    [InlineData("truncate")]
    public void VerifyAndExportRejectBrokenSqlRelations(string kind) {
        Create();
        RowBuildSpec spec = StoreFixture.Spec();
        using (RecapGridStoreHandle handle = Open()) {
            RecapCellArtifact cell = StoreFixture.Put(handle, spec);
            RecapRowView view = Assert.IsType<RecapGridRowViewPutResult.Inserted>(handle.Writer.PutRowView(spec, [cell])).Winner;
            Assert.IsType<RecapGridFulfilledPutResult.Inserted>(handle.Writer.PutFulfilled(StoreFixture.Fulfilled(spec), view.Id));
        }
        if (kind == "truncate") {
            using var stream = new FileStream(new StorePaths(_root).DatabasePath, FileMode.Open, FileAccess.Write, FileShare.None);
            stream.SetLength(stream.Length / 2);
            stream.Flush(flushToDisk: true);
        }
        else {
            Execute("PRAGMA ignore_check_constraints = ON; " + (kind switch {
                "member-ordinal-gap" => "UPDATE row_view_member SET column_ordinal = 1;",
                "member-orphan" => "DELETE FROM cell_artifact;",
                "member-column" => "UPDATE row_view_member SET logical_column_id = 'case.other';",
                "cell-outcome" => "UPDATE cell_artifact SET outcome = 99;",
                "fulfilled-orphan" => "UPDATE fulfilled_view_ref SET row_result_id = '11111111111111111111111111111111';",
                _ => throw new ArgumentOutOfRangeException(nameof(kind))
            }));
        }
        Assert.IsType<RecapGridStoreVerifyResult.Unhealthy>(RecapGridStoreMaintenance.Verify(_root));
        Assert.IsType<RecapGridStoreExportResult.Invalid>(RecapGridStoreMaintenance.Export(_root));
    }

    [Theory]
    [InlineData("cell-id")]
    [InlineData("slot-recipe")]
    public void MalformedStoredCellFieldsReturnInvalidWithoutChangingDatabase(string field) {
        Create();
        RecapCellArtifact cell;
        using (RecapGridStoreHandle setup = Open()) cell = StoreFixture.Put(setup, StoreFixture.Spec());
        Execute(field switch {
            "cell-id" => "UPDATE cell_artifact SET cell_id = 'short';",
            "slot-recipe" => "UPDATE cell_artifact SET recipe_digest = 'not-a-recipe';",
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        });
        string path = new StorePaths(_root).DatabasePath;
        byte[] before = File.ReadAllBytes(path);
        using (RecapGridStoreReaderHandle reader = Assert.IsType<RecapGridStoreReaderOpenResult.Opened>(
            RecapGridStoreFactory.OpenReader(_root)).Handle) {
            RecapGridStoreReadResult<RecapCellArtifact> result = field == "cell-id"
                ? reader.Reader.TryReadCell(cell.Slot)
                : reader.Reader.ReadCell(cell.Id);
            Assert.IsType<RecapGridStoreReadResult<RecapCellArtifact>.Invalid>(result);
        }
        var invalid = Assert.IsType<RecapGridStoreVerifyResult.Unhealthy>(RecapGridStoreMaintenance.Verify(_root));
        Assert.True(invalid.Incomplete);
        Assert.NotEmpty(invalid.Errors);
        Assert.StartsWith("GridStoreInvalid: ", invalid.Errors[0]);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void FullVerificationDetectsCounterMismatch() {
        Create();
        using (RecapGridStoreHandle handle = Open()) StoreFixture.Put(handle, StoreFixture.Spec());
        Execute("UPDATE store_metadata SET cell_count = 9;");
        Assert.IsType<RecapGridStoreVerifyResult.Unhealthy>(RecapGridStoreMaintenance.Verify(_root));
    }

    [Fact]
    public void SchemaUsesSingleSqlAuthorityAndEnforcesSlotAndForeignKeys() {
        Create();
        using RecapGridStoreHandle handle = Open();
        RowBuildSpec spec = StoreFixture.Spec();
        RecapCellArtifact cell = StoreFixture.Put(handle, spec);
        using SqliteConnection connection = OpenRaw();
        connection.Open();
        using (SqliteCommand columns = connection.CreateCommand()) {
            columns.CommandText = "SELECT name FROM pragma_table_info('cell_artifact') UNION ALL SELECT name FROM pragma_table_info('row_view') UNION ALL SELECT name FROM pragma_table_info('fulfilled_view_ref');";
            using SqliteDataReader reader = columns.ExecuteReader();
            var names = new List<string>();
            while (reader.Read()) names.Add(reader.GetString(0));
            Assert.DoesNotContain("canonical", names);
            Assert.DoesNotContain("key_canonical", names);
            Assert.DoesNotContain("evaluation_key_digest", names);
            Assert.DoesNotContain("content_digest", names);
            Assert.Contains("cell_id", names);
            Assert.Contains("row_result_id", names);
        }
        using SqliteCommand duplicate = connection.CreateCommand();
        duplicate.CommandText = "INSERT INTO cell_artifact SELECT '11111111111111111111111111111111', recipe_digest, history_row_id, logical_column_id, definition_digest, outcome, content FROM cell_artifact;";
        Assert.Throws<SqliteException>(() => duplicate.ExecuteNonQuery());
        using SqliteCommand fk = connection.CreateCommand();
        fk.CommandText = "PRAGMA foreign_keys = ON; INSERT INTO row_view_member(row_result_id, column_ordinal, logical_column_id, definition_digest, cell_id) VALUES ('11111111111111111111111111111111', 0, 'case.culprit', $definition, $cell);";
        fk.Parameters.AddWithValue("$definition", cell.DefinitionDigest.Value);
        fk.Parameters.AddWithValue("$cell", cell.Id.Value);
        Assert.Throws<SqliteException>(() => fk.ExecuteNonQuery());
    }

    [Fact]
    public void SameAssignmentWithDifferentCoordinateIsConflictAndLatchesInvalid() {
        Create();
        using RecapGridStoreHandle handle = Open();
        RowBuildSpec spec = StoreFixture.Spec();
        RecapCellArtifact cell = StoreFixture.Put(handle, spec);
        RecapRowView stored = Assert.IsType<RecapGridRowViewPutResult.Inserted>(handle.Writer.PutRowView(spec, [cell])).Winner;
        var coordinate = new RowViewCoordinate(spec.RefId, spec.TimelineId, spec.HistoryRowId,
            new HistorySegmentDescriptorDigest(new string('e', 64)), spec.RecipeDigest, spec.TargetDigest,
            null, null, bootstrapCompleted: true);
        RowBuildSpec conflicting = RowBuildSpec.CreateFull(spec.Recipe, coordinate, spec.OrderedAssignments);
        var invalid = Assert.IsType<RecapGridRowViewPutResult.Invalid>(handle.Writer.PutRowView(conflicting, [cell]));
        Assert.Equal("RowViewAssignmentConflict", invalid.Code);
        Assert.Equal(invalid.Code, Assert.IsType<RecapGridCellPutResult.Invalid>(
            handle.Writer.PutCell(spec, StoreFixture.Draft(spec))).Code);
        using RecapGridStoreHandle reopened = Open();
        Assert.Equal(stored.Id, Assert.IsType<RecapGridStoreReadResult<RecapRowView>.Found>(
            reopened.Reader.ReadViewAt(spec.Coordinate.AssignmentKey)).Value.Id);
    }

    [Fact]
    public void ReadViewMaterializesOnlyMetadataWhileCellAndFullVerifyEnforceContentLimit() {
        Create();
        RowBuildSpec spec = StoreFixture.Spec();
        CellId cellId;
        RowResultId rowId;
        using (RecapGridStoreHandle setup = Open()) {
            RecapCellArtifact cell = StoreFixture.Put(setup, spec);
            cellId = cell.Id;
            rowId = Assert.IsType<RecapGridRowViewPutResult.Inserted>(setup.Writer.PutRowView(spec, [cell])).Winner.Id;
        }
        using (SqliteConnection connection = OpenRaw()) {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE cell_artifact SET content = $content;";
            command.Parameters.AddWithValue("$content", new string('x', RecapGridLimits.MaximumContentUtf8Bytes + 1));
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        using RecapGridStoreHandle reopened = Open();
        RecapRowView row = Assert.IsType<RecapGridStoreReadResult<RecapRowView>.Found>(reopened.Reader.ReadView(rowId)).Value;
        Assert.Equal(cellId, Assert.Single(row.OrderedCells).CellId);
        Assert.IsType<RecapGridStoreReadResult<RecapCellArtifact>.Invalid>(reopened.Reader.ReadCell(cellId));
        Assert.IsType<RecapGridStoreVerifyResult.Unhealthy>(RecapGridStoreMaintenance.Verify(_root));
    }

    [Fact]
    public void LegacySchemaOpenIsReadOnlyUnsupportedAndExplicitResetNeedsNoLegacyReader() {
        Directory.CreateDirectory(_root);
        StorePaths paths = new(_root);
        // A valid older store also has the durable lifetime-lock slot. Without
        // it, opening correctly fails before the database version is inspected.
        StoreDurableFiles.EnsureSlots(paths);
        using (var connection = new SqliteConnection($"Data Source={paths.DatabasePath};Pooling=False")) {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            // Deliberately not a decodable old graph: the version boundary must run before graph reads.
            command.CommandText = "PRAGMA application_id = 1096042322; PRAGMA user_version = 2; CREATE TABLE old_state(value TEXT); INSERT INTO old_state VALUES ('preserved');";
            command.ExecuteNonQuery();
        }
        byte[] before = File.ReadAllBytes(paths.DatabasePath);
        Assert.Equal(2, Assert.IsType<RecapGridStoreOpenResult.UnsupportedSchema>(RecapGridStoreFactory.Open(_root)).SchemaVersion);
        Assert.Equal(2, Assert.IsType<RecapGridStoreReaderOpenResult.UnsupportedSchema>(RecapGridStoreFactory.OpenReader(_root)).SchemaVersion);
        Assert.Equal(before, File.ReadAllBytes(paths.DatabasePath));
        var witness = Assert.IsType<RecapGridStorePrepareResetResult.Prepared>(RecapGridStoreMaintenance.PrepareReset(_root)).Witness;
        Assert.Equal(3, Assert.IsType<RecapGridStoreResetResult.Reset>(RecapGridStoreMaintenance.Reset(_root, witness)).Identity.SchemaVersion);
        Assert.IsType<RecapGridStoreVerifyResult.Healthy>(RecapGridStoreMaintenance.Verify(_root));
        using RecapGridStoreHandle handle = Open();
        StoreFixture.Put(handle, StoreFixture.Spec());
    }

    [Theory]
    [InlineData("user-version-priority", true)]
    [InlineData("journal-mode", false)]
    [InlineData("application-id", false)]
    [InlineData("metadata-absent", false)]
    [InlineData("metadata-duplicate", false)]
    [InlineData("metadata-singleton", false)]
    [InlineData("metadata-schema-version", false)]
    [InlineData("metadata-instance-id", false)]
    [InlineData("unexpected-schema-object", false)]
    [InlineData("missing-schema-object", false)]
    public void SchemaIdentityMutationMapsAcrossEveryOperator(
        string mutation,
        bool unsupported
    ) {
        Create();
        ApplySchemaIdentityMutation(mutation);

        string databasePath = new StorePaths(_root).DatabasePath;
        byte[] beforeCreate = File.ReadAllBytes(databasePath);
        RecapGridStoreCreateResult createdAgain =
            RecapGridStoreFactory.Create(_root);
        Assert.Equal(beforeCreate, File.ReadAllBytes(databasePath));
        RecapGridStoreOpenResult opened = RecapGridStoreFactory.Open(_root);
        RecapGridStoreReaderOpenResult readerOpened =
            RecapGridStoreFactory.OpenReader(_root);
        RecapGridStoreInspectResult inspected =
            RecapGridStoreMaintenance.Inspect(_root);
        RecapGridStoreExportResult exported =
            RecapGridStoreMaintenance.Export(_root);
        RecapGridStoreVerifyResult verified =
            RecapGridStoreMaintenance.Verify(_root);

        if (unsupported) {
            Assert.Equal("GridStoreUnsupportedSchema", Assert.IsType<
                RecapGridStoreCreateResult.Invalid
            >(createdAgain).Code);
            Assert.Equal(99, Assert.IsType<
                RecapGridStoreOpenResult.UnsupportedSchema
            >(opened).SchemaVersion);
            Assert.Equal(99, Assert.IsType<
                RecapGridStoreReaderOpenResult.UnsupportedSchema
            >(readerOpened).SchemaVersion);
            Assert.Equal(99, Assert.IsType<
                RecapGridStoreInspectResult.UnsupportedSchema
            >(inspected).SchemaVersion);
            Assert.Equal(99, Assert.IsType<
                RecapGridStoreExportResult.UnsupportedSchema
            >(exported).SchemaVersion);
            Assert.Equal(99, Assert.IsType<
                RecapGridStoreVerifyResult.UnsupportedSchema
            >(verified).SchemaVersion);
            return;
        }

        Assert.Equal("GridStoreInvalid", Assert.IsType<
            RecapGridStoreCreateResult.Invalid
        >(createdAgain).Code);
        Assert.Equal("GridStoreInvalid", Assert.IsType<
            RecapGridStoreOpenResult.Invalid
        >(opened).Code);
        Assert.Equal("GridStoreInvalid", Assert.IsType<
            RecapGridStoreReaderOpenResult.Invalid
        >(readerOpened).Code);
        Assert.Equal("GridStoreInvalid", Assert.IsType<
            RecapGridStoreInspectResult.Invalid
        >(inspected).Code);
        Assert.Equal("GridStoreInvalid", Assert.IsType<
            RecapGridStoreExportResult.Invalid
        >(exported).Code);
        RecapGridStoreVerifyResult.Unhealthy unhealthy = Assert.IsType<
            RecapGridStoreVerifyResult.Unhealthy
        >(verified);
        Assert.True(unhealthy.Incomplete);
        Assert.NotEmpty(unhealthy.Errors);
        Assert.StartsWith("GridStoreInvalid: ", unhealthy.Errors[0]);
    }


    private void Create() {
        Directory.CreateDirectory(_root);
        Assert.IsType<RecapGridStoreCreateResult.Created>(RecapGridStoreFactory.Create(_root));
    }
    private RecapGridStoreHandle Open() => Assert.IsType<RecapGridStoreOpenResult.Opened>(RecapGridStoreFactory.Open(_root)).Handle;
    private SqliteConnection OpenRaw() => new($"Data Source={new StorePaths(_root).DatabasePath};Mode=ReadWrite;Pooling=False;Foreign Keys=False");
    private void Execute(string sql) {
        using SqliteConnection connection = OpenRaw();
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
    private void ApplySchemaIdentityMutation(string mutation) {
        using SqliteConnection connection = OpenRaw();
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = mutation switch {
            "user-version-priority" => """
                PRAGMA user_version = 99;
                PRAGMA application_id = 0;
                DROP TABLE store_metadata;
                """,
            "journal-mode" => "PRAGMA journal_mode = WAL;",
            "application-id" => "PRAGMA application_id = 0;",
            "metadata-absent" => "DELETE FROM store_metadata;",
            "metadata-duplicate" => """
                PRAGMA ignore_check_constraints = ON;
                INSERT INTO store_metadata(
                    singleton, schema_version, store_instance_id,
                    cell_count, row_view_count,
                    row_view_member_count, fulfilled_view_count
                ) VALUES (2, 3, '00112233445566778899aabbccddeeff',
                    0, 0, 0, 0);
                """,
            "metadata-singleton" => """
                PRAGMA ignore_check_constraints = ON;
                UPDATE store_metadata SET singleton = 2;
                """,
            "metadata-schema-version" => """
                PRAGMA ignore_check_constraints = ON;
                UPDATE store_metadata SET schema_version = 99;
                """,
            "metadata-instance-id" =>
                "UPDATE store_metadata SET store_instance_id = 'bad';",
            "unexpected-schema-object" =>
                "CREATE TABLE unexpected(value INTEGER) STRICT;",
            "missing-schema-object" => "DROP TABLE fulfilled_view_ref;",
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };
        command.ExecuteNonQuery();
    }


    public void Dispose() {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
