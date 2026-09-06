using Atelia.MemoPod;
using Microsoft.Data.Sqlite;

namespace Atelia.Galatea.Server.CharacterMemory;

internal sealed partial class CharacterMemorySqliteStore {
    private const string ReceiptDeliverySchemaSql = """
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
        SqliteConnection connection, SqliteTransaction? transaction = null
    ) {
        using SqliteCommand command = connection.CreateCommand();
        if (transaction is not null) { command.Transaction = transaction; }
        command.CommandText = ReceiptDeliverySchemaSql + ";"
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
        command.CommandText = """
            SELECT source_action_address, state, notice_body, created_revision,
                   state_revision, expected_session_head, rendered_observation,
                   observation_address
            FROM note_receipt_delivery WHERE
            """ + " " + predicate;
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
            reader.GetString(0), state, reader.GetString(2), reader.GetInt64(3),
            reader.GetInt64(4), reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7)
        );
        if (reader.Read()) { throw Corrupt("Multiple Character Note receipt delivery rows matched."); }
        return result;
    }

    internal CharacterNoteReceiptDeliverySnapshot BindReceiptDelivery(
        string source, long expectedRevision, string expectedHead,
        string renderedObservation
    ) {
        RequireEventAddress(expectedHead, nameof(expectedHead));
        ArgumentException.ThrowIfNullOrWhiteSpace(renderedObservation);
        if (TextExtractorUtf8.GetByteCount(renderedObservation)
                > PlayerTurnObservationEnvelope.MaximumRenderedUtf8Bytes) {
            throw new ArgumentOutOfRangeException(nameof(renderedObservation));
        }
        return TransitionReceiptDelivery(source, expectedRevision,
            CharacterNoteReceiptDeliveryState.Pending,
            CharacterNoteReceiptDeliveryState.ObservationBound,
            expectedHead, renderedObservation, null);
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
        string? expectedHead, string? observation, string? address
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
                        RequireReceiptObservation(observation!, current.NoticeBody);
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
                            rendered_observation = $observation, observation_address = $address
                        WHERE source_action_address = $source AND state_revision = $expected;
                        """;
                    command.Parameters.AddWithValue("$state", to.ToString());
                    command.Parameters.AddWithValue("$revision", revision);
                    command.Parameters.AddWithValue("$head", (object?)expectedHead ?? DBNull.Value);
                    command.Parameters.AddWithValue("$observation", (object?)observation ?? DBNull.Value);
                    command.Parameters.AddWithValue("$address", (object?)address ?? DBNull.Value);
                    command.Parameters.AddWithValue("$source", source);
                    command.Parameters.AddWithValue("$expected", expectedRevision);
                    RequireOne(command.ExecuteNonQuery(), "receipt delivery transition");
                    return ReadReceiptDeliveryCore(connection, transaction,
                        "source_action_address = $source", source)!;
                },
                result => ReadReceiptDeliveryExact(source) == result);
        }
    }

    private static void InsertPendingReceiptDelivery(
        SqliteConnection connection, SqliteTransaction transaction,
        CharacterMemoryCaptureSnapshot capture, long revision
    ) {
        CharacterNoteAppliedMemo[] memos = capture.Notes.Select(note => new CharacterNoteAppliedMemo(
            capture.SourceActionAddress, note.ArtifactOrdinal, CharacterNoteDefaultPodV1.PodId,
            MemoId.Parse(note.MemoId!), note.ExactText
        )).ToArray();
        CharacterNoteSaveReceipt receipt = CharacterNoteSaveReceipt.CreateDurable(memos);
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO note_receipt_delivery(source_action_address, state, notice_body,
                created_revision, state_revision, expected_session_head,
                rendered_observation, observation_address)
            VALUES ($source, 'Pending', $body, $revision, $revision, NULL, NULL, NULL);
            """;
        command.Parameters.AddWithValue("$source", capture.SourceActionAddress);
        command.Parameters.AddWithValue("$body", receipt.Notice.Body);
        command.Parameters.AddWithValue("$revision", revision);
        RequireOne(command.ExecuteNonQuery(), "receipt delivery creation");
    }

    private static void ValidateReceiptDeliverySchema(SqliteConnection connection) {
        RequireExactColumns(connection, "note_receipt_delivery", [
            "source_action_address", "state", "notice_body", "created_revision", "state_revision",
            "expected_session_head", "rendered_observation", "observation_address"]);
        RequireStrictTable(connection, "note_receipt_delivery");
        RequireExactTableSchema(connection, "note_receipt_delivery",
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                StrictUtf8.GetBytes(NormalizeSchemaSql(ReceiptDeliverySchemaSql)))));
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
            CharacterNoteAppliedMemo[] memos = capture.Notes.Select(note => new CharacterNoteAppliedMemo(
                source, note.ArtifactOrdinal, CharacterNoteDefaultPodV1.PodId,
                MemoId.Parse(note.MemoId!), note.ExactText)).ToArray();
            if (!string.Equals(CharacterNoteSaveReceipt.CreateDurable(memos).Notice.Body,
                    row.NoticeBody, StringComparison.Ordinal)) {
                throw Corrupt("Receipt payload does not match its durable Applied capture.");
            }
            if (row.ExpectedSessionHead is { } head) { RequireEventAddress(head, nameof(head)); }
            if (row.ObservationAddress is { } address) { RequireEventAddress(address, nameof(address)); }
            if (row.RenderedObservation is { } observation
                && (string.IsNullOrWhiteSpace(observation)
                    || TextExtractorUtf8.GetByteCount(observation)
                        > PlayerTurnObservationEnvelope.MaximumRenderedUtf8Bytes)) {
                throw Corrupt("Receipt delivery Observation is invalid.");
            }
            if (row.RenderedObservation is { } rendered) {
                RequireReceiptObservation(rendered, row.NoticeBody);
            }
        }
    }

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
