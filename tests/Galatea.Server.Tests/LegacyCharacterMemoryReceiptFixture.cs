using Atelia.Galatea.Server.CharacterMemory;
using Microsoft.Data.Sqlite;

namespace Atelia.Galatea.Server.Tests;

/// <summary>Independent historical row/DDL fixture; never invokes a retired receipt writer.</summary>
internal static class LegacyCharacterMemoryReceiptFixture {
    internal const string Body = "历史 Note 保存回执；不可从此 Markdown 重建业务内容。";
    internal static void Install(string database, CharacterNoteReceiptDeliverySnapshot row,
        int version, bool semantic = false) {
        using var connection = new SqliteConnection($"Data Source={database};Mode=ReadWrite;Pooling=False");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DROP TABLE note_receipt_delivery;" + (version == 3 ? V3Schema : V4Schema) + ";" + """
            CREATE UNIQUE INDEX ux_note_receipt_single_bound
            ON note_receipt_delivery((1)) WHERE state = 'ObservationBound';
            CREATE INDEX ix_note_receipt_pending_schedule
            ON note_receipt_delivery(created_revision, source_action_address)
            WHERE state = 'Pending';
            """;
        command.ExecuteNonQuery();
        command.CommandText = """
            INSERT INTO note_receipt_delivery(source_action_address, state, notice_body,
                created_revision, state_revision, expected_session_head, rendered_observation, observation_address
            """ + (version == 4 ? ", receipt_format, bound_input" : string.Empty) + ") VALUES($source, $state, $body, $created, $revision, $head, $rendered, $address"
            + (version == 4 ? ", $format, $input" : string.Empty) + ");";
        command.Parameters.AddWithValue("$source", row.SourceActionAddress);
        command.Parameters.AddWithValue("$state", row.State.ToString());
        command.Parameters.AddWithValue("$body", semantic ? DBNull.Value : (object?)row.NoticeBody ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", row.CreatedRevision);
        command.Parameters.AddWithValue("$revision", row.StateRevision);
        command.Parameters.AddWithValue("$head", (object?)row.ExpectedSessionHead ?? DBNull.Value);
        command.Parameters.AddWithValue("$rendered", (object?)row.RenderedObservation ?? DBNull.Value);
        command.Parameters.AddWithValue("$address", (object?)row.ObservationAddress ?? DBNull.Value);
        if (version == 4) {
            command.Parameters.AddWithValue("$format", semantic ? "applied-source-v1" : "legacy-text");
            command.Parameters.AddWithValue("$input", (object?)row.BoundInput?.ToUtf8Json() ?? DBNull.Value);
        }
        command.ExecuteNonQuery();
        command.Parameters.Clear();
        command.CommandText = $"""
            PRAGMA writable_schema = ON;
            UPDATE sqlite_schema SET sql = replace(sql, 'schema_version = 5', 'schema_version = {version}')
                WHERE type = 'table' AND name = 'character_memory_meta';
            PRAGMA writable_schema = OFF;
            PRAGMA schema_version = 401;
            PRAGMA ignore_check_constraints = ON;
            UPDATE character_memory_meta SET schema_version = {version}, store_revision = max(store_revision, {row.StateRevision});
            PRAGMA ignore_check_constraints = OFF;
            PRAGMA user_version = {version};
            """;
        command.ExecuteNonQuery();
    }

    private const string V3Schema = """
        CREATE TABLE note_receipt_delivery (
            source_action_address TEXT NOT NULL PRIMARY KEY
                REFERENCES note_action_capture(source_action_address)
                ON DELETE RESTRICT,
            state TEXT NOT NULL CHECK(state IN (
                'Pending', 'ObservationBound', 'Delivered'
            )),
            notice_body TEXT NOT NULL CHECK(length(notice_body) > 0),
            created_revision INTEGER NOT NULL CHECK(created_revision >= 1),
            state_revision INTEGER NOT NULL CHECK(state_revision >= created_revision),
            expected_session_head TEXT NULL,
            rendered_observation TEXT NULL,
            observation_address TEXT NULL,
            CHECK(
                (state = 'Pending' AND expected_session_head IS NULL
                    AND rendered_observation IS NULL AND observation_address IS NULL)
                OR (state = 'ObservationBound' AND expected_session_head IS NOT NULL
                    AND rendered_observation IS NOT NULL AND observation_address IS NULL)
                OR (state = 'Delivered' AND expected_session_head IS NOT NULL
                    AND rendered_observation IS NULL AND observation_address IS NOT NULL)
            )
        ) STRICT
        """;

    private const string V4Schema = """
        CREATE TABLE note_receipt_delivery (
            source_action_address TEXT NOT NULL PRIMARY KEY
                REFERENCES note_action_capture(source_action_address) ON DELETE RESTRICT,
            state TEXT NOT NULL CHECK(state IN ('Pending', 'ObservationBound', 'Delivered')),
            notice_body TEXT NULL,
            created_revision INTEGER NOT NULL CHECK(created_revision >= 1),
            state_revision INTEGER NOT NULL CHECK(state_revision >= created_revision),
            expected_session_head TEXT NULL,
            rendered_observation TEXT NULL,
            observation_address TEXT NULL,
            receipt_format TEXT NOT NULL CHECK(receipt_format IN ('legacy-text', 'applied-source-v1')),
            bound_input BLOB NULL,
            CHECK((receipt_format = 'legacy-text' AND notice_body IS NOT NULL AND length(notice_body) > 0)
                OR (receipt_format = 'applied-source-v1' AND notice_body IS NULL AND rendered_observation IS NULL)),
            CHECK(
                (state = 'Pending' AND expected_session_head IS NULL AND rendered_observation IS NULL
                    AND bound_input IS NULL AND observation_address IS NULL)
                OR (state = 'ObservationBound' AND expected_session_head IS NOT NULL AND observation_address IS NULL
                    AND ((rendered_observation IS NOT NULL AND bound_input IS NULL)
                        OR (rendered_observation IS NULL AND bound_input IS NOT NULL)))
                OR (state = 'Delivered' AND expected_session_head IS NOT NULL AND rendered_observation IS NULL
                    AND bound_input IS NULL AND observation_address IS NOT NULL)
            )
        ) STRICT
        """;
}
