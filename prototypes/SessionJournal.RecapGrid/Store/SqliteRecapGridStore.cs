using System.Collections.ObjectModel;
using System.Reflection;
using Microsoft.Data.Sqlite;

namespace Atelia.SessionJournal.RecapGrid.Store;

internal sealed class SqliteRecapGridStore {
    internal const int SchemaVersion = 2;
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
                ) VALUES (1, 2, $instance, 0, 0, 0, 0);
                """;
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
            _ => throw new InvalidDataException(
                "The export cursor has an unknown kind."
            )
        };
        for (; phase < 3 && !incomplete; phase++) {
            string? digestAfter = phase switch {
                0 when after?.IsCell == true => after.Key,
                1 when after?.IsRowView == true => after.Key,
                _ => null
            };
            bool exhausted = phase switch {
                0 => ExportDigestTable(
                    connection,
                    transaction,
                    "cell_artifact",
                    "cell_digest",
                    "cell",
                    digestAfter,
                    includeContent,
                    items,
                    ref totalBytes,
                    ref last
                ),
                1 => ExportDigestTable(
                    connection,
                    transaction,
                    "row_view",
                    "view_digest",
                    "row-view",
                    digestAfter,
                    includeContent,
                    items,
                    ref totalBytes,
                    ref last
                ),
                _ => ExportFulfilledTable(
                    connection,
                    transaction,
                    after?.IsFulfilled == true ? after : null,
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

    internal RecapCellArtifact? ReadCellByEvaluationKey(
        EvaluationKey key
    ) {
        ArgumentNullException.ThrowIfNull(key);
        using SqliteConnection connection = OpenVerifiedConnection();
        RecapCellArtifact? cell = ReadCellByEvaluationKeyCore(
            connection,
            transaction: null,
            key.Digest
        );
        if (cell is not null && !cell.EvaluationKey.ToCanonicalBytes()
                .SequenceEqual(key.ToCanonicalBytes())) {
            throw new InvalidDataException(
                "An evaluation-key digest is bound to different canonical bytes."
            );
        }
        return cell;
    }

    internal RecapCellArtifact? ReadCellByDigest(CellDigest digest) {
        if (digest.Value is null) {
            throw new ArgumentException(
                "CellDigest must not be default.",
                nameof(digest)
            );
        }
        using SqliteConnection connection = OpenVerifiedConnection();
        return ReadCellByDigestCore(connection, transaction: null, digest);
    }

    internal RecapGridMissingResult FindMissing(RowBuildSpec spec) {
        ArgumentNullException.ThrowIfNull(spec);
        using SqliteConnection connection = OpenVerifiedConnection();
        var missing = new List<EvaluationKey>();
        foreach (RowBuildAssignment assignment in spec.OrderedAssignments) {
            switch (assignment) {
                case RowBuildAssignment.Evaluate evaluate:
                    RecapCellArtifact? winner = ReadCellByEvaluationKeyCore(
                        connection,
                        transaction: null,
                        evaluate.EvaluationKey.Digest
                    );
                    if (winner is null) {
                        missing.Add(evaluate.EvaluationKey);
                    }
                    else if (!winner.EvaluationKey.ToCanonicalBytes()
                            .SequenceEqual(
                                evaluate.EvaluationKey.ToCanonicalBytes()
                            )) {
                        throw new InvalidDataException(
                            "An EvaluationKey digest is bound to different canonical bytes."
                        );
                    }
                    break;
                case RowBuildAssignment.Reuse reuse:
                    RecapCellArtifact? reused = ReadCellByDigestCore(
                        connection,
                        transaction: null,
                        reuse.Cell.CellDigest
                    );
                    if (reused is null
                        || !reused.ToCanonicalBytes().SequenceEqual(
                            reuse.Cell.ToCanonicalBytes())) {
                        return new RecapGridMissingResult.PrerequisiteMissing(
                            reuse.LogicalColumnId,
                            reuse.Cell.CellDigest
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

    internal RecapRowView? ReadRowView(RowViewDigest digest) {
        if (digest.Value is null) {
            throw new ArgumentException(
                "RowViewDigest must not be default.",
                nameof(digest)
            );
        }
        using SqliteConnection connection = OpenVerifiedConnection();
        return ReadRowViewCore(connection, transaction: null, digest);
    }

    internal RecapRowView? ReadRowViewAt(RowViewAssignmentKey key) {
        ArgumentNullException.ThrowIfNull(key);
        using SqliteConnection connection = OpenVerifiedConnection();
        return ReadRowViewAtCore(connection, transaction: null, key);
    }

    internal RecapGridFulfilledView? ReadFulfilled(FulfilledViewKey key) {
        ArgumentNullException.ThrowIfNull(key);
        using SqliteConnection connection = OpenVerifiedConnection();
        RowViewDigest? digest = ReadFulfilledCore(
            connection,
            transaction: null,
            key
        );
        return digest is null ? null : new RecapGridFulfilledView(digest.Value);
    }

    internal RecapGridCellPutResult PutCell(RecapCellArtifact proposed) {
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
                RecapCellArtifact? winner = ReadCellByEvaluationKeyCore(
                    connection,
                    transaction,
                    proposed.EvaluationKey.Digest
                );
                if (winner is not null) {
                    transaction.Rollback();
                    if (!winner.EvaluationKey.ToCanonicalBytes()
                            .SequenceEqual(
                                proposed.EvaluationKey.ToCanonicalBytes()
                            )) {
                        (code, detail) = LatchInvalid(new StoreException(
                            "EvaluationKeyDigestCollision",
                            "An EvaluationKey digest is bound to different canonical bytes."
                        ));
                        return new RecapGridCellPutResult.Invalid(
                            code,
                            detail
                        );
                    }
                    return new RecapGridCellPutResult.AlreadyFilled(winner);
                }
                StoreCounts counts = ReadCounts(connection, transaction);
                InsertCell(connection, transaction, proposed);
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
                return new RecapGridCellPutResult.Inserted();
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
                    proposed.EvaluationKey.Digest
                );
                return new RecapGridCellPutResult.CommitIndeterminate(
                    proposed.EvaluationKey.Digest,
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
        RecapRowView proposed
    ) {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(proposed);
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
                try {
                    _ = RecapRowView.DecodeCanonical(
                        spec,
                        selected,
                        proposed.ToCanonicalBytes()
                    );
                }
                catch (InvalidDataException) {
                    transaction.Rollback();
                    return new RecapGridRowViewPutResult.Rejected(
                        "RowViewSpecMismatch"
                    );
                }
                RowViewDigest? assignedDigest =
                    ReadRowViewAssignmentDigest(
                        connection,
                        transaction,
                        proposed.Coordinate.AssignmentKey
                    );
                if (assignedDigest is { } assigned) {
                    RecapRowView assignedView = ReadRowViewCore(
                        connection,
                        transaction,
                        assigned
                    ) ?? throw new InvalidDataException(
                        "A row-view assignment references a missing RowView."
                    );
                    transaction.Rollback();
                    return assignedView.ToCanonicalBytes().SequenceEqual(
                        proposed.ToCanonicalBytes()
                    )
                        ? new RecapGridRowViewPutResult.AlreadyPresent()
                        : LatchRowViewInvalid(
                            "RowViewAssignmentConflict",
                            "A row-view assignment is already bound to another exact value."
                        );
                }
                RecapRowView? existing = ReadRowViewCore(
                    connection,
                    transaction,
                    proposed.Digest
                );
                if (existing is not null) {
                    transaction.Rollback();
                    return existing.ToCanonicalBytes().SequenceEqual(
                        proposed.ToCanonicalBytes()
                    )
                        ? new RecapGridRowViewPutResult.AlreadyPresent()
                        : LatchRowViewInvalid(
                            "RowViewDigestCollision",
                            "A RowView digest is bound to different canonical bytes."
                        );
                }
                RecapRowView? predecessor = null;
                if (proposed.PreviousViewDigest is { } previous) {
                    predecessor = ReadRowViewAtCore(
                        connection,
                        transaction,
                        new RowViewAssignmentKey(
                            proposed.RefId,
                            proposed.TimelineId,
                            proposed.RecipeDigest,
                            proposed.PreviousHistoryRowId!.Value
                        )
                    );
                    if (predecessor is null) {
                        transaction.Rollback();
                        return new RecapGridRowViewPutResult
                            .PrerequisiteMissing(
                                "PreviousAssignmentUnavailable"
                            );
                    }
                    if (predecessor.Digest != previous
                        || predecessor.RefId != proposed.RefId
                        || predecessor.TimelineId != proposed.TimelineId
                        || predecessor.RecipeDigest != proposed.RecipeDigest
                        || predecessor.HistoryRowId
                            != proposed.PreviousHistoryRowId
                        || predecessor.TargetDigest != proposed.TargetDigest) {
                        transaction.Rollback();
                        return new RecapGridRowViewPutResult.Rejected(
                            "PreviousAssignmentMismatch"
                        );
                    }
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
                InsertRowView(connection, transaction, proposed);
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
                return new RecapGridRowViewPutResult.Inserted();
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
                    proposed.Coordinate.AssignmentKey,
                    proposed.Digest,
                    TryObserveRowViewAt(
                        proposed.Coordinate.AssignmentKey
                    )?.Digest
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
        RowViewDigest viewDigest
    ) {
        ArgumentNullException.ThrowIfNull(key);
        if (viewDigest.Value is null) {
            throw new ArgumentException(
                "RowViewDigest must not be default.",
                nameof(viewDigest)
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
                RowViewDigest? existing = ReadFulfilledCore(
                    connection,
                    transaction,
                    key
                );
                if (existing is not null) {
                    transaction.Rollback();
                    if (existing == viewDigest) {
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
                    viewDigest
                );
                if (view is null) {
                    transaction.Rollback();
                    return new RecapGridFulfilledPutResult
                        .PrerequisiteMissing("RowViewUnavailable");
                }
                if (view.RefId != key.RefId
                    || view.TimelineId != key.TimelineId
                    || view.RecipeDigest != key.RecipeDigest
                    || view.RowDescriptorDigest
                        != key.ThroughRowDescriptorDigest) {
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
                    viewDigest
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

    private RecapCellArtifact? TryObserveCell(EvaluationKeyDigest key) {
        try {
            using SqliteConnection connection = OpenVerifiedConnection();
            return ReadCellByEvaluationKeyCore(connection, null, key);
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

    private RowViewDigest? TryObserveFulfilled(FulfilledViewKey key) {
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
                    => ReadCellByEvaluationKeyCore(
                        connection,
                        transaction,
                        evaluate.EvaluationKey.Digest
                    ),
                RowBuildAssignment.Reuse reuse
                    => ReadCellByDigestCore(
                        connection,
                        transaction,
                        reuse.Cell.CellDigest
                    ),
                _ => null
            };
            if (cell is null) {
                return null;
            }
            bool exact = assignment switch {
                RowBuildAssignment.Evaluate evaluate
                    => cell.EvaluationKey.ToCanonicalBytes().SequenceEqual(
                        evaluate.EvaluationKey.ToCanonicalBytes()
                    ),
                RowBuildAssignment.Reuse reuse
                    => cell.ToCanonicalBytes().SequenceEqual(
                        reuse.Cell.ToCanonicalBytes()
                    ),
                _ => false
            };
            if (!exact) {
                throw new InvalidDataException(
                    "A RowBuild assignment digest is bound to different canonical bytes."
                );
            }
            cells[index] = cell;
        }
        return cells;
    }

    private static void InsertRowView(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        RecapRowView view
    ) {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO row_view(
                view_digest, ref_id, timeline_id, history_row_id,
                row_descriptor_digest, recipe_digest, target_digest,
                previous_history_row_id, previous_view_digest,
                bootstrap_completed, canonical
            ) VALUES (
                $view, $ref, $timeline, $row, $descriptor, $recipe, $target,
                $previousRow, $previousView, $bootstrap, $canonical
            );
            """;
        command.Parameters.AddWithValue("$view", view.Digest.Value);
        command.Parameters.AddWithValue("$ref", view.RefId.ToHexString());
        command.Parameters.AddWithValue("$timeline", view.TimelineId.Value);
        command.Parameters.AddWithValue("$row", view.HistoryRowId.Value);
        command.Parameters.AddWithValue(
            "$descriptor",
            view.RowDescriptorDigest.Value
        );
        command.Parameters.AddWithValue("$recipe", view.RecipeDigest.Value);
        command.Parameters.AddWithValue("$target", view.TargetDigest.Value);
        command.Parameters.AddWithValue(
            "$previousRow",
            (object?)view.PreviousHistoryRowId?.Value ?? DBNull.Value
        );
        command.Parameters.AddWithValue(
            "$previousView",
            (object?)view.PreviousViewDigest?.Value ?? DBNull.Value
        );
        command.Parameters.AddWithValue(
            "$bootstrap",
            view.BootstrapCompleted ? 1 : 0
        );
        command.Parameters.AddWithValue("$canonical", view.ToCanonicalBytes());
        command.ExecuteNonQuery();
    }

    private static void InsertRowViewMembers(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        RecapRowView view
    ) {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO row_view_member(
                view_digest, column_ordinal, logical_column_id,
                definition_digest, cell_digest
            ) VALUES ($view, $ordinal, $column, $definition, $cell);
            """;
        SqliteParameter viewParameter = command.Parameters.Add(
            "$view",
            SqliteType.Text
        );
        SqliteParameter ordinalParameter = command.Parameters.Add(
            "$ordinal",
            SqliteType.Integer
        );
        SqliteParameter columnParameter = command.Parameters.Add(
            "$column",
            SqliteType.Text
        );
        SqliteParameter definitionParameter = command.Parameters.Add(
            "$definition",
            SqliteType.Text
        );
        SqliteParameter cellParameter = command.Parameters.Add(
            "$cell",
            SqliteType.Text
        );
        for (int index = 0; index < view.OrderedCells.Count; index++) {
            RecapRowViewCell member = view.OrderedCells[index];
            viewParameter.Value = view.Digest.Value;
            ordinalParameter.Value = index;
            columnParameter.Value = member.LogicalColumnId.Value;
            definitionParameter.Value = member.DefinitionDigest.Value;
            cellParameter.Value = member.CellDigest.Value;
            command.ExecuteNonQuery();
        }
    }

    private static RowViewDigest? ReadRowViewAssignmentDigest(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        RowViewAssignmentKey key
    ) {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT view_digest
            FROM row_view
            WHERE ref_id = $ref
              AND timeline_id = $timeline
              AND recipe_digest = $recipe
              AND history_row_id = $row;
            """;
        command.Parameters.AddWithValue("$ref", key.RefId.ToHexString());
        command.Parameters.AddWithValue("$timeline", key.TimelineId.Value);
        command.Parameters.AddWithValue("$recipe", key.RecipeDigest.Value);
        command.Parameters.AddWithValue("$row", key.HistoryRowId.Value);
        object? result = command.ExecuteScalar();
        return result is string value ? new RowViewDigest(value) : null;
    }

    private static RecapRowView? ReadRowViewAtCore(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        RowViewAssignmentKey key
    ) {
        RowViewDigest? digest = ReadRowViewAssignmentDigest(
            connection,
            transaction,
            key
        );
        if (digest is null) {
            return null;
        }
        RecapRowView view = ReadRowViewCore(
            connection,
            transaction,
            digest.Value
        ) ?? throw new InvalidDataException(
            "A row-view assignment references a missing RowView."
        );
        if (view.Coordinate.AssignmentKey != key) {
            throw new InvalidDataException(
                "A row-view assignment locator differs from its canonical value."
            );
        }
        return view;
    }

    private static RecapRowView? ReadRowViewCore(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        RowViewDigest digest
    ) {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT ref_id, timeline_id, history_row_id,
                   row_descriptor_digest, recipe_digest, target_digest,
                   previous_history_row_id, previous_view_digest,
                   bootstrap_completed,
                   length(canonical), canonical
            FROM row_view WHERE view_digest = $view;
            """;
        command.Parameters.AddWithValue("$view", digest.Value);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read()) {
            return null;
        }
        long length = reader.GetInt64(9);
        if (length is < 1
            or > RecapGridLimits.MaximumRowViewCanonicalUtf8Bytes) {
            throw new InvalidDataException(
                "A RowView canonical payload exceeds its byte bound."
            );
        }
        byte[] canonical = reader.GetFieldValue<byte[]>(10);
        RecapRowView view = RecapRowView.DecodeCanonical(canonical);
        if (canonical.Length != length
            || view.Digest != digest
            || !string.Equals(reader.GetString(0), view.RefId.ToHexString(),
                StringComparison.Ordinal)
            || !string.Equals(reader.GetString(1), view.TimelineId.Value,
                StringComparison.Ordinal)
            || !string.Equals(reader.GetString(2), view.HistoryRowId.Value,
                StringComparison.Ordinal)
            || !string.Equals(reader.GetString(3),
                view.RowDescriptorDigest.Value, StringComparison.Ordinal)
            || !string.Equals(reader.GetString(4), view.RecipeDigest.Value,
                StringComparison.Ordinal)
            || !string.Equals(reader.GetString(5), view.TargetDigest.Value,
                StringComparison.Ordinal)
            || !string.Equals(
                reader.IsDBNull(6) ? null : reader.GetString(6),
                view.PreviousHistoryRowId?.Value,
                StringComparison.Ordinal)
            || !string.Equals(
                reader.IsDBNull(7) ? null : reader.GetString(7),
                view.PreviousViewDigest?.Value,
                StringComparison.Ordinal)
            || reader.GetInt32(8) != (view.BootstrapCompleted ? 1 : 0)) {
            throw new InvalidDataException(
                "A RowView locator differs from its canonical payload."
            );
        }
        reader.Close();
        using SqliteCommand members = connection.CreateCommand();
        members.Transaction = transaction;
        members.CommandText = """
            SELECT m.column_ordinal, m.logical_column_id,
                   m.definition_digest, m.cell_digest,
                   c.logical_column_id, c.definition_digest,
                   length(c.canonical), c.canonical
            FROM row_view_member AS m
            LEFT JOIN cell_artifact AS c ON c.cell_digest = m.cell_digest
            WHERE m.view_digest = $view
            ORDER BY m.column_ordinal;
            """;
        members.Parameters.AddWithValue("$view", digest.Value);
        using SqliteDataReader memberReader = members.ExecuteReader();
        int index = 0;
        while (memberReader.Read()) {
            if (index >= view.OrderedCells.Count
                || memberReader.GetInt32(0) != index) {
                throw new InvalidDataException(
                    "A RowView member ordinal is invalid."
                );
            }
            RecapRowViewCell expected = view.OrderedCells[index];
            if (memberReader.IsDBNull(4)
                || memberReader.IsDBNull(5)
                || memberReader.IsDBNull(6)
                || memberReader.IsDBNull(7)) {
                throw new InvalidDataException(
                    "A RowView member references a missing Cell."
                );
            }
            long cellLength = memberReader.GetInt64(6);
            byte[] cellBytes = memberReader.GetFieldValue<byte[]>(7);
            if (cellLength is < 1
                or > RecapGridLimits.MaximumCellArtifactCanonicalUtf8Bytes
                || cellBytes.Length != cellLength) {
                throw new InvalidDataException(
                    "A RowView member Cell exceeds its canonical bound."
                );
            }
            RecapCellArtifact cell = RecapCellArtifact.DecodeCanonical(
                cellBytes
            );
            if (!string.Equals(memberReader.GetString(1),
                    expected.LogicalColumnId.Value, StringComparison.Ordinal)
                || !string.Equals(memberReader.GetString(2),
                    expected.DefinitionDigest.Value, StringComparison.Ordinal)
                || !string.Equals(memberReader.GetString(3),
                    expected.CellDigest.Value, StringComparison.Ordinal)
                || !string.Equals(memberReader.GetString(4),
                    cell.LogicalColumnId.Value, StringComparison.Ordinal)
                || !string.Equals(memberReader.GetString(5),
                    cell.DefinitionDigest.Value, StringComparison.Ordinal)
                || cell.LogicalColumnId != expected.LogicalColumnId
                || cell.DefinitionDigest != expected.DefinitionDigest
                || cell.CellDigest != expected.CellDigest) {
                throw new InvalidDataException(
                    "A RowView member differs from its Cell or manifest."
                );
            }
            index++;
        }
        if (index != view.OrderedCells.Count) {
            throw new InvalidDataException(
                "A RowView member set is incomplete."
            );
        }
        return view;
    }

    private static void InsertFulfilled(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        FulfilledViewKey key,
        RowViewDigest viewDigest
    ) {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO fulfilled_view_ref(
                ref_id, timeline_id, timeline_head_generation,
                through_row_descriptor_digest, recipe_digest,
                key_canonical, view_digest
            ) VALUES (
                $ref, $timeline, $generation, $through, $recipe,
                $canonical, $view
            );
            """;
        command.Parameters.AddWithValue("$ref", key.RefId.ToHexString());
        command.Parameters.AddWithValue("$timeline", key.TimelineId.Value);
        command.Parameters.AddWithValue(
            "$generation",
            key.TimelineHeadGeneration
        );
        command.Parameters.AddWithValue(
            "$through",
            key.ThroughRowDescriptorDigest.Value
        );
        command.Parameters.AddWithValue("$recipe", key.RecipeDigest.Value);
        command.Parameters.AddWithValue("$canonical", key.ToCanonicalBytes());
        command.Parameters.AddWithValue("$view", viewDigest.Value);
        command.ExecuteNonQuery();
    }

    private static RowViewDigest? ReadFulfilledCore(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        FulfilledViewKey expected
    ) {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT length(key_canonical), key_canonical, view_digest
            FROM fulfilled_view_ref
            WHERE ref_id = $ref
              AND timeline_id = $timeline
              AND timeline_head_generation = $generation
              AND through_row_descriptor_digest = $through
              AND recipe_digest = $recipe;
            """;
        command.Parameters.AddWithValue(
            "$ref",
            expected.RefId.ToHexString()
        );
        command.Parameters.AddWithValue(
            "$timeline",
            expected.TimelineId.Value
        );
        command.Parameters.AddWithValue(
            "$generation",
            expected.TimelineHeadGeneration
        );
        command.Parameters.AddWithValue(
            "$through",
            expected.ThroughRowDescriptorDigest.Value
        );
        command.Parameters.AddWithValue(
            "$recipe",
            expected.RecipeDigest.Value
        );
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read()) {
            return null;
        }
        long length = reader.GetInt64(0);
        if (length is < 1
            or > RecapGridLimits.MaximumFulfilledViewKeyCanonicalUtf8Bytes) {
            throw new InvalidDataException(
                "A fulfilled-view key exceeds its canonical byte bound."
            );
        }
        byte[] canonical = reader.GetFieldValue<byte[]>(1);
        FulfilledViewKey key = FulfilledViewKey.DecodeCanonical(canonical);
        if (canonical.Length != length
            || !canonical.SequenceEqual(expected.ToCanonicalBytes())
            || key.RefId != expected.RefId
            || key.TimelineId != expected.TimelineId
            || key.TimelineHeadGeneration
                != expected.TimelineHeadGeneration
            || key.ThroughRowDescriptorDigest
                != expected.ThroughRowDescriptorDigest
            || key.RecipeDigest != expected.RecipeDigest) {
            throw new InvalidDataException(
                "A fulfilled-view locator differs from its canonical key."
            );
        }
        RowViewDigest viewDigest = new(reader.GetString(2));
        reader.Close();
        RecapRowView view = ReadRowViewCore(
            connection,
            transaction,
            viewDigest
        ) ?? throw new InvalidDataException(
            "A fulfilled-view reference targets a missing RowView."
        );
        if (view.RefId != key.RefId
            || view.TimelineId != key.TimelineId
            || view.RecipeDigest != key.RecipeDigest
            || view.RowDescriptorDigest
                != key.ThroughRowDescriptorDigest) {
            throw new InvalidDataException(
                "A fulfilled-view reference targets a differently scoped RowView."
            );
        }
        return viewDigest;
    }

    private static void InsertCell(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        RecapCellArtifact cell
    ) {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO cell_artifact(
                cell_digest, evaluation_key_digest,
                history_segment_digest, logical_column_id,
                definition_digest, content_digest, canonical
            ) VALUES (
                $cell, $evaluation, $history, $column,
                $definition, $content, $canonical
            );
            """;
        command.Parameters.AddWithValue("$cell", cell.CellDigest.Value);
        command.Parameters.AddWithValue(
            "$evaluation",
            cell.EvaluationKey.Digest.Value
        );
        command.Parameters.AddWithValue(
            "$history",
            cell.EvaluationKey.HistorySegmentDigest.Value
        );
        command.Parameters.AddWithValue(
            "$column",
            cell.LogicalColumnId.Value
        );
        command.Parameters.AddWithValue(
            "$definition",
            cell.DefinitionDigest.Value
        );
        command.Parameters.AddWithValue(
            "$content",
            cell.ContentDigest.Value
        );
        command.Parameters.AddWithValue("$canonical", cell.ToCanonicalBytes());
        command.ExecuteNonQuery();
    }

    private static RecapCellArtifact? ReadCellByEvaluationKeyCore(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        EvaluationKeyDigest digest
    ) => ReadCellCore(
        connection,
        transaction,
        "evaluation_key_digest",
        digest.Value
    );

    private static RecapCellArtifact? ReadCellByDigestCore(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CellDigest digest
    ) => ReadCellCore(
        connection,
        transaction,
        "cell_digest",
        digest.Value
    );

    private static RecapCellArtifact? ReadCellCore(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string keyColumn,
        string key
    ) {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT cell_digest, evaluation_key_digest,
                   history_segment_digest, logical_column_id,
                   definition_digest, content_digest,
                   length(canonical), canonical
            FROM cell_artifact
            WHERE {keyColumn} = $key;
            """;
        command.Parameters.AddWithValue("$key", key);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read()) {
            return null;
        }
        long length = reader.GetInt64(6);
        if (length is < 1
            or > RecapGridLimits.MaximumCellArtifactCanonicalUtf8Bytes) {
            throw new InvalidDataException(
                "A Cell canonical payload exceeds its byte bound."
            );
        }
        byte[] canonical = reader.GetFieldValue<byte[]>(7);
        if (canonical.Length != length) {
            throw new InvalidDataException(
                "A Cell canonical payload length differs from its locator."
            );
        }
        RecapCellArtifact cell = RecapCellArtifact.DecodeCanonical(canonical);
        if (!string.Equals(reader.GetString(0), cell.CellDigest.Value,
                StringComparison.Ordinal)
            || !string.Equals(reader.GetString(1),
                cell.EvaluationKey.Digest.Value, StringComparison.Ordinal)
            || !string.Equals(reader.GetString(2),
                cell.EvaluationKey.HistorySegmentDigest.Value,
                StringComparison.Ordinal)
            || !string.Equals(reader.GetString(3), cell.LogicalColumnId.Value,
                StringComparison.Ordinal)
            || !string.Equals(reader.GetString(4), cell.DefinitionDigest.Value,
                StringComparison.Ordinal)
            || !string.Equals(reader.GetString(5), cell.ContentDigest.Value,
                StringComparison.Ordinal)) {
            throw new InvalidDataException(
                "A Cell locator differs from its canonical payload."
            );
        }
        return cell;
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
            command.CommandText = """
                SELECT
                    (SELECT user_version FROM pragma_user_version),
                    (SELECT application_id FROM pragma_application_id),
                    (SELECT COUNT(*) FROM store_metadata);
                """;
            using SqliteDataReader reader = command.ExecuteReader();
            if (!reader.Read()) {
                throw new InvalidDataException(
                    "RecapGrid Store schema identity is unavailable."
                );
            }
            int schemaVersion = reader.GetInt32(0);
            if (schemaVersion != SchemaVersion) {
                throw new StoreUnsupportedSchemaException(schemaVersion);
            }
            if (reader.GetInt32(1) != ApplicationId
                || reader.GetInt32(2) != 1) {
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
        return new RecapGridStoreIdentity(
            new RecapGridStoreInstanceId(reader.GetString(1)),
            SchemaVersion
        );
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
            "Atelia.SessionJournal.RecapGrid.Store.SchemaV2.sql"
        )
            ?? throw new InvalidOperationException(
                "The RecapGrid Store V2 schema resource is missing."
            );
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static bool ExportDigestTable(
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
                ? (ReadCellByDigestCore(
                    connection,
                    transaction,
                    new CellDigest(key)
                ) ?? throw new InvalidDataException(
                    "A Cell disappeared during export."
                )).ToCanonicalBytes()
                : (ReadRowViewCore(
                    connection,
                    transaction,
                    new RowViewDigest(key)
                ) ?? throw new InvalidDataException(
                    "A RowView disappeared during export."
                )).ToCanonicalBytes();
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
            last = RecapGridStoreExportCursor.CreateDigest(kind, key);
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
                       through_row_descriptor_digest, recipe_digest,
                       key_canonical, view_digest
                FROM fulfilled_view_ref
                WHERE (ref_id, timeline_id, timeline_head_generation,
                       through_row_descriptor_digest, recipe_digest)
                    >= ('', '', 0, '', '')
                ORDER BY ref_id, timeline_id, timeline_head_generation,
                         through_row_descriptor_digest, recipe_digest
                LIMIT $limit;
                """
            : """
                SELECT ref_id, timeline_id, timeline_head_generation,
                       through_row_descriptor_digest, recipe_digest,
                       key_canonical, view_digest
                FROM fulfilled_view_ref
                WHERE (ref_id, timeline_id, timeline_head_generation,
                       through_row_descriptor_digest, recipe_digest)
                    > ($ref, $timeline, $generation, $through, $recipe)
                ORDER BY ref_id, timeline_id, timeline_head_generation,
                         through_row_descriptor_digest, recipe_digest
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
                    reader.GetFieldValue<byte[]>(5),
                    reader.GetString(6)
                ));
            }
        }
        foreach (FulfilledPhysicalRow row in rows) {
            if (items.Count >= RecapGridStoreLimits.MaximumPageItems) {
                return false;
            }
            (FulfilledViewKey key, RowViewDigest viewDigest) =
                ValidateFulfilledPhysicalRow(connection, transaction, row);
            byte[] canonical = key.ToCanonicalBytes();
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
                        viewDigest
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
        if (item.CanonicalBytes is < 1
            or > RecapGridStoreLimits.MaximumPageBytes) {
            throw new InvalidDataException(
                "An export item exceeds the page byte bound."
            );
        }
        int nextBytes = checked(totalBytes + item.CanonicalBytes);
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
        string? after = null;
        while (true) {
            List<CellDigest> page = ReadDigestPage<CellDigest>(
                connection,
                transaction,
                "cell_artifact",
                "cell_digest",
                after,
                static value => new CellDigest(value)
            );
            foreach (CellDigest digest in page) {
                _ = ReadCellByDigestCore(connection, transaction, digest)
                    ?? throw new InvalidDataException(
                        "A Cell disappeared during verification."
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
            List<RowViewDigest> page = ReadDigestPage<RowViewDigest>(
                connection,
                transaction,
                "row_view",
                "view_digest",
                after,
                static value => new RowViewDigest(value)
            );
            foreach (RowViewDigest digest in page) {
                RecapRowView view = ReadRowViewCore(
                    connection,
                    transaction,
                    digest
                ) ?? throw new InvalidDataException(
                    "A RowView disappeared during verification."
                );
                if (view.PreviousViewDigest is { } previous) {
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
                    if (predecessor.Digest != previous
                        || predecessor.TargetDigest != view.TargetDigest
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
                           through_row_descriptor_digest, recipe_digest,
                           key_canonical, view_digest
                    FROM fulfilled_view_ref
                    WHERE (ref_id, timeline_id, timeline_head_generation,
                           through_row_descriptor_digest, recipe_digest)
                        >= ('', '', 0, '', '')
                    ORDER BY ref_id, timeline_id, timeline_head_generation,
                             through_row_descriptor_digest, recipe_digest
                    LIMIT 128;
                    """
                : """
                    SELECT ref_id, timeline_id, timeline_head_generation,
                           through_row_descriptor_digest, recipe_digest,
                           key_canonical, view_digest
                    FROM fulfilled_view_ref
                    WHERE (ref_id, timeline_id, timeline_head_generation,
                           through_row_descriptor_digest, recipe_digest)
                        > ($ref, $timeline, $generation, $through, $recipe)
                    ORDER BY ref_id, timeline_id, timeline_head_generation,
                             through_row_descriptor_digest, recipe_digest
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
                        reader.GetFieldValue<byte[]>(5),
                        reader.GetString(6)
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

    private static (FulfilledViewKey Key, RowViewDigest ViewDigest)
        ValidateFulfilledPhysicalRow(
            SqliteConnection connection,
            SqliteTransaction transaction,
            FulfilledPhysicalRow row
        ) {
        if (row.Canonical.Length is < 1
            or > RecapGridLimits.MaximumFulfilledViewKeyCanonicalUtf8Bytes) {
            throw new InvalidDataException(
                "A fulfilled-view key exceeds its canonical byte bound."
            );
        }
        FulfilledViewKey key = FulfilledViewKey.DecodeCanonical(row.Canonical);
        if (!string.Equals(
                row.RefId,
                key.RefId.ToHexString(),
                StringComparison.Ordinal)
            || !string.Equals(
                row.TimelineId,
                key.TimelineId.Value,
                StringComparison.Ordinal)
            || row.Generation != key.TimelineHeadGeneration
            || !string.Equals(
                row.Through,
                key.ThroughRowDescriptorDigest.Value,
                StringComparison.Ordinal)
            || !string.Equals(
                row.Recipe,
                key.RecipeDigest.Value,
                StringComparison.Ordinal)) {
            throw new InvalidDataException(
                "A fulfilled-view physical locator differs from its canonical key."
            );
        }
        var viewDigest = new RowViewDigest(row.ViewDigest);
        RecapRowView view = ReadRowViewCore(
            connection,
            transaction,
            viewDigest
        ) ?? throw new InvalidDataException(
            "A fulfilled-view reference targets a missing RowView."
        );
        if (view.TimelineId != key.TimelineId
            || view.RecipeDigest != key.RecipeDigest
            || view.RowDescriptorDigest
                != key.ThroughRowDescriptorDigest) {
            throw new InvalidDataException(
                "A fulfilled-view reference targets a differently scoped RowView."
            );
        }
        return (key, viewDigest);
    }

    private sealed record FulfilledPhysicalRow(
        string RefId,
        string TimelineId,
        long Generation,
        string Through,
        string Recipe,
        byte[] Canonical,
        string ViewDigest
    );

    private static List<T> ReadDigestPage<T>(
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
            page.Add(factory(reader.GetString(0)));
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
