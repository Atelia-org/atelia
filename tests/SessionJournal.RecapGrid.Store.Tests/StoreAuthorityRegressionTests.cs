using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid.Store;
using Atelia.EventJournal;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
            0,
            HistoryTimelineSelectedPath.EmptyDigest,
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

    [Fact]
    public void CreatedStoreMatchesIndependentV2LogicalSchemaFingerprint() {
        Create();

        using SqliteConnection connection = OpenRaw();
        connection.Open();
        var transcript = new StringBuilder();
        using (SqliteCommand identity = connection.CreateCommand()) {
            identity.CommandText = """
                SELECT
                    (SELECT application_id FROM pragma_application_id),
                    (SELECT user_version FROM pragma_user_version),
                    singleton,
                    schema_version,
                    length(store_instance_id),
                    store_instance_id = lower(store_instance_id),
                    store_instance_id NOT GLOB '*[^0-9a-f]*',
                    cell_count,
                    row_view_count,
                    row_view_member_count,
                    fulfilled_view_count
                FROM store_metadata;
                """;
            using SqliteDataReader reader = identity.ExecuteReader();
            Assert.True(reader.Read());
            long[] identityShape = [
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetInt64(6),
                reader.GetInt64(7),
                reader.GetInt64(8),
                reader.GetInt64(9),
                reader.GetInt64(10)
            ];
            Assert.Equal(new long[] {
                1_096_042_322L,
                2,
                1,
                2,
                32,
                1,
                1,
                0,
                0,
                0,
                0
            }, identityShape);
            AppendFingerprintField(
                transcript,
                reader.GetInt64(0).ToString(CultureInfo.InvariantCulture)
            );
            AppendFingerprintField(
                transcript,
                reader.GetInt64(1).ToString(CultureInfo.InvariantCulture)
            );
            for (int index = 2; index < 11; index++) {
                AppendFingerprintField(
                    transcript,
                    reader.GetInt64(index).ToString(
                        CultureInfo.InvariantCulture
                    )
                );
            }
            Assert.False(reader.Read());
        }

        using (SqliteCommand persistentPragmas = connection.CreateCommand()) {
            persistentPragmas.CommandText = """
                SELECT
                    (SELECT page_size FROM pragma_page_size),
                    (SELECT journal_mode FROM pragma_journal_mode);
                """;
            using SqliteDataReader reader = persistentPragmas.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(4096, reader.GetInt64(0));
            Assert.Equal("delete", reader.GetString(1));
            Assert.False(reader.Read());
        }

        var names = new List<string>();
        using (SqliteCommand schema = connection.CreateCommand()) {
            schema.CommandText = """
                SELECT type, name, tbl_name, sql
                FROM sqlite_schema
                WHERE name NOT LIKE 'sqlite_%'
                ORDER BY type, name;
                """;
            using SqliteDataReader reader = schema.ExecuteReader();
            while (reader.Read()) {
                names.Add(reader.GetString(1));
                for (int index = 0; index < 4; index++) {
                    AppendFingerprintField(
                        transcript,
                        reader.GetString(index)
                    );
                }
            }
        }

        Assert.Equal(new[] {
            "cell_artifact",
            "fulfilled_view_ref",
            "row_view",
            "row_view_member",
            "store_metadata"
        }, names);
        Assert.Equal(
            "3b14f5e58f4012f699b9314b96f145dc43e878fbbc7e8d25574991319281343c",
            Convert.ToHexStringLower(SHA256.HashData(
                Encoding.UTF8.GetBytes(transcript.ToString())
            ))
        );
    }

    [Theory]
    [InlineData("user-version-priority", true)]
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
        Assert.IsType<RecapGridStoreCreateResult.Created>(
            RecapGridStoreFactory.Create(_root)
        );
    }

    private RecapGridStoreHandle Open() => Assert.IsType<
        RecapGridStoreOpenResult.Opened
    >(RecapGridStoreFactory.Open(_root)).Handle;

    private SqliteConnection OpenRaw() => new(
        $"Data Source={new StorePaths(_root).DatabasePath};Mode=ReadWrite;Pooling=False;Foreign Keys=False"
    );

    private static void AppendFingerprintField(
        StringBuilder transcript,
        string value
    ) => transcript.Append(Encoding.UTF8.GetByteCount(value))
        .Append(':')
        .Append(value);

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
            "application-id" => "PRAGMA application_id = 0;",
            "metadata-absent" => "DELETE FROM store_metadata;",
            "metadata-duplicate" => """
                PRAGMA ignore_check_constraints = ON;
                INSERT INTO store_metadata(
                    singleton, schema_version, store_instance_id,
                    cell_count, row_view_count,
                    row_view_member_count, fulfilled_view_count
                ) VALUES (2, 2, '00112233445566778899aabbccddeeff',
                    0, 0, 0, 0);
                """,
            "metadata-singleton" => """
                PRAGMA ignore_check_constraints = ON;
                UPDATE store_metadata SET singleton = 2;
                """,
            "metadata-schema-version" => """
                PRAGMA ignore_check_constraints = ON;
                UPDATE store_metadata SET schema_version = 3;
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

    private void Corrupt(string sql, string value) {
        using SqliteConnection connection = OpenRaw();
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
            0,
            HistoryTimelineSelectedPath.EmptyDigest,
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
