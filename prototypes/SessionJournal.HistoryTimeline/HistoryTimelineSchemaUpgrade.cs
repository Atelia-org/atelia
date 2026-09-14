using Atelia.EventJournal;

namespace Atelia.SessionJournal.HistoryTimeline;

public abstract record HistoryTimelineUpgradeResult {
    private HistoryTimelineUpgradeResult() { }
    public sealed record Upgraded(TimelineHeadRef Head) : HistoryTimelineUpgradeResult;
    public sealed record AlreadyCurrent(TimelineHeadRef Head) : HistoryTimelineUpgradeResult;
    public sealed record Absent : HistoryTimelineUpgradeResult;
    public sealed record Busy : HistoryTimelineUpgradeResult;
    public sealed record UnsupportedSchema(int SchemaVersion) : HistoryTimelineUpgradeResult;
    public sealed record LimitExceeded(string Limit) : HistoryTimelineUpgradeResult;
    public sealed record Invalid(string Code, string Detail) : HistoryTimelineUpgradeResult;
    public sealed record PublishIndeterminate(TimelineHeadRef Head, int? ObservedSchemaVersion)
        : HistoryTimelineUpgradeResult;
}

public static partial class HistoryTimelineMaintenance {
    /// <summary>
    /// Upgrades one explicitly named schema-2 database in a stopped repository copy.
    /// The active locator is neither consulted nor changed; retained Timelines are supported.
    /// </summary>
    public static HistoryTimelineUpgradeResult UpgradeSchemaV2(
        string repositoryPath, RefId refId, TimelineId timelineId
    ) => UpgradeSchemaV2Core(repositoryPath, refId, timelineId,
        HistoryTimelineStorageLimits.Production, HistoryTimelinePersistenceTestHooks.None);

    internal static HistoryTimelineUpgradeResult UpgradeSchemaV2Core(
        string repositoryPath, RefId refId, TimelineId timelineId,
        HistoryTimelineStorageLimits limits, HistoryTimelinePersistenceTestHooks? hooks = null
    ) {
        hooks ??= HistoryTimelinePersistenceTestHooks.None;
        string? temporary = null;
        string? database = null;
        bool published = false;
        TimelineHeadRef? intended = null;
        FileStream? lease = null;
        try {
            HistoryTimelineSyntax.RequireTimelineId(timelineId);
            var paths = new HistoryTimelinePaths(CanonicalRepositoryPath(repositoryPath), refId);
            database = paths.TimelineDatabasePath(timelineId);
            HistoryTimelineDurableFiles.RequireSafePath(paths.RepositoryPath, database);
            if (!File.Exists(database)) { return new HistoryTimelineUpgradeResult.Absent(); }
            lease = HistoryTimelineDurableFiles.AcquireExclusive(paths, create: false);
            int version = SqliteHistoryTimelineLedger.ReadUpgradeSourceVersion(database, limits);
            if (version == SqliteHistoryTimelineLedger.SchemaVersion) {
                return new HistoryTimelineUpgradeResult.AlreadyCurrent(
                    RequireUpgradeVerified(database, timelineId, refId, limits));
            }
            if (version != 2) { return new HistoryTimelineUpgradeResult.UnsupportedSchema(version); }
            temporary = Path.Combine(paths.TimelineRootPath, $".{timelineId.Value}.{Guid.NewGuid():N}.upgrade.tmp");
            intended = SqliteHistoryTimelineLedger.ConvertSchemaV2Copy(database, temporary, refId, timelineId, limits);
            TimelineHeadRef verified = RequireUpgradeVerified(temporary, timelineId, refId, limits);
            if (verified != intended) { throw new InvalidDataException("The upgraded Timeline head changed."); }
            hooks.AfterUpgradeSourceValidated?.Invoke();
            FlushFile(temporary);
            hooks.BeforeUpgradeReplace?.Invoke();
            File.Move(temporary, database, overwrite: true);
            temporary = null;
            published = true;
            HistoryTimelineDurableFiles.FlushDirectory(paths.TimelineRootPath);
            hooks.AfterUpgradeReplace?.Invoke();
            TimelineHeadRef reopened = RequireUpgradeVerified(database, timelineId, refId, limits);
            if (reopened != intended) { throw new InvalidDataException("The published Timeline head changed."); }
            return new HistoryTimelineUpgradeResult.Upgraded(reopened);
        }
        catch (Exception exception) when (published && intended is not null && IsMaintenanceFailure(exception)) {
            int? observed = null;
            try { observed = SqliteHistoryTimelineLedger.ReadUpgradeSourceVersion(database!, limits); }
            catch (Exception observedFailure) when (IsMaintenanceFailure(observedFailure)) { }
            return new HistoryTimelineUpgradeResult.PublishIndeterminate(intended, observed);
        }
        catch (HistoryTimelineLeaseBusyException) { return new HistoryTimelineUpgradeResult.Busy(); }
        catch (HistoryTimelineStoreLimitException exception) { return new HistoryTimelineUpgradeResult.LimitExceeded(exception.Limit); }
        catch (HistoryTimelineUnsupportedSchemaException exception) { return new HistoryTimelineUpgradeResult.UnsupportedSchema(exception.SchemaVersion); }
        catch (Microsoft.Data.Sqlite.SqliteException exception) when (exception.SqliteErrorCode is 5 or 6) {
            return new HistoryTimelineUpgradeResult.Busy();
        }
        catch (Exception exception) when (IsMaintenanceFailure(exception)) {
            return new HistoryTimelineUpgradeResult.Invalid(MaintenanceErrorCode(exception), exception.Message);
        }
        finally {
            lease?.Dispose();
            if (temporary is not null) {
                foreach (string suffix in new[] { "", "-journal", "-wal", "-shm" }) { TryDeleteFile(temporary + suffix); }
            }
        }
    }

    private static TimelineHeadRef RequireUpgradeVerified(string path, TimelineId timelineId, RefId refId,
        HistoryTimelineStorageLimits limits) {
        var ledger = new SqliteHistoryTimelineLedger(path, timelineId, refId, limits, readOnly: true);
        return ledger.VerifyFully() switch {
            HistoryTimelineStoreReadResult<TimelineHeadRef>.Found found => found.Value,
            HistoryTimelineStoreReadResult<TimelineHeadRef>.Busy => throw new HistoryTimelineLeaseBusyException(),
            HistoryTimelineStoreReadResult<TimelineHeadRef>.UnsupportedSchema schema => throw new HistoryTimelineUnsupportedSchemaException(schema.SchemaVersion),
            HistoryTimelineStoreReadResult<TimelineHeadRef>.Invalid invalid => throw new InvalidDataException(invalid.Detail),
            _ => throw new InvalidDataException("The upgraded Timeline is unavailable.")
        };
    }
}
