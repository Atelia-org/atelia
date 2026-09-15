using Microsoft.Data.Sqlite;

namespace Atelia.Galatea.Server;

internal sealed record GalateaDelegationStoreUpgradeResult(string Outcome, string? BackupPath);

internal sealed partial class GalateaDelegationSqliteStore {
    /// <summary>Explicit offline upgrade. Ordinary opens accept only V5.</summary>
    internal static GalateaDelegationStoreUpgradeResult UpgradeExisting(
        string storeDirectory, GalateaDelegationStoreOwner owner,
        GalateaDelegationStoreLimits limits, bool apply,
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
        GalateaDelegationStateSnapshot expected;
        using (SqliteConnection source = OpenConnection(databasePath, create: false, readOnly: !apply)) {
            ConfigureOpenedDatabase(source, readOnly: !apply);
            using SqliteCommand versionCommand = source.CreateCommand();
            versionCommand.CommandText = "PRAGMA user_version;";
            int version = Convert.ToInt32(versionCommand.ExecuteScalar());
            if (version == SchemaVersion) {
                _ = ValidateOpenedDatabase(source, owner, limits);
                return new("AlreadyCurrent", null);
            }
            if (version is not (1 or 2 or 3 or 4)) {
                throw new InvalidDataException($"Delegation schema version {version} cannot be upgraded.");
            }
            if (version is 3 or 4) {
                ValidateV3UpgradeSource(source, owner, limits, version);
            } else {
                ValidateLegacyUpgradeSource(source, owner, limits, version);
            }
            string sql = version switch {
                1 => UpgradeV1ColumnsSql + UpgradeRecoveryColumnsSql
                    + UpgradeMetaToV3Sql + UpgradeV3ToV4Sql,
                2 => UpgradeRecoveryColumnsSql + UpgradeMetaToV3Sql
                    + UpgradeV3ToV4Sql,
                3 => UpgradeV3ToV4Sql,
                4 => string.Empty,
                _ => throw new InvalidOperationException("Unexpected upgrade version.")
            };
            sql += UpgradeV4ToV5Sql;
            // The only legacy projection lives in this offline upgrader. A
            // disposable copy lets dry-run validate the exact future V5 state
            // without runtime dual-format readers or modifying the source.
            using (var projected = new SqliteConnection("Data Source=:memory:")) {
                projected.Open();
                source.BackupDatabase(projected);
                using SqliteCommand projection = projected.CreateCommand();
                projection.CommandText = sql;
                _ = projection.ExecuteNonQuery();
                expected = ValidateOpenedDatabase(projected, owner, limits);
            }
            if (!apply) { return new("DryRunReady", null); }
            backupPath = CreateUpgradeBackup(source, databasePath, owner, limits, version);
            string operation = $"upgrade-delegation-v{version}-to-v5";
            using SqliteTransaction transaction = source.BeginTransaction(deferred: false);
            using SqliteCommand upgrade = source.CreateCommand();
            upgrade.Transaction = transaction;
            upgrade.CommandText = sql;
            _ = upgrade.ExecuteNonQuery();
            RequireSameUpgradeState(expected, ReadSnapshotCore(source, transaction));
            hooks?.BeforeCommit?.Invoke(operation);
            transaction.Commit();
            hooks?.AfterCommitBeforeReturn?.Invoke(operation);
        }
        using (SqliteConnection reopened = OpenConnection(databasePath, create: false, readOnly: true)) {
            ConfigureOpenedDatabase(reopened, readOnly: true);
            RequireSameUpgradeState(expected, ValidateOpenedDatabase(reopened, owner, limits));
        }
        return new("Upgraded", backupPath);
    }

    private static void ValidateLegacyUpgradeSource(
        SqliteConnection source, GalateaDelegationStoreOwner owner,
        GalateaDelegationStoreLimits limits, int version
    ) {
        ValidateSchemaIdentity(source, version);
        RequireOwner(source, transaction: null, owner, limits, version);
        using (SqliteCommand integrity = source.CreateCommand()) {
            integrity.CommandText = "PRAGMA integrity_check;";
            if (!string.Equals(integrity.ExecuteScalar() as string, "ok", StringComparison.Ordinal)) {
                throw new InvalidDataException("Legacy delegation integrity check failed.");
            }
        }
        using (SqliteCommand keys = source.CreateCommand()) {
            keys.CommandText = "PRAGMA foreign_key_check;";
            using SqliteDataReader reader = keys.ExecuteReader();
            if (reader.Read()) { throw new InvalidDataException("Legacy delegation foreign key check failed."); }
        }
        using (SqliteCommand route = source.CreateCommand()) {
            route.CommandText = "SELECT state, ensure_attempt_count, ensure_last_code, next_ensure_at_ms FROM route_binding;";
            using SqliteDataReader reader = route.ExecuteReader();
            if (!reader.Read()) { throw Corrupt("Legacy route singleton is missing."); }
            string state = reader.GetString(0);
            int count = reader.GetInt32(1);
            string? code = ReadNullableString(reader, 2);
            long? next = reader.IsDBNull(3) ? null : reader.GetInt64(3);
            bool valid = count == 0 ? code is null && next is null
                : state == "Binding" && count > 0 && code is not null && next is >= 0;
            if (!valid) { throw Corrupt("Legacy binding recovery shape is invalid."); }
            if (code is not null) {
                try { RequireFailureToken(code, nameof(code)); }
                catch (ArgumentException exception) { throw new InvalidDataException("Legacy recovery code is invalid.", exception); }
            }
            if (reader.Read()) { throw Corrupt("Legacy route has multiple rows."); }
        }
        // V3 deliberately broadens these shapes. Validate the old restrictions
        // before projection so a rename cannot launder an invalid legacy row.
        using SqliteCommand shape = source.CreateCommand();
        shape.CommandText = """
            SELECT COUNT(*) FROM outbound_mail WHERE
                (state IN ('Queued', 'Started') AND
                    (reconcile_attempt_count != 0 OR reconcile_last_code IS NOT NULL OR next_reconcile_at_ms IS NOT NULL))
                OR (state = 'TerminalFailed' AND NOT COALESCE((
                    (operation_id IS NOT NULL AND requested_thread_id IS NOT NULL
                        AND accepted_thread_id = requested_thread_id AND accepted_turn_id IS NOT NULL
                        AND terminal_stage IS NOT NULL AND terminal_code IS NOT NULL)
                    OR (operation_id IS NULL AND requested_thread_id IS NULL
                        AND accepted_thread_id IS NULL AND accepted_turn_id IS NULL
                        AND terminal_stage = 'preflight' AND terminal_code = 'TASK_INVALID_OR_TOO_LARGE')
                ), 0))
                OR terminal_stage = 'local-recovery';
            """;
        if (Convert.ToInt64(shape.ExecuteScalar()) != 0) { throw Corrupt("Legacy mail shape is invalid."); }
        shape.CommandText = """
            SELECT COUNT(*) FROM route_binding WHERE state = 'Binding'
                AND NOT EXISTS(SELECT 1 FROM outbound_mail WHERE route_class = 'Codex' AND state = 'Queued');
            """;
        if (Convert.ToInt64(shape.ExecuteScalar()) != 0) { throw Corrupt("Legacy binding has no FIFO mail owner."); }
    }

    private static void ValidateV3UpgradeSource(
        SqliteConnection source,
        GalateaDelegationStoreOwner owner,
        GalateaDelegationStoreLimits limits,
        int version = 3
    ) {
        ValidateSchemaIdentity(source, expectedVersion: version);
        RequireOwner(source, transaction: null, owner, limits,
            expectedVersion: version);
        using (SqliteCommand integrity = source.CreateCommand()) {
            integrity.CommandText = "PRAGMA integrity_check;";
            if (!string.Equals(integrity.ExecuteScalar() as string, "ok",
                    StringComparison.Ordinal)) {
                throw new InvalidDataException("V3 delegation integrity check failed.");
            }
        }
        using SqliteCommand keys = source.CreateCommand();
        keys.CommandText = "PRAGMA foreign_key_check;";
        using SqliteDataReader reader = keys.ExecuteReader();
        if (reader.Read()) {
            throw new InvalidDataException("V3 delegation foreign key check failed.");
        }
    }

    private static string CreateUpgradeBackup(
        SqliteConnection source, string databasePath,
        GalateaDelegationStoreOwner owner, GalateaDelegationStoreLimits limits, int version
    ) {
        string backupPath = databasePath + $".v{version}-backup-"
            + DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ", System.Globalization.CultureInfo.InvariantCulture)
            + "-" + Guid.NewGuid().ToString("N") + ".sqlite3";
        using (new FileStream(backupPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        using (SqliteConnection backup = OpenConnection(backupPath, create: false)) {
            source.BackupDatabase(backup);
            ConfigureOpenedDatabase(backup, readOnly: true);
            if (version is 3 or 4) {
                ValidateV3UpgradeSource(backup, owner, limits, version);
            } else {
                ValidateLegacyUpgradeSource(backup, owner, limits, version);
            }
        }
        using (var file = new FileStream(backupPath, FileMode.Open, FileAccess.Write, FileShare.None)) {
            file.Flush(flushToDisk: true);
        }
        GalateaDelegationDurableFiles.FlushDirectory(Path.GetDirectoryName(databasePath)!);
        return backupPath;
    }

    private static void RequireSameUpgradeState(GalateaDelegationStateSnapshot before, GalateaDelegationStateSnapshot after) {
        GalateaReplyLeaseSnapshot? left = before.ActiveLease;
        GalateaReplyLeaseSnapshot? right = after.ActiveLease;
        bool sameLease = left is null ? right is null
            : right is not null && (left with { NoticeIds = right.NoticeIds }) == right
                && left.NoticeIds.SequenceEqual(right.NoticeIds);
        if (before.Owner != after.Owner || before.Baseline != after.Baseline
            || before.Limits != after.Limits || before.StoreRevision != after.StoreRevision
            || before.NextCompletionSequence != after.NextCompletionSequence
            || before.Route != after.Route || !before.Captures.SequenceEqual(after.Captures)
            || !before.Mails.SequenceEqual(after.Mails)
            || !before.InternalMailOutboxes.SequenceEqual(after.InternalMailOutboxes)
            || !before.Notices.SequenceEqual(after.Notices) || !sameLease) {
            throw new InvalidDataException("Delegation upgrade changed expected business state.");
        }
    }

    private const string UpgradeV1ColumnsSql = """
        ALTER TABLE outbound_mail DROP COLUMN frozen_route_policy_fingerprint;
        ALTER TABLE route_binding DROP COLUMN policy_fingerprint;
        """;

    private const string UpgradeRecoveryColumnsSql = """
        ALTER TABLE outbound_mail RENAME COLUMN reconcile_attempt_count TO recovery_failure_count;
        ALTER TABLE outbound_mail RENAME COLUMN reconcile_last_code TO recovery_last_code;
        ALTER TABLE outbound_mail RENAME COLUMN next_reconcile_at_ms TO next_retry_at_ms;
        UPDATE outbound_mail
        SET recovery_failure_count = (SELECT ensure_attempt_count FROM route_binding WHERE singleton = 1),
            recovery_last_code = (SELECT ensure_last_code FROM route_binding WHERE singleton = 1),
            next_retry_at_ms = (SELECT next_ensure_at_ms FROM route_binding WHERE singleton = 1)
        WHERE dispatch_id = (
            SELECT mail.dispatch_id FROM outbound_mail AS mail
            JOIN action_capture AS capture ON capture.source_action_address = mail.source_action_address
            WHERE mail.route_class = 'Codex' AND mail.state = 'Queued'
            ORDER BY capture.capture_sequence, mail.artifact_ordinal LIMIT 1
        ) AND EXISTS(SELECT 1 FROM route_binding WHERE state = 'Binding');
        ALTER TABLE route_binding DROP COLUMN ensure_attempt_count;
        ALTER TABLE route_binding DROP COLUMN ensure_last_code;
        ALTER TABLE route_binding DROP COLUMN next_ensure_at_ms;
        """;

    private const string UpgradeMetaToV3Sql = """
        ALTER TABLE delegation_meta RENAME TO delegation_meta_old;
        CREATE TABLE delegation_meta (
            singleton INTEGER NOT NULL PRIMARY KEY CHECK(singleton = 1),
            schema_version INTEGER NOT NULL CHECK(schema_version = 3),
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
        ) SELECT singleton, 3, user_id, session_repository_id,
            capture_frontier_segment_number, capture_frontier_tail_offset,
            baseline_selected_head, maximum_queued_mails, maximum_task_utf8_bytes,
            maximum_reply_utf8_bytes, maximum_inbox_replies, maximum_inbox_utf8_bytes,
            next_completion_sequence, revision
        FROM delegation_meta_old;
        DROP TABLE delegation_meta_old;
        PRAGMA user_version = 3;
        """;

    private const string UpgradeV3ToV4Sql = """
        ALTER TABLE delegation_meta RENAME TO delegation_meta_old;
        CREATE TABLE delegation_meta (
            singleton INTEGER NOT NULL PRIMARY KEY CHECK(singleton = 1),
            schema_version INTEGER NOT NULL CHECK(schema_version = 4),
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
        ) SELECT singleton, 4, user_id, session_repository_id,
            capture_frontier_segment_number, capture_frontier_tail_offset,
            baseline_selected_head, maximum_queued_mails, maximum_task_utf8_bytes,
            maximum_reply_utf8_bytes, maximum_inbox_replies, maximum_inbox_utf8_bytes,
            next_completion_sequence, revision
        FROM delegation_meta_old;
        DROP TABLE delegation_meta_old;

        CREATE TABLE internal_mail_outbox (
            dispatch_id TEXT NOT NULL PRIMARY KEY
                REFERENCES outbound_mail(dispatch_id) ON DELETE RESTRICT,
            target_user_id TEXT NOT NULL,
            target_session_repository_id TEXT NOT NULL,
            from_character_name TEXT NOT NULL,
            message_id TEXT NOT NULL CHECK(
                length(message_id) = 32
                AND message_id NOT GLOB '*[^0-9a-f]*'
            ),
            state TEXT NOT NULL CHECK(state IN (
                'Pending', 'ObservationBound', 'Delivered', 'Quarantined'
            )),
            expected_session_head TEXT NULL,
            rendered_observation TEXT NULL,
            observation_address TEXT NULL,
            quarantine_code TEXT NULL,
            revision INTEGER NOT NULL CHECK(revision >= 0)
        ) STRICT;
        CREATE UNIQUE INDEX ux_internal_mail_message_id
        ON internal_mail_outbox(message_id);
        CREATE INDEX ix_internal_mail_target_state
        ON internal_mail_outbox(target_user_id, state);
        PRAGMA user_version = 4;
        """;
    private const string UpgradeV4ToV5Sql = """
        ALTER TABLE delegation_meta RENAME TO delegation_meta_old;
        CREATE TABLE delegation_meta (
            singleton INTEGER NOT NULL PRIMARY KEY CHECK(singleton = 1),
            schema_version INTEGER NOT NULL CHECK(schema_version = 5),
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
        ) SELECT singleton, 5, user_id, session_repository_id,
            capture_frontier_segment_number, capture_frontier_tail_offset,
            baseline_selected_head, maximum_queued_mails, maximum_task_utf8_bytes,
            maximum_reply_utf8_bytes, maximum_inbox_replies, maximum_inbox_utf8_bytes,
            next_completion_sequence, revision
        FROM delegation_meta_old;
        DROP TABLE delegation_meta_old;

        ALTER TABLE outbound_mail ADD COLUMN content_format TEXT NOT NULL DEFAULT 'legacy-task' CHECK(content_format IN ('legacy-task', 'semantic-mail-v1'));
        ALTER TABLE outbound_mail ADD COLUMN sender_name TEXT NULL;
        ALTER TABLE outbound_mail ADD COLUMN task_sha256 TEXT NULL;
        ALTER TABLE outbound_mail ADD COLUMN task_utf8_bytes INTEGER NULL CHECK(task_utf8_bytes IS NULL OR task_utf8_bytes > 0);
        ALTER TABLE internal_mail_outbox ADD COLUMN bound_input TEXT NULL;
        ALTER TABLE reply_lease ADD COLUMN bound_input TEXT NULL;
        ALTER TABLE reply_notice ADD COLUMN notice_format TEXT NOT NULL DEFAULT 'legacy-text' CHECK(notice_format IN ('legacy-text', 'semantic-notice-v1'));
        ALTER TABLE reply_notice ADD COLUMN sender_kind TEXT NULL;
        ALTER TABLE reply_notice ADD COLUMN sender_id TEXT NULL;
        ALTER TABLE reply_notice ADD COLUMN sender_name TEXT NULL;
        ALTER TABLE reply_notice ADD COLUMN detail TEXT NULL;
        ALTER TABLE reply_notice ADD COLUMN thread_id TEXT NULL;
        ALTER TABLE reply_notice ADD COLUMN turn_id TEXT NULL;
        PRAGMA user_version = 5;
        """;
}
