using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace Atelia.Galatea.Server.CharacterMemory;

internal sealed partial class CharacterMemorySqliteStore {
    private static void MigrateV3ToV4IfNeeded(SqliteConnection connection,
        CharacterMemoryStoreOwner owner, CharacterMemoryStoreTestHooks hooks) {
        long version = ReadPragmaInteger(connection, "user_version");
        if (version == 4) { return; }
        if (version != 3) { throw Corrupt("Character Memory semantic migration requires V3."); }
        const string operation = "migrate-character-memory-v3-to-v4";
        _ = ValidateOpenedDatabase(connection, owner, expectedVersion: 3);
        string preflight = ReadReceiptMigrationAuthority(connection);
        hooks.AfterValidationBeforeTransaction?.Invoke(operation);
        Exception? uncertain = null;
        using (SqliteTransaction transaction = connection.BeginTransaction(deferred: false)) {
            _ = ValidateOpenedDatabase(connection, owner, expectedVersion: 3);
            if (ReadReceiptMigrationAuthority(connection) != preflight) { throw Corrupt("V3 authority changed after migration preflight."); }
            using SqliteCommand read = connection.CreateCommand();
            read.Transaction = transaction;
            read.CommandText = "SELECT sql FROM sqlite_schema WHERE name = 'character_memory_meta' AND type = 'table';";
            string metaSql = NormalizeSchemaSql((string)read.ExecuteScalar()!).Replace("schema_version = 3", "schema_version = 4", StringComparison.Ordinal);
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "ALTER TABLE character_memory_meta RENAME TO character_memory_meta_v3;" + metaSql + ";" + """
                INSERT INTO character_memory_meta SELECT singleton, 4, user_id, session_repository_id,
                    capture_frontier_segment_number, capture_frontier_tail_offset, baseline_selected_head,
                    store_state, provision_target_pod_state_identity, settled_default_pod_state_identity,
                    active_source_action, active_derived_info_source_action, quarantine_code,
                    quarantine_observed_pod_state_identity, store_revision FROM character_memory_meta_v3;
                DROP TABLE character_memory_meta_v3;
                DROP INDEX ux_note_receipt_single_bound;
                DROP INDEX ix_note_receipt_pending_schedule;
                ALTER TABLE note_receipt_delivery RENAME TO note_receipt_delivery_v3;
                """;
            command.ExecuteNonQuery();
            CreateReceiptDeliverySchema(connection, transaction, version: 4);
            command.CommandText = """
                INSERT INTO note_receipt_delivery SELECT source_action_address, state, notice_body,
                    created_revision, state_revision, expected_session_head, rendered_observation,
                    observation_address, 'legacy-text', NULL FROM note_receipt_delivery_v3;
                DROP TABLE note_receipt_delivery_v3;
                PRAGMA user_version = 4;
                """;
            command.ExecuteNonQuery();
            hooks.BeforeCommit?.Invoke(operation);
            try {
                transaction.Commit();
                hooks.AfterCommitBeforeReturn?.Invoke(operation);
            }
            catch (Exception exception) when (GalateaExceptionClassifier.IsNonFatal(exception)) { uncertain = exception; }
        }
        try {
            _ = ValidateOpenedDatabase(connection, owner, expectedVersion: 4);
            if (ReadReceiptMigrationAuthority(connection) != preflight) { throw Corrupt("V4 migration changed existing receipt or capture authority."); }
        }
        catch (Exception exception) when (uncertain is not null && GalateaExceptionClassifier.IsNonFatal(exception)) {
            throw new CharacterMemoryStoreCommitOutcomeException(operation, new AggregateException(uncertain, exception));
        }
    }

    private static string ReadReceiptMigrationAuthority(SqliteConnection connection) {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(StrictUtf8.GetBytes(ReadPreReceiptAuthorityDigest(connection)));
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT CAST(source_action_address AS BLOB), CAST(state AS BLOB), CAST(notice_body AS BLOB),
                CAST(created_revision AS BLOB), CAST(state_revision AS BLOB), CAST(expected_session_head AS BLOB),
                CAST(rendered_observation AS BLOB), CAST(observation_address AS BLOB)
            FROM note_receipt_delivery ORDER BY source_action_address;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        Span<byte> length = stackalloc byte[4];
        while (reader.Read()) {
            for (int i = 0; i < reader.FieldCount; i++) {
                byte[] bytes = reader.IsDBNull(i) ? [] : reader.GetFieldValue<byte[]>(i);
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, reader.IsDBNull(i) ? -1 : bytes.Length);
                hash.AppendData(length);
                hash.AppendData(bytes);
            }
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
