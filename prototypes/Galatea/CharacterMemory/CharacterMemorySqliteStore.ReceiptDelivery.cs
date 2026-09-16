using Atelia.MemoPod;
using Atelia.SessionJournal;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Atelia.Galatea.Server.CharacterMemory;

internal sealed partial class CharacterMemorySqliteStore {
    private const string V3ReceiptDeliverySchemaSql = """
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
    private const string ReceiptDeliverySchemaSql = """
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
    private const string ReceiptBoundIndexSql = """
        CREATE UNIQUE INDEX ux_note_receipt_single_bound
        ON note_receipt_delivery((1)) WHERE state = 'ObservationBound'
        """;
    private const string ReceiptPendingIndexSql = """
        CREATE INDEX ix_note_receipt_pending_schedule
        ON note_receipt_delivery(created_revision, source_action_address)
        WHERE state = 'Pending'
        """;

    private static void CreateReceiptDeliverySchema(
        SqliteConnection connection, SqliteTransaction? transaction = null, int version = SchemaVersion
    ) {
        using SqliteCommand command = connection.CreateCommand();
        if (transaction is not null) { command.Transaction = transaction; }
        command.CommandText = (version == 3 ? V3ReceiptDeliverySchemaSql : ReceiptDeliverySchemaSql) + ";"
            + ReceiptBoundIndexSql + ";" + ReceiptPendingIndexSql + ";";
        command.ExecuteNonQuery();
    }

    internal CharacterNoteReceiptDeliverySnapshot? ReadPendingReceiptDelivery()
        => ReadReceiptDeliveryWhere("state = 'Pending' ORDER BY created_revision, source_action_address LIMIT 1");

    internal CharacterNoteReceiptDeliverySnapshot? ReadBoundReceiptDelivery()
        => ReadReceiptDeliveryWhere("state = 'ObservationBound'");

    internal CharacterNoteReceiptDeliverySnapshot? ReadReceiptDeliveryExact(string source) {
        RequireEventAddress(source, nameof(source));
        return ReadReceiptDeliveryWhere("source_action_address = $source", source);
    }

    private CharacterNoteReceiptDeliverySnapshot? ReadReceiptDeliveryWhere(
        string predicate, string? source = null
    ) {
        lock (_gate) {
            ThrowIfDisposed();
            using SqliteConnection connection = OpenVerifiedConnection();
            return ReadReceiptDeliveryCore(connection, null, predicate, source);
        }
    }

    private static CharacterNoteReceiptDeliverySnapshot? ReadReceiptDeliveryCore(
        SqliteConnection connection, SqliteTransaction? transaction,
        string predicate, string? source = null
    ) {
        using SqliteCommand command = connection.CreateCommand();
        if (transaction is not null) { command.Transaction = transaction; }
        bool legacySchema = ReadPragmaInteger(connection, "user_version") == 3;
        command.CommandText = """
            SELECT source_action_address, state, CAST(notice_body AS BLOB), created_revision,
                   state_revision, expected_session_head, rendered_observation,
                   observation_address,
            """ + (legacySchema ? " 'legacy-text', NULL " : " receipt_format, bound_input ")
            + " FROM note_receipt_delivery WHERE " + predicate;
        if (source is not null) { command.Parameters.AddWithValue("$source", source); }
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read()) { return null; }
        CharacterNoteReceiptDeliveryState state = reader.GetString(1) switch {
            "Pending" => CharacterNoteReceiptDeliveryState.Pending,
            "ObservationBound" => CharacterNoteReceiptDeliveryState.ObservationBound,
            "Delivered" => CharacterNoteReceiptDeliveryState.Delivered,
            _ => throw Corrupt("Unknown Character Note receipt delivery state."),
        };
        var result = new CharacterNoteReceiptDeliverySnapshot(
            reader.GetString(0), state, reader.IsDBNull(2) ? null : ReadFrozenReceiptBody(reader, 2), reader.GetInt64(3),
            reader.GetInt64(4), reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            BoundInput: reader.IsDBNull(9) ? null : ReadBoundInput(reader.GetFieldValue<byte[]>(9))
        );
        string format = reader.GetString(8);
        if (reader.Read()) { throw Corrupt("Multiple Character Note receipt delivery rows matched."); }
        reader.Close();
        if (format == "applied-source-v1") {
            CharacterMemoryCaptureSnapshot capture = ReadCaptureCore(connection, transaction, result.SourceActionAddress)
                ?? throw Corrupt("Receipt source capture is absent.");
            if (capture.State != CharacterMemoryCaptureState.Applied || capture.StateRevision != result.CreatedRevision || result.NoticeBody is not null) {
                throw Corrupt("Semantic receipt does not reference its immutable Applied capture.");
            }
            result = result with { Facts = new CharacterNoteReceiptFacts(capture.SourceActionAddress,
                capture.Notes.Select(note => new CharacterNoteAppliedMemo(capture.SourceActionAddress,
                    note.ArtifactOrdinal, CharacterNoteDefaultPodV1.PodId, MemoId.Parse(note.MemoId!), note.ExactText)).ToArray()) };
        }
        else if (format != "legacy-text" || result.NoticeBody is null) { throw Corrupt("Invalid receipt format."); }
        return result;
    }

    private static SessionInputContent ReadBoundInput(byte[] bytes) {
        if (bytes.Length > GalateaObservationContent.MaximumContentUtf8Bytes + 1024) { throw Corrupt("Bound input exceeds its machine-content limit."); }
        SessionInputContent content;
        try { content = JsonSerializer.Deserialize<SessionInputContent>(bytes) ?? throw Corrupt("Bound input is null."); }
        catch (JsonException exception) { throw Corrupt("Bound input is invalid machine JSON: " + exception.GetType().Name); }
        if (!content.IsStructured || !GalateaObservationContent.IsSupportedSchemaId(content.SchemaId)) { throw Corrupt("New receipt bindings require a structured Galatea Observation."); }
        GalateaObservationContent.Validate(content);
        return content;
    }

    private static string ReadFrozenReceiptBody(SqliteDataReader reader, int ordinal) {
        // Decode the original SQLite TEXT bytes strictly; GetString would
        // replace malformed UTF-8 before the payload contract could reject it.
        byte[] bytes = reader.GetFieldValue<byte[]>(ordinal);
        if (bytes.Length > PlayerTurnObservationEnvelope.MaximumNoteSaveReceiptUtf8Bytes) {
            throw Corrupt("Frozen receipt body exceeds its UTF-8 budget.");
        }
        try {
            string body = StrictUtf8.GetString(bytes);
            _ = new PlayerTurnNotice.NoteSaveReceipt(body);
            return body;
        }
        catch (ArgumentException) {
            throw Corrupt("Frozen receipt body must be nonblank valid UTF-8 text.");
        }
    }

    internal CharacterNoteReceiptDeliverySnapshot BindReceiptDelivery(
        string source, long expectedRevision, string expectedHead,
        SessionInputContent observation
    ) {
        RequireEventAddress(expectedHead, nameof(expectedHead));
        ArgumentNullException.ThrowIfNull(observation);
        _ = ReadBoundInput(observation.ToUtf8Json());
        return TransitionReceiptDelivery(source, expectedRevision,
            CharacterNoteReceiptDeliveryState.Pending,
            CharacterNoteReceiptDeliveryState.ObservationBound,
            expectedHead, observation, null);
    }

    internal CharacterNoteReceiptDeliverySnapshot RollbackReceiptDelivery(
        string source, long expectedRevision
    ) => TransitionReceiptDelivery(source, expectedRevision,
        CharacterNoteReceiptDeliveryState.ObservationBound,
        CharacterNoteReceiptDeliveryState.Pending, null, null, null);

    internal CharacterNoteReceiptDeliverySnapshot CompleteReceiptDelivery(
        string source, long expectedRevision, string observationAddress
    ) {
        RequireEventAddress(observationAddress, nameof(observationAddress));
        return TransitionReceiptDelivery(source, expectedRevision,
            CharacterNoteReceiptDeliveryState.ObservationBound,
            CharacterNoteReceiptDeliveryState.Delivered,
            null, null, observationAddress);
    }

    private CharacterNoteReceiptDeliverySnapshot TransitionReceiptDelivery(
        string source, long expectedRevision,
        CharacterNoteReceiptDeliveryState from, CharacterNoteReceiptDeliveryState to,
        string? expectedHead, SessionInputContent? observation, string? address
    ) {
        RequireEventAddress(source, nameof(source));
        if (expectedRevision < 1) { throw new ArgumentOutOfRangeException(nameof(expectedRevision)); }
        lock (_gate) {
            ThrowIfDisposed();
            return ExecuteWrite("transition-note-receipt-delivery",
                (connection, transaction) => {
                    _ = RequireReady(connection, transaction);
                    CharacterNoteReceiptDeliverySnapshot current = ReadReceiptDeliveryCore(
                        connection, transaction, "source_action_address = $source", source
                    ) ?? throw new CharacterMemoryStoreConflictException("Receipt delivery is absent.");
                    if (current.StateRevision != expectedRevision || current.State != from) {
                        throw new CharacterMemoryStoreConflictException("Receipt delivery handle is stale.");
                    }
                    if (to == CharacterNoteReceiptDeliveryState.ObservationBound) {
                        RequireReceiptInput(observation!, current);
                        if (ReadReceiptDeliveryCore(connection, transaction,
                                "state = 'ObservationBound'") is not null) {
                            throw new CharacterMemoryStoreConflictException("Another receipt delivery is already bound.");
                        }
                    }
                    if (to == CharacterNoteReceiptDeliveryState.Delivered) {
                        expectedHead = current.ExpectedSessionHead;
                        observation = null;
                    }
                    long revision = IncrementStoreRevision(connection, transaction);
                    using SqliteCommand command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = """
                        UPDATE note_receipt_delivery SET state = $state,
                            state_revision = $revision, expected_session_head = $head,
                            rendered_observation = NULL, bound_input = $observation, observation_address = $address
                        WHERE source_action_address = $source AND state_revision = $expected;
                        """;
                    command.Parameters.AddWithValue("$state", to.ToString());
                    command.Parameters.AddWithValue("$revision", revision);
                    command.Parameters.AddWithValue("$head", (object?)expectedHead ?? DBNull.Value);
                    command.Parameters.AddWithValue("$observation", (object?)observation?.ToUtf8Json() ?? DBNull.Value);
                    command.Parameters.AddWithValue("$address", (object?)address ?? DBNull.Value);
                    command.Parameters.AddWithValue("$source", source);
                    command.Parameters.AddWithValue("$expected", expectedRevision);
                    RequireOne(command.ExecuteNonQuery(), "receipt delivery transition");
                    return ReadReceiptDeliveryCore(connection, transaction,
                        "source_action_address = $source", source)!;
                },
                result => ReceiptEquivalent(ReadReceiptDeliveryExact(source), result));
        }
    }

    private static void InsertPendingReceiptDelivery(
        SqliteConnection connection, SqliteTransaction transaction,
        CharacterMemoryCaptureSnapshot capture, long revision
    ) {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO note_receipt_delivery(source_action_address, state, notice_body,
                created_revision, state_revision, expected_session_head,
                rendered_observation, observation_address, receipt_format, bound_input)
            VALUES ($source, 'Pending', NULL, $revision, $revision, NULL, NULL, NULL, 'applied-source-v1', NULL);
            """;
        command.Parameters.AddWithValue("$source", capture.SourceActionAddress);
        command.Parameters.AddWithValue("$revision", revision);
        RequireOne(command.ExecuteNonQuery(), "receipt delivery creation");
    }

    private static void ValidateReceiptDeliverySchema(SqliteConnection connection, int version = SchemaVersion) {
        string[] columns = [
            "source_action_address", "state", "notice_body", "created_revision", "state_revision",
            "expected_session_head", "rendered_observation", "observation_address"];
        if (version == 4) { columns = [.. columns, "receipt_format", "bound_input"]; }
        RequireExactColumns(connection, "note_receipt_delivery", columns);
        RequireStrictTable(connection, "note_receipt_delivery");
        RequireExactTableSchema(connection, "note_receipt_delivery",
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                StrictUtf8.GetBytes(NormalizeSchemaSql(version == 3 ? V3ReceiptDeliverySchemaSql : ReceiptDeliverySchemaSql)))));
        RequireExactForeignKeys(connection, "note_receipt_delivery", [
            "source_action_address->note_action_capture.source_action_address:RESTRICT"]);
        RequireExactIndex(connection, "note_receipt_delivery", "ux_note_receipt_single_bound",
            -2, null, ReceiptBoundIndexSql);
        RequireExactCompositeIndex(connection, "note_receipt_delivery", "ix_note_receipt_pending_schedule",
            false, [(3, "created_revision"), (0, "source_action_address")], ReceiptPendingIndexSql);
    }

    private static void ValidateReceiptDeliveryRows(SqliteConnection connection) {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT source_action_address FROM note_receipt_delivery ORDER BY source_action_address;";
        var sources = new List<string>();
        using (SqliteDataReader reader = command.ExecuteReader()) {
            while (reader.Read()) { sources.Add(reader.GetString(0)); }
        }
        long storeRevision = ReadStatusCore(connection, null).StoreRevision;
        foreach (string source in sources) {
            RequireEventAddress(source, nameof(source));
            CharacterNoteReceiptDeliverySnapshot row = ReadReceiptDeliveryCore(connection, null,
                "source_action_address = $source", source)!;
            CharacterMemoryCaptureSnapshot capture = ReadCaptureCore(connection, null, source)
                ?? throw Corrupt("Receipt source capture is absent.");
            if (capture.State != CharacterMemoryCaptureState.Applied
                || row.CreatedRevision != capture.StateRevision
                || row.StateRevision > storeRevision) {
                throw Corrupt("Receipt delivery does not match its durable Applied capture.");
            }
            // NoticeBody is frozen in the Applied transaction. Later renderer
            // wording changes must not invalidate or rewrite that saved notice.
            if (row.ExpectedSessionHead is { } head) { RequireEventAddress(head, nameof(head)); }
            if (row.ObservationAddress is { } address) { RequireEventAddress(address, nameof(address)); }
            if (row.RenderedObservation is { } observation
                && (string.IsNullOrWhiteSpace(observation)
                    || TextExtractorUtf8.GetByteCount(observation)
                        > PlayerTurnObservationEnvelope.MaximumRenderedUtf8Bytes)) {
                throw Corrupt("Receipt delivery Observation is invalid.");
            }
            if (row.RenderedObservation is { } rendered) {
                RequireReceiptObservation(rendered, row.NoticeBody
                    ?? throw Corrupt("Legacy bound receipt has no notice body."));
            }
            if (row.BoundInput is { } input) { RequireReceiptInput(input, row); }
        }
    }

    private static void RequireReceiptInput(SessionInputContent input, CharacterNoteReceiptDeliverySnapshot row) {
        PlayerTurnObservation observation = GalateaObservationContent.ReadPlayerTurn(input);
        if (observation.Notices.Count == 0
            || observation.Notices[^1] is not PlayerTurnNotice.NoteSaveReceipt notice
            || observation.Notices.Count(n => n is PlayerTurnNotice.NoteSaveReceipt) != 1) {
            throw Corrupt("Bound input must contain exactly its receipt as the final notice.");
        }
        if (row.Facts is { } facts) {
            CharacterNoteReceiptSelection selection = notice.Selection
                ?? throw Corrupt("Semantic receipt cannot bind a legacy body.");
            if (selection.SourceActionAddress != facts.SourceActionAddress
                || !selection.MemoIds.SequenceEqual(facts.Memos.Select(memo => memo.MemoId))
                || selection.ExactTexts.Count != 0 && !selection.ExactTexts.SequenceEqual(facts.Memos.Select(memo => memo.ExactText))) {
                throw Corrupt("Bound receipt content differs from its immutable Applied batch.");
            }
        }
        else if (notice.Selection is not null || !notice.IsLegacyDurable
            || notice.LegacySourceActionAddress != row.SourceActionAddress
            || !string.Equals(notice.Body, row.NoticeBody, StringComparison.Ordinal)) {
            throw Corrupt("Legacy receipt body must remain exact in a new structured Observation.");
        }
    }

    private static bool ReceiptEquivalent(CharacterNoteReceiptDeliverySnapshot? left, CharacterNoteReceiptDeliverySnapshot right) =>
        left is not null && (left with { Facts = null }) == (right with { Facts = null })
        && (left.Facts is null && right.Facts is null || left.Facts is { } a && right.Facts is { } b
            && a.SourceActionAddress == b.SourceActionAddress && a.Memos.SequenceEqual(b.Memos));

    private static void RequireReceiptObservation(string rendered, string noticeBody) {
        if (!PlayerTurnObservationEnvelope.TryUnwrap(rendered, out PlayerTurnObservation observation)
            || observation.Notices.Count == 0
            || observation.Notices[^1] is not PlayerTurnNotice.NoteSaveReceipt receipt
            || !string.Equals(receipt.Body, noticeBody, StringComparison.Ordinal)
            || observation.Notices.Count(static item => item is PlayerTurnNotice.NoteSaveReceipt) != 1
            || !string.Equals(PlayerTurnObservationEnvelope.Wrap(observation), rendered, StringComparison.Ordinal)) {
            throw Corrupt("Bound receipt Observation must canonically contain exactly its frozen save receipt.");
        }
    }
}
