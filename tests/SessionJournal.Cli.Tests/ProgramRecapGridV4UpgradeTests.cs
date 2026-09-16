using System.Text.Json;
using Atelia.EventJournal;
using Atelia.Galatea.RecapGrid;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid;
using Atelia.SessionJournal.RecapGrid.Control;
using Atelia.SessionJournal.RecapGrid.Store;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Atelia.SessionJournal.Cli.Tests;

/// <summary>End-to-end authority proofs for the V4 Store maintenance command.</summary>
public sealed partial class ProgramRecapGridCommandTests {
    // Mirrors SqliteRecapGridStore.ApplicationId, which is internal to this
    // black-box CLI test assembly.
    private const int RecapGridApplicationId = 0x41544752;

    [Fact]
    public void UpgradeStoreV5ResolvesActiveAndInactivePartialWithoutProvider() {
        V4PartialFixture active = CreateV4PartialFixture(activate: true);
        Dictionary<string, byte[]> before = SnapshotRepositoryBytes();

        (int dryCode, JsonElement dry) = RunGridCaptured(
            "upgrade-store-v5", "--input", _root);
        Assert.True(dryCode == 0, dry.GetRawText());
        Assert.Equal("dry-run-ready", dry.GetProperty("status").GetString());
        AssertRepositoryBytesEqual(before, SnapshotRepositoryBytes());

        (int applyCode, JsonElement applied) = RunGridCaptured(
            "upgrade-store-v5", "--input", _root, "--apply");
        Assert.Equal(0, applyCode);
        Assert.Equal("upgraded", applied.GetProperty("status").GetString());
        Assert.True(File.Exists(applied.GetProperty("detail")
            .GetProperty("BackupPath").GetString()!));
        AssertUpgradedPartial(active);

        (int repeatedCode, JsonElement repeated) = RunGridCaptured(
            "upgrade-store-v5", "--input", _root);
        Assert.Equal(0, repeatedCode);
        Assert.Equal("already-current", repeated.GetProperty("status").GetString());

        // A registered recipe need not be active to be the one exact proof.
        Dispose();
        V4PartialFixture inactive = CreateV4PartialFixture(activate: false);
        (int inactiveCode, JsonElement inactiveResult) = RunGridCaptured(
            "upgrade-store-v5", "--input", _root, "--apply");
        Assert.Equal(0, inactiveCode);
        Assert.Equal("upgraded", inactiveResult.GetProperty("status").GetString());
        AssertUpgradedPartial(inactive);
    }

    [Fact]
    public void UpgradeStoreV5RejectsOrphanPartialWithoutMutation() {
        V4PartialFixture fixture = CreateV4PartialFixture(activate: false);
        // Its root is no longer registered in any exact Control scope.
        using (var connection = OpenV4ForMutation()) {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE cell_artifact SET recipe_digest=$recipe WHERE cell_id=$id;";
            command.Parameters.AddWithValue("$recipe", new string('f', 64));
            command.Parameters.AddWithValue("$id", fixture.PartialCell.Value);
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        Dictionary<string, byte[]> before = SnapshotRepositoryBytes();

        (int code, JsonElement result) = RunGridCaptured(
            "upgrade-store-v5", "--input", _root);

        Assert.Equal(2, code);
        Assert.Equal("partial-proof-unprovable",
            result.GetProperty("status").GetString());
        AssertRepositoryBytesEqual(before, SnapshotRepositoryBytes());
    }

    [Fact]
    public void UpgradeStoreV5UsesHistoricalExactScopeNotNewActiveTimeline() {
        V4PartialFixture fixture = CreateV4PartialFixture(activate: false);
        ActiveTimelineLocator oldLocator = Assert.IsType<HistoryTimelineInspectResult.Available>(
            HistoryTimelineMaintenance.Inspect(_root, fixture.RefId)).Locator;
        Dictionary<string, byte[]> raw = SnapshotRawAuthority();
        Dictionary<string, byte[]> control = SnapshotDirectory(Path.Combine(
            _root, "control", "recap-grid"));
        var policy = new HistoryTimelineInitialPolicySpec(
            HistoryPartitionAlgorithms.FirstReplaySafeBoundaryAtTargetV1,
            O200kBaseHistoryUnitLoadEstimator.EstimatorId,
            new HistoryLoadUnit(1), 64, 1024 * 1024);
        HistoryTimelineAbandonResult.Abandoned abandoned = Assert.IsType<
            HistoryTimelineAbandonResult.Abandoned>(
            HistoryTimelineMaintenance.Abandon(_root, fixture.RefId, oldLocator,
                policy, new O200kBaseHistoryUnitLoadEstimator()));
        Assert.NotEqual(oldLocator.ActiveTimelineId,
            abandoned.Locator.ActiveTimelineId);

        (int code, JsonElement result) = RunGridCaptured(
            "upgrade-store-v5", "--input", _root, "--apply");

        Assert.Equal(0, code);
        Assert.Equal("upgraded", result.GetProperty("status").GetString());
        Assert.Equal(abandoned.Locator, Assert.IsType<HistoryTimelineInspectResult.Available>(
            HistoryTimelineMaintenance.Inspect(_root, fixture.RefId)).Locator);
        AssertSnapshotEqual(raw, SnapshotRawAuthority());
        AssertSnapshotEqual(control, SnapshotDirectory(Path.Combine(
            _root, "control", "recap-grid")));
        AssertUpgradedPartial(fixture);
    }

    private V4PartialFixture CreateV4PartialFixture(bool activate) {
        CreateJournal(turns: 3);
        RefId refId;
        using (SessionJournalEngine journal = SessionJournalEngine.OpenReadOnly(_root)) {
            refId = journal.BranchRefId;
        }
        Assert.Equal(0, RunInit(SessionJournalDefaults.MainBranchName, refId,
            WriteAdmission(["create"])));
        Assert.Equal(0, Run("timeline", "sync", "--input", _root,
            "--confirm-ref", refId.ToHexString(), "--max-rows", "16"));

        Assert.True(GalateaRecapGridAssets.TryCreateRegistrationBundle(
            GalateaRecapGridAssets.RollingRewriteZhCnV7, GalateaParameters,
            out RecapGridControlRegistrationBundle? bundle));
        string admission = WriteAdmission(
            ["create", "register-family", "register-definition", "register-recipe", "activate"],
            bundle!.Families.Select(static x => x.Digest.Value).ToArray(),
            bundle.Definitions.Select(static x => x.Capability.CapabilityFingerprint)
                .Distinct().ToArray(),
            [ContextHeaderCarrierTokens.Observation, ContextHeaderCarrierTokens.Action],
            ["world-understanding", "autobiography"]);
        Assert.Equal(0, Run("control", "provision-asset", "--input", _root,
            "--confirm-ref", refId.ToHexString(), "--admission", admission,
            "--asset", GalateaRecapGridAssets.RollingRewriteZhCnV7,
            "--character-name", "Galatea"));
        string recipePath = ExternalPath("v4-partial-recipe.json");
        Assert.Equal(0, Run([
            "control", "compose-full-recipe", "--input", _root, "--output", recipePath,
            .. bundle.Definitions.SelectMany(static x => new[] { "--definition", x.Digest.Value! })
        ]));
        GridBuildRecipe recipe = GridBuildRecipe.DecodeCanonical(File.ReadAllBytes(recipePath));
        Assert.Equal(0, Run("control", "put-recipe", "--input", _root,
            "--confirm-ref", refId.ToHexString(), "--admission", admission,
            "--recipe", recipePath));
        TimelineHeadRef head = ReadTimelineHead(refId.ToHexString());
        if (activate) {
            Assert.Equal(0, Run(DirectActivationArgs(refId.ToHexString(), admission,
                ReadControlHead(refId.ToHexString()), head, recipe.Digest)));
        }
        HistoryTimelineSelectedRow[] selected;
        using (HistoryTimelineReaderHandle timeline = Assert.IsType<HistoryTimelineReaderOpenResult.Opened>(
                   HistoryTimelineMaintenance.OpenReader(_root, refId)).Handle) {
            selected = ReadSelected(timeline.Reader, head).ToArray();
        }
        Assert.True(selected.Length >= 2, "Fixture requires a predecessor row.");
        CreateV4Store(refId, head.TimelineId, recipe, selected[0].Descriptor.RowId,
            selected[1].Descriptor.RowId, out CellId partialCell, out RowResultId prior);
        return new V4PartialFixture(refId, head.TimelineId, recipe,
            selected[1].Descriptor.RowId, selected[0].Descriptor.RowId,
            partialCell, prior);
    }

    private void CreateV4Store(RefId refId, TimelineId timelineId,
        GridBuildRecipe recipe, HistoryRowId priorHistory, HistoryRowId partialHistory,
        out CellId partialCell, out RowResultId priorResult) {
        string directory = Path.Combine(_root, "derived", "recap-grid", "v1");
        Directory.CreateDirectory(directory);
        string database = Path.Combine(directory, "grid.sqlite");
        File.Delete(database);
        priorResult = new RowResultId(Guid.NewGuid().ToString("N"));
        partialCell = new CellId(Guid.NewGuid().ToString("N"));
        using var connection = new SqliteConnection($"Data Source={database};Mode=ReadWriteCreate;Pooling=False");
        connection.Open();
        using (SqliteCommand schema = connection.CreateCommand()) {
            schema.CommandText = ReadV4SchemaForCliTest()
                + $"PRAGMA application_id = {RecapGridApplicationId}; PRAGMA user_version = 4;";
            schema.ExecuteNonQuery();
        }
        using SqliteTransaction transaction = connection.BeginTransaction();
        Execute("INSERT INTO store_metadata(singleton,schema_version,store_instance_id,cell_count,row_view_count,row_view_member_count,fulfilled_view_count) VALUES(1,4,'00112233445566778899aabbccddeeff',$cells,1,$members,0);",
            ("$cells", recipe.Target.OrderedColumns.Count + 1),
            ("$members", recipe.Target.OrderedColumns.Count));
        Execute("INSERT INTO row_view(row_result_id,ref_id,timeline_id,history_row_id,recipe_digest,target_digest,previous_history_row_id,previous_row_result_id,bootstrap_completed) VALUES($id,$ref,$timeline,$history,$recipe,$target,NULL,NULL,1);",
            ("$id", priorResult.Value), ("$ref", refId.ToHexString()),
            ("$timeline", timelineId.Value), ("$history", priorHistory.Value),
            ("$recipe", recipe.Digest.Value), ("$target", recipe.Target.Digest.Value));
        for (int index = 0; index < recipe.Target.OrderedColumns.Count; index++) {
            BuildTargetColumn column = recipe.Target.OrderedColumns[index];
            string id = Guid.NewGuid().ToString("N");
            Execute("INSERT INTO cell_artifact(cell_id,recipe_digest,history_row_id,logical_column_id,definition_digest,outcome,content) VALUES($id,$recipe,$history,$column,$definition,0,$content);",
                ("$id", id), ("$recipe", recipe.Digest.Value), ("$history", priorHistory.Value),
                ("$column", column.LogicalColumnId.Value), ("$definition", column.DefinitionDigest.Value),
                ("$content", "prior " + index));
            Execute("INSERT INTO row_view_member(row_result_id,column_ordinal,logical_column_id,definition_digest,cell_id) VALUES($row,$ordinal,$column,$definition,$cell);",
                ("$row", priorResult.Value), ("$ordinal", index), ("$column", column.LogicalColumnId.Value),
                ("$definition", column.DefinitionDigest.Value), ("$cell", id));
        }
        BuildTargetColumn partialColumn = recipe.Target.OrderedColumns[0];
        Execute("INSERT INTO cell_artifact(cell_id,recipe_digest,history_row_id,logical_column_id,definition_digest,outcome,content) VALUES($id,$recipe,$history,$column,$definition,0,'partial content');",
            ("$id", partialCell.Value), ("$recipe", recipe.Digest.Value), ("$history", partialHistory.Value),
            ("$column", partialColumn.LogicalColumnId.Value), ("$definition", partialColumn.DefinitionDigest.Value));
        transaction.Commit();

        void Execute(string sql, params (string Name, object Value)[] values) {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction; command.CommandText = sql;
            foreach ((string name, object value) in values) command.Parameters.AddWithValue(name, value);
            _ = command.ExecuteNonQuery();
        }
    }

    private void AssertUpgradedPartial(V4PartialFixture fixture) {
        using RecapGridStoreReaderHandle reader = Assert.IsType<RecapGridStoreReaderOpenResult.Opened>(
            RecapGridStoreFactory.OpenReader(_root)).Handle;
        var key = new RowWorkKey(fixture.RefId, fixture.TimelineId, fixture.Recipe.Digest,
            fixture.PartialHistory);
        RowWork work = Assert.IsType<RecapGridStoreReadResult<RowWork>.Found>(
            reader.Reader.ReadRowWork(key)).Value;
        Assert.Equal(fixture.Recipe.Target.Digest, work.ProducerTarget.Digest);
        Assert.Equal(fixture.PriorHistory, work.PreviousHistoryRowId);
        Assert.Equal(fixture.PriorResult, work.PreviousRowResultId);
        RecapCellArtifact cell = Assert.IsType<RecapGridStoreReadResult<RecapCellArtifact>.Found>(
            reader.Reader.ReadCell(fixture.PartialCell)).Value;
        Assert.Equal("partial content", cell.Content);
        Assert.Equal(work.WorkId, cell.Slot.WorkId);
    }

    private SqliteConnection OpenV4ForMutation() {
        var connection = new SqliteConnection($"Data Source={Path.Combine(_root, "derived", "recap-grid", "v1", "grid.sqlite")};Mode=ReadWrite;Pooling=False");
        connection.Open();
        return connection;
    }

    private static IReadOnlyList<HistoryTimelineSelectedRow> ReadSelected(
        HistoryTimelineReader reader, TimelineHeadRef head) {
        var rows = new List<HistoryTimelineSelectedRow>(); HistoryTimelinePathCursor? cursor = null;
        do { var page = Assert.IsType<HistoryTimelinePathPageResult.Page>(reader.ReadSelectedPathPage(head, cursor)); rows.AddRange(page.Value.Rows); cursor = page.Value.Next; } while (cursor is not null);
        return rows;
    }

    private static string ReadV4SchemaForCliTest() {
        using Stream stream = typeof(RecapGridStoreFactory).Assembly.GetManifestResourceStream(
            "Atelia.SessionJournal.RecapGrid.Store.SchemaV4.sql")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private Dictionary<string, byte[]> SnapshotRepositoryBytes() => Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
        .ToDictionary(path => Path.GetRelativePath(_root, path), File.ReadAllBytes);
    private static void AssertRepositoryBytesEqual(Dictionary<string, byte[]> expected, Dictionary<string, byte[]> actual) {
        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
        foreach ((string path, byte[] bytes) in expected) Assert.Equal(bytes, actual[path]);
    }

    private sealed record V4PartialFixture(RefId RefId, TimelineId TimelineId,
        GridBuildRecipe Recipe, HistoryRowId PartialHistory, HistoryRowId PriorHistory,
        CellId PartialCell, RowResultId PriorResult);
}
