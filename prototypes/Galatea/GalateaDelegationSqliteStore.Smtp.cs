using Atelia.Galatea.Server.Mailbox;
using Microsoft.Data.Sqlite;

namespace Atelia.Galatea.Server;

internal sealed partial class GalateaDelegationSqliteStore {
    // No INSERT SELECT or backfill: an outbox exists only for a new capture.
    private const string CreateSmtpOutboxSql = """
        CREATE TABLE smtp_mail_outbox (
            dispatch_id TEXT NOT NULL PRIMARY KEY
                REFERENCES outbound_mail(dispatch_id) ON DELETE RESTRICT,
            recipient TEXT NOT NULL,
            from_character_id TEXT NOT NULL,
            sender_account_reference TEXT NOT NULL,
            state TEXT NOT NULL CHECK(state IN (
                'Pending', 'Attempting', 'ProviderAccepted', 'DefiniteFailure', 'OutcomeUnknown'
            )),
            result_code TEXT NULL,
            revision INTEGER NOT NULL CHECK(revision >= 0)
        ) STRICT;
        CREATE INDEX ix_smtp_mail_state ON smtp_mail_outbox(state);
        """;

    private void InsertSmtpMailOutbox(SqliteConnection connection, SqliteTransaction transaction,
        string dispatchId, string recipient, string characterId, string? senderAccountReference) {
        if (!GalateaExternalMailAddress.TryParse(recipient, out var address)) {
            throw new InvalidDataException("SMTP capture requires a single ASCII address.");
        }
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO smtp_mail_outbox(dispatch_id, recipient, from_character_id,
                sender_account_reference, state, result_code, revision)
            VALUES ($dispatch, $recipient, $character, $account, $state, $code, 0);
            """;
        command.Parameters.AddWithValue("$dispatch", dispatchId);
        command.Parameters.AddWithValue("$recipient", address!.Value);
        command.Parameters.AddWithValue("$character", characterId);
        // Host identity, never recipient/body controlled; not a credential path.
        string reference = senderAccountReference ?? GalateaSmtpConfig.Disabled.ReferenceFor(characterId);
        if (!GalateaSmtpConfig.IsReferenceFor(reference, characterId)) {
            throw new InvalidDataException("Invalid SMTP capture binding.");
        }
        command.Parameters.AddWithValue("$account", reference);
        // Policy rejection is a terminal, observable per-mail record in the same capture transaction.
        // Enabling a binding later cannot turn this old rejected mail into a pending send.
        string? blocked = GalateaSmtpConfig.BlockedReason(reference, characterId);
        command.Parameters.AddWithValue("$state", blocked is null ? "Pending" : "DefiniteFailure");
        command.Parameters.AddWithValue("$code", (object?)blocked ?? DBNull.Value);
        // Fault injection at the outbox write boundary, inside the capture transaction.
        _hooks.BeforeSmtpOutboxInsert?.Invoke();
        command.ExecuteNonQuery();
    }

    private static List<GalateaSmtpMailOutboxSnapshot> ReadSmtpMailOutboxes(
        SqliteConnection connection, SqliteTransaction? transaction) {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT s.dispatch_id, m.source_action_address, a.capture_sequence, m.artifact_ordinal,
                s.recipient, s.from_character_id, s.sender_account_reference,
                m.subject, m.body, s.state, s.result_code, s.revision
            FROM smtp_mail_outbox s
            JOIN outbound_mail m ON m.dispatch_id = s.dispatch_id
            JOIN action_capture a ON a.source_action_address = m.source_action_address
            ORDER BY a.capture_sequence, m.artifact_ordinal;
            """;
        var rows = new List<GalateaSmtpMailOutboxSnapshot>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) {
            if (!Enum.TryParse<GalateaSmtpMailState>(reader.GetString(9), out var state)
                || !Enum.IsDefined(state)) { throw Corrupt("Unknown SMTP state."); }
            rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt32(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6), ReadNullableString(reader, 7),
                reader.GetString(8), state, ReadNullableString(reader, 10), reader.GetInt64(11)));
        }
        // Detect rows hidden by a broken join even if foreign_key enforcement was bypassed.
        using var count = connection.CreateCommand();
        count.Transaction = transaction;
        count.CommandText = "SELECT count(*) FROM smtp_mail_outbox;";
        if (Convert.ToInt64(count.ExecuteScalar()) != rows.Count) { throw Corrupt("Orphan SMTP row."); }
        return rows;
    }

    private static void ValidateSmtpMailOutboxes(IReadOnlyList<GalateaSmtpMailOutboxSnapshot> rows,
        IReadOnlyList<GalateaOutboundMailSnapshot> mails,
        IReadOnlyList<GalateaInternalMailOutboxSnapshot> internalRows, GalateaDelegationStoreOwner owner) {
        foreach (var row in rows) {
            var mail = mails.SingleOrDefault(m => m.DispatchId == row.DispatchId);
            if (mail is null || mail.IsCodexRouted || mail.State != GalateaDurableMailState.Unrouted
                || internalRows.Any(r => r.DispatchId == row.DispatchId)
                || row.FromCharacterId != owner.CharacterId || !GalateaSmtpConfig.IsReferenceFor(row.SenderAccountReference, owner.CharacterId)
                || (GalateaSmtpConfig.BlockedReason(row.SenderAccountReference, owner.CharacterId) is { } blocked
                    && (row.State != GalateaSmtpMailState.DefiniteFailure || row.ResultCode != blocked))
                || !GalateaExternalMailAddress.TryParse(mail.Recipient, out var address)
                || address!.Value != row.Recipient || row.Revision < 0
                || (row.State is GalateaSmtpMailState.Pending or GalateaSmtpMailState.Attempting
                    ? row.ResultCode is not null : !GalateaSmtpOutboxConsumer.IsReasonCode(row.ResultCode))) {
                throw Corrupt("SMTP outbox identity or state is invalid.");
            }
        }
    }

    private static bool SmtpTargetsPublished(GalateaDelegationStateSnapshot snapshot,
        GalateaDelegationCaptureResult result, GalateaDelegationCaptureRequest request) =>
        request.Intents.Select((intent, ordinal) => (intent, ordinal)).All(pair =>
            GalateaMailRecipientClassifier.Classify(pair.intent.Recipient, request.InternalTargets?[pair.ordinal], request.Sender.Name)
                != GalateaMailRecipientClass.Email
            || snapshot.SmtpMailOutboxes.Any(row => row.DispatchId == result.DispatchIds[pair.ordinal]));

    internal GalateaSmtpMailOutboxSnapshot? ClaimPendingSmtpMail() {
        lock (_gate) {
            ThrowIfNotWritable();
            var row = ReadSnapshot().SmtpMailOutboxes.FirstOrDefault(r => r.State == GalateaSmtpMailState.Pending);
            if (row is null) { return null; }
            return ChangeSmtpState(row, GalateaSmtpMailState.Attempting, null, allowCommitRecovery: false);
        }
    }

    internal void CompleteSmtpAttempt(string dispatchId, long expectedRevision, GalateaSmtpSendResult result) {
        if (result.State is not (GalateaSmtpMailState.ProviderAccepted or GalateaSmtpMailState.DefiniteFailure
            or GalateaSmtpMailState.OutcomeUnknown) || !GalateaSmtpOutboxConsumer.IsReasonCode(result.Code)) {
            throw new ArgumentException("Invalid SMTP terminal result.", nameof(result));
        }
        lock (_gate) {
            var row = ReadSnapshot().SmtpMailOutboxes.Single(r => r.DispatchId == dispatchId);
            if (row.State != GalateaSmtpMailState.Attempting || row.Revision != expectedRevision) {
                throw new GalateaDelegationStoreConflictException("SMTP attempt has changed.");
            }
            _ = ChangeSmtpState(row, result.State, result.Code);
        }
    }

    // Called only on writable store open, while holding the exclusive lifetime lock.
    // Never on a sweep or consumer construction: that could invalidate a live attempt.
    private void RecoverSmtpAttemptsOnOpen() {
        lock (_gate) {
            foreach (var row in ReadSnapshot().SmtpMailOutboxes.Where(r => r.State == GalateaSmtpMailState.Attempting)) {
                _ = ChangeSmtpState(row, GalateaSmtpMailState.OutcomeUnknown, "PROCESS_RESTART");
            }
        }
    }

    private GalateaSmtpMailOutboxSnapshot ChangeSmtpState(GalateaSmtpMailOutboxSnapshot row,
        GalateaSmtpMailState state, string? code, bool allowCommitRecovery = true) =>
        ExecuteWrite("smtp-" + state, (connection, transaction) => {
            _ = IncrementStoreRevision(connection, transaction);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE smtp_mail_outbox SET state = $next, result_code = $code, revision = revision + 1
                WHERE dispatch_id = $dispatch AND state = $previous AND revision = $revision;
                """;
            command.Parameters.AddWithValue("$next", state.ToString());
            command.Parameters.AddWithValue("$code", (object?)code ?? DBNull.Value);
            command.Parameters.AddWithValue("$dispatch", row.DispatchId);
            command.Parameters.AddWithValue("$previous", row.State.ToString());
            command.Parameters.AddWithValue("$revision", row.Revision);
            RequireOne(command.ExecuteNonQuery(), "SMTP state transition");
            return row with { State = state, ResultCode = code, Revision = checked(row.Revision + 1) };
        }, (snapshot, result) => snapshot.SmtpMailOutboxes.Contains(result), allowCommitRecovery);
}
