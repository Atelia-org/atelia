using Microsoft.Data.Sqlite;

namespace Atelia.Galatea.Server;

internal sealed partial class GalateaDelegationSqliteStore {
    private static void CreateSchema(SqliteConnection connection) {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE delegation_meta (
                singleton INTEGER NOT NULL PRIMARY KEY CHECK(singleton = 1),
                schema_version INTEGER NOT NULL CHECK(schema_version = 1),
                user_id TEXT NOT NULL,
                session_repository_id TEXT NOT NULL,
                capture_frontier_segment_number INTEGER NOT NULL
                    CHECK(capture_frontier_segment_number
                        BETWEEN 1 AND 4294967295),
                capture_frontier_tail_offset INTEGER NOT NULL
                    CHECK(capture_frontier_tail_offset >= 4
                        AND capture_frontier_tail_offset % 4 = 0),
                baseline_selected_head TEXT NULL,
                route_policy_fingerprint TEXT NOT NULL,
                maximum_queued_mails INTEGER NOT NULL
                    CHECK(maximum_queued_mails >= 1),
                maximum_task_utf8_bytes INTEGER NOT NULL
                    CHECK(maximum_task_utf8_bytes >= 1),
                maximum_reply_utf8_bytes INTEGER NOT NULL
                    CHECK(maximum_reply_utf8_bytes >= 1),
                maximum_inbox_replies INTEGER NOT NULL
                    CHECK(maximum_inbox_replies >= 1),
                maximum_inbox_utf8_bytes INTEGER NOT NULL
                    CHECK(maximum_inbox_utf8_bytes
                        >= maximum_reply_utf8_bytes),
                next_completion_sequence INTEGER NOT NULL
                    CHECK(next_completion_sequence >= 1),
                revision INTEGER NOT NULL CHECK(revision >= 0)
            ) STRICT;

            CREATE TABLE action_capture (
                source_action_address TEXT NOT NULL PRIMARY KEY,
                capture_sequence INTEGER NOT NULL
                    CHECK(capture_sequence >= 1),
                visible_action_sha256 TEXT NOT NULL
                    CHECK(length(visible_action_sha256) = 64),
                visible_action_utf8_bytes INTEGER NOT NULL
                    CHECK(visible_action_utf8_bytes >= 0),
                extractor_contract_id TEXT NOT NULL,
                artifact_count INTEGER NOT NULL
                    CHECK(artifact_count BETWEEN 0 AND 64),
                revision INTEGER NOT NULL CHECK(revision >= 0)
            ) STRICT;

            CREATE UNIQUE INDEX ux_action_capture_sequence
            ON action_capture(capture_sequence);

            CREATE TABLE outbound_mail (
                dispatch_id TEXT NOT NULL PRIMARY KEY,
                source_action_address TEXT NOT NULL
                    REFERENCES action_capture(source_action_address)
                    ON DELETE RESTRICT,
                artifact_ordinal INTEGER NOT NULL CHECK(artifact_ordinal >= 0),
                recipient TEXT NOT NULL,
                subject TEXT NULL,
                body TEXT NULL,
                in_reply_to_message_id TEXT NULL,
                evidence_quote TEXT NULL,
                route_class TEXT NOT NULL
                    CHECK(route_class IN ('Codex', 'Unrouted')),
                frozen_route_policy_fingerprint TEXT NULL,
                state TEXT NOT NULL CHECK(state IN (
                    'Unrouted', 'Queued', 'Started',
                    'OutcomeUnknown', 'Accepted', 'TerminalCompleted',
                    'TerminalFailed', 'Quarantined'
                )),
                operation_id TEXT NULL,
                requested_thread_id TEXT NULL,
                accepted_thread_id TEXT NULL,
                accepted_turn_id TEXT NULL,
                terminal_final_sha256 TEXT NULL,
                terminal_stage TEXT NULL,
                terminal_code TEXT NULL,
                reconcile_attempt_count INTEGER NOT NULL DEFAULT 0
                    CHECK(reconcile_attempt_count >= 0),
                reconcile_last_code TEXT NULL,
                next_reconcile_at_ms INTEGER NULL
                    CHECK(next_reconcile_at_ms IS NULL
                        OR next_reconcile_at_ms >= 0),
                revision INTEGER NOT NULL CHECK(revision >= 0)
            ) STRICT;

            CREATE UNIQUE INDEX ux_outbound_source_ordinal
            ON outbound_mail(source_action_address, artifact_ordinal);

            CREATE TABLE route_binding (
                singleton INTEGER NOT NULL PRIMARY KEY CHECK(singleton = 1),
                state TEXT NOT NULL CHECK(state IN (
                    'Unbound', 'Binding', 'Bound',
                    'Quarantined'
                )),
                binding_operation_id TEXT NULL,
                thread_id TEXT NULL,
                policy_fingerprint TEXT NOT NULL,
                active_dispatch_id TEXT NULL
                    REFERENCES outbound_mail(dispatch_id) ON DELETE RESTRICT,
                quarantine_code TEXT NULL,
                ensure_attempt_count INTEGER NOT NULL DEFAULT 0
                    CHECK(ensure_attempt_count >= 0),
                ensure_last_code TEXT NULL,
                next_ensure_at_ms INTEGER NULL
                    CHECK(next_ensure_at_ms IS NULL
                        OR next_ensure_at_ms >= 0),
                revision INTEGER NOT NULL CHECK(revision >= 0)
            ) STRICT;

            CREATE TABLE reply_notice (
                notice_id TEXT NOT NULL PRIMARY KEY,
                dispatch_id TEXT NOT NULL UNIQUE
                    REFERENCES outbound_mail(dispatch_id) ON DELETE RESTRICT,
                kind TEXT NOT NULL CHECK(kind IN ('Reply', 'DeliveryFailure')),
                body TEXT NOT NULL,
                stage TEXT NULL,
                code TEXT NULL,
                completion_sequence INTEGER NOT NULL
                    CHECK(completion_sequence >= 1),
                state TEXT NOT NULL
                    CHECK(state IN ('Ready', 'Leased', 'Consumed')),
                consumed_action_address TEXT NULL,
                revision INTEGER NOT NULL CHECK(revision >= 0)
            ) STRICT;

            CREATE UNIQUE INDEX ux_reply_notice_completion
            ON reply_notice(completion_sequence);

            CREATE TABLE reply_lease (
                lease_id TEXT NOT NULL PRIMARY KEY,
                state TEXT NOT NULL CHECK(state IN (
                    'CutoffFrozen', 'ObservationBound',
                    'ObservationCommitted', 'Quarantined'
                )),
                active_slot INTEGER NULL CHECK(active_slot = 1),
                player_text TEXT NOT NULL,
                expected_session_head TEXT NULL,
                rendered_observation TEXT NULL,
                observation_utf8_bytes INTEGER NULL
                    CHECK(observation_utf8_bytes IS NULL
                        OR observation_utf8_bytes >= 0),
                observation_sha256 TEXT NULL
                    CHECK(observation_sha256 IS NULL
                        OR length(observation_sha256) = 64),
                completion_frontier INTEGER NOT NULL
                    CHECK(completion_frontier >= 0),
                observation_address TEXT NULL,
                revision INTEGER NOT NULL CHECK(revision >= 0)
            ) STRICT;

            CREATE UNIQUE INDEX ux_reply_lease_one_active
            ON reply_lease(active_slot) WHERE active_slot IS NOT NULL;

            CREATE TABLE reply_lease_item (
                lease_id TEXT NOT NULL
                    REFERENCES reply_lease(lease_id) ON DELETE RESTRICT,
                ordinal INTEGER NOT NULL CHECK(ordinal >= 0),
                notice_id TEXT NOT NULL
                    REFERENCES reply_notice(notice_id) ON DELETE RESTRICT,
                PRIMARY KEY(lease_id, ordinal)
            ) STRICT;

            CREATE UNIQUE INDEX ux_reply_lease_item_notice
            ON reply_lease_item(notice_id);
            """;
        command.ExecuteNonQuery();
    }

    private static void InsertInitialState(
        SqliteConnection connection,
        GalateaDelegationStoreOwner owner,
        GalateaDelegationStoreBaseline baseline,
        GalateaDelegationStoreLimits limits
    ) {
        using SqliteTransaction transaction =
            connection.BeginTransaction(deferred: false);
        using (SqliteCommand meta = connection.CreateCommand()) {
            meta.Transaction = transaction;
            meta.CommandText = """
                INSERT INTO delegation_meta(
                    singleton, schema_version, user_id,
                    session_repository_id,
                    capture_frontier_segment_number,
                    capture_frontier_tail_offset,
                    baseline_selected_head, route_policy_fingerprint,
                    maximum_queued_mails, maximum_task_utf8_bytes,
                    maximum_reply_utf8_bytes, maximum_inbox_replies,
                    maximum_inbox_utf8_bytes, next_completion_sequence,
                    revision
                ) VALUES (
                    1, $schema, $user, $repository,
                    $frontierSegment, $frontierTail,
                    $baseline, $policy, $maximumQueued, $maximumTaskBytes,
                    $maximumReplyBytes, $maximumInboxReplies,
                    $maximumInboxBytes, 1, 0
                );
                """;
            meta.Parameters.AddWithValue("$schema", SchemaVersion);
            meta.Parameters.AddWithValue("$user", owner.UserId);
            meta.Parameters.AddWithValue(
                "$repository",
                owner.SessionRepositoryId
            );
            meta.Parameters.AddWithValue(
                "$frontierSegment",
                baseline.CaptureFromPhysicalFrontier.SegmentNumber
            );
            meta.Parameters.AddWithValue(
                "$frontierTail",
                baseline.CaptureFromPhysicalFrontier.TailOffset
            );
            meta.Parameters.AddWithValue(
                "$baseline",
                (object?)baseline.SelectedHead ?? DBNull.Value
            );
            meta.Parameters.AddWithValue(
                "$policy",
                owner.RoutePolicyFingerprint
            );
            meta.Parameters.AddWithValue(
                "$maximumQueued",
                limits.MaximumQueuedMails
            );
            meta.Parameters.AddWithValue(
                "$maximumTaskBytes",
                limits.MaximumTaskUtf8Bytes
            );
            meta.Parameters.AddWithValue(
                "$maximumReplyBytes",
                limits.MaximumReplyUtf8Bytes
            );
            meta.Parameters.AddWithValue(
                "$maximumInboxReplies",
                limits.MaximumInboxReplies
            );
            meta.Parameters.AddWithValue(
                "$maximumInboxBytes",
                limits.MaximumInboxUtf8Bytes
            );
            meta.ExecuteNonQuery();
        }
        using (SqliteCommand route = connection.CreateCommand()) {
            route.Transaction = transaction;
            route.CommandText = """
                INSERT INTO route_binding(
                    singleton, state, binding_operation_id, thread_id,
                    policy_fingerprint, active_dispatch_id,
                    quarantine_code, ensure_attempt_count, ensure_last_code,
                    next_ensure_at_ms, revision
                ) VALUES (
                    1, 'Unbound', NULL, NULL, $policy, NULL, NULL,
                    0, NULL, NULL, 0
                );
                """;
            route.Parameters.AddWithValue(
                "$policy",
                owner.RoutePolicyFingerprint
            );
            route.ExecuteNonQuery();
        }
        transaction.Commit();
    }
}
