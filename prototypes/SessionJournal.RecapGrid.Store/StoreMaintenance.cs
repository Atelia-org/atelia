namespace Atelia.SessionJournal.RecapGrid.Store;

public static class RecapGridStoreMaintenance {
    public static RecapGridStoreExportResult Export(
        string repositoryPath,
        RecapGridStoreExportCursor? after = null,
        bool includeContent = false
    ) {
        try {
            var paths = new StorePaths(repositoryPath);
            if (!StoreDurableFiles.RegularFileExists(
                    paths,
                    paths.DatabasePath)) {
                return new RecapGridStoreExportResult.Absent();
            }
            using FileStream lease = StoreDurableFiles.AcquireShared(paths);
            var store = new SqliteRecapGridStore(
                paths,
                StoreStorageLimits.Production,
                readOnly: true
            );
            return new RecapGridStoreExportResult.Page(
                store.ExportPage(after, includeContent)
            );
        }
        catch (PlatformNotSupportedException) {
            return new RecapGridStoreExportResult.PlatformUnsupported();
        }
        catch (StoreBusyException) {
            return new RecapGridStoreExportResult.Busy();
        }
        catch (Microsoft.Data.Sqlite.SqliteException exception) when (
            SqliteRecapGridStore.IsBusy(exception)) {
            return new RecapGridStoreExportResult.Busy();
        }
        catch (StoreUnsupportedSchemaException exception) {
            return new RecapGridStoreExportResult.UnsupportedSchema(
                exception.SchemaVersion
            );
        }
        catch (Exception exception) when (
            SqliteRecapGridStore.IsStoreFailure(exception)) {
            return new RecapGridStoreExportResult.Invalid(
                RecapGridStoreFactory.ErrorCode(exception),
                exception.Message
            );
        }
    }

    public static RecapGridStoreInspectResult Inspect(string repositoryPath) {
        try {
            var paths = new StorePaths(repositoryPath);
            if (!StoreDurableFiles.RegularFileExists(
                    paths,
                    paths.DatabasePath)) {
                return new RecapGridStoreInspectResult.Absent();
            }
            using FileStream lease = StoreDurableFiles.AcquireShared(paths);
            var store = new SqliteRecapGridStore(
                paths,
                StoreStorageLimits.Production,
                readOnly: true
            );
            return new RecapGridStoreInspectResult.Available(store.Inspect());
        }
        catch (PlatformNotSupportedException) {
            return new RecapGridStoreInspectResult.PlatformUnsupported();
        }
        catch (StoreBusyException) {
            return new RecapGridStoreInspectResult.Busy();
        }
        catch (Microsoft.Data.Sqlite.SqliteException exception) when (
            SqliteRecapGridStore.IsBusy(exception)) {
            return new RecapGridStoreInspectResult.Busy();
        }
        catch (StoreUnsupportedSchemaException exception) {
            return new RecapGridStoreInspectResult.UnsupportedSchema(
                exception.SchemaVersion
            );
        }
        catch (Exception exception) when (
            SqliteRecapGridStore.IsStoreFailure(exception)) {
            return new RecapGridStoreInspectResult.Invalid(
                RecapGridStoreFactory.ErrorCode(exception),
                exception.Message
            );
        }
    }

    public static RecapGridStoreVerifyResult Verify(string repositoryPath) {
        try {
            var paths = new StorePaths(repositoryPath);
            if (!StoreDurableFiles.RegularFileExists(
                    paths,
                    paths.DatabasePath)) {
                return new RecapGridStoreVerifyResult.Absent();
            }
            using FileStream lease = StoreDurableFiles.AcquireShared(paths);
            var store = new SqliteRecapGridStore(
                paths,
                StoreStorageLimits.Production,
                readOnly: true
            );
            return new RecapGridStoreVerifyResult.Healthy(
                store.VerifyFully()
            );
        }
        catch (PlatformNotSupportedException) {
            return new RecapGridStoreVerifyResult.PlatformUnsupported();
        }
        catch (StoreBusyException) {
            return new RecapGridStoreVerifyResult.Busy();
        }
        catch (Microsoft.Data.Sqlite.SqliteException exception) when (
            SqliteRecapGridStore.IsBusy(exception)) {
            return new RecapGridStoreVerifyResult.Busy();
        }
        catch (StoreUnsupportedSchemaException exception) {
            return new RecapGridStoreVerifyResult.UnsupportedSchema(
                exception.SchemaVersion
            );
        }
        catch (Exception exception) when (
            SqliteRecapGridStore.IsStoreFailure(exception)) {
            return new RecapGridStoreVerifyResult.Unhealthy(
                Array.AsReadOnly(new[] {
                    $"{RecapGridStoreFactory.ErrorCode(exception)}: {exception.Message}"
                }),
                Incomplete: true
            );
        }
    }

    public static RecapGridStorePrepareResetResult PrepareReset(
        string repositoryPath
    ) => PrepareResetCore(
        repositoryPath,
        StoreStorageLimits.Production
    );

    public static RecapGridStoreResetResult Reset(
        string repositoryPath,
        RecapGridStorePhysicalWitness expected
    ) => ResetCore(
        repositoryPath,
        expected,
        StoreStorageLimits.Production,
        StorePersistenceTestHooks.None
    );

    internal static RecapGridStoreResetResult ResetForTest(
        string repositoryPath,
        RecapGridStorePhysicalWitness expected,
        StoreStorageLimits limits,
        StorePersistenceTestHooks hooks
    ) => ResetCore(repositoryPath, expected, limits, hooks);

    private static RecapGridStorePrepareResetResult PrepareResetCore(
        string repositoryPath,
        StoreStorageLimits limits
    ) {
        try {
            var paths = new StorePaths(repositoryPath);
            if (!StoreDurableFiles.RegularFileExists(
                    paths,
                    paths.DatabasePath)) {
                return new RecapGridStorePrepareResetResult.Absent();
            }
            using FileStream lease = StoreDurableFiles.AcquireExclusive(
                paths,
                create: false
            );
            if (!StoreDurableFiles.RegularFileExists(
                    paths,
                    paths.DatabasePath)) {
                return new RecapGridStorePrepareResetResult.Absent();
            }
            string? sidecar = ExistingSidecar(paths);
            if (sidecar is not null) {
                return new RecapGridStorePrepareResetResult
                    .OfflineCleanupRequired(sidecar);
            }
            return new RecapGridStorePrepareResetResult.Prepared(
                StoreDurableFiles.ComputeWitness(paths)
            );
        }
        catch (PlatformNotSupportedException) {
            return new RecapGridStorePrepareResetResult.PlatformUnsupported();
        }
        catch (StoreBusyException) {
            return new RecapGridStorePrepareResetResult.Busy();
        }
        catch (StoreLimitException exception) {
            return new RecapGridStorePrepareResetResult.Limit(exception.Limit);
        }
        catch (Exception exception) when (
            SqliteRecapGridStore.IsStoreFailure(exception)) {
            return new RecapGridStorePrepareResetResult.Invalid(
                RecapGridStoreFactory.ErrorCode(exception),
                exception.Message
            );
        }
    }

    private static RecapGridStoreResetResult ResetCore(
        string repositoryPath,
        RecapGridStorePhysicalWitness expected,
        StoreStorageLimits limits,
        StorePersistenceTestHooks hooks
    ) {
        ArgumentNullException.ThrowIfNull(expected);
        try {
            var paths = new StorePaths(repositoryPath);
            if (!StoreDurableFiles.RegularFileExists(
                    paths,
                    paths.DatabasePath)) {
                return new RecapGridStoreResetResult.Absent();
            }
            using FileStream lease = StoreDurableFiles.AcquireExclusive(
                paths,
                create: false
            );
            if (!StoreDurableFiles.RegularFileExists(
                    paths,
                    paths.DatabasePath)) {
                return new RecapGridStoreResetResult.Absent();
            }
            string? sidecar = ExistingSidecar(paths);
            if (sidecar is not null) {
                return new RecapGridStoreResetResult
                    .OfflineCleanupRequired(sidecar);
            }
            RecapGridStorePhysicalWitness actual =
                StoreDurableFiles.ComputeWitness(paths);
            if (actual != expected) {
                return new RecapGridStoreResetResult.StaleConfirmation(actual);
            }
            string temporary = Path.Combine(
                paths.RootPath,
                $".grid.reset.{Guid.NewGuid():N}.sqlite"
            );
            paths.RequireSafe(temporary);
            bool published = false;
            RecapGridStoreIdentity? intended = null;
            try {
                intended = SqliteRecapGridStore.CreateDatabase(
                    temporary,
                    limits
                );
                hooks.BeforeResetPublish?.Invoke(temporary);
                File.Move(temporary, paths.DatabasePath, overwrite: true);
                published = true;
                hooks.AfterResetPublish?.Invoke(temporary);
                StoreDurableFiles.FlushDirectory(paths.RootPath);
                return new RecapGridStoreResetResult.Reset(intended);
            }
            catch (Exception) when (published) {
                RecapGridStoreIdentity? observed = null;
                try {
                    observed = new SqliteRecapGridStore(
                        paths,
                        limits
                    ).ReadIdentity();
                }
                catch { }
                return new RecapGridStoreResetResult.CommitIndeterminate(
                    intended!,
                    observed
                );
            }
            finally {
                if (!published) {
                    TryDeleteTemporary(temporary);
                }
            }
        }
        catch (PlatformNotSupportedException) {
            return new RecapGridStoreResetResult.PlatformUnsupported();
        }
        catch (StoreBusyException) {
            return new RecapGridStoreResetResult.Busy();
        }
        catch (StoreLimitException exception) {
            return new RecapGridStoreResetResult.Limit(exception.Limit);
        }
        catch (Exception exception) when (
            SqliteRecapGridStore.IsStoreFailure(exception)) {
            return new RecapGridStoreResetResult.Invalid(
                RecapGridStoreFactory.ErrorCode(exception),
                exception.Message
            );
        }
    }

    private static string? ExistingSidecar(StorePaths paths) {
        foreach (string path in new[] {
                     paths.JournalPath,
                     paths.WalPath,
                     paths.ShmPath
                 }) {
            if (StoreDurableFiles.RegularFileExists(paths, path)) {
                return Path.GetFileName(path);
            }
        }
        return null;
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
}
