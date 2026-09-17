using System.Text.Json;
using Atelia.Completion.Abstractions;
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
    public void UpgradeStoreV5MigratesTwoExactRefScopesWithoutChangingAuthorities() {
        V4MultiRefFixture fixture = CreateV4MultiRefFixture();
        Dictionary<string, byte[]> before = SnapshotRepositoryBytes();
        Dictionary<string, byte[]> raw = SnapshotRawAuthority();
        Dictionary<string, byte[]> timelines = SnapshotDirectory(Path.Combine(
            _root, "derived", "history-timeline"));
        Dictionary<string, byte[]> control = SnapshotDirectory(Path.Combine(
            _root, "control", "recap-grid"));
        Dictionary<string, Dictionary<string, byte[]>> cadences = fixture.Scopes
            .ToDictionary(
                static scope => scope.RefId.ToHexString(),
                scope => SnapshotDirectory(Path.Combine(
                    _root, "control", "recap-grid", "v1", "refs",
                    scope.RefId.ToHexString(), "cadence")),
                StringComparer.Ordinal);
        string storeDirectory = Path.Combine(
            _root, "derived", "recap-grid", "v1");

        (int dryCode, JsonElement dry) = RunGridCaptured(
            "upgrade-store-v5", "--input", _root);

        Assert.True(dryCode == 0, dry.GetRawText());
        Assert.Equal("dry-run-ready", dry.GetProperty("status").GetString());
        AssertRepositoryBytesEqual(before, SnapshotRepositoryBytes());
        Assert.Empty(Directory.GetFiles(
            storeDirectory, "grid.sqlite.v4-backup-*.sqlite"));

        (int applyCode, JsonElement applied) = RunGridCaptured(
            "upgrade-store-v5", "--input", _root, "--apply");

        Assert.True(applyCode == 0, applied.GetRawText());
        Assert.Equal("upgraded", applied.GetProperty("status").GetString());
        string backup = applied.GetProperty("detail")
            .GetProperty("BackupPath").GetString()!;
        Assert.Equal(
            new[] { backup },
            Directory.GetFiles(
                storeDirectory, "grid.sqlite.v4-backup-*.sqlite"));
        AssertSnapshotEqual(raw, SnapshotRawAuthority());
        AssertSnapshotEqual(timelines, SnapshotDirectory(Path.Combine(
            _root, "derived", "history-timeline")));
        AssertSnapshotEqual(control, SnapshotDirectory(Path.Combine(
            _root, "control", "recap-grid")));
        foreach (V4MultiRefScopeFixture scope in fixture.Scopes) {
            AssertSnapshotEqual(
                cadences[scope.RefId.ToHexString()],
                SnapshotDirectory(Path.Combine(
                    _root, "control", "recap-grid", "v1", "refs",
                    scope.RefId.ToHexString(), "cadence")));
        }

        (int verifyCode, JsonElement verified) = RunGridCaptured(
            "verify", "--input", _root);
        Assert.True(verifyCode == 0, verified.GetRawText());
        Assert.Equal("healthy", verified.GetProperty("status").GetString());

        using (RecapGridStoreReaderHandle reader = Assert.IsType<
                   RecapGridStoreReaderOpenResult.Opened>(
                   RecapGridStoreFactory.OpenReader(_root)).Handle) {
            foreach (V4MultiRefScopeFixture scope in fixture.Scopes) {
                AssertUpgradedMultiRefScope(reader.Reader, scope);
                AssertExactControlScope(scope);
            }
        }
        AssertExportedMultiRefWork(fixture.Scopes);

        (int repeatedCode, JsonElement repeated) = RunGridCaptured(
            "upgrade-store-v5", "--input", _root, "--apply");
        Assert.True(repeatedCode == 0, repeated.GetRawText());
        Assert.Equal("already-current",
            repeated.GetProperty("status").GetString());
        Assert.Equal(
            new[] { backup },
            Directory.GetFiles(
                storeDirectory, "grid.sqlite.v4-backup-*.sqlite"));
        AssertSnapshotEqual(raw, SnapshotRawAuthority());
        AssertSnapshotEqual(timelines, SnapshotDirectory(Path.Combine(
            _root, "derived", "history-timeline")));
        AssertSnapshotEqual(control, SnapshotDirectory(Path.Combine(
            _root, "control", "recap-grid")));
    }

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

        Assert.True(code == 0, result.GetRawText());
        Assert.Equal("upgraded", result.GetProperty("status").GetString());
        Assert.Equal(abandoned.Locator, Assert.IsType<HistoryTimelineInspectResult.Available>(
            HistoryTimelineMaintenance.Inspect(_root, fixture.RefId)).Locator);
        AssertSnapshotEqual(raw, SnapshotRawAuthority());
        AssertSnapshotEqual(control, SnapshotDirectory(Path.Combine(
            _root, "control", "recap-grid")));
        AssertUpgradedPartial(fixture);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UpgradeStoreV5RecoversOverlayBootstrapAndPostBootstrapWork(bool afterBootstrap) {
        V4PartialFixture baseFixture = CreateV4PartialFixture(activate: false);
        HistoryRowId bootstrap = baseFixture.PriorHistory;
        HistoryRowId partial = afterBootstrap ? baseFixture.PartialHistory : bootstrap;
        GridBuildRecipe overlay = GridBuildRecipe.CreateOverlay(baseFixture.Recipe, bootstrap,
            baseFixture.Recipe.Target, [baseFixture.Recipe.Target.OrderedColumns[0].LogicalColumnId]);
        string overlayPath = ExternalPath("v4-overlay.json");
        File.WriteAllBytes(overlayPath, overlay.ToCanonicalBytes());
        Assert.Equal(0, Run("control", "put-recipe", "--input", _root,
            "--confirm-ref", baseFixture.RefId.ToHexString(), "--admission",
            Path.Combine(_root, "admission.json"), "--recipe", overlayPath));
        CreateV4OverlayStore(baseFixture, overlay, bootstrap, partial, afterBootstrap,
            out CellId reusedSource, out CellId overlayPartial, out RowResultId? bootstrapView);

        (int code, JsonElement result) = RunGridCaptured("upgrade-store-v5", "--input", _root, "--apply");
        Assert.True(code == 0, result.GetRawText());
        Assert.Equal("upgraded", result.GetProperty("status").GetString());
        using RecapGridStoreReaderHandle reader = Assert.IsType<RecapGridStoreReaderOpenResult.Opened>(RecapGridStoreFactory.OpenReader(_root)).Handle;
        RowWork work = Assert.IsType<RecapGridStoreReadResult<RowWork>.Found>(reader.Reader.ReadRowWork(
            new RowWorkKey(baseFixture.RefId, baseFixture.TimelineId, overlay.Digest, partial))).Value;
        if (!afterBootstrap) {
            Assert.Equal(reusedSource, work.OrderedAssignments[1].ReusedCellId);
            Assert.Null(work.PreviousRowResultId);
        } else {
            Assert.All(work.OrderedAssignments, x => Assert.True(x.IsEvaluate));
            Assert.Equal(bootstrap, work.PreviousHistoryRowId);
            Assert.Equal(bootstrapView, work.PreviousRowResultId);
        }
        Assert.Equal("overlay partial", Assert.IsType<RecapGridStoreReadResult<RecapCellArtifact>.Found>(reader.Reader.ReadCell(overlayPartial)).Value.Content);

        (int hiddenCode, JsonElement hidden) = RunGridCaptured(
            "export", "--input", _root);
        Assert.True(hiddenCode == 0, hidden.GetRawText());
        Assert.All(hidden.GetProperty("detail").GetProperty("items")
            .EnumerateArray(), static item => Assert.Equal(
                JsonValueKind.Null,
                item.GetProperty("jsonBase64").ValueKind
            ));

        JsonElement exportedWork = Assert.Single(
            DrainStoreExportContent(),
            item => item.GetProperty("Kind").GetString() == "row-work"
                && item.GetProperty("Key").GetString() == work.WorkId.Value
        );
        byte[] workJson = Convert.FromBase64String(
            exportedWork.GetProperty("jsonBase64").GetString()!
        );
        using JsonDocument document = JsonDocument.Parse(workJson);
        JsonElement root = document.RootElement;
        Assert.Equal(work.ProducerTarget.Digest.Value,
            root.GetProperty("producerTarget").GetProperty("digest")
                .GetString());
        Assert.Equal(work.PreviousHistoryRowId?.Value,
            root.GetProperty("previousHistoryRowId").ValueKind
                == JsonValueKind.Null
                ? null
                : root.GetProperty("previousHistoryRowId").GetString());
        Assert.Equal(work.PreviousRowResultId?.Value,
            root.GetProperty("previousRowResultId").ValueKind
                == JsonValueKind.Null
                ? null
                : root.GetProperty("previousRowResultId").GetString());
        JsonElement[] exportedAssignments = root
            .GetProperty("orderedAssignments").EnumerateArray().ToArray();
        if (afterBootstrap) {
            Assert.All(exportedAssignments, static assignment => Assert.Equal(
                "evaluate",
                assignment.GetProperty("kind").GetString()
            ));
        }
        else {
            Assert.Equal("reuse",
                exportedAssignments[1].GetProperty("kind").GetString());
            Assert.Equal(reusedSource.Value,
                exportedAssignments[1].GetProperty("reusedCellId").GetString());
        }
    }

    private HistoryTimelineSelectedRow[] ReadSelectedFor(RefId refId) {
        TimelineHeadRef head = ReadTimelineHead(refId.ToHexString());
        using HistoryTimelineReaderHandle reader = Assert.IsType<HistoryTimelineReaderOpenResult.Opened>(HistoryTimelineMaintenance.OpenReader(_root, refId)).Handle;
        return ReadSelected(reader.Reader, head).ToArray();
    }

    private IReadOnlyList<JsonElement> DrainStoreExportContent() {
        var items = new List<JsonElement>();
        string? after = null;
        do {
            string[] args = after is null
                ? ["export", "--input", _root, "--include-content"]
                : ["export", "--input", _root, "--include-content",
                    "--after", after];
            (int code, JsonElement result) = RunGridCaptured(args);
            Assert.True(code == 0, result.GetRawText());
            JsonElement detail = result.GetProperty("detail");
            items.AddRange(detail.GetProperty("items").EnumerateArray()
                .Select(static item => item.Clone()));
            after = detail.GetProperty("nextCursor").ValueKind
                == JsonValueKind.Null
                ? null
                : detail.GetProperty("nextCursor").GetString();
            if (!detail.GetProperty("Incomplete").GetBoolean()) {
                break;
            }
            Assert.False(string.IsNullOrEmpty(after));
        } while (true);
        return items;
    }

    private void CreateV4OverlayStore(V4PartialFixture basis, GridBuildRecipe overlay,
        HistoryRowId bootstrap, HistoryRowId partial, bool afterBootstrap,
        out CellId reusedSource, out CellId overlayPartial, out RowResultId? bootstrapView) {
        string database = Path.Combine(_root, "derived", "recap-grid", "v1", "grid.sqlite");
        File.Delete(database); CellId baseEvaluated = new(Guid.NewGuid().ToString("N"));
        reusedSource = new CellId(Guid.NewGuid().ToString("N"));
        overlayPartial = new CellId(Guid.NewGuid().ToString("N"));
        bootstrapView = afterBootstrap ? new RowResultId(Guid.NewGuid().ToString("N")) : null;
        using var connection = new SqliteConnection($"Data Source={database};Mode=ReadWriteCreate;Pooling=False"); connection.Open();
        using (SqliteCommand schema = connection.CreateCommand()) { schema.CommandText = ReadV4SchemaForCliTest() + $"PRAGMA application_id={RecapGridApplicationId};PRAGMA user_version=4;"; schema.ExecuteNonQuery(); }
        using SqliteTransaction tx = connection.BeginTransaction();
        string baseRow = Guid.NewGuid().ToString("N");
        CellId lastCell = default;
        int cells = afterBootstrap ? 4 : 3, rows = afterBootstrap ? 2 : 1, members = afterBootstrap ? 4 : 2;
        Q("INSERT INTO store_metadata VALUES(1,4,'00112233445566778899aabbccddeeff',$c,$r,$m,0);", ("$c",cells),("$r",rows),("$m",members));
        Row(baseRow, basis.Recipe, bootstrap, null, null);
        Cell(baseEvaluated, basis.Recipe, bootstrap, 0, "base source");
        Cell(reusedSource, basis.Recipe, bootstrap, 1, "base other");
        Member(baseRow, 0, baseEvaluated); Member(baseRow, 1, lastCell);
        if (afterBootstrap) { Row(bootstrapView!.Value.Value, overlay, bootstrap, null, null); Cell(overlayPartial, overlay, bootstrap, 0, "overlay bootstrap"); Member(bootstrapView.Value.Value, 0, overlayPartial); Member(bootstrapView.Value.Value, 1, reusedSource); overlayPartial = new CellId(Guid.NewGuid().ToString("N")); }
        Cell(overlayPartial, overlay, partial, 0, "overlay partial"); tx.Commit();
        void Q(string s, params (string,object)[] v) { using SqliteCommand c=connection.CreateCommand();c.Transaction=tx;c.CommandText=s;foreach(var p in v)c.Parameters.AddWithValue(p.Item1,p.Item2);c.ExecuteNonQuery(); }
        void Row(string id, GridBuildRecipe r, HistoryRowId h, HistoryRowId? ph, RowResultId? pr) => Q("INSERT INTO row_view VALUES($id,$ref,$ti,$h,$rd,$td,$ph,$pr,1);",("$id",id),("$ref",basis.RefId.ToHexString()),("$ti",basis.TimelineId.Value),("$h",h.Value),("$rd",r.Digest.Value),("$td",r.Target.Digest.Value),("$ph",(object?)ph?.Value??DBNull.Value),("$pr",(object?)pr?.Value??DBNull.Value));
        void Cell(CellId id, GridBuildRecipe r, HistoryRowId h, int i, string text) { var col=r.Target.OrderedColumns[i]; lastCell=id; Q("INSERT INTO cell_artifact VALUES($id,$r,$h,$l,$d,0,$x);",("$id",id.Value),("$r",r.Digest.Value),("$h",h.Value),("$l",col.LogicalColumnId.Value),("$d",col.DefinitionDigest.Value),("$x",text)); }
        void Member(string row,int i,CellId id) { var col=basis.Recipe.Target.OrderedColumns[i]; Q("INSERT INTO row_view_member VALUES($r,$i,$l,$d,$c);",("$r",row),("$i",i),("$l",col.LogicalColumnId.Value),("$d",col.DefinitionDigest.Value),("$c",id.Value)); }
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

    private V4MultiRefFixture CreateV4MultiRefFixture() {
        CreateJournal(turns: 3);
        RefId mainRef;
        RefId featureRef;
        using (Atelia.EventJournal.EventJournal journal =
               Atelia.EventJournal.EventJournal.OpenExisting(_root)) {
            mainRef = journal.OpenBranch(
                SessionJournalDefaults.MainBranchName).Value;
            EventAddress mainHead = journal.GetHead(mainRef)!.Value;
            featureRef = journal.CreateBranch("feature", mainHead).Value;
        }
        using (SessionJournalEngine feature =
               SessionJournalEngine.Open(_root, "feature")) {
            for (int index = 0; index < 2; index++) {
                _ = feature.AppendObservation($"feature observation {index}");
                _ = feature.AppendImportedAgentAction(
                    new ActionMessage([
                        new ActionBlock.Text($"feature answer {index}")
                    ]),
                    new CompletionDescriptor("import", "v1", "model"));
            }
        }

        string createAdmission = WriteAdmission(["create"]);
        Assert.Equal(0, RunInit(
            SessionJournalDefaults.MainBranchName,
            mainRef,
            createAdmission));
        Assert.Equal(0, RunInit("feature", featureRef, createAdmission));
        (string Branch, RefId RefId)[] branches = [
            (SessionJournalDefaults.MainBranchName, mainRef),
            ("feature", featureRef)
        ];
        foreach ((string branch, RefId refId) in branches) {
            Assert.Equal(0, Run(
                "timeline", "sync", "--input", _root,
                "--branch", branch,
                "--confirm-ref", refId.ToHexString(), "--max-rows", "16"));
        }

        Assert.True(GalateaRecapGridAssets.TryCreateRegistrationBundle(
            GalateaRecapGridAssets.RollingRewriteZhCnV7,
            GalateaParameters,
            out RecapGridControlRegistrationBundle? bundle));
        string admission = WriteAdmission(
            ["create", "register-family", "register-definition",
                "register-recipe", "activate"],
            bundle!.Families.Select(static value => value.Digest.Value)
                .ToArray(),
            bundle.Definitions.Select(static value =>
                    value.Capability.CapabilityFingerprint)
                .Distinct().ToArray(),
            [ContextHeaderCarrierTokens.Observation,
                ContextHeaderCarrierTokens.Action],
            ["world-understanding", "autobiography"]);
        foreach ((string branch, RefId refId) in branches) {
            Assert.Equal(0, Run(
                "control", "provision-asset", "--input", _root,
                "--branch", branch,
                "--confirm-ref", refId.ToHexString(),
                "--admission", admission,
                "--asset", GalateaRecapGridAssets.RollingRewriteZhCnV7,
                "--character-name", "Galatea"));
        }

        string mainRecipePath = ExternalPath("v4-multi-main-recipe.json");
        string featureRecipePath = ExternalPath(
            "v4-multi-feature-recipe.json");
        Assert.Equal(0, Run([
            "control", "compose-full-recipe", "--input", _root,
            "--output", mainRecipePath,
            .. bundle.Definitions.SelectMany(static value =>
                new[] { "--definition", value.Digest.Value! })
        ]));
        Assert.Equal(0, Run([
            "control", "compose-full-recipe", "--input", _root,
            "--branch", "feature",
            "--output", featureRecipePath,
            .. bundle.Definitions.Reverse().SelectMany(static value =>
                new[] { "--definition", value.Digest.Value! })
        ]));
        GridBuildRecipe mainRecipe = GridBuildRecipe.DecodeCanonical(
            File.ReadAllBytes(mainRecipePath));
        GridBuildRecipe featureRecipe = GridBuildRecipe.DecodeCanonical(
            File.ReadAllBytes(featureRecipePath));
        Assert.NotEqual(mainRecipe.Digest, featureRecipe.Digest);

        V4MultiRefScopeSeed[] seeds = [
            ConfigureScope(
                SessionJournalDefaults.MainBranchName,
                mainRef,
                mainRecipePath,
                mainRecipe,
                useTail: false,
                "main"),
            ConfigureScope(
                "feature",
                featureRef,
                featureRecipePath,
                featureRecipe,
                useTail: true,
                "feature")
        ];
        IReadOnlyDictionary<string, string> headings = bundle.Definitions
            .ToDictionary(
                static definition => definition.LogicalColumnId.Value,
                static definition => definition.Target.SemanticHeading,
                StringComparer.Ordinal);
        return new V4MultiRefFixture(
            CreateV4MultiRefStore(seeds, headings));

        V4MultiRefScopeSeed ConfigureScope(
            string branch,
            RefId refId,
            string recipePath,
            GridBuildRecipe recipe,
            bool useTail,
            string label) {
            Assert.Equal(0, Run(
                "control", "put-recipe", "--input", _root,
                "--branch", branch,
                "--confirm-ref", refId.ToHexString(),
                "--admission", admission,
                "--recipe", recipePath));
            TimelineHeadRef head = ReadTimelineHead(refId.ToHexString());
            Assert.Equal(0, Run([
                .. DirectActivationArgs(
                    refId.ToHexString(),
                    admission,
                    ReadControlHead(refId.ToHexString()),
                    head,
                    recipe.Digest),
                "--branch", branch
            ]));
            HistoryTimelineSelectedRow[] selected;
            using (HistoryTimelineExactReaderHandle timeline = Assert.IsType<
                       HistoryTimelineExactReaderOpenResult.Opened>(
                       HistoryTimelineMaintenance.OpenExactReader(
                           _root, refId, head.TimelineId)).Handle) {
                selected = ReadSelected(timeline.Reader, head).ToArray();
            }
            Assert.True(selected.Length >= 2,
                $"{branch} fixture requires a predecessor row.");
            int priorIndex = useTail ? selected.Length - 2 : 0;
            GridBuildRecipeDigest? active = ReadControlHead(
                refId.ToHexString()).ActiveRecipeDigest;
            Assert.Equal(recipe.Digest, active);
            return new V4MultiRefScopeSeed(
                label,
                refId,
                head.TimelineId,
                recipe,
                selected[priorIndex].Descriptor.RowId,
                selected[priorIndex + 1].Descriptor.RowId,
                recipe.Digest);
        }
    }

    private V4MultiRefScopeFixture[] CreateV4MultiRefStore(
        IReadOnlyList<V4MultiRefScopeSeed> seeds,
        IReadOnlyDictionary<string, string> headings) {
        string directory = Path.Combine(
            _root, "derived", "recap-grid", "v1");
        Directory.CreateDirectory(directory);
        string database = Path.Combine(directory, "grid.sqlite");
        File.Delete(database);
        using var connection = new SqliteConnection(
            $"Data Source={database};Mode=ReadWriteCreate;Pooling=False");
        connection.Open();
        using (SqliteCommand schema = connection.CreateCommand()) {
            schema.CommandText = ReadV4SchemaForCliTest()
                + $"PRAGMA application_id={RecapGridApplicationId};"
                + "PRAGMA user_version=4;";
            schema.ExecuteNonQuery();
        }
        using SqliteTransaction transaction = connection.BeginTransaction();
        int cellCount = seeds.Sum(static seed =>
            seed.Recipe.Target.OrderedColumns.Count + 1);
        int memberCount = seeds.Sum(static seed =>
            seed.Recipe.Target.OrderedColumns.Count);
        Execute(
            "INSERT INTO store_metadata(singleton,schema_version,store_instance_id,cell_count,row_view_count,row_view_member_count,fulfilled_view_count) VALUES(1,4,'00112233445566778899aabbccddeeff',$cells,$rows,$members,0);",
            ("$cells", cellCount),
            ("$rows", seeds.Count),
            ("$members", memberCount));
        var fixtures = new List<V4MultiRefScopeFixture>(seeds.Count);
        foreach (V4MultiRefScopeSeed seed in seeds) {
            var priorResult = new RowResultId(Guid.NewGuid().ToString("N"));
            Execute(
                "INSERT INTO row_view(row_result_id,ref_id,timeline_id,history_row_id,recipe_digest,target_digest,previous_history_row_id,previous_row_result_id,bootstrap_completed) VALUES($id,$ref,$timeline,$history,$recipe,$target,NULL,NULL,1);",
                ("$id", priorResult.Value),
                ("$ref", seed.RefId.ToHexString()),
                ("$timeline", seed.TimelineId.Value),
                ("$history", seed.PriorHistory.Value),
                ("$recipe", seed.Recipe.Digest.Value),
                ("$target", seed.Recipe.Target.Digest.Value));
            var priorCells = new List<V4CellExpectation>();
            for (int index = 0;
                 index < seed.Recipe.Target.OrderedColumns.Count;
                 index++) {
                BuildTargetColumn column =
                    seed.Recipe.Target.OrderedColumns[index];
                var cellId = new CellId(Guid.NewGuid().ToString("N"));
                string content = $"{seed.Label} prior {index}";
                InsertCell(
                    cellId,
                    seed.Recipe,
                    seed.PriorHistory,
                    column,
                    content);
                Execute(
                    "INSERT INTO row_view_member(row_result_id,column_ordinal,logical_column_id,definition_digest,cell_id) VALUES($row,$ordinal,$column,$definition,$cell);",
                    ("$row", priorResult.Value),
                    ("$ordinal", index),
                    ("$column", column.LogicalColumnId.Value),
                    ("$definition", column.DefinitionDigest.Value),
                    ("$cell", cellId.Value));
                priorCells.Add(new V4CellExpectation(
                    cellId, column, content));
            }
            BuildTargetColumn partialColumn =
                seed.Recipe.Target.OrderedColumns[0];
            var partialCell = new CellId(Guid.NewGuid().ToString("N"));
            string partialContent = seed.Label + " partial";
            InsertCell(
                partialCell,
                seed.Recipe,
                seed.PartialHistory,
                partialColumn,
                partialContent);
            fixtures.Add(new V4MultiRefScopeFixture(
                seed.Label,
                seed.RefId,
                seed.TimelineId,
                seed.Recipe,
                seed.PriorHistory,
                seed.PartialHistory,
                priorResult,
                priorCells,
                new V4CellExpectation(
                    partialCell, partialColumn, partialContent),
                seed.ActiveRecipeDigest,
                headings));
        }
        transaction.Commit();
        return fixtures.ToArray();

        void InsertCell(
            CellId id,
            GridBuildRecipe recipe,
            HistoryRowId history,
            BuildTargetColumn column,
            string content) => Execute(
                "INSERT INTO cell_artifact(cell_id,recipe_digest,history_row_id,logical_column_id,definition_digest,outcome,content) VALUES($id,$recipe,$history,$column,$definition,0,$content);",
                ("$id", id.Value),
                ("$recipe", recipe.Digest.Value),
                ("$history", history.Value),
                ("$column", column.LogicalColumnId.Value),
                ("$definition", column.DefinitionDigest.Value),
                ("$content", content));

        void Execute(string sql, params (string Name, object Value)[] values) {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            foreach ((string name, object value) in values) {
                command.Parameters.AddWithValue(name, value);
            }
            _ = command.ExecuteNonQuery();
        }
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

    private static void AssertUpgradedMultiRefScope(
        RecapGridStoreReader reader,
        V4MultiRefScopeFixture fixture) {
        RowWork priorWork = Assert.IsType<
            RecapGridStoreReadResult<RowWork>.Found>(reader.ReadRowWork(
            new RowWorkKey(
                fixture.RefId,
                fixture.TimelineId,
                fixture.Recipe.Digest,
                fixture.PriorHistory))).Value;
        Assert.Equal(fixture.RefId, priorWork.Key.RefId);
        Assert.Equal(fixture.TimelineId, priorWork.Key.TimelineId);
        Assert.Equal(fixture.Recipe.Digest,
            priorWork.Key.RootRecipeDigest);
        Assert.Equal(fixture.PriorHistory, priorWork.Key.HistoryRowId);
        Assert.Equal(fixture.Recipe.Target.Digest,
            priorWork.ProducerTarget.Digest);
        Assert.Null(priorWork.PreviousHistoryRowId);
        Assert.Null(priorWork.PreviousRowResultId);
        Assert.All(priorWork.OrderedAssignments,
            static assignment => Assert.True(assignment.IsEvaluate));

        RecapRowView row = Assert.IsType<
            RecapGridStoreReadResult<RecapRowView>.Found>(
            reader.ReadView(fixture.PriorResult)).Value;
        Assert.Equal(fixture.PriorResult, row.Id);
        Assert.Equal(fixture.RefId, row.RefId);
        Assert.Equal(fixture.TimelineId, row.TimelineId);
        Assert.Equal(fixture.PriorHistory, row.HistoryRowId);
        Assert.Equal(fixture.Recipe.Digest, row.RecipeDigest);
        Assert.Equal(fixture.Recipe.Target.Digest, row.TargetDigest);
        Assert.Equal(
            fixture.PriorCells.Select(static cell => cell.Id),
            row.OrderedCells.Select(static member => member.CellId));
        for (int index = 0; index < fixture.PriorCells.Count; index++) {
            V4CellExpectation expected = fixture.PriorCells[index];
            RecapCellArtifact cell = Assert.IsType<
                RecapGridStoreReadResult<RecapCellArtifact>.Found>(
                reader.ReadCell(expected.Id)).Value;
            Assert.Equal(expected.Id, cell.Id);
            Assert.Equal(fixture.Recipe.Digest,
                cell.Slot.RecipeDigest);
            Assert.Equal(fixture.PriorHistory,
                cell.Slot.HistoryRowId);
            Assert.Equal(priorWork.WorkId, cell.Slot.WorkId);
            Assert.Equal(expected.Column.LogicalColumnId,
                cell.LogicalColumnId);
            Assert.Equal(expected.Column.DefinitionDigest,
                cell.DefinitionDigest);
            Assert.Equal(expected.Content, cell.Content);
            Assert.Equal(expected.Column.LogicalColumnId,
                row.OrderedCells[index].LogicalColumnId);
            Assert.Equal(expected.Column.DefinitionDigest,
                row.OrderedCells[index].DefinitionDigest);
        }

        RowWork partialWork = Assert.IsType<
            RecapGridStoreReadResult<RowWork>.Found>(reader.ReadRowWork(
            new RowWorkKey(
                fixture.RefId,
                fixture.TimelineId,
                fixture.Recipe.Digest,
                fixture.PartialHistory))).Value;
        Assert.Equal(fixture.RefId, partialWork.Key.RefId);
        Assert.Equal(fixture.TimelineId, partialWork.Key.TimelineId);
        Assert.Equal(fixture.Recipe.Digest,
            partialWork.Key.RootRecipeDigest);
        Assert.Equal(fixture.PartialHistory,
            partialWork.Key.HistoryRowId);
        Assert.Equal(fixture.Recipe.Target.Digest,
            partialWork.ProducerTarget.Digest);
        Assert.Equal(fixture.PriorHistory,
            partialWork.PreviousHistoryRowId);
        Assert.Equal(fixture.PriorResult,
            partialWork.PreviousRowResultId);
        Assert.All(partialWork.OrderedAssignments,
            static assignment => Assert.True(assignment.IsEvaluate));
        Assert.NotEqual(priorWork.WorkId, partialWork.WorkId);

        RecapCellArtifact partialCell = Assert.IsType<
            RecapGridStoreReadResult<RecapCellArtifact>.Found>(
            reader.ReadCell(fixture.PartialCell.Id)).Value;
        Assert.Equal(fixture.PartialCell.Id, partialCell.Id);
        Assert.Equal(fixture.Recipe.Digest,
            partialCell.Slot.RecipeDigest);
        Assert.Equal(fixture.PartialHistory,
            partialCell.Slot.HistoryRowId);
        Assert.Equal(partialWork.WorkId, partialCell.Slot.WorkId);
        Assert.Equal(fixture.PartialCell.Column.LogicalColumnId,
            partialCell.LogicalColumnId);
        Assert.Equal(fixture.PartialCell.Column.DefinitionDigest,
            partialCell.DefinitionDigest);
        Assert.Equal(fixture.PartialCell.Content, partialCell.Content);
    }

    private void AssertExactControlScope(V4MultiRefScopeFixture fixture) {
        using RecapGridControlReaderHandle handle = Assert.IsType<
            RecapGridControlReaderOpenResult.Opened>(
            RecapGridControlMaintenance.OpenExactReader(
                _root, fixture.RefId, fixture.TimelineId)).Handle;
        RecapGridControlSnapshot snapshot = Assert.IsType<
            RecapGridControlSnapshotResult.Available>(
            handle.Reader.ReadSnapshot()).Snapshot;
        Assert.Equal(fixture.ActiveRecipeDigest,
            snapshot.Head.ActiveRecipeDigest);
        Assert.Equal(fixture.Recipe.Digest,
            snapshot.ActiveRecipe?.Recipe.Digest);
        Assert.Equal(fixture.Recipe.Target.Digest,
            snapshot.ActiveRecipe?.Recipe.Target.Digest);
        Dictionary<string, string> actualHeadings = snapshot.Definitions
            .ToDictionary(
                static definition => definition.LogicalColumnId.Value,
                static definition => definition.Target.SemanticHeading,
                StringComparer.Ordinal);
        Assert.Equal(fixture.Headings.Count, actualHeadings.Count);
        foreach ((string logicalColumn, string heading) in fixture.Headings) {
            Assert.Equal(heading, actualHeadings[logicalColumn]);
        }
    }

    private void AssertExportedMultiRefWork(
        IReadOnlyList<V4MultiRefScopeFixture> fixtures) {
        JsonElement[] exported = DrainStoreExportContent()
            .Where(static item => item.GetProperty("Kind").GetString()
                == "row-work")
            .Select(DecodeExportedJson)
            .ToArray();
        foreach (V4MultiRefScopeFixture fixture in fixtures) {
            JsonElement work = Assert.Single(exported, item =>
                item.GetProperty("refId").GetUInt64()
                    == PackedRef(fixture.RefId)
                && item.GetProperty("historyRowId").GetString()
                    == fixture.PartialHistory.Value);
            Assert.Equal(fixture.Recipe.Digest.Value,
                work.GetProperty("rootRecipeDigest").GetString());
            Assert.Equal(fixture.Recipe.Target.Digest.Value,
                work.GetProperty("producerTarget")
                    .GetProperty("digest").GetString());
            Assert.Equal(fixture.PriorHistory.Value,
                work.GetProperty("previousHistoryRowId").GetString());
            Assert.Equal(fixture.PriorResult.Value,
                work.GetProperty("previousRowResultId").GetString());
            Assert.Equal(
                fixture.Recipe.Target.OrderedColumns.Select(
                    static column => column.DefinitionDigest.Value),
                work.GetProperty("producerTarget")
                    .GetProperty("orderedColumns")
                    .EnumerateArray()
                    .Select(static column => column
                        .GetProperty("definitionDigest").GetString()));
        }
        Assert.Equal(
            fixtures.Select(static fixture => PackedRef(fixture.RefId))
                .Order(),
            exported.Where(item => fixtures.Any(fixture =>
                    fixture.PartialHistory.Value
                        == item.GetProperty("historyRowId").GetString()))
                .Select(static item => item.GetProperty("refId").GetUInt64())
                .Order());

        static JsonElement DecodeExportedJson(JsonElement item) {
            using JsonDocument document = JsonDocument.Parse(
                Convert.FromBase64String(
                    item.GetProperty("jsonBase64").GetString()!));
            return document.RootElement.Clone();
        }

        static ulong PackedRef(RefId refId) => Convert.ToUInt64(
            refId.ToHexString(),
            16);
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

    private sealed record V4MultiRefFixture(
        IReadOnlyList<V4MultiRefScopeFixture> Scopes);

    private sealed record V4MultiRefScopeSeed(
        string Label,
        RefId RefId,
        TimelineId TimelineId,
        GridBuildRecipe Recipe,
        HistoryRowId PriorHistory,
        HistoryRowId PartialHistory,
        GridBuildRecipeDigest ActiveRecipeDigest);

    private sealed record V4MultiRefScopeFixture(
        string Label,
        RefId RefId,
        TimelineId TimelineId,
        GridBuildRecipe Recipe,
        HistoryRowId PriorHistory,
        HistoryRowId PartialHistory,
        RowResultId PriorResult,
        IReadOnlyList<V4CellExpectation> PriorCells,
        V4CellExpectation PartialCell,
        GridBuildRecipeDigest ActiveRecipeDigest,
        IReadOnlyDictionary<string, string> Headings);

    private sealed record V4CellExpectation(
        CellId Id,
        BuildTargetColumn Column,
        string Content);
}
