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
    public void CanonicalCorruptionLatchesInvalidAndVerifyIsIncomplete() {
        Directory.CreateDirectory(_root);
        Assert.IsType<RecapGridStoreCreateResult.Created>(
            RecapGridStoreFactory.Create(_root)
        );
        RecapCellArtifact cell = Cell('b', "answer");
        using RecapGridStoreHandle handle = Assert.IsType<
            RecapGridStoreOpenResult.Opened
        >(RecapGridStoreFactory.Open(_root)).Handle;
        Assert.IsType<RecapGridCellPutResult.Inserted>(
            handle.Writer.PutCell(cell)
        );

        using (SqliteConnection connection = OpenRaw()) {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE cell_artifact SET canonical = X'00';";
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        RecapGridStoreReadResult<RecapCellArtifact>.Invalid invalid =
            Assert.IsType<
                RecapGridStoreReadResult<RecapCellArtifact>.Invalid
            >(handle.Reader.TryReadCell(cell.EvaluationKey));
        Assert.Equal(
            invalid.Code,
            Assert.IsType<RecapGridCellPutResult.Invalid>(
                handle.Writer.PutCell(Cell('c', "other"))
            ).Code
        );
        RecapGridStoreVerifyResult.Unhealthy unhealthy = Assert.IsType<
            RecapGridStoreVerifyResult.Unhealthy
        >(RecapGridStoreMaintenance.Verify(_root));
        Assert.True(unhealthy.Incomplete);
        Assert.NotEmpty(unhealthy.Errors);
    }

    [Fact]
    public void AllThreePostCommitFailuresReturnObservedSettlement() {
        Directory.CreateDirectory(_root);
        Assert.IsType<RecapGridStoreCreateResult.Created>(
            RecapGridStoreFactory.Create(_root)
        );
        (RowBuildSpec spec, RecapCellArtifact cell, RecapRowView view,
            FulfilledViewKey key) = RowValues();
        static void ThrowAfterCommit() => throw new IOException("injected");
        using RecapGridStoreHandle handle = Assert.IsType<
            RecapGridStoreOpenResult.Opened
        >(RecapGridStoreFactory.OpenForTest(
            _root,
            StoreStorageLimits.Production,
            new StorePersistenceTestHooks(
                AfterCellCommit: ThrowAfterCommit,
                AfterRowViewCommit: ThrowAfterCommit,
                AfterFulfilledCommit: ThrowAfterCommit
            )
        )).Handle;

        Assert.Equal(
            cell.CellDigest,
            Assert.IsType<RecapGridCellPutResult.CommitIndeterminate>(
                handle.Writer.PutCell(cell)
            ).Observed?.CellDigest
        );
        RecapGridRowViewPutResult.CommitIndeterminate rowSettlement =
            Assert.IsType<RecapGridRowViewPutResult.CommitIndeterminate>(
                handle.Writer.PutRowView(spec, view)
            );
        Assert.Equal(spec.Coordinate.AssignmentKey,
            rowSettlement.IntendedAssignment);
        Assert.Equal(view.Digest, rowSettlement.Intended);
        Assert.Equal(view.Digest, rowSettlement.Observed);
        Assert.Equal(
            view.Digest,
            Assert.IsType<RecapGridFulfilledPutResult.CommitIndeterminate>(
                handle.Writer.PutFulfilled(key, view.Digest)
            ).Observed
        );
    }

    [Fact]
    public void AllThreeNativeCommitReturnFailuresReturnObservedSettlement() {
        Directory.CreateDirectory(_root);
        Assert.IsType<RecapGridStoreCreateResult.Created>(
            RecapGridStoreFactory.Create(_root)
        );
        (RowBuildSpec spec, RecapCellArtifact cell, RecapRowView view,
            FulfilledViewKey key) = RowValues();
        static void ThrowAfterNativeCommit()
            => throw new IOException("injected after native COMMIT return");
        using RecapGridStoreHandle handle = Assert.IsType<
            RecapGridStoreOpenResult.Opened
        >(RecapGridStoreFactory.OpenForTest(
            _root,
            StoreStorageLimits.Production,
            new StorePersistenceTestHooks(
                AfterCellNativeCommitReturn: ThrowAfterNativeCommit,
                AfterRowViewNativeCommitReturn: ThrowAfterNativeCommit,
                AfterFulfilledNativeCommitReturn: ThrowAfterNativeCommit
            )
        )).Handle;

        Assert.Equal(
            cell.CellDigest,
            Assert.IsType<RecapGridCellPutResult.CommitIndeterminate>(
                handle.Writer.PutCell(cell)
            ).Observed?.CellDigest
        );
        RecapGridRowViewPutResult.CommitIndeterminate rowSettlement =
            Assert.IsType<RecapGridRowViewPutResult.CommitIndeterminate>(
                handle.Writer.PutRowView(spec, view)
            );
        Assert.Equal(spec.Coordinate.AssignmentKey,
            rowSettlement.IntendedAssignment);
        Assert.Equal(view.Digest, rowSettlement.Intended);
        Assert.Equal(view.Digest, rowSettlement.Observed);
        Assert.Equal(
            view.Digest,
            Assert.IsType<RecapGridFulfilledPutResult.CommitIndeterminate>(
                handle.Writer.PutFulfilled(key, view.Digest)
            ).Observed
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
            handle.Writer.PutCell(Cell('b', "answer"))
        );
        Assert.Equal(2, retries);
        using (SqliteCommand rollback = blocker.CreateCommand()) {
            rollback.CommandText = "ROLLBACK;";
            rollback.ExecuteNonQuery();
        }
        Assert.IsType<RecapGridCellPutResult.Inserted>(
            handle.Writer.PutCell(Cell('b', "answer"))
        );
    }

    [Fact]
    public void LifetimeCountIsNotAdmissionCappedAndQueryPlansUseIndexes() {
        Directory.CreateDirectory(_root);
        Assert.IsType<RecapGridStoreCreateResult.Created>(
            RecapGridStoreFactory.Create(_root)
        );
        using RecapGridStoreHandle handle = Assert.IsType<
            RecapGridStoreOpenResult.Opened
        >(RecapGridStoreFactory.Open(_root)).Handle;
        Assert.IsType<RecapGridCellPutResult.Inserted>(
            handle.Writer.PutCell(Cell('b', "one"))
        );
        Assert.IsType<RecapGridCellPutResult.Inserted>(
            handle.Writer.PutCell(Cell('c', "two"))
        );

        using SqliteConnection connection = OpenRaw();
        connection.Open();
        foreach (string sql in new[] {
                     "EXPLAIN QUERY PLAN SELECT canonical FROM cell_artifact WHERE evaluation_key_digest = $key;",
                     "EXPLAIN QUERY PLAN SELECT canonical FROM row_view WHERE view_digest = $key;",
                     "EXPLAIN QUERY PLAN SELECT view_digest FROM fulfilled_view_ref WHERE ref_id = $key AND timeline_id = $key AND timeline_head_generation = $generation AND through_row_descriptor_digest = $key AND recipe_digest = $key;",
                     "EXPLAIN QUERY PLAN SELECT cell_digest FROM cell_artifact WHERE cell_digest >= '' ORDER BY cell_digest LIMIT 129;",
                     "EXPLAIN QUERY PLAN SELECT cell_digest FROM cell_artifact WHERE cell_digest > $key ORDER BY cell_digest LIMIT 129;",
                     "EXPLAIN QUERY PLAN SELECT view_digest FROM row_view WHERE view_digest >= '' ORDER BY view_digest LIMIT 129;",
                     "EXPLAIN QUERY PLAN SELECT view_digest FROM row_view WHERE view_digest > $key ORDER BY view_digest LIMIT 129;",
                     "EXPLAIN QUERY PLAN SELECT ref_id, timeline_id, timeline_head_generation, through_row_descriptor_digest, recipe_digest FROM fulfilled_view_ref WHERE (ref_id, timeline_id, timeline_head_generation, through_row_descriptor_digest, recipe_digest) >= ('', '', 0, '', '') ORDER BY ref_id, timeline_id, timeline_head_generation, through_row_descriptor_digest, recipe_digest LIMIT 129;",
                     "EXPLAIN QUERY PLAN SELECT ref_id, timeline_id, timeline_head_generation, through_row_descriptor_digest, recipe_digest FROM fulfilled_view_ref WHERE (ref_id, timeline_id, timeline_head_generation, through_row_descriptor_digest, recipe_digest) > ($ref, $timeline, $generation, $through, $recipe) ORDER BY ref_id, timeline_id, timeline_head_generation, through_row_descriptor_digest, recipe_digest LIMIT 129;"
                 }) {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$key", new string('0', 64));
            command.Parameters.AddWithValue("$ref", "0000000000000000");
            command.Parameters.AddWithValue(
                "$timeline",
                "00000000000000000000000000000000"
            );
            command.Parameters.AddWithValue("$generation", 0L);
            command.Parameters.AddWithValue("$through", new string('0', 64));
            command.Parameters.AddWithValue("$recipe", new string('0', 64));
            string plan = string.Join(
                "\n",
                ReadPlan(command)
            );
            Assert.Contains("SEARCH", plan, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("SCAN ", plan, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                "TEMP B-TREE",
                plan,
                StringComparison.OrdinalIgnoreCase
            );
        }
    }

    [Fact]
    public void ExportIsBoundedAndHidesCanonicalContentByDefault() {
        Directory.CreateDirectory(_root);
        Assert.IsType<RecapGridStoreCreateResult.Created>(
            RecapGridStoreFactory.Create(_root)
        );
        RecapCellArtifact cell = Cell('b', "private answer");
        using (RecapGridStoreHandle handle = Assert.IsType<
               RecapGridStoreOpenResult.Opened
               >(RecapGridStoreFactory.Open(_root)).Handle) {
            Assert.IsType<RecapGridCellPutResult.Inserted>(
                handle.Writer.PutCell(cell)
            );
        }

        RecapGridStoreExportPage hidden = Assert.IsType<
            RecapGridStoreExportResult.Page
        >(RecapGridStoreMaintenance.Export(_root)).Value;
        RecapGridStoreExportItem item = Assert.Single(hidden.Items);
        Assert.Equal("cell", item.Kind);
        Assert.Null(item.Canonical);
        Assert.False(hidden.Incomplete);
        Assert.Null(hidden.NextCursor);

        RecapGridStoreExportItem revealed = Assert.Single(
            Assert.IsType<RecapGridStoreExportResult.Page>(
                RecapGridStoreMaintenance.Export(
                    _root,
                    includeContent: true
                )
            ).Value.Items
        );
        Assert.Equal(cell.ToCanonicalBytes(), revealed.Canonical);
        RecapGridStoreExportCursor created =
            RecapGridStoreExportCursor.CreateDigest(item.Kind, item.Key);
        RecapGridStoreExportCursor cursor =
            RecapGridStoreExportCursor.Parse(created.Value);
        Assert.True(cursor.IsCell);
        Assert.Equal(item.Key, cursor.Key);
        Assert.Throws<ArgumentException>(
            () => RecapGridStoreExportCursor.Parse("not/canonical")
        );
    }

    [Fact]
    public void ExportContractedByteBoundStillPermitsMaximumItemPage() {
        Directory.CreateDirectory(_root);
        Assert.IsType<RecapGridStoreCreateResult.Created>(
            RecapGridStoreFactory.Create(_root)
        );
        string content = new('\u9ffe', 5_196);
        RecapCellArtifact[] cells = Enumerable.Range(1, 129)
            .Select(index => Cell(index, content))
            .ToArray();
        InsertCellsRaw(cells);

        RecapGridStoreExportPage first = Assert.IsType<
            RecapGridStoreExportResult.Page
        >(RecapGridStoreMaintenance.Export(
            _root,
            includeContent: true
        )).Value;

        Assert.Equal(RecapGridStoreLimits.MaximumPageItems, first.Items.Count);
        Assert.InRange(
            first.Items.Sum(static item => item.CanonicalBytes),
            RecapGridStoreLimits.MaximumPageBytes - 64 * 1024,
            RecapGridStoreLimits.MaximumPageBytes
        );
        Assert.True(first.Incomplete);
        Assert.NotNull(first.NextCursor);

        RecapGridStoreExportPage second = Assert.IsType<
            RecapGridStoreExportResult.Page
        >(RecapGridStoreMaintenance.Export(
            _root,
            first.NextCursor,
            includeContent: true
        )).Value;
        Assert.Single(second.Items);
        Assert.False(second.Incomplete);
        Assert.Null(second.NextCursor);
    }

    [Fact]
    public void ExportUsesTypedReversibleCursorAcrossMoreThanTwoPages() {
        Directory.CreateDirectory(_root);
        Assert.IsType<RecapGridStoreCreateResult.Created>(
            RecapGridStoreFactory.Create(_root)
        );
        RecapCellArtifact[] cells = Enumerable.Range(1, 257)
            .Select(static index => Cell(index, $"answer-{index}"))
            .OrderBy(static cell => cell.CellDigest.Value, StringComparer.Ordinal)
            .ToArray();
        InsertCellsRaw(cells);

        var exported = new List<string>();
        RecapGridStoreExportCursor? cursor = null;
        int pages = 0;
        do {
            RecapGridStoreExportPage page = Assert.IsType<
                RecapGridStoreExportResult.Page
            >(RecapGridStoreMaintenance.Export(_root, cursor)).Value;
            pages++;
            exported.AddRange(page.Items.Select(static item => item.Key));
            if (!page.Incomplete) {
                Assert.Null(page.NextCursor);
                break;
            }
            cursor = RecapGridStoreExportCursor.Parse(
                Assert.IsType<RecapGridStoreExportCursor>(
                    page.NextCursor
                ).Value
            );
        } while (true);

        Assert.Equal(3, pages);
        Assert.Equal(
            cells.Select(static cell => cell.CellDigest.Value),
            exported
        );
    }

    [Fact]
    public void FulfilledExportPreservesCompositeCursorAndViewDiagnosticsAcrossThreePages() {
        Directory.CreateDirectory(_root);
        Assert.IsType<RecapGridStoreCreateResult.Created>(
            RecapGridStoreFactory.Create(_root)
        );
        (RowBuildSpec spec, RecapCellArtifact cell, _, _) =
            RowValues();
        using (RecapGridStoreHandle handle = Assert.IsType<
               RecapGridStoreOpenResult.Opened
               >(RecapGridStoreFactory.Open(_root)).Handle) {
            Assert.IsType<RecapGridCellPutResult.Inserted>(
                handle.Writer.PutCell(cell)
            );
        }
        GridBuildRecipe recipe = Recipe();
        var keys = new List<FulfilledViewKey>(257);
        var views = new List<RecapRowView>(257);
        for (int index = 1; index <= 257; index++) {
            var refId = new RefId((ulong)index);
            RowBuildSpec assignedSpec = RowBuildSpec.CreateFull(
                recipe,
                new RowViewCoordinate(
                    refId,
                    recipe.TimelineId,
                    spec.HistoryRowId,
                    spec.HistorySegmentDigest,
                    recipe.Digest,
                    recipe.Target.Digest,
                    previousHistoryRowId: null,
                    previousViewDigest: null,
                    bootstrapCompleted: true
                ),
                spec.PriorInput,
                spec.OrderedAssignments
            );
            RecapRowView assignedView = RecapRowView.Create(
                assignedSpec,
                [cell]
            );
            var head = new TimelineHeadRef(
                recipe.TimelineId,
                refId,
                null,
                new string('d', 64),
                null,
                0,
                HistoryTimelineSelectedPath.EmptyDigest,
                generation: index
            );
            views.Add(assignedView);
            keys.Add(FulfilledViewKey.Create(
                refId,
                head,
                assignedView.RowDescriptorDigest,
                recipe
            ));
        }
        using (SqliteConnection connection = OpenRaw()) {
            connection.Open();
            using SqliteTransaction transaction = connection.BeginTransaction();
            for (int index = 0; index < keys.Count; index++) {
                FulfilledViewKey key = keys[index];
                RecapRowView view = views[index];
                using (SqliteCommand insertView = connection.CreateCommand()) {
                    insertView.Transaction = transaction;
                    insertView.CommandText = """
                        INSERT INTO row_view(
                            view_digest, ref_id, timeline_id, history_row_id,
                            row_descriptor_digest, recipe_digest, target_digest,
                            previous_history_row_id, previous_view_digest,
                            bootstrap_completed, canonical
                        ) VALUES (
                            $view, $ref, $timeline, $row, $descriptor, $recipe,
                            $target, NULL, NULL, 1, $canonical
                        );
                        """;
                    insertView.Parameters.AddWithValue("$view", view.Digest.Value);
                    insertView.Parameters.AddWithValue("$ref", key.RefId.ToHexString());
                    insertView.Parameters.AddWithValue("$timeline", key.TimelineId.Value);
                    insertView.Parameters.AddWithValue("$row", view.HistoryRowId.Value);
                    insertView.Parameters.AddWithValue(
                        "$descriptor",
                        view.RowDescriptorDigest.Value
                    );
                    insertView.Parameters.AddWithValue("$recipe", view.RecipeDigest.Value);
                    insertView.Parameters.AddWithValue("$target", view.TargetDigest.Value);
                    insertView.Parameters.AddWithValue("$canonical", view.ToCanonicalBytes());
                    insertView.ExecuteNonQuery();
                }
                using (SqliteCommand insertMember = connection.CreateCommand()) {
                    insertMember.Transaction = transaction;
                    insertMember.CommandText = """
                        INSERT INTO row_view_member(
                            view_digest, column_ordinal, logical_column_id,
                            definition_digest, cell_digest
                        ) VALUES ($view, 0, $column, $definition, $cell);
                        """;
                    insertMember.Parameters.AddWithValue("$view", view.Digest.Value);
                    insertMember.Parameters.AddWithValue(
                        "$column",
                        cell.LogicalColumnId.Value
                    );
                    insertMember.Parameters.AddWithValue(
                        "$definition",
                        cell.DefinitionDigest.Value
                    );
                    insertMember.Parameters.AddWithValue("$cell", cell.CellDigest.Value);
                    insertMember.ExecuteNonQuery();
                }
                using SqliteCommand insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO fulfilled_view_ref(
                        ref_id, timeline_id, timeline_head_generation,
                        through_row_descriptor_digest, recipe_digest,
                        key_canonical, view_digest
                    ) VALUES (
                        $ref, $timeline, $generation, $through, $recipe,
                        $canonical, $view
                    );
                    """;
                insert.Parameters.AddWithValue("$ref", key.RefId.ToHexString());
                insert.Parameters.AddWithValue(
                    "$timeline",
                    key.TimelineId.Value
                );
                insert.Parameters.AddWithValue(
                    "$generation",
                    key.TimelineHeadGeneration
                );
                insert.Parameters.AddWithValue(
                    "$through",
                    key.ThroughRowDescriptorDigest.Value
                );
                insert.Parameters.AddWithValue(
                    "$recipe",
                    key.RecipeDigest.Value
                );
                insert.Parameters.AddWithValue(
                    "$canonical",
                    key.ToCanonicalBytes()
                );
                insert.Parameters.AddWithValue("$view", view.Digest.Value);
                insert.ExecuteNonQuery();
            }
            using SqliteCommand count = connection.CreateCommand();
            count.Transaction = transaction;
            count.CommandText = """
                UPDATE store_metadata
                SET row_view_count = $count,
                    row_view_member_count = $count,
                    fulfilled_view_count = $count;
                """;
            count.Parameters.AddWithValue("$count", keys.Count);
            Assert.Equal(1, count.ExecuteNonQuery());
            transaction.Commit();
        }

        int expectedIndex = 0;
        int pages = 0;
        RecapGridStoreExportCursor? cursor = null;
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        do {
            RecapGridStoreExportPage page = Assert.IsType<
                RecapGridStoreExportResult.Page
            >(RecapGridStoreMaintenance.Export(
                _root,
                cursor,
                includeContent: true
            )).Value;
            pages++;
            foreach (RecapGridStoreExportItem item in page.Items
                         .Where(static item => item.Kind == "fulfilled")) {
                Assert.True(seenKeys.Add(item.Key));
                byte[] canonical = Assert.IsType<byte[]>(item.Canonical);
                FulfilledViewKey decoded =
                    FulfilledViewKey.DecodeCanonical(canonical);
                FulfilledViewKey expected = keys[expectedIndex];
                Assert.Equal(expected.ToCanonicalBytes(), canonical);
                Assert.Equal(expected.RefId, decoded.RefId);
                Assert.Equal(
                    RecapGridStoreExportCursor.CreateFulfilled(
                        expected.RefId.ToHexString(),
                        expected.TimelineId.Value,
                        expected.TimelineHeadGeneration,
                        expected.ThroughRowDescriptorDigest.Value,
                        expected.RecipeDigest.Value
                    ).Key,
                    item.Key
                );
                Assert.Equal(
                    views[expectedIndex].Digest,
                    item.FulfilledViewDigest
                );
                expectedIndex++;
            }
            if (!page.Incomplete) {
                Assert.Null(page.NextCursor);
                break;
            }
            string cursorValue = Assert.IsType<
                RecapGridStoreExportCursor
            >(page.NextCursor).Value;
            Assert.True(seenCursors.Add(cursorValue));
            cursor = RecapGridStoreExportCursor.Parse(cursorValue);
        } while (true);

        Assert.Equal(5, pages);
        Assert.Equal(keys.Count, expectedIndex);
        Assert.Equal(keys.Count, seenKeys.Count);
    }

    [Fact]
    public async Task TwoHandlesSettleToOneExactWinner() {
        Directory.CreateDirectory(_root);
        Assert.IsType<RecapGridStoreCreateResult.Created>(
            RecapGridStoreFactory.Create(_root)
        );
        RecapCellArtifact cell = Cell('b', "winner");
        using var barrier = new Barrier(2);
        int firstOne = 0;
        int firstTwo = 0;
        Action beforeOne = () => {
            if (Interlocked.Exchange(ref firstOne, 1) == 0) {
                barrier.SignalAndWait();
            }
        };
        Action beforeTwo = () => {
            if (Interlocked.Exchange(ref firstTwo, 1) == 0) {
                barrier.SignalAndWait();
            }
        };
        using RecapGridStoreHandle one = OpenWithHooks(
            new StorePersistenceTestHooks(BeforeCellBegin: beforeOne)
        );
        using RecapGridStoreHandle two = OpenWithHooks(
            new StorePersistenceTestHooks(BeforeCellBegin: beforeTwo)
        );
        Task<RecapGridCellPutResult> taskOne = Task.Run(
            () => one.Writer.PutCell(cell)
        );
        Task<RecapGridCellPutResult> taskTwo = Task.Run(
            () => two.Writer.PutCell(cell)
        );
        await Task.WhenAll(taskOne, taskTwo);
        RecapGridCellPutResult resultOne = await taskOne;
        RecapGridCellPutResult resultTwo = await taskTwo;
        Assert.Single(new[] { resultOne, resultTwo },
            static result => result is RecapGridCellPutResult.Inserted);
        RecapGridStoreHandle loser = resultOne
            is RecapGridCellPutResult.Inserted ? two : one;
        RecapGridCellPutResult loserResult = resultOne
            is RecapGridCellPutResult.Inserted ? resultTwo : resultOne;
        if (loserResult is RecapGridCellPutResult.Busy) {
            loserResult = loser.Writer.PutCell(cell);
        }
        RecapGridCellPutResult.AlreadyFilled settled = Assert.IsType<
            RecapGridCellPutResult.AlreadyFilled
        >(loserResult);
        Assert.Equal(
            cell.ToCanonicalBytes(),
            settled.Winner.ToCanonicalBytes()
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
            () => draining.Writer.PutCell(Cell('c', "drain"))
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
            draining.Writer.PutCell(Cell('d', "after dispose"))
        );
    }

    private SqliteConnection OpenRaw() => new(
        $"Data Source={new StorePaths(_root).DatabasePath};Mode=ReadWrite;Pooling=False"
    );

    private void InsertCellsRaw(IReadOnlyList<RecapCellArtifact> cells) {
        using SqliteConnection connection = OpenRaw();
        connection.Open();
        using SqliteTransaction transaction = connection.BeginTransaction();
        foreach (RecapCellArtifact cell in cells) {
            using SqliteCommand insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO cell_artifact(
                    cell_digest, evaluation_key_digest,
                    history_segment_digest, logical_column_id,
                    definition_digest, content_digest, canonical
                ) VALUES (
                    $cell, $evaluation, $history, $column,
                    $definition, $content, $canonical
                );
                """;
            insert.Parameters.AddWithValue("$cell", cell.CellDigest.Value);
            insert.Parameters.AddWithValue(
                "$evaluation",
                cell.EvaluationKey.Digest.Value
            );
            insert.Parameters.AddWithValue(
                "$history",
                cell.EvaluationKey.HistorySegmentDigest.Value
            );
            insert.Parameters.AddWithValue(
                "$column",
                cell.LogicalColumnId.Value
            );
            insert.Parameters.AddWithValue(
                "$definition",
                cell.DefinitionDigest.Value
            );
            insert.Parameters.AddWithValue(
                "$content",
                cell.ContentDigest.Value
            );
            insert.Parameters.AddWithValue(
                "$canonical",
                cell.ToCanonicalBytes()
            );
            insert.ExecuteNonQuery();
        }
        using SqliteCommand count = connection.CreateCommand();
        count.Transaction = transaction;
        count.CommandText = "UPDATE store_metadata SET cell_count = $count;";
        count.Parameters.AddWithValue("$count", cells.Count);
        Assert.Equal(1, count.ExecuteNonQuery());
        transaction.Commit();
    }

    private RecapGridStoreHandle OpenWithHooks(
        StorePersistenceTestHooks hooks
    ) => Assert.IsType<RecapGridStoreOpenResult.Opened>(
        RecapGridStoreFactory.OpenForTest(
            _root,
            StoreStorageLimits.Production,
            hooks
        )
    ).Handle;

    private static IReadOnlyList<string> ReadPlan(SqliteCommand command) {
        var result = new List<string>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read()) {
            result.Add(reader.GetString(3));
        }
        return result;
    }

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

    private static RecapCellArtifact Cell(int descriptor, string content) {
        var definition = new MaintainerDefinitionDigest(new string('a', 64));
        EvaluationKey evaluation = EvaluationKey.Create(
            new HistorySegmentDescriptorDigest(descriptor.ToString("x64")),
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

    private static (
        RowBuildSpec Spec,
        RecapCellArtifact Cell,
        RecapRowView View,
        FulfilledViewKey Fulfilled
    ) RowValues() {
        GridBuildRecipe recipe = Recipe();
        TimelineId timeline = recipe.TimelineId;
        var definition = new MaintainerDefinitionDigest(new string('a', 64));
        var column = new LogicalColumnId("case.culprit");
        var descriptor = new HistorySegmentDescriptorDigest(new string('b', 64));
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
            "answer",
            RecapGridLimits.MaximumContentUtf8Bytes
        );
        var rowId = new HistoryRowId(new string('c', 64));
        RowBuildSpec spec = RowBuildSpec.CreateFull(
            recipe,
            new RowViewCoordinate(
                new RefId(1),
                timeline,
                rowId,
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
        RecapRowView view = RecapRowView.Create(spec, [cell]);
        var head = new TimelineHeadRef(
            timeline,
            new RefId(1),
            null,
            new string('d', 64),
            null,
            0,
            HistoryTimelineSelectedPath.EmptyDigest,
            generation: 1
        );
        return (
            spec,
            cell,
            view,
            FulfilledViewKey.Create(
                head.RefId,
                head,
                view.RowDescriptorDigest,
                recipe
            )
        );
    }

    private static GridBuildRecipe Recipe() {
        var timeline = new TimelineId("00112233445566778899aabbccddeeff");
        var definition = new MaintainerDefinitionDigest(new string('a', 64));
        var column = new LogicalColumnId("case.culprit");
        return GridBuildRecipe.CreateFull(
            timeline,
            new HistoryRowId(new string('c', 64)),
            BuildTarget.Create([new BuildTargetColumn(column, definition)])
        );
    }

    public void Dispose() {
        if (Directory.Exists(_root)) {
            Directory.Delete(_root, recursive: true);
        }
    }
}
