using System.Collections.ObjectModel;
using System.Reflection;
using Microsoft.Data.Sqlite;

namespace Atelia.SessionJournal.RecapGrid.Store;

internal sealed class SqliteRecapGridStore {
    internal const int SchemaVersion = 5;
    internal const int ApplicationId = 0x41544752;
    private const long SqliteNativeMaximumPageCountRequest = 4_294_967_294L;

    private static readonly string SchemaSql = ReadSchemaSql();
    private static readonly Lazy<SchemaEntry[]> ExpectedSchema = new(
        BuildExpectedSchema,
        LazyThreadSafetyMode.ExecutionAndPublication
    );
    private readonly StorePaths _paths;
    private readonly StoreStorageLimits _limits;
    private readonly StorePersistenceTestHooks _hooks;
    private readonly bool _readOnly;
    private readonly object _invalidGate = new();
    private string? _invalidCode;
    private string? _invalidDetail;

    internal SqliteRecapGridStore(
        StorePaths paths,
        StoreStorageLimits limits,
        StorePersistenceTestHooks? hooks = null,
        bool readOnly = false
    ) {
        _paths = paths;
        _limits = limits;
        _hooks = hooks ?? StorePersistenceTestHooks.None;
        _readOnly = readOnly;
    }

    internal static RecapGridStoreIdentity CreateDatabase(
        string path,
        StoreStorageLimits limits
    ) {
        var instance = RecapGridStoreInstanceId.Generate();
        using SqliteConnection connection = OpenConnection(
            path,
            create: true,
            readOnly: false
        );
        ConfigureCreated(connection, limits);
        using (SqliteCommand schema = connection.CreateCommand()) {
            schema.CommandText = SchemaSql;
            schema.ExecuteNonQuery();
        }
        using (SqliteCommand metadata = connection.CreateCommand()) {
            metadata.CommandText = """
                INSERT INTO store_metadata(
                    singleton, schema_version, store_instance_id,
                    cell_count, row_view_count,
                    row_view_member_count, fulfilled_view_count
                ) VALUES (1, $schemaVersion, $instance, 0, 0, 0, 0);
                """;
            metadata.Parameters.AddWithValue("$schemaVersion", SchemaVersion);
            metadata.Parameters.AddWithValue("$instance", instance.Value);
            metadata.ExecuteNonQuery();
        }
        RequireFilePresent(path);
        ValidateSchemaIdentity(connection);
        return new RecapGridStoreIdentity(instance, SchemaVersion);
    }

    internal RecapGridStoreIdentity ReadIdentity() {
        using SqliteConnection connection = OpenVerifiedConnection();
        return ReadIdentity(connection, transaction: null);
    }

    internal RecapGridStoreInfo Inspect() {
        using SqliteConnection connection = OpenVerifiedConnection();
        return ReadInfo(connection, transaction: null);
    }

    private RecapGridStoreInfo ReadInfo(
        SqliteConnection connection,
        SqliteTransaction? transaction
    ) {
        RecapGridStoreIdentity identity = ReadIdentity(connection, transaction);
        StoreCounts counts = ReadCounts(connection, transaction);
        string version;
        string source;
        using (SqliteCommand command = connection.CreateCommand()) {
            command.Transaction = transaction;
            command.CommandText = "SELECT sqlite_version(), sqlite_source_id();";
            using SqliteDataReader reader = command.ExecuteReader();
            if (!reader.Read()) {
                throw new InvalidDataException(
                    "SQLite runtime identity is unavailable."
                );
            }
            version = reader.GetString(0);
            source = reader.GetString(1);
        }
        var options = new List<string>();
        using (SqliteCommand command = connection.CreateCommand()) {
            command.Transaction = transaction;
            command.CommandText = "PRAGMA compile_options;";
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read()) {
                options.Add(reader.GetString(0));
            }
        }
        options.Sort(StringComparer.Ordinal);
        return new RecapGridStoreInfo(
            identity,
            new FileInfo(_paths.DatabasePath).Length,
            counts.CellCount,
            counts.RowViewCount,
            counts.RowViewMemberCount,
            counts.FulfilledViewCount,
            version,
            source,
            Array.AsReadOnly(options.ToArray())
        );
    }

    internal RecapGridStoreInfo VerifyFully() {
        using SqliteConnection connection = OpenVerifiedConnection();
        using SqliteTransaction transaction =
            connection.BeginTransaction(deferred: true);
        using (SqliteCommand integrity = connection.CreateCommand()) {
            integrity.Transaction = transaction;
            integrity.CommandText = $"PRAGMA integrity_check({RecapGridStoreLimits.MaximumVerificationErrors});";
            using SqliteDataReader reader = integrity.ExecuteReader();
            int count = 0;
            while (reader.Read()) {
                count++;
                if (!string.Equals(
                        reader.GetString(0),
                        "ok",
                        StringComparison.Ordinal)) {
                    throw new InvalidDataException(
                        "SQLite integrity_check reported corruption."
                    );
                }
            }
            if (count != 1) {
                throw new InvalidDataException(
                    "SQLite integrity_check did not return one healthy result."
                );
            }
        }
        using (SqliteCommand foreignKeys = connection.CreateCommand()) {
            foreignKeys.Transaction = transaction;
            foreignKeys.CommandText =
                "SELECT * FROM pragma_foreign_key_check LIMIT 1;";
            using SqliteDataReader reader = foreignKeys.ExecuteReader();
            if (reader.Read()) {
                throw new InvalidDataException(
                    "SQLite foreign_key_check reported an orphan."
                );
            }
        }
        StoreCounts stored = ReadCounts(connection, transaction);
        using (SqliteCommand counts = connection.CreateCommand()) {
            counts.Transaction = transaction;
            counts.CommandText = """
                SELECT
                    (SELECT COUNT(*) FROM cell_artifact),
                    (SELECT COUNT(*) FROM row_view),
                    (SELECT COUNT(*) FROM row_view_member),
                    (SELECT COUNT(*) FROM fulfilled_view_ref);
                """;
            using SqliteDataReader reader = counts.ExecuteReader();
            if (!reader.Read()
                || reader.GetInt64(0) != stored.CellCount
                || reader.GetInt64(1) != stored.RowViewCount
                || reader.GetInt64(2) != stored.RowViewMemberCount
                || reader.GetInt64(3) != stored.FulfilledViewCount) {
                throw new InvalidDataException(
                    "RecapGrid Store counters differ from physical tables."
                );
            }
        }
        VerifyAllRowWorks(connection, transaction);
        VerifyAllCells(connection, transaction);
        VerifyAllRowViews(connection, transaction);
        VerifyAllFulfilled(connection, transaction);
        RecapGridStoreInfo info = ReadInfo(connection, transaction);
        transaction.Commit();
        return info;
    }

    internal RecapGridStoreExportPage ExportPage(
        RecapGridStoreExportCursor? after,
        bool includeContent
    ) {
        using SqliteConnection connection = OpenVerifiedConnection();
        using SqliteTransaction transaction =
            connection.BeginTransaction(deferred: true);
        var items = new List<RecapGridStoreExportItem>();
        int totalBytes = 0;
        bool incomplete = false;
        RecapGridStoreExportCursor? last = null;
        int phase = after switch {
            null => 0,
            { IsCell: true } => 0,
            { IsRowView: true } => 1,
            { IsFulfilled: true } => 2,
            { IsRowWork: true } => 3,
            _ => throw new InvalidDataException(
                "The export cursor has an unknown kind."
            )
        };
        for (; phase < 4 && !incomplete; phase++) {
            string? digestAfter = phase switch {
                0 when after?.IsCell == true => after.Key,
                1 when after?.IsRowView == true => after.Key,
                3 when after?.IsRowWork == true => after.Key,
                _ => null
            };
            bool exhausted = phase switch {
                0 => ExportIdTable(
                    connection,
                    transaction,
                    "cell_artifact",
                    "cell_id",
                    "cell",
                    digestAfter,
                    includeContent,
                    items,
                    ref totalBytes,
                    ref last
                ),
                1 => ExportIdTable(
                    connection,
                    transaction,
                    "row_view",
                    "row_result_id",
                    "row-view",
                    digestAfter,
                    includeContent,
                    items,
                    ref totalBytes,
                    ref last
                ),
                2 => ExportFulfilledTable(
                    connection,
                    transaction,
                    after?.IsFulfilled == true ? after : null,
                    includeContent,
                    items,
                    ref totalBytes,
                    ref last
                ),
                _ => ExportRowWorkTable(
                    connection,
                    transaction,
                    digestAfter,
                    includeContent,
                    items,
                    ref totalBytes,
                    ref last
                )
            };
            incomplete = !exhausted;
            after = null;
        }
        transaction.Commit();
        return new RecapGridStoreExportPage(
            Array.AsReadOnly(items.ToArray()),
            incomplete ? last : null,
            incomplete
        );
    }

    internal RecapCellArtifact? ReadCellBySlot(CellSlot slot) {
        ArgumentNullException.ThrowIfNull(slot);
        using SqliteConnection connection = OpenVerifiedConnection();
        return ReadCellBySlotCore(connection, null, slot);
    }

    internal RecapCellArtifact? ReadCellById(CellId cellId) {
        if (cellId.Value is null) {
            throw new ArgumentException(
                "CellId must not be default.",
                nameof(cellId)
            );
        }
        using SqliteConnection connection = OpenVerifiedConnection();
        return ReadCellByIdCore(connection, transaction: null, cellId);
    }

    internal RecapGridMissingResult FindMissing(RowBuildSpec spec) {
        ArgumentNullException.ThrowIfNull(spec);
        using SqliteConnection connection = OpenVerifiedConnection();
        var missing = new List<CellSlot>();
        for (int index = 0; index < spec.OrderedAssignments.Count; index++) {
            RowBuildAssignment assignment = spec.OrderedAssignments[index];
            switch (assignment) {
                case RowBuildAssignment.Evaluate evaluate:
                    RecapCellArtifact? winner = ReadCellBySlotCore(
                        connection,
                        transaction: null,
                        evaluate.Slot
                    );
                    if (winner is null) {
                        missing.Add(evaluate.Slot);
                    }
                    else if (winner.DefinitionDigest != spec.DefinitionAt(index)) {
                        throw new InvalidDataException("A Cell winner has the wrong definition for its slot.");
                    }
                    break;
                case RowBuildAssignment.Reuse reuse:
                    RecapCellArtifact? reused = ReadCellByIdCore(
                        connection,
                        transaction: null,
                        reuse.Cell.Id
                    );
                    if (reused is null
                        || reused != reuse.Cell) {
                        return new RecapGridMissingResult.PrerequisiteMissing(
                            reuse.LogicalColumnId,
                            reuse.Cell.Id
                        );
                    }
                    break;
                default:
                    throw new InvalidDataException(
                        "The RowBuildSpec contains an unknown assignment."
                    );
            }
        }
        return missing.Count == 0
            ? new RecapGridMissingResult.Complete()
            : new RecapGridMissingResult.Missing(
                Array.AsReadOnly(missing.ToArray())
            );
    }

    internal RecapRowView? ReadRowView(RowResultId rowResultId) {
        if (rowResultId.Value is null) {
            throw new ArgumentException(
                "RowResultId must not be default.",
                nameof(rowResultId)
            );
        }
        using SqliteConnection connection = OpenVerifiedConnection();
        return ReadRowViewCore(connection, transaction: null, rowResultId);
    }

    internal RecapRowView? ReadRowViewAt(RowViewAssignmentKey key) {
        ArgumentNullException.ThrowIfNull(key);
        using SqliteConnection connection = OpenVerifiedConnection();
        return ReadRowViewAtCore(connection, transaction: null, key);
    }

    internal RowWork? ReadRowWork(RowWorkKey key) {
        ArgumentNullException.ThrowIfNull(key);
        using SqliteConnection connection = OpenVerifiedConnection();
        return ReadRowWorkCore(connection, transaction: null, key);
    }

    internal RecapGridRowWorkPutResult PutRowWork(RowWork proposed) {
        ArgumentNullException.ThrowIfNull(proposed);
        if (_readOnly) {
            return new RecapGridRowWorkPutResult.Invalid("StoreReadOnly", "The Store is read-only.");
        }
        if (TryInvalid(out string code, out string detail)) {
            return new RecapGridRowWorkPutResult.Invalid(code, detail);
        }
        try {
            using SqliteConnection connection = OpenVerifiedConnection();
            using WriteTransaction transaction = BeginWriteTransaction(connection);
            RowWork? existing = ReadRowWorkCore(connection, transaction, proposed.Key);
            if (existing is not null) {
                transaction.Rollback();
                return existing.ToCanonicalBytes().SequenceEqual(proposed.ToCanonicalBytes())
                    ? new RecapGridRowWorkPutResult.AlreadyPresent(existing)
                    : new RecapGridRowWorkPutResult.SelectionConflict(existing);
            }
            InsertRowWork(connection, transaction, proposed);
            transaction.Commit();
            return new RecapGridRowWorkPutResult.Inserted(proposed);
        }
        catch (SqliteException exception) when (IsBusy(exception)) {
            return new RecapGridRowWorkPutResult.Busy();
        }
        catch (Exception exception) when (IsStoreFailure(exception)) {
            (code, detail) = LatchInvalid(exception);
            return new RecapGridRowWorkPutResult.Invalid(code, detail);
        }
    }

    internal RecapGridFulfilledView? ReadFulfilled(FulfilledViewKey key) {
        ArgumentNullException.ThrowIfNull(key);
        using SqliteConnection connection = OpenVerifiedConnection();
        RowResultId? rowResultId = ReadFulfilledCore(
            connection,
            transaction: null,
            key
        );
        return rowResultId is null ? null : new RecapGridFulfilledView(rowResultId.Value);
    }

    internal RecapGridCellPutResult PutCell(RowBuildSpec spec, RecapCellDraft proposed) {
        ArgumentNullException.ThrowIfNull(proposed);
        if (_readOnly) {
            return new RecapGridCellPutResult.Rejected("StoreReadOnly");
        }
        if (TryInvalid(out string code, out string detail)) {
            return new RecapGridCellPutResult.Invalid(code, detail);
        }
        for (int attempt = 1; attempt <= _limits.MaximumCommitAttempts;
             attempt++) {
            bool commitAttempted = false;
            bool committed = false;
            SqliteConnection? writeConnection = null;
            try {
                _hooks.BeforeCellBegin?.Invoke();
                using SqliteConnection connection =
                    writeConnection = OpenVerifiedConnection();
                using WriteTransaction transaction =
                    BeginWriteTransaction(connection);
                if (!DraftMatchesSpec(spec, proposed)) {
                    transaction.Rollback();
                    return new RecapGridCellPutResult.Rejected("CellSpecMismatch");
                }
                if (!TryValidateStoredWork(connection, transaction, spec, out string? workError)) {
                    transaction.Rollback();
                    return new RecapGridCellPutResult.Rejected(workError!);
                }
                if (!TryValidatePredecessor(connection, transaction, spec, out _, out string? previousError)) {
                    transaction.Rollback();
                    return new RecapGridCellPutResult.Rejected(previousError!);
                }
                RecapCellArtifact? winner = ReadCellBySlotCore(connection, transaction, proposed.Slot);
                if (winner is not null) {
                    transaction.Rollback();
                    if (winner.DefinitionDigest != proposed.DefinitionDigest) {
                        return new RecapGridCellPutResult.Invalid("CellDefinitionMismatch", "The stored winner differs from the immutable slot definition.");
                    }
                    return new RecapGridCellPutResult.AlreadyFilled(winner);
                }
                var stored = new RecapCellArtifact(CellId.Generate(), proposed.Slot,
                    proposed.DefinitionDigest, proposed.Outcome, proposed.Content);
                StoreCounts counts = ReadCounts(connection, transaction);
                InsertCell(connection, transaction, stored);
                WriteCounts(
                    connection,
                    transaction,
                    counts with {
                        CellCount = StoreCountMath.Increment(counts.CellCount)
                    }
                );
                _hooks.BeforeCellCommit?.Invoke();
                commitAttempted = true;
                transaction.Commit(_hooks.AfterCellNativeCommitReturn);
                committed = true;
                _hooks.AfterCellCommit?.Invoke();
                return new RecapGridCellPutResult.Inserted(stored);
            }
            catch (StoreCommitBusyRolledBackException) {
                if (attempt == _limits.MaximumCommitAttempts) {
                    return new RecapGridCellPutResult.Busy();
                }
                _hooks.BeforeLocalCommitRetry?.Invoke(attempt);
                if (_limits.CommitRetryDelayMilliseconds > 0) {
                    Thread.Sleep(_limits.CommitRetryDelayMilliseconds);
                }
            }
            catch (SqliteException exception)
                when (!commitAttempted && IsBusy(exception)) {
                if (attempt == _limits.MaximumCommitAttempts) {
                    return new RecapGridCellPutResult.Busy();
                }
                _hooks.BeforeLocalCommitRetry?.Invoke(attempt);
                if (_limits.CommitRetryDelayMilliseconds > 0) {
                    Thread.Sleep(_limits.CommitRetryDelayMilliseconds);
                }
            }
            catch (SqliteException exception)
                when (!commitAttempted && IsFull(exception)) {
                return new RecapGridCellPutResult.Limit(
                    "SqliteFull"
                );
            }
            catch (Exception) when (commitAttempted || committed) {
                writeConnection?.Dispose();
                RecapCellArtifact? observed = TryObserveCell(
                    proposed.Slot
                );
                return new RecapGridCellPutResult.CommitIndeterminate(
                    proposed.Slot,
                    observed
                );
            }
            catch (Exception exception) when (IsStoreFailure(exception)) {
                (code, detail) = LatchInvalid(exception);
                return new RecapGridCellPutResult.Invalid(code, detail);
            }
        }
        return new RecapGridCellPutResult.Busy();
    }

    internal RecapGridRowViewPutResult PutRowView(
        RowBuildSpec spec,
        IReadOnlyList<RecapCellArtifact> selectedCells
    ) {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(selectedCells);
        if (_readOnly) {
            return new RecapGridRowViewPutResult.Rejected("StoreReadOnly");
        }
        if (TryInvalid(out string code, out string detail)) {
            return new RecapGridRowViewPutResult.Invalid(code, detail);
        }
        for (int attempt = 1; attempt <= _limits.MaximumCommitAttempts;
             attempt++) {
            bool commitAttempted = false;
            bool committed = false;
            SqliteConnection? writeConnection = null;
            try {
                _hooks.BeforeRowViewBegin?.Invoke();
                using SqliteConnection connection =
                    writeConnection = OpenVerifiedConnection();
                using WriteTransaction transaction =
                    BeginWriteTransaction(connection);
                RecapCellArtifact[]? selected = ResolveSpecCells(
                    connection,
                    transaction,
                    spec
                );
                if (selected is null) {
                    transaction.Rollback();
                    return new RecapGridRowViewPutResult
                        .PrerequisiteMissing("SelectedCellUnavailable");
                }
                if (!TryValidateStoredWork(connection, transaction, spec, out string? workError)) {
                    transaction.Rollback();
                    return new RecapGridRowViewPutResult.Rejected(workError!);
                }
                RecapRowView proposed;
                try {
                    if (!selected.SequenceEqual(selectedCells)) {
                        transaction.Rollback();
                        return new RecapGridRowViewPutResult.Rejected("RowViewSpecMismatch");
                    }
                    proposed = RecapRowView.Create(RowResultId.Generate(), spec, selected);
                }
                catch (ArgumentException) {
                    transaction.Rollback();
                    return new RecapGridRowViewPutResult.Rejected("RowViewSpecMismatch");
                }
                RecapRowView? assigned = ReadRowViewAtCore(connection, transaction, spec.Coordinate.AssignmentKey);
                if (assigned is not null) {
                    transaction.Rollback();
                    return assigned.HasSameAssignment(proposed)
                        ? new RecapGridRowViewPutResult.AlreadyPresent(assigned)
                        : LatchRowViewInvalid("RowViewAssignmentConflict", "A row assignment has different members or predecessor.");
                }
                if (!TryValidatePredecessor(connection, transaction, spec, out RecapRowView? predecessor, out string? previousError)) {
                    transaction.Rollback();
                    return new RecapGridRowViewPutResult.PrerequisiteMissing(previousError!);
                }
                bool expectedBootstrapCompleted =
                    spec.Recipe.Kind == GridBuildRecipeKind.Full
                    || spec.Recipe.BootstrapThroughRowId is null
                    || predecessor?.BootstrapCompleted == true
                    || spec.Recipe.BootstrapThroughRowId
                        == proposed.HistoryRowId;
                if (proposed.BootstrapCompleted
                    != expectedBootstrapCompleted) {
                    transaction.Rollback();
                    return new RecapGridRowViewPutResult.Rejected(
                        "BootstrapRecurrenceMismatch"
                    );
                }
                StoreCounts counts = ReadCounts(connection, transaction);
                long memberCount = proposed.OrderedCells.Count;
                InsertRowView(connection, transaction, proposed, spec.Work?.WorkId);
                InsertRowViewMembers(connection, transaction, proposed);
                WriteCounts(
                    connection,
                    transaction,
                    counts with {
                        RowViewCount = StoreCountMath.Increment(
                            counts.RowViewCount
                        ),
                        RowViewMemberCount = StoreCountMath.Add(
                            counts.RowViewMemberCount,
                            memberCount
                        )
                    }
                );
                _hooks.BeforeRowViewCommit?.Invoke();
                commitAttempted = true;
                transaction.Commit(_hooks.AfterRowViewNativeCommitReturn);
                committed = true;
                _hooks.AfterRowViewCommit?.Invoke();
                return new RecapGridRowViewPutResult.Inserted(proposed);
            }
            catch (StoreCommitBusyRolledBackException) {
                if (attempt == _limits.MaximumCommitAttempts) {
                    return new RecapGridRowViewPutResult.Busy();
                }
                _hooks.BeforeLocalCommitRetry?.Invoke(attempt);
                if (_limits.CommitRetryDelayMilliseconds > 0) {
                    Thread.Sleep(_limits.CommitRetryDelayMilliseconds);
                }
            }
            catch (Microsoft.Data.Sqlite.SqliteException exception)
                when (!commitAttempted && IsBusy(exception)) {
                if (attempt == _limits.MaximumCommitAttempts) {
                    return new RecapGridRowViewPutResult.Busy();
                }
                _hooks.BeforeLocalCommitRetry?.Invoke(attempt);
                if (_limits.CommitRetryDelayMilliseconds > 0) {
                    Thread.Sleep(_limits.CommitRetryDelayMilliseconds);
                }
            }
            catch (Microsoft.Data.Sqlite.SqliteException exception)
                when (!commitAttempted && IsFull(exception)) {
                return new RecapGridRowViewPutResult.Limit(
                    "SqliteFull"
                );
            }
            catch (Exception) when (commitAttempted || committed) {
                writeConnection?.Dispose();
                return new RecapGridRowViewPutResult.CommitIndeterminate(
                    spec.Coordinate.AssignmentKey,
                    TryObserveRowViewAt(spec.Coordinate.AssignmentKey)
                );
            }
            catch (Exception exception) when (IsStoreFailure(exception)) {
                (code, detail) = LatchInvalid(exception);
                return new RecapGridRowViewPutResult.Invalid(code, detail);
            }
        }
        return new RecapGridRowViewPutResult.Busy();
    }

    internal RecapGridFulfilledPutResult PutFulfilled(
        FulfilledViewKey key,
        RowResultId rowResultId
    ) {
        ArgumentNullException.ThrowIfNull(key);
        if (rowResultId.Value is null) {
            throw new ArgumentException(
                "RowResultId must not be default.",
                nameof(rowResultId)
            );
        }
        if (_readOnly) {
            return new RecapGridFulfilledPutResult.Rejected("StoreReadOnly");
        }
        if (TryInvalid(out string code, out string detail)) {
            return new RecapGridFulfilledPutResult.Invalid(code, detail);
        }
        for (int attempt = 1; attempt <= _limits.MaximumCommitAttempts;
             attempt++) {
            bool commitAttempted = false;
            bool committed = false;
            SqliteConnection? writeConnection = null;
            try {
                _hooks.BeforeFulfilledBegin?.Invoke();
                using SqliteConnection connection =
                    writeConnection = OpenVerifiedConnection();
                using WriteTransaction transaction =
                    BeginWriteTransaction(connection);
                RowResultId? existing = ReadFulfilledCore(
                    connection,
                    transaction,
                    key
                );
                if (existing is not null) {
                    transaction.Rollback();
                    if (existing == rowResultId) {
                        return new RecapGridFulfilledPutResult
                            .AlreadyPresent();
                    }
                    (code, detail) = LatchInvalid(new StoreException(
                        "FulfilledViewConflict",
                        "A fulfilled-view key is already bound to another RowView."
                    ));
                    return new RecapGridFulfilledPutResult.Invalid(
                        code,
                        detail
                    );
                }
                RecapRowView? view = ReadRowViewCore(
                    connection,
                    transaction,
                    rowResultId
                );
                if (view is null) {
                    transaction.Rollback();
                    return new RecapGridFulfilledPutResult
                        .PrerequisiteMissing("RowViewUnavailable");
                }
                if (view.RefId != key.RefId
                    || view.TimelineId != key.TimelineId
                    || view.RecipeDigest != key.RecipeDigest
                    || view.HistoryRowId
                        != key.ThroughRowId) {
                    transaction.Rollback();
                    return new RecapGridFulfilledPutResult.Rejected(
                        "FulfilledViewScopeMismatch"
                    );
                }
                StoreCounts counts = ReadCounts(connection, transaction);
                InsertFulfilled(
                    connection,
                    transaction,
                    key,
                    rowResultId
                );
                WriteCounts(
                    connection,
                    transaction,
                    counts with {
                        FulfilledViewCount = StoreCountMath.Increment(
                            counts.FulfilledViewCount
                        )
                    }
                );
                _hooks.BeforeFulfilledCommit?.Invoke();
                commitAttempted = true;
                transaction.Commit(
                    _hooks.AfterFulfilledNativeCommitReturn
                );
                committed = true;
                _hooks.AfterFulfilledCommit?.Invoke();
                return new RecapGridFulfilledPutResult.Inserted();
            }
            catch (StoreCommitBusyRolledBackException) {
                if (attempt == _limits.MaximumCommitAttempts) {
                    return new RecapGridFulfilledPutResult.Busy();
                }
                _hooks.BeforeLocalCommitRetry?.Invoke(attempt);
                if (_limits.CommitRetryDelayMilliseconds > 0) {
                    Thread.Sleep(_limits.CommitRetryDelayMilliseconds);
                }
            }
            catch (Microsoft.Data.Sqlite.SqliteException exception)
                when (!commitAttempted && IsBusy(exception)) {
                if (attempt == _limits.MaximumCommitAttempts) {
                    return new RecapGridFulfilledPutResult.Busy();
                }
                _hooks.BeforeLocalCommitRetry?.Invoke(attempt);
                if (_limits.CommitRetryDelayMilliseconds > 0) {
                    Thread.Sleep(_limits.CommitRetryDelayMilliseconds);
                }
            }
            catch (Microsoft.Data.Sqlite.SqliteException exception)
                when (!commitAttempted && IsFull(exception)) {
                return new RecapGridFulfilledPutResult.Limit(
                    "SqliteFull"
                );
            }
            catch (Exception) when (commitAttempted || committed) {
                writeConnection?.Dispose();
                return new RecapGridFulfilledPutResult.CommitIndeterminate(
                    key,
                    TryObserveFulfilled(key)
                );
            }
            catch (Exception exception) when (IsStoreFailure(exception)) {
                (code, detail) = LatchInvalid(exception);
                return new RecapGridFulfilledPutResult.Invalid(code, detail);
            }
        }
        return new RecapGridFulfilledPutResult.Busy();
    }

    internal (string Code, string Detail) LatchInvalid(
        Exception exception
    ) {
        string code = exception switch {
            StoreException store => store.Code,
            StoreUnsupportedSchemaException =>
                "GridStoreUnsupportedSchema",
            FileNotFoundException => "GridStoreSlotMissing",
            UnauthorizedAccessException => "GridStoreUnauthorized",
            InvalidDataException => "GridStoreInvalid",
            SqliteException sqlite => $"GridStoreSqlite{sqlite.SqliteErrorCode}",
            IOException => "GridStoreIoInvalid",
            _ => "GridStoreInvalid"
        };
        lock (_invalidGate) {
            _invalidCode ??= code;
            _invalidDetail ??= exception.Message;
            return (_invalidCode, _invalidDetail);
        }
    }

    internal bool TryInvalid(out string code, out string detail) {
        lock (_invalidGate) {
            code = _invalidCode!;
            detail = _invalidDetail!;
            return _invalidCode is not null;
        }
    }

    internal static bool IsBusy(SqliteException exception)
        => exception.SqliteErrorCode is 5 or 6;
    internal static bool IsFull(SqliteException exception)
        => exception.SqliteErrorCode is 13 or 18;
    internal static bool IsStoreFailure(Exception exception)
        => exception is StoreException
            or StoreUnsupportedSchemaException
            or FileNotFoundException
            or UnauthorizedAccessException
            or InvalidDataException
            or SqliteException
            or IOException
            or OverflowException;

    private RecapCellArtifact? TryObserveCell(CellSlot key) {
        try {
            using SqliteConnection connection = OpenVerifiedConnection();
            return ReadCellBySlotCore(connection, null, key);
        }
        catch {
            return null;
        }
    }

    private RecapRowView? TryObserveRowViewAt(RowViewAssignmentKey key) {
        try {
            using SqliteConnection connection = OpenVerifiedConnection();
            return ReadRowViewAtCore(connection, null, key);
        }
        catch {
            return null;
        }
    }

    private RowResultId? TryObserveFulfilled(FulfilledViewKey key) {
        try {
            using SqliteConnection connection = OpenVerifiedConnection();
            return ReadFulfilledCore(connection, null, key);
        }
        catch {
            return null;
        }
    }

    private RecapGridRowViewPutResult.Invalid LatchRowViewInvalid(
        string code,
        string detail
    ) {
        (string latchedCode, string latchedDetail) = LatchInvalid(
            new StoreException(code, detail)
        );
        return new RecapGridRowViewPutResult.Invalid(
            latchedCode,
            latchedDetail
        );
    }

    private static RecapCellArtifact[]? ResolveSpecCells(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        RowBuildSpec spec
    ) {
        var cells = new RecapCellArtifact[spec.OrderedAssignments.Count];
        for (int index = 0; index < cells.Length; index++) {
            RowBuildAssignment assignment = spec.OrderedAssignments[index];
            RecapCellArtifact? cell = assignment switch {
                RowBuildAssignment.Evaluate evaluate
                    => ReadCellBySlotCore(
                        connection,
                        transaction,
                        evaluate.Slot
                    ),
                RowBuildAssignment.Reuse reuse
                    => ReadCellByIdCore(
                        connection,
                        transaction,
                        reuse.Cell.Id
                    ),
                _ => null
            };
            if (cell is null) {
                return null;
            }
            bool exact = assignment switch {
                RowBuildAssignment.Evaluate evaluate
                    => cell.Slot == evaluate.Slot,
                RowBuildAssignment.Reuse reuse => cell == reuse.Cell,
                _ => false
            };
            if (!exact) {
                throw new InvalidDataException(
                    "A row assignment differs from its stored Cell."
                );
            }
            cells[index] = cell;
        }
        return cells;
    }

    private static bool DraftMatchesSpec(RowBuildSpec spec, RecapCellDraft draft) {
        for (int index = 0; index < spec.OrderedAssignments.Count; index++) {
            if (spec.OrderedAssignments[index] is RowBuildAssignment.Evaluate evaluate
                && evaluate.Slot == draft.Slot
                && spec.DefinitionAt(index) == draft.DefinitionDigest) { return true; }
        }
        return false;
    }

    private static bool TryValidateStoredWork(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        RowBuildSpec spec,
        out string? error
    ) {
        error = null;
        if (spec.Work is null) {
            error = "RowWorkRequired";
            return false;
        }
        RowWork? stored = ReadRowWorkCore(connection, transaction, spec.Work.Key);
        if (stored is null) {
            error = "RowWorkUnavailable";
            return false;
        }
        if (!stored.ToCanonicalBytes().SequenceEqual(spec.Work.ToCanonicalBytes())) {
            error = "RowWorkMismatch";
            return false;
        }
        return true;
    }

    private static bool TryValidatePredecessor(SqliteConnection connection, SqliteTransaction? transaction,
        RowBuildSpec spec, out RecapRowView? predecessor, out string? error) {
        predecessor = null;
        error = null;
        if (spec.PreviousRowResultId is not { } previous) { return true; }
        predecessor = ReadRowViewAtCore(connection, transaction,
            new RowViewAssignmentKey(spec.RefId, spec.TimelineId, spec.RecipeDigest, spec.PreviousHistoryRowId!.Value));
        if (predecessor is null) { error = "PreviousAssignmentUnavailable"; return false; }
        if (predecessor.Id != previous) {
            error = "PreviousAssignmentMismatch";
            return false;
        }
        return true;
    }

    private static void InsertRowView(SqliteConnection connection, SqliteTransaction? transaction, RecapRowView view, RowWorkId? workId) {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO row_view(row_result_id,ref_id,timeline_id,history_row_id,
                recipe_digest,target_digest,work_id,previous_history_row_id,previous_row_result_id,bootstrap_completed)
            VALUES($id,$ref,$timeline,$row,$recipe,$target,$work,$previousRow,$previous,$bootstrap);
            """;
        command.Parameters.AddWithValue("$id", view.Id.Value);
        command.Parameters.AddWithValue("$ref", view.RefId.ToHexString());
        command.Parameters.AddWithValue("$timeline", view.TimelineId.Value);
        command.Parameters.AddWithValue("$row", view.HistoryRowId.Value);
        command.Parameters.AddWithValue("$recipe", view.RecipeDigest.Value);
        command.Parameters.AddWithValue("$target", view.TargetDigest.Value);
        command.Parameters.AddWithValue("$work", (object?)workId?.Value ?? DBNull.Value);
        command.Parameters.AddWithValue("$previousRow", (object?)view.PreviousHistoryRowId?.Value ?? DBNull.Value);
        command.Parameters.AddWithValue("$previous", (object?)view.PreviousRowResultId?.Value ?? DBNull.Value);
        command.Parameters.AddWithValue("$bootstrap", view.BootstrapCompleted ? 1 : 0);
        command.ExecuteNonQuery();
    }

    private static void InsertRowViewMembers(SqliteConnection connection, SqliteTransaction? transaction, RecapRowView view) {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO row_view_member(row_result_id,column_ordinal,logical_column_id,definition_digest,cell_id)
            VALUES($row,$ordinal,$column,$definition,$cell);
            """;
        var row = command.Parameters.Add("$row", SqliteType.Text);
        var ordinal = command.Parameters.Add("$ordinal", SqliteType.Integer);
        var column = command.Parameters.Add("$column", SqliteType.Text);
        var definition = command.Parameters.Add("$definition", SqliteType.Text);
        var cell = command.Parameters.Add("$cell", SqliteType.Text);
        for (int index = 0; index < view.OrderedCells.Count; index++) {
            RecapRowViewCell member = view.OrderedCells[index];
            row.Value = view.Id.Value;
            ordinal.Value = index;
            column.Value = member.LogicalColumnId.Value;
            definition.Value = member.DefinitionDigest.Value;
            cell.Value = member.CellId.Value;
            command.ExecuteNonQuery();
        }
    }

    private static RowResultId? ReadRowViewAssignmentId(SqliteConnection connection, SqliteTransaction? transaction,
        RowViewAssignmentKey key) {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT row_result_id FROM row_view WHERE ref_id=$ref AND timeline_id=$timeline
                AND recipe_digest=$recipe AND history_row_id=$row;
            """;
        command.Parameters.AddWithValue("$ref", key.RefId.ToHexString());
        command.Parameters.AddWithValue("$timeline", key.TimelineId.Value);
        command.Parameters.AddWithValue("$recipe", key.RecipeDigest.Value);
        command.Parameters.AddWithValue("$row", key.HistoryRowId.Value);
        return command.ExecuteScalar() is string value ? DecodeStoredValue(() => new RowResultId(value)) : null;
    }

    private static RecapRowView? ReadRowViewAtCore(SqliteConnection connection, SqliteTransaction? transaction,
        RowViewAssignmentKey key) {
        RowResultId? id = ReadRowViewAssignmentId(connection, transaction, key);
        if (id is null) { return null; }
        RecapRowView view = ReadRowViewCore(connection, transaction, id.Value)
            ?? throw new InvalidDataException("A row assignment references a missing result.");
        if (view.Coordinate.AssignmentKey != key) {
            throw new InvalidDataException("A row assignment differs from its stored coordinate.");
        }
        return view;
    }

    private static RowWork? ReadRowWorkCore(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        RowWorkKey key
    ) {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT work_id,ref_id,timeline_id,root_recipe_digest,history_row_id,
                previous_history_row_id,previous_row_result_id,producer_target,canonical
            FROM row_work
            WHERE ref_id=$ref AND timeline_id=$timeline
                AND root_recipe_digest=$root AND history_row_id=$row;
            """;
        command.Parameters.AddWithValue("$ref", key.RefId.ToHexString());
        command.Parameters.AddWithValue("$timeline", key.TimelineId.Value);
        command.Parameters.AddWithValue("$root", key.RootRecipeDigest.Value);
        command.Parameters.AddWithValue("$row", key.HistoryRowId.Value);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read()) {
            return null;
        }
        RowWorkPhysical physical = ReadRowWorkPhysical(reader);
        reader.Close();
        RowWork work = ReadAndValidateRowWorkPhysical(
            connection,
            transaction,
            physical
        );
        if (work.Key != key) {
            throw new InvalidDataException("A RowWork key differs from its stored canonical identity.");
        }
        return work;
    }

    private static RowWork? ReadRowWorkByIdCore(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        RowWorkId workId,
        bool validateReferences = true
    ) {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT work_id,ref_id,timeline_id,root_recipe_digest,history_row_id,
                previous_history_row_id,previous_row_result_id,producer_target,canonical
            FROM row_work WHERE work_id=$id;
            """;
        command.Parameters.AddWithValue("$id", workId.Value);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read()) {
            return null;
        }
        RowWorkPhysical physical = ReadRowWorkPhysical(reader);
        reader.Close();
        RowWork work = ReadAndValidateRowWorkPhysical(
            connection,
            transaction,
            physical,
            validateReferences
        );
        if (work.WorkId != workId) {
            throw new InvalidDataException(
                "A RowWork identity differs from its physical key."
            );
        }
        return work;
    }

    private static RowWorkPhysical ReadRowWorkPhysical(
        SqliteDataReader reader
    ) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.GetFieldValue<byte[]>(7),
        reader.GetFieldValue<byte[]>(8)
    );

    private static RowWork ReadAndValidateRowWorkPhysical(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        RowWorkPhysical physical,
        bool validateReferences = true
    ) {
        RowWork work = RowWork.DecodeCanonical(physical.Canonical);
        if (!string.Equals(work.WorkId.Value, physical.WorkId,
                StringComparison.Ordinal)
            || !string.Equals(work.Key.RefId.ToHexString(), physical.RefId,
                StringComparison.Ordinal)
            || !string.Equals(work.Key.TimelineId.Value, physical.TimelineId,
                StringComparison.Ordinal)
            || !string.Equals(work.Key.RootRecipeDigest.Value, physical.Root,
                StringComparison.Ordinal)
            || !string.Equals(work.Key.HistoryRowId.Value, physical.HistoryRow,
                StringComparison.Ordinal)
            || !string.Equals(work.PreviousHistoryRowId?.Value,
                physical.PreviousHistoryRow, StringComparison.Ordinal)
            || !string.Equals(work.PreviousRowResultId?.Value,
                physical.PreviousRowResult, StringComparison.Ordinal)
            || !work.ProducerTarget.ToCanonicalBytes()
                .SequenceEqual(physical.ProducerTarget)) {
            throw new InvalidDataException(
                "A RowWork physical projection differs from its canonical value."
            );
        }

        using (SqliteCommand members = connection.CreateCommand()) {
            members.Transaction = transaction;
            members.CommandText = """
                SELECT column_ordinal,logical_column_id,definition_digest,reused_cell_id
                FROM row_work_member WHERE work_id=$id ORDER BY column_ordinal;
                """;
            members.Parameters.AddWithValue("$id", work.WorkId.Value);
            using SqliteDataReader reader = members.ExecuteReader();
            int ordinal = 0;
            while (reader.Read()) {
                if (ordinal >= work.OrderedAssignments.Count
                    || reader.GetInt64(0) != ordinal) {
                    throw new InvalidDataException(
                        "A RowWork member count or ordinal is invalid."
                    );
                }
                RowWorkAssignment assignment = work.OrderedAssignments[ordinal];
                BuildTargetColumn column = work.ProducerTarget.OrderedColumns[ordinal];
                string? reused = reader.IsDBNull(3) ? null : reader.GetString(3);
                if (!string.Equals(reader.GetString(1),
                        assignment.LogicalColumnId.Value,
                        StringComparison.Ordinal)
                    || !string.Equals(reader.GetString(1),
                        column.LogicalColumnId.Value,
                        StringComparison.Ordinal)
                    || !string.Equals(reader.GetString(2),
                        column.DefinitionDigest.Value,
                        StringComparison.Ordinal)
                    || !string.Equals(reused,
                        assignment.ReusedCellId?.Value,
                        StringComparison.Ordinal)) {
                    throw new InvalidDataException(
                        "A RowWork member differs from its canonical assignment."
                    );
                }
                ordinal++;
            }
            if (ordinal != work.OrderedAssignments.Count) {
                throw new InvalidDataException(
                    "A RowWork member count differs from its canonical assignments."
                );
            }
        }

        if (!validateReferences) {
            return work;
        }

        for (int index = 0; index < work.OrderedAssignments.Count; index++) {
            RowWorkAssignment assignment = work.OrderedAssignments[index];
            if (assignment.ReusedCellId is not { } reusedCellId) {
                continue;
            }
            RecapCellArtifact source = ReadCellByIdCore(
                connection,
                transaction,
                reusedCellId
            ) ?? throw new InvalidDataException(
                "A RowWork reuse assignment references a missing Cell."
            );
            BuildTargetColumn column = work.ProducerTarget.OrderedColumns[index];
            if (source.Slot.HistoryRowId != work.Key.HistoryRowId
                || source.LogicalColumnId != assignment.LogicalColumnId
                || source.DefinitionDigest != column.DefinitionDigest) {
                throw new InvalidDataException(
                    "A RowWork reuse source differs from its frozen assignment."
                );
            }
            RowWorkId sourceWorkId = source.Slot.WorkId
                ?? throw new InvalidDataException(
                    "A RowWork reuse source is missing its own RowWork identity."
                );
            RowWork sourceWork = ReadRowWorkByIdCore(
                connection,
                transaction,
                sourceWorkId,
                validateReferences: false
            ) ?? throw new InvalidDataException(
                "A RowWork reuse source references a missing source RowWork."
            );
            ValidateCellAgainstWork(source, sourceWork);
        }

        if (work.PreviousRowResultId is { } previousResult) {
            RowResultId? assigned = ReadRowViewAssignmentId(
                connection,
                transaction,
                new RowViewAssignmentKey(
                    work.Key.RefId,
                    work.Key.TimelineId,
                    work.Key.RootRecipeDigest,
                    work.PreviousHistoryRowId!.Value
                )
            );
            if (assigned != previousResult) {
                throw new InvalidDataException(
                    "A RowWork exact predecessor is unavailable in its root scope."
                );
            }
        }
        return work;
    }

    private static void InsertRowWork(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        RowWork work
    ) {
        using (SqliteCommand command = connection.CreateCommand()) {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO row_work(work_id,ref_id,timeline_id,root_recipe_digest,
                    history_row_id,previous_history_row_id,previous_row_result_id,
                    producer_target,canonical)
                VALUES($id,$ref,$timeline,$root,$row,$previousRow,$previousResult,
                    $target,$canonical);
                """;
            command.Parameters.AddWithValue("$id", work.WorkId.Value);
            command.Parameters.AddWithValue("$ref", work.Key.RefId.ToHexString());
            command.Parameters.AddWithValue("$timeline", work.Key.TimelineId.Value);
            command.Parameters.AddWithValue("$root", work.Key.RootRecipeDigest.Value);
            command.Parameters.AddWithValue("$row", work.Key.HistoryRowId.Value);
            command.Parameters.AddWithValue("$previousRow", (object?)work.PreviousHistoryRowId?.Value ?? DBNull.Value);
            command.Parameters.AddWithValue("$previousResult", (object?)work.PreviousRowResultId?.Value ?? DBNull.Value);
            command.Parameters.AddWithValue("$target", work.ProducerTarget.ToCanonicalBytes());
            command.Parameters.AddWithValue("$canonical", work.ToCanonicalBytes());
            command.ExecuteNonQuery();
        }
        using SqliteCommand member = connection.CreateCommand();
        member.Transaction = transaction;
        member.CommandText = """
            INSERT INTO row_work_member(work_id,column_ordinal,logical_column_id,
                definition_digest,reused_cell_id)
            VALUES($work,$ordinal,$column,$definition,$reused);
            """;
        var workId = member.Parameters.Add("$work", SqliteType.Text);
        var ordinal = member.Parameters.Add("$ordinal", SqliteType.Integer);
        var column = member.Parameters.Add("$column", SqliteType.Text);
        var definition = member.Parameters.Add("$definition", SqliteType.Text);
        var reused = member.Parameters.Add("$reused", SqliteType.Text);
        for (int index = 0; index < work.OrderedAssignments.Count; index++) {
            RowWorkAssignment assignment = work.OrderedAssignments[index];
            BuildTargetColumn target = work.ProducerTarget.OrderedColumns[index];
            workId.Value = work.WorkId.Value;
            ordinal.Value = index;
            column.Value = assignment.LogicalColumnId.Value;
            definition.Value = target.DefinitionDigest.Value;
            reused.Value = (object?)assignment.ReusedCellId?.Value ?? DBNull.Value;
            member.ExecuteNonQuery();
        }
    }

    private static RecapRowView? ReadRowViewCore(SqliteConnection connection, SqliteTransaction? transaction, RowResultId id)
        => DecodeStoredValue(() => ReadRowViewFields(connection, transaction, id));

    private static RecapRowView? ReadRowViewFields(SqliteConnection connection, SqliteTransaction? transaction, RowResultId id) {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT ref_id,timeline_id,history_row_id,recipe_digest,target_digest,
                previous_history_row_id,previous_row_result_id,bootstrap_completed
            FROM row_view WHERE row_result_id=$id;
            """;
        command.Parameters.AddWithValue("$id", id.Value);
        RowViewCoordinate coordinate;
        using (SqliteDataReader reader = command.ExecuteReader()) {
            if (!reader.Read()) { return null; }
            coordinate = new RowViewCoordinate(ParseRef(reader.GetString(0)),
                new(reader.GetString(1)), new(reader.GetString(2)),
                new(reader.GetString(3)), new(reader.GetString(4)),
                reader.IsDBNull(5) ? null : new Atelia.SessionJournal.HistoryTimeline.HistoryRowId(reader.GetString(5)),
                reader.IsDBNull(6) ? null : new RowResultId(reader.GetString(6)),
                reader.GetInt64(7) switch { 0 => false, 1 => true, _ => throw new InvalidDataException("Invalid bootstrap flag.") });
        }
        using SqliteCommand members = connection.CreateCommand();
        members.Transaction = transaction;
        members.CommandText = """
            SELECT m.column_ordinal,m.logical_column_id,m.definition_digest,m.cell_id,
                c.logical_column_id,c.definition_digest,c.history_row_id
            FROM row_view_member AS m LEFT JOIN cell_artifact AS c ON c.cell_id=m.cell_id
            WHERE m.row_result_id=$id ORDER BY m.column_ordinal;
            """;
        members.Parameters.AddWithValue("$id", id.Value);
        var values = new List<RecapRowViewCell>();
        using (SqliteDataReader reader = members.ExecuteReader()) {
            while (reader.Read()) {
                if (values.Count >= RecapGridLimits.MaximumColumnCount || reader.GetInt64(0) != values.Count) {
                    throw new InvalidDataException("A row member count or ordinal is invalid.");
                }
                if (reader.IsDBNull(4) || reader.IsDBNull(5) || reader.IsDBNull(6)
                    || reader.GetString(1) != reader.GetString(4)
                    || reader.GetString(2) != reader.GetString(5)
                    || reader.GetString(6) != coordinate.HistoryRowId.Value) {
                    throw new InvalidDataException("A row member differs from its cell or history row.");
                }
                values.Add(new RecapRowViewCell(new(reader.GetString(1)), new(reader.GetString(2)), new(reader.GetString(3))));
            }
        }
        return new RecapRowView(id, coordinate, values);
    }

    private static Atelia.EventJournal.RefId ParseRef(string value) {
        StoreSyntax.RequireLowerHex(value, 16, nameof(value));
        return new Atelia.EventJournal.RefId(ulong.Parse(value, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture));
    }

    private static void InsertFulfilled(SqliteConnection connection, SqliteTransaction? transaction,
        FulfilledViewKey key, RowResultId id) {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO fulfilled_view_ref(ref_id,timeline_id,timeline_head_generation,
                through_history_row_id,recipe_digest,row_result_id)
            VALUES($ref,$timeline,$generation,$through,$recipe,$id);
            """;
        BindFulfilled(command, key);
        command.Parameters.AddWithValue("$id", id.Value);
        command.ExecuteNonQuery();
    }

    private static void BindFulfilled(SqliteCommand command, FulfilledViewKey key) {
        command.Parameters.AddWithValue("$ref", key.RefId.ToHexString());
        command.Parameters.AddWithValue("$timeline", key.TimelineId.Value);
        command.Parameters.AddWithValue("$generation", key.TimelineHeadGeneration);
        command.Parameters.AddWithValue("$through", key.ThroughRowId.Value);
        command.Parameters.AddWithValue("$recipe", key.RecipeDigest.Value);
    }

    private static RowResultId? ReadFulfilledCore(SqliteConnection connection, SqliteTransaction? transaction,
        FulfilledViewKey key) {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT row_result_id FROM fulfilled_view_ref WHERE ref_id=$ref AND timeline_id=$timeline
                AND timeline_head_generation=$generation AND through_history_row_id=$through AND recipe_digest=$recipe;
            """;
        BindFulfilled(command, key);
        if (command.ExecuteScalar() is not string value) { return null; }
        var id = DecodeStoredValue(() => new RowResultId(value));
        ValidateFulfilledTarget(connection, transaction, key, id);
        return id;
    }

    private static void ValidateFulfilledTarget(SqliteConnection connection, SqliteTransaction? transaction,
        FulfilledViewKey key, RowResultId id) {
        RecapRowView view = ReadRowViewCore(connection, transaction, id)
            ?? throw new InvalidDataException("A fulfillment references a missing row.");
        if (view.RefId != key.RefId || view.TimelineId != key.TimelineId
            || view.RecipeDigest != key.RecipeDigest || view.HistoryRowId != key.ThroughRowId) {
            throw new InvalidDataException("A fulfillment differs from its row scope.");
        }
    }

    private static void InsertCell(SqliteConnection connection, SqliteTransaction? transaction, RecapCellArtifact cell) {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO cell_artifact(cell_id,recipe_digest,history_row_id,work_id,logical_column_id,definition_digest,outcome,content)
            VALUES($id,$recipe,$row,$work,$column,$definition,$outcome,$content);
            """;
        command.Parameters.AddWithValue("$id", cell.Id.Value);
        BindSlot(command, cell.Slot);
        command.Parameters.AddWithValue("$definition", cell.DefinitionDigest.Value);
        command.Parameters.AddWithValue("$outcome", (int)cell.Outcome);
        command.Parameters.AddWithValue("$content", cell.Content);
        command.ExecuteNonQuery();
    }

    private static void BindSlot(SqliteCommand command, CellSlot slot) {
        command.Parameters.AddWithValue("$recipe", slot.RecipeDigest.Value);
        command.Parameters.AddWithValue("$row", slot.HistoryRowId.Value);
        command.Parameters.AddWithValue("$work", (object?)slot.WorkId?.Value ?? DBNull.Value);
        command.Parameters.AddWithValue("$column", slot.LogicalColumnId.Value);
    }

    private static RecapCellArtifact? ReadCellBySlotCore(SqliteConnection connection, SqliteTransaction? transaction, CellSlot slot) {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = slot.WorkId is { }
            ? CellSelect + " WHERE work_id=$work AND logical_column_id=$column;"
            : CellSelect + " WHERE recipe_digest=$recipe AND history_row_id=$row AND logical_column_id=$column;";
        BindSlot(command, slot);
        return ReadCellValue(command);
    }

    private static RecapCellArtifact? ReadCellByIdCore(SqliteConnection connection, SqliteTransaction? transaction, CellId id) {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = CellSelect + " WHERE cell_id=$id;";
        command.Parameters.AddWithValue("$id", id.Value);
        return ReadCellValue(command);
    }

    private const string CellSelect = "SELECT cell_id,recipe_digest,history_row_id,work_id,logical_column_id,definition_digest,outcome,length(CAST(content AS BLOB)),CAST(content AS BLOB) FROM cell_artifact";
    private static RecapCellArtifact? ReadCellValue(SqliteCommand command)
        => DecodeStoredValue(() => ReadCellFields(command));

    private static RecapCellArtifact? ReadCellFields(SqliteCommand command) {
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read()) { return null; }
        long length = reader.GetInt64(7);
        if (length is < 0 or > RecapGridLimits.MaximumContentUtf8Bytes) {
            throw new InvalidDataException("A cell content exceeds its UTF-8 byte bound.");
        }
        string content;
        try { content = new System.Text.UTF8Encoding(false, true).GetString(reader.GetFieldValue<byte[]>(8)); }
        catch (System.Text.DecoderFallbackException exception) {
            throw new InvalidDataException("Cell content is not valid UTF-8.", exception);
        }
        if (RecapGridSyntax.Utf8Length(content) != length) {
            throw new InvalidDataException("Cell content is not valid UTF-8.");
        }
        return new RecapCellArtifact(new(reader.GetString(0)),
            reader.IsDBNull(3)
                ? new CellSlot(new(reader.GetString(1)), new(reader.GetString(2)), new(reader.GetString(4)))
                : new CellSlot(new(reader.GetString(1)), new(reader.GetString(2)), new RowWorkId(reader.GetString(3)), new(reader.GetString(4))),
            new(reader.GetString(5)), (RecapCellOutcome)reader.GetInt32(6), content);
    }

    private SqliteConnection OpenVerifiedConnection() {
        RequireFilePresent(_paths.DatabasePath);
        SqliteConnection connection = OpenConnection(
            _paths.DatabasePath,
            create: false,
            _readOnly
        );
        try {
            if (_readOnly) {
                ConfigureReadOnly(connection);
            }
            else {
                ConfigureOpened(connection, _limits);
            }
            ValidateSchemaIdentity(connection);
            ValidateCounts(ReadCounts(connection, null));
            return connection;
        }
        catch {
            connection.Dispose();
            throw;
        }
    }

    private static SqliteConnection OpenConnection(
        string path,
        bool create,
        bool readOnly
    ) {
        var builder = new SqliteConnectionStringBuilder {
            DataSource = path,
            Mode = readOnly
                ? SqliteOpenMode.ReadOnly
                : create
                ? SqliteOpenMode.ReadWriteCreate
                : SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 0
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        return connection;
    }

    private static void ConfigureCreated(
        SqliteConnection connection,
        StoreStorageLimits limits
    ) {
        ExecutePragma(connection, "PRAGMA page_size = 4096;");
        ExecutePragma(connection, "PRAGMA journal_mode = DELETE;");
        ExecutePragma(
            connection,
            $"PRAGMA busy_timeout = 0; PRAGMA max_page_count = {SqliteNativeMaximumPageCountRequest};"
        );
        ConfigureOpened(connection, limits);
        ExecutePragma(connection, $"PRAGMA application_id = {ApplicationId};");
        ExecutePragma(connection, $"PRAGMA user_version = {SchemaVersion};");
    }

    private static void ConfigureOpened(
        SqliteConnection connection,
        StoreStorageLimits limits
    ) {
        // Install the zero-wait policy before any pragma that may itself need
        // a database lock. Local retries are owned by the commit loop below.
        ExecuteNativeControl(connection, """
            PRAGMA busy_timeout = 0;
            PRAGMA foreign_keys = ON;
            PRAGMA trusted_schema = OFF;
            PRAGMA synchronous = EXTRA;
            PRAGMA temp_store = MEMORY;
            PRAGMA locking_mode = NORMAL;
            PRAGMA read_uncommitted = OFF;
            """);
        ExecutePragma(
            connection,
            $"PRAGMA max_page_count = {SqliteNativeMaximumPageCountRequest};"
        );
        RequirePragmaInteger(connection, "page_size", 4096);
        RequirePragmaText(connection, "journal_mode", "delete");
        RequirePragmaInteger(connection, "synchronous", 3);
        RequirePragmaInteger(connection, "foreign_keys", 1);
        RequirePragmaInteger(connection, "trusted_schema", 0);
        RequirePragmaInteger(connection, "busy_timeout", 0);
        RequirePragmaInteger(connection, "temp_store", 2);
        RequirePragmaText(connection, "locking_mode", "normal");
        RequirePragmaInteger(connection, "read_uncommitted", 0);
    }

    private WriteTransaction BeginWriteTransaction(
        SqliteConnection connection
    ) => WriteTransaction.Begin(connection);

    private sealed class WriteTransaction : IDisposable {
        private readonly SqliteConnection _connection;
        private bool _completed;

        private WriteTransaction(SqliteConnection connection) {
            _connection = connection;
        }

        internal static WriteTransaction Begin(SqliteConnection connection) {
            ExecuteControl(connection, "BEGIN IMMEDIATE;");
            return new WriteTransaction(connection);
        }

        internal void Commit(Action? afterNativeCommitReturn = null) {
            try {
                ExecuteControl(_connection, "COMMIT;");
            }
            catch (SqliteException exception) when (IsBusy(exception)) {
                try {
                    ExecuteControl(_connection, "ROLLBACK;");
                    _completed = true;
                }
                catch {
                    throw new IOException(
                        "A busy SQLite COMMIT could not be confirmed rolled back.",
                        exception
                    );
                }
                throw new StoreCommitBusyRolledBackException(exception);
            }
            afterNativeCommitReturn?.Invoke();
            _completed = true;
        }

        internal void Rollback() {
            if (_completed) {
                return;
            }
            ExecuteControl(_connection, "ROLLBACK;");
            _completed = true;
        }

        public void Dispose() {
            if (_completed) {
                return;
            }
            try {
                ExecuteControl(_connection, "ROLLBACK;");
            }
            catch (SqliteException) { }
            _completed = true;
        }

        public static implicit operator SqliteTransaction?(
            WriteTransaction transaction
        ) => null;

        private static void ExecuteControl(
            SqliteConnection connection,
            string statement
        ) => ExecuteNativeControl(connection, statement);
    }

    private sealed class StoreCommitBusyRolledBackException(
        SqliteException inner
    ) : Exception("SQLite COMMIT was confirmed rolled back after BUSY.", inner);

    private static void ConfigureReadOnly(SqliteConnection connection) {
        ExecuteNativeControl(connection, """
            PRAGMA busy_timeout = 0;
            PRAGMA foreign_keys = ON;
            PRAGMA trusted_schema = OFF;
            PRAGMA synchronous = EXTRA;
            PRAGMA temp_store = MEMORY;
            PRAGMA query_only = ON;
            """);
        RequirePragmaInteger(connection, "page_size", 4096);
        RequirePragmaText(connection, "journal_mode", "delete");
        RequirePragmaInteger(connection, "synchronous", 3);
        RequirePragmaInteger(connection, "foreign_keys", 1);
        RequirePragmaInteger(connection, "trusted_schema", 0);
        RequirePragmaInteger(connection, "busy_timeout", 0);
        RequirePragmaInteger(connection, "temp_store", 2);
        RequirePragmaInteger(connection, "query_only", 1);
    }

    private static void ValidateSchemaIdentity(SqliteConnection connection) {
        using (SqliteCommand command = connection.CreateCommand()) {
            command.CommandText = "PRAGMA user_version;";
            using SqliteDataReader reader = command.ExecuteReader();
            if (!reader.Read()) {
                throw new InvalidDataException(
                    "RecapGrid Store schema version is unavailable."
                );
            }
            int schemaVersion = reader.GetInt32(0);
            if (schemaVersion != SchemaVersion) {
                throw new StoreUnsupportedSchemaException(schemaVersion);
            }
        }
        using (SqliteCommand command = connection.CreateCommand()) {
            command.CommandText = """
                SELECT
                    (SELECT application_id FROM pragma_application_id),
                    (SELECT COUNT(*) FROM store_metadata);
                """;
            using SqliteDataReader reader = command.ExecuteReader();
            if (!reader.Read()) {
                throw new InvalidDataException(
                    "RecapGrid Store schema identity is unavailable."
                );
            }
            if (reader.GetInt32(0) != ApplicationId
                || reader.GetInt32(1) != 1) {
                throw new InvalidDataException(
                    "RecapGrid Store schema identity is invalid."
                );
            }
        }
        using SqliteCommand schema = connection.CreateCommand();
        schema.CommandText = """
            SELECT type, name, tbl_name, sql
            FROM sqlite_schema
            WHERE name NOT LIKE 'sqlite_%'
            ORDER BY type, name;
            """;
        using SqliteDataReader rows = schema.ExecuteReader();
        SchemaEntry[] expected = ExpectedSchema.Value;
        int index = 0;
        while (rows.Read()) {
            if (index >= expected.Length) {
                throw new InvalidDataException(
                    "RecapGrid Store contains an unexpected schema object."
                );
            }
            SchemaEntry item = expected[index++];
            if (!string.Equals(rows.GetString(0), item.Type,
                    StringComparison.Ordinal)
                || !string.Equals(rows.GetString(1), item.Name,
                    StringComparison.Ordinal)
                || !string.Equals(rows.GetString(2), item.Table,
                    StringComparison.Ordinal)
                || rows.IsDBNull(3)
                || !string.Equals(rows.GetString(3), item.Sql,
                    StringComparison.Ordinal)) {
                throw new InvalidDataException(
                    "RecapGrid Store schema shape differs from V2."
                );
            }
        }
        if (index != expected.Length) {
            throw new InvalidDataException(
                "RecapGrid Store is missing a required schema object."
            );
        }
        rows.Close();
        _ = ReadIdentity(connection, transaction: null);
    }

    private static RecapGridStoreIdentity ReadIdentity(
        SqliteConnection connection,
        SqliteTransaction? transaction
    ) {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT schema_version, store_instance_id
            FROM store_metadata WHERE singleton = 1;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetInt32(0) != SchemaVersion) {
            throw new InvalidDataException(
                "RecapGrid Store metadata is missing."
            );
        }
        try {
            return new RecapGridStoreIdentity(
                new RecapGridStoreInstanceId(reader.GetString(1)),
                SchemaVersion
            );
        }
        catch (ArgumentException exception) {
            throw new InvalidDataException(
                "RecapGrid Store instance identity is invalid.",
                exception
            );
        }
    }

    private static StoreCounts ReadCounts(
        SqliteConnection connection,
        SqliteTransaction? transaction
    ) {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT cell_count, row_view_count,
                   row_view_member_count, fulfilled_view_count
            FROM store_metadata WHERE singleton = 1;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read()) {
            throw new InvalidDataException(
                "RecapGrid Store counters are missing."
            );
        }
        return new StoreCounts(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3)
        );
    }

    private static void WriteCounts(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        StoreCounts counts
    ) {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE store_metadata
            SET cell_count = $cells,
                row_view_count = $views,
                row_view_member_count = $members,
                fulfilled_view_count = $fulfilled
            WHERE singleton = 1;
            """;
        command.Parameters.AddWithValue("$cells", counts.CellCount);
        command.Parameters.AddWithValue("$views", counts.RowViewCount);
        command.Parameters.AddWithValue("$members", counts.RowViewMemberCount);
        command.Parameters.AddWithValue("$fulfilled", counts.FulfilledViewCount);
        if (command.ExecuteNonQuery() != 1) {
            throw new InvalidDataException(
                "RecapGrid Store counters could not be updated."
            );
        }
    }

    private static void ValidateCounts(StoreCounts counts) {
        if (counts.CellCount < 0
            || counts.RowViewCount < 0
            || counts.RowViewMemberCount < 0
            || counts.FulfilledViewCount < 0) {
            throw new InvalidDataException(
                "RecapGrid Store counters must not be negative."
            );
        }
    }

    private static void RequireFilePresent(string path) {
        var file = new FileInfo(path);
        if (!file.Exists) {
            throw new FileNotFoundException(
                "The RecapGrid Store exact database slot is absent.",
                path
            );
        }
        if (file.Length is < 1) {
            throw new InvalidDataException(
                "The RecapGrid Store exact database slot is empty."
            );
        }
    }

    private static void ExecutePragma(
        SqliteConnection connection,
        string sql,
        SqliteTransaction? transaction = null
    ) {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        _ = command.ExecuteScalar();
    }

    private static void ExecuteNativeControl(
        SqliteConnection connection,
        string statement
    ) {
        int timeoutResult = SQLitePCL.raw.sqlite3_busy_timeout(
            connection.Handle,
            0
        );
        if (timeoutResult != SQLitePCL.raw.SQLITE_OK) {
            throw new SqliteException(
                "SQLite rejected the zero-wait busy policy.",
                timeoutResult
            );
        }
        int result = SQLitePCL.raw.sqlite3_exec(
            connection.Handle,
            statement
        );
        if (result != SQLitePCL.raw.SQLITE_OK) {
            throw new SqliteException(
                SQLitePCL.raw.sqlite3_errmsg(connection.Handle)
                    .utf8_to_string(),
                result
            );
        }
    }

    private static void RequirePragmaInteger(
        SqliteConnection connection,
        string name,
        long expected,
        SqliteTransaction? transaction = null
    ) {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA {name};";
        object? value = command.ExecuteScalar();
        if (value is null || Convert.ToInt64(value) != expected) {
            throw new InvalidDataException(
                $"RecapGrid Store PRAGMA {name} is not {expected}."
            );
        }
    }

    private static void RequirePragmaText(
        SqliteConnection connection,
        string name,
        string expected
    ) {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"PRAGMA {name};";
        string? value = Convert.ToString(command.ExecuteScalar());
        if (!string.Equals(value, expected,
                StringComparison.OrdinalIgnoreCase)) {
            throw new InvalidDataException(
                $"RecapGrid Store PRAGMA {name} is not {expected}."
            );
        }
    }

    private static string ReadSchemaSql() {
        Assembly assembly = typeof(SqliteRecapGridStore).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(
            "Atelia.SessionJournal.RecapGrid.Store.SchemaV5.sql"
        )
            ?? throw new InvalidOperationException(
                "The RecapGrid Store V5 schema resource is missing."
            );
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static byte[] ExportCellJson(RecapCellArtifact cell) => RecapGridCanonical.Encode(new {
        schemaVersion = SchemaVersion,
        id = cell.Id.Value,
        slot = new { recipeDigest = cell.Slot.RecipeDigest.Value, historyRowId = cell.Slot.HistoryRowId.Value,
            workId = cell.Slot.WorkId?.Value, logicalColumnId = cell.Slot.LogicalColumnId.Value },
        definitionDigest = cell.DefinitionDigest.Value,
        outcome = cell.Outcome == RecapCellOutcome.Updated ? "updated" : "keep-unchanged",
        content = cell.Content
    });

    private static byte[] ExportRowJson(
        RecapRowView row,
        RowWorkId? workId
    ) => RecapGridCanonical.Encode(new {
        schemaVersion = SchemaVersion, id = row.Id.Value,
        refId = row.RefId.Packed, timelineId = row.TimelineId.Value, historyRowId = row.HistoryRowId.Value,
        recipeDigest = row.RecipeDigest.Value, workId = workId?.Value,
        targetDigest = row.TargetDigest.Value, previousHistoryRowId = row.PreviousHistoryRowId?.Value,
        previousRowResultId = row.PreviousRowResultId?.Value, bootstrapCompleted = row.BootstrapCompleted,
        orderedCells = row.OrderedCells.Select(static cell => new {
            logicalColumnId = cell.LogicalColumnId.Value, definitionDigest = cell.DefinitionDigest.Value, cellId = cell.CellId.Value
        }).ToArray()
    });

    private static byte[] ExportFulfilledJson(FulfilledViewKey key) => RecapGridCanonical.Encode(new {
        refId = key.RefId.Packed, timelineId = key.TimelineId.Value, timelineHeadGeneration = key.TimelineHeadGeneration,
        throughRowId = key.ThroughRowId.Value, recipeDigest = key.RecipeDigest.Value
    });

    private static byte[] ExportRowWorkJson(RowWork work)
        => RecapGridCanonical.Encode(new {
            schemaVersion = SchemaVersion,
            workId = work.WorkId.Value,
            refId = work.Key.RefId.Packed,
            timelineId = work.Key.TimelineId.Value,
            rootRecipeDigest = work.Key.RootRecipeDigest.Value,
            historyRowId = work.Key.HistoryRowId.Value,
            previousHistoryRowId = work.PreviousHistoryRowId?.Value,
            previousRowResultId = work.PreviousRowResultId?.Value,
            producerTarget = new {
                digest = work.ProducerTarget.Digest.Value,
                orderedColumns = work.ProducerTarget.OrderedColumns.Select(
                    static column => new {
                        logicalColumnId = column.LogicalColumnId.Value,
                        definitionDigest = column.DefinitionDigest.Value
                    }
                ).ToArray()
            },
            orderedAssignments = work.OrderedAssignments.Select(
                static assignment => new {
                    kind = assignment.IsEvaluate ? "evaluate" : "reuse",
                    logicalColumnId = assignment.LogicalColumnId.Value,
                    reusedCellId = assignment.ReusedCellId?.Value
                }
            ).ToArray()
        });

    private static bool ExportIdTable(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string column,
        string kind,
        string? after,
        bool includeContent,
        List<RecapGridStoreExportItem> items,
        ref int totalBytes,
        ref RecapGridStoreExportCursor? last
    ) {
        int remaining = RecapGridStoreLimits.MaximumPageItems - items.Count;
        int queryLimit = checked(remaining + 1);
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = after is null
            ? $"SELECT {column} FROM {table} WHERE {column} >= '' ORDER BY {column} LIMIT $limit;"
            : $"SELECT {column} FROM {table} WHERE {column} > $after ORDER BY {column} LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", queryLimit);
        if (after is not null) {
            command.Parameters.AddWithValue("$after", after);
        }
        var keys = new List<string>(queryLimit);
        using (SqliteDataReader reader = command.ExecuteReader()) {
            while (reader.Read()) {
                keys.Add(reader.GetString(0));
            }
        }
        foreach (string key in keys) {
            if (items.Count >= RecapGridStoreLimits.MaximumPageItems) {
                return false;
            }
            byte[] canonical = kind == "cell"
                ? ExportCellForExport(
                    connection,
                    transaction,
                    DecodeStoredValue(() => new CellId(key))
                )
                : ExportRowForExport(
                    connection,
                    transaction,
                    DecodeStoredValue(() => new RowResultId(key))
                );
            if (!TryAddExportItem(
                    items,
                    ref totalBytes,
                    new RecapGridStoreExportItem(
                        kind,
                        key,
                        canonical.Length,
                        includeContent ? canonical : null
                    ))) {
                return false;
            }
            last = RecapGridStoreExportCursor.CreateId(kind, key);
        }
        return keys.Count < queryLimit;
    }

    private static byte[] ExportCellForExport(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CellId cellId
    ) {
        RecapCellArtifact cell = ReadCellByIdCore(
            connection,
            transaction,
            cellId
        ) ?? throw new InvalidDataException(
            "A Cell disappeared during export."
        );
        RowWorkId workId = cell.Slot.WorkId
            ?? throw new InvalidDataException(
                "A V5 Cell is missing its RowWork identity."
            );
        RowWork work = ReadRowWorkByIdCore(
            connection,
            transaction,
            workId
        ) ?? throw new InvalidDataException(
            "A Cell references a missing RowWork."
        );
        ValidateCellAgainstWork(cell, work);
        return ExportCellJson(cell);
    }

    private static byte[] ExportRowForExport(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RowResultId rowResultId
    ) {
        RecapRowView row = ReadRowViewCore(
            connection,
            transaction,
            rowResultId
        ) ?? throw new InvalidDataException(
            "A RowView disappeared during export."
        );
        RowWorkId? workId = ReadRowViewWorkId(
            connection,
            transaction,
            rowResultId
        );
        RowWorkId value = workId
            ?? throw new InvalidDataException(
                "A V5 RowView is missing its RowWork identity."
            );
        RowWork work = ReadRowWorkByIdCore(
            connection,
            transaction,
            value
        ) ?? throw new InvalidDataException(
            "A RowView references a missing RowWork."
        );
        ValidateRowViewAgainstWork(connection, transaction, row, work);
        return ExportRowJson(row, workId);
    }

    private static bool ExportRowWorkTable(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? after,
        bool includeContent,
        List<RecapGridStoreExportItem> items,
        ref int totalBytes,
        ref RecapGridStoreExportCursor? last
    ) {
        int remaining = RecapGridStoreLimits.MaximumPageItems - items.Count;
        int queryLimit = checked(remaining + 1);
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = after is null
            ? "SELECT work_id FROM row_work WHERE work_id >= '' ORDER BY work_id LIMIT $limit;"
            : "SELECT work_id FROM row_work WHERE work_id > $after ORDER BY work_id LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", queryLimit);
        if (after is not null) {
            command.Parameters.AddWithValue("$after", after);
        }
        var keys = new List<string>(queryLimit);
        using (SqliteDataReader reader = command.ExecuteReader()) {
            while (reader.Read()) {
                keys.Add(reader.GetString(0));
            }
        }
        foreach (string key in keys) {
            if (items.Count >= RecapGridStoreLimits.MaximumPageItems) {
                return false;
            }
            var workId = DecodeStoredValue(() => new RowWorkId(key));
            RowWork work = ReadRowWorkByIdCore(
                connection,
                transaction,
                workId
            ) ?? throw new InvalidDataException(
                "A RowWork disappeared during export."
            );
            byte[] canonical = ExportRowWorkJson(work);
            if (!TryAddExportItem(
                    items,
                    ref totalBytes,
                    new RecapGridStoreExportItem(
                        "row-work",
                        key,
                        canonical.Length,
                        includeContent ? canonical : null
                    ))) {
                return false;
            }
            last = RecapGridStoreExportCursor.CreateRowWork(key);
        }
        return keys.Count < queryLimit;
    }

    private static bool ExportFulfilledTable(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RecapGridStoreExportCursor? after,
        bool includeContent,
        List<RecapGridStoreExportItem> items,
        ref int totalBytes,
        ref RecapGridStoreExportCursor? last
    ) {
        int remaining = RecapGridStoreLimits.MaximumPageItems - items.Count;
        int queryLimit = checked(remaining + 1);
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = after is null
            ? """
                SELECT ref_id, timeline_id, timeline_head_generation,
                       through_history_row_id, recipe_digest,
                       row_result_id
                FROM fulfilled_view_ref
                WHERE (ref_id, timeline_id, timeline_head_generation,
                       through_history_row_id, recipe_digest)
                    >= ('', '', 0, '', '')
                ORDER BY ref_id, timeline_id, timeline_head_generation,
                         through_history_row_id, recipe_digest
                LIMIT $limit;
                """
            : """
                SELECT ref_id, timeline_id, timeline_head_generation,
                       through_history_row_id, recipe_digest,
                       row_result_id
                FROM fulfilled_view_ref
                WHERE (ref_id, timeline_id, timeline_head_generation,
                       through_history_row_id, recipe_digest)
                    > ($ref, $timeline, $generation, $through, $recipe)
                ORDER BY ref_id, timeline_id, timeline_head_generation,
                         through_history_row_id, recipe_digest
                LIMIT $limit;
                """;
        command.Parameters.AddWithValue("$limit", queryLimit);
        if (after is not null) {
            command.Parameters.AddWithValue("$ref", after.RefId!);
            command.Parameters.AddWithValue("$timeline", after.TimelineId!);
            command.Parameters.AddWithValue("$generation", after.Generation);
            command.Parameters.AddWithValue("$through", after.Through!);
            command.Parameters.AddWithValue("$recipe", after.Recipe!);
        }
        var rows = new List<FulfilledPhysicalRow>(queryLimit);
        using (SqliteDataReader reader = command.ExecuteReader()) {
            while (reader.Read()) {
                rows.Add(new FulfilledPhysicalRow(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt64(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5)
                ));
            }
        }
        foreach (FulfilledPhysicalRow row in rows) {
            if (items.Count >= RecapGridStoreLimits.MaximumPageItems) {
                return false;
            }
            (FulfilledViewKey key, RowResultId rowResultId) =
                ValidateFulfilledPhysicalRow(connection, transaction, row);
            byte[] canonical = ExportFulfilledJson(key);
            RecapGridStoreExportCursor cursor =
                RecapGridStoreExportCursor.CreateFulfilled(
                    row.RefId,
                    row.TimelineId,
                    row.Generation,
                    row.Through,
                    row.Recipe
                );
            if (!TryAddExportItem(
                    items,
                    ref totalBytes,
                    new RecapGridStoreExportItem(
                        "fulfilled",
                        cursor.Key,
                        canonical.Length,
                        includeContent ? canonical : null,
                        rowResultId
                    ))) {
                return false;
            }
            last = cursor;
        }
        return rows.Count < queryLimit;
    }

    private static bool TryAddExportItem(
        List<RecapGridStoreExportItem> items,
        ref int totalBytes,
        RecapGridStoreExportItem item
    ) {
        if (item.JsonUtf8Bytes is < 1
            or > RecapGridStoreLimits.MaximumPageBytes) {
            throw new InvalidDataException(
                "An export item exceeds the page byte bound."
            );
        }
        int nextBytes = checked(totalBytes + item.JsonUtf8Bytes);
        if (items.Count > 0
            && nextBytes > RecapGridStoreLimits.MaximumPageBytes) {
            return false;
        }
        items.Add(item);
        totalBytes = nextBytes;
        return true;
    }

    private static void VerifyAllCells(
        SqliteConnection connection,
        SqliteTransaction transaction
    ) {
        using (SqliteCommand legacy = connection.CreateCommand()) {
            legacy.Transaction = transaction;
            legacy.CommandText =
                "SELECT 1 FROM cell_artifact WHERE work_id IS NULL LIMIT 1;";
            if (legacy.ExecuteScalar() is not null) {
                throw new InvalidDataException(
                    "A V5 Cell is missing its RowWork identity."
                );
            }
        }

        WorkCellKey? after = null;
        RowWorkId? currentWorkId = null;
        RowWork? currentWork = null;
        while (true) {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = after is null
                ? """
                    SELECT work_id,logical_column_id,cell_id
                    FROM cell_artifact
                    ORDER BY work_id,logical_column_id LIMIT 128;
                    """
                : """
                    SELECT work_id,logical_column_id,cell_id
                    FROM cell_artifact
                    WHERE (work_id,logical_column_id) > ($work,$column)
                    ORDER BY work_id,logical_column_id LIMIT 128;
                    """;
            if (after is not null) {
                command.Parameters.AddWithValue("$work", after.WorkId.Value);
                command.Parameters.AddWithValue(
                    "$column",
                    after.LogicalColumnId.Value
                );
            }
            var page = new List<WorkCellKey>(
                RecapGridStoreLimits.MaximumPageItems
            );
            using (SqliteDataReader reader = command.ExecuteReader()) {
                while (reader.Read()) {
                    page.Add(DecodeStoredValue(() => new WorkCellKey(
                        new RowWorkId(reader.GetString(0)),
                        new LogicalColumnId(reader.GetString(1)),
                        new CellId(reader.GetString(2))
                    )));
                }
            }
            foreach (WorkCellKey key in page) {
                RecapCellArtifact cell = ReadCellByIdCore(
                    connection,
                    transaction,
                    key.CellId
                )
                    ?? throw new InvalidDataException(
                        "A Cell disappeared during verification."
                    );
                if (cell.Slot.WorkId != key.WorkId
                    || cell.LogicalColumnId != key.LogicalColumnId) {
                    throw new InvalidDataException(
                        "A Cell differs from its verification key."
                    );
                }
                if (currentWorkId != key.WorkId) {
                    currentWork = ReadRowWorkByIdCore(
                        connection,
                        transaction,
                        key.WorkId
                    ) ?? throw new InvalidDataException(
                        "A Cell references a missing RowWork."
                    );
                    currentWorkId = key.WorkId;
                }
                ValidateCellAgainstWork(cell, currentWork!);
            }
            if (page.Count < RecapGridStoreLimits.MaximumPageItems) {
                return;
            }
            after = page[^1];
        }
    }

    private static void ValidateCellAgainstWork(
        RecapCellArtifact cell,
        RowWork work
    ) {
        int index = work.OrderedAssignments
            .Select(static (assignment, index) => (assignment, index))
            .Where(pair => pair.assignment.LogicalColumnId
                == cell.LogicalColumnId)
            .Select(static pair => pair.index)
            .DefaultIfEmpty(-1)
            .Single();
        if (index < 0
            || !work.OrderedAssignments[index].IsEvaluate
            || cell.Slot.RecipeDigest != work.Key.RootRecipeDigest
            || cell.Slot.HistoryRowId != work.Key.HistoryRowId
            || cell.DefinitionDigest
                != work.ProducerTarget.OrderedColumns[index]
                    .DefinitionDigest) {
            throw new InvalidDataException(
                "A Cell differs from its RowWork Evaluate assignment."
            );
        }
    }

    private static void VerifyAllRowWorks(
        SqliteConnection connection,
        SqliteTransaction transaction
    ) {
        string? after = null;
        while (true) {
            List<RowWorkId> page = ReadIdPage<RowWorkId>(
                connection,
                transaction,
                "row_work",
                "work_id",
                after,
                static value => new RowWorkId(value)
            );
            foreach (RowWorkId workId in page) {
                _ = ReadRowWorkByIdCore(connection, transaction, workId)
                    ?? throw new InvalidDataException(
                        "A RowWork disappeared during verification."
                    );
            }
            if (page.Count < RecapGridStoreLimits.MaximumPageItems) {
                return;
            }
            after = page[^1].Value;
        }
    }

    private static void VerifyAllRowViews(
        SqliteConnection connection,
        SqliteTransaction transaction
    ) {
        string? after = null;
        while (true) {
            List<RowResultId> page = ReadIdPage<RowResultId>(
                connection,
                transaction,
                "row_view",
                "row_result_id",
                after,
                static value => new RowResultId(value)
            );
            foreach (RowResultId rowResultId in page) {
                RecapRowView view = ReadRowViewCore(
                    connection,
                    transaction,
                    rowResultId
                ) ?? throw new InvalidDataException(
                    "A RowView disappeared during verification."
                );
                RowWorkId workId = ReadRowViewWorkId(
                    connection,
                    transaction,
                    rowResultId
                ) ?? throw new InvalidDataException(
                    "A V5 RowView is missing its RowWork identity."
                );
                RowWork work = ReadRowWorkByIdCore(
                    connection,
                    transaction,
                    workId
                ) ?? throw new InvalidDataException(
                    "A RowView references a missing RowWork."
                );
                ValidateRowViewAgainstWork(
                    connection,
                    transaction,
                    view,
                    work
                );
                if (view.PreviousRowResultId is { } previous) {
                    RecapRowView predecessor = ReadRowViewAtCore(
                        connection,
                        transaction,
                        new RowViewAssignmentKey(
                            view.RefId,
                            view.TimelineId,
                            view.RecipeDigest,
                            view.PreviousHistoryRowId!.Value
                        )
                    ) ?? throw new InvalidDataException(
                        "A RowView predecessor assignment is missing."
                    );
                    if (predecessor.Id != previous
                        || predecessor.BootstrapCompleted
                            && !view.BootstrapCompleted) {
                        throw new InvalidDataException(
                            "A RowView predecessor recurrence is invalid."
                        );
                    }
                }
            }
            if (page.Count < RecapGridStoreLimits.MaximumPageItems) {
                return;
            }
            after = page[^1].Value;
        }
    }

    private static void VerifyAllFulfilled(
        SqliteConnection connection,
        SqliteTransaction transaction
    ) {
        RecapGridStoreExportCursor? after = null;
        while (true) {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = after is null
                ? """
                    SELECT ref_id, timeline_id, timeline_head_generation,
                           through_history_row_id, recipe_digest,
                           row_result_id
                    FROM fulfilled_view_ref
                    WHERE (ref_id, timeline_id, timeline_head_generation,
                           through_history_row_id, recipe_digest)
                        >= ('', '', 0, '', '')
                    ORDER BY ref_id, timeline_id, timeline_head_generation,
                             through_history_row_id, recipe_digest
                    LIMIT 128;
                    """
                : """
                    SELECT ref_id, timeline_id, timeline_head_generation,
                           through_history_row_id, recipe_digest,
                           row_result_id
                    FROM fulfilled_view_ref
                    WHERE (ref_id, timeline_id, timeline_head_generation,
                           through_history_row_id, recipe_digest)
                        > ($ref, $timeline, $generation, $through, $recipe)
                    ORDER BY ref_id, timeline_id, timeline_head_generation,
                             through_history_row_id, recipe_digest
                    LIMIT 128;
                    """;
            if (after is not null) {
                command.Parameters.AddWithValue("$ref", after.RefId!);
                command.Parameters.AddWithValue(
                    "$timeline",
                    after.TimelineId!
                );
                command.Parameters.AddWithValue(
                    "$generation",
                    after.Generation
                );
                command.Parameters.AddWithValue("$through", after.Through!);
                command.Parameters.AddWithValue("$recipe", after.Recipe!);
            }
            var rows = new List<FulfilledPhysicalRow>();
            using (SqliteDataReader reader = command.ExecuteReader()) {
                while (reader.Read()) {
                    rows.Add(new FulfilledPhysicalRow(
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetInt64(2),
                        reader.GetString(3),
                        reader.GetString(4),
                        reader.GetString(5)
                    ));
                }
            }
            foreach (FulfilledPhysicalRow row in rows) {
                _ = ValidateFulfilledPhysicalRow(
                    connection,
                    transaction,
                    row
                );
            }
            if (rows.Count < RecapGridStoreLimits.MaximumPageItems) {
                return;
            }
            FulfilledPhysicalRow tail = rows[^1];
            after = RecapGridStoreExportCursor.CreateFulfilled(
                tail.RefId,
                tail.TimelineId,
                tail.Generation,
                tail.Through,
                tail.Recipe
            );
        }
    }

    private static RowWorkId? ReadRowViewWorkId(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        RowResultId rowResultId
    ) {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT work_id FROM row_view WHERE row_result_id=$id;";
        command.Parameters.AddWithValue("$id", rowResultId.Value);
        object? value = command.ExecuteScalar();
        return value is string text
            ? DecodeStoredValue(() => new RowWorkId(text))
            : null;
    }

    private static void ValidateRowViewAgainstWork(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        RecapRowView view,
        RowWork work
    ) {
        if (view.RefId != work.Key.RefId
            || view.TimelineId != work.Key.TimelineId
            || view.RecipeDigest != work.Key.RootRecipeDigest
            || view.HistoryRowId != work.Key.HistoryRowId
            || view.TargetDigest != work.ProducerTarget.Digest
            || view.PreviousHistoryRowId != work.PreviousHistoryRowId
            || view.PreviousRowResultId != work.PreviousRowResultId
            || view.OrderedCells.Count != work.OrderedAssignments.Count) {
            throw new InvalidDataException(
                "A RowView scope, target, or exact prior differs from its RowWork."
            );
        }
        for (int index = 0; index < view.OrderedCells.Count; index++) {
            RecapRowViewCell member = view.OrderedCells[index];
            RowWorkAssignment assignment = work.OrderedAssignments[index];
            BuildTargetColumn column = work.ProducerTarget.OrderedColumns[index];
            if (member.LogicalColumnId != assignment.LogicalColumnId
                || member.LogicalColumnId != column.LogicalColumnId
                || member.DefinitionDigest != column.DefinitionDigest) {
                throw new InvalidDataException(
                    "A RowView member differs from its RowWork assignment."
                );
            }
            RecapCellArtifact cell = ReadCellByIdCore(
                connection,
                transaction,
                member.CellId
            ) ?? throw new InvalidDataException(
                "A RowView member references a missing Cell."
            );
            bool exact = assignment.ReusedCellId is { } reused
                ? member.CellId == reused
                : cell.Slot.WorkId == work.WorkId;
            if (!exact) {
                throw new InvalidDataException(
                    "A RowView member differs from its frozen RowWork source."
                );
            }
            if (assignment.IsEvaluate) {
                ValidateCellAgainstWork(cell, work);
            }
        }
    }

    private static (FulfilledViewKey Key, RowResultId RowResultId)
        ValidateFulfilledPhysicalRow(
            SqliteConnection connection,
            SqliteTransaction transaction,
            FulfilledPhysicalRow row
        ) {
        var key = DecodeStoredValue(() => new FulfilledViewKey(ParseRef(row.RefId), new(row.TimelineId), row.Generation,
            new(row.Through), new(row.Recipe)));
        var id = DecodeStoredValue(() => new RowResultId(row.RowResultId));
        ValidateFulfilledTarget(connection, transaction, key, id);
        return (key, id);
    }

    private sealed record FulfilledPhysicalRow(
        string RefId,
        string TimelineId,
        long Generation,
        string Through,
        string Recipe,
        string RowResultId
    );

    private sealed record RowWorkPhysical(
        string WorkId,
        string RefId,
        string TimelineId,
        string Root,
        string HistoryRow,
        string? PreviousHistoryRow,
        string? PreviousRowResult,
        byte[] ProducerTarget,
        byte[] Canonical
    );

    private sealed record WorkCellKey(
        RowWorkId WorkId,
        LogicalColumnId LogicalColumnId,
        CellId CellId
    );

    // Translate malformed SQL values only; public input validation stays outside this boundary.
    private static T DecodeStoredValue<T>(Func<T> materialize) {
        try { return materialize(); }
        catch (Exception exception) when (exception is ArgumentException or FormatException) {
            throw new InvalidDataException("A stored RecapGrid value is malformed.", exception);
        }
    }

    private static List<T> ReadIdPage<T>(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string column,
        string? after,
        Func<string, T> factory
    ) {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = after is null
            ? $"SELECT {column} FROM {table} WHERE {column} >= '' ORDER BY {column} LIMIT 128;"
            : $"SELECT {column} FROM {table} WHERE {column} > $after ORDER BY {column} LIMIT 128;";
        if (after is not null) {
            command.Parameters.AddWithValue("$after", after);
        }
        var page = new List<T>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read()) {
            page.Add(DecodeStoredValue(() => factory(reader.GetString(0))));
        }
        return page;
    }

    private static SchemaEntry[] BuildExpectedSchema() {
        var builder = new SqliteConnectionStringBuilder {
            DataSource = ":memory:",
            Mode = SqliteOpenMode.Memory,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using (SqliteCommand create = connection.CreateCommand()) {
            create.CommandText = SchemaSql;
            create.ExecuteNonQuery();
        }
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT type, name, tbl_name, sql
            FROM sqlite_schema
            WHERE name NOT LIKE 'sqlite_%'
            ORDER BY type, name;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        var values = new List<SchemaEntry>();
        while (reader.Read()) {
            values.Add(new SchemaEntry(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3)
            ));
        }
        return values.ToArray();
    }

    private sealed record StoreCounts(
        long CellCount,
        long RowViewCount,
        long RowViewMemberCount,
        long FulfilledViewCount
    );
    private sealed record SchemaEntry(
        string Type,
        string Name,
        string Table,
        string Sql
    );

}
