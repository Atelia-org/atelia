using Atelia.MemoPod;
using Atelia.SessionJournal;
using Microsoft.Data.Sqlite;

namespace Atelia.Galatea.Server.CharacterMemory;

// Legacy V3/V4 receipt readers are used only by the explicit offline upgrade.
internal sealed partial class CharacterMemorySqliteStore {
    private static CharacterNoteReceiptDeliverySnapshot? ReadLegacyReceiptDeliveryCore(
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

    private static void ValidateLegacyReceiptDeliveryRows(SqliteConnection connection) {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT source_action_address FROM note_receipt_delivery ORDER BY source_action_address;";
        var sources = new List<string>();
        using (SqliteDataReader reader = command.ExecuteReader()) {
            while (reader.Read()) { sources.Add(reader.GetString(0)); }
        }
        long storeRevision = ReadStatusCore(connection, null).StoreRevision;
        foreach (string source in sources) {
            RequireEventAddress(source, nameof(source));
            CharacterNoteReceiptDeliverySnapshot row = ReadLegacyReceiptDeliveryCore(connection, null,
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
            if (row.BoundInput is { } input) { RequireLegacyReceiptInput(input, row); }
        }
    }

    private static void RequireLegacyReceiptInput(SessionInputContent input, CharacterNoteReceiptDeliverySnapshot row) {
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

    private static void RequireReceiptObservation(string rendered, string noticeBody) {
        if (!PlayerTurnObservationEnvelope.TryUnwrap(rendered, out PlayerTurnObservation observation)
            || observation.Notices.Count == 0
            || observation.Notices[^1] is not PlayerTurnNotice.NoteSaveReceipt receipt
            || !string.Equals(receipt.Body, noticeBody, StringComparison.Ordinal)
            || observation.Notices.Count(static item => item is PlayerTurnNotice.NoteSaveReceipt) != 1
            || !string.Equals(PlayerTurnObservationEnvelope.Wrap(observation), rendered, StringComparison.Ordinal)) {
            throw Corrupt("Bound receipt Observation must canonically contain exactly its frozen save receipt.");
        }
    }}
