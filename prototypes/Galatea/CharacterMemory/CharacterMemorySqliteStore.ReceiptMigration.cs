using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace Atelia.Galatea.Server.CharacterMemory;

internal sealed partial class CharacterMemorySqliteStore {
    private static void MigrateV2ToV3IfNeeded(
        SqliteConnection connection, CharacterMemoryStoreOwner owner,
        CharacterMemoryStoreTestHooks hooks
    ) {
        long version = ReadPragmaInteger(connection, "user_version");
        if (version == SchemaVersion) { return; }
        if (version != 2) { throw Corrupt("Character Memory schema is not V2 or V3."); }
        const string operation = "migrate-character-memory-v2-to-v3";
        _ = ValidateOpenedDatabase(connection, owner, expectedVersion: 2);
        string preflight = ReadPreReceiptAuthorityDigest(connection);
        hooks.AfterValidationBeforeTransaction?.Invoke(operation);
        Exception? uncertain = null;
        using (SqliteTransaction transaction = connection.BeginTransaction(deferred: false)) {
            _ = ValidateOpenedDatabase(connection, owner, expectedVersion: 2);
            if (preflight != ReadPreReceiptAuthorityDigest(connection, transaction)) {
                throw Corrupt("Character Memory V2 authority changed after migration preflight.");
            }
            string metaSql;
            using (SqliteCommand read = connection.CreateCommand()) {
                read.Transaction = transaction;
                read.CommandText = "SELECT sql FROM sqlite_schema WHERE name = 'character_memory_meta' AND type = 'table';";
                metaSql = (string)read.ExecuteScalar()!;
            }
            // The complete V2 schema was validated twice before deriving V3.
            // Only the schema-version constraint changes; all old authority is copied verbatim.
            metaSql = NormalizeSchemaSql(metaSql).Replace(
                "schema_version = 2", "schema_version = 3", StringComparison.Ordinal);
            using (SqliteCommand rebuild = connection.CreateCommand()) {
                rebuild.Transaction = transaction;
                rebuild.CommandText = "ALTER TABLE character_memory_meta RENAME TO character_memory_meta_v2;"
                    + metaSql + ";" + """
                    INSERT INTO character_memory_meta SELECT
                        singleton, 3, user_id, session_repository_id,
                        capture_frontier_segment_number, capture_frontier_tail_offset,
                        baseline_selected_head, store_state, provision_target_pod_state_identity,
                        settled_default_pod_state_identity, active_source_action,
                        active_derived_info_source_action, quarantine_code,
                        quarantine_observed_pod_state_identity, store_revision
                    FROM character_memory_meta_v2;
                    DROP TABLE character_memory_meta_v2;
                    PRAGMA user_version = 3;
                    """;
                rebuild.ExecuteNonQuery();
            }
            // Deliberately empty: an old Applied capture does not prove that
            // its old process-local notification is still pending.
            CreateReceiptDeliverySchema(connection, transaction);
            hooks.BeforeCommit?.Invoke(operation);
            try {
                transaction.Commit();
                hooks.AfterCommitBeforeReturn?.Invoke(operation);
            }
            catch (Exception exception) when (GalateaExceptionClassifier.IsNonFatal(exception)) {
                uncertain = exception;
            }
        }
        try {
            _ = ValidateOpenedDatabase(connection, owner);
            if (preflight != ReadPreReceiptAuthorityDigest(connection)) {
                throw Corrupt("Character Memory V3 migration changed existing authority.");
            }
            using SqliteCommand count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM note_receipt_delivery;";
            if ((long)count.ExecuteScalar()! != 0) {
                throw Corrupt("Character Memory migration created historical receipt deliveries.");
            }
        }
        catch (Exception validationException) when (
            uncertain is not null && GalateaExceptionClassifier.IsNonFatal(validationException)
        ) {
            throw new CharacterMemoryStoreCommitOutcomeException(operation,
                new AggregateException(uncertain, validationException));
        }
    }

    private static string ReadPreReceiptAuthorityDigest(
        SqliteConnection connection, SqliteTransaction? transaction = null
    ) {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[4];
        foreach ((string table, string order) in new[] {
            ("character_memory_meta", "singleton"),
            ("note_action_capture", "source_action_address"),
            ("character_note", "source_action_address, artifact_ordinal"),
            ("derived_info_work", "source_action_address"),
        }) {
            hash.AppendData(StrictUtf8.GetBytes(table));
            using SqliteCommand command = connection.CreateCommand();
            if (transaction is not null) { command.Transaction = transaction; }
            command.CommandText = $"SELECT * FROM {table} ORDER BY {order};";
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read()) {
                for (int index = 0; index < reader.FieldCount; index++) {
                    if (table == "character_memory_meta" && index == 1) { continue; }
                    string? value = reader.IsDBNull(index) ? null
                        : Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture);
                    byte[] bytes = value is null ? [] : StrictUtf8.GetBytes(value);
                    System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length,
                        value is null ? -1 : bytes.Length);
                    hash.AppendData(length);
                    hash.AppendData(bytes);
                }
            }
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
