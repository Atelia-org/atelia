using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid.Store;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Atelia.SessionJournal.RecapGrid.Store.Tests;

public sealed partial class StoreAuthorityRegressionTests : IDisposable {
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
    public void V4FullStoreUpgradeIsProviderFreeBytePreservingInDryRunAndPreservesIdsOnApply() {
        CreateV4FullStore(out RowBuildSpec spec, out RecapCellArtifact cell,
            out RecapRowView row);
        string path = new StorePaths(_root).DatabasePath;
        byte[] before = File.ReadAllBytes(path);

        RecapGridStoreUpgradeResult dryRun =
            RecapGridStoreMaintenance.UpgradeV4(_root, apply: false);
        Assert.IsType<RecapGridStoreUpgradeResult.DryRunReady>(dryRun);
        Assert.Equal(before, File.ReadAllBytes(path));

        RecapGridStoreUpgradeResult appliedResult =
            RecapGridStoreMaintenance.UpgradeV4(_root, apply: true);
        Assert.True(appliedResult is RecapGridStoreUpgradeResult.Upgraded,
            appliedResult.ToString());
        RecapGridStoreUpgradeResult.Upgraded applied =
            (RecapGridStoreUpgradeResult.Upgraded)appliedResult;
        Assert.True(File.Exists(applied.BackupPath));
        Assert.Equal(before, File.ReadAllBytes(applied.BackupPath));

        using RecapGridStoreReaderHandle reader = Assert.IsType<
            RecapGridStoreReaderOpenResult.Opened>(
            RecapGridStoreFactory.OpenReader(_root)).Handle;
        var key = new RowWorkKey(spec.Coordinate.RefId,
            spec.Coordinate.TimelineId, spec.Recipe.Digest,
            spec.HistoryRowId);
        RowWork work = Assert.IsType<RecapGridStoreReadResult<RowWork>.Found>(
            reader.Reader.ReadRowWork(key)).Value;
        Assert.Equal(spec.Recipe.Target.Digest, work.ProducerTarget.Digest);
        Assert.Equal(cell.Id, Assert.IsType<
            RecapGridStoreReadResult<RecapCellArtifact>.Found>(
            reader.Reader.ReadCell(cell.Id)).Value.Id);
        Assert.Equal(row.Id, Assert.IsType<
            RecapGridStoreReadResult<RecapRowView>.Found>(
            reader.Reader.ReadView(row.Id)).Value.Id);
        reader.Dispose();
        Assert.IsType<RecapGridStoreUpgradeResult.AlreadyCurrent>(
            RecapGridStoreMaintenance.UpgradeV4(_root, apply: false));
    }

    [Theory]
    [InlineData("temp")]
    [InlineData("backup")]
    public void V4UpgradePreReplaceFailureLeavesActiveV4AndNeverReportsUpgraded(
        string failpoint
    ) {
        CreateV4FullStore(out _, out _, out _);
        byte[] before = File.ReadAllBytes(new StorePaths(_root).DatabasePath);
        Action fail = () => throw new IOException("intentional pre-replace failure");
        var hooks = new StoreUpgradeTestHooks(
            AfterTempVerified: failpoint == "temp" ? fail : null,
            AfterBackupDurable: failpoint == "backup" ? fail : null);

        RecapGridStoreUpgradeResult result = RecapGridStoreMaintenance
            .UpgradeV4ForTest(_root, apply: true, static () => [], hooks);

        Assert.IsType<RecapGridStoreUpgradeResult.Invalid>(result);
        Assert.Equal(4, ReadSchemaVersion(new StorePaths(_root).DatabasePath));
        Assert.Equal(before, File.ReadAllBytes(new StorePaths(_root).DatabasePath));
        if (failpoint == "backup") {
            string backup = Assert.Single(Directory.GetFiles(
                new StorePaths(_root).RootPath, "grid.sqlite.v4-backup-*.sqlite"));
            Assert.Equal(before, File.ReadAllBytes(backup));
        }
    }

    [Theory]
    [InlineData("replace")]
    [InlineData("directory")]
    [InlineData("verify")]
    public void V4UpgradePostReplaceFailureIsIndeterminateWithStrictBackupAndRetryIsAlreadyCurrent(
        string failpoint
    ) {
        CreateV4FullStore(out _, out _, out _);
        byte[] before = File.ReadAllBytes(new StorePaths(_root).DatabasePath);
        Action fail = () => throw new IOException("intentional post-replace failure");
        var hooks = new StoreUpgradeTestHooks(
            AfterReplaceBeforeDirectoryFsync: failpoint == "replace" ? fail : null,
            AfterDirectoryFsyncBeforeVerify: failpoint == "directory" ? fail : null,
            AfterVerify: failpoint == "verify" ? fail : null);

        RecapGridStoreUpgradeResult.CommitIndeterminate result = Assert.IsType<
            RecapGridStoreUpgradeResult.CommitIndeterminate>(
            RecapGridStoreMaintenance.UpgradeV4ForTest(
                _root, apply: true, static () => [], hooks));

        Assert.Equal("inspect-and-verify-active-before-any-restore-or-retry",
            result.NextAction);
        Assert.Equal(before, File.ReadAllBytes(result.BackupPath));
        Assert.Equal(4, result.Backup.Identity.SchemaVersion);
        Assert.Equal(before.Length, result.Backup.Witness.Length);
        Assert.NotNull(result.ObservedActive.Witness);
        Assert.Equal(5, ReadSchemaVersion(new StorePaths(_root).DatabasePath));
        Assert.IsType<RecapGridStoreVerifyResult.Healthy>(
            RecapGridStoreMaintenance.Verify(_root));
        Assert.IsType<RecapGridStoreUpgradeResult.AlreadyCurrent>(
            RecapGridStoreMaintenance.UpgradeV4(_root, apply: true));
        Assert.Single(Directory.GetFiles(new StorePaths(_root).RootPath,
            "grid.sqlite.v4-backup-*.sqlite"));
    }

    [Fact]
    public void V4OrphanPartialIsDiagnosedAndNeverWritesDuringDryRun() {
        CreateV4FullStore(out RowBuildSpec spec, out _, out _);
        string path = new StorePaths(_root).DatabasePath;
        Execute($"""
            INSERT INTO cell_artifact(cell_id,recipe_digest,history_row_id,
                logical_column_id,definition_digest,outcome,content)
            VALUES('{new string('f', 32)}','{spec.Recipe.Digest.Value}',
                '{spec.HistoryRowId.Value}','case.orphan',
                '{StoreFixture.Definition.Value}',0,'orphan');
            UPDATE store_metadata SET cell_count=2;
            """);
        byte[] before = File.ReadAllBytes(path);

        RecapGridStoreUpgradeResult.Invalid rejected = Assert.IsType<
            RecapGridStoreUpgradeResult.Invalid>(
            RecapGridStoreMaintenance.UpgradeV4(_root, apply: false));

        Assert.Equal("GridStoreInvalid", rejected.Code);
        Assert.Contains("orphan partial cell", rejected.Detail,
            StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void V4PartialWithExactExternalWorkProofPreservesCellAndFreezesWork() {
        CreateV4FullStore(out RowBuildSpec previous, out _, out RecapRowView row);
        HistoryRowId partialHistory = new(new string('e', 64));
        var partialCell = new RecapCellArtifact(
            new CellId(new string('f', 32)),
            new CellSlot(previous.Recipe.Digest, partialHistory,
                StoreFixture.Column),
            StoreFixture.Definition,
            RecapCellOutcome.Updated,
            "partial v4 content");
        Execute($"""
            INSERT INTO cell_artifact(cell_id,recipe_digest,history_row_id,
                logical_column_id,definition_digest,outcome,content)
            VALUES('{partialCell.Id.Value}','{partialCell.Slot.RecipeDigest.Value}',
                '{partialHistory.Value}','{StoreFixture.Column.Value}',
                '{StoreFixture.Definition.Value}',0,'{partialCell.Content}');
            UPDATE store_metadata SET cell_count=2;
            """);
        var proof = new RowWork(new RowWorkKey(
                previous.Coordinate.RefId,
                previous.Coordinate.TimelineId,
                previous.Recipe.Digest,
                partialHistory),
            previous.Recipe.Target,
            previous.HistoryRowId,
            row.Id,
            [new RowWorkAssignment(StoreFixture.Column, reusedCellId: null)]);

        Assert.IsType<RecapGridStoreUpgradeResult.Upgraded>(
            RecapGridStoreMaintenance.UpgradeV4(
                _root,
                apply: true,
                () => [proof]));
        using RecapGridStoreReaderHandle reader = Assert.IsType<
            RecapGridStoreReaderOpenResult.Opened>(
            RecapGridStoreFactory.OpenReader(_root)).Handle;
        RowWork restored = Assert.IsType<
            RecapGridStoreReadResult<RowWork>.Found>(
            reader.Reader.ReadRowWork(proof.Key)).Value;
        Assert.Equal(proof.WorkId, restored.WorkId);
        Assert.Equal(proof.ToCanonicalBytes(), restored.ToCanonicalBytes());
        RecapCellArtifact preserved = Assert.IsType<
            RecapGridStoreReadResult<RecapCellArtifact>.Found>(
            reader.Reader.ReadCell(partialCell.Id)).Value;
        Assert.Equal(partialCell.Content, preserved.Content);
        Assert.Equal(restored.WorkId, preserved.Slot.WorkId);
    }

    [Fact]
    public void V4OverlaySharedCellKeepsOriginalProducerAndCreatesReuseWork() {
        CreateV4FullStore(out _, out RecapCellArtifact sourceCell, out _);
        RowBuildSpec overlay = StoreFixture.Spec(
            recipe: StoreFixture.Recipe(bootstrap: 'd'));
        var overlayRow = new RecapRowView(
            new RowResultId(Guid.NewGuid().ToString("N")),
            overlay.Coordinate,
            [new RecapRowViewCell(StoreFixture.Column,
                StoreFixture.Definition, sourceCell.Id)]);
        Execute($"""
            INSERT INTO row_view(row_result_id,ref_id,timeline_id,history_row_id,
                recipe_digest,target_digest,previous_history_row_id,
                previous_row_result_id,bootstrap_completed)
            VALUES('{overlayRow.Id.Value}','{overlayRow.RefId.ToHexString()}',
                '{overlayRow.TimelineId.Value}','{overlayRow.HistoryRowId.Value}',
                '{overlayRow.RecipeDigest.Value}','{overlayRow.TargetDigest.Value}',
                NULL,NULL,1);
            INSERT INTO row_view_member(row_result_id,column_ordinal,
                logical_column_id,definition_digest,cell_id)
            VALUES('{overlayRow.Id.Value}',0,'{StoreFixture.Column.Value}',
                '{StoreFixture.Definition.Value}','{sourceCell.Id.Value}');
            UPDATE store_metadata SET row_view_count=2,row_view_member_count=2;
            """);

        Assert.IsType<RecapGridStoreUpgradeResult.Upgraded>(
            RecapGridStoreMaintenance.UpgradeV4(_root, apply: true));
        using RecapGridStoreReaderHandle reader = Assert.IsType<
            RecapGridStoreReaderOpenResult.Opened>(
            RecapGridStoreFactory.OpenReader(_root)).Handle;
        RowWork overlayWork = Assert.IsType<
            RecapGridStoreReadResult<RowWork>.Found>(reader.Reader.ReadRowWork(
                new RowWorkKey(overlay.Coordinate.RefId,
                    overlay.Coordinate.TimelineId, overlay.Recipe.Digest,
                    overlay.HistoryRowId))).Value;
        Assert.Equal(sourceCell.Id,
            Assert.Single(overlayWork.OrderedAssignments).ReusedCellId);
        RecapCellArtifact preserved = Assert.IsType<
            RecapGridStoreReadResult<RecapCellArtifact>.Found>(
            reader.Reader.ReadCell(sourceCell.Id)).Value;
        Assert.NotEqual(overlayWork.WorkId, preserved.Slot.WorkId);
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
            Assert.DoesNotContain("row_descriptor_digest", names);
            Assert.DoesNotContain("through_row_descriptor_digest", names);
            Assert.Contains("through_history_row_id", names);
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
    public void ConflictingRowWorkCannotPublishOrFillNewCell() {
        Create();
        using RecapGridStoreHandle handle = Open();
        RowBuildSpec spec = StoreFixture.Spec();
        RecapCellArtifact cell = StoreFixture.Put(handle, spec);
        RecapRowView stored = Assert.IsType<RecapGridRowViewPutResult.Inserted>(handle.Writer.PutRowView(spec, [cell])).Winner;
        RowBuildSpec predecessorSpec = StoreFixture.Spec(row: new HistoryRowId(new string('b', 64)));
        RecapCellArtifact predecessorCell = StoreFixture.Put(handle, predecessorSpec);
        RecapRowView predecessor = Assert.IsType<RecapGridRowViewPutResult.Inserted>(
            handle.Writer.PutRowView(predecessorSpec, [predecessorCell])).Winner;
        RowBuildSpec conflicting = StoreFixture.Spec(previous: predecessor);
        Assert.IsType<RecapGridRowWorkPutResult.SelectionConflict>(
            handle.Writer.PutRowWork(conflicting.Work!));
        Assert.Equal("RowWorkMismatch", Assert.IsType<RecapGridCellPutResult.Rejected>(
            handle.Writer.PutCell(conflicting, StoreFixture.Draft(conflicting))).Code);
        Assert.IsType<RecapGridStoreReadResult<RecapCellArtifact>.Missing>(
            handle.Reader.TryReadCell(
                ((RowBuildAssignment.Evaluate)conflicting.OrderedAssignments[0]).Slot));
        Assert.IsType<RecapGridCellPutResult.AlreadyFilled>(
            handle.Writer.PutCell(spec, StoreFixture.Draft(spec)));
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

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void LegacySchemaOpenIsReadOnlyUnsupportedAndExplicitResetNeedsNoLegacyReader(int oldSchema) {
        Directory.CreateDirectory(_root);
        StorePaths paths = new(_root);
        // A valid older store also has the durable lifetime-lock slot. Without
        // it, opening correctly fails before the database version is inspected.
        StoreDurableFiles.EnsureSlots(paths);
        using (var connection = new SqliteConnection($"Data Source={paths.DatabasePath};Pooling=False")) {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            // Deliberately not a decodable old graph: the version boundary must run before graph reads.
            command.CommandText = $"PRAGMA application_id = 1096042322; PRAGMA user_version = {oldSchema}; CREATE TABLE old_state(value TEXT); INSERT INTO old_state VALUES ('preserved');";
            command.ExecuteNonQuery();
        }
        byte[] before = File.ReadAllBytes(paths.DatabasePath);
        Assert.Equal(oldSchema, Assert.IsType<RecapGridStoreOpenResult.UnsupportedSchema>(RecapGridStoreFactory.Open(_root)).SchemaVersion);
        Assert.Equal(oldSchema, Assert.IsType<RecapGridStoreReaderOpenResult.UnsupportedSchema>(RecapGridStoreFactory.OpenReader(_root)).SchemaVersion);
        Assert.Equal(before, File.ReadAllBytes(paths.DatabasePath));
        var witness = Assert.IsType<RecapGridStorePrepareResetResult.Prepared>(RecapGridStoreMaintenance.PrepareReset(_root)).Witness;
        Assert.Equal(5, Assert.IsType<RecapGridStoreResetResult.Reset>(RecapGridStoreMaintenance.Reset(_root, witness)).Identity.SchemaVersion);
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

    private void CreateV4FullStore(out RowBuildSpec spec,
        out RecapCellArtifact cell, out RecapRowView row) {
        Create();
        string path = new StorePaths(_root).DatabasePath;
        File.Delete(path);
        spec = StoreFixture.Spec();
        cell = StoreFixture.Proposed(spec, "v4 content");
        row = new RecapRowView(new RowResultId(Guid.NewGuid().ToString("N")),
            spec.Coordinate, [new RecapRowViewCell(
                StoreFixture.Column, StoreFixture.Definition, cell.Id)]);
        using SqliteConnection connection = new(
            $"Data Source={path};Mode=ReadWriteCreate;Pooling=False");
        connection.Open();
        using (SqliteCommand schema = connection.CreateCommand()) {
            schema.CommandText = ReadV4Schema()
                + $"PRAGMA application_id = {SqliteRecapGridStore.ApplicationId};"
                + "PRAGMA user_version = 4;";
            schema.ExecuteNonQuery();
        }
        using SqliteTransaction transaction = connection.BeginTransaction();
        ExecuteV4("""
            INSERT INTO store_metadata(singleton,schema_version,store_instance_id,
                cell_count,row_view_count,row_view_member_count,fulfilled_view_count)
            VALUES(1,4,'00112233445566778899aabbccddeeff',1,1,1,0);
            """);
        ExecuteV4("""
            INSERT INTO cell_artifact(cell_id,recipe_digest,history_row_id,
                logical_column_id,definition_digest,outcome,content)
            VALUES($id,$recipe,$history,$column,$definition,0,$content);
            """, ("$id", cell.Id.Value),
            ("$recipe", cell.Slot.RecipeDigest.Value),
            ("$history", cell.Slot.HistoryRowId.Value),
            ("$column", cell.Slot.LogicalColumnId.Value),
            ("$definition", cell.DefinitionDigest.Value),
            ("$content", cell.Content));
        ExecuteV4("""
            INSERT INTO row_view(row_result_id,ref_id,timeline_id,history_row_id,
                recipe_digest,target_digest,previous_history_row_id,
                previous_row_result_id,bootstrap_completed)
            VALUES($id,$ref,$timeline,$history,$recipe,$target,NULL,NULL,1);
            """, ("$id", row.Id.Value),
            ("$ref", row.RefId.ToHexString()),
            ("$timeline", row.TimelineId.Value),
            ("$history", row.HistoryRowId.Value),
            ("$recipe", row.RecipeDigest.Value),
            ("$target", row.TargetDigest.Value));
        ExecuteV4("""
            INSERT INTO row_view_member(row_result_id,column_ordinal,
                logical_column_id,definition_digest,cell_id)
            VALUES($row,0,$column,$definition,$cell);
            """, ("$row", row.Id.Value),
            ("$column", StoreFixture.Column.Value),
            ("$definition", StoreFixture.Definition.Value),
            ("$cell", cell.Id.Value));
        transaction.Commit();

        void ExecuteV4(string sql, params (string Name, object Value)[] values) {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            foreach ((string name, object value) in values) {
                command.Parameters.AddWithValue(name, value);
            }
            _ = command.ExecuteNonQuery();
        }
    }

    private static string ReadV4Schema() {
        using Stream stream = typeof(SqliteRecapGridStore).Assembly
            .GetManifestResourceStream(
                "Atelia.SessionJournal.RecapGrid.Store.SchemaV4.sql")
            ?? throw new InvalidOperationException("V4 Store schema is absent.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
    private static int ReadSchemaVersion(string path) {
        using var connection = new SqliteConnection(
            $"Data Source={path};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar(),
            System.Globalization.CultureInfo.InvariantCulture);
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
                ) VALUES (2, 4, '00112233445566778899aabbccddeeff',
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
