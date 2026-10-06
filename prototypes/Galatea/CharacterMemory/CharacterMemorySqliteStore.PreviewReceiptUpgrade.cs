using Atelia.SessionJournal;
using Microsoft.Data.Sqlite;

namespace Atelia.Galatea.Server.CharacterMemory;

// Historical receipt interpretation is confined to explicit offline maintenance.
internal sealed partial class CharacterMemorySqliteStore {
    private sealed record LegacyReceiptUpgradeProof(
        string SourceActionAddress, long StateRevision, string ExpectedSessionHead,
        SessionInputContent ExactInput, string? AppendedObservationAddress
    );

    private static LegacyReceiptUpgradeProof[] PreflightLegacyReceiptUpgrade(
        SqliteConnection connection, int version, SessionJournalEngine? journal
    ) {
        if (version < 3) { return []; }
        CharacterNoteReceiptDeliverySnapshot? bound = ReadLegacyReceiptDeliveryCore(
            connection, null, "state = 'ObservationBound'");
        if (bound is null) { return []; }
        if (journal is null) { throw Corrupt("An old bound receipt requires a read-only SessionJournal proof."); }
        if (!journal.IsReadOnly) { throw Corrupt("Old receipt upgrade requires a read-only SessionJournal handle."); }
        Atelia.EventJournal.EventAddress head = journal.ReadView.ReadCurrentHead()
            ?? throw Corrupt("An old bound receipt requires a non-empty SessionJournal.");
        SessionInputContent input = bound.BoundInput ?? SessionInputContent.Text(
            bound.RenderedObservation ?? throw Corrupt("Old bound receipt has no exact input."));
        var request = new SessionExpectedObservationTurnRequest(head,
            EventAddressTextCodec.Parse(bound.ExpectedSessionHead!), input);
        SessionExpectedObservationTurnReadResult proof = journal.ReadView.ProveExpectedObservationTurnAtSelectedHead(request);
        string? address = proof switch {
            SessionExpectedObservationTurnReadResult.NotAppended => null,
            SessionExpectedObservationTurnReadResult.InProgress progress => EventAddressTextCodec.Format(progress.Evidence.ObservationAddress),
            SessionExpectedObservationTurnReadResult.Terminal terminal => EventAddressTextCodec.Format(terminal.Evidence.ObservationAddress),
            SessionExpectedObservationTurnReadResult.Terminated terminated => EventAddressTextCodec.Format(terminated.Evidence.ObservationAddress),
            _ => throw Corrupt($"Old receipt upgrade requires exact Observation evidence ({proof.GetType().Name}).")
        };
        return [new(bound.SourceActionAddress, bound.StateRevision, bound.ExpectedSessionHead!, input, address)];
    }

    private static void MigrateV4ToV5IfNeeded(SqliteConnection connection,
        CharacterMemoryStoreOwner owner, CharacterMemoryStoreTestHooks hooks,
        IReadOnlyList<LegacyReceiptUpgradeProof> proofs
    ) {
        long version = ReadPragmaInteger(connection, "user_version");
        if (version == 5) { return; }
        if (version != 4) { throw Corrupt("Character Memory receipt preview upgrade requires V4."); }
        const string operation = "migrate-character-memory-v4-to-v5";
        _ = ValidateOpenedDatabase(connection, owner, expectedVersion: 4);
        string preflight = ReadUpgradeAuthorityDigest(connection, 4);
        hooks.AfterValidationBeforeTransaction?.Invoke(operation);
        Exception? uncertain = null;
        string expectedAuthority;
        using (SqliteTransaction transaction = connection.BeginTransaction(deferred: false)) {
            _ = ValidateOpenedDatabase(connection, owner, expectedVersion: 4);
            RequireUpgradeAuthority(preflight, ReadUpgradeAuthorityDigest(connection, 4));
            var sources = new List<string>();
            using (SqliteCommand read = connection.CreateCommand()) {
                read.Transaction = transaction;
                read.CommandText = "SELECT source_action_address FROM note_receipt_delivery ORDER BY created_revision, source_action_address;";
                using SqliteDataReader reader = read.ExecuteReader();
                while (reader.Read()) { sources.Add(reader.GetString(0)); }
            }
            var rows = sources.Select(source => ReadLegacyReceiptDeliveryCore(
                connection, transaction, "source_action_address = $source", source)!).ToArray();
            int boundCount = 0;
            foreach (CharacterNoteReceiptDeliverySnapshot row in rows) {
                if (row.State != CharacterNoteReceiptDeliveryState.ObservationBound) { continue; }
                boundCount++;
                LegacyReceiptUpgradeProof proof = proofs.SingleOrDefault(value => value.SourceActionAddress == row.SourceActionAddress)
                    ?? throw Corrupt("Old bound receipt lacks its preflight proof.");
                SessionInputContent input = row.BoundInput ?? SessionInputContent.Text(row.RenderedObservation!);
                if (row.StateRevision != proof.StateRevision || row.ExpectedSessionHead != proof.ExpectedSessionHead
                    || input != proof.ExactInput) { throw Corrupt("Old bound receipt changed after exact proof."); }
            }
            if (boundCount != proofs.Count) { throw Corrupt("Old receipt commitment set changed after exact proof."); }
            string metaSql;
            using (SqliteCommand read = connection.CreateCommand()) {
                read.Transaction = transaction;
                read.CommandText = "SELECT sql FROM sqlite_schema WHERE name = 'character_memory_meta' AND type = 'table';";
                metaSql = NormalizeSchemaSql((string)read.ExecuteScalar()!).Replace("schema_version = 4", "schema_version = 5", StringComparison.Ordinal);
            }
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "ALTER TABLE character_memory_meta RENAME TO character_memory_meta_v4;" + metaSql + ";" + """
                INSERT INTO character_memory_meta SELECT singleton, 5, user_id, session_repository_id,
                    capture_frontier_segment_number, capture_frontier_tail_offset, baseline_selected_head,
                    store_state, provision_target_pod_state_identity, settled_default_pod_state_identity,
                    active_source_action, active_derived_info_source_action, quarantine_code,
                    quarantine_observed_pod_state_identity, store_revision FROM character_memory_meta_v4;
                DROP TABLE character_memory_meta_v4;
                DROP INDEX ux_note_receipt_single_bound;
                DROP INDEX ix_note_receipt_pending_schedule;
                ALTER TABLE note_receipt_delivery RENAME TO note_receipt_delivery_v4;
                """;
            command.ExecuteNonQuery();
            CreateReceiptDeliverySchema(connection, transaction, version: 5);
            foreach (CharacterNoteReceiptDeliverySnapshot row in rows) {
                LegacyReceiptUpgradeProof? proof = proofs.SingleOrDefault(value => value.SourceActionAddress == row.SourceActionAddress);
                bool delivered = row.State == CharacterNoteReceiptDeliveryState.Delivered || proof?.AppendedObservationAddress is not null;
                long stateRevision = row.State == CharacterNoteReceiptDeliveryState.ObservationBound
                    ? IncrementStoreRevision(connection, transaction) : row.StateRevision;
                byte[]? content = null;
                if (!delivered) {
                    CharacterMemoryCaptureSnapshot capture = ReadCaptureCore(connection, transaction, row.SourceActionAddress)
                        ?? throw Corrupt("Old receipt source capture is absent.");
                    content = SerializeNoteReceiptBatch(FreezeNoteReceiptBatch(capture));
                }
                command.CommandText = """
                    INSERT INTO note_receipt_delivery(source_action_address, state, created_revision,
                        state_revision, receipt_content, expected_session_head, bound_input, observation_address)
                    VALUES($source, $state, $created, $revision, $content, $head, NULL, $address);
                    """;
                command.Parameters.Clear();
                command.Parameters.AddWithValue("$source", row.SourceActionAddress);
                command.Parameters.AddWithValue("$state", delivered ? "Delivered" : "Pending");
                command.Parameters.AddWithValue("$created", row.CreatedRevision);
                command.Parameters.AddWithValue("$revision", stateRevision);
                command.Parameters.AddWithValue("$content", (object?)content ?? DBNull.Value);
                command.Parameters.AddWithValue("$head", delivered ? (object?)row.ExpectedSessionHead ?? DBNull.Value : DBNull.Value);
                command.Parameters.AddWithValue("$address", (object?) (proof?.AppendedObservationAddress ?? row.ObservationAddress) ?? DBNull.Value);
                RequireOne(command.ExecuteNonQuery(), "receipt preview upgrade conversion");
            }
            command.Parameters.Clear();
            command.CommandText = "DROP TABLE note_receipt_delivery_v4; PRAGMA user_version = 5;";
            command.ExecuteNonQuery();
            _ = ValidateOpenedDatabase(connection, owner);
            expectedAuthority = ReadUpgradeAuthorityDigest(connection, 5);
            hooks.BeforeCommit?.Invoke(operation);
            try {
                transaction.Commit();
                hooks.AfterCommitBeforeReturn?.Invoke(operation);
            }
            catch (Exception exception) when (GalateaExceptionClassifier.IsNonFatal(exception)) { uncertain = exception; }
        }
        try {
            _ = ValidateOpenedDatabase(connection, owner);
            RequireUpgradeAuthority(expectedAuthority, ReadUpgradeAuthorityDigest(connection, 5));
        }
        catch (Exception exception) when (uncertain is not null && GalateaExceptionClassifier.IsNonFatal(exception)) {
            throw new CharacterMemoryStoreCommitOutcomeException(operation, new AggregateException(uncertain, exception));
        }
    }
}
