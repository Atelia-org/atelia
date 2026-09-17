using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid.Store;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Atelia.SessionJournal.RecapGrid.Store.Tests;

public sealed class StoreRestoreTests : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "atelia-recap-grid-restore", Guid.NewGuid().ToString("N"));

    [Fact]
    public void FullV5RestoresExactV4AndLeavesExternalAuthoritiesUntouched() {
        CreateV4(out _, out _, out _);
        string journal = WriteExternal("journal/raw.bin", "raw authority");
        string timeline = WriteExternal("derived/history-timeline/v2/fixed.bin",
            "timeline authority");
        string control = WriteExternal("control/recap-grid/fixed.bin",
            "control authority");
        var external = new Dictionary<string, byte[]> {
            [journal] = File.ReadAllBytes(journal),
            [timeline] = File.ReadAllBytes(timeline),
            [control] = File.ReadAllBytes(control)
        };
        RecapGridStoreUpgradeResult.Upgraded upgraded = Upgrade();
        byte[] backupBytes = File.ReadAllBytes(upgraded.BackupPath);

        RecapGridStorePrepareRestoreResult.Prepared prepared = Assert.IsType<
            RecapGridStorePrepareRestoreResult.Prepared>(
            RecapGridStoreMaintenance.PrepareRestoreV4(
                _root, upgraded.BackupPath));
        RecapGridStoreRestoreResult.Restored restored = Assert.IsType<
            RecapGridStoreRestoreResult.Restored>(
            RecapGridStoreMaintenance.RestoreV4(
                _root, upgraded.BackupPath, prepared.Active.Witness,
                prepared.Backup.Witness));

        Assert.Equal(4, restored.Active.Identity.SchemaVersion);
        Assert.Equal(backupBytes, File.ReadAllBytes(DatabasePath));
        Assert.IsType<RecapGridStoreReaderOpenResult.UnsupportedSchema>(
            RecapGridStoreFactory.OpenReader(_root));
        Assert.IsType<RecapGridStorePrepareRestoreResult.AlreadyRestored>(
            RecapGridStoreMaintenance.PrepareRestoreV4(
                _root, upgraded.BackupPath));
        Assert.IsType<RecapGridStoreRestoreResult.AlreadyRestored>(
            RecapGridStoreMaintenance.RestoreV4(
                _root, upgraded.BackupPath, prepared.Active.Witness,
                prepared.Backup.Witness));
        foreach ((string path, byte[] bytes) in external) {
            Assert.Equal(bytes, File.ReadAllBytes(path));
        }
    }

    [Fact]
    public void RestoreRefusesStaleActiveAndChangedBackupWithoutWritingActive() {
        CreateV4(out _, out _, out _);
        RecapGridStoreUpgradeResult.Upgraded upgraded = Upgrade();
        RecapGridStorePrepareRestoreResult.Prepared prepared = Prepare(upgraded);
        byte[] activeBefore = File.ReadAllBytes(DatabasePath);

        var wrongActive = new RecapGridStorePhysicalWitness(
            prepared.Active.Witness.Length, new string('0', 64));
        Assert.IsType<RecapGridStoreRestoreResult.ActiveChanged>(
            RecapGridStoreMaintenance.RestoreV4(
                _root, upgraded.BackupPath, wrongActive,
                prepared.Backup.Witness));
        Assert.Equal(activeBefore, File.ReadAllBytes(DatabasePath));

        Execute(upgraded.BackupPath,
            "UPDATE cell_artifact SET content='changed but strict V4';");
        Assert.IsType<RecapGridStoreRestoreResult.BackupChanged>(
            RecapGridStoreMaintenance.RestoreV4(
                _root, upgraded.BackupPath, prepared.Active.Witness,
                prepared.Backup.Witness));
        Assert.Equal(activeBefore, File.ReadAllBytes(DatabasePath));
    }

    [Fact]
    public void RestoreRechecksActiveAfterBackupVerification() {
        CreateV4(out _, out _, out _);
        RecapGridStoreUpgradeResult.Upgraded upgraded = Upgrade();
        RecapGridStorePrepareRestoreResult.Prepared prepared = Prepare(upgraded);
        var hooks = new StoreRestoreTestHooks(
            AfterBackupVerifiedBeforeActiveRecheck: () =>
            Execute(DatabasePath,
                "UPDATE cell_artifact SET content='changed V5 before replace';"));

        Assert.IsType<RecapGridStoreRestoreResult.ActiveChanged>(
            RecapGridStoreMaintenance.RestoreV4ForTest(
                _root, upgraded.BackupPath, prepared.Active.Witness,
                prepared.Backup.Witness, static () => [], hooks));
        Assert.Equal(5, ReadSchema(DatabasePath));
        Assert.Contains("changed V5 before replace", File.ReadAllText(
            DatabasePath));
        Assert.Empty(Directory.GetFiles(StoreRoot,
            ".grid.restore-v4.*.sqlite"));
    }

    [Fact]
    public void RestoreChecksPhysicalWitnessesBeforeResolvingPartialProofs() {
        CreateV4(out _, out _, out _);
        RecapGridStoreUpgradeResult.Upgraded upgraded = Upgrade();
        RecapGridStorePrepareRestoreResult.Prepared prepared = Prepare(upgraded);
        int resolverCalls = 0;
        Func<RecapGridStoreV4PartialProofFacts, IReadOnlyList<RowWork>>
            resolver = _ => {
            resolverCalls++;
            throw new InvalidOperationException(
                "A stale physical request must not read proof authorities.");
        };
        var wrongBackup = new RecapGridStorePhysicalWitness(
            prepared.Backup.Witness.Length, new string('0', 64));

        Assert.IsType<RecapGridStoreRestoreResult.BackupChanged>(
            RecapGridStoreMaintenance.RestoreV4(
                _root, upgraded.BackupPath, prepared.Active.Witness,
                wrongBackup, resolver));
        Assert.Equal(0, resolverCalls);

        var wrongActive = new RecapGridStorePhysicalWitness(
            prepared.Active.Witness.Length, new string('0', 64));
        Assert.IsType<RecapGridStoreRestoreResult.ActiveChanged>(
            RecapGridStoreMaintenance.RestoreV4(
                _root, upgraded.BackupPath, wrongActive,
                prepared.Backup.Witness, resolver));
        Assert.Equal(0, resolverCalls);
    }

    [Fact]
    public void RestoreRechecksBackupAfterTempVerification() {
        CreateV4(out _, out _, out _);
        RecapGridStoreUpgradeResult.Upgraded upgraded = Upgrade();
        RecapGridStorePrepareRestoreResult.Prepared prepared = Prepare(upgraded);
        byte[] activeBefore = File.ReadAllBytes(DatabasePath);
        var hooks = new StoreRestoreTestHooks(AfterTempVerified: () =>
            Execute(upgraded.BackupPath,
                "UPDATE cell_artifact SET content='changed V4 before replace';"));

        Assert.IsType<RecapGridStoreRestoreResult.BackupChanged>(
            RecapGridStoreMaintenance.RestoreV4ForTest(
                _root, upgraded.BackupPath, prepared.Active.Witness,
                prepared.Backup.Witness, static () => [], hooks));
        Assert.Equal(activeBefore, File.ReadAllBytes(DatabasePath));
        Assert.Equal(5, ReadSchema(DatabasePath));
        Assert.Empty(Directory.GetFiles(StoreRoot,
            ".grid.restore-v4.*.sqlite"));
    }

    [Theory]
    [InlineData("replace")]
    [InlineData("directory")]
    [InlineData("verify")]
    public void PostReplaceFailureIsCommitIndeterminate(string failpoint) {
        CreateV4(out _, out _, out _);
        RecapGridStoreUpgradeResult.Upgraded upgraded = Upgrade();
        RecapGridStorePrepareRestoreResult.Prepared prepared = Prepare(upgraded);
        Action fail = () => throw new IOException("restore interruption");
        var hooks = new StoreRestoreTestHooks(
            AfterReplaceBeforeDirectoryFsync:
                failpoint == "replace" ? fail : null,
            AfterDirectoryFsyncBeforeVerify:
                failpoint == "directory" ? fail : null,
            AfterVerify: failpoint == "verify" ? fail : null);

        RecapGridStoreRestoreResult.CommitIndeterminate result = Assert.IsType<
            RecapGridStoreRestoreResult.CommitIndeterminate>(
            RecapGridStoreMaintenance.RestoreV4ForTest(
                _root, upgraded.BackupPath, prepared.Active.Witness,
                prepared.Backup.Witness, static () => [], hooks));

        Assert.Equal("inspect-active-before-any-restore-or-upgrade",
            result.NextAction);
        Assert.Equal(4, result.ObservedActive.SchemaVersion);
        Assert.Equal(prepared.Backup.Witness,
            result.ObservedActive.Witness);
        Assert.IsType<RecapGridStoreRestoreResult.AlreadyRestored>(
            RecapGridStoreMaintenance.RestoreV4(
                _root, upgraded.BackupPath, prepared.Active.Witness,
                prepared.Backup.Witness));
    }

    [Fact]
    public void RestoreRejectsNonExactBackupPathsLinksAndSidecars() {
        CreateV4(out _, out _, out _);
        RecapGridStoreUpgradeResult.Upgraded upgraded = Upgrade();
        string outside = Path.Combine(_root, "outside.sqlite");
        File.Copy(upgraded.BackupPath, outside);
        AssertInvalid(outside);
        AssertInvalid(DatabasePath);
        string loose = Path.Combine(StoreRoot,
            "grid.sqlite.v4-backup-not-an-upgrade.sqlite");
        File.Copy(upgraded.BackupPath, loose);
        AssertInvalid(loose);

        string linked = Path.Combine(StoreRoot,
            "grid.sqlite.v4-backup-20260917T010203004Z-"
            + Guid.NewGuid().ToString("N") + ".sqlite");
        File.CreateSymbolicLink(linked, upgraded.BackupPath);
        AssertInvalid(linked);

        File.WriteAllBytes(upgraded.BackupPath + "-wal", [1]);
        Assert.IsType<
            RecapGridStorePrepareRestoreResult.OfflineCleanupRequired>(
            RecapGridStoreMaintenance.PrepareRestoreV4(
                _root, upgraded.BackupPath));

        void AssertInvalid(string path) => Assert.IsType<
            RecapGridStorePrepareRestoreResult.Invalid>(
            RecapGridStoreMaintenance.PrepareRestoreV4(_root, path));
    }

    [Fact]
    public void PartialV4BackupRestoresOnlyWithCallerOwnedExactProof() {
        CreateV4(out RowBuildSpec previous, out _, out RecapRowView prior);
        HistoryRowId partialHistory = new(new string('e', 64));
        var partial = new RecapCellArtifact(
            new CellId(new string('f', 32)),
            new CellSlot(previous.Recipe.Digest, partialHistory,
                StoreFixture.Column),
            StoreFixture.Definition, RecapCellOutcome.Updated,
            "partial restore content");
        Execute(DatabasePath, $"""
            INSERT INTO cell_artifact(cell_id,recipe_digest,history_row_id,
                logical_column_id,definition_digest,outcome,content)
            VALUES('{partial.Id.Value}','{partial.Slot.RecipeDigest.Value}',
                '{partialHistory.Value}','{StoreFixture.Column.Value}',
                '{StoreFixture.Definition.Value}',0,'{partial.Content}');
            UPDATE store_metadata SET cell_count=2;
            """);
        var proof = new RowWork(new RowWorkKey(previous.Coordinate.RefId,
                previous.Coordinate.TimelineId, previous.Recipe.Digest,
                partialHistory), previous.Recipe.Target,
            previous.HistoryRowId, prior.Id,
            [new RowWorkAssignment(StoreFixture.Column, null)]);
        Func<RecapGridStoreV4PartialProofFacts, IReadOnlyList<RowWork>>
            resolver = facts => {
                Assert.Equal(partial.Id.Value,
                    Assert.Single(facts.PartialCells).CellId);
                return [proof];
            };
        RecapGridStoreUpgradeResult.Upgraded upgraded = Assert.IsType<
            RecapGridStoreUpgradeResult.Upgraded>(
            RecapGridStoreMaintenance.UpgradeV4(
                _root, apply: true, resolver));
        RecapGridStorePrepareRestoreResult.Prepared prepared = Assert.IsType<
            RecapGridStorePrepareRestoreResult.Prepared>(
            RecapGridStoreMaintenance.PrepareRestoreV4(
                _root, upgraded.BackupPath, resolver));

        Assert.IsType<RecapGridStoreRestoreResult.Restored>(
            RecapGridStoreMaintenance.RestoreV4(
                _root, upgraded.BackupPath, prepared.Active.Witness,
                prepared.Backup.Witness, resolver));
        Assert.Equal(4, ReadSchema(DatabasePath));
        Assert.Contains("partial restore content", File.ReadAllText(
            DatabasePath));
    }

    private RecapGridStoreUpgradeResult.Upgraded Upgrade() => Assert.IsType<
        RecapGridStoreUpgradeResult.Upgraded>(
        RecapGridStoreMaintenance.UpgradeV4(_root, apply: true));

    private RecapGridStorePrepareRestoreResult.Prepared Prepare(
        RecapGridStoreUpgradeResult.Upgraded upgraded
    ) => Assert.IsType<RecapGridStorePrepareRestoreResult.Prepared>(
        RecapGridStoreMaintenance.PrepareRestoreV4(
            _root, upgraded.BackupPath));

    private string StoreRoot => Path.Combine(_root,
        "derived", "recap-grid", "v1");
    private string DatabasePath => Path.Combine(StoreRoot, "grid.sqlite");

    private string WriteExternal(string relative, string content) {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private void CreateV4(
        out RowBuildSpec spec,
        out RecapCellArtifact cell,
        out RecapRowView row
    ) {
        Directory.CreateDirectory(_root);
        Assert.IsType<RecapGridStoreCreateResult.Created>(
            RecapGridStoreFactory.Create(_root));
        File.Delete(DatabasePath);
        spec = StoreFixture.Spec();
        cell = StoreFixture.Proposed(spec, "v4 content");
        row = new RecapRowView(new RowResultId(Guid.NewGuid().ToString("N")),
            spec.Coordinate, [new RecapRowViewCell(
                StoreFixture.Column, StoreFixture.Definition, cell.Id)]);
        using var connection = new SqliteConnection(
            $"Data Source={DatabasePath};Mode=ReadWriteCreate;Pooling=False");
        connection.Open();
        using (SqliteCommand schema = connection.CreateCommand()) {
            schema.CommandText = ReadV4Schema()
                + $"PRAGMA application_id={SqliteRecapGridStore.ApplicationId};"
                + "PRAGMA user_version=4;";
            schema.ExecuteNonQuery();
        }
        using SqliteTransaction transaction = connection.BeginTransaction();
        Execute("""
            INSERT INTO store_metadata(singleton,schema_version,
                store_instance_id,cell_count,row_view_count,
                row_view_member_count,fulfilled_view_count)
            VALUES(1,4,'00112233445566778899aabbccddeeff',1,1,1,0);
            """);
        Execute("""
            INSERT INTO cell_artifact(cell_id,recipe_digest,history_row_id,
                logical_column_id,definition_digest,outcome,content)
            VALUES($cell,$recipe,$history,$column,$definition,0,$content);
            """, ("$cell", cell.Id.Value),
            ("$recipe", cell.Slot.RecipeDigest.Value),
            ("$history", cell.Slot.HistoryRowId.Value),
            ("$column", StoreFixture.Column.Value),
            ("$definition", StoreFixture.Definition.Value),
            ("$content", cell.Content));
        Execute("""
            INSERT INTO row_view(row_result_id,ref_id,timeline_id,
                history_row_id,recipe_digest,target_digest,
                previous_history_row_id,previous_row_result_id,
                bootstrap_completed)
            VALUES($row,$ref,$timeline,$history,$recipe,$target,NULL,NULL,1);
            """, ("$row", row.Id.Value),
            ("$ref", row.RefId.ToHexString()),
            ("$timeline", row.TimelineId.Value),
            ("$history", row.HistoryRowId.Value),
            ("$recipe", row.RecipeDigest.Value),
            ("$target", row.TargetDigest.Value));
        Execute("""
            INSERT INTO row_view_member(row_result_id,column_ordinal,
                logical_column_id,definition_digest,cell_id)
            VALUES($row,0,$column,$definition,$cell);
            """, ("$row", row.Id.Value),
            ("$column", StoreFixture.Column.Value),
            ("$definition", StoreFixture.Definition.Value),
            ("$cell", cell.Id.Value));
        transaction.Commit();

        void Execute(string sql,
            params (string Name, object Value)[] values) {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            foreach ((string name, object value) in values) {
                command.Parameters.AddWithValue(name, value);
            }
            command.ExecuteNonQuery();
        }
    }

    private static string ReadV4Schema() {
        using Stream stream = typeof(SqliteRecapGridStore).Assembly
            .GetManifestResourceStream(
                "Atelia.SessionJournal.RecapGrid.Store.SchemaV4.sql")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static void Execute(string database, string sql) {
        using var connection = new SqliteConnection(
            $"Data Source={database};Mode=ReadWrite;Pooling=False");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static int ReadSchema(string database) {
        using var connection = new SqliteConnection(
            $"Data Source={database};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar(),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    public void Dispose() {
        if (Directory.Exists(_root)) {
            Directory.Delete(_root, recursive: true);
        }
    }
}
