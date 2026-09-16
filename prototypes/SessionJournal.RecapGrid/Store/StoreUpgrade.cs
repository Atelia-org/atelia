using Microsoft.Data.Sqlite;
using Atelia.EventJournal;
using Atelia.SessionJournal.HistoryTimeline;

namespace Atelia.SessionJournal.RecapGrid.Store;

public static partial class RecapGridStoreMaintenance {
    /// <summary>
    /// Provider-free V4 to V5 conversion. Ordinary Store opens deliberately
    /// remain V5-only; this path takes the exclusive lifetime lease, builds a
    /// complete replacement database, and never calls a completion factory.
    /// </summary>
    public static RecapGridStoreUpgradeResult UpgradeV4(
        string repositoryPath,
        bool apply
    ) => UpgradeV4Core(repositoryPath, apply, static () => [],
        StoreUpgradeTestHooks.None);

    /// <summary>
    /// Upgrades a stopped V4 Store using partial-work proofs gathered by a
    /// higher layer that owns Control and Timeline reads. Store validates the
    /// submitted immutable facts; it never chooses a scope, target, or prior.
    /// </summary>
    public static RecapGridStoreUpgradeResult UpgradeV4(
        string repositoryPath,
        bool apply,
        Func<IReadOnlyList<RowWork>> resolvePartialWorkProofs
    ) => UpgradeV4Core(repositoryPath, apply, resolvePartialWorkProofs,
        StoreUpgradeTestHooks.None);

    internal static RecapGridStoreUpgradeResult UpgradeV4ForTest(
        string repositoryPath,
        bool apply,
        Func<IReadOnlyList<RowWork>> resolvePartialWorkProofs,
        StoreUpgradeTestHooks hooks
    ) => UpgradeV4Core(repositoryPath, apply, resolvePartialWorkProofs,
        hooks);

    private static RecapGridStoreUpgradeResult UpgradeV4Core(
        string repositoryPath,
        bool apply,
        Func<IReadOnlyList<RowWork>> resolvePartialWorkProofs,
        StoreUpgradeTestHooks hooks
    ) {
        ArgumentNullException.ThrowIfNull(resolvePartialWorkProofs);
        ArgumentNullException.ThrowIfNull(hooks);
        try {
            var paths = new StorePaths(repositoryPath);
            if (!StoreDurableFiles.RegularFileExists(paths, paths.DatabasePath)) {
                return new RecapGridStoreUpgradeResult.Absent();
            }
            using FileStream lease = StoreDurableFiles.AcquireExclusive(
                paths, create: false);
            if (!StoreDurableFiles.RegularFileExists(paths, paths.DatabasePath)) {
                return new RecapGridStoreUpgradeResult.Absent();
            }
            string? sidecar = ExistingSidecar(paths);
            if (sidecar is not null) {
                return new RecapGridStoreUpgradeResult.OfflineCleanupRequired(
                    sidecar);
            }
            int version;
            using (SqliteConnection source = OpenRaw(paths.DatabasePath,
                       readOnly: true)) {
                version = ReadV4Version(source);
            }
            if (version == SqliteRecapGridStore.SchemaVersion) {
                return new RecapGridStoreUpgradeResult.AlreadyCurrent();
            }
            if (version != 4) {
                return new RecapGridStoreUpgradeResult.UnsupportedSchema(
                    version);
            }
            IReadOnlyList<RowWork> partialWorkProofs =
                resolvePartialWorkProofs();
            string temporary = Path.Combine(paths.RootPath,
                $".grid.upgrade-v5.{Guid.NewGuid():N}.sqlite");
            paths.RequireSafe(temporary);
            string? backup = null;
            RecapGridStoreUpgradeEvidence? backupEvidence = null;
            bool published = false;
            bool cleanupNeeded = true;
            try {
                if (!apply) {
                    V4Snapshot dryRunSnapshot;
                    using (SqliteConnection source = OpenRaw(paths.DatabasePath,
                               readOnly: true)) {
                        dryRunSnapshot = ReadV4Snapshot(source,
                            partialWorkProofs);
                    }
                    BuildV5Replacement(temporary, dryRunSnapshot);
                    _ = VerifyV5(paths, temporary);
                    hooks.AfterTempVerified?.Invoke();
                    return new RecapGridStoreUpgradeResult.DryRunReady(
                        dryRunSnapshot.RowViews.Count,
                        dryRunSnapshot.Cells.Count);
                }
                backup = paths.DatabasePath + ".v4-backup-"
                    + DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ",
                        System.Globalization.CultureInfo.InvariantCulture)
                    + "-" + Guid.NewGuid().ToString("N") + ".sqlite";
                paths.RequireSafe(backup);
                File.Copy(paths.DatabasePath, backup, overwrite: false);
                StoreDurableFiles.FlushFile(paths, backup);
                StoreDurableFiles.FlushDirectory(paths.RootPath);
                VerifiedV4 verifiedBackup = VerifyV4(
                    paths, backup, partialWorkProofs);
                backupEvidence = verifiedBackup.Evidence;
                hooks.AfterBackupDurable?.Invoke();
                BuildV5Replacement(temporary, verifiedBackup.Snapshot);
                _ = VerifyV5(paths, temporary);
                hooks.AfterTempVerified?.Invoke();
                VerifiedV4 activeBeforeReplace = VerifyV4(paths,
                    paths.DatabasePath, partialWorkProofs);
                if (activeBeforeReplace.Evidence != backupEvidence) {
                    bool cleaned = TryDeleteUpgradeTemporary(temporary);
                    cleanupNeeded = false;
                    return new RecapGridStoreUpgradeResult.PreCommitFailed(
                        backup, backupEvidence, "active-changed-before-replace",
                        "The active V4 Store no longer matches the durable backup.",
                        cleaned, "inspect-active-and-backup-before-a-new-upgrade");
                }
                File.Move(temporary, paths.DatabasePath, overwrite: true);
                published = true;
                hooks.AfterReplaceBeforeDirectoryFsync?.Invoke();
                StoreDurableFiles.FlushDirectory(paths.RootPath);
                hooks.AfterDirectoryFsyncBeforeVerify?.Invoke();
                RecapGridStoreUpgradeEvidence activeEvidence = VerifyV5(
                    paths, paths.DatabasePath);
                hooks.AfterVerify?.Invoke();
                return new RecapGridStoreUpgradeResult.Upgraded(backup,
                    backupEvidence, activeEvidence);
            }
            catch (Exception exception) when (published && !IsFatal(exception)) {
                // The replacement may have reached stable storage even when a
                // later fsync, verifier, or caller interruption failed.  Do
                // not collapse that state into an ordinary validation error.
                return new RecapGridStoreUpgradeResult.CommitIndeterminate(
                    backup!, backupEvidence!, ObserveActive(paths),
                    "inspect-and-verify-active-before-any-restore-or-retry");
            }
            catch (Exception exception) when (apply
                && backupEvidence is not null
                && !IsFatal(exception)) {
                bool cleaned = TryDeleteUpgradeTemporary(temporary);
                cleanupNeeded = false;
                return new RecapGridStoreUpgradeResult.PreCommitFailed(
                    backup, backupEvidence, "precommit-failed", exception.Message,
                    cleaned, backupEvidence is null
                        ? "inspect-active-before-a-new-upgrade"
                        : "inspect-active-and-backup-before-a-new-upgrade");
            }
            finally {
                if (!published && cleanupNeeded) {
                    TryDeleteTemporary(temporary);
                }
            }
        }
        catch (PlatformNotSupportedException) {
            return new RecapGridStoreUpgradeResult.PlatformUnsupported();
        }
        catch (StoreBusyException) {
            return new RecapGridStoreUpgradeResult.Busy();
        }
        catch (SqliteException exception) when (SqliteRecapGridStore.IsBusy(exception)) {
            return new RecapGridStoreUpgradeResult.Busy();
        }
        catch (RecapGridStorePartialProofException exception) {
            return new RecapGridStoreUpgradeResult.Invalid(
                exception.Failure switch {
                    RecapGridStorePartialProofFailure.Unprovable => "partial-proof-unprovable",
                    RecapGridStorePartialProofFailure.Ambiguous => "partial-proof-ambiguous",
                    RecapGridStorePartialProofFailure.Unavailable => "partial-proof-unavailable",
                    _ => "partial-proof-unprovable"
                }, exception.Message);
        }
        catch (Exception exception) when (exception is ArgumentException
            or FormatException
            or OverflowException) {
            return new RecapGridStoreUpgradeResult.Invalid(
                "GridStoreInvalid", exception.Message);
        }
        catch (Exception exception) when (SqliteRecapGridStore.IsStoreFailure(exception)) {
            return new RecapGridStoreUpgradeResult.Invalid(
                RecapGridStoreFactory.ErrorCode(exception), exception.Message);
        }
        catch (Exception exception) when (!IsFatal(exception)) {
            return new RecapGridStoreUpgradeResult.Invalid(
                "GridStoreUpgradeFailed", exception.Message);
        }
    }

    private static VerifiedV4 VerifyV4(
        StorePaths paths,
        string path,
        IReadOnlyList<RowWork> partialWorkProofs
    ) {
        if (!StoreDurableFiles.RegularFileExists(paths, path)) {
            throw new InvalidDataException("V4 backup is absent.");
        }
        using SqliteConnection source = OpenRaw(path, readOnly: true);
        if (ReadV4Version(source) != 4) {
            throw new InvalidDataException("Backup is not a V4 Store.");
        }
        V4Snapshot snapshot = ReadV4Snapshot(source, partialWorkProofs);
        var evidence = new RecapGridStoreUpgradeEvidence(
            new RecapGridStoreIdentity(new RecapGridStoreInstanceId(snapshot.InstanceId), 4),
            snapshot.Cells.Count,
            snapshot.RowViews.Count,
            snapshot.RowViews.Values.Sum(static row => row.Members.Count),
            snapshot.Fulfilled.Count,
            StoreDurableFiles.ComputeWitness(paths, path));
        return new VerifiedV4(snapshot, evidence);
    }

    private static RecapGridStoreUpgradeEvidence VerifyV5(
        StorePaths paths,
        string path
    ) {
        if (!StoreDurableFiles.RegularFileExists(paths, path)) {
            throw new InvalidDataException("V5 replacement is absent.");
        }
        RecapGridStoreInfo info = new SqliteRecapGridStore(
            paths.WithDatabasePathForVerification(path),
            StoreStorageLimits.Production,
            readOnly: true).VerifyFully();
        return new RecapGridStoreUpgradeEvidence(info.Identity,
            info.CellCount, info.RowViewCount, info.RowViewMemberCount,
            info.FulfilledViewCount, StoreDurableFiles.ComputeWitness(paths, path));
    }

    private static RecapGridStoreUpgradeObservation ObserveActive(
        StorePaths paths
    ) {
        int? schema = null;
        RecapGridStoreIdentity? identity = null;
        RecapGridStorePhysicalWitness? witness = null;
        try {
            if (StoreDurableFiles.RegularFileExists(paths, paths.DatabasePath)) {
                using SqliteConnection connection = OpenRaw(paths.DatabasePath,
                    readOnly: true);
                schema = ReadV4Version(connection);
                if (schema == SqliteRecapGridStore.SchemaVersion) {
                    identity = new SqliteRecapGridStore(paths,
                        StoreStorageLimits.Production, readOnly: true).ReadIdentity();
                }
                witness = StoreDurableFiles.ComputeWitness(paths);
            }
        }
        catch (Exception exception) when (!IsFatal(exception)) { }
        return new RecapGridStoreUpgradeObservation(schema, identity, witness);
    }

    private static bool IsFatal(Exception exception) {
        if (exception is OutOfMemoryException
            or StackOverflowException
            or AccessViolationException) {
            return true;
        }
        if (exception is AggregateException aggregate) {
            return aggregate.InnerExceptions.Any(IsFatal);
        }
        return exception.InnerException is { } inner && IsFatal(inner);
    }

    private static bool TryDeleteUpgradeTemporary(string path) {
        TryDeleteTemporary(path);
        return !File.Exists(path)
            && !File.Exists(path + "-journal")
            && !File.Exists(path + "-wal")
            && !File.Exists(path + "-shm");
    }

    private static int ReadV4Version(SqliteConnection connection) {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        object? result = command.ExecuteScalar();
        if (result is null) {
            throw new InvalidDataException("The Store schema version is absent.");
        }
        return Convert.ToInt32(result,
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static V4Snapshot ReadV4Snapshot(
        SqliteConnection source,
        IReadOnlyList<RowWork> partialWorkProofs
    ) {
        ValidateV4Identity(source);
        var cells = new Dictionary<string, V4Cell>(StringComparer.Ordinal);
        using (SqliteCommand command = source.CreateCommand()) {
            command.CommandText = """
                SELECT cell_id,recipe_digest,history_row_id,logical_column_id,
                       definition_digest,outcome,content
                FROM cell_artifact ORDER BY cell_id;
                """;
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read()) {
                var cell = new V4Cell(reader.GetString(0), reader.GetString(1),
                    reader.GetString(2), reader.GetString(3), reader.GetString(4),
                    reader.GetInt32(5), reader.GetString(6));
                if (!cells.TryAdd(cell.Id, cell)) {
                    throw new InvalidDataException("V4 has duplicate cell IDs.");
                }
            }
        }
        var rows = new Dictionary<string, V4Row>(StringComparer.Ordinal);
        using (SqliteCommand command = source.CreateCommand()) {
            command.CommandText = """
                SELECT row_result_id,ref_id,timeline_id,history_row_id,
                       recipe_digest,target_digest,previous_history_row_id,
                       previous_row_result_id,bootstrap_completed
                FROM row_view ORDER BY row_result_id;
                """;
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read()) {
                var row = new V4Row(reader.GetString(0), reader.GetString(1),
                    reader.GetString(2), reader.GetString(3), reader.GetString(4),
                    reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.GetInt64(8) switch {
                        0 => false,
                        1 => true,
                        _ => throw new InvalidDataException("V4 bootstrap flag is invalid.")
                    });
                if (!rows.TryAdd(row.Id, row)) {
                    throw new InvalidDataException("V4 has duplicate row-result IDs.");
                }
            }
        }
        var referencedCells = new HashSet<string>(StringComparer.Ordinal);
        foreach (V4Row row in rows.Values) {
            using SqliteCommand command = source.CreateCommand();
            command.CommandText = """
                SELECT column_ordinal,logical_column_id,definition_digest,cell_id
                FROM row_view_member WHERE row_result_id=$id
                ORDER BY column_ordinal;
                """;
            command.Parameters.AddWithValue("$id", row.Id);
            using SqliteDataReader reader = command.ExecuteReader();
            int ordinal = 0;
            while (reader.Read()) {
                if (reader.GetInt64(0) != ordinal++) {
                    throw new InvalidDataException("V4 row member ordinals are invalid.");
                }
                string cellId = reader.GetString(3);
                if (!cells.TryGetValue(cellId, out V4Cell? cell)
                    || cell.LogicalColumnId != reader.GetString(1)
                    || cell.DefinitionDigest != reader.GetString(2)
                    || cell.HistoryRowId != row.HistoryRowId) {
                    throw new InvalidDataException(
                        "V4 row member cannot prove its referenced cell.");
                }
                row.Members.Add(new V4Member(reader.GetString(1),
                    reader.GetString(2), cellId));
                referencedCells.Add(cellId);
            }
            if (row.Members.Count == 0) {
                throw new InvalidDataException("V4 row has no members.");
            }
        }
        IReadOnlyList<V4PartialWork> partialWorks = RecoverPartialWorks(
            cells,
            rows,
            referencedCells,
            partialWorkProofs
        );
        var fulfilled = new List<V4Fulfilled>();
        using (SqliteCommand command = source.CreateCommand()) {
            command.CommandText = """
                SELECT ref_id,timeline_id,timeline_head_generation,
                       through_history_row_id,recipe_digest,row_result_id
                FROM fulfilled_view_ref;
                """;
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read()) {
                var value = new V4Fulfilled(reader.GetString(0), reader.GetString(1),
                    reader.GetInt64(2), reader.GetString(3), reader.GetString(4),
                    reader.GetString(5));
                if (!rows.ContainsKey(value.RowResultId)) {
                    throw new InvalidDataException("V4 fulfillment lacks its row view.");
                }
                fulfilled.Add(value);
            }
        }
        string instance;
        long cellCount;
        long rowCount;
        long memberCount;
        long fulfilledCount;
        using (SqliteCommand command = source.CreateCommand()) {
            command.CommandText = """
                SELECT store_instance_id,cell_count,row_view_count,
                       row_view_member_count,fulfilled_view_count
                FROM store_metadata WHERE singleton=1;
                """;
            using SqliteDataReader reader = command.ExecuteReader();
            if (!reader.Read()) {
                throw new InvalidDataException("V4 Store metadata is invalid.");
            }
            instance = reader.GetString(0);
            cellCount = reader.GetInt64(1);
            rowCount = reader.GetInt64(2);
            memberCount = reader.GetInt64(3);
            fulfilledCount = reader.GetInt64(4);
            if (reader.Read()) {
                throw new InvalidDataException("V4 Store metadata is invalid.");
            }
        }
        if (cellCount != cells.Count || rowCount != rows.Count
            || memberCount != rows.Values.Sum(static row => row.Members.Count)
            || fulfilledCount != fulfilled.Count) {
            throw new InvalidDataException("V4 Store counters differ from rows.");
        }
        return new V4Snapshot(instance, cells, rows, fulfilled, partialWorks);
    }

    private static IReadOnlyList<V4PartialWork> RecoverPartialWorks(
        IReadOnlyDictionary<string, V4Cell> cells,
        IReadOnlyDictionary<string, V4Row> rows,
        IReadOnlySet<string> referencedCells,
        IReadOnlyList<RowWork> proofs
    ) {
        ArgumentNullException.ThrowIfNull(proofs);
        V4Cell[] unreferenced = cells.Values.Where(cell =>
            !referencedCells.Contains(cell.Id)).ToArray();
        if (unreferenced.Length == 0) {
            if (proofs.Count != 0) {
                throw new InvalidDataException(
                    "V4 partial-work proof exists but the Store has no partial cell.");
            }
            return [];
        }
        var keyedProofs = proofs.GroupBy(static work => work.Key)
            .ToDictionary(static group => group.Key,
                static group => group.ToArray());
        var recovered = new List<V4PartialWork>();
        foreach (IGrouping<(string Recipe, string History), V4Cell> group
                 in unreferenced.GroupBy(static cell => (
                     cell.RecipeDigest, cell.HistoryRowId))) {
            V4Cell[] partialCells = group.ToArray();
            RowWork[] matching = keyedProofs.Where(pair =>
                pair.Key.RootRecipeDigest.Value == group.Key.Recipe
                && pair.Key.HistoryRowId.Value == group.Key.History)
                .SelectMany(static pair => pair.Value)
                .ToArray();
            if (matching.Length != 1) {
                throw new InvalidDataException(
                    "V4 has an orphan partial cell without a uniquely "
                    + "provable scope/root/prior; the upgrade refuses to guess "
                    + "its assignment.");
            }
            RowWork proof = matching[0];
            ValidatePartialProof(proof, partialCells, cells, rows,
                referencedCells);
            recovered.Add(new V4PartialWork(proof, partialCells));
            _ = keyedProofs.Remove(proof.Key);
        }
        if (keyedProofs.Count != 0) {
            throw new InvalidDataException(
                "V4 partial-work proof does not correspond to a partial cell.");
        }
        return recovered;
    }

    private static void ValidatePartialProof(
        RowWork proof,
        IReadOnlyList<V4Cell> partialCells,
        IReadOnlyDictionary<string, V4Cell> cells,
        IReadOnlyDictionary<string, V4Row> rows,
        IReadOnlySet<string> referencedCells
    ) {
        var partialByColumn = partialCells.ToDictionary(
            static cell => cell.LogicalColumnId,
            StringComparer.Ordinal
        );
        if (partialByColumn.Count != partialCells.Count) {
            throw new InvalidDataException(
                "V4 partial work contains duplicate logical columns.");
        }
        if (proof.PreviousRowResultId is { } previousResult) {
            if (proof.PreviousHistoryRowId is not { } previousHistory
                || !rows.TryGetValue(previousResult.Value, out V4Row? row)
                || row.RefId != proof.Key.RefId.ToHexString()
                || row.TimelineId != proof.Key.TimelineId.Value
                || row.RecipeDigest != proof.Key.RootRecipeDigest.Value
                || row.HistoryRowId != previousHistory.Value) {
                throw new InvalidDataException(
                    "V4 partial work prior cannot be proved by a persisted row.");
            }
        }
        else if (proof.PreviousHistoryRowId is not null) {
            throw new InvalidDataException(
                "V4 partial work has an incoherent exact prior.");
        }
        foreach (RowWorkAssignment assignment in proof.OrderedAssignments) {
            BuildTargetColumn target = proof.ProducerTarget.OrderedColumns
                .Single(column => column.LogicalColumnId
                    == assignment.LogicalColumnId);
            if (assignment.IsEvaluate) {
                if (partialByColumn.TryGetValue(
                        assignment.LogicalColumnId.Value,
                        out V4Cell? partial)
                    && partial.DefinitionDigest != target.DefinitionDigest.Value) {
                    throw new InvalidDataException(
                        "V4 partial cell conflicts with its proved target.");
                }
                continue;
            }
            if (partialByColumn.ContainsKey(assignment.LogicalColumnId.Value)
                || assignment.ReusedCellId is not { } reused
                || !referencedCells.Contains(reused.Value)
                || !cells.TryGetValue(reused.Value, out V4Cell? source)
                || source.LogicalColumnId != assignment.LogicalColumnId.Value
                || source.DefinitionDigest != target.DefinitionDigest.Value
                || source.HistoryRowId != proof.Key.HistoryRowId.Value) {
                throw new InvalidDataException(
                    "V4 partial work reuse cannot be proved by its source cell.");
            }
        }
        foreach (V4Cell cell in partialCells) {
            RowWorkAssignment? assignment = proof.OrderedAssignments
                .SingleOrDefault(value => value.LogicalColumnId.Value
                    == cell.LogicalColumnId);
            if (assignment is null || !assignment.IsEvaluate
                || cell.RecipeDigest != proof.Key.RootRecipeDigest.Value
                || cell.HistoryRowId != proof.Key.HistoryRowId.Value) {
                throw new InvalidDataException(
                    "V4 partial cell does not belong to its proved RowWork.");
            }
        }
    }


    private static void ValidateV4Identity(SqliteConnection source) {
        using SqliteCommand command = source.CreateCommand();
        command.CommandText = """
            SELECT (SELECT application_id FROM pragma_application_id),
                   (SELECT schema_version FROM store_metadata WHERE singleton=1),
                   (SELECT COUNT(*) FROM store_metadata);
            """;
        using (SqliteDataReader reader = command.ExecuteReader()) {
            if (!reader.Read()
                || reader.GetInt32(0) != SqliteRecapGridStore.ApplicationId
                || reader.GetInt32(1) != 4 || reader.GetInt64(2) != 1) {
                throw new InvalidDataException("V4 Store identity is invalid.");
            }
        }
        command.CommandText = "PRAGMA integrity_check;";
        if (!string.Equals(command.ExecuteScalar() as string, "ok",
                StringComparison.Ordinal)) {
            throw new InvalidDataException("V4 Store integrity_check failed.");
        }
        command.CommandText = "PRAGMA foreign_key_check;";
        using (SqliteDataReader keys = command.ExecuteReader()) {
            if (keys.Read()) {
                throw new InvalidDataException("V4 Store foreign_key_check failed.");
            }
        }
    }

    private static void BuildV5Replacement(string path, V4Snapshot snapshot) {
        _ = SqliteRecapGridStore.CreateDatabase(path,
            StoreStorageLimits.Production);
        using SqliteConnection destination = OpenRaw(path, readOnly: false);
        using SqliteTransaction transaction = destination.BeginTransaction();
        Execute(destination, transaction, "PRAGMA foreign_keys = OFF;");
        try {
            CopyV4Tables(destination, transaction, snapshot);
            Execute(destination, transaction, "PRAGMA foreign_keys = ON;");
            using SqliteCommand check = destination.CreateCommand();
            check.Transaction = transaction;
            check.CommandText = "PRAGMA foreign_key_check;";
            using SqliteDataReader keys = check.ExecuteReader();
            if (keys.Read()) {
                throw new InvalidDataException("V5 replacement has a foreign-key error.");
            }
            transaction.Commit();
        }
        catch {
            transaction.Rollback();
            throw;
        }
    }

    private static void CopyV4Tables(SqliteConnection destination,
        SqliteTransaction transaction, V4Snapshot snapshot) {
        Execute(destination, transaction, """
            UPDATE store_metadata SET store_instance_id=$instance,
                cell_count=$cells,row_view_count=$rows,
                row_view_member_count=$members,fulfilled_view_count=$fulfilled
            WHERE singleton=1;
            """, ("$instance", snapshot.InstanceId),
            ("$cells", snapshot.Cells.Count), ("$rows", snapshot.RowViews.Count),
            ("$members", snapshot.RowViews.Values.Sum(static row => row.Members.Count)),
            ("$fulfilled", snapshot.Fulfilled.Count));
        foreach (V4Cell cell in snapshot.Cells.Values.OrderBy(static value => value.Id,
                     StringComparer.Ordinal)) {
            Execute(destination, transaction, """
                INSERT INTO cell_artifact(cell_id,recipe_digest,history_row_id,
                    work_id,logical_column_id,definition_digest,outcome,content)
                VALUES($id,$recipe,$row,NULL,$column,$definition,$outcome,$content);
                """, ("$id", cell.Id), ("$recipe", cell.RecipeDigest),
                ("$row", cell.HistoryRowId), ("$column", cell.LogicalColumnId),
                ("$definition", cell.DefinitionDigest), ("$outcome", cell.Outcome),
                ("$content", cell.Content));
        }
        var visited = new HashSet<string>(StringComparer.Ordinal);
        foreach (V4Row row in snapshot.RowViews.Values) {
            InsertRow(destination, transaction, snapshot, row, visited);
        }
        foreach (V4PartialWork partial in snapshot.PartialWorks) {
            InsertPartialWork(destination, transaction, partial);
        }
        foreach (V4Fulfilled fulfilled in snapshot.Fulfilled) {
            Execute(destination, transaction, """
                INSERT INTO fulfilled_view_ref(ref_id,timeline_id,
                    timeline_head_generation,through_history_row_id,
                    recipe_digest,row_result_id)
                VALUES($ref,$timeline,$generation,$through,$recipe,$row);
                """, ("$ref", fulfilled.RefId), ("$timeline", fulfilled.TimelineId),
                ("$generation", fulfilled.Generation), ("$through", fulfilled.ThroughRowId),
                ("$recipe", fulfilled.RecipeDigest), ("$row", fulfilled.RowResultId));
        }
    }

    private static void InsertRow(SqliteConnection destination,
        SqliteTransaction transaction, V4Snapshot snapshot, V4Row row,
        HashSet<string> visited) {
        if (!visited.Add(row.Id)) { return; }
        if (row.PreviousRowResultId is { } previous) {
            if (!snapshot.RowViews.TryGetValue(previous, out V4Row? predecessor)
                || predecessor.RefId != row.RefId
                || predecessor.TimelineId != row.TimelineId
                || predecessor.RecipeDigest != row.RecipeDigest
                || predecessor.HistoryRowId != row.PreviousHistoryRowId) {
                throw new InvalidDataException("V4 predecessor proof is invalid.");
            }
            InsertRow(destination, transaction, snapshot, predecessor, visited);
        }
        Execute(destination, transaction, """
            INSERT INTO row_view(row_result_id,ref_id,timeline_id,history_row_id,
                recipe_digest,target_digest,work_id,previous_history_row_id,
                previous_row_result_id,bootstrap_completed)
            VALUES($id,$ref,$timeline,$history,$recipe,$target,NULL,$previousHistory,
                $previousResult,$bootstrap);
            """, ("$id", row.Id), ("$ref", row.RefId),
            ("$timeline", row.TimelineId), ("$history", row.HistoryRowId),
            ("$recipe", row.RecipeDigest), ("$target", row.TargetDigest),
            ("$previousHistory", (object?)row.PreviousHistoryRowId ?? DBNull.Value),
            ("$previousResult", (object?)row.PreviousRowResultId ?? DBNull.Value),
            ("$bootstrap", row.BootstrapCompleted ? 1 : 0));
        for (int ordinal = 0; ordinal < row.Members.Count; ordinal++) {
            V4Member member = row.Members[ordinal];
            Execute(destination, transaction, """
                INSERT INTO row_view_member(row_result_id,column_ordinal,
                    logical_column_id,definition_digest,cell_id)
                VALUES($row,$ordinal,$column,$definition,$cell);
                """, ("$row", row.Id), ("$ordinal", ordinal),
                ("$column", member.LogicalColumnId),
                ("$definition", member.DefinitionDigest), ("$cell", member.CellId));
        }
        RowWork work = CreateRowWork(snapshot, row);
        Execute(destination, transaction, """
            INSERT INTO row_work(work_id,ref_id,timeline_id,root_recipe_digest,
                history_row_id,previous_history_row_id,previous_row_result_id,
                producer_target,canonical)
            VALUES($id,$ref,$timeline,$recipe,$history,$previousHistory,
                $previousResult,$target,$canonical);
            """, ("$id", work.WorkId.Value),
            ("$ref", work.Key.RefId.ToHexString()),
            ("$timeline", work.Key.TimelineId.Value),
            ("$recipe", work.Key.RootRecipeDigest.Value),
            ("$history", work.Key.HistoryRowId.Value),
            ("$previousHistory", (object?)work.PreviousHistoryRowId?.Value ?? DBNull.Value),
            ("$previousResult", (object?)work.PreviousRowResultId?.Value ?? DBNull.Value),
            ("$target", work.ProducerTarget.ToCanonicalBytes()),
            ("$canonical", work.ToCanonicalBytes()));
        for (int ordinal = 0; ordinal < work.OrderedAssignments.Count; ordinal++) {
            RowWorkAssignment assignment = work.OrderedAssignments[ordinal];
            BuildTargetColumn target = work.ProducerTarget.OrderedColumns[ordinal];
            Execute(destination, transaction, """
                INSERT INTO row_work_member(work_id,column_ordinal,
                    logical_column_id,definition_digest,reused_cell_id)
                VALUES($work,$ordinal,$column,$definition,$reused);
                """, ("$work", work.WorkId.Value), ("$ordinal", ordinal),
                ("$column", assignment.LogicalColumnId.Value),
                ("$definition", target.DefinitionDigest.Value),
                ("$reused", (object?)assignment.ReusedCellId?.Value ?? DBNull.Value));
        }
        Execute(destination, transaction,
            "UPDATE row_view SET work_id=$work WHERE row_result_id=$row;",
            ("$work", work.WorkId.Value), ("$row", row.Id));
        for (int index = 0; index < work.OrderedAssignments.Count; index++) {
            if (!work.OrderedAssignments[index].IsEvaluate) { continue; }
            string cellId = row.Members[index].CellId;
            Execute(destination, transaction, """
                UPDATE cell_artifact SET work_id=$work
                WHERE cell_id=$cell AND work_id IS NULL;
                """, ("$work", work.WorkId.Value), ("$cell", cellId));
        }
    }

    private static RowWork CreateRowWork(V4Snapshot snapshot, V4Row row) {
        BuildTarget target = BuildTarget.Create(row.Members.Select(static value =>
            new BuildTargetColumn(new LogicalColumnId(value.LogicalColumnId),
                new MaintainerDefinitionDigest(value.DefinitionDigest))));
        if (target.Digest.Value != row.TargetDigest) {
            throw new InvalidDataException(
                "V4 row target digest cannot be proved from its members.");
        }
        return new RowWork(new RowWorkKey(ParseRef(row.RefId),
                new TimelineId(row.TimelineId),
                new GridBuildRecipeDigest(row.RecipeDigest),
                new HistoryRowId(row.HistoryRowId)), target,
            row.PreviousHistoryRowId is null ? null : new HistoryRowId(
                row.PreviousHistoryRowId),
            row.PreviousRowResultId is null ? null : new RowResultId(
                row.PreviousRowResultId),
            row.Members.Select(member => {
                V4Cell cell = snapshot.Cells[member.CellId];
                bool evaluated = cell.RecipeDigest == row.RecipeDigest
                    && cell.HistoryRowId == row.HistoryRowId;
                return new RowWorkAssignment(new LogicalColumnId(
                    member.LogicalColumnId), evaluated ? null : new CellId(
                    member.CellId));
            }));
    }

    private static void InsertPartialWork(
        SqliteConnection destination,
        SqliteTransaction transaction,
        V4PartialWork partial
    ) {
        InsertWork(destination, transaction, partial.Work);
        foreach (V4Cell cell in partial.Cells) {
            Execute(destination, transaction, """
                UPDATE cell_artifact SET work_id=$work
                WHERE cell_id=$cell AND work_id IS NULL;
                """, ("$work", partial.Work.WorkId.Value),
                ("$cell", cell.Id));
        }
    }

    private static void InsertWork(
        SqliteConnection destination,
        SqliteTransaction transaction,
        RowWork work
    ) {
        Execute(destination, transaction, """
            INSERT INTO row_work(work_id,ref_id,timeline_id,root_recipe_digest,
                history_row_id,previous_history_row_id,previous_row_result_id,
                producer_target,canonical)
            VALUES($id,$ref,$timeline,$recipe,$history,$previousHistory,
                $previousResult,$target,$canonical);
            """, ("$id", work.WorkId.Value),
            ("$ref", work.Key.RefId.ToHexString()),
            ("$timeline", work.Key.TimelineId.Value),
            ("$recipe", work.Key.RootRecipeDigest.Value),
            ("$history", work.Key.HistoryRowId.Value),
            ("$previousHistory", (object?)work.PreviousHistoryRowId?.Value
                ?? DBNull.Value),
            ("$previousResult", (object?)work.PreviousRowResultId?.Value
                ?? DBNull.Value),
            ("$target", work.ProducerTarget.ToCanonicalBytes()),
            ("$canonical", work.ToCanonicalBytes()));
        for (int ordinal = 0; ordinal < work.OrderedAssignments.Count;
             ordinal++) {
            RowWorkAssignment assignment = work.OrderedAssignments[ordinal];
            BuildTargetColumn target = work.ProducerTarget
                .OrderedColumns[ordinal];
            Execute(destination, transaction, """
                INSERT INTO row_work_member(work_id,column_ordinal,
                    logical_column_id,definition_digest,reused_cell_id)
                VALUES($work,$ordinal,$column,$definition,$reused);
                """, ("$work", work.WorkId.Value), ("$ordinal", ordinal),
                ("$column", assignment.LogicalColumnId.Value),
                ("$definition", target.DefinitionDigest.Value),
                ("$reused", (object?)assignment.ReusedCellId?.Value
                    ?? DBNull.Value));
        }
    }

    private static RefId ParseRef(string value) {
        StoreSyntax.RequireLowerHex(value, 16, nameof(value));
        return new RefId(ulong.Parse(value,
            System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture));
    }

    private static SqliteConnection OpenRaw(string path, bool readOnly) {
        var builder = new SqliteConnectionStringBuilder {
            DataSource = path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 0
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        return connection;
    }

    private static void Execute(SqliteConnection connection,
        SqliteTransaction transaction, string sql,
        params (string Name, object Value)[] values) {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach ((string name, object value) in values) {
            command.Parameters.AddWithValue(name, value);
        }
        _ = command.ExecuteNonQuery();
    }

    private sealed record V4Snapshot(string InstanceId,
        IReadOnlyDictionary<string, V4Cell> Cells,
        IReadOnlyDictionary<string, V4Row> RowViews,
        IReadOnlyList<V4Fulfilled> Fulfilled,
        IReadOnlyList<V4PartialWork> PartialWorks);

    private sealed record VerifiedV4(
        V4Snapshot Snapshot,
        RecapGridStoreUpgradeEvidence Evidence
    );

    private sealed record V4Cell(string Id, string RecipeDigest,
        string HistoryRowId, string LogicalColumnId, string DefinitionDigest,
        int Outcome, string Content);

    private sealed class V4Row(string id, string refId, string timelineId,
        string historyRowId, string recipeDigest, string targetDigest,
        string? previousHistoryRowId, string? previousRowResultId,
        bool bootstrapCompleted) {
        internal string Id { get; } = id;
        internal string RefId { get; } = refId;
        internal string TimelineId { get; } = timelineId;
        internal string HistoryRowId { get; } = historyRowId;
        internal string RecipeDigest { get; } = recipeDigest;
        internal string TargetDigest { get; } = targetDigest;
        internal string? PreviousHistoryRowId { get; } = previousHistoryRowId;
        internal string? PreviousRowResultId { get; } = previousRowResultId;
        internal bool BootstrapCompleted { get; } = bootstrapCompleted;
        internal List<V4Member> Members { get; } = [];
    }

    private sealed record V4Member(string LogicalColumnId,
        string DefinitionDigest, string CellId);

    private sealed record V4Fulfilled(string RefId, string TimelineId,
        long Generation, string ThroughRowId, string RecipeDigest,
        string RowResultId);

    private sealed record V4PartialWork(
        RowWork Work,
        IReadOnlyList<V4Cell> Cells
    );

}
