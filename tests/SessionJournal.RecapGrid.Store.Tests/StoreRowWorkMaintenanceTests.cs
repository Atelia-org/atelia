using System.Diagnostics;
using System.Text.Json;
using Atelia.SessionJournal.HistoryTimeline;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace Atelia.SessionJournal.RecapGrid.Store.Tests;

public sealed class StoreRowWorkMaintenanceTests : IDisposable {
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "atelia-recap-grid-row-work-maintenance",
        Guid.NewGuid().ToString("N")
    );
    private readonly ITestOutputHelper _output;

    public StoreRowWorkMaintenanceTests(ITestOutputHelper output) {
        _output = output;
    }

    [Fact]
    public void VerifyAndExportCloseMixedWorkProducerPriorAndReuseFacts() {
        MixedFixture fixture = CreateMixedFixture();

        Assert.IsType<RecapGridStoreVerifyResult.Healthy>(
            RecapGridStoreMaintenance.Verify(_root)
        );

        IReadOnlyList<RecapGridStoreExportItem> hidden = DrainExport(
            includeContent: false
        );
        Assert.All(hidden, static item => Assert.Null(item.Json));
        Assert.Equal(
            4,
            hidden.Count(static item => item.Kind == "row-work")
        );
        Assert.All(
            hidden.SkipWhile(static item => item.Kind != "row-work"),
            static item => Assert.Equal("row-work", item.Kind)
        );

        IReadOnlyList<RecapGridStoreExportItem> revealed = DrainExport(
            includeContent: true
        );
        RecapGridStoreExportItem overlayItem = Assert.Single(
            revealed,
            item => item.Kind == "row-work"
                && item.Key == fixture.Overlay.Work!.WorkId.Value
        );
        using JsonDocument overlay = JsonDocument.Parse(
            Assert.IsType<byte[]>(overlayItem.Json)
        );
        JsonElement root = overlay.RootElement;
        Assert.Equal(fixture.Overlay.Work!.WorkId.Value,
            root.GetProperty("workId").GetString());
        Assert.Equal(fixture.Overlay.Recipe.Target.Digest.Value,
            root.GetProperty("producerTarget").GetProperty("digest")
                .GetString());
        JsonElement[] assignments = root.GetProperty("orderedAssignments")
            .EnumerateArray().ToArray();
        Assert.Equal("evaluate", assignments[0].GetProperty("kind").GetString());
        Assert.Equal("reuse", assignments[1].GetProperty("kind").GetString());
        Assert.Equal(fixture.BaseTwo.Id.Value,
            assignments[1].GetProperty("reusedCellId").GetString());

        RecapGridStoreExportItem partialItem = Assert.Single(
            revealed,
            item => item.Kind == "row-work"
                && item.Key == fixture.Partial.Work!.WorkId.Value
        );
        using JsonDocument partial = JsonDocument.Parse(
            Assert.IsType<byte[]>(partialItem.Json)
        );
        Assert.Equal(fixture.Full.HistoryRowId.Value,
            partial.RootElement.GetProperty("previousHistoryRowId").GetString());
        Assert.Equal(fixture.FullRow.Id.Value,
            partial.RootElement.GetProperty("previousRowResultId").GetString());

        RecapGridStoreExportItem cellItem = Assert.Single(
            revealed,
            item => item.Kind == "cell" && item.Key == fixture.OverlayOne.Id.Value
        );
        using JsonDocument cell = JsonDocument.Parse(
            Assert.IsType<byte[]>(cellItem.Json)
        );
        Assert.Equal(fixture.Overlay.Work.WorkId.Value,
            cell.RootElement.GetProperty("slot").GetProperty("workId").GetString());

        RecapGridStoreExportItem rowItem = Assert.Single(
            revealed,
            item => item.Kind == "row-view" && item.Key == fixture.OverlayRow.Id.Value
        );
        using JsonDocument row = JsonDocument.Parse(
            Assert.IsType<byte[]>(rowItem.Json)
        );
        Assert.Equal(fixture.Overlay.Work.WorkId.Value,
            row.RootElement.GetProperty("workId").GetString());
    }

    [Theory]
    [InlineData("physical-canonical")]
    [InlineData("member")]
    [InlineData("prior")]
    [InlineData("cell-wrong-work")]
    [InlineData("cell-reuse-work")]
    [InlineData("cell-null-work")]
    [InlineData("row-wrong-work")]
    [InlineData("row-target")]
    [InlineData("row-null-work")]
    public void VerifyRejectsEveryRowWorkAuthorityCorruption(string corruption) {
        MixedFixture fixture = CreateMixedFixture();
        Execute(corruption switch {
            "physical-canonical" => $"UPDATE row_work SET producer_target=x'00' WHERE work_id='{fixture.Overlay.Work!.WorkId.Value}';",
            "member" => $"UPDATE row_work_member SET definition_digest='{new string('b', 64)}' WHERE work_id='{fixture.Overlay.Work!.WorkId.Value}' AND column_ordinal=1;",
            "prior" => $"UPDATE row_work SET previous_row_result_id='{new string('f', 32)}' WHERE work_id='{fixture.Partial.Work!.WorkId.Value}';",
            "cell-wrong-work" => $"UPDATE cell_artifact SET work_id='{fixture.ZeroCell.Work!.WorkId.Value}' WHERE cell_id='{fixture.OverlayOne.Id.Value}';",
            "cell-reuse-work" => $"UPDATE cell_artifact SET work_id='{fixture.Overlay.Work!.WorkId.Value}' WHERE cell_id='{fixture.BaseTwo.Id.Value}';",
            "cell-null-work" => $"UPDATE cell_artifact SET work_id=NULL WHERE cell_id='{fixture.OverlayOne.Id.Value}';",
            "row-wrong-work" => $"UPDATE row_view SET work_id='{fixture.Full.Work!.WorkId.Value}' WHERE row_result_id='{fixture.OverlayRow.Id.Value}';",
            "row-target" => $"UPDATE row_view SET target_digest='{new string('b', 64)}' WHERE row_result_id='{fixture.OverlayRow.Id.Value}';",
            "row-null-work" => $"UPDATE row_view SET work_id=NULL WHERE row_result_id='{fixture.OverlayRow.Id.Value}';",
            _ => throw new ArgumentOutOfRangeException(nameof(corruption))
        });

        Assert.IsType<RecapGridStoreVerifyResult.Unhealthy>(
            RecapGridStoreMaintenance.Verify(_root)
        );
        Assert.IsType<RecapGridStoreExportResult.Invalid>(
            RecapGridStoreMaintenance.Export(_root)
        );
    }

    [Fact]
    public void RowViewPhaseResumeRejectsEvaluateCellWithWrongRoot() {
        MixedFixture fixture = CreateMixedFixture();
        Execute($"UPDATE cell_artifact SET recipe_digest='{new string('b', 64)}' WHERE cell_id='{fixture.OverlayOne.Id.Value}';");

        RecapGridStoreExportResult result = RecapGridStoreMaintenance.Export(
            _root,
            RecapGridStoreExportCursor.CreateId(
                "row-view",
                new string('0', 32)
            )
        );

        Assert.IsType<RecapGridStoreExportResult.Invalid>(result);
    }

    [Fact]
    public void RowWorkPhaseResumeRejectsReuseSourceWithoutOwnWork() {
        MixedFixture fixture = CreateMixedFixture();
        Execute($"UPDATE cell_artifact SET work_id=NULL WHERE cell_id='{fixture.BaseTwo.Id.Value}';");

        RecapGridStoreExportResult result = RecapGridStoreMaintenance.Export(
            _root,
            RecapGridStoreExportCursor.CreateRowWork(new string('0', 64))
        );

        Assert.IsType<RecapGridStoreExportResult.Invalid>(result);
    }

    [Fact]
    public void ExportPagesAll257ZeroCellWorksWithTypedV2Cursor() {
        Create();
        using (RecapGridStoreHandle handle = Open()) {
            for (int index = 1; index <= 257; index++) {
                RowBuildSpec spec = StoreFixture.Spec(
                    row: new HistoryRowId(index.ToString("x64"))
                );
                StoreFixture.PutWork(handle, spec);
            }
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        RecapGridStoreExportCursor? cursor = null;
        int pages = 0;
        do {
            RecapGridStoreExportPage page = Assert.IsType<
                RecapGridStoreExportResult.Page
            >(RecapGridStoreMaintenance.Export(_root, cursor)).Value;
            pages++;
            Assert.All(page.Items,
                static item => Assert.Equal("row-work", item.Kind));
            foreach (RecapGridStoreExportItem item in page.Items) {
                Assert.True(seen.Add(item.Key));
            }
            if (!page.Incomplete) {
                Assert.Null(page.NextCursor);
                break;
            }
            cursor = RecapGridStoreExportCursor.Parse(
                Assert.IsType<RecapGridStoreExportCursor>(page.NextCursor).Value
            );
            Assert.True(cursor.IsRowWork);
        } while (true);

        Assert.Equal(3, pages);
        Assert.Equal(257, seen.Count);
        Assert.IsType<RecapGridStoreVerifyResult.Healthy>(
            RecapGridStoreMaintenance.Verify(_root)
        );
    }

    [Fact]
    public void CursorV2RoundTripsNewRowWorkAndUnchangedExistingKinds() {
        RecapGridStoreExportCursor[] cursors = [
            RecapGridStoreExportCursor.CreateId("cell", new string('a', 32)),
            RecapGridStoreExportCursor.CreateId("row-view", new string('b', 32)),
            RecapGridStoreExportCursor.CreateFulfilled(
                new string('c', 16),
                new string('d', 32),
                7,
                new string('e', 64),
                new string('f', 64)
            ),
            RecapGridStoreExportCursor.CreateRowWork(new string('1', 64))
        ];

        foreach (RecapGridStoreExportCursor cursor in cursors) {
            Assert.Equal(cursor,
                RecapGridStoreExportCursor.Parse(cursor.Value));
        }
        Assert.Equal(34, Decode(cursors[0]).Length);
        Assert.Equal(34, Decode(cursors[1]).Length);
        Assert.Equal(186, Decode(cursors[2]).Length);
        Assert.Equal(66, Decode(cursors[3]).Length);
        Assert.All(cursors, static cursor => Assert.Equal(2, Decode(cursor)[0]));
    }

    [Theory]
    [InlineData(
        "AgFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYQ",
        1,
        34
    )]
    [InlineData(
        "AgJiYmJiYmJiYmJiYmJiYmJiYmJiYmJiYmJiYmJiYmJiYg",
        2,
        34
    )]
    [InlineData(
        "AgNjY2NjY2NjY2NjY2NjY2NjZGRkZGRkZGRkZGRkZGRkZGRkZGRkZGRkZGRkZGRkZGQAAAAAAAAAB2VlZWVlZWVlZWVlZWVlZWVlZWVlZWVlZWVlZWVlZWVlZWVlZWVlZWVlZWVlZWVlZWVlZWVlZWVlZWVlZWVlZWVmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZm",
        3,
        186
    )]
    public void ParsesPinnedPreRowWorkV2Cursor(
        string wire,
        int expectedKind,
        int expectedLength
    ) {
        RecapGridStoreExportCursor cursor =
            RecapGridStoreExportCursor.Parse(wire);

        Assert.Equal(wire, cursor.Value);
        Assert.Equal(expectedKind, Decode(cursor)[1]);
        Assert.Equal(expectedLength, Decode(cursor).Length);
    }

    [Fact]
    public void VerifyAndDrainExport4097ActualWorkCellRows() {
        const int rowCount = 4097;
        Create();
        InsertScaleRows(rowCount);

        Stopwatch verify = Stopwatch.StartNew();
        Assert.IsType<RecapGridStoreVerifyResult.Healthy>(
            RecapGridStoreMaintenance.Verify(_root)
        );
        verify.Stop();

        Stopwatch export = Stopwatch.StartNew();
        IReadOnlyList<RecapGridStoreExportItem> items = DrainExport(
            includeContent: false
        );
        export.Stop();

        Assert.Equal(rowCount,
            items.Count(static item => item.Kind == "row-work"));
        Assert.Equal(rowCount,
            items.Count(static item => item.Kind == "cell"));
        Assert.Equal(rowCount,
            items.Count(static item => item.Kind == "row-view"));
        Assert.DoesNotContain(items,
            static item => item.Kind == "fulfilled");
        _output.WriteLine(
            "4097 actual rows: verify={0} ms; drain export={1} ms; items={2}",
            verify.ElapsedMilliseconds,
            export.ElapsedMilliseconds,
            items.Count
        );
    }

    private MixedFixture CreateMixedFixture() {
        Create();
        using RecapGridStoreHandle handle = Open();
        RowBuildSpec full = StoreFixture.Spec(StoreFixture.Recipe(columns: 2));
        RecapCellArtifact baseOne = StoreFixture.Put(handle, full, "base one", 0);
        RecapCellArtifact baseTwo = StoreFixture.Put(handle, full, "base two", 1);
        RecapRowView fullRow = Assert.IsType<RecapGridRowViewPutResult.Inserted>(
            handle.Writer.PutRowView(full, [baseOne, baseTwo])
        ).Winner;

        GridBuildRecipe overlayRecipe = GridBuildRecipe.CreateOverlay(
            full.Recipe,
            full.HistoryRowId,
            full.Recipe.Target,
            [StoreFixture.Column]
        );
        RowBuildSpec overlay = StoreFixture.WithWork(
            RowBuildSpec.CreateOverlayBootstrap(
                overlayRecipe,
                new RowViewCoordinate(
                    full.RefId,
                    full.TimelineId,
                    full.HistoryRowId,
                    overlayRecipe.Digest,
                    overlayRecipe.Target.Digest,
                    null,
                    null,
                    bootstrapCompleted: true
                ),
                [
                    new RowBuildAssignment.Evaluate(new CellSlot(
                        overlayRecipe.Digest,
                        full.HistoryRowId,
                        StoreFixture.Column
                    )),
                    new RowBuildAssignment.Reuse(baseTwo.LogicalColumnId, baseTwo)
                ]
            )
        );
        RecapCellArtifact overlayOne = StoreFixture.Put(
            handle,
            overlay,
            "overlay one"
        );
        RecapRowView overlayRow = Assert.IsType<RecapGridRowViewPutResult.Inserted>(
            handle.Writer.PutRowView(overlay, [overlayOne, baseTwo])
        ).Winner;

        RowBuildSpec partial = StoreFixture.Spec(
            full.Recipe,
            new HistoryRowId(new string('d', 64)),
            fullRow
        );
        _ = StoreFixture.Put(handle, partial, "partial", 0);
        RowBuildSpec zeroCell = StoreFixture.Spec(
            full.Recipe,
            new HistoryRowId(new string('e', 64)),
            fullRow
        );
        StoreFixture.PutWork(handle, zeroCell);

        return new MixedFixture(
            full,
            fullRow,
            baseTwo,
            overlay,
            overlayOne,
            overlayRow,
            partial,
            zeroCell
        );
    }

    private IReadOnlyList<RecapGridStoreExportItem> DrainExport(
        bool includeContent
    ) {
        var items = new List<RecapGridStoreExportItem>();
        RecapGridStoreExportCursor? cursor = null;
        do {
            RecapGridStoreExportPage page = Assert.IsType<
                RecapGridStoreExportResult.Page
            >(RecapGridStoreMaintenance.Export(
                _root,
                cursor,
                includeContent
            )).Value;
            items.AddRange(page.Items);
            if (!page.Incomplete) {
                break;
            }
            cursor = RecapGridStoreExportCursor.Parse(
                Assert.IsType<RecapGridStoreExportCursor>(page.NextCursor).Value
            );
        } while (true);
        return items;
    }

    private void InsertScaleRows(int count) {
        GridBuildRecipe recipe = StoreFixture.Recipe();
        Directory.CreateDirectory(_root);
        using var connection = new SqliteConnection(
            $"Data Source={new StorePaths(_root).DatabasePath};Mode=ReadWrite;Pooling=False"
        );
        connection.Open();
        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO row_work(work_id,ref_id,timeline_id,root_recipe_digest,
                history_row_id,previous_history_row_id,previous_row_result_id,
                producer_target,canonical)
            VALUES($work,$ref,$timeline,$root,$history,NULL,NULL,$target,$canonical);
            INSERT INTO row_work_member(work_id,column_ordinal,logical_column_id,
                definition_digest,reused_cell_id)
            VALUES($work,0,$column,$definition,NULL);
            INSERT INTO cell_artifact(cell_id,recipe_digest,history_row_id,work_id,
                logical_column_id,definition_digest,outcome,content)
            VALUES($cell,$root,$history,$work,$column,$definition,0,'scale');
            INSERT INTO row_view(row_result_id,ref_id,timeline_id,history_row_id,
                recipe_digest,target_digest,work_id,previous_history_row_id,
                previous_row_result_id,bootstrap_completed)
            VALUES($row,$ref,$timeline,$history,$root,$targetDigest,$work,NULL,NULL,1);
            INSERT INTO row_view_member(row_result_id,column_ordinal,
                logical_column_id,definition_digest,cell_id)
            VALUES($row,0,$column,$definition,$cell);
            """;
        SqliteParameter work = command.Parameters.Add("$work", SqliteType.Text);
        SqliteParameter history = command.Parameters.Add(
            "$history",
            SqliteType.Text
        );
        SqliteParameter canonical = command.Parameters.Add(
            "$canonical",
            SqliteType.Blob
        );
        SqliteParameter cell = command.Parameters.Add("$cell", SqliteType.Text);
        SqliteParameter row = command.Parameters.Add("$row", SqliteType.Text);
        command.Parameters.AddWithValue("$ref", new Atelia.EventJournal.RefId(1).ToHexString());
        command.Parameters.AddWithValue("$timeline", StoreFixture.Timeline.Value);
        command.Parameters.AddWithValue("$root", recipe.Digest.Value);
        command.Parameters.AddWithValue("$target", recipe.Target.ToCanonicalBytes());
        command.Parameters.AddWithValue("$targetDigest", recipe.Target.Digest.Value);
        command.Parameters.AddWithValue("$column", StoreFixture.Column.Value);
        command.Parameters.AddWithValue("$definition", StoreFixture.Definition.Value);

        for (int index = 1; index <= count; index++) {
            var historyRowId = new HistoryRowId(index.ToString("x64"));
            var rowWork = new RowWork(
                new RowWorkKey(
                    new Atelia.EventJournal.RefId(1),
                    StoreFixture.Timeline,
                    recipe.Digest,
                    historyRowId
                ),
                recipe.Target,
                previousHistoryRowId: null,
                previousRowResultId: null,
                [new RowWorkAssignment(StoreFixture.Column, null)]
            );
            work.Value = rowWork.WorkId.Value;
            history.Value = historyRowId.Value;
            canonical.Value = rowWork.ToCanonicalBytes();
            cell.Value = index.ToString("x32");
            row.Value = (index + count).ToString("x32");
            command.ExecuteNonQuery();
        }

        using SqliteCommand counts = connection.CreateCommand();
        counts.Transaction = transaction;
        counts.CommandText = """
            UPDATE store_metadata
            SET cell_count=$count,row_view_count=$count,
                row_view_member_count=$count;
            """;
        counts.Parameters.AddWithValue("$count", count);
        counts.ExecuteNonQuery();
        transaction.Commit();
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

    private void Execute(string sql) {
        using var connection = new SqliteConnection(
            $"Data Source={new StorePaths(_root).DatabasePath};Mode=ReadWrite;Pooling=False"
        );
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=OFF; " + sql;
        command.ExecuteNonQuery();
    }

    private static byte[] Decode(RecapGridStoreExportCursor cursor) {
        string padded = cursor.Value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        return Convert.FromBase64String(padded);
    }

    public void Dispose() {
        try {
            if (Directory.Exists(_root)) {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record MixedFixture(
        RowBuildSpec Full,
        RecapRowView FullRow,
        RecapCellArtifact BaseTwo,
        RowBuildSpec Overlay,
        RecapCellArtifact OverlayOne,
        RecapRowView OverlayRow,
        RowBuildSpec Partial,
        RowBuildSpec ZeroCell
    );
}
