namespace Atelia.SessionJournal.RecapGrid.Store;

public static class RecapGridStoreFactory {
    public static RecapGridStoreCreateResult Create(string repositoryPath)
        => CreateCore(
            repositoryPath,
            StoreStorageLimits.Production,
            StorePersistenceTestHooks.None
        );

    public static RecapGridStoreOpenResult Open(string repositoryPath)
        => OpenCore(
            repositoryPath,
            StoreStorageLimits.Production,
            StorePersistenceTestHooks.None
        );

    public static RecapGridStoreReaderOpenResult OpenReader(
        string repositoryPath
    ) => OpenReaderCore(repositoryPath, StoreStorageLimits.Production);

    internal static RecapGridStoreCreateResult CreateForTest(
        string repositoryPath,
        StoreStorageLimits limits,
        StorePersistenceTestHooks hooks
    ) => CreateCore(repositoryPath, limits, hooks);

    internal static RecapGridStoreOpenResult OpenForTest(
        string repositoryPath,
        StoreStorageLimits limits,
        StorePersistenceTestHooks hooks
    ) => OpenCore(repositoryPath, limits, hooks);

    private static RecapGridStoreCreateResult CreateCore(
        string repositoryPath,
        StoreStorageLimits limits,
        StorePersistenceTestHooks hooks
    ) {
        try {
            var paths = new StorePaths(repositoryPath);
            using FileStream lease = StoreDurableFiles.AcquireExclusive(
                paths,
                create: true
            );
            if (StoreDurableFiles.RegularFileExists(
                paths,
                paths.DatabasePath
            )) {
                return new RecapGridStoreCreateResult.AlreadyExists();
            }
            RequireNoSidecars(paths);
            string temporary = Path.Combine(
                paths.RootPath,
                $".grid.create.{Guid.NewGuid():N}.sqlite"
            );
            paths.RequireSafe(temporary);
            bool published = false;
            RecapGridStoreIdentity? intended = null;
            try {
                intended = SqliteRecapGridStore.CreateDatabase(
                    temporary,
                    limits
                );
                hooks.BeforeCreatePublish?.Invoke(temporary);
                File.Move(temporary, paths.DatabasePath);
                published = true;
                hooks.AfterCreatePublish?.Invoke(temporary);
                StoreDurableFiles.FlushDirectory(paths.RootPath);
                return new RecapGridStoreCreateResult.Created(intended);
            }
            catch (Exception) when (published) {
                return new RecapGridStoreCreateResult.CommitIndeterminate(
                    intended!,
                    TryReadIdentity(paths, limits)
                );
            }
            finally {
                if (!published) {
                    TryDeleteTemporary(temporary);
                }
            }
        }
        catch (PlatformNotSupportedException) {
            return new RecapGridStoreCreateResult.PlatformUnsupported();
        }
        catch (StoreBusyException) {
            return new RecapGridStoreCreateResult.Busy();
        }
        catch (Microsoft.Data.Sqlite.SqliteException exception) when (
            SqliteRecapGridStore.IsBusy(exception)) {
            return new RecapGridStoreCreateResult.Busy();
        }
        catch (StoreLimitException exception) {
            return new RecapGridStoreCreateResult.Limit(exception.Limit);
        }
        catch (Exception exception) when (
            SqliteRecapGridStore.IsStoreFailure(exception)
        ) {
            return new RecapGridStoreCreateResult.Invalid(
                ErrorCode(exception),
                exception.Message
            );
        }
    }

    private static RecapGridStoreOpenResult OpenCore(
        string repositoryPath,
        StoreStorageLimits limits,
        StorePersistenceTestHooks hooks
    ) {
        try {
            var paths = new StorePaths(repositoryPath);
            if (!StoreDurableFiles.RegularFileExists(
                    paths,
                    paths.DatabasePath)) {
                return new RecapGridStoreOpenResult.Absent();
            }
            FileStream lease = StoreDurableFiles.AcquireShared(paths);
            try {
                var store = new SqliteRecapGridStore(
                    paths,
                    limits,
                    hooks
                );
                RecapGridStoreIdentity identity = store.ReadIdentity();
                var lifetime = new StoreLifetime(lease);
                var reader = new RecapGridStoreReader(store, lifetime);
                var writer = new RecapGridStoreWriter(store, lifetime);
                return new RecapGridStoreOpenResult.Opened(
                    new RecapGridStoreHandle(
                        identity,
                        reader,
                        writer,
                        lifetime
                    )
                );
            }
            catch {
                lease.Dispose();
                throw;
            }
        }
        catch (PlatformNotSupportedException) {
            return new RecapGridStoreOpenResult.PlatformUnsupported();
        }
        catch (StoreBusyException) {
            return new RecapGridStoreOpenResult.Busy();
        }
        catch (Microsoft.Data.Sqlite.SqliteException exception) when (
            SqliteRecapGridStore.IsBusy(exception)) {
            return new RecapGridStoreOpenResult.Busy();
        }
        catch (StoreUnsupportedSchemaException exception) {
            return new RecapGridStoreOpenResult.UnsupportedSchema(
                exception.SchemaVersion
            );
        }
        catch (Exception exception) when (
            SqliteRecapGridStore.IsStoreFailure(exception)
        ) {
            return new RecapGridStoreOpenResult.Invalid(
                ErrorCode(exception),
                exception.Message
            );
        }
    }

    private static RecapGridStoreReaderOpenResult OpenReaderCore(
        string repositoryPath,
        StoreStorageLimits limits
    ) {
        try {
            var paths = new StorePaths(repositoryPath);
            if (!StoreDurableFiles.RegularFileExists(
                    paths,
                    paths.DatabasePath)) {
                return new RecapGridStoreReaderOpenResult.Absent();
            }
            FileStream lease = StoreDurableFiles.AcquireShared(paths);
            try {
                var store = new SqliteRecapGridStore(
                    paths,
                    limits,
                    readOnly: true
                );
                RecapGridStoreIdentity identity = store.ReadIdentity();
                var lifetime = new StoreLifetime(lease);
                var reader = new RecapGridStoreReader(store, lifetime);
                return new RecapGridStoreReaderOpenResult.Opened(
                    new RecapGridStoreReaderHandle(
                        identity,
                        reader,
                        lifetime
                    )
                );
            }
            catch {
                lease.Dispose();
                throw;
            }
        }
        catch (PlatformNotSupportedException) {
            return new RecapGridStoreReaderOpenResult.PlatformUnsupported();
        }
        catch (StoreBusyException) {
            return new RecapGridStoreReaderOpenResult.Busy();
        }
        catch (Microsoft.Data.Sqlite.SqliteException exception) when (
            SqliteRecapGridStore.IsBusy(exception)) {
            return new RecapGridStoreReaderOpenResult.Busy();
        }
        catch (StoreUnsupportedSchemaException exception) {
            return new RecapGridStoreReaderOpenResult.UnsupportedSchema(
                exception.SchemaVersion
            );
        }
        catch (Exception exception) when (
            SqliteRecapGridStore.IsStoreFailure(exception)
        ) {
            return new RecapGridStoreReaderOpenResult.Invalid(
                ErrorCode(exception),
                exception.Message
            );
        }
    }

    private static RecapGridStoreIdentity? TryReadIdentity(
        StorePaths paths,
        StoreStorageLimits limits
    ) {
        try {
            return new SqliteRecapGridStore(paths, limits).ReadIdentity();
        }
        catch {
            return null;
        }
    }

    private static void RequireNoSidecars(StorePaths paths) {
        foreach (string sidecar in new[] {
                     paths.JournalPath,
                     paths.WalPath,
                     paths.ShmPath
                 }) {
            if (StoreDurableFiles.RegularFileExists(paths, sidecar)) {
                throw new StoreException(
                    "GridStoreOrphanSidecar",
                    "An exact SQLite sidecar exists without the canonical database."
                );
            }
        }
    }

    private static void TryDeleteTemporary(string path) {
        foreach (string candidate in new[] {
                     path,
                     path + "-journal",
                     path + "-wal",
                     path + "-shm"
                 }) {
            try {
                File.Delete(candidate);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal static string ErrorCode(Exception exception) => exception switch {
        StoreException store => store.Code,
        StoreUnsupportedSchemaException => "GridStoreUnsupportedSchema",
        StoreLimitException => "GridStoreLimit",
        FileNotFoundException => "GridStoreSlotMissing",
        UnauthorizedAccessException => "GridStoreUnauthorized",
        InvalidDataException => "GridStoreInvalid",
        Microsoft.Data.Sqlite.SqliteException sqlite =>
            $"GridStoreSqlite{sqlite.SqliteErrorCode}",
        IOException => "GridStoreIoInvalid",
        _ => "GridStoreInvalid"
    };
}

public sealed class RecapGridStoreReader {
    private readonly SqliteRecapGridStore _store;
    private readonly StoreLifetime _lifetime;

    internal RecapGridStoreReader(
        SqliteRecapGridStore store,
        StoreLifetime lifetime
    ) {
        _store = store;
        _lifetime = lifetime;
    }

    public RecapGridStoreReadResult<RecapCellArtifact> TryReadCell(
        EvaluationKey evaluationKey
    ) {
        ArgumentNullException.ThrowIfNull(evaluationKey);
        using StoreLifetime.Operation? operation = _lifetime.TryEnter();
        if (operation is null) {
            return new RecapGridStoreReadResult<RecapCellArtifact>.Disposed();
        }
        return Read(() => _store.ReadCellByEvaluationKey(evaluationKey));
    }

    public RecapGridStoreReadResult<RecapCellArtifact> ReadCell(
        CellDigest cellDigest
    ) {
        using StoreLifetime.Operation? operation = _lifetime.TryEnter();
        if (operation is null) {
            return new RecapGridStoreReadResult<RecapCellArtifact>.Disposed();
        }
        return Read(() => _store.ReadCellByDigest(cellDigest));
    }

    public RecapGridMissingResult FindMissingAssignments(RowBuildSpec spec) {
        ArgumentNullException.ThrowIfNull(spec);
        using StoreLifetime.Operation? operation = _lifetime.TryEnter();
        if (operation is null) {
            return new RecapGridMissingResult.Disposed();
        }
        if (_store.TryInvalid(out string code, out string detail)) {
            return new RecapGridMissingResult.Invalid(code, detail);
        }
        try {
            return _store.FindMissing(spec);
        }
        catch (Microsoft.Data.Sqlite.SqliteException exception)
            when (SqliteRecapGridStore.IsBusy(exception)) {
            return new RecapGridMissingResult.Busy();
        }
        catch (Exception exception) when (
            SqliteRecapGridStore.IsStoreFailure(exception)
        ) {
            (code, detail) = _store.LatchInvalid(exception);
            return new RecapGridMissingResult.Invalid(code, detail);
        }
    }

    public RecapGridStoreReadResult<RecapRowView> ReadView(
        RowViewDigest digest
    ) {
        using StoreLifetime.Operation? operation = _lifetime.TryEnter();
        if (operation is null) {
            return new RecapGridStoreReadResult<RecapRowView>.Disposed();
        }
        if (_store.TryInvalid(out string code, out string detail)) {
            return new RecapGridStoreReadResult<RecapRowView>
                .Invalid(code, detail);
        }
        try {
            RecapRowView? value = _store.ReadRowView(digest);
            return value is null
                ? new RecapGridStoreReadResult<RecapRowView>.Missing()
                : new RecapGridStoreReadResult<RecapRowView>.Found(value);
        }
        catch (Microsoft.Data.Sqlite.SqliteException exception)
            when (SqliteRecapGridStore.IsBusy(exception)) {
            return new RecapGridStoreReadResult<RecapRowView>.Busy();
        }
        catch (Exception exception) when (
            SqliteRecapGridStore.IsStoreFailure(exception)
        ) {
            (code, detail) = _store.LatchInvalid(exception);
            return new RecapGridStoreReadResult<RecapRowView>
                .Invalid(code, detail);
        }
    }

    public RecapGridStoreReadResult<RecapGridFulfilledView> ReadFulfilled(
        FulfilledViewKey key
    ) {
        ArgumentNullException.ThrowIfNull(key);
        using StoreLifetime.Operation? operation = _lifetime.TryEnter();
        if (operation is null) {
            return new RecapGridStoreReadResult<RecapGridFulfilledView>
                .Disposed();
        }
        if (_store.TryInvalid(out string code, out string detail)) {
            return new RecapGridStoreReadResult<RecapGridFulfilledView>
                .Invalid(code, detail);
        }
        try {
            RecapGridFulfilledView? value = _store.ReadFulfilled(key);
            return value is null
                ? new RecapGridStoreReadResult<RecapGridFulfilledView>
                    .Missing()
                : new RecapGridStoreReadResult<RecapGridFulfilledView>
                    .Found(value);
        }
        catch (Microsoft.Data.Sqlite.SqliteException exception)
            when (SqliteRecapGridStore.IsBusy(exception)) {
            return new RecapGridStoreReadResult<RecapGridFulfilledView>.Busy();
        }
        catch (Exception exception) when (
            SqliteRecapGridStore.IsStoreFailure(exception)
        ) {
            (code, detail) = _store.LatchInvalid(exception);
            return new RecapGridStoreReadResult<RecapGridFulfilledView>
                .Invalid(code, detail);
        }
    }

    private RecapGridStoreReadResult<RecapCellArtifact> Read(
        Func<RecapCellArtifact?> action
    ) {
        if (_store.TryInvalid(out string code, out string detail)) {
            return new RecapGridStoreReadResult<RecapCellArtifact>
                .Invalid(code, detail);
        }
        try {
            RecapCellArtifact? value = action();
            return value is null
                ? new RecapGridStoreReadResult<RecapCellArtifact>.Missing()
                : new RecapGridStoreReadResult<RecapCellArtifact>.Found(value);
        }
        catch (Microsoft.Data.Sqlite.SqliteException exception)
            when (SqliteRecapGridStore.IsBusy(exception)) {
            return new RecapGridStoreReadResult<RecapCellArtifact>.Busy();
        }
        catch (Exception exception) when (
            SqliteRecapGridStore.IsStoreFailure(exception)
        ) {
            (code, detail) = _store.LatchInvalid(exception);
            return new RecapGridStoreReadResult<RecapCellArtifact>
                .Invalid(code, detail);
        }
    }
}

public sealed class RecapGridStoreWriter {
    private readonly SqliteRecapGridStore _store;
    private readonly StoreLifetime _lifetime;

    internal RecapGridStoreWriter(
        SqliteRecapGridStore store,
        StoreLifetime lifetime
    ) {
        _store = store;
        _lifetime = lifetime;
    }

    public RecapGridCellPutResult PutCell(RecapCellArtifact cell) {
        ArgumentNullException.ThrowIfNull(cell);
        using StoreLifetime.Operation? operation = _lifetime.TryEnter();
        return operation is null
            ? new RecapGridCellPutResult.Disposed()
            : _store.PutCell(cell);
    }

    public RecapGridRowViewPutResult PutRowView(
        RowBuildSpec spec,
        RecapRowView view
    ) {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(view);
        using StoreLifetime.Operation? operation = _lifetime.TryEnter();
        return operation is null
            ? new RecapGridRowViewPutResult.Disposed()
            : _store.PutRowView(spec, view);
    }

    public RecapGridFulfilledPutResult PutFulfilled(
        FulfilledViewKey key,
        RowViewDigest viewDigest
    ) {
        ArgumentNullException.ThrowIfNull(key);
        using StoreLifetime.Operation? operation = _lifetime.TryEnter();
        return operation is null
            ? new RecapGridFulfilledPutResult.Disposed()
            : _store.PutFulfilled(key, viewDigest);
    }
}
