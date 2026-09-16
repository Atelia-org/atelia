using Atelia.EventJournal;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid.Store;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Atelia.SessionJournal.RecapGrid.Store.Tests;

public sealed class StoreMaintenanceAndFailureTests : IDisposable {
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "atelia-recap-grid-store-maintenance-tests",
        Guid.NewGuid().ToString("N")
    );

    [Fact]
    public void InspectVerifyAndResetRequireExactExclusiveWitness() {
        Directory.CreateDirectory(_root);
        RecapGridStoreCreateResult.Created created = Assert.IsType<
            RecapGridStoreCreateResult.Created
        >(RecapGridStoreFactory.Create(_root));

        using (RecapGridStoreHandle open = Assert.IsType<
               RecapGridStoreOpenResult.Opened
               >(RecapGridStoreFactory.Open(_root)).Handle) {
            Assert.IsType<RecapGridStoreInspectResult.Available>(
                RecapGridStoreMaintenance.Inspect(_root)
            );
            Assert.IsType<RecapGridStoreVerifyResult.Healthy>(
                RecapGridStoreMaintenance.Verify(_root)
            );
            Assert.IsType<RecapGridStorePrepareResetResult.Busy>(
                RecapGridStoreMaintenance.PrepareReset(_root)
            );
        }

        RecapGridStorePhysicalWitness witness = Assert.IsType<
            RecapGridStorePrepareResetResult.Prepared
        >(RecapGridStoreMaintenance.PrepareReset(_root)).Witness;
        byte[] original = File.ReadAllBytes(
            new StorePaths(_root).DatabasePath
        );
        var wrong = new RecapGridStorePhysicalWitness(
            witness.Length,
            new string(witness.Sha256[0] == '0' ? '1' : '0', 64)
        );
        Assert.IsType<RecapGridStoreResetResult.StaleConfirmation>(
            RecapGridStoreMaintenance.Reset(_root, wrong)
        );
        Assert.Equal(original, File.ReadAllBytes(
            new StorePaths(_root).DatabasePath
        ));

        string outside = Path.Combine(_root, "raw-authority.marker");
        File.WriteAllText(outside, "unchanged");
        RecapGridStoreResetResult.Reset reset = Assert.IsType<
            RecapGridStoreResetResult.Reset
        >(RecapGridStoreMaintenance.Reset(_root, witness));
        Assert.NotEqual(created.Identity.InstanceId, reset.Identity.InstanceId);
        Assert.Equal("unchanged", File.ReadAllText(outside));
        RecapGridStoreInfo info = Assert.IsType<
            RecapGridStoreVerifyResult.Healthy
        >(RecapGridStoreMaintenance.Verify(_root)).Info;
        Assert.Equal(reset.Identity, info.Identity);
        Assert.Equal(0, info.CellCount);
        Assert.Equal(0, info.RowViewCount);
        Assert.Equal(0, info.FulfilledViewCount);
    }

    [Fact]
    public void ResetRejectsFixedSidecarAndPostPublishIsIndeterminate() {
        Directory.CreateDirectory(_root);
        Assert.IsType<RecapGridStoreCreateResult.Created>(
            RecapGridStoreFactory.Create(_root)
        );
        StorePaths paths = new(_root);
        File.WriteAllText(paths.JournalPath, "orphan");
        Assert.IsType<
            RecapGridStorePrepareResetResult.OfflineCleanupRequired
        >(RecapGridStoreMaintenance.PrepareReset(_root));
        File.Delete(paths.JournalPath);

        RecapGridStorePhysicalWitness witness = Assert.IsType<
            RecapGridStorePrepareResetResult.Prepared
        >(RecapGridStoreMaintenance.PrepareReset(_root)).Witness;
        string? recreatedTemporary = null;
        RecapGridStoreResetResult.CommitIndeterminate indeterminate =
            Assert.IsType<RecapGridStoreResetResult.CommitIndeterminate>(
                RecapGridStoreMaintenance.ResetForTest(
                    _root,
                    witness,
                    StoreStorageLimits.Production,
                    new StorePersistenceTestHooks(
                        AfterResetPublish: temporary => {
                            recreatedTemporary = temporary;
                            Directory.CreateDirectory(temporary);
                            throw new IOException("injected after publish");
                        }
                    )
                )
            );
        Assert.Equal(indeterminate.Intended, indeterminate.Observed);
        Assert.NotNull(recreatedTemporary);
        Assert.True(Directory.Exists(recreatedTemporary));
        Assert.IsType<RecapGridStoreVerifyResult.Healthy>(
            RecapGridStoreMaintenance.Verify(_root)
        );
    }

    [Fact]
    public void SymlinkRepositoryRootIsRejectedWithoutExternalMutation() {
        string external = _root + "-external";
        string linked = _root + "-linked";
        Directory.CreateDirectory(external);
        File.WriteAllText(Path.Combine(external, "authority.bin"), "exact");
        Directory.CreateSymbolicLink(linked, external);
        byte[] before = File.ReadAllBytes(
            Path.Combine(external, "authority.bin")
        );

        Assert.IsType<RecapGridStoreCreateResult.Invalid>(
            RecapGridStoreFactory.Create(linked)
        );
        Assert.IsType<RecapGridStoreInspectResult.Invalid>(
            RecapGridStoreMaintenance.Inspect(linked)
        );
        Assert.IsType<RecapGridStorePrepareResetResult.Invalid>(
            RecapGridStoreMaintenance.PrepareReset(linked)
        );
        Assert.IsType<RecapGridStoreResetResult.Invalid>(
            RecapGridStoreMaintenance.Reset(
                linked,
                new RecapGridStorePhysicalWitness(1, new string('0', 64))
            )
        );
        Assert.Equal(before, File.ReadAllBytes(
            Path.Combine(external, "authority.bin")
        ));
        Assert.False(Directory.Exists(Path.Combine(external, "derived")));
        Directory.Delete(linked);
        Directory.Delete(external, recursive: true);
    }

    [Fact]
    public void WriterContentionIsBoundedAndDoesNotLatchInvalid() {
        Directory.CreateDirectory(_root);
        Assert.IsType<RecapGridStoreCreateResult.Created>(
            RecapGridStoreFactory.Create(_root)
        );
        int retries = 0;
        StoreStorageLimits limits = StoreStorageLimits.Production with {
            MaximumCommitAttempts = 3,
            CommitRetryDelayMilliseconds = 0
        };
        using RecapGridStoreHandle handle = Assert.IsType<
            RecapGridStoreOpenResult.Opened
        >(RecapGridStoreFactory.OpenForTest(
            _root,
            limits,
            new StorePersistenceTestHooks(
                BeforeLocalCommitRetry: _ => retries++
            )
        )).Handle;
        using SqliteConnection blocker = OpenRaw();
        blocker.Open();
        using (SqliteCommand begin = blocker.CreateCommand()) {
            begin.CommandText = "BEGIN EXCLUSIVE;";
            begin.ExecuteNonQuery();
        }
        Assert.IsType<RecapGridStoreOpenResult.Busy>(
            RecapGridStoreFactory.Open(_root)
        );
        Assert.IsType<RecapGridStoreReaderOpenResult.Busy>(
            RecapGridStoreFactory.OpenReader(_root)
        );
        Assert.IsType<RecapGridStoreInspectResult.Busy>(
            RecapGridStoreMaintenance.Inspect(_root)
        );
        Assert.IsType<RecapGridStoreVerifyResult.Busy>(
            RecapGridStoreMaintenance.Verify(_root)
        );
        Assert.IsType<RecapGridStoreExportResult.Busy>(
            RecapGridStoreMaintenance.Export(_root)
        );
        Assert.IsType<RecapGridCellPutResult.Busy>(
            Put(handle, Cell('b', "answer"))
        );
        Assert.Equal(2, retries);
        using (SqliteCommand rollback = blocker.CreateCommand()) {
            rollback.CommandText = "ROLLBACK;";
            rollback.ExecuteNonQuery();
        }
        Assert.IsType<RecapGridCellPutResult.Inserted>(
            Put(handle, Cell('b', "answer"))
        );
    }

    [Fact]
    public async Task DisposeDrainsEnteredOperation() {
        Directory.CreateDirectory(_root);
        Assert.IsType<RecapGridStoreCreateResult.Created>(
            RecapGridStoreFactory.Create(_root)
        );
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        RecapGridStoreHandle draining = OpenWithHooks(
            new StorePersistenceTestHooks(
                BeforeCellBegin: () => {
                    entered.Set();
                    release.Wait();
                }
            )
        );
        Task<RecapGridCellPutResult> put = Task.Run(
            () => Put(draining, Cell('c', "drain"))
        );
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        Task dispose = Task.Run(draining.Dispose);
        Assert.NotSame(
            dispose,
            await Task.WhenAny(dispose, Task.Delay(100))
        );
        release.Set();
        await dispose.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsType<RecapGridCellPutResult.Inserted>(await put);
        Assert.IsType<RecapGridCellPutResult.Disposed>(
            Put(draining, Cell('d', "after dispose"))
        );
    }

    [Fact]
    public void InvalidSqlValueLatchesInvalidAndVerifyIsIncomplete() {
        Create();
        using RecapGridStoreHandle handle = OpenWithHooks(new StorePersistenceTestHooks());
        RecapCellArtifact cell = Assert.IsType<RecapGridCellPutResult.Inserted>(Put(handle, Cell('b', "answer"))).Winner;
        using (SqliteConnection connection = OpenRaw()) {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA ignore_check_constraints = ON; UPDATE cell_artifact SET outcome = 99;";
            command.ExecuteNonQuery();
        }
        var invalid = Assert.IsType<RecapGridStoreReadResult<RecapCellArtifact>.Invalid>(handle.Reader.TryReadCell(cell.Slot));
        Assert.Equal(invalid.Code, Assert.IsType<RecapGridCellPutResult.Invalid>(Put(handle, Cell('c', "other"))).Code);
        var unhealthy = Assert.IsType<RecapGridStoreVerifyResult.Unhealthy>(RecapGridStoreMaintenance.Verify(_root));
        Assert.True(unhealthy.Incomplete);
        Assert.NotEmpty(unhealthy.Errors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AllThreeCommitFailuresObserveBySlotAndAssignment(bool nativeReturn) {
        Create();
        RowBuildSpec spec = StoreFixture.Spec();
        static void Throw() => throw new IOException("injected after COMMIT");
        StorePersistenceTestHooks hooks = nativeReturn
            ? new(AfterCellNativeCommitReturn: Throw, AfterRowViewNativeCommitReturn: Throw, AfterFulfilledNativeCommitReturn: Throw)
            : new(AfterCellCommit: Throw, AfterRowViewCommit: Throw, AfterFulfilledCommit: Throw);
        using RecapGridStoreHandle handle = OpenWithHooks(hooks);
        StoreFixture.PutWork(handle, spec);
        var cellResult = Assert.IsType<RecapGridCellPutResult.CommitIndeterminate>(
            handle.Writer.PutCell(spec, StoreFixture.Draft(spec)));
        RecapCellArtifact cell = Assert.IsType<RecapCellArtifact>(cellResult.Observed);
        Assert.Equal(((RowBuildAssignment.Evaluate)spec.OrderedAssignments[0]).Slot, cellResult.IntendedSlot);
        Assert.Equal(cellResult.IntendedSlot, cell.Slot);
        Assert.Equal(cell, Assert.IsType<RecapGridCellPutResult.AlreadyFilled>(
            handle.Writer.PutCell(spec, StoreFixture.Draft(spec, "loser"))).Winner);
        var rowResult = Assert.IsType<RecapGridRowViewPutResult.CommitIndeterminate>(handle.Writer.PutRowView(spec, [cell]));
        RecapRowView view = Assert.IsType<RecapRowView>(rowResult.Observed);
        Assert.Equal(spec.Coordinate.AssignmentKey, rowResult.IntendedAssignment);
        Assert.Equal(view.Id, Assert.IsType<RecapGridRowViewPutResult.AlreadyPresent>(handle.Writer.PutRowView(spec, [cell])).Winner.Id);
        Assert.Equal(view.Id, Assert.IsType<RecapGridFulfilledPutResult.CommitIndeterminate>(
            handle.Writer.PutFulfilled(StoreFixture.Fulfilled(spec), view.Id)).Observed);
    }

    [Fact]
    public async Task ConcurrentDifferentContentSettlesToOneStoredWinner() {
        Create();
        RowBuildSpec spec = StoreFixture.Spec();
        using var barrier = new Barrier(2);
        int firstOne = 0, firstTwo = 0;
        using RecapGridStoreHandle one = OpenWithHooks(new StorePersistenceTestHooks(BeforeCellBegin: () => {
            if (Interlocked.Exchange(ref firstOne, 1) == 0) barrier.SignalAndWait();
        }));
        using RecapGridStoreHandle two = OpenWithHooks(new StorePersistenceTestHooks(BeforeCellBegin: () => {
            if (Interlocked.Exchange(ref firstTwo, 1) == 0) barrier.SignalAndWait();
        }));
        StoreFixture.PutWork(one, spec);
        RecapGridCellPutResult[] results = await Task.WhenAll(
            Task.Run(() => one.Writer.PutCell(spec, StoreFixture.Draft(spec, "one"))),
            Task.Run(() => two.Writer.PutCell(spec, StoreFixture.Draft(spec, "two"))));
        RecapCellArtifact winner = Assert.Single(results.OfType<RecapGridCellPutResult.Inserted>()).Winner;
        for (int index = 0; index < results.Length; index++) {
            RecapGridCellPutResult result = results[index];
            if (result is RecapGridCellPutResult.Inserted) continue;
            if (result is RecapGridCellPutResult.Busy) result = (index == 0 ? one : two).Writer.PutCell(spec, StoreFixture.Draft(spec, "retry"));
            Assert.Equal(winner, Assert.IsType<RecapGridCellPutResult.AlreadyFilled>(result).Winner);
        }
        RecapRowView row = Assert.IsType<RecapGridRowViewPutResult.Inserted>(one.Writer.PutRowView(spec, [winner])).Winner;
        Assert.Equal(row.Id, Assert.IsType<RecapGridRowViewPutResult.AlreadyPresent>(two.Writer.PutRowView(spec, [winner])).Winner.Id);
    }

    [Fact]
    public async Task ConcurrentRowPublicationReturnsActualStoredRow() {
        Create();
        RowBuildSpec spec = StoreFixture.Spec();
        RecapCellArtifact cell;
        using (RecapGridStoreHandle setup = OpenWithHooks(new StorePersistenceTestHooks())) cell = StoreFixture.Put(setup, spec);
        using var barrier = new Barrier(2);
        int firstOne = 0, firstTwo = 0;
        using RecapGridStoreHandle one = OpenWithHooks(new StorePersistenceTestHooks(BeforeRowViewBegin: () => {
            if (Interlocked.Exchange(ref firstOne, 1) == 0) barrier.SignalAndWait();
        }));
        using RecapGridStoreHandle two = OpenWithHooks(new StorePersistenceTestHooks(BeforeRowViewBegin: () => {
            if (Interlocked.Exchange(ref firstTwo, 1) == 0) barrier.SignalAndWait();
        }));
        RecapGridRowViewPutResult[] results = await Task.WhenAll(
            Task.Run(() => one.Writer.PutRowView(spec, [cell])),
            Task.Run(() => two.Writer.PutRowView(spec, [cell])));
        RecapRowView winner = Assert.Single(results.OfType<RecapGridRowViewPutResult.Inserted>()).Winner;
        for (int index = 0; index < results.Length; index++) {
            RecapGridRowViewPutResult result = results[index];
            if (result is RecapGridRowViewPutResult.Inserted) continue;
            if (result is RecapGridRowViewPutResult.Busy) result = (index == 0 ? one : two).Writer.PutRowView(spec, [cell]);
            Assert.Equal(winner.Id, Assert.IsType<RecapGridRowViewPutResult.AlreadyPresent>(result).Winner.Id);
        }
    }

    [Fact]
    public void QueryPlansUseSlotAndResultIndexes() {
        Create();
        using SqliteConnection connection = OpenRaw();
        connection.Open();
        foreach (string sql in new[] {
            "SELECT content FROM cell_artifact WHERE recipe_digest = $key AND history_row_id = $key AND logical_column_id = $key",
            "SELECT cell_id FROM cell_artifact WHERE cell_id > $key ORDER BY cell_id LIMIT 129",
            "SELECT row_result_id FROM row_view WHERE row_result_id > $key ORDER BY row_result_id LIMIT 129",
            "SELECT row_result_id FROM fulfilled_view_ref WHERE ref_id = $key AND timeline_id = $key AND timeline_head_generation = 1 AND through_history_row_id = $key AND recipe_digest = $key",
            "SELECT ref_id FROM fulfilled_view_ref WHERE (ref_id, timeline_id, timeline_head_generation, through_history_row_id, recipe_digest) > ($key, $key, 0, $key, $key) ORDER BY ref_id, timeline_id, timeline_head_generation, through_history_row_id, recipe_digest LIMIT 129"
        }) {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "EXPLAIN QUERY PLAN " + sql;
            command.Parameters.AddWithValue("$key", new string('0', 64));
            using SqliteDataReader reader = command.ExecuteReader();
            var plans = new List<string>();
            while (reader.Read()) plans.Add(reader.GetString(3));
            string plan = string.Join("\n", plans);
            Assert.Contains("SEARCH", plan, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("SCAN ", plan, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("TEMP B-TREE", plan, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ExportHidesJsonContentByDefaultAndReportsActualProjectionSize() {
        Create();
        RecapCellArtifact cell;
        using (RecapGridStoreHandle handle = OpenWithHooks(new StorePersistenceTestHooks())) {
            cell = Assert.IsType<RecapGridCellPutResult.Inserted>(Put(handle, Cell('b', "private answer"))).Winner;
        }
        RecapGridStoreExportItem hidden = Assert.Single(Assert.IsType<RecapGridStoreExportResult.Page>(
            RecapGridStoreMaintenance.Export(_root)).Value.Items);
        Assert.Equal(cell.Id.Value, hidden.Key);
        Assert.Null(hidden.Json);
        RecapGridStoreExportItem revealed = Assert.Single(Assert.IsType<RecapGridStoreExportResult.Page>(
            RecapGridStoreMaintenance.Export(_root, includeContent: true)).Value.Items);
        byte[] json = Assert.IsType<byte[]>(revealed.Json);
        Assert.Equal(json.Length, revealed.JsonUtf8Bytes);
        Assert.Contains("private answer", System.Text.Encoding.UTF8.GetString(json));
        RecapGridStoreExportCursor cursor = RecapGridStoreExportCursor.Parse(RecapGridStoreExportCursor.CreateId(hidden.Kind, hidden.Key).Value);
        Assert.True(cursor.IsCell);
        Assert.Equal(hidden.Key, cursor.Key);
        Assert.Throws<ArgumentException>(() => RecapGridStoreExportCursor.Parse("not/canonical"));
    }

    [Fact]
    public void ExportUsesTypedReversibleCursorAcrossThreePages() {
        Create();
        RecapCellArtifact[] cells = Enumerable.Range(1, 257).Select(i => Cell(i, $"answer-{i}")).OrderBy(c => c.Id.Value, StringComparer.Ordinal).ToArray();
        InsertCellsRaw(cells);
        var exported = new List<string>();
        RecapGridStoreExportCursor? cursor = null;
        int pages = 0;
        do {
            var page = Assert.IsType<RecapGridStoreExportResult.Page>(RecapGridStoreMaintenance.Export(_root, cursor)).Value;
            pages++;
            exported.AddRange(page.Items.Select(item => item.Key));
            if (!page.Incomplete) { Assert.Null(page.NextCursor); break; }
            cursor = RecapGridStoreExportCursor.Parse(Assert.IsType<RecapGridStoreExportCursor>(page.NextCursor).Value);
        } while (true);
        Assert.Equal(3, pages);
        Assert.Equal(cells.Select(c => c.Id.Value), exported);
    }

    [Fact]
    public void ExportRespectsByteAndItemBoundsWithoutLosingLargeItems() {
        Create();
        RecapCellArtifact[] cells = Enumerable.Range(1, 129).Select(i => Cell(i, new string('x', 16000))).ToArray();
        InsertCellsRaw(cells);
        RecapGridStoreExportCursor? cursor = null;
        var seen = new HashSet<string>();
        do {
            var page = Assert.IsType<RecapGridStoreExportResult.Page>(RecapGridStoreMaintenance.Export(_root, cursor, includeContent: true)).Value;
            Assert.InRange(page.Items.Count, 1, RecapGridStoreLimits.MaximumPageItems);
            Assert.InRange(page.Items.Sum(i => i.JsonUtf8Bytes), 1, RecapGridStoreLimits.MaximumPageBytes);
            foreach (var item in page.Items) {
                Assert.True(seen.Add(item.Key));
                Assert.Equal(item.JsonUtf8Bytes, Assert.IsType<byte[]>(item.Json).Length);
            }
            if (!page.Incomplete) break;
            cursor = Assert.IsType<RecapGridStoreExportCursor>(page.NextCursor);
        } while (true);
        Assert.Equal(cells.Length, seen.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FulfilledExportUsesCompositeCursorAndStoredResultIds(bool varyingThroughRow) {
        Create();
        var expected = new Dictionary<string, RowResultId>();
        using (RecapGridStoreHandle handle = OpenWithHooks(new StorePersistenceTestHooks())) {
            for (int index = 1; index <= 257; index++) {
                RowBuildSpec spec = varyingThroughRow
                    ? StoreFixture.Spec(row: new HistoryRowId(index.ToString("x64")))
                    : StoreFixture.Spec(
                        recipe: GridBuildRecipe.CreateFull(
                            StoreFixture.Timeline,
                            new HistoryRowId(index.ToString("x64")),
                            StoreFixture.Recipe().Target),
                        refId: new RefId((ulong)index));
                StoreFixture.PutWork(handle, spec);
                RecapGridCellPutResult put = handle.Writer.PutCell(spec, StoreFixture.Draft(spec));
                RecapCellArtifact cell = put switch {
                    RecapGridCellPutResult.Inserted inserted => inserted.Winner,
                    RecapGridCellPutResult.AlreadyFilled filled => filled.Winner,
                    _ => throw new InvalidOperationException(put.ToString())
                };
                RecapRowView view = Assert.IsType<RecapGridRowViewPutResult.Inserted>(handle.Writer.PutRowView(spec, [cell])).Winner;
                FulfilledViewKey key = StoreFixture.Fulfilled(spec, varyingThroughRow ? 7 : index);
                Assert.IsType<RecapGridFulfilledPutResult.Inserted>(handle.Writer.PutFulfilled(key, view.Id));
                string cursorKey = RecapGridStoreExportCursor.CreateFulfilled(key.RefId.ToHexString(), key.TimelineId.Value,
                    key.TimelineHeadGeneration, key.ThroughRowId.Value, key.RecipeDigest.Value).Key;
                expected.Add(cursorKey, view.Id);
            }
        }
        RecapGridStoreExportCursor? cursor = null;
        var seen = new HashSet<string>();
        var cursors = new HashSet<string>();
        int pages = 0;
        do {
            var page = Assert.IsType<RecapGridStoreExportResult.Page>(RecapGridStoreMaintenance.Export(_root, cursor, includeContent: true)).Value;
            pages++;
            foreach (var item in page.Items.Where(i => i.Kind == "fulfilled")) {
                Assert.True(seen.Add(item.Key));
                Assert.Equal(expected[item.Key], item.FulfilledRowResultId);
                Assert.NotNull(item.Json);
            }
            if (!page.Incomplete) break;
            cursor = Assert.IsType<RecapGridStoreExportCursor>(page.NextCursor);
            Assert.True(cursors.Add(cursor.Value));
            cursor = RecapGridStoreExportCursor.Parse(cursor.Value);
        } while (true);
        Assert.Equal(7, pages);
        Assert.Equal(expected.Count, seen.Count);
    }

    private void Create() {
        Directory.CreateDirectory(_root);
        Assert.IsType<RecapGridStoreCreateResult.Created>(RecapGridStoreFactory.Create(_root));
    }
    private SqliteConnection OpenRaw() => new($"Data Source={new StorePaths(_root).DatabasePath};Mode=ReadWrite;Pooling=False");
    private RecapGridStoreHandle OpenWithHooks(StorePersistenceTestHooks hooks) => Assert.IsType<RecapGridStoreOpenResult.Opened>(
        RecapGridStoreFactory.OpenForTest(_root, StoreStorageLimits.Production, hooks)).Handle;
    private static RecapCellArtifact Cell(char row, string content) => StoreFixture.Proposed(
        StoreFixture.Spec(row: new HistoryRowId(new string(row, 64))), content);
    private static RecapCellArtifact Cell(int row, string content) => StoreFixture.Proposed(
        StoreFixture.Spec(row: new HistoryRowId(row.ToString("x64"))), content);
    private static RecapGridCellPutResult Put(RecapGridStoreHandle handle, RecapCellArtifact cell) {
        RowBuildSpec spec = StoreFixture.Spec(row: cell.Slot.HistoryRowId);
        StoreFixture.PutWork(handle, spec);
        return handle.Writer.PutCell(spec, RecapCellDraft.Create(cell.Slot, cell.DefinitionDigest,
            cell.Outcome, cell.Content, RecapGridLimits.MaximumContentUtf8Bytes));
    }
    private void InsertCellsRaw(IReadOnlyList<RecapCellArtifact> cells) {
        using SqliteConnection connection = OpenRaw();
        connection.Open();
        using SqliteTransaction transaction = connection.BeginTransaction();
        foreach (RecapCellArtifact cell in cells) {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO cell_artifact(cell_id, recipe_digest, history_row_id, logical_column_id, definition_digest, outcome, content) VALUES ($id, $recipe, $row, $column, $definition, $outcome, $content);";
            command.Parameters.AddWithValue("$id", cell.Id.Value);
            command.Parameters.AddWithValue("$recipe", cell.Slot.RecipeDigest.Value);
            command.Parameters.AddWithValue("$row", cell.Slot.HistoryRowId.Value);
            command.Parameters.AddWithValue("$column", cell.LogicalColumnId.Value);
            command.Parameters.AddWithValue("$definition", cell.DefinitionDigest.Value);
            command.Parameters.AddWithValue("$outcome", (int)cell.Outcome);
            command.Parameters.AddWithValue("$content", cell.Content);
            command.ExecuteNonQuery();
        }
        using SqliteCommand count = connection.CreateCommand();
        count.Transaction = transaction;
        count.CommandText = "UPDATE store_metadata SET cell_count = $count;";
        count.Parameters.AddWithValue("$count", cells.Count);
        Assert.Equal(1, count.ExecuteNonQuery());
        transaction.Commit();
    }
    public void Dispose() {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
