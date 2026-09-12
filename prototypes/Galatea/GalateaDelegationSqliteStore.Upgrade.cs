using Microsoft.Data.Sqlite;

namespace Atelia.Galatea.Server;

internal sealed record GalateaDelegationStoreUpgradeResult(
    string Outcome,
    string? BackupPath
);

internal sealed partial class GalateaDelegationSqliteStore {
    /// <summary>
    /// Explicit offline format upgrade. Ordinary opens never migrate a store.
    /// The original lifetime lock covers validation, backup, migration and reopen.
    /// </summary>
    internal static GalateaDelegationStoreUpgradeResult UpgradeExisting(
        string storeDirectory,
        GalateaDelegationStoreOwner owner,
        GalateaDelegationStoreLimits limits,
        bool apply,
        GalateaDelegationStoreTestHooks? hooks = null
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeDirectory);
        GalateaDelegationDurableFiles.RequireLinux();
        ValidateOwner(owner);
        ValidateLimits(limits);
        string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(storeDirectory));
        RequireExistingAncestorsNoReparse(fullPath);
        RejectReparsePoint(fullPath, "delegation store directory");
        RejectReparsePoint(Path.Combine(fullPath, LockFileName), "delegation lifetime lock");
        using FileStream lifetimeLock = AcquireLifetimeLock(fullPath, FileMode.Open);
        string databasePath = Path.Combine(fullPath, DatabaseFileName);
        RejectReparsePoint(databasePath, "delegation database");
        string? backupPath;
        GalateaDelegationStateSnapshot before;
        const string operation = "upgrade-delegation-v1-to-v2";
        using (SqliteConnection connection = OpenConnection(databasePath, create: false, readOnly: !apply)) {
            ConfigureOpenedDatabase(connection, readOnly: !apply);
            using SqliteCommand versionCommand = connection.CreateCommand();
            versionCommand.CommandText = "PRAGMA user_version;";
            int version = Convert.ToInt32(versionCommand.ExecuteScalar());
            if (version == SchemaVersion) {
                _ = ValidateOpenedDatabase(connection, owner, limits);
                return new("AlreadyCurrent", null);
            }
            if (version != 1) {
                throw new InvalidDataException($"Delegation schema version {version} cannot be upgraded.");
            }
            // V1 has the same business columns. The current projection ignores
            // the three retired policy columns and validates the actual state.
            before = ValidateOpenedDatabase(connection, owner, limits, expectedVersion: 1);
            if (!apply) { return new("DryRunReady", null); }
            backupPath = CreateUpgradeBackup(connection, databasePath, owner, limits);
            using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
            using SqliteCommand upgrade = connection.CreateCommand();
            upgrade.Transaction = transaction;
            upgrade.CommandText = UpgradeV1ToV2Sql;
            _ = upgrade.ExecuteNonQuery();
            RequireSameUpgradeState(before, ReadSnapshotCore(connection, transaction));
            hooks?.BeforeCommit?.Invoke(operation);
            transaction.Commit();
            hooks?.AfterCommitBeforeReturn?.Invoke(operation);
        }
        // A caller interrupted after commit can rerun the command: V2 is
        // complete and is reported as AlreadyCurrent, without another backup.
        using (SqliteConnection reopened = OpenConnection(databasePath, create: false, readOnly: true)) {
            ConfigureOpenedDatabase(reopened, readOnly: true);
            RequireSameUpgradeState(before, ValidateOpenedDatabase(reopened, owner, limits));
        }
        return new("Upgraded", backupPath);
    }

    private static string CreateUpgradeBackup(
        SqliteConnection source,
        string databasePath,
        GalateaDelegationStoreOwner owner,
        GalateaDelegationStoreLimits limits
    ) {
        string backupPath = databasePath + ".v1-backup-"
            + DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ", System.Globalization.CultureInfo.InvariantCulture)
            + "-" + Guid.NewGuid().ToString("N") + ".sqlite3";
        using (new FileStream(backupPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        using (SqliteConnection backup = OpenConnection(backupPath, create: false)) {
            source.BackupDatabase(backup);
            ConfigureOpenedDatabase(backup, readOnly: true);
            _ = ValidateOpenedDatabase(backup, owner, limits, expectedVersion: 1);
        }
        using (var file = new FileStream(backupPath, FileMode.Open, FileAccess.Write, FileShare.None)) {
            file.Flush(flushToDisk: true);
        }
        GalateaDelegationDurableFiles.FlushDirectory(Path.GetDirectoryName(databasePath)!);
        return backupPath;
    }

    private static void RequireSameUpgradeState(
        GalateaDelegationStateSnapshot before,
        GalateaDelegationStateSnapshot after
    ) {
        GalateaReplyLeaseSnapshot? left = before.ActiveLease;
        GalateaReplyLeaseSnapshot? right = after.ActiveLease;
        bool sameLease = left is null ? right is null
            : right is not null
                && (left with { NoticeIds = right.NoticeIds }) == right
                && left.NoticeIds.SequenceEqual(right.NoticeIds);
        if (before.Owner != after.Owner || before.Baseline != after.Baseline
            || before.Limits != after.Limits || before.StoreRevision != after.StoreRevision
            || before.NextCompletionSequence != after.NextCompletionSequence
            || before.Route != after.Route || !before.Captures.SequenceEqual(after.Captures)
            || !before.Mails.SequenceEqual(after.Mails) || !before.Notices.SequenceEqual(after.Notices)
            || !sameLease) {
            throw new InvalidDataException("Delegation upgrade changed existing business state.");
        }
    }

    // Neither removed column participates in an index or a foreign key.
    // Only meta needs rebuilding because V1 CHECKs schema_version = 1.
    private const string UpgradeV1ToV2Sql = """
        ALTER TABLE outbound_mail DROP COLUMN frozen_route_policy_fingerprint;
        ALTER TABLE route_binding DROP COLUMN policy_fingerprint;
        ALTER TABLE delegation_meta RENAME TO delegation_meta_v1;
        CREATE TABLE delegation_meta (
            singleton INTEGER NOT NULL PRIMARY KEY CHECK(singleton = 1),
            schema_version INTEGER NOT NULL CHECK(schema_version = 2),
            user_id TEXT NOT NULL,
            session_repository_id TEXT NOT NULL,
            capture_frontier_segment_number INTEGER NOT NULL
                CHECK(capture_frontier_segment_number BETWEEN 1 AND 4294967295),
            capture_frontier_tail_offset INTEGER NOT NULL
                CHECK(capture_frontier_tail_offset >= 4 AND capture_frontier_tail_offset % 4 = 0),
            baseline_selected_head TEXT NULL,
            maximum_queued_mails INTEGER NOT NULL CHECK(maximum_queued_mails >= 1),
            maximum_task_utf8_bytes INTEGER NOT NULL CHECK(maximum_task_utf8_bytes >= 1),
            maximum_reply_utf8_bytes INTEGER NOT NULL CHECK(maximum_reply_utf8_bytes >= 1),
            maximum_inbox_replies INTEGER NOT NULL CHECK(maximum_inbox_replies >= 1),
            maximum_inbox_utf8_bytes INTEGER NOT NULL
                CHECK(maximum_inbox_utf8_bytes >= maximum_reply_utf8_bytes),
            next_completion_sequence INTEGER NOT NULL CHECK(next_completion_sequence >= 1),
            revision INTEGER NOT NULL CHECK(revision >= 0)
        ) STRICT;
        INSERT INTO delegation_meta (
            singleton, schema_version, user_id, session_repository_id,
            capture_frontier_segment_number, capture_frontier_tail_offset,
            baseline_selected_head, maximum_queued_mails, maximum_task_utf8_bytes,
            maximum_reply_utf8_bytes, maximum_inbox_replies, maximum_inbox_utf8_bytes,
            next_completion_sequence, revision
        ) SELECT singleton, 2, user_id, session_repository_id,
            capture_frontier_segment_number, capture_frontier_tail_offset,
            baseline_selected_head, maximum_queued_mails, maximum_task_utf8_bytes,
            maximum_reply_utf8_bytes, maximum_inbox_replies, maximum_inbox_utf8_bytes,
            next_completion_sequence, revision
        FROM delegation_meta_v1;
        DROP TABLE delegation_meta_v1;
        PRAGMA user_version = 2;
        """;
}
