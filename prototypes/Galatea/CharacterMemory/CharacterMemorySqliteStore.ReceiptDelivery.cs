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
    private const string V4ReceiptDeliverySchemaSql = """
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

    private const string ReceiptDeliverySchemaSql = """
        CREATE TABLE note_receipt_delivery (
            source_action_address TEXT NOT NULL PRIMARY KEY
                REFERENCES note_action_capture(source_action_address) ON DELETE RESTRICT,
            state TEXT NOT NULL CHECK(state IN ('Pending', 'ObservationBound', 'Delivered')),
            created_revision INTEGER NOT NULL CHECK(created_revision >= 1),
            state_revision INTEGER NOT NULL CHECK(state_revision >= created_revision),
            receipt_content BLOB NULL,
            expected_session_head TEXT NULL,
            bound_input BLOB NULL,
            observation_address TEXT NULL,
            CHECK(
                (state = 'Pending' AND receipt_content IS NOT NULL AND expected_session_head IS NULL
                    AND bound_input IS NULL AND observation_address IS NULL)
                OR (state = 'ObservationBound' AND receipt_content IS NOT NULL AND expected_session_head IS NOT NULL
                    AND bound_input IS NOT NULL AND observation_address IS NULL)
                OR (state = 'Delivered' AND receipt_content IS NULL AND expected_session_head IS NOT NULL
                    AND bound_input IS NULL AND observation_address IS NOT NULL)
            )
        ) STRICT
        """;

    private static void CreateReceiptDeliverySchema(
        SqliteConnection connection, SqliteTransaction? transaction = null, int version = SchemaVersion
    ) {
        using SqliteCommand command = connection.CreateCommand();
        if (transaction is not null) { command.Transaction = transaction; }
        command.CommandText = ReceiptSchemaSql(version) + ";" + ReceiptBoundIndexSql + ";" + ReceiptPendingIndexSql + ";";
        command.ExecuteNonQuery();
    }

    private static string ReceiptSchemaSql(int version) => version switch {
        3 => V3ReceiptDeliverySchemaSql,
        4 => V4ReceiptDeliverySchemaSql,
        5 => ReceiptDeliverySchemaSql,
        _ => throw Corrupt("Unsupported Character Note receipt schema."),
    };

    public ActionReceiptDeliverySnapshot? ReadPendingReceiptDelivery()
        => ReadReceiptDeliveryWhere("state = 'Pending' ORDER BY created_revision, source_action_address LIMIT 1");

    public ActionReceiptDeliverySnapshot? ReadBoundReceiptDelivery()
        => ReadReceiptDeliveryWhere("state = 'ObservationBound'");

    public ActionReceiptDeliverySnapshot? ReadReceiptDeliveryExact(string source) {
        RequireEventAddress(source, nameof(source));
        return ReadReceiptDeliveryWhere("source_action_address = $source", source);
    }

    private ActionReceiptDeliverySnapshot? ReadReceiptDeliveryWhere(string predicate, string? source = null) {
        lock (_gate) {
            ThrowIfDisposed();
            using SqliteConnection connection = OpenVerifiedConnection();
            return ReadReceiptDeliveryCore(connection, null, predicate, source);
        }
    }

    private static ActionReceiptDeliverySnapshot? ReadReceiptDeliveryCore(
        SqliteConnection connection, SqliteTransaction? transaction, string predicate, string? source = null
    ) {
        using SqliteCommand command = connection.CreateCommand();
        if (transaction is not null) { command.Transaction = transaction; }
        command.CommandText = """
            SELECT source_action_address, state, created_revision, state_revision,
                receipt_content, expected_session_head, bound_input, observation_address
            FROM note_receipt_delivery WHERE
            """ + " " + predicate;
        if (source is not null) { command.Parameters.AddWithValue("$source", source); }
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read()) { return null; }
        ActionReceiptDeliveryState state = reader.GetString(1) switch {
            "Pending" => ActionReceiptDeliveryState.Pending,
            "ObservationBound" => ActionReceiptDeliveryState.ObservationBound,
            "Delivered" => ActionReceiptDeliveryState.Delivered,
            _ => throw Corrupt("Unknown Character Note receipt delivery state."),
        };
        var result = new ActionReceiptDeliverySnapshot(
            reader.GetString(0), state, reader.GetInt64(2), reader.GetInt64(3),
            reader.IsDBNull(4) ? null : ReadFrozenNoteReceiptBatch(reader.GetFieldValue<byte[]>(4)),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : ReadBoundInput(reader.GetFieldValue<byte[]>(6)),
            reader.IsDBNull(7) ? null : reader.GetString(7));
        if (reader.Read()) { throw Corrupt("Multiple Character Note receipt delivery rows matched."); }
        reader.Close();
        RequireReceiptCaptureRelation(connection, transaction, result);
        RequireReceiptRowState(connection, transaction, result);
        return result;
    }

    private static void RequireReceiptRowState(
        SqliteConnection connection, SqliteTransaction? transaction, ActionReceiptDeliverySnapshot row
    ) {
        RequireEventAddress(row.SourceActionAddress, nameof(row.SourceActionAddress));
        long storeRevision = ReadStatusCore(connection, transaction).StoreRevision;
        if (row.StateRevision > storeRevision) { throw Corrupt("Receipt revision exceeds its owner revision."); }
        if (row.State != ActionReceiptDeliveryState.Pending && row.StateRevision <= row.CreatedRevision) {
            throw Corrupt("A bound or delivered receipt requires a later transition revision.");
        }
        if (row.ExpectedSessionHead is { } head) { RequireEventAddress(head, nameof(head)); }
        if (row.ObservationAddress is { } address) { RequireEventAddress(address, nameof(address)); }
        if (row.State != ActionReceiptDeliveryState.ObservationBound) { return; }
        using SqliteCommand earlier = connection.CreateCommand();
        if (transaction is not null) { earlier.Transaction = transaction; }
        earlier.CommandText = """
            SELECT count(*) FROM note_receipt_delivery
            WHERE state = 'Pending' AND (created_revision < $revision
                OR (created_revision = $revision AND source_action_address < $source));
            """;
        earlier.Parameters.AddWithValue("$revision", row.CreatedRevision);
        earlier.Parameters.AddWithValue("$source", row.SourceActionAddress);
        if ((long)earlier.ExecuteScalar()! != 0) { throw Corrupt("Bound Note receipt skipped an earlier Pending batch."); }
        RequireReceiptInput(row.BoundInput ?? throw Corrupt("Bound Note receipt has no frozen input."), row);
    }

    private static NoteReceiptBatch ReadFrozenNoteReceiptBatch(byte[] bytes) {
        if (bytes.Length > 128 * 1024) { throw Corrupt("Frozen Note receipt exceeds its codec budget."); }
        try {
            NoteReceiptBatch batch = ActionReceiptBatchCodec.ReadFrozen(StrictUtf8.GetString(bytes)) as NoteReceiptBatch
                ?? throw Corrupt("Character Memory receipt must contain a Note batch.");
            batch.RequireFrozen();
            return batch;
        }
        catch (ArgumentException) { throw Corrupt("Frozen Note receipt must be valid UTF-8 receipt content."); }
        catch (JsonException) { throw Corrupt("Frozen Note receipt must be valid receipt JSON."); }
    }

    private static NoteReceiptBatch FreezeNoteReceiptBatch(CharacterMemoryCaptureSnapshot capture) {
        if (capture.State is not (CharacterMemoryCaptureState.Planned or CharacterMemoryCaptureState.Applied)
            || capture.Notes.Count != capture.ArtifactCount
            || capture.Notes.Count is < 1 or > CharacterNoteBounds.MaximumIntentCount) {
            throw Corrupt("Applied receipt requires a nonempty bounded Note batch.");
        }
        var items = new NoteReceiptItem[capture.Notes.Count];
        for (int ordinal = 0; ordinal < items.Length; ordinal++) {
            CharacterMemoryNoteSnapshot note = capture.Notes[ordinal];
            if (note.ArtifactOrdinal != ordinal || note.MemoId is null) {
                throw Corrupt("Applied receipt requires assigned Memo IDs in artifact order.");
            }
            items[ordinal] = new NoteReceiptItem(MemoId.Parse(note.MemoId), ActionReceiptPreview.Create(note.ExactText));
        }
        var batch = new NoteReceiptBatch(capture.SourceActionAddress, CharacterNoteDefaultPodV1.PodId, items);
        batch.RequireFrozen();
        return batch;
    }

    private static byte[] SerializeNoteReceiptBatch(NoteReceiptBatch batch)
        => StrictUtf8.GetBytes(ActionReceiptBatchCodec.SerializeFrozen(batch));

    // Only immutable capture identity and Memo IDs are checked here. Receipt reads
    // and binds do not hydrate the ledger's complete Note text or the current Pod.
    private static void RequireReceiptCaptureRelation(
        SqliteConnection connection, SqliteTransaction? transaction, ActionReceiptDeliverySnapshot row
    ) {
        using SqliteCommand capture = connection.CreateCommand();
        if (transaction is not null) { capture.Transaction = transaction; }
        capture.CommandText = "SELECT state, state_revision, artifact_count FROM note_action_capture WHERE source_action_address = $source;";
        capture.Parameters.AddWithValue("$source", row.SourceActionAddress);
        int count;
        using (SqliteDataReader reader = capture.ExecuteReader()) {
            if (!reader.Read() || reader.GetString(0) != "Applied" || reader.GetInt64(1) != row.CreatedRevision) {
                throw Corrupt("Receipt delivery does not match its immutable Applied capture.");
            }
            count = reader.GetInt32(2);
        }
        if (row.FrozenBatch is not { } frozen) { return; }
        if (frozen is not NoteReceiptBatch batch || batch.SourceActionAddress != row.SourceActionAddress
            || batch.PodId != CharacterNoteDefaultPodV1.PodId || batch.Items.Count != count) {
            throw Corrupt("Frozen Note receipt has invalid capture identity.");
        }
        batch.RequireFrozen();
        using SqliteCommand ids = connection.CreateCommand();
        if (transaction is not null) { ids.Transaction = transaction; }
        ids.CommandText = "SELECT artifact_ordinal, memo_id FROM character_note WHERE source_action_address = $source ORDER BY artifact_ordinal;";
        ids.Parameters.AddWithValue("$source", row.SourceActionAddress);
        using SqliteDataReader notes = ids.ExecuteReader();
        int ordinal = 0;
        while (notes.Read()) {
            if (ordinal >= batch.Items.Count || notes.GetInt32(0) != ordinal
                || notes.GetString(1) != batch.Items[ordinal].MemoId.Value) {
                throw Corrupt("Frozen Note receipt differs from its ordered Applied Memo IDs.");
            }
            ordinal++;
        }
        if (ordinal != count) { throw Corrupt("Frozen Note receipt is not the complete Applied batch."); }
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

    public ActionReceiptDeliverySnapshot BindReceiptDelivery(
        string source, long expectedRevision, string expectedHead, SessionInputContent observation
    ) {
        RequireEventAddress(expectedHead, nameof(expectedHead));
        ArgumentNullException.ThrowIfNull(observation);
        _ = ReadBoundInput(observation.ToUtf8Json());
        return TransitionReceiptDelivery(source, expectedRevision, ActionReceiptDeliveryState.Pending,
            ActionReceiptDeliveryState.ObservationBound, expectedHead, observation, null);
    }

    public ActionReceiptDeliverySnapshot RollbackReceiptDelivery(string source, long expectedRevision)
        => TransitionReceiptDelivery(source, expectedRevision, ActionReceiptDeliveryState.ObservationBound,
            ActionReceiptDeliveryState.Pending, null, null, null);

    public ActionReceiptDeliverySnapshot CompleteReceiptDelivery(string source, long expectedRevision, string observationAddress) {
        RequireEventAddress(observationAddress, nameof(observationAddress));
        return TransitionReceiptDelivery(source, expectedRevision, ActionReceiptDeliveryState.ObservationBound,
            ActionReceiptDeliveryState.Delivered, null, null, observationAddress);
    }

    private ActionReceiptDeliverySnapshot TransitionReceiptDelivery(
        string source, long expectedRevision, ActionReceiptDeliveryState from, ActionReceiptDeliveryState to,
        string? expectedHead, SessionInputContent? observation, string? address
    ) {
        RequireEventAddress(source, nameof(source));
        if (expectedRevision < 1) { throw new ArgumentOutOfRangeException(nameof(expectedRevision)); }
        lock (_gate) {
            ThrowIfDisposed();
            return ExecuteWrite("transition-note-receipt-delivery", (connection, transaction) => {
                _ = RequireReady(connection, transaction);
                ActionReceiptDeliverySnapshot current = ReadReceiptDeliveryCore(connection, transaction,
                    "source_action_address = $source", source)
                    ?? throw new CharacterMemoryStoreConflictException("Receipt delivery is absent.");
                if (current.StateRevision != expectedRevision || current.State != from) {
                    throw new CharacterMemoryStoreConflictException("Receipt delivery handle is stale.");
                }
                if (to == ActionReceiptDeliveryState.ObservationBound) {
                    RequireReceiptInput(observation!, current);
                    ActionReceiptDeliverySnapshot? first = ReadReceiptDeliveryCore(connection, transaction,
                        "state = 'Pending' ORDER BY created_revision, source_action_address LIMIT 1");
                    if (first?.SourceActionAddress != source) {
                        throw new CharacterMemoryStoreConflictException("Only the earliest Pending Note receipt may bind.");
                    }
                    if (ReadReceiptDeliveryCore(connection, transaction, "state = 'ObservationBound'") is not null) {
                        throw new CharacterMemoryStoreConflictException("Another receipt delivery is already bound.");
                    }
                }
                if (to == ActionReceiptDeliveryState.Delivered) { expectedHead = current.ExpectedSessionHead; }
                long revision = IncrementStoreRevision(connection, transaction);
                using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE note_receipt_delivery SET state = $state, state_revision = $revision,
                        receipt_content = CASE WHEN $state = 'Delivered' THEN NULL ELSE receipt_content END,
                        expected_session_head = $head, bound_input = $observation, observation_address = $address
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
                return ReadReceiptDeliveryCore(connection, transaction, "source_action_address = $source", source)!;
            }, result => ReceiptEquivalent(ReadReceiptDeliveryExact(source), result));
        }
    }

    private static void InsertPendingReceiptDelivery(
        SqliteConnection connection, SqliteTransaction transaction, CharacterMemoryCaptureSnapshot capture, long revision
    ) {
        NoteReceiptBatch frozen = FreezeNoteReceiptBatch(capture);
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO note_receipt_delivery(source_action_address, state, created_revision, state_revision,
                receipt_content, expected_session_head, bound_input, observation_address)
            VALUES ($source, 'Pending', $revision, $revision, $receipt, NULL, NULL, NULL);
            """;
        command.Parameters.AddWithValue("$source", capture.SourceActionAddress);
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$receipt", SerializeNoteReceiptBatch(frozen));
        RequireOne(command.ExecuteNonQuery(), "receipt delivery creation");
    }

    private static void ValidateReceiptDeliverySchema(SqliteConnection connection, int version = SchemaVersion) {
        string[] columns = version == 5
            ? ["source_action_address", "state", "created_revision", "state_revision", "receipt_content",
                "expected_session_head", "bound_input", "observation_address"]
            : ["source_action_address", "state", "notice_body", "created_revision", "state_revision",
                "expected_session_head", "rendered_observation", "observation_address"];
        if (version == 4) { columns = [.. columns, "receipt_format", "bound_input"]; }
        RequireExactColumns(connection, "note_receipt_delivery", columns);
        RequireStrictTable(connection, "note_receipt_delivery");
        RequireExactTableSchema(connection, "note_receipt_delivery",
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                StrictUtf8.GetBytes(NormalizeSchemaSql(ReceiptSchemaSql(version))))));
        RequireExactForeignKeys(connection, "note_receipt_delivery",
            ["source_action_address->note_action_capture.source_action_address:RESTRICT"]);
        RequireExactIndex(connection, "note_receipt_delivery", "ux_note_receipt_single_bound", -2, null, ReceiptBoundIndexSql);
        RequireExactCompositeIndex(connection, "note_receipt_delivery", "ix_note_receipt_pending_schedule",
            false, [(version == 5 ? 2 : 3, "created_revision"), (0, "source_action_address")], ReceiptPendingIndexSql);
    }

    private static void ValidateReceiptDeliveryRows(SqliteConnection connection) {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT source_action_address FROM note_receipt_delivery ORDER BY source_action_address;";
        var sources = new List<string>();
        using (SqliteDataReader reader = command.ExecuteReader()) {
            while (reader.Read()) { sources.Add(reader.GetString(0)); }
        }
        foreach (string source in sources) {
            RequireEventAddress(source, nameof(source));
            _ = ReadReceiptDeliveryCore(connection, null, "source_action_address = $source", source);
        }
    }

    private static void RequireReceiptInput(SessionInputContent input, ActionReceiptDeliverySnapshot row) {
        PlayerTurnObservation observation = GalateaObservationContent.ReadPlayerTurn(input);
        PlayerTurnNotice.ActionReceipt[] notices = observation.Notices.OfType<PlayerTurnNotice.ActionReceipt>()
            .Where(notice => notice.Batch is NoteReceiptBatch).ToArray();
        if (notices.Length != 1 || row.FrozenBatch is not NoteReceiptBatch batch
            || !batch.MatchesProjection(notices[0].Batch)) {
            throw Corrupt("Bound Note receipt differs from its immutable frozen batch.");
        }
    }

    private static bool ReceiptEquivalent(ActionReceiptDeliverySnapshot? left, ActionReceiptDeliverySnapshot right)
        => left is not null && (left with { FrozenBatch = null }) == (right with { FrozenBatch = null })
            && (left.FrozenBatch is null && right.FrozenBatch is null
                || left.FrozenBatch is { } a && right.FrozenBatch is { } b
                    && a.ToJson().GetRawText() == b.ToJson().GetRawText());
}
