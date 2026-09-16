using Microsoft.Data.Sqlite;

namespace Atelia.SessionJournal.RecapGrid.Store;

public static partial class RecapGridStoreMaintenance {
    public static RecapGridStorePrepareRestoreResult PrepareRestoreV4(
        string repositoryPath,
        string backupPath
    ) => PrepareRestoreV4(repositoryPath, backupPath, static () => []);

    public static RecapGridStorePrepareRestoreResult PrepareRestoreV4(
        string repositoryPath,
        string backupPath,
        Func<IReadOnlyList<RowWork>> resolvePartialWorkProofs
    ) {
        ArgumentNullException.ThrowIfNull(resolvePartialWorkProofs);
        try {
            var paths = new StorePaths(repositoryPath);
            string backup = RequireExactV4BackupPath(paths, backupPath);
            if (!StoreDurableFiles.RegularFileExists(paths, paths.DatabasePath)) {
                return new RecapGridStorePrepareRestoreResult.Absent();
            }
            if (!StoreDurableFiles.RegularFileExists(paths, backup)) {
                return new RecapGridStorePrepareRestoreResult.BackupAbsent();
            }
            using FileStream lease = StoreDurableFiles.AcquireExclusive(
                paths, create: false);
            if (!StoreDurableFiles.RegularFileExists(paths, paths.DatabasePath)) {
                return new RecapGridStorePrepareRestoreResult.Absent();
            }
            if (!StoreDurableFiles.RegularFileExists(paths, backup)) {
                return new RecapGridStorePrepareRestoreResult.BackupAbsent();
            }
            string? sidecar = ExistingRestoreSidecar(paths, backup);
            if (sidecar is not null) {
                return new RecapGridStorePrepareRestoreResult
                    .OfflineCleanupRequired(sidecar);
            }
            int backupSchema = ReadSchema(backup);
            if (backupSchema != 4) {
                return new RecapGridStorePrepareRestoreResult.UnsupportedSchema(
                    "backup", backupSchema);
            }
            IReadOnlyList<RowWork> partialWorkProofs =
                resolvePartialWorkProofs();
            RecapGridStoreUpgradeEvidence backupEvidence = VerifyV4(
                paths, backup, partialWorkProofs).Evidence;
            int activeSchema = ReadSchema(paths.DatabasePath);
            if (activeSchema == 4) {
                RecapGridStoreUpgradeEvidence restored = VerifyV4(
                    paths, paths.DatabasePath, partialWorkProofs).Evidence;
                if (restored == backupEvidence) {
                    return new RecapGridStorePrepareRestoreResult
                        .AlreadyRestored(backup, restored);
                }
                return new RecapGridStorePrepareRestoreResult.UnsupportedSchema(
                    "active", activeSchema);
            }
            if (activeSchema != SqliteRecapGridStore.SchemaVersion) {
                return new RecapGridStorePrepareRestoreResult.UnsupportedSchema(
                    "active", activeSchema);
            }
            RecapGridStoreUpgradeEvidence active = VerifyV5(
                paths, paths.DatabasePath);
            return new RecapGridStorePrepareRestoreResult.Prepared(
                backup, active, backupEvidence);
        }
        catch (PlatformNotSupportedException) {
            return new RecapGridStorePrepareRestoreResult.PlatformUnsupported();
        }
        catch (StoreBusyException) {
            return new RecapGridStorePrepareRestoreResult.Busy();
        }
        catch (SqliteException exception) when (
            SqliteRecapGridStore.IsBusy(exception)) {
            return new RecapGridStorePrepareRestoreResult.Busy();
        }
        catch (RecapGridStorePartialProofException exception) {
            return new RecapGridStorePrepareRestoreResult.Invalid(
                PartialProofCode(exception.Failure), exception.Message);
        }
        catch (Exception exception) when (!IsFatal(exception)) {
            return new RecapGridStorePrepareRestoreResult.Invalid(
                RestoreErrorCode(exception), exception.Message);
        }
    }

    public static RecapGridStoreRestoreResult RestoreV4(
        string repositoryPath,
        string backupPath,
        RecapGridStorePhysicalWitness expectedActive,
        RecapGridStorePhysicalWitness expectedBackup
    ) => RestoreV4Core(repositoryPath, backupPath, expectedActive,
        expectedBackup, static () => [], StoreRestoreTestHooks.None);

    public static RecapGridStoreRestoreResult RestoreV4(
        string repositoryPath,
        string backupPath,
        RecapGridStorePhysicalWitness expectedActive,
        RecapGridStorePhysicalWitness expectedBackup,
        Func<IReadOnlyList<RowWork>> resolvePartialWorkProofs
    ) => RestoreV4Core(repositoryPath, backupPath, expectedActive,
        expectedBackup, resolvePartialWorkProofs, StoreRestoreTestHooks.None);

    internal static RecapGridStoreRestoreResult RestoreV4ForTest(
        string repositoryPath,
        string backupPath,
        RecapGridStorePhysicalWitness expectedActive,
        RecapGridStorePhysicalWitness expectedBackup,
        Func<IReadOnlyList<RowWork>> resolvePartialWorkProofs,
        StoreRestoreTestHooks hooks
    ) => RestoreV4Core(repositoryPath, backupPath, expectedActive,
        expectedBackup, resolvePartialWorkProofs, hooks);

    private static RecapGridStoreRestoreResult RestoreV4Core(
        string repositoryPath,
        string backupPath,
        RecapGridStorePhysicalWitness expectedActive,
        RecapGridStorePhysicalWitness expectedBackup,
        Func<IReadOnlyList<RowWork>> resolvePartialWorkProofs,
        StoreRestoreTestHooks hooks
    ) {
        ArgumentNullException.ThrowIfNull(expectedActive);
        ArgumentNullException.ThrowIfNull(expectedBackup);
        ArgumentNullException.ThrowIfNull(resolvePartialWorkProofs);
        ArgumentNullException.ThrowIfNull(hooks);
        try {
            var paths = new StorePaths(repositoryPath);
            string backup = RequireExactV4BackupPath(paths, backupPath);
            if (!StoreDurableFiles.RegularFileExists(paths, paths.DatabasePath)) {
                return new RecapGridStoreRestoreResult.Absent();
            }
            if (!StoreDurableFiles.RegularFileExists(paths, backup)) {
                return new RecapGridStoreRestoreResult.BackupAbsent();
            }
            using FileStream lease = StoreDurableFiles.AcquireExclusive(
                paths, create: false);
            if (!StoreDurableFiles.RegularFileExists(paths, paths.DatabasePath)) {
                return new RecapGridStoreRestoreResult.Absent();
            }
            if (!StoreDurableFiles.RegularFileExists(paths, backup)) {
                return new RecapGridStoreRestoreResult.BackupAbsent();
            }
            string? sidecar = ExistingRestoreSidecar(paths, backup);
            if (sidecar is not null) {
                return new RecapGridStoreRestoreResult
                    .OfflineCleanupRequired(sidecar);
            }
            RecapGridStorePhysicalWitness backupWitness =
                StoreDurableFiles.ComputeWitness(paths, backup);
            if (backupWitness != expectedBackup) {
                return new RecapGridStoreRestoreResult.BackupChanged(
                    backupWitness);
            }
            RecapGridStorePhysicalWitness activeWitness =
                StoreDurableFiles.ComputeWitness(paths);
            bool mayBeAlreadyRestored = activeWitness == expectedBackup;
            if (!mayBeAlreadyRestored && activeWitness != expectedActive) {
                return new RecapGridStoreRestoreResult.ActiveChanged(
                    activeWitness);
            }
            int backupSchema = ReadSchema(backup);
            if (backupSchema != 4) {
                return new RecapGridStoreRestoreResult.UnsupportedSchema(
                    "backup", backupSchema);
            }
            IReadOnlyList<RowWork> partialWorkProofs =
                resolvePartialWorkProofs();
            RecapGridStoreUpgradeEvidence backupEvidence = VerifyV4(
                paths, backup, partialWorkProofs).Evidence;
            if (backupEvidence.Witness != expectedBackup) {
                return new RecapGridStoreRestoreResult.BackupChanged(
                    backupEvidence.Witness);
            }
            if (mayBeAlreadyRestored) {
                int restoredSchema = ReadSchema(paths.DatabasePath);
                if (restoredSchema != 4) {
                    return new RecapGridStoreRestoreResult.ActiveChanged(
                        StoreDurableFiles.ComputeWitness(paths));
                }
                RecapGridStoreUpgradeEvidence restored = VerifyV4(
                    paths, paths.DatabasePath, partialWorkProofs).Evidence;
                if (restored == backupEvidence) {
                    return new RecapGridStoreRestoreResult.AlreadyRestored(
                        backup, restored);
                }
                return new RecapGridStoreRestoreResult.ActiveChanged(
                    restored.Witness);
            }
            int activeSchema = ReadSchema(paths.DatabasePath);
            if (activeSchema != SqliteRecapGridStore.SchemaVersion) {
                return new RecapGridStoreRestoreResult.UnsupportedSchema(
                    "active", activeSchema);
            }
            RecapGridStoreUpgradeEvidence active = VerifyV5(
                paths, paths.DatabasePath);
            if (active.Witness != expectedActive) {
                return new RecapGridStoreRestoreResult.ActiveChanged(
                    active.Witness);
            }
            string temporary = Path.Combine(paths.RootPath,
                $".grid.restore-v4.{Guid.NewGuid():N}.sqlite");
            paths.RequireSafe(temporary);
            bool published = false;
            try {
                File.Copy(backup, temporary, overwrite: false);
                StoreDurableFiles.FlushFile(paths, temporary);
                RecapGridStoreUpgradeEvidence temporaryEvidence = VerifyV4(
                    paths, temporary, partialWorkProofs).Evidence;
                if (temporaryEvidence != backupEvidence) {
                    throw new InvalidDataException(
                        "The V4 restore temporary does not match its backup.");
                }
                hooks.AfterTempVerified?.Invoke();
                RecapGridStoreUpgradeEvidence backupBeforeReplace = VerifyV4(
                    paths, backup, partialWorkProofs).Evidence;
                if (backupBeforeReplace != backupEvidence
                    || backupBeforeReplace.Witness != expectedBackup) {
                    return new RecapGridStoreRestoreResult.BackupChanged(
                        backupBeforeReplace.Witness);
                }
                hooks.AfterBackupVerifiedBeforeActiveRecheck?.Invoke();
                sidecar = ExistingRestoreSidecar(paths, backup);
                if (sidecar is not null) {
                    return new RecapGridStoreRestoreResult
                        .OfflineCleanupRequired(sidecar);
                }
                RecapGridStoreUpgradeEvidence activeBeforeReplace = VerifyV5(
                    paths, paths.DatabasePath);
                if (activeBeforeReplace != active
                    || activeBeforeReplace.Witness != expectedActive) {
                    return new RecapGridStoreRestoreResult.ActiveChanged(
                        activeBeforeReplace.Witness);
                }
                File.Move(temporary, paths.DatabasePath, overwrite: true);
                published = true;
                hooks.AfterReplaceBeforeDirectoryFsync?.Invoke();
                StoreDurableFiles.FlushDirectory(paths.RootPath);
                hooks.AfterDirectoryFsyncBeforeVerify?.Invoke();
                RecapGridStoreUpgradeEvidence restored = VerifyV4(
                    paths, paths.DatabasePath, partialWorkProofs).Evidence;
                if (restored != backupEvidence) {
                    throw new InvalidDataException(
                        "The restored V4 Store does not match its backup.");
                }
                hooks.AfterVerify?.Invoke();
                return new RecapGridStoreRestoreResult.Restored(
                    backup, restored);
            }
            catch (Exception exception) when (published
                && !IsFatal(exception)) {
                return new RecapGridStoreRestoreResult.CommitIndeterminate(
                    backup, backupEvidence,
                    ObserveRestoreActive(paths, partialWorkProofs),
                    "inspect-active-before-any-restore-or-upgrade");
            }
            finally {
                if (!published) {
                    TryDeleteTemporary(temporary);
                }
            }
        }
        catch (PlatformNotSupportedException) {
            return new RecapGridStoreRestoreResult.PlatformUnsupported();
        }
        catch (StoreBusyException) {
            return new RecapGridStoreRestoreResult.Busy();
        }
        catch (SqliteException exception) when (
            SqliteRecapGridStore.IsBusy(exception)) {
            return new RecapGridStoreRestoreResult.Busy();
        }
        catch (RecapGridStorePartialProofException exception) {
            return new RecapGridStoreRestoreResult.Invalid(
                PartialProofCode(exception.Failure), exception.Message);
        }
        catch (Exception exception) when (!IsFatal(exception)) {
            return new RecapGridStoreRestoreResult.Invalid(
                RestoreErrorCode(exception), exception.Message);
        }
    }

    private static string RequireExactV4BackupPath(
        StorePaths paths,
        string backupPath
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupPath);
        if (!Path.IsPathFullyQualified(backupPath)) {
            throw new StoreException("GridStoreBackupPathInvalid",
                "The V4 backup path must be absolute.");
        }
        string full = Path.GetFullPath(backupPath);
        paths.RequireSafe(full);
        if (!string.Equals(Path.GetDirectoryName(full), paths.RootPath,
                StringComparison.Ordinal)) {
            throw new StoreException("GridStoreBackupPathInvalid",
                "The V4 backup must be an exact file in the Store root.");
        }
        string name = Path.GetFileName(full);
        const string prefix = "grid.sqlite.v4-backup-";
        const string suffix = ".sqlite";
        string middle = name.StartsWith(prefix, StringComparison.Ordinal)
            && name.EndsWith(suffix, StringComparison.Ordinal)
            ? name[prefix.Length..^suffix.Length]
            : string.Empty;
        if (!name.StartsWith(prefix, StringComparison.Ordinal)
            || !name.EndsWith(suffix, StringComparison.Ordinal)
            || middle.Length != 52
            || middle[19] != '-'
            || !DateTimeOffset.TryParseExact(middle[..19],
                "yyyyMMdd'T'HHmmssfff'Z'",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal
                    | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out _)
            || !Guid.TryParseExact(middle[20..], "N", out Guid id)
            || !string.Equals(id.ToString("N"), middle[20..],
                StringComparison.Ordinal)) {
            throw new StoreException("GridStoreBackupPathInvalid",
                "The V4 backup name is not an exact Store backup slot.");
        }
        return full;
    }

    private static string? ExistingRestoreSidecar(
        StorePaths paths,
        string backup
    ) {
        foreach (string candidate in new[] {
                     paths.JournalPath, paths.WalPath, paths.ShmPath,
                     backup + "-journal", backup + "-wal", backup + "-shm"
                 }) {
            if (StoreDurableFiles.RegularFileExists(paths, candidate)) {
                return Path.GetFileName(candidate);
            }
        }
        return null;
    }

    private static int ReadSchema(string path) {
        using SqliteConnection connection = OpenRaw(path, readOnly: true);
        return ReadV4Version(connection);
    }

    private static RecapGridStoreUpgradeObservation ObserveRestoreActive(
        StorePaths paths,
        IReadOnlyList<RowWork> partialWorkProofs
    ) {
        int? schema = null;
        RecapGridStoreIdentity? identity = null;
        RecapGridStorePhysicalWitness? witness = null;
        try {
            if (StoreDurableFiles.RegularFileExists(paths, paths.DatabasePath)) {
                schema = ReadSchema(paths.DatabasePath);
                if (schema == 4) {
                    RecapGridStoreUpgradeEvidence evidence = VerifyV4(paths,
                        paths.DatabasePath, partialWorkProofs).Evidence;
                    identity = evidence.Identity;
                    witness = evidence.Witness;
                }
                else if (schema == SqliteRecapGridStore.SchemaVersion) {
                    RecapGridStoreUpgradeEvidence evidence = VerifyV5(paths,
                        paths.DatabasePath);
                    identity = evidence.Identity;
                    witness = evidence.Witness;
                }
                else {
                    witness = StoreDurableFiles.ComputeWitness(paths);
                }
            }
        }
        catch (Exception exception) when (!IsFatal(exception)) { }
        return new RecapGridStoreUpgradeObservation(schema, identity, witness);
    }

    private static string PartialProofCode(
        RecapGridStorePartialProofFailure failure
    ) => failure switch {
        RecapGridStorePartialProofFailure.Unprovable =>
            "partial-proof-unprovable",
        RecapGridStorePartialProofFailure.Ambiguous =>
            "partial-proof-ambiguous",
        RecapGridStorePartialProofFailure.Unavailable =>
            "partial-proof-unavailable",
        _ => "partial-proof-unprovable"
    };

    private static string RestoreErrorCode(Exception exception)
        => exception is ArgumentException or FormatException
            ? "GridStoreBackupPathInvalid"
            : SqliteRecapGridStore.IsStoreFailure(exception)
                ? RecapGridStoreFactory.ErrorCode(exception)
                : "GridStoreRestoreFailed";
}
