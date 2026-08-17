using System.Security.Cryptography;
using Atelia.EventJournal;
using Microsoft.Data.Sqlite;
using SJ = Atelia.SessionJournal;

namespace Atelia.SessionJournal.HistoryTimeline;

public static class HistoryTimelineFactory {
    public static HistoryTimelineBuildReadSessionOpenResult
        OpenBuildReadSession(
            SJ.SessionJournalReadView selectedRef,
            params IHistoryUnitLoadEstimator[] estimators
        ) {
        ArgumentNullException.ThrowIfNull(selectedRef);
        HistoryTimelineOpenResult opened = Open(selectedRef, estimators);
        return opened switch {
            HistoryTimelineOpenResult.Opened success
                => new HistoryTimelineBuildReadSessionOpenResult.Opened(
                    new HistoryTimelineBuildReadSession(
                        success.Handle,
                        selectedRef
                    )
                ),
            HistoryTimelineOpenResult.Absent
                => new HistoryTimelineBuildReadSessionOpenResult.Absent(),
            HistoryTimelineOpenResult.Busy
                => new HistoryTimelineBuildReadSessionOpenResult.Busy(),
            HistoryTimelineOpenResult.UnsupportedSchema unsupported
                => new HistoryTimelineBuildReadSessionOpenResult
                    .UnsupportedSchema(unsupported.SchemaVersion),
            HistoryTimelineOpenResult.Invalid invalid
                => new HistoryTimelineBuildReadSessionOpenResult.Invalid(
                    invalid.Code,
                    invalid.Detail
                ),
            _ => new HistoryTimelineBuildReadSessionOpenResult.Invalid(
                "TimelineOpenOutcomeInvalid",
                "The Timeline factory returned an unknown open outcome."
            )
        };
    }

    public static HistoryTimelineCreateResult Create(
        SJ.SessionJournalReadView selectedRef,
        HistoryTimelineInitialPolicySpec initialPolicy,
        params IHistoryUnitLoadEstimator[] estimators
    ) => CreateCore(
        selectedRef,
        initialPolicy,
        HistoryTimelineStorageLimits.Production,
        HistoryTimelinePersistenceTestHooks.None,
        estimators
    );

    public static HistoryTimelineOpenResult Open(
        SJ.SessionJournalReadView selectedRef,
        params IHistoryUnitLoadEstimator[] estimators
    ) => OpenCore(
        selectedRef,
        HistoryTimelineStorageLimits.Production,
        HistoryTimelinePersistenceTestHooks.None,
        estimators
    );

    internal static HistoryTimelineCreateResult CreateForTest(
        SJ.SessionJournalReadView selectedRef,
        HistoryTimelineInitialPolicySpec initialPolicy,
        HistoryTimelineStorageLimits limits,
        params IHistoryUnitLoadEstimator[] estimators
    ) => CreateCore(
        selectedRef,
        initialPolicy,
        limits,
        HistoryTimelinePersistenceTestHooks.None,
        estimators
    );

    internal static HistoryTimelineCreateResult CreateForTest(
        SJ.SessionJournalReadView selectedRef,
        HistoryTimelineInitialPolicySpec initialPolicy,
        HistoryTimelineStorageLimits limits,
        HistoryTimelinePersistenceTestHooks hooks,
        params IHistoryUnitLoadEstimator[] estimators
    ) => CreateCore(
        selectedRef,
        initialPolicy,
        limits,
        hooks,
        estimators
    );

    internal static HistoryTimelineOpenResult OpenForTest(
        SJ.SessionJournalReadView selectedRef,
        HistoryTimelineStorageLimits limits,
        params IHistoryUnitLoadEstimator[] estimators
    ) => OpenCore(
        selectedRef,
        limits,
        HistoryTimelinePersistenceTestHooks.None,
        estimators
    );

    internal static HistoryTimelineOpenResult OpenForTest(
        SJ.SessionJournalReadView selectedRef,
        HistoryTimelineStorageLimits limits,
        HistoryTimelinePersistenceTestHooks hooks,
        params IHistoryUnitLoadEstimator[] estimators
    ) => OpenCore(selectedRef, limits, hooks, estimators);

    private static HistoryTimelineCreateResult CreateCore(
        SJ.SessionJournalReadView selectedRef,
        HistoryTimelineInitialPolicySpec initialPolicy,
        HistoryTimelineStorageLimits limits,
        HistoryTimelinePersistenceTestHooks hooks,
        IHistoryUnitLoadEstimator[] estimators
    ) {
        ArgumentNullException.ThrowIfNull(selectedRef);
        ArgumentNullException.ThrowIfNull(initialPolicy);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(estimators);
        try {
            string repositoryPath = CanonicalRepositoryPath(
                selectedRef.Path
            );
            RefId refId = selectedRef.BranchRefId;
            _ = selectedRef.ReadCurrentHead();
            var paths = new HistoryTimelinePaths(
                repositoryPath,
                refId
            );
            using FileStream lease =
                HistoryTimelineDurableFiles.AcquireExclusive(
                    paths,
                    create: true
                );
            if (HistoryTimelineDurableFiles.ExistsExact(
                    repositoryPath,
                    paths.LocatorPath)) {
                ActiveTimelineLocator existing = ReadLocator(paths);
                if (existing.RefId != refId) {
                    return new HistoryTimelineCreateResult.Invalid(
                        "LocatorRefMismatch",
                        "The canonical locator belongs to another Ref."
                    );
                }
                return ValidateExistingForCreate(
                    paths,
                    existing,
                    limits,
                    hooks
                );
            }
            if (!HistoryPartitionAlgorithms.IsSupported(
                    initialPolicy.PartitionAlgorithmId)) {
                return new HistoryTimelineCreateResult.Invalid(
                    "PartitionAlgorithmUnavailable",
                    initialPolicy.PartitionAlgorithmId
                );
            }
            var registry = new HistoryTimelineEstimatorRegistry(
                estimators
            );
            if (registry.Resolve(
                    initialPolicy.HistoryLoadEstimatorId) is null) {
                return new HistoryTimelineCreateResult.Invalid(
                    "HistoryLoadEstimatorUnavailable",
                    initialPolicy.HistoryLoadEstimatorId
                );
            }
            HistoryTimelineDurableFiles.EnsureDirectoryDurable(
                repositoryPath,
                paths.TimelineRootPath
            );
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
            var locator = new ActiveTimelineLocator(
                refId,
                timelineId,
                generation: 0
            );
            HistoryTimelineDurableFiles.WriteCreateNew(
                repositoryPath,
                paths.LocatorPath,
                locator.ToCanonicalBytes(),
                hooks.BeforeLocatorCreatePublish,
                hooks.AfterLocatorCreatePublish
            );
            var head = new TimelineHeadRef(
                timelineId,
                refId,
                headRowId: null,
                policy.PolicyDigest,
                selectedRawHeadAtCommit: null,
                selectedPathCount: 0,
                HistorySelectedPathCommitment.EmptyDigest,
                generation: 0
            );
            return new HistoryTimelineCreateResult.Created(
                locator,
                head
            );
        }
        catch (HistoryTimelineLeaseBusyException) {
            return new HistoryTimelineCreateResult.Busy();
        }
        catch (HistoryTimelineStoreLimitException exception) {
            return new HistoryTimelineCreateResult.LimitExceeded(
                exception.Limit
            );
        }
        catch (SqliteException exception)
            when (exception.SqliteErrorCode is 13 or 18) {
            return new HistoryTimelineCreateResult.LimitExceeded(
                "SqliteFull"
            );
        }
        catch (Exception exception) when (IsFactoryFailure(exception)) {
            return new HistoryTimelineCreateResult.Invalid(
                FactoryErrorCode(exception),
                exception.Message
            );
        }
    }

    private static HistoryTimelineCreateResult ValidateExistingForCreate(
        HistoryTimelinePaths paths,
        ActiveTimelineLocator locator,
        HistoryTimelineStorageLimits limits,
        HistoryTimelinePersistenceTestHooks hooks
    ) {
        string databasePath = paths.TimelineDatabasePath(
            locator.ActiveTimelineId
        );
        HistoryTimelineDurableFiles.RequireSafePath(
            paths.RepositoryPath,
            databasePath
        );
        var ledger = new SqliteHistoryTimelineLedger(
            databasePath,
            locator.ActiveTimelineId,
            paths.RefId,
            limits,
            hooks,
            readOnly: true
        );
        HistoryTimelineStoreReadResult<TimelineHeadRef> head =
            ledger.VerifyAndReadHead();
        if (head is HistoryTimelineStoreReadResult<TimelineHeadRef>.Busy) {
            return new HistoryTimelineCreateResult.Busy();
        }
        if (head is HistoryTimelineStoreReadResult<
                TimelineHeadRef>.UnsupportedSchema headUnsupported) {
            return new HistoryTimelineCreateResult.Invalid(
                "TimelineStoreUnsupportedSchema",
                HistoryTimelineCoordinator.UnsupportedSchemaDetail(
                    headUnsupported.SchemaVersion
                )
            );
        }
        if (head is HistoryTimelineStoreReadResult<
                TimelineHeadRef>.Invalid headInvalid) {
            return new HistoryTimelineCreateResult.Invalid(
                headInvalid.Code,
                headInvalid.Detail
            );
        }
        if (head is not HistoryTimelineStoreReadResult<
                TimelineHeadRef>.Found found) {
            return new HistoryTimelineCreateResult.Invalid(
                "TimelineHeadUnavailable",
                "The active Timeline database has no canonical head."
            );
        }

        HistoryTimelineStoreReadResult<PartitionPolicyRevision> policy =
            ledger.ReadPolicy(found.Value.ActivePartitionPolicyDigest);
        if (policy is HistoryTimelineStoreReadResult<
                PartitionPolicyRevision>.Busy) {
            return new HistoryTimelineCreateResult.Busy();
        }
        if (policy is HistoryTimelineStoreReadResult<
                PartitionPolicyRevision>.UnsupportedSchema policyUnsupported) {
            return new HistoryTimelineCreateResult.Invalid(
                "TimelineStoreUnsupportedSchema",
                HistoryTimelineCoordinator.UnsupportedSchemaDetail(
                    policyUnsupported.SchemaVersion
                )
            );
        }
        if (policy is HistoryTimelineStoreReadResult<
                PartitionPolicyRevision>.Invalid policyInvalid) {
            return new HistoryTimelineCreateResult.Invalid(
                policyInvalid.Code,
                policyInvalid.Detail
            );
        }
        if (policy is not HistoryTimelineStoreReadResult<
                PartitionPolicyRevision>.Found) {
            return new HistoryTimelineCreateResult.Invalid(
                "PartitionPolicyUnavailable",
                found.Value.ActivePartitionPolicyDigest
            );
        }
        return new HistoryTimelineCreateResult.AlreadyExists(locator);
    }

    private static HistoryTimelineOpenResult OpenCore(
        SJ.SessionJournalReadView selectedRef,
        HistoryTimelineStorageLimits limits,
        HistoryTimelinePersistenceTestHooks hooks,
        IHistoryUnitLoadEstimator[] estimators
    ) {
        ArgumentNullException.ThrowIfNull(selectedRef);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(estimators);
        FileStream? lease = null;
        try {
            string repositoryPath = CanonicalRepositoryPath(
                selectedRef.Path
            );
            RefId refId = selectedRef.BranchRefId;
            _ = selectedRef.ReadCurrentHead();
            var paths = new HistoryTimelinePaths(
                repositoryPath,
                refId
            );
            if (!HistoryTimelineDurableFiles.ExistsExact(
                    repositoryPath,
                    paths.LocatorPath)) {
                return new HistoryTimelineOpenResult.Absent();
            }
            lease = HistoryTimelineDurableFiles.AcquireSharedExisting(
                paths
            );
            ActiveTimelineLocator locator = ReadLocator(paths);
            if (locator.RefId != refId) {
                return new HistoryTimelineOpenResult.Invalid(
                    "LocatorRefMismatch",
                    "The canonical locator belongs to another Ref."
                );
            }
            string databasePath = paths.TimelineDatabasePath(
                locator.ActiveTimelineId
            );
            HistoryTimelineDurableFiles.RequireSafePath(
                repositoryPath,
                databasePath
            );
            var ledger = new SqliteHistoryTimelineLedger(
                databasePath,
                locator.ActiveTimelineId,
                refId,
                limits,
                hooks
            );
            HistoryTimelineStoreReadResult<TimelineHeadRef> head =
                ledger.VerifyAndReadHead();
            if (head is HistoryTimelineStoreReadResult<
                    TimelineHeadRef>.Busy) {
                return new HistoryTimelineOpenResult.Busy();
            }
            if (head is HistoryTimelineStoreReadResult<
                    TimelineHeadRef>.Invalid invalid) {
                return new HistoryTimelineOpenResult.Invalid(
                    invalid.Code,
                    invalid.Detail
                );
            }
            if (head is HistoryTimelineStoreReadResult<
                    TimelineHeadRef>.UnsupportedSchema unsupported) {
                return new HistoryTimelineOpenResult.UnsupportedSchema(
                    unsupported.SchemaVersion
                );
            }
            if (head is not HistoryTimelineStoreReadResult<
                    TimelineHeadRef>.Found) {
                return new HistoryTimelineOpenResult.Invalid(
                    "TimelineHeadUnavailable",
                    "The active Timeline database has no canonical head."
                );
            }
            var registry = new HistoryTimelineEstimatorRegistry(
                estimators
            );
            TimelineHeadRef verifiedHead = ((HistoryTimelineStoreReadResult<
                TimelineHeadRef>.Found)head).Value;
            HistoryTimelineStoreReadResult<PartitionPolicyRevision>
                policyRead = ledger.ReadPolicy(
                    verifiedHead.ActivePartitionPolicyDigest
                );
            if (policyRead is HistoryTimelineStoreReadResult<
                    PartitionPolicyRevision>.Busy) {
                return new HistoryTimelineOpenResult.Busy();
            }
            if (policyRead is HistoryTimelineStoreReadResult<
                    PartitionPolicyRevision>.Invalid policyInvalid) {
                return new HistoryTimelineOpenResult.Invalid(
                    policyInvalid.Code,
                    policyInvalid.Detail
                );
            }
            if (policyRead is not HistoryTimelineStoreReadResult<
                    PartitionPolicyRevision>.Found policyFound) {
                return new HistoryTimelineOpenResult.Invalid(
                    "PartitionPolicyUnavailable",
                    verifiedHead.ActivePartitionPolicyDigest
                );
            }
            if (!HistoryPartitionAlgorithms.IsSupported(
                    policyFound.Value.PartitionAlgorithmId)) {
                return new HistoryTimelineOpenResult.Invalid(
                    "PartitionAlgorithmUnavailable",
                    policyFound.Value.PartitionAlgorithmId
                );
            }
            if (registry.Resolve(
                    policyFound.Value.HistoryLoadEstimatorId) is null) {
                return new HistoryTimelineOpenResult.Invalid(
                    "HistoryLoadEstimatorUnavailable",
                    policyFound.Value.HistoryLoadEstimatorId
                );
            }
            var lifetime = new HistoryTimelineLifetime(
                lease,
                hooks.AfterLifetimeClosing
            );
            var coordinator = new HistoryTimelineCoordinator(
                repositoryPath,
                ledger,
                new HistoryTimelineCoordinatorTestHooks(
                    OnlineRawCaptureLimit:
                        hooks.OnlineRawCaptureLimit),
                lifetime,
                estimators
            );
            var reader = new HistoryTimelineReader(
                repositoryPath,
                ledger,
                lifetime
            );
            var handle = new HistoryTimelineHandle(
                locator,
                coordinator,
                reader,
                lifetime
            );
            lease = null;
            return new HistoryTimelineOpenResult.Opened(handle);
        }
        catch (HistoryTimelineLeaseBusyException) {
            return new HistoryTimelineOpenResult.Busy();
        }
        catch (Exception exception) when (IsFactoryFailure(exception)) {
            return new HistoryTimelineOpenResult.Invalid(
                FactoryErrorCode(exception),
                exception.Message
            );
        }
        finally {
            lease?.Dispose();
        }
    }

    internal static ActiveTimelineLocator ReadLocator(
        HistoryTimelinePaths paths
    ) {
        byte[] bytes = HistoryTimelineDurableFiles.ReadBounded(
            paths.RepositoryPath,
            paths.LocatorPath,
            HistoryTimelineStoreLimits.MaximumLocatorUtf8Bytes
        );
        return HistoryTimelineCanonicalCodec
            .DecodeActiveTimelineLocator(bytes);
    }

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

    private static string CanonicalRepositoryPath(string path)
        => Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(path)
        );

    private static bool IsFactoryFailure(Exception exception)
        => exception is ArgumentException
            or InvalidDataException
            or IOException
            or NotSupportedException
            or PlatformNotSupportedException
            or SqliteException
            or UnauthorizedAccessException;

    private static string FactoryErrorCode(Exception exception)
        => exception switch {
            InvalidDataException => "TimelineStoreInvalid",
            FileNotFoundException => "TimelineStoreSlotMissing",
            IOException => "TimelineStoreIoInvalid",
            SqliteException sqlite =>
                $"TimelineStoreSqlite{sqlite.SqliteErrorCode}",
            UnauthorizedAccessException => "TimelineStoreUnauthorized",
            PlatformNotSupportedException =>
                "TimelineStorePlatformUnsupported",
            _ => "TimelineFactoryInvalid"
        };
}
