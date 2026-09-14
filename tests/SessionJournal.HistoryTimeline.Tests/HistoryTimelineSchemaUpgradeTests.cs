using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.SessionJournal;
using Microsoft.Data.Sqlite;

namespace Atelia.SessionJournal.HistoryTimeline.Tests;

public sealed class HistoryTimelineSchemaUpgradeTests : IDisposable {
    private readonly List<string> _paths = [];
    private readonly O200kBaseHistoryUnitLoadEstimator _estimator = new();

    [Fact]
    public void CurrentRuntimeRejectsFixedOldSchemaWithoutChangingSource() {
        LegacyFixture fixture = Extract();
        byte[] before = File.ReadAllBytes(fixture.DatabasePath);
        using (SessionJournalEngine journal = SessionJournalEngine.Open(fixture.Path)) {
            Assert.Equal(2, Assert.IsType<HistoryTimelineOpenResult.UnsupportedSchema>(
                HistoryTimelineFactory.Open(journal.ReadView, _estimator)).SchemaVersion);
        }
        Assert.Equal(2, Assert.IsType<HistoryTimelineReaderOpenResult.UnsupportedSchema>(
            HistoryTimelineMaintenance.OpenReader(fixture.Path, fixture.RefId)).SchemaVersion);
        Assert.Equal(before, File.ReadAllBytes(fixture.DatabasePath));
    }

    [Fact]
    public void UpgradePreservesAllRowsAndDurableAuthoritiesAndRepeatsWithoutWrites() {
        LegacyFixture fixture = Extract();
        Dictionary<string, string[]> before = SnapshotTables(fixture.DatabasePath);
        Dictionary<string, byte[]> outside = SnapshotOutsideDatabase(fixture);
        Assert.True(before["rows"].Length > before["current_selected_path"].Length);
        Assert.NotEmpty(before["current_selected_path"]);
        var upgraded = Assert.IsType<HistoryTimelineUpgradeResult.Upgraded>(
            HistoryTimelineMaintenance.UpgradeSchemaV2(fixture.Path, fixture.RefId, fixture.TimelineId));
        Assert.Equal(fixture.Head, upgraded.Head);
        Assert.Equal(3, Version(fixture.DatabasePath));
        AssertSnapshot(before, SnapshotTables(fixture.DatabasePath));
        AssertOutsideUnchanged(outside, SnapshotOutsideDatabase(fixture));
        using (HistoryTimelineReaderHandle reader = Assert.IsType<HistoryTimelineReaderOpenResult.Opened>(
            HistoryTimelineMaintenance.OpenReader(fixture.Path, fixture.RefId)).Handle) {
            Assert.Equal(fixture.Head, Assert.IsType<HistoryTimelineSnapshotResult.Available>(reader.Reader.ReadSnapshot()).Head);
            foreach (HistoryRowId row in fixture.AllRowIds) {
                var ledger = new SqliteHistoryTimelineLedger(fixture.DatabasePath, fixture.TimelineId,
                    fixture.RefId, HistoryTimelineStorageLimits.Production);
                HistorySegmentDescriptor actual = Assert.IsType<HistoryTimelineStoreReadResult<HistorySegmentDescriptor>.Found>(ledger.ReadRow(row)).Value;
                Assert.Equal(row, actual.RowId);
                using JsonDocument json = JsonDocument.Parse(actual.ToCanonicalBytes());
                Assert.Equal(2, json.RootElement.GetProperty("v").GetInt32());
                Assert.False(json.RootElement.TryGetProperty("descriptorDigest", out _));
            }
            Assert.IsNotType<HistoryTimelineReaderRowResult.Selected>(reader.Reader.ReadSelectedRow(fixture.Head, fixture.DeselectedRowId));
        }
        Assert.IsType<HistoryTimelineInspectResult.Available>(HistoryTimelineMaintenance.Verify(fixture.Path, fixture.RefId));
        byte[] afterFirst = File.ReadAllBytes(fixture.DatabasePath);
        var repeated = Assert.IsType<HistoryTimelineUpgradeResult.AlreadyCurrent>(
            HistoryTimelineMaintenance.UpgradeSchemaV2(fixture.Path, fixture.RefId, fixture.TimelineId));
        Assert.Equal(fixture.Head, repeated.Head);
        Assert.Equal(afterFirst, File.ReadAllBytes(fixture.DatabasePath));
    }

    [Fact]
    public void UpgradedTimelineColdOpenCanSealAndReconcileWithoutLosingRetainedRows() {
        LegacyFixture fixture = Extract();
        Assert.IsType<HistoryTimelineUpgradeResult.Upgraded>(HistoryTimelineMaintenance.UpgradeSchemaV2(
            fixture.Path, fixture.RefId, fixture.TimelineId));
        using SessionJournalEngine journal = SessionJournalEngine.Open(fixture.Path);
        EventAddress? oldRawHead = journal.ReadView.ReadCurrentHead();
        journal.AppendObservation("after offline Timeline upgrade");
        journal.AppendImportedAgentAction(new ActionMessage([new ActionBlock.Text("new turn")]),
            new CompletionDescriptor("import", "v1", "model-A"));
        journal.AppendObservation("reserve after upgrade");
        journal.AppendImportedAgentAction(new ActionMessage([new ActionBlock.Text("reserve turn")]),
            new CompletionDescriptor("import", "v1", "model-A"));
        EventAddress newRawHead = journal.ReadView.ReadCurrentHead()!.Value;
        TimelineHeadRef committed;
        using (HistoryTimelineHandle handle = Assert.IsType<HistoryTimelineOpenResult.Opened>(
            HistoryTimelineFactory.Open(journal.ReadView, _estimator)).Handle) {
            TimelineHeadRef head = Assert.IsType<HistoryTimelineSnapshotResult.Available>(handle.Reader.ReadSnapshot()).Head;
            committed = head;
            int appendedRows = 0;
            for (int attempt = 0; attempt < 8; attempt++) {
                OnlineSelectedRawCapture capture = Assert.IsType<OnlineSelectedRawCaptureResult.Captured>(
                    handle.Coordinator.CaptureOnline(committed, journal.ReadView)).Capture;
                if (handle.Coordinator.PlanNextRow(committed, capture) is not HistoryTimelinePlanResult.Selected planned) break;
                committed = Assert.IsType<HistoryTimelineCommitResult.Committed>(handle.Coordinator.CommitRow(planned.Candidate)).Head;
                appendedRows++;
            }
            Assert.True(appendedRows >= 2, "Seal past the fixture's pre-existing recent reserve into the newly appended history.");
            Assert.True(committed.Generation > fixture.Head.Generation);
        }
        Assert.True(journal.MoveCurrentHeadForTest(newRawHead, oldRawHead));
        using (HistoryTimelineHandle reopened = Assert.IsType<HistoryTimelineOpenResult.Opened>(
            HistoryTimelineFactory.Open(journal.ReadView, _estimator)).Handle) {
            var reconciled = Assert.IsType<HistoryTimelineReconcileResult.Reconciled>(
                reopened.Coordinator.ReconcileSelectedPath(committed, journal.ReadView));
            Assert.True(reconciled.Head.Generation > committed.Generation);
        }
        var ledger = new SqliteHistoryTimelineLedger(fixture.DatabasePath, fixture.TimelineId, fixture.RefId,
            HistoryTimelineStorageLimits.Production);
        foreach (HistoryRowId row in fixture.AllRowIds) Assert.IsType<HistoryTimelineStoreReadResult<HistorySegmentDescriptor>.Found>(ledger.ReadRow(row));
        Assert.IsType<HistoryTimelineInspectResult.Available>(HistoryTimelineMaintenance.Verify(fixture.Path, fixture.RefId));
    }

    [Fact]
    public void ActiveHandleMakesUpgradeBusyWithoutChangingDatabase() {
        LegacyFixture fixture = Extract();
        Assert.IsType<HistoryTimelineUpgradeResult.Upgraded>(HistoryTimelineMaintenance.UpgradeSchemaV2(
            fixture.Path, fixture.RefId, fixture.TimelineId));
        using HistoryTimelineReaderHandle active = Assert.IsType<HistoryTimelineReaderOpenResult.Opened>(
            HistoryTimelineMaintenance.OpenReader(fixture.Path, fixture.RefId)).Handle;
        byte[] before = File.ReadAllBytes(fixture.DatabasePath);
        Assert.IsType<HistoryTimelineUpgradeResult.Busy>(HistoryTimelineMaintenance.UpgradeSchemaV2(
            fixture.Path, fixture.RefId, fixture.TimelineId));
        Assert.Equal(before, File.ReadAllBytes(fixture.DatabasePath));
    }

    [Fact]
    public void ExplicitScopeCanUpgradeRetainedTimelineWithoutChangingActiveLocator() {
        LegacyFixture fixture = Extract();
        var paths = new HistoryTimelinePaths(fixture.Path, fixture.RefId);
        // Only this disposable copy changes its active scope. The retained schema-2 database remains original.
        File.Delete(paths.LocatorPath);
        HistoryTimelineCreateResult.Created current;
        using (SessionJournalEngine journal = SessionJournalEngine.Open(fixture.Path)) {
            current = Assert.IsType<HistoryTimelineCreateResult.Created>(HistoryTimelineFactory.Create(
                journal.ReadView, new HistoryTimelineInitialPolicySpec(
                    HistoryPartitionAlgorithms.FirstReplaySafeBoundaryAtTargetV1, O200kBaseHistoryUnitLoadEstimator.EstimatorId,
                    new HistoryLoadUnit(1), 8, 1024 * 1024), _estimator));
        }
        Assert.NotEqual(fixture.TimelineId, current.Locator.ActiveTimelineId);
        byte[] locator = File.ReadAllBytes(paths.LocatorPath);
        byte[] activeDatabase = File.ReadAllBytes(paths.TimelineDatabasePath(current.Locator.ActiveTimelineId));
        Dictionary<string, string[]> retained = SnapshotTables(fixture.DatabasePath);
        Assert.Equal(fixture.Head, Assert.IsType<HistoryTimelineUpgradeResult.Upgraded>(HistoryTimelineMaintenance.UpgradeSchemaV2(
            fixture.Path, fixture.RefId, fixture.TimelineId)).Head);
        AssertSnapshot(retained, SnapshotTables(fixture.DatabasePath));
        Assert.Equal(locator, File.ReadAllBytes(paths.LocatorPath));
        Assert.Equal(activeDatabase, File.ReadAllBytes(paths.TimelineDatabasePath(current.Locator.ActiveTimelineId)));
    }

    [Theory]
    [InlineData("leaf")]
    [InlineData("internal-node")]
    public void RawCommitmentByteDamageWithCleanGuardIsRejectedWithoutPublication(string target) {
        LegacyFixture fixture = Extract();
        string commitment;
        using (SqliteConnection connection = OpenSqlite(fixture.DatabasePath)) {
            using SqliteCommand read = connection.CreateCommand();
            read.CommandText = target == "leaf"
                ? "SELECT leaf_digest FROM current_selected_path WHERE ordinal=0;"
                : "SELECT digest FROM current_selected_path_merkle WHERE level=1 AND node_index=0;";
            commitment = Assert.IsType<string>(read.ExecuteScalar());
        }
        byte[] bytes = File.ReadAllBytes(fixture.DatabasePath);
        byte[] encoded = System.Text.Encoding.ASCII.GetBytes(commitment);
        int position = bytes.AsSpan().IndexOf(encoded);
        Assert.True(position >= 0);
        // A physical data-byte fault does not run SQL triggers. The SQLite page
        // shape, string length, head and clean mutation guard remain intact.
        bytes[position] = bytes[position] == (byte)'0' ? (byte)'1' : (byte)'0';
        File.WriteAllBytes(fixture.DatabasePath, bytes);
        using (SqliteConnection connection = OpenSqlite(fixture.DatabasePath)) {
            using SqliteCommand read = connection.CreateCommand();
            read.CommandText = "PRAGMA integrity_check;";
            Assert.Equal("ok", read.ExecuteScalar());
            read.CommandText = "SELECT dirty FROM current_selected_path_guard WHERE singleton=1;";
            Assert.Equal(0L, read.ExecuteScalar());
            read.CommandText = "SELECT head_canonical FROM store_metadata WHERE singleton=1;";
            Assert.Equal(fixture.Head.ToCanonicalBytes(), Assert.IsType<byte[]>(read.ExecuteScalar()));
        }
        string locatorPath = new HistoryTimelinePaths(fixture.Path, fixture.RefId).LocatorPath;
        byte[] locator = File.ReadAllBytes(locatorPath);
        Assert.IsType<HistoryTimelineUpgradeResult.Invalid>(HistoryTimelineMaintenance.UpgradeSchemaV2(
            fixture.Path, fixture.RefId, fixture.TimelineId));
        Assert.Equal(bytes, File.ReadAllBytes(fixture.DatabasePath));
        Assert.Equal(locator, File.ReadAllBytes(locatorPath));
        Assert.Equal(2, Version(fixture.DatabasePath));
    }

    [Theory]
    [InlineData("descriptor")]
    [InlineData("row-locator")]
    [InlineData("metadata-scope")]
    [InlineData("schema")]
    public void InvalidLegacySourceIsNeverPublished(string mutation) {
        LegacyFixture fixture = Extract();
        Execute(fixture.DatabasePath, mutation switch {
            "descriptor" => "UPDATE rows SET canonical = X'00' WHERE row_id = (SELECT row_id FROM rows LIMIT 1);",
            "row-locator" => "PRAGMA foreign_keys=OFF; UPDATE rows SET descriptor_digest = '0000000000000000000000000000000000000000000000000000000000000000' WHERE row_id = (SELECT row_id FROM rows LIMIT 1);",
            "metadata-scope" => "UPDATE store_metadata SET ref_id = '0000000000000002';",
            "schema" => "CREATE TABLE unexpected(value INTEGER);",
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        });
        byte[] before = File.ReadAllBytes(fixture.DatabasePath);
        Assert.IsType<HistoryTimelineUpgradeResult.Invalid>(HistoryTimelineMaintenance.UpgradeSchemaV2(
            fixture.Path, fixture.RefId, fixture.TimelineId));
        Assert.Equal(2, Version(fixture.DatabasePath));
        Assert.Equal(before, File.ReadAllBytes(fixture.DatabasePath));
    }

    [Fact]
    public void UnknownSchemaAndAbsentScopeAreExplicitAndDoNotWrite() {
        LegacyFixture fixture = Extract();
        Execute(fixture.DatabasePath, "PRAGMA user_version = 99;");
        byte[] before = File.ReadAllBytes(fixture.DatabasePath);
        Assert.Equal(99, Assert.IsType<HistoryTimelineUpgradeResult.UnsupportedSchema>(
            HistoryTimelineMaintenance.UpgradeSchemaV2(fixture.Path, fixture.RefId, fixture.TimelineId)).SchemaVersion);
        Assert.Equal(before, File.ReadAllBytes(fixture.DatabasePath));
        Assert.IsType<HistoryTimelineUpgradeResult.Absent>(HistoryTimelineMaintenance.UpgradeSchemaV2(
            fixture.Path, fixture.RefId, new TimelineId(new string('f', 32))));
    }

    [Fact]
    public void UpgradePageBudgetFailureDoesNotPublish() {
        LegacyFixture fixture = Extract();
        byte[] before = File.ReadAllBytes(fixture.DatabasePath);
        var limited = Assert.IsType<HistoryTimelineUpgradeResult.LimitExceeded>(
            HistoryTimelineMaintenance.UpgradeSchemaV2Core(fixture.Path, fixture.RefId, fixture.TimelineId,
                HistoryTimelineStorageLimits.Production with { MaximumPathPageUtf8Bytes = 1 },
                HistoryTimelinePersistenceTestHooks.None));
        Assert.Equal("UpgradePageBytes", limited.Limit);
        Assert.Equal(before, File.ReadAllBytes(fixture.DatabasePath));
        Assert.Equal(2, Version(fixture.DatabasePath));
    }

    [Theory]
    [InlineData("upgrade-source-validated", false)]
    [InlineData("upgrade-before-replace", false)]
    [InlineData("upgrade-after-replace", true)]
    public async Task ProcessDeathLeavesCompleteSourceOrCompleteTarget(string failpoint, bool published) {
        LegacyFixture fixture = Extract();
        byte[] source = File.ReadAllBytes(fixture.DatabasePath);
        Dictionary<string, string[]> graph = SnapshotTables(fixture.DatabasePath);
        await HistoryTimelineCrashRecoveryTests.RunCrashHarnessAsync(fixture.Path, "upgrade", failpoint);
        Assert.Equal(published ? 3 : 2, Version(fixture.DatabasePath));
        if (!published) Assert.Equal(source, File.ReadAllBytes(fixture.DatabasePath));
        AssertSnapshot(graph, SnapshotTables(fixture.DatabasePath));
        HistoryTimelineUpgradeResult retry = HistoryTimelineMaintenance.UpgradeSchemaV2(fixture.Path, fixture.RefId, fixture.TimelineId);
        if (published) Assert.Equal(fixture.Head, Assert.IsType<HistoryTimelineUpgradeResult.AlreadyCurrent>(retry).Head);
        else Assert.Equal(fixture.Head, Assert.IsType<HistoryTimelineUpgradeResult.Upgraded>(retry).Head);
        AssertSnapshot(graph, SnapshotTables(fixture.DatabasePath));
        Assert.IsType<HistoryTimelineInspectResult.Available>(HistoryTimelineMaintenance.Verify(fixture.Path, fixture.RefId));
    }

    [Theory]
    [InlineData("source-validated", false)]
    [InlineData("before-replace", false)]
    [InlineData("after-replace", true)]
    public void PublicationFailureLeavesWholeOldOrNewDatabase(string failpoint, bool published) {
        LegacyFixture fixture = Extract();
        byte[] before = File.ReadAllBytes(fixture.DatabasePath);
        static void Fail() => throw new IOException("injected upgrade failure");
        var hooks = new HistoryTimelinePersistenceTestHooks(
            AfterUpgradeSourceValidated: failpoint == "source-validated" ? Fail : null,
            BeforeUpgradeReplace: failpoint == "before-replace" ? Fail : null,
            AfterUpgradeReplace: failpoint == "after-replace" ? Fail : null);
        HistoryTimelineUpgradeResult result = HistoryTimelineMaintenance.UpgradeSchemaV2Core(fixture.Path,
            fixture.RefId, fixture.TimelineId, HistoryTimelineStorageLimits.Production, hooks);
        if (published) {
            var uncertain = Assert.IsType<HistoryTimelineUpgradeResult.PublishIndeterminate>(result);
            Assert.Equal(fixture.Head, uncertain.Head);
            Assert.Equal(3, uncertain.ObservedSchemaVersion);
            Assert.IsType<HistoryTimelineInspectResult.Available>(HistoryTimelineMaintenance.Verify(fixture.Path, fixture.RefId));
        }
        else {
            Assert.IsType<HistoryTimelineUpgradeResult.Invalid>(result);
            Assert.Equal(before, File.ReadAllBytes(fixture.DatabasePath));
        }
        Assert.Equal(published ? 3 : 2, Version(fixture.DatabasePath));
        HistoryTimelineUpgradeResult retry = HistoryTimelineMaintenance.UpgradeSchemaV2(fixture.Path, fixture.RefId, fixture.TimelineId);
        if (published) Assert.Equal(fixture.Head, Assert.IsType<HistoryTimelineUpgradeResult.AlreadyCurrent>(retry).Head);
        else Assert.Equal(fixture.Head, Assert.IsType<HistoryTimelineUpgradeResult.Upgraded>(retry).Head);
    }

    private LegacyFixture Extract() {
        string root = FindRepositoryRoot();
        string source = Path.Combine(root, "tests", "SessionJournal.HistoryTimeline.Tests", "Fixtures", "RowIdentityV2");
        string path = Path.Combine(Path.GetTempPath(), "atelia-timeline-upgrade-tests", Guid.NewGuid().ToString("N"));
        _paths.Add(path);
        ZipFile.ExtractToDirectory(Path.Combine(source, "repository.zip"), path);
        using JsonDocument expected = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(source, "expected.json")));
        RefId refId = new RefId(ulong.Parse(expected.RootElement.GetProperty("refId").GetString()!, System.Globalization.NumberStyles.HexNumber));
        var timeline = new TimelineId(expected.RootElement.GetProperty("timelineId").GetString()!);
        TimelineHeadRef head = HistoryTimelineCanonicalCodec.DecodeTimelineHead(Convert.FromBase64String(
            expected.RootElement.GetProperty("timelineHeadBase64").GetString()!));
        string database = new HistoryTimelinePaths(path, refId).TimelineDatabasePath(timeline);
        using SqliteConnection connection = OpenSqlite(database);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT row_id FROM rows ORDER BY row_id;";
        using SqliteDataReader reader = command.ExecuteReader();
        var rows = new List<HistoryRowId>();
        while (reader.Read()) rows.Add(new HistoryRowId(reader.GetString(0)));
        return new LegacyFixture(path, refId, timeline, database, head, rows,
            new HistoryRowId(expected.RootElement.GetProperty("oldUnselectedRowId").GetString()!));
    }

    private static Dictionary<string, string[]> SnapshotTables(string database) {
        using SqliteConnection connection = OpenSqlite(database);
        var result = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (string table in new[] { "rows", "policies", "store_metadata", "current_selected_path", "current_selected_path_merkle", "current_selected_path_guard" }) {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"SELECT * FROM {table};";
            using SqliteDataReader reader = command.ExecuteReader();
            var rows = new List<string>();
            while (reader.Read()) {
                var fields = new SortedDictionary<string, string?>(StringComparer.Ordinal);
                for (int i = 0; i < reader.FieldCount; i++) {
                    string name = reader.GetName(i);
                    if (table == "rows" && name == "descriptor_digest" || table == "store_metadata" && name == "schema_version") continue;
                    if (table == "rows" && name == "canonical") {
                        JsonObject descriptor = JsonNode.Parse(reader.GetFieldValue<byte[]>(i))!.AsObject();
                        descriptor.Remove("descriptorDigest");
                        descriptor["v"] = 2;
                        fields.Add(name, descriptor.ToJsonString());
                    }
                    else fields.Add(name, reader.IsDBNull(i) ? null : reader.GetValue(i) is byte[] bytes
                        ? Convert.ToBase64String(bytes) : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture));
                }
                rows.Add(JsonSerializer.Serialize(fields));
            }
            result.Add(table, rows.Order(StringComparer.Ordinal).ToArray());
        }
        return result;
    }
    private static void AssertSnapshot(Dictionary<string, string[]> expected, Dictionary<string, string[]> actual) {
        Assert.Equal(expected.Keys, actual.Keys);
        foreach (string table in expected.Keys) Assert.Equal(expected[table], actual[table]);
    }
    private static Dictionary<string, byte[]> SnapshotOutsideDatabase(LegacyFixture fixture) => Directory.EnumerateFiles(
        fixture.Path, "*", SearchOption.AllDirectories).Where(path => path != fixture.DatabasePath && !path.EndsWith(".lock", StringComparison.Ordinal))
        .Order(StringComparer.Ordinal).ToDictionary(path => Path.GetRelativePath(fixture.Path, path), File.ReadAllBytes);
    private static void AssertOutsideUnchanged(Dictionary<string, byte[]> expected, Dictionary<string, byte[]> actual) {
        Assert.Equal(expected.Keys, actual.Keys);
        foreach (string file in expected.Keys) Assert.Equal(expected[file], actual[file]);
    }
    private static int Version(string database) {
        using SqliteConnection connection = OpenSqlite(database);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar());
    }
    private static SqliteConnection OpenSqlite(string database) {
        var connection = new SqliteConnection($"Data Source={database};Mode=ReadOnly;Pooling=False");
        connection.Open();
        return connection;
    }
    private static void Execute(string database, string sql) {
        using var connection = new SqliteConnection($"Data Source={database};Mode=ReadWrite;Pooling=False");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
    private static string FindRepositoryRoot() {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Atelia.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("Atelia.sln not found.");
    }
    public void Dispose() {
        foreach (string path in _paths) if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }
    private sealed record LegacyFixture(string Path, RefId RefId, TimelineId TimelineId, string DatabasePath,
        TimelineHeadRef Head, IReadOnlyList<HistoryRowId> AllRowIds, HistoryRowId DeselectedRowId);
}
