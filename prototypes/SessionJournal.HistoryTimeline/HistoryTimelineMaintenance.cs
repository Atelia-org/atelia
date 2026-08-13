using System.Security.Cryptography;
using Atelia.EventJournal;
using Microsoft.Data.Sqlite;

namespace Atelia.SessionJournal.HistoryTimeline;

public static class HistoryTimelineMaintenance {
    private const string BackupDatabaseFileName = "timeline.sqlite";
    private const string BackupManifestFileName = "manifest.json";

    public static HistoryTimelineReaderOpenResult OpenReader(
        string repositoryPath,
        RefId refId
    ) => OpenReaderCore(
        repositoryPath,
        refId,
        HistoryTimelineStorageLimits.Production
    );

    public static HistoryTimelineInspectResult Inspect(
        string repositoryPath,
        RefId refId
    ) {
        HistoryTimelineReaderOpenResult opened = OpenReader(
            repositoryPath,
            refId
        );
        switch (opened) {
            case HistoryTimelineReaderOpenResult.Absent:
                return new HistoryTimelineInspectResult.Absent();
            case HistoryTimelineReaderOpenResult.Busy:
                return new HistoryTimelineInspectResult.Busy();
            case HistoryTimelineReaderOpenResult.UnsupportedSchema schema:
                return new HistoryTimelineInspectResult.Invalid(
                    "TimelineStoreUnsupportedSchema",
                    HistoryTimelineCoordinator.UnsupportedSchemaDetail(
                        schema.SchemaVersion
                    )
                );
            case HistoryTimelineReaderOpenResult.Invalid invalid:
                return new HistoryTimelineInspectResult.Invalid(
                    invalid.Code,
                    invalid.Detail
                );
            case HistoryTimelineReaderOpenResult.Opened available:
                using (available.Handle) {
                    HistoryTimelineSnapshotResult snapshot =
                        available.Handle.Reader.ReadSnapshot();
                    return snapshot switch {
                        HistoryTimelineSnapshotResult.Available head
                            => new HistoryTimelineInspectResult.Available(
                                available.Handle.Locator,
                                head.Head
                            ),
                        HistoryTimelineSnapshotResult.Busy
                            => new HistoryTimelineInspectResult.Busy(),
                        HistoryTimelineSnapshotResult.UnsupportedSchema schema
                            => new HistoryTimelineInspectResult.Invalid(
                                "TimelineStoreUnsupportedSchema",
                                HistoryTimelineCoordinator
                                    .UnsupportedSchemaDetail(
                                        schema.SchemaVersion
                                    )
                            ),
                        HistoryTimelineSnapshotResult.Invalid invalid
                            => new HistoryTimelineInspectResult.Invalid(
                                invalid.Code,
                                invalid.Detail
                            ),
                        _ => new HistoryTimelineInspectResult.Invalid(
                            "TimelineHeadUnavailable",
                            "The active Timeline has no canonical head."
                        )
                    };
                }
            default:
                return new HistoryTimelineInspectResult.Invalid(
                    "TimelineReaderOpenOutcomeInvalid",
                    "The Timeline reader returned an unknown outcome."
                );
        }
    }

    public static HistoryTimelineExportResult Export(
        string repositoryPath,
        RefId refId,
        TimelineHeadRef? expectedWholeHead = null,
        HistoryTimelinePathCursor? cursor = null,
        int maximumRows = HistoryTimelineStoreLimits.MaximumPathPageRows
    ) {
        HistoryTimelineReaderOpenResult opened = OpenReader(
            repositoryPath,
            refId
        );
        switch (opened) {
            case HistoryTimelineReaderOpenResult.Absent:
                return new HistoryTimelineExportResult.Absent();
            case HistoryTimelineReaderOpenResult.Busy:
                return new HistoryTimelineExportResult.Busy();
            case HistoryTimelineReaderOpenResult.UnsupportedSchema schema:
                return new HistoryTimelineExportResult.Invalid(
                    "TimelineStoreUnsupportedSchema",
                    HistoryTimelineCoordinator.UnsupportedSchemaDetail(
                        schema.SchemaVersion
                    )
                );
            case HistoryTimelineReaderOpenResult.Invalid invalid:
                return new HistoryTimelineExportResult.Invalid(
                    invalid.Code,
                    invalid.Detail
                );
            case HistoryTimelineReaderOpenResult.Opened available:
                using (available.Handle) {
                    HistoryTimelineSnapshotResult snapshot =
                        available.Handle.Reader.ReadSnapshot();
                    if (snapshot is HistoryTimelineSnapshotResult.Busy) {
                        return new HistoryTimelineExportResult.Busy();
                    }
                    if (snapshot is HistoryTimelineSnapshotResult
                            .UnsupportedSchema schema) {
                        return new HistoryTimelineExportResult.Invalid(
                            "TimelineStoreUnsupportedSchema",
                            HistoryTimelineCoordinator
                                .UnsupportedSchemaDetail(
                                    schema.SchemaVersion
                                )
                        );
                    }
                    if (snapshot is HistoryTimelineSnapshotResult
                            .Invalid snapshotInvalid) {
                        return new HistoryTimelineExportResult.Invalid(
                            snapshotInvalid.Code,
                            snapshotInvalid.Detail
                        );
                    }
                    if (snapshot is not HistoryTimelineSnapshotResult
                            .Available current) {
                        return new HistoryTimelineExportResult.Invalid(
                            "TimelineHeadUnavailable",
                            "The active Timeline has no canonical head."
                        );
                    }
                    if (expectedWholeHead is not null
                        && expectedWholeHead != current.Head) {
                        return new HistoryTimelineExportResult
                            .StaleTimelineHead(current.Head);
                    }
                    TimelineHeadRef boundHead = expectedWholeHead
                        ?? current.Head;
                    return available.Handle.Reader
                        .ReadSelectedPathPage(
                            boundHead,
                            cursor,
                            maximumRows
                        ) switch {
                            HistoryTimelinePathPageResult.Page page
                                => new HistoryTimelineExportResult.Page(
                                    new HistoryTimelineExportPage(
                                        available.Handle.Locator,
                                        boundHead,
                                        page.Value
                                    )
                                ),
                            HistoryTimelinePathPageResult
                                .StaleTimelineHead stale
                                => new HistoryTimelineExportResult
                                    .StaleTimelineHead(stale.Actual),
                            HistoryTimelinePathPageResult.Busy
                                => new HistoryTimelineExportResult.Busy(),
                            HistoryTimelinePathPageResult.Invalid invalid
                                => new HistoryTimelineExportResult.Invalid(
                                    invalid.Code,
                                    invalid.Detail
                                ),
                            _ => new HistoryTimelineExportResult.Invalid(
                                "TimelinePathOutcomeInvalid",
                                "The Timeline reader returned an unknown path outcome."
                            )
                        };
                }
            default:
                return new HistoryTimelineExportResult.Invalid(
                    "TimelineReaderOpenOutcomeInvalid",
                    "The Timeline reader returned an unknown outcome."
                );
        }
    }

    public static HistoryTimelineInspectResult Verify(
        string repositoryPath,
        RefId refId
    ) {
        string canonicalPath;
        HistoryTimelinePaths paths;
        FileStream? lease = null;
        try {
            canonicalPath = CanonicalRepositoryPath(repositoryPath);
            paths = new HistoryTimelinePaths(canonicalPath, refId);
            if (!HistoryTimelineDurableFiles.ExistsExact(
                    canonicalPath,
                    paths.LocatorPath)) {
                return new HistoryTimelineInspectResult.Absent();
            }
            lease = HistoryTimelineDurableFiles.AcquireSharedExisting(
                paths
            );
            ActiveTimelineLocator locator =
                HistoryTimelineFactory.ReadLocator(paths);
            var ledger = CreateLedger(paths, locator);
            HistoryTimelineStoreReadResult<TimelineHeadRef> verified =
                ledger.VerifyFully();
            return verified switch {
                HistoryTimelineStoreReadResult<TimelineHeadRef>.Found found
                    => new HistoryTimelineInspectResult.Available(
                        locator,
                        found.Value
                    ),
                HistoryTimelineStoreReadResult<TimelineHeadRef>.Busy
                    => new HistoryTimelineInspectResult.Busy(),
                HistoryTimelineStoreReadResult<TimelineHeadRef>
                    .UnsupportedSchema schema
                    => new HistoryTimelineInspectResult.Invalid(
                        "TimelineStoreUnsupportedSchema",
                        HistoryTimelineCoordinator
                            .UnsupportedSchemaDetail(schema.SchemaVersion)
                    ),
                HistoryTimelineStoreReadResult<TimelineHeadRef>
                    .Invalid invalid
                    => new HistoryTimelineInspectResult.Invalid(
                        invalid.Code,
                        invalid.Detail
                    ),
                _ => new HistoryTimelineInspectResult.Invalid(
                    "TimelineHeadUnavailable",
                    "The active Timeline has no canonical head."
                )
            };
        }
        catch (HistoryTimelineLeaseBusyException) {
            return new HistoryTimelineInspectResult.Busy();
        }
        catch (Exception exception) when (IsMaintenanceFailure(exception)) {
            return new HistoryTimelineInspectResult.Invalid(
                MaintenanceErrorCode(exception),
                exception.Message
            );
        }
        finally {
            lease?.Dispose();
        }
    }

    public static HistoryTimelineBackupResult Backup(
        string repositoryPath,
        RefId refId,
        string backupDirectory
    ) => BackupCore(
        repositoryPath,
        refId,
        backupDirectory,
        HistoryTimelineStorageLimits.Production,
        HistoryTimelinePersistenceTestHooks.None
    );

    internal static HistoryTimelineBackupResult BackupCore(
        string repositoryPath,
        RefId refId,
        string backupDirectory,
        HistoryTimelineStorageLimits limits,
        HistoryTimelinePersistenceTestHooks hooks
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupDirectory);
        FileStream? lease = null;
        string? staging = null;
        try {
            string canonicalPath = CanonicalRepositoryPath(repositoryPath);
            var paths = new HistoryTimelinePaths(canonicalPath, refId);
            if (!HistoryTimelineDurableFiles.ExistsExact(
                    canonicalPath,
                    paths.LocatorPath)) {
                return new HistoryTimelineBackupResult.Absent();
            }
            lease = HistoryTimelineDurableFiles.AcquireSharedExisting(
                paths
            );
            ActiveTimelineLocator locator =
                HistoryTimelineFactory.ReadLocator(paths);
            var ledger = CreateLedger(paths, locator, limits);
            string destination = Path.GetFullPath(backupDirectory);
            RequireExternalDestination(destination);
            string parent = Path.GetDirectoryName(destination)!;
            staging = Path.Combine(
                parent,
                $".{Path.GetFileName(destination)}."
                + $"{Guid.NewGuid():N}.tmp"
            );
            Directory.CreateDirectory(staging);
            string databasePath = Path.Combine(
                staging,
                BackupDatabaseFileName
            );
            ledger.BackupTo(databasePath);
            hooks.AfterBackupCopyBeforeVerify?.Invoke();
            FlushFile(databasePath);
            var backupLedger = new SqliteHistoryTimelineLedger(
                databasePath,
                locator.ActiveTimelineId,
                refId,
                limits
            );
            HistoryTimelineStoreReadResult<TimelineHeadRef> verified =
                backupLedger.VerifyFully();
            if (verified is HistoryTimelineStoreReadResult<
                    TimelineHeadRef>.Busy) {
                return new HistoryTimelineBackupResult.Busy();
            }
            if (verified is HistoryTimelineStoreReadResult<
                    TimelineHeadRef>.UnsupportedSchema schema) {
                return new HistoryTimelineBackupResult.Invalid(
                    "TimelineStoreUnsupportedSchema",
                    HistoryTimelineCoordinator.UnsupportedSchemaDetail(
                        schema.SchemaVersion
                    )
                );
            }
            if (verified is HistoryTimelineStoreReadResult<
                    TimelineHeadRef>.Invalid invalid) {
                return new HistoryTimelineBackupResult.Invalid(
                    invalid.Code,
                    invalid.Detail
                );
            }
            if (verified is not HistoryTimelineStoreReadResult<
                    TimelineHeadRef>.Found found) {
                return new HistoryTimelineBackupResult.Invalid(
                    "TimelineHeadUnavailable",
                    "The backup Timeline has no canonical head."
                );
            }
            long databaseBytes = new FileInfo(databasePath).Length;
            string databaseSha256 = ComputeFileSha256(databasePath);
            string headSha256 = HistoryTimelineHash.Compute(
                SqliteHistoryTimelineLedger.HeadHashDomain,
                found.Value.ToCanonicalBytes()
            );
            var manifest = new HistoryTimelineBackupManifest(
                locator,
                found.Value,
                headSha256,
                databaseSha256,
                databaseBytes
            );
            WriteExternalCreateNew(
                Path.Combine(staging, BackupManifestFileName),
                HistoryTimelineCanonicalCodec.Encode(manifest)
            );
            HistoryTimelineDurableFiles.FlushDirectory(staging);
            hooks.BeforeBackupPublish?.Invoke();
            Directory.Move(staging, destination);
            HistoryTimelineDurableFiles.FlushDirectory(parent);
            hooks.AfterBackupPublish?.Invoke();
            staging = null;
            return new HistoryTimelineBackupResult.Created(
                manifest,
                destination
            );
        }
        catch (HistoryTimelineLeaseBusyException) {
            return new HistoryTimelineBackupResult.Busy();
        }
        catch (HistoryTimelineStoreLimitException exception) {
            return new HistoryTimelineBackupResult.LimitExceeded(
                exception.Limit
            );
        }
        catch (Exception exception) when (IsMaintenanceFailure(exception)) {
            return new HistoryTimelineBackupResult.Invalid(
                MaintenanceErrorCode(exception),
                exception.Message
            );
        }
        finally {
            lease?.Dispose();
            if (staging is not null) {
                TryDeleteStaging(staging);
            }
        }
    }

    public static HistoryTimelineRestoreResult Restore(
        string repositoryPath,
        RefId refId,
        HistoryTimelineActiveConfirmation expected,
        string backupDirectory
    ) => RestoreCore(
        repositoryPath,
        refId,
        expected,
        backupDirectory,
        HistoryTimelineStorageLimits.Production,
        HistoryTimelinePersistenceTestHooks.None
    );

    public static HistoryTimelineAbandonResult Abandon(
        string repositoryPath,
        RefId refId,
        ActiveTimelineLocator expectedLocator,
        HistoryTimelineInitialPolicySpec initialPolicy,
        params IHistoryUnitLoadEstimator[] estimators
    ) => AbandonCore(
        repositoryPath,
        refId,
        expectedLocator,
        initialPolicy,
        HistoryTimelineStorageLimits.Production,
        HistoryTimelinePersistenceTestHooks.None,
        estimators
    );

    internal static HistoryTimelineReaderOpenResult OpenReaderCore(
        string repositoryPath,
        RefId refId,
        HistoryTimelineStorageLimits limits
    ) {
        FileStream? lease = null;
        try {
            string canonicalPath = CanonicalRepositoryPath(repositoryPath);
            var paths = new HistoryTimelinePaths(canonicalPath, refId);
            if (!HistoryTimelineDurableFiles.ExistsExact(
                    canonicalPath,
                    paths.LocatorPath)) {
                return new HistoryTimelineReaderOpenResult.Absent();
            }
            lease = HistoryTimelineDurableFiles.AcquireSharedExisting(
                paths
            );
            ActiveTimelineLocator locator =
                HistoryTimelineFactory.ReadLocator(paths);
            var ledger = new SqliteHistoryTimelineLedger(
                paths.TimelineDatabasePath(locator.ActiveTimelineId),
                locator.ActiveTimelineId,
                refId,
                limits,
                readOnly: true
            );
            HistoryTimelineStoreReadResult<TimelineHeadRef> head =
                ledger.VerifyAndReadHead();
            if (head is HistoryTimelineStoreReadResult<
                    TimelineHeadRef>.Busy) {
                return new HistoryTimelineReaderOpenResult.Busy();
            }
            if (head is HistoryTimelineStoreReadResult<
                    TimelineHeadRef>.Invalid invalid) {
                return new HistoryTimelineReaderOpenResult.Invalid(
                    invalid.Code,
                    invalid.Detail
                );
            }
            if (head is HistoryTimelineStoreReadResult<
                    TimelineHeadRef>.UnsupportedSchema unsupported) {
                return new HistoryTimelineReaderOpenResult
                    .UnsupportedSchema(unsupported.SchemaVersion);
            }
            if (head is not HistoryTimelineStoreReadResult<
                    TimelineHeadRef>.Found headFound) {
                return new HistoryTimelineReaderOpenResult.Invalid(
                    "TimelineHeadUnavailable",
                    "The active Timeline database has no canonical head."
                );
            }
            HistoryTimelineStoreReadResult<PartitionPolicyRevision>
                policy = ledger.ReadPolicy(
                    headFound.Value.ActivePartitionPolicyDigest
                );
            if (policy is HistoryTimelineStoreReadResult<
                    PartitionPolicyRevision>.Busy) {
                return new HistoryTimelineReaderOpenResult.Busy();
            }
            if (policy is HistoryTimelineStoreReadResult<
                    PartitionPolicyRevision>.Invalid policyInvalid) {
                return new HistoryTimelineReaderOpenResult.Invalid(
                    policyInvalid.Code,
                    policyInvalid.Detail
                );
            }
            if (policy is HistoryTimelineStoreReadResult<
                    PartitionPolicyRevision>.UnsupportedSchema
                    policySchema) {
                return new HistoryTimelineReaderOpenResult
                    .UnsupportedSchema(policySchema.SchemaVersion);
            }
            if (policy is not HistoryTimelineStoreReadResult<
                    PartitionPolicyRevision>.Found) {
                return new HistoryTimelineReaderOpenResult.Invalid(
                    "PartitionPolicyUnavailable",
                    headFound.Value.ActivePartitionPolicyDigest
                );
            }
            var lifetime = new HistoryTimelineLifetime(lease);
            var reader = new HistoryTimelineReader(
                canonicalPath,
                ledger,
                lifetime
            );
            var handle = new HistoryTimelineReaderHandle(
                locator,
                reader,
                lifetime
            );
            lease = null;
            return new HistoryTimelineReaderOpenResult.Opened(handle);
        }
        catch (HistoryTimelineLeaseBusyException) {
            return new HistoryTimelineReaderOpenResult.Busy();
        }
        catch (Exception exception) when (IsMaintenanceFailure(exception)) {
            return new HistoryTimelineReaderOpenResult.Invalid(
                MaintenanceErrorCode(exception),
                exception.Message
            );
        }
        finally {
            lease?.Dispose();
        }
    }

    internal static HistoryTimelineRestoreResult RestoreCore(
        string repositoryPath,
        RefId refId,
        HistoryTimelineActiveConfirmation expected,
        string backupDirectory,
        HistoryTimelineStorageLimits limits,
        HistoryTimelinePersistenceTestHooks? hooks = null
    ) {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupDirectory);
        FileStream? lease = null;
        string? temporaryDatabase = null;
        try {
            string canonicalPath = CanonicalRepositoryPath(repositoryPath);
            var paths = new HistoryTimelinePaths(canonicalPath, refId);
            lease = HistoryTimelineDurableFiles.AcquireExclusive(
                paths,
                create: false
            );
            ActiveTimelineLocator locator =
                HistoryTimelineFactory.ReadLocator(paths);
            var current = CreateLedger(paths, locator, limits);
            HistoryTimelineStoreReadResult<TimelineHeadRef> currentRead =
                current.ReadHeadForRestoreConfirmation();
            if (currentRead is HistoryTimelineStoreReadResult<
                    TimelineHeadRef>.Busy) {
                return new HistoryTimelineRestoreResult.Busy();
            }
            if (currentRead is HistoryTimelineStoreReadResult<
                    TimelineHeadRef>.UnsupportedSchema schema) {
                return new HistoryTimelineRestoreResult.Invalid(
                    "TimelineStoreUnsupportedSchema",
                    HistoryTimelineCoordinator.UnsupportedSchemaDetail(
                        schema.SchemaVersion
                    )
                );
            }
            if (currentRead is HistoryTimelineStoreReadResult<
                    TimelineHeadRef>.Invalid invalid) {
                return new HistoryTimelineRestoreResult.Invalid(
                    invalid.Code,
                    invalid.Detail
                );
            }
            if (currentRead is not HistoryTimelineStoreReadResult<
                    TimelineHeadRef>.Found currentHead) {
                return new HistoryTimelineRestoreResult.Invalid(
                    "TimelineHeadUnavailable",
                    "Restore requires a readable current canonical head."
                );
            }
            if (locator != expected.Locator
                || currentHead.Value != expected.Head) {
                return new HistoryTimelineRestoreResult
                    .ConfirmationMismatch(
                        locator,
                        currentHead.Value
                    );
            }

            string backupRoot = Path.GetFullPath(backupDirectory);
            RequireExistingExternalDirectory(backupRoot);
            string manifestPath = Path.Combine(
                backupRoot,
                BackupManifestFileName
            );
            string backupDatabase = Path.Combine(
                backupRoot,
                BackupDatabaseFileName
            );
            byte[] manifestBytes = ReadExternalBounded(
                manifestPath,
                HistoryTimelineStoreLimits
                    .MaximumBackupManifestUtf8Bytes
            );
            HistoryTimelineBackupManifest manifest =
                HistoryTimelineCanonicalCodec
                    .DecodeHistoryTimelineBackupManifest(manifestBytes);
            if (manifest.Locator != locator
                || manifest.Head != currentHead.Value) {
                return new HistoryTimelineRestoreResult
                    .ConfirmationMismatch(
                        locator,
                        currentHead.Value
                    );
            }
            string activeDatabase = paths.TimelineDatabasePath(
                locator.ActiveTimelineId
            );
            temporaryDatabase = Path.Combine(
                paths.TimelineRootPath,
                $".{locator.ActiveTimelineId.Value}."
                + $"{Guid.NewGuid():N}.restore.tmp"
            );
            CopyFileExact(backupDatabase, temporaryDatabase);
            hooks?.AfterRestoreCopyBeforeVerify?.Invoke();
            var privateCopyInfo = new FileInfo(temporaryDatabase);
            if (!privateCopyInfo.Exists
                || privateCopyInfo.Length != manifest.DatabaseBytes) {
                return new HistoryTimelineRestoreResult.Invalid(
                    "BackupDatabaseSizeInvalid",
                    "The private restore copy size differs from its manifest."
                );
            }
            if (!string.Equals(
                    ComputeFileSha256(temporaryDatabase),
                    manifest.DatabaseSha256,
                    StringComparison.Ordinal)) {
                return new HistoryTimelineRestoreResult.Invalid(
                    "BackupDatabaseDigestMismatch",
                    "The private restore copy differs from its manifest."
                );
            }
            var restored = new SqliteHistoryTimelineLedger(
                temporaryDatabase,
                locator.ActiveTimelineId,
                refId,
                limits
            );
            HistoryTimelineStoreReadResult<TimelineHeadRef> restoredRead =
                restored.VerifyFully();
            if (restoredRead is HistoryTimelineStoreReadResult<
                    TimelineHeadRef>.UnsupportedSchema restoredSchema) {
                return new HistoryTimelineRestoreResult.Invalid(
                    "TimelineStoreUnsupportedSchema",
                    HistoryTimelineCoordinator.UnsupportedSchemaDetail(
                        restoredSchema.SchemaVersion
                    )
                );
            }
            if (restoredRead is not HistoryTimelineStoreReadResult<
                    TimelineHeadRef>.Found restoredHead
                || restoredHead.Value != manifest.Head) {
                return new HistoryTimelineRestoreResult.Invalid(
                    "BackupTimelineInvalid",
                    "The backup database does not contain its manifested active head."
                );
            }
            hooks?.BeforeRestoreReplace?.Invoke();
            File.Move(
                temporaryDatabase,
                activeDatabase,
                overwrite: true
            );
            temporaryDatabase = null;
            HistoryTimelineDurableFiles.FlushDirectory(
                paths.TimelineRootPath
            );
            hooks?.AfterRestoreReplace?.Invoke();
            return new HistoryTimelineRestoreResult.Restored(
                locator,
                manifest.Head
            );
        }
        catch (HistoryTimelineLeaseBusyException) {
            return new HistoryTimelineRestoreResult.Busy();
        }
        catch (HistoryTimelineStoreLimitException exception) {
            return new HistoryTimelineRestoreResult.LimitExceeded(
                exception.Limit
            );
        }
        catch (Exception exception) when (IsMaintenanceFailure(exception)) {
            return new HistoryTimelineRestoreResult.Invalid(
                MaintenanceErrorCode(exception),
                exception.Message
            );
        }
        finally {
            lease?.Dispose();
            TryDeleteFile(temporaryDatabase);
        }
    }

    internal static HistoryTimelineAbandonResult AbandonCore(
        string repositoryPath,
        RefId refId,
        ActiveTimelineLocator expectedLocator,
        HistoryTimelineInitialPolicySpec initialPolicy,
        HistoryTimelineStorageLimits limits,
        HistoryTimelinePersistenceTestHooks? hooks,
        IHistoryUnitLoadEstimator[] estimators
    ) {
        ArgumentNullException.ThrowIfNull(expectedLocator);
        ArgumentNullException.ThrowIfNull(initialPolicy);
        ArgumentNullException.ThrowIfNull(estimators);
        FileStream? lease = null;
        try {
            string canonicalPath = CanonicalRepositoryPath(repositoryPath);
            var paths = new HistoryTimelinePaths(canonicalPath, refId);
            lease = HistoryTimelineDurableFiles.AcquireExclusive(
                paths,
                create: false
            );
            ActiveTimelineLocator actual =
                HistoryTimelineFactory.ReadLocator(paths);
            if (actual != expectedLocator) {
                return new HistoryTimelineAbandonResult
                    .ConfirmationMismatch(actual);
            }
            if (!HistoryPartitionAlgorithms.IsSupported(
                    initialPolicy.PartitionAlgorithmId)) {
                return new HistoryTimelineAbandonResult.Invalid(
                    "PartitionAlgorithmUnavailable",
                    initialPolicy.PartitionAlgorithmId
                );
            }
            var registry = new HistoryTimelineEstimatorRegistry(
                estimators
            );
            if (registry.Resolve(
                    initialPolicy.HistoryLoadEstimatorId) is null) {
                return new HistoryTimelineAbandonResult.Invalid(
                    "HistoryLoadEstimatorUnavailable",
                    initialPolicy.HistoryLoadEstimatorId
                );
            }
            TimelineId timelineId = GenerateUnusedTimelineId(paths);
            PartitionPolicyRevision policy =
                initialPolicy.CreatePolicy(timelineId);
            string databasePath = paths.TimelineDatabasePath(timelineId);
            SqliteHistoryTimelineLedger.CreateNew(
                databasePath,
                refId,
                policy,
                limits
            );
            FlushFile(databasePath);
            HistoryTimelineDurableFiles.FlushDirectory(
                paths.TimelineRootPath
            );
            long nextGeneration = checked(actual.Generation + 1);
            var nextLocator = new ActiveTimelineLocator(
                refId,
                timelineId,
                nextGeneration
            );
            HistoryTimelineDurableFiles.WriteAtomicReplace(
                canonicalPath,
                paths.LocatorPath,
                nextLocator.ToCanonicalBytes(),
                hooks?.BeforeLocatorAbandonPublish,
                hooks?.AfterLocatorAbandonPublish
            );
            var nextHead = new TimelineHeadRef(
                timelineId,
                refId,
                headRowId: null,
                policy.PolicyDigest,
                selectedRawHeadAtCommit: null,
                selectedPathCount: 0,
                HistorySelectedPathCommitment.EmptyDigest,
                generation: 0
            );
            return new HistoryTimelineAbandonResult.Abandoned(
                nextLocator,
                nextHead
            );
        }
        catch (HistoryTimelineLeaseBusyException) {
            return new HistoryTimelineAbandonResult.Busy();
        }
        catch (Exception exception) when (IsMaintenanceFailure(exception)) {
            return new HistoryTimelineAbandonResult.Invalid(
                MaintenanceErrorCode(exception),
                exception.Message
            );
        }
        finally {
            lease?.Dispose();
        }
    }

    private static SqliteHistoryTimelineLedger CreateLedger(
        HistoryTimelinePaths paths,
        ActiveTimelineLocator locator,
        HistoryTimelineStorageLimits? limits = null,
        bool readOnly = true
    ) => new(
        paths.TimelineDatabasePath(locator.ActiveTimelineId),
        locator.ActiveTimelineId,
        paths.RefId,
        limits ?? HistoryTimelineStorageLimits.Production,
        readOnly: readOnly
    );

    private static TimelineId GenerateUnusedTimelineId(
        HistoryTimelinePaths paths
    ) {
        for (int attempt = 0; attempt < 16; attempt++) {
            string value = Convert.ToHexString(
                RandomNumberGenerator.GetBytes(16)
            ).ToLowerInvariant();
            var id = new TimelineId(value);
            if (!File.Exists(paths.TimelineDatabasePath(id))) {
                return id;
            }
        }
        throw new IOException(
            "Failed to allocate a collision-free TimelineId."
        );
    }

    private static string ComputeFileSha256(string path) {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            FileOptions.SequentialScan
        );
        return Convert.ToHexString(SHA256.HashData(stream))
            .ToLowerInvariant();
    }

    private static void CopyFileExact(
        string source,
        string destination
    ) {
        using var input = new FileStream(
            source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            FileOptions.SequentialScan
        );
        if (input.Length < 1) {
            throw new InvalidDataException(
                "Restore source is empty."
            );
        }
        using var output = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 1024 * 1024,
            FileOptions.SequentialScan | FileOptions.WriteThrough
        );
        input.CopyTo(output, 1024 * 1024);
        output.Flush(flushToDisk: true);
    }

    private static byte[] ReadExternalBounded(
        string path,
        int maximumBytes
    ) {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.SequentialScan
        );
        if (stream.Length is < 1 || stream.Length > maximumBytes) {
            throw new InvalidDataException(
                "Backup manifest exceeds its code-owned byte bound."
            );
        }
        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static void WriteExternalCreateNew(
        string path,
        ReadOnlySpan<byte> bytes
    ) {
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.WriteThrough
        );
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static void FlushFile(string path) {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.Read,
            bufferSize: 1,
            FileOptions.None
        );
        stream.Flush(flushToDisk: true);
    }

    private static void RequireExternalDestination(string destination) {
        if (Directory.Exists(destination) || File.Exists(destination)) {
            throw new IOException(
                "Backup destination already exists."
            );
        }
        RequireExistingExternalDirectory(
            Path.GetDirectoryName(destination)!
        );
    }

    private static void RequireExistingExternalDirectory(string path) {
        string full = Path.GetFullPath(path);
        if (!Directory.Exists(full)) {
            throw new DirectoryNotFoundException(full);
        }
        string? cursor = full;
        while (cursor is not null) {
            if (Directory.Exists(cursor)
                && (File.GetAttributes(cursor)
                    & FileAttributes.ReparsePoint) != 0) {
                throw new InvalidDataException(
                    $"Backup path contains a reparse point: {cursor}"
                );
            }
            cursor = Path.GetDirectoryName(cursor);
        }
    }

    private static void TryDeleteStaging(string path) {
        try {
            string database = Path.Combine(
                path,
                BackupDatabaseFileName
            );
            string manifest = Path.Combine(
                path,
                BackupManifestFileName
            );
            TryDeleteFile(database);
            TryDeleteFile(manifest);
            if (Directory.Exists(path)
                && !Directory.EnumerateFileSystemEntries(path).Any()) {
                Directory.Delete(path);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteFile(string? path) {
        if (path is null) {
            return;
        }
        try {
            if (File.Exists(path)) {
                File.Delete(path);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string CanonicalRepositoryPath(string path)
        => Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(path)
        );

    private static bool IsMaintenanceFailure(Exception exception)
        => exception is ArgumentException
            or InvalidDataException
            or IOException
            or NotSupportedException
            or OverflowException
            or PlatformNotSupportedException
            or SqliteException
            or UnauthorizedAccessException;

    private static string MaintenanceErrorCode(Exception exception)
        => exception switch {
            InvalidDataException => "TimelineStoreInvalid",
            FileNotFoundException => "TimelineStoreSlotMissing",
            IOException => "TimelineStoreIoInvalid",
            SqliteException sqlite =>
                $"TimelineStoreSqlite{sqlite.SqliteErrorCode}",
            UnauthorizedAccessException => "TimelineStoreUnauthorized",
            PlatformNotSupportedException =>
                "TimelineStorePlatformUnsupported",
            OverflowException => "TimelineGenerationExhausted",
            _ => "TimelineMaintenanceInvalid"
        };
}
