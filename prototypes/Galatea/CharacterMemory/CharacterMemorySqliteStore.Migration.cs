using Atelia.EventJournal;
using Microsoft.Data.Sqlite;

namespace Atelia.Galatea.Server.CharacterMemory;

internal sealed partial class CharacterMemorySqliteStore {
    private const int PreviousSchemaVersion = 1;
    private const string V1MetaSchemaSha256 =
        "8220d2dca086a69fcdec65a7b1d015f7b726acbd7bf05cf176f6615daf986ac4";
    private const string V1NoteSchemaSha256 =
        "36327b2553cb267238fbe991928358419859109fa7ebf0774cb5bc76775471d9";

    private sealed record V1Status(
        CharacterMemoryStoreBaseline Baseline,
        CharacterMemoryStoreState StoreState,
        string ProvisionTargetPodStateIdentity,
        string? SettledDefaultPodStateIdentity,
        string? ActiveSourceAction,
        string? QuarantineCode,
        string? QuarantineObservedPodStateIdentity,
        long StoreRevision
    );

    private static void MigrateV1ToV2IfNeeded(
        SqliteConnection connection,
        CharacterMemoryStoreOwner owner,
        CharacterMemoryStoreTestHooks hooks
    ) {
        long version = ReadPragmaInteger(connection, "user_version");
        if (version == SchemaVersion) { return; }
        if (version != PreviousSchemaVersion) {
            throw Corrupt(
                $"Character Memory schema version '{version}' is unsupported."
            );
        }

        V1Status v1 = ValidateV1Database(connection, owner);
        const string operation = "migrate-character-memory-v1-to-v2";
        Exception? uncertain = null;
        using (SqliteTransaction transaction =
               connection.BeginTransaction(deferred: false)) {
            RebuildV1AsV2(connection, transaction);
            hooks.BeforeCommit?.Invoke(operation);
            try {
                transaction.Commit();
                hooks.AfterCommitBeforeReturn?.Invoke(operation);
            }
            catch (Exception exception) when (
                GalateaExceptionClassifier.IsNonFatal(exception)) {
                uncertain = exception;
            }
        }

        try {
            CharacterMemoryStatusSnapshot v2 = ValidateOpenedDatabase(
                connection,
                owner
            );
            if (v2.Baseline != v1.Baseline
                || v2.StoreState != v1.StoreState
                || !string.Equals(
                    v2.ProvisionTargetPodStateIdentity,
                    v1.ProvisionTargetPodStateIdentity,
                    StringComparison.Ordinal
                )
                || !string.Equals(
                    v2.SettledDefaultPodStateIdentity,
                    v1.SettledDefaultPodStateIdentity,
                    StringComparison.Ordinal
                )
                || !string.Equals(
                    v2.ActiveSourceAction,
                    v1.ActiveSourceAction,
                    StringComparison.Ordinal
                )
                || v2.ActiveDerivedInfoSourceAction is not null
                || !string.Equals(
                    v2.QuarantineCode,
                    v1.QuarantineCode,
                    StringComparison.Ordinal
                )
                || !string.Equals(
                    v2.QuarantineObservedPodStateIdentity,
                    v1.QuarantineObservedPodStateIdentity,
                    StringComparison.Ordinal
                )
                || v2.StoreRevision != v1.StoreRevision) {
                throw Corrupt(
                    "Character Memory V1 migration changed existing authority."
                );
            }
        }
        catch (Exception validationException) when (
            uncertain is not null
            && GalateaExceptionClassifier.IsNonFatal(validationException)) {
            throw new CharacterMemoryStoreCommitOutcomeException(
                operation,
                new AggregateException(uncertain, validationException)
            );
        }
        if (uncertain is not null) {
            return;
        }
    }

    private static V1Status ValidateV1Database(
        SqliteConnection connection,
        CharacterMemoryStoreOwner owner
    ) {
        ValidateV1SchemaIdentity(connection);
        using (SqliteCommand integrity = connection.CreateCommand()) {
            integrity.CommandText = "PRAGMA integrity_check;";
            if (!string.Equals(
                integrity.ExecuteScalar() as string,
                "ok",
                StringComparison.Ordinal
            )) {
                throw Corrupt("Character Memory V1 SQLite integrity_check failed.");
            }
        }
        using (SqliteCommand foreignKeys = connection.CreateCommand()) {
            foreignKeys.CommandText = "PRAGMA foreign_key_check;";
            using SqliteDataReader reader = foreignKeys.ExecuteReader();
            if (reader.Read()) {
                throw Corrupt("Character Memory V1 SQLite foreign_key_check failed.");
            }
        }

        V1Status status = ReadV1Status(connection, owner);
        var sources = new List<string>();
        using (SqliteCommand command = connection.CreateCommand()) {
            command.CommandText = """
                SELECT source_action_address
                FROM note_action_capture
                ORDER BY source_action_address;
                """;
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read()) { sources.Add(reader.GetString(0)); }
        }
        foreach (string source in sources) {
            CharacterMemoryCaptureSnapshot capture = ReadCaptureCore(
                connection,
                transaction: null,
                source
            )
                ?? throw Corrupt("Character Memory V1 capture disappeared during validation.");
            if (capture.StateRevision > status.StoreRevision) {
                throw Corrupt(
                    "Character Memory V1 capture revision exceeds store revision."
                );
            }
        }
        ValidateV1GlobalCountInvariants(connection, status);
        return status;
    }

    private static void ValidateV1SchemaIdentity(SqliteConnection connection) {
        RequirePragmaInteger(connection, "application_id", ApplicationId);
        RequirePragmaInteger(connection, "user_version", PreviousSchemaVersion);
        var actual = new HashSet<string>(StringComparer.Ordinal);
        using (SqliteCommand command = connection.CreateCommand()) {
            command.CommandText = """
                SELECT type || ':' || name FROM sqlite_schema
                WHERE name NOT LIKE 'sqlite_%'
                  AND type IN ('table', 'index', 'trigger', 'view');
                """;
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read()) { actual.Add(reader.GetString(0)); }
        }
        string[] expected = [
            "table:character_memory_meta",
            "table:note_action_capture",
            "table:character_note",
            "index:ux_character_note_memo_id",
            "index:ux_note_capture_single_active",
        ];
        if (!actual.SetEquals(expected)) {
            throw Corrupt("Character Memory V1 schema object set is not exact.");
        }
        RequireExactColumns(connection, "character_memory_meta", [
            "singleton", "schema_version", "user_id",
            "session_repository_id", "capture_frontier_segment_number",
            "capture_frontier_tail_offset", "baseline_selected_head",
            "store_state", "provision_target_pod_state_identity",
            "settled_default_pod_state_identity", "active_source_action",
            "quarantine_code", "quarantine_observed_pod_state_identity",
            "store_revision",
        ]);
        RequireExactColumns(connection, "note_action_capture", [
            "source_action_address", "visible_action_sha256",
            "visible_action_utf8_bytes", "extractor_contract_id",
            "extraction_commitment", "artifact_count", "state",
            "base_pod_state_identity", "target_pod_state_identity",
            "rejection_code", "state_revision",
        ]);
        RequireExactColumns(connection, "character_note", [
            "source_action_address", "artifact_ordinal", "exact_text", "memo_id",
        ]);
        foreach (string table in new[] {
            "character_memory_meta", "note_action_capture", "character_note",
        }) {
            RequireStrictTable(connection, table);
        }
        RequireExactTableSchema(
            connection,
            "character_memory_meta",
            V1MetaSchemaSha256
        );
        RequireExactTableSchema(
            connection,
            "note_action_capture",
            CaptureSchemaSha256
        );
        RequireExactTableSchema(
            connection,
            "character_note",
            V1NoteSchemaSha256
        );
        RequireExactForeignKeys(connection, "character_note", [
            "source_action_address->note_action_capture.source_action_address:RESTRICT"
        ]);
        RequireExactIndex(
            connection,
            "character_note",
            "ux_character_note_memo_id",
            3,
            "memo_id",
            """
                CREATE UNIQUE INDEX ux_character_note_memo_id
                ON character_note(memo_id) WHERE memo_id IS NOT NULL
                """
        );
        RequireExactIndex(
            connection,
            "note_action_capture",
            "ux_note_capture_single_active",
            -2,
            null,
            """
                CREATE UNIQUE INDEX ux_note_capture_single_active
                ON note_action_capture((1))
                WHERE state IN ('Captured', 'Planned')
                """
        );
    }

    private static V1Status ReadV1Status(
        SqliteConnection connection,
        CharacterMemoryStoreOwner expectedOwner
    ) {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT schema_version, user_id, session_repository_id,
                   capture_frontier_segment_number,
                   capture_frontier_tail_offset, baseline_selected_head,
                   store_state, provision_target_pod_state_identity,
                   settled_default_pod_state_identity, active_source_action,
                   quarantine_code, quarantine_observed_pod_state_identity,
                   store_revision
            FROM character_memory_meta WHERE singleton = 1;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read()
            || reader.GetInt32(0) != PreviousSchemaVersion
            || !string.Equals(reader.GetString(1), expectedOwner.UserId, StringComparison.Ordinal)
            || !string.Equals(reader.GetString(2), expectedOwner.SessionRepositoryId, StringComparison.Ordinal)) {
            throw Corrupt("Character Memory V1 owner identity does not match.");
        }
        var baseline = new CharacterMemoryStoreBaseline(
            new EventJournalPhysicalAppendFrontier(
                checked((uint)reader.GetInt64(3)),
                reader.GetInt64(4)
            ),
            reader.IsDBNull(5) ? null : reader.GetString(5)
        );
        CharacterMemoryStoreState state = ParseStoreState(reader.GetString(6));
        string provisionTarget = reader.GetString(7);
        string? settled = reader.IsDBNull(8) ? null : reader.GetString(8);
        string? activeSource = reader.IsDBNull(9) ? null : reader.GetString(9);
        string? quarantineCode = reader.IsDBNull(10) ? null : reader.GetString(10);
        string? observed = reader.IsDBNull(11) ? null : reader.GetString(11);
        long revision = reader.GetInt64(12);
        if (reader.Read()) { throw Corrupt("Character Memory V1 meta is not singleton."); }
        reader.Close();

        ValidateBaseline(baseline);
        RequirePodStateIdentity(provisionTarget, "V1 provision target identity");
        if (settled is not null) { RequirePodStateIdentity(settled, "V1 settled identity"); }
        if (activeSource is not null) { RequireEventAddress(activeSource, "V1 active source"); }
        if (quarantineCode is not null) { RequireCode(quarantineCode, "V1 quarantine code"); }
        if (observed is not null) { RequirePodStateIdentity(observed, "V1 observed identity"); }
        CharacterMemoryCaptureSnapshot? active = activeSource is null
            ? null
            : ReadCaptureCore(connection, transaction: null, activeSource)
                ?? throw Corrupt("Character Memory V1 active source has no capture.");
        bool valid = state switch {
            CharacterMemoryStoreState.Provisioning => settled is null
                && activeSource is null && quarantineCode is null
                && observed is null && active is null,
            CharacterMemoryStoreState.Ready => settled is not null
                && quarantineCode is null && observed is null
                && ((activeSource is null && active is null)
                    || active is { State: CharacterMemoryCaptureState.Captured
                        or CharacterMemoryCaptureState.Planned }),
            CharacterMemoryStoreState.Quarantined => quarantineCode is not null,
            _ => false,
        };
        if (!valid) { throw Corrupt("Character Memory V1 meta shape is invalid."); }
        if (state is CharacterMemoryStoreState.Ready
            && active is { State: CharacterMemoryCaptureState.Planned }
            && !string.Equals(active.BasePodStateIdentity, settled, StringComparison.Ordinal)) {
            throw Corrupt("Character Memory V1 active plan base is stale.");
        }
        return new V1Status(
            baseline,
            state,
            provisionTarget,
            settled,
            activeSource,
            quarantineCode,
            observed,
            revision
        );
    }

    private static void ValidateV1GlobalCountInvariants(
        SqliteConnection connection,
        V1Status status
    ) {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM note_action_capture
                 WHERE state IN ('Captured', 'Planned')),
                (SELECT MIN(source_action_address) FROM note_action_capture
                 WHERE state IN ('Captured', 'Planned')),
                (SELECT MAX(source_action_address) FROM note_action_capture
                 WHERE state IN ('Captured', 'Planned')),
                (SELECT COUNT(*)
                 FROM note_action_capture AS capture
                 LEFT JOIN (
                     SELECT source_action_address, COUNT(*) AS child_count,
                            MIN(artifact_ordinal) AS minimum_ordinal,
                            MAX(artifact_ordinal) AS maximum_ordinal
                     FROM character_note GROUP BY source_action_address
                 ) AS children USING(source_action_address)
                 WHERE capture.artifact_count != COALESCE(children.child_count, 0)
                    OR (capture.artifact_count > 0
                        AND (children.minimum_ordinal != 0
                            OR children.maximum_ordinal != capture.artifact_count - 1)));
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read()) { throw Corrupt("Character Memory V1 invariant query was empty."); }
        long expected = status.ActiveSourceAction is null ? 0 : 1;
        if (reader.GetInt64(0) != expected
            || !string.Equals(reader.IsDBNull(1) ? null : reader.GetString(1), status.ActiveSourceAction, StringComparison.Ordinal)
            || !string.Equals(reader.IsDBNull(2) ? null : reader.GetString(2), status.ActiveSourceAction, StringComparison.Ordinal)
            || reader.GetInt64(3) != 0
            || reader.Read()) {
            throw Corrupt("Character Memory V1 global invariants are invalid.");
        }
    }

    private static void RebuildV1AsV2(
        SqliteConnection connection,
        SqliteTransaction transaction
    ) {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DROP INDEX ux_character_note_memo_id;
            ALTER TABLE character_memory_meta RENAME TO character_memory_meta_v1;
            ALTER TABLE character_note RENAME TO character_note_v1;

            CREATE TABLE character_memory_meta (
                singleton INTEGER NOT NULL PRIMARY KEY CHECK(singleton = 1),
                schema_version INTEGER NOT NULL CHECK(schema_version = 2),
                user_id TEXT NOT NULL,
                session_repository_id TEXT NOT NULL,
                capture_frontier_segment_number INTEGER NOT NULL
                    CHECK(capture_frontier_segment_number BETWEEN 1 AND 4294967295),
                capture_frontier_tail_offset INTEGER NOT NULL
                    CHECK(capture_frontier_tail_offset >= 4
                        AND capture_frontier_tail_offset % 4 = 0),
                baseline_selected_head TEXT NULL,
                store_state TEXT NOT NULL CHECK(store_state IN (
                    'Provisioning', 'Ready', 'Quarantined'
                )),
                provision_target_pod_state_identity TEXT NOT NULL,
                settled_default_pod_state_identity TEXT NULL,
                active_source_action TEXT NULL,
                active_derived_info_source_action TEXT NULL,
                quarantine_code TEXT NULL,
                quarantine_observed_pod_state_identity TEXT NULL,
                store_revision INTEGER NOT NULL CHECK(store_revision >= 0),
                CHECK(
                    (store_state = 'Provisioning'
                        AND settled_default_pod_state_identity IS NULL
                        AND active_source_action IS NULL
                        AND active_derived_info_source_action IS NULL
                        AND quarantine_code IS NULL
                        AND quarantine_observed_pod_state_identity IS NULL)
                    OR (store_state = 'Ready'
                        AND settled_default_pod_state_identity IS NOT NULL
                        AND quarantine_code IS NULL
                        AND quarantine_observed_pod_state_identity IS NULL)
                    OR (store_state = 'Quarantined'
                        AND quarantine_code IS NOT NULL)
                ),
                CHECK(active_source_action IS NULL
                    OR active_derived_info_source_action IS NULL)
            ) STRICT;

            CREATE TABLE character_note (
                source_action_address TEXT NOT NULL
                    REFERENCES note_action_capture(source_action_address)
                    ON DELETE RESTRICT,
                artifact_ordinal INTEGER NOT NULL
                    CHECK(artifact_ordinal BETWEEN 0 AND 15),
                exact_text TEXT NOT NULL CHECK(length(exact_text) > 0),
                memo_id TEXT NULL,
                derived_title TEXT NULL,
                derived_gist TEXT NULL,
                derived_summary TEXT NULL,
                PRIMARY KEY(source_action_address, artifact_ordinal)
            ) STRICT;

            CREATE TABLE derived_info_work (
                source_action_address TEXT NOT NULL PRIMARY KEY
                    REFERENCES note_action_capture(source_action_address)
                    ON DELETE RESTRICT,
                state TEXT NOT NULL CHECK(state IN (
                    'Pending', 'Prepared', 'Planned', 'Applied', 'Rejected'
                )),
                enricher_contract_id TEXT NULL,
                derived_info_commitment TEXT NULL,
                base_pod_state_identity TEXT NULL,
                target_pod_state_identity TEXT NULL,
                rejection_code TEXT NULL,
                created_revision INTEGER NOT NULL CHECK(created_revision >= 1),
                state_revision INTEGER NOT NULL CHECK(state_revision >= 1),
                CHECK(
                    (state = 'Pending'
                        AND enricher_contract_id IS NULL
                        AND derived_info_commitment IS NULL
                        AND base_pod_state_identity IS NULL
                        AND target_pod_state_identity IS NULL
                        AND rejection_code IS NULL)
                    OR (state = 'Prepared'
                        AND enricher_contract_id IS NOT NULL
                        AND derived_info_commitment IS NOT NULL
                        AND base_pod_state_identity IS NULL
                        AND target_pod_state_identity IS NULL
                        AND rejection_code IS NULL)
                    OR (state IN ('Planned', 'Applied')
                        AND enricher_contract_id IS NOT NULL
                        AND derived_info_commitment IS NOT NULL
                        AND base_pod_state_identity IS NOT NULL
                        AND target_pod_state_identity IS NOT NULL
                        AND rejection_code IS NULL)
                    OR (state = 'Rejected'
                        AND ((enricher_contract_id IS NULL
                                AND derived_info_commitment IS NULL
                                AND base_pod_state_identity IS NULL
                                AND target_pod_state_identity IS NULL)
                            OR (enricher_contract_id IS NOT NULL
                                AND derived_info_commitment IS NOT NULL
                                AND base_pod_state_identity IS NULL
                                AND target_pod_state_identity IS NULL)
                            OR (enricher_contract_id IS NOT NULL
                                AND derived_info_commitment IS NOT NULL
                                AND base_pod_state_identity IS NOT NULL
                                AND target_pod_state_identity IS NOT NULL))
                        AND rejection_code IS NOT NULL)
                )
            ) STRICT;

            INSERT INTO character_memory_meta(
                singleton, schema_version, user_id, session_repository_id,
                capture_frontier_segment_number, capture_frontier_tail_offset,
                baseline_selected_head, store_state,
                provision_target_pod_state_identity,
                settled_default_pod_state_identity, active_source_action,
                active_derived_info_source_action, quarantine_code,
                quarantine_observed_pod_state_identity, store_revision
            )
            SELECT singleton, 2, user_id, session_repository_id,
                   capture_frontier_segment_number, capture_frontier_tail_offset,
                   baseline_selected_head, store_state,
                   provision_target_pod_state_identity,
                   settled_default_pod_state_identity, active_source_action,
                   NULL, quarantine_code,
                   quarantine_observed_pod_state_identity, store_revision
            FROM character_memory_meta_v1;

            INSERT INTO character_note(
                source_action_address, artifact_ordinal, exact_text, memo_id,
                derived_title, derived_gist, derived_summary
            )
            SELECT source_action_address, artifact_ordinal, exact_text, memo_id,
                   NULL, NULL, NULL
            FROM character_note_v1;

            INSERT INTO derived_info_work(
                source_action_address, state, enricher_contract_id,
                derived_info_commitment, base_pod_state_identity,
                target_pod_state_identity, rejection_code,
                created_revision, state_revision
            )
            SELECT source_action_address, 'Pending', NULL, NULL, NULL, NULL,
                   NULL, state_revision, state_revision
            FROM note_action_capture
            WHERE state = 'Applied' AND artifact_count > 0;

            DROP TABLE character_note_v1;
            DROP TABLE character_memory_meta_v1;

            CREATE UNIQUE INDEX ux_character_note_memo_id
            ON character_note(memo_id) WHERE memo_id IS NOT NULL;

            CREATE UNIQUE INDEX ux_derived_info_single_planned
            ON derived_info_work((1)) WHERE state = 'Planned';

            PRAGMA user_version = 2;
            """;
        _ = command.ExecuteNonQuery();
    }

    private static long ReadPragmaInteger(
        SqliteConnection connection,
        string name
    ) {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"PRAGMA {name};";
        return Convert.ToInt64(command.ExecuteScalar());
    }
}
