using System.Text.Json;
using System.Xml;
using Atelia.Galatea.Server.Mailbox;
using Atelia.SessionJournal;
using Microsoft.Data.Sqlite;

namespace Atelia.Galatea.Server;

internal sealed partial class GalateaDelegationSqliteStore {
    private const string CreateImapSql = """
        CREATE INDEX ix_smtp_auto_display_sender ON smtp_mail_outbox(recipient COLLATE NOCASE);
        CREATE TABLE imap_checkpoint (
            account_reference TEXT NOT NULL PRIMARY KEY,
            uid_validity INTEGER NOT NULL CHECK(uid_validity BETWEEN 1 AND 4294967295),
            scanned_through_uid INTEGER NOT NULL CHECK(scanned_through_uid BETWEEN 0 AND 4294967295),
            baseline_at_ms INTEGER NOT NULL CHECK(baseline_at_ms >= 0),
            blocked_code TEXT NULL,
            revision INTEGER NOT NULL CHECK(revision >= 0)
        ) STRICT;
        CREATE TABLE external_mail_inbox (
            inbox_id INTEGER NOT NULL PRIMARY KEY CHECK(inbox_id >= 1),
            account_reference TEXT NOT NULL REFERENCES imap_checkpoint(account_reference) ON DELETE RESTRICT,
            uid_validity INTEGER NOT NULL CHECK(uid_validity BETWEEN 1 AND 4294967295),
            uid INTEGER NOT NULL CHECK(uid BETWEEN 1 AND 4294967295),
            message_id TEXT NOT NULL CHECK(length(message_id) = 32 AND message_id NOT GLOB '*[^0-9a-f]*'),
            target_character_id TEXT NOT NULL,
            target_session_repository_id TEXT NOT NULL,
            target_character_name TEXT NOT NULL,
            declared_from TEXT NULL,
            subject TEXT NULL,
            body TEXT NULL,
            attachment_count INTEGER NOT NULL CHECK(attachment_count >= 0),
            state TEXT NOT NULL CHECK(state IN ('Pending', 'ObservationBound', 'Observed', 'Rejected', 'Quarantined')),
            expected_session_head TEXT NULL,
            bound_input TEXT NULL,
            observation_address TEXT NULL,
            code TEXT NULL,
            revision INTEGER NOT NULL CHECK(revision >= 0)
        ) STRICT;
        CREATE UNIQUE INDEX ux_external_mail_uid ON external_mail_inbox(account_reference, uid_validity, uid);
        CREATE UNIQUE INDEX ux_external_mail_message_id ON external_mail_inbox(message_id);
        CREATE INDEX ix_external_mail_state ON external_mail_inbox(state, inbox_id);
        """;

    private static void ValidateImapSchema(SqliteConnection connection) {
        RequireExactColumns(connection, "imap_checkpoint", ["account_reference", "uid_validity", "scanned_through_uid", "baseline_at_ms", "blocked_code", "revision"]);
        RequireExactColumns(connection, "external_mail_inbox", ["inbox_id", "account_reference", "uid_validity", "uid", "message_id",
            "target_character_id", "target_session_repository_id", "target_character_name", "declared_from", "subject", "body",
            "attachment_count", "state", "expected_session_head", "bound_input", "observation_address", "code", "revision"]);
        RequireStrictTable(connection, "imap_checkpoint");
        RequireStrictTable(connection, "external_mail_inbox");
        RequireExactForeignKeys(connection, "imap_checkpoint", []);
        RequireExactForeignKeys(connection, "external_mail_inbox", ["account_reference->imap_checkpoint.account_reference:RESTRICT"]);
        RequireExactIndexColumns(connection, "ux_external_mail_uid", ["account_reference", "uid_validity", "uid"]);
        RequireExactIndexColumns(connection, "ux_external_mail_message_id", ["message_id"]);
        RequireExactIndexColumns(connection, "ix_external_mail_state", ["state", "inbox_id"], requireUnique: false);
        RequireExactIndexColumns(connection, "ix_smtp_auto_display_sender", ["recipient"], requireUnique: false);
        using (SqliteCommand shape = connection.CreateCommand()) {
            shape.CommandText = "SELECT partial FROM pragma_index_list('smtp_mail_outbox') WHERE name='ix_smtp_auto_display_sender';";
            if (shape.ExecuteScalar() is not long partial || partial != 0) {
                throw Corrupt("SMTP auto-display index must belong to its outbox and cannot be partial.");
            }
        }
        using SqliteCommand index = connection.CreateCommand();
        index.CommandText = "PRAGMA index_xinfo('ix_smtp_auto_display_sender');";
        using SqliteDataReader reader = index.ExecuteReader();
        int keys = 0;
        while (reader.Read()) {
            if (reader.GetInt32(5) == 0) { continue; }
            if (++keys != 1 || reader.GetString(2) != "recipient"
                || reader.GetString(4) != "NOCASE" || reader.GetInt32(3) != 0) {
                throw Corrupt("SMTP auto-display index collation is invalid.");
            }
        }
        if (keys != 1) { throw Corrupt("SMTP auto-display index key is missing."); }
    }

    internal GalateaImapCheckpointSnapshot? ReadImapCheckpoint(string accountReference) {
        RequireImapReference(accountReference, _owner.CharacterId);
        lock (_gate) {
            ThrowIfDisposed();
            using SqliteConnection connection = OpenVerifiedConnection();
            return ReadImapCheckpointCore(connection, null, accountReference, _owner);
        }
    }

    internal GalateaImapCheckpointSnapshot EstablishImapBaseline(string accountReference, uint uidValidity,
        uint scannedThroughUid, DateTimeOffset baselineAt) {
        RequireImapReference(accountReference, _owner.CharacterId);
        RequireImapUidValidity(uidValidity);
        long at = baselineAt.ToUnixTimeMilliseconds();
        ArgumentOutOfRangeException.ThrowIfNegative(at);
        lock (_gate) {
            return ExecuteImapWrite("imap-baseline", (connection, transaction) => {
                if (ReadImapCheckpointCore(connection, transaction, accountReference, _owner) is { } existing) {
                    return existing; // Never move a successfully committed first baseline forward.
                }
                _ = IncrementStoreRevision(connection, transaction);
                using SqliteCommand insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO imap_checkpoint VALUES($account, $validity, $uid, $at, NULL, 0);
                    """;
                insert.Parameters.AddWithValue("$account", accountReference);
                insert.Parameters.AddWithValue("$validity", (long)uidValidity);
                insert.Parameters.AddWithValue("$uid", (long)scannedThroughUid);
                insert.Parameters.AddWithValue("$at", at);
                RequireOne(insert.ExecuteNonQuery(), "IMAP baseline");
                return new GalateaImapCheckpointSnapshot(accountReference, uidValidity, scannedThroughUid, at, null, 0);
            }, (connection, result) => ReadImapCheckpointCore(connection, null, accountReference, _owner) == result);
        }
    }

    internal GalateaImapCheckpointSnapshot AdvanceImapCheckpoint(GalateaImapCheckpointSnapshot expected, uint uid) {
        RequireImapAdvance(expected, uid);
        lock (_gate) {
            return ExecuteImapWrite("imap-advance", (connection, transaction) => {
                RequireImapCheckpoint(connection, transaction, expected, allowBlocked: false);
                return AdvanceImapCheckpointCore(connection, transaction, expected, uid);
            }, (connection, result) => ReadImapCheckpointCore(connection, null, result.AccountReference, _owner) == result);
        }
    }

    internal GalateaImapCheckpointSnapshot BlockImapCheckpoint(GalateaImapCheckpointSnapshot expected, string code) {
        RequireFailureToken(code, nameof(code));
        lock (_gate) {
            return ExecuteImapWrite("imap-block", (connection, transaction) => {
                RequireImapCheckpoint(connection, transaction, expected, allowBlocked: true);
                if (expected.BlockedCode == code) { return expected; }
                return UpdateImapCheckpointCore(connection, transaction, expected, expected.UidValidity,
                    expected.ScannedThroughUid, expected.BaselineAtUnixTimeMilliseconds, code);
            }, (connection, result) => ReadImapCheckpointCore(connection, null, result.AccountReference, _owner) == result);
        }
    }

    internal GalateaImapCheckpointSnapshot RebaselineImapCheckpoint(GalateaImapCheckpointSnapshot expected,
        uint uidValidity, uint scannedThroughUid, DateTimeOffset baselineAt) {
        RequireImapUidValidity(uidValidity);
        long at = baselineAt.ToUnixTimeMilliseconds();
        ArgumentOutOfRangeException.ThrowIfNegative(at);
        lock (_gate) {
            return ExecuteImapWrite("imap-rebaseline", (connection, transaction) => {
                RequireImapCheckpoint(connection, transaction, expected, allowBlocked: true);
                if (expected.BlockedCode != "IMAP_UIDVALIDITY_CHANGED" || uidValidity == expected.UidValidity) {
                    throw Conflict("IMAP rebaseline requires a changed UIDVALIDITY and its durable block.");
                }
                return UpdateImapCheckpointCore(connection, transaction, expected, uidValidity, scannedThroughUid, at, null);
            }, (connection, result) => ReadImapCheckpointCore(connection, null, result.AccountReference, _owner) == result);
        }
    }

    internal bool DecideImapAdmission(GalateaImapCheckpointSnapshot expected, uint uid, string from,
        IReadOnlyCollection<string> configuredSenders) {
        RequireImapAdvance(expected, uid);
        RequireImapAddress(from);
        ArgumentNullException.ThrowIfNull(configuredSenders);
        if (configuredSenders.Count > 128) { throw new ArgumentOutOfRangeException(nameof(configuredSenders)); }
        foreach (string value in configuredSenders) { RequireImapAddress(value); }
        lock (_gate) {
            GalateaImapCheckpointSnapshot? deniedPoststate = null;
            return ExecuteImapWrite("imap-admission", (connection, transaction) => {
                RequireImapCheckpoint(connection, transaction, expected, allowBlocked: false);
                bool allowed = configuredSenders.Any(value => GalateaExternalMailAddress.SameMailbox(value, from))
                    || ExistsAcceptedOutboundEmail(connection, transaction, from);
                if (!allowed) { deniedPoststate = AdvanceImapCheckpointCore(connection, transaction, expected, uid); }
                return allowed;
            }, (connection, result) => ReadImapCheckpointCore(connection, null, expected.AccountReference, _owner)
                == (result ? expected : deniedPoststate));
        }
    }

    // No subjects, bodies, current endpoint filter or SMTP result-state filter.
    private bool ExistsAcceptedOutboundEmail(SqliteConnection connection, SqliteTransaction transaction, string from) {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT 1 FROM smtp_mail_outbox
            WHERE from_character_id = $character
              AND substr(sender_account_reference, 1, length($prefix)) = $prefix COLLATE BINARY
              AND recipient = $from COLLATE NOCASE
              AND substr(recipient, 1, instr(recipient, '@') - 1) = $local COLLATE BINARY
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$character", _owner.CharacterId);
        command.Parameters.AddWithValue("$prefix", "smtp:" + _owner.CharacterId + ":");
        command.Parameters.AddWithValue("$from", from);
        command.Parameters.AddWithValue("$local", from[..from.IndexOf('@')]);
        return command.ExecuteScalar() is not null;
    }

    internal GalateaExternalMailInboxSnapshot? AcceptImapMail(GalateaImapCheckpointSnapshot expected, uint uid,
        string targetName, string from, string? subject, string body, int attachmentCount) {
        RequireImapAdvance(expected, uid);
        RequireExternalMailPayload(targetName, from, subject, body, attachmentCount);
        lock (_gate) {
            GalateaImapCheckpointSnapshot? poststate = null;
            return ExecuteImapWrite<GalateaExternalMailInboxSnapshot?>("imap-accept", (connection, transaction) => {
                RequireImapCheckpoint(connection, transaction, expected, allowBlocked: false);
                (long count, long bytes) = ReadExternalMailCapacity(connection, transaction);
                long incomingBytes = StrictUtf8.GetByteCount(from) + StrictUtf8.GetByteCount(subject ?? "") + StrictUtf8.GetByteCount(body);
                if (count >= GalateaImapPersistenceBounds.MaximumPendingCount
                    || incomingBytes > GalateaImapPersistenceBounds.MaximumPendingUtf8Bytes - bytes) { return null; }
                GalateaExternalMailInboxSnapshot row = InsertExternalMail(connection, transaction, expected, uid,
                    targetName, from, subject, body, attachmentCount, code: null);
                poststate = AdvanceImapCheckpointCore(connection, transaction, expected, uid);
                return row;
            }, (connection, result) => result is null
                ? ReadImapCheckpointCore(connection, null, expected.AccountReference, _owner) == expected
                : ReadExternalMailCore(connection, null, result.InboxId, _owner) == result
                    && ReadImapCheckpointCore(connection, null, expected.AccountReference, _owner) == poststate);
        }
    }

    internal GalateaExternalMailInboxSnapshot RejectImapMail(GalateaImapCheckpointSnapshot expected, uint uid, string code) {
        RequireImapAdvance(expected, uid);
        RequireFailureToken(code, nameof(code));
        lock (_gate) {
            GalateaImapCheckpointSnapshot? poststate = null;
            return ExecuteImapWrite("imap-reject", (connection, transaction) => {
                RequireImapCheckpoint(connection, transaction, expected, allowBlocked: false);
                GalateaExternalMailInboxSnapshot row = InsertExternalMail(connection, transaction, expected, uid,
                    _owner.CharacterId, null, null, null, 0, code);
                poststate = AdvanceImapCheckpointCore(connection, transaction, expected, uid);
                return row;
            }, (connection, result) => ReadExternalMailCore(connection, null, result.InboxId, _owner) == result
                && ReadImapCheckpointCore(connection, null, expected.AccountReference, _owner) == poststate);
        }
    }

    internal GalateaExternalMailInboxSnapshot? ReadExternalMail(long inboxId) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(inboxId);
        lock (_gate) {
            ThrowIfDisposed();
            using SqliteConnection connection = OpenVerifiedConnection();
            return ReadExternalMailCore(connection, null, inboxId, _owner);
        }
    }

    internal GalateaExternalMailInboxSnapshot? ReadPendingExternalMail() {
        lock (_gate) {
            ThrowIfDisposed();
            using SqliteConnection connection = OpenVerifiedConnection();
            return ReadExternalMailRows(connection, null, _owner, "WHERE state = 'Pending' ORDER BY inbox_id LIMIT 1").FirstOrDefault();
        }
    }

    internal IReadOnlyList<GalateaExternalMailInboxSnapshot> ReadUnsettledExternalMails() {
        lock (_gate) {
            ThrowIfDisposed();
            using SqliteConnection connection = OpenVerifiedConnection();
            // Only proof-bearing blockers. Rejected and historical content are not read.
            return ReadExternalMailRows(connection, null, _owner,
                "WHERE state IN ('ObservationBound', 'Quarantined') ORDER BY inbox_id LIMIT 129");
        }
    }

    internal GalateaImapInboxStatus ReadImapInboxStatus() {
        lock (_gate) {
            ThrowIfDisposed();
            using SqliteConnection connection = OpenVerifiedConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT SUM(state = 'Pending'), SUM(state = 'ObservationBound'), MAX(state = 'Quarantined')
                FROM external_mail_inbox WHERE state IN ('Pending', 'ObservationBound', 'Quarantined');
                """;
            using SqliteDataReader reader = command.ExecuteReader();
            if (!reader.Read()) { throw Corrupt("IMAP status aggregate is missing."); }
            return new(reader.IsDBNull(0) ? 0 : checked((int)reader.GetInt64(0)),
                reader.IsDBNull(1) ? 0 : checked((int)reader.GetInt64(1)), !reader.IsDBNull(2) && reader.GetInt64(2) != 0);
        }
    }

    internal GalateaExternalMailInboxSnapshot BindExternalMailObservation(long inboxId, long expectedRevision,
        string exactBaseHead, SessionInputContent observation) {
        RequireEventAddress(exactBaseHead, nameof(exactBaseHead));
        RequireNewBoundObservation(observation);
        RequireText(EncodeBoundInput(observation), GalateaDelegationStateBounds.MaximumObservationUtf8Bytes,
            nameof(observation), allowLineBreaks: true);
        return ChangeExternalMailObservation("bind-external-mail-observation", inboxId, expectedRevision,
            GalateaExternalMailInboxState.Pending, GalateaExternalMailInboxState.ObservationBound,
            exactBaseHead, observation, null, null);
    }

    internal GalateaExternalMailInboxSnapshot ResetExternalMailObservation(long inboxId, long expectedRevision) =>
        ChangeExternalMailObservation("reset-external-mail-observation", inboxId, expectedRevision,
            GalateaExternalMailInboxState.ObservationBound, GalateaExternalMailInboxState.Pending, null, null, null, null);

    internal GalateaExternalMailInboxSnapshot CompleteExternalMailObservation(long inboxId, long expectedRevision, string observationAddress) {
        RequireEventAddress(observationAddress, nameof(observationAddress));
        return ChangeExternalMailObservation("complete-external-mail-observation", inboxId, expectedRevision,
            GalateaExternalMailInboxState.ObservationBound, GalateaExternalMailInboxState.Observed, null, null, observationAddress, null);
    }

    internal GalateaExternalMailInboxSnapshot QuarantineExternalMailObservation(long inboxId, long expectedRevision, string code) {
        RequireFailureToken(code, nameof(code));
        return ChangeExternalMailObservation("quarantine-external-mail-observation", inboxId, expectedRevision,
            GalateaExternalMailInboxState.ObservationBound, GalateaExternalMailInboxState.Quarantined, null, null, null, code);
    }

    private GalateaExternalMailInboxSnapshot ChangeExternalMailObservation(string operation, long inboxId,
        long expectedRevision, GalateaExternalMailInboxState expectedState, GalateaExternalMailInboxState state,
        string? head, SessionInputContent? observation, string? address, string? code) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(inboxId);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        lock (_gate) {
            return ExecuteImapWrite(operation, (connection, transaction) => {
                GalateaExternalMailInboxSnapshot current = ReadExternalMailCore(connection, transaction, inboxId, _owner)
                    ?? throw Conflict("External mail row is absent.");
                if (current.State != expectedState || current.Revision != expectedRevision) { throw Conflict("External mail observation CAS did not match."); }
                if (observation is not null) { ValidateExternalMailBoundInput(observation, current); }
                bool preserveBound = state is GalateaExternalMailInboxState.Observed or GalateaExternalMailInboxState.Quarantined;
                var result = current with {
                    State = state, ExpectedSessionHead = preserveBound ? current.ExpectedSessionHead : head,
                    BoundInput = preserveBound ? current.BoundInput : observation,
                    ObservationAddress = address, Code = code, Revision = checked(current.Revision + 1)
                };
                _ = IncrementStoreRevision(connection, transaction);
                using SqliteCommand update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE external_mail_inbox SET state = $state, expected_session_head = $head,
                        bound_input = $input, observation_address = $address, code = $code, revision = revision + 1
                    WHERE inbox_id = $id AND state = $previous AND revision = $revision;
                    """;
                update.Parameters.AddWithValue("$state", result.State.ToString());
                update.Parameters.AddWithValue("$head", (object?)result.ExpectedSessionHead ?? DBNull.Value);
                update.Parameters.AddWithValue("$input", result.BoundInput is null ? DBNull.Value : EncodeBoundInput(result.BoundInput));
                update.Parameters.AddWithValue("$address", (object?)result.ObservationAddress ?? DBNull.Value);
                update.Parameters.AddWithValue("$code", (object?)result.Code ?? DBNull.Value);
                update.Parameters.AddWithValue("$id", inboxId);
                update.Parameters.AddWithValue("$previous", expectedState.ToString());
                update.Parameters.AddWithValue("$revision", expectedRevision);
                RequireOne(update.ExecuteNonQuery(), "external mail observation");
                return result;
            }, (connection, result) => ReadExternalMailCore(connection, null, inboxId, _owner) == result);
        }
    }

    private GalateaExternalMailInboxSnapshot InsertExternalMail(SqliteConnection connection, SqliteTransaction transaction,
        GalateaImapCheckpointSnapshot expected, uint uid, string targetName, string? from, string? subject,
        string? body, int attachmentCount, string? code) {
        string messageId = Guid.NewGuid().ToString("N");
        using SqliteCommand insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO external_mail_inbox(account_reference,uid_validity,uid,message_id,
                target_character_id,target_session_repository_id,target_character_name,declared_from,subject,body,
                attachment_count,state,expected_session_head,bound_input,observation_address,code,revision)
            VALUES($account,$validity,$uid,$message,$character,$repository,$name,$from,$subject,$body,
                $attachments,$state,NULL,NULL,NULL,$code,0);
            SELECT last_insert_rowid();
            """;
        insert.Parameters.AddWithValue("$account", expected.AccountReference);
        insert.Parameters.AddWithValue("$validity", (long)expected.UidValidity);
        insert.Parameters.AddWithValue("$uid", (long)uid);
        insert.Parameters.AddWithValue("$message", messageId);
        insert.Parameters.AddWithValue("$character", _owner.CharacterId);
        insert.Parameters.AddWithValue("$repository", _owner.SessionRepositoryId);
        insert.Parameters.AddWithValue("$name", targetName);
        insert.Parameters.AddWithValue("$from", (object?)from ?? DBNull.Value);
        insert.Parameters.AddWithValue("$subject", (object?)subject ?? DBNull.Value);
        insert.Parameters.AddWithValue("$body", (object?)body ?? DBNull.Value);
        insert.Parameters.AddWithValue("$attachments", attachmentCount);
        insert.Parameters.AddWithValue("$state", code is null ? "Pending" : "Rejected");
        insert.Parameters.AddWithValue("$code", (object?)code ?? DBNull.Value);
        long id = Convert.ToInt64(insert.ExecuteScalar());
        return new(id, expected.AccountReference, expected.UidValidity, uid, messageId, _owner.CharacterId,
            _owner.SessionRepositoryId, targetName, from, subject, body, attachmentCount,
            code is null ? GalateaExternalMailInboxState.Pending : GalateaExternalMailInboxState.Rejected,
            null, null, null, code, 0);
    }

    private void RequireImapCheckpoint(SqliteConnection connection, SqliteTransaction transaction,
        GalateaImapCheckpointSnapshot expected, bool allowBlocked) {
        ValidateImapCheckpoint(expected, _owner);
        GalateaImapCheckpointSnapshot? current = ReadImapCheckpointCore(connection, transaction, expected.AccountReference, _owner);
        if (current != expected || (!allowBlocked && current.BlockedCode is not null)) { throw Conflict("IMAP checkpoint CAS did not match a ready checkpoint."); }
    }

    private GalateaImapCheckpointSnapshot AdvanceImapCheckpointCore(SqliteConnection connection, SqliteTransaction transaction,
        GalateaImapCheckpointSnapshot expected, uint uid) => UpdateImapCheckpointCore(connection, transaction, expected,
            expected.UidValidity, uid, expected.BaselineAtUnixTimeMilliseconds, null);

    private GalateaImapCheckpointSnapshot UpdateImapCheckpointCore(SqliteConnection connection, SqliteTransaction transaction,
        GalateaImapCheckpointSnapshot expected, uint validity, uint uid, long at, string? code) {
        _ = IncrementStoreRevision(connection, transaction);
        using SqliteCommand update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE imap_checkpoint SET uid_validity=$validity,scanned_through_uid=$uid,baseline_at_ms=$at,
                blocked_code=$code,revision=revision+1
            WHERE account_reference=$account AND uid_validity=$previousValidity
                AND scanned_through_uid=$previousUid AND revision=$revision;
            """;
        update.Parameters.AddWithValue("$validity", (long)validity);
        update.Parameters.AddWithValue("$uid", (long)uid);
        update.Parameters.AddWithValue("$at", at);
        update.Parameters.AddWithValue("$code", (object?)code ?? DBNull.Value);
        update.Parameters.AddWithValue("$account", expected.AccountReference);
        update.Parameters.AddWithValue("$previousValidity", (long)expected.UidValidity);
        update.Parameters.AddWithValue("$previousUid", (long)expected.ScannedThroughUid);
        update.Parameters.AddWithValue("$revision", expected.Revision);
        RequireOne(update.ExecuteNonQuery(), "IMAP checkpoint");
        return expected with { UidValidity=validity, ScannedThroughUid=uid, BaselineAtUnixTimeMilliseconds=at,
            BlockedCode=code, Revision=checked(expected.Revision+1) };
    }

    // Domain-scoped post-state readback, including uncertain COMMIT. No old mail body snapshot.
    private T ExecuteImapWrite<T>(string operation, Func<SqliteConnection, SqliteTransaction, T> apply,
        Func<SqliteConnection, T, bool> isPublished) {
        ThrowIfNotWritable();
        T result;
        Exception? uncertain = null;
        using (SqliteConnection connection = OpenVerifiedConnection())
        using (SqliteTransaction transaction = connection.BeginTransaction(deferred: false)) {
            result = apply(connection, transaction);
            _hooks.BeforeCommit?.Invoke(operation);
            try {
                transaction.Commit();
                _hooks.AfterCommitBeforeReturn?.Invoke(operation);
            }
            catch (Exception exception) when (GalateaExceptionClassifier.IsNonFatal(exception)) { uncertain = exception; }
        }
        if (uncertain is null) { return result; }
        using SqliteConnection reopened = OpenVerifiedConnection();
        if (isPublished(reopened, result)) { return result; }
        throw new GalateaDelegationCommitOutcomeException(operation, "the exact IMAP post-state was absent after reopen", uncertain);
    }

    private static GalateaImapCheckpointSnapshot? ReadImapCheckpointCore(SqliteConnection connection,
        SqliteTransaction? transaction, string reference, GalateaDelegationStoreOwner owner) {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT account_reference,uid_validity,scanned_through_uid,baseline_at_ms,blocked_code,revision FROM imap_checkpoint WHERE account_reference=$account;";
        command.Parameters.AddWithValue("$account", reference);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read()) { return null; }
        GalateaImapCheckpointSnapshot value = ReadImapCheckpointRow(reader);
        ValidateImapCheckpoint(value, owner);
        if (reader.Read()) { throw Corrupt("Multiple IMAP checkpoint rows share one identity."); }
        return value;
    }

    private static GalateaImapCheckpointSnapshot ReadImapCheckpointRow(SqliteDataReader reader) =>
        new(reader.GetString(0), ReadImapUint(reader, 1, false), ReadImapUint(reader, 2, true), reader.GetInt64(3), ReadNullableString(reader, 4), reader.GetInt64(5));

    private const string ExternalMailSelect = """
        SELECT inbox_id,account_reference,uid_validity,uid,message_id,target_character_id,target_session_repository_id,
            target_character_name,declared_from,subject,body,attachment_count,state,expected_session_head,bound_input,
            observation_address,code,revision FROM external_mail_inbox
        """;

    private static GalateaExternalMailInboxSnapshot? ReadExternalMailCore(SqliteConnection connection,
        SqliteTransaction? transaction, long inboxId, GalateaDelegationStoreOwner owner) {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = ExternalMailSelect + " WHERE inbox_id=$id;";
        command.Parameters.AddWithValue("$id", inboxId);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read()) { return null; }
        GalateaExternalMailInboxSnapshot row = ReadExternalMailRow(reader);
        ValidateExternalMailRow(row, owner);
        if (reader.Read()) { throw Corrupt("Multiple external rows share an inbox id."); }
        return row;
    }

    private static List<GalateaExternalMailInboxSnapshot> ReadExternalMailRows(SqliteConnection connection,
        SqliteTransaction? transaction, GalateaDelegationStoreOwner owner, string suffix) {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = ExternalMailSelect + " " + suffix + ";";
        using SqliteDataReader reader = command.ExecuteReader();
        var rows = new List<GalateaExternalMailInboxSnapshot>();
        while (reader.Read()) {
            var row = ReadExternalMailRow(reader);
            ValidateExternalMailRow(row, owner);
            rows.Add(row);
        }
        return rows;
    }

    private static GalateaExternalMailInboxSnapshot ReadExternalMailRow(SqliteDataReader reader) =>
        new(reader.GetInt64(0), reader.GetString(1), ReadImapUint(reader, 2, false), ReadImapUint(reader, 3, false),
            reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7), ReadNullableString(reader, 8),
            ReadNullableString(reader, 9), ReadNullableString(reader, 10), reader.GetInt32(11),
            ParseExact<GalateaExternalMailInboxState>(reader.GetString(12)), ReadNullableString(reader, 13),
            ReadBoundInput(reader, 14), ReadNullableString(reader, 15), ReadNullableString(reader, 16), reader.GetInt64(17));

    private static (long Count, long Bytes) ReadExternalMailCapacity(SqliteConnection connection, SqliteTransaction? transaction) {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*),COALESCE(SUM(length(CAST(declared_from AS BLOB))+
                length(CAST(COALESCE(subject,'') AS BLOB))+length(CAST(body AS BLOB))),0)
            FROM external_mail_inbox WHERE state IN ('Pending','ObservationBound');
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read()) { throw Corrupt("External mail capacity aggregate is missing."); }
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    private static void ValidateImapDurableState(SqliteConnection connection, SqliteTransaction? transaction,
        GalateaDelegationStoreOwner owner) {
        var checkpoints = new Dictionary<string, GalateaImapCheckpointSnapshot>(StringComparer.Ordinal);
        using (SqliteCommand command = connection.CreateCommand()) {
            command.Transaction = transaction;
            command.CommandText = "SELECT account_reference,uid_validity,scanned_through_uid,baseline_at_ms,blocked_code,revision FROM imap_checkpoint;";
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read()) {
                var checkpoint = ReadImapCheckpointRow(reader);
                ValidateImapCheckpoint(checkpoint, owner);
                if (!checkpoints.TryAdd(checkpoint.AccountReference, checkpoint)) { throw Corrupt("Duplicate IMAP namespace."); }
            }
        }
        // Strict open audits retained immutable content one row at a time. Ordinary
        // snapshots and poll/relay reads never hydrate this historical content.
        using (SqliteCommand command = connection.CreateCommand()) {
            command.Transaction = transaction;
            command.CommandText = ExternalMailSelect + " ORDER BY inbox_id;";
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read()) {
                var row = ReadExternalMailRow(reader);
                ValidateExternalMailRow(row, owner);
                if (!checkpoints.TryGetValue(row.AccountReference, out var checkpoint)
                    || (checkpoint.UidValidity == row.UidValidity && checkpoint.ScannedThroughUid < row.Uid)) {
                    throw Corrupt("External mail row is not covered by its checkpoint.");
                }
            }
        }
        (long count, long bytes) = ReadExternalMailCapacity(connection, transaction);
        if (count > GalateaImapPersistenceBounds.MaximumPendingCount || bytes > GalateaImapPersistenceBounds.MaximumPendingUtf8Bytes) {
            throw Corrupt("External mail active inbox capacity is exceeded.");
        }
    }

    private static void ValidateImapCheckpoint(GalateaImapCheckpointSnapshot value, GalateaDelegationStoreOwner owner) {
        try {
            RequireImapReference(value.AccountReference, owner.CharacterId);
            RequireImapUidValidity(value.UidValidity);
            ArgumentOutOfRangeException.ThrowIfNegative(value.BaselineAtUnixTimeMilliseconds);
            _ = DateTimeOffset.FromUnixTimeMilliseconds(value.BaselineAtUnixTimeMilliseconds);
            ArgumentOutOfRangeException.ThrowIfNegative(value.Revision);
            if (value.BlockedCode is not null) { RequireFailureToken(value.BlockedCode, nameof(value.BlockedCode)); }
        }
        catch (ArgumentException exception) { throw Corrupt("IMAP checkpoint identity or state is invalid.", exception); }
    }

    private static void ValidateExternalMailRow(GalateaExternalMailInboxSnapshot row, GalateaDelegationStoreOwner owner) {
        try {
            RequireImapReference(row.AccountReference, owner.CharacterId);
            if (row.InboxId <= 0 || row.Revision < 0 || row.UidValidity == 0 || row.Uid == 0
                || row.TargetCharacterId != owner.CharacterId || row.TargetSessionRepositoryId != owner.SessionRepositoryId
                || row.MessageId.Length != 32 || row.MessageId.Any(c => !(c is >= '0' and <= '9' or >= 'a' and <= 'f'))) {
                throw Corrupt("External mail identity is invalid.");
            }
            RequireBoundedText(row.TargetCharacterName, nameof(row.TargetCharacterName));
            bool bound = row.ExpectedSessionHead is not null && row.BoundInput is not null;
            bool emptyProof = row.ExpectedSessionHead is null && row.BoundInput is null && row.ObservationAddress is null;
            if (row.State == GalateaExternalMailInboxState.Rejected) {
                if (row.From is not null || row.Subject is not null || row.Body is not null || row.AttachmentCount != 0
                    || !emptyProof || row.Code is null) { throw Corrupt("Rejected external mail contains payload or proof."); }
            } else {
                RequireExternalMailPayload(row.TargetCharacterName, row.From!, row.Subject, row.Body!, row.AttachmentCount);
                bool shape = row.State switch {
                    GalateaExternalMailInboxState.Pending => emptyProof && row.Code is null,
                    GalateaExternalMailInboxState.ObservationBound => bound && row.ObservationAddress is null && row.Code is null,
                    GalateaExternalMailInboxState.Observed => bound && row.ObservationAddress is not null && row.Code is null,
                    GalateaExternalMailInboxState.Quarantined => bound && row.ObservationAddress is null && row.Code is not null,
                    _ => false
                };
                if (!shape) { throw Corrupt("External mail proof state shape is invalid."); }
            }
            if (row.Code is not null) { RequireFailureToken(row.Code, nameof(row.Code)); }
            if (row.ExpectedSessionHead is not null) { RequireEventAddress(row.ExpectedSessionHead, nameof(row.ExpectedSessionHead)); }
            if (row.ObservationAddress is not null) { RequireEventAddress(row.ObservationAddress, nameof(row.ObservationAddress)); }
            if (row.BoundInput is not null) { ValidateExternalMailBoundInput(row.BoundInput, row); }
        }
        catch (Exception exception) when (exception is ArgumentException or XmlException) { throw Corrupt("External mail payload or proof is invalid.", exception); }
    }

    private static void ValidateExternalMailBoundInput(SessionInputContent content, GalateaExternalMailInboxSnapshot row) {
        RequireNewBoundObservation(content);
        JsonElement value = content.JsonValue;
        if (content.SchemaId != GalateaObservationContent.V5SchemaId || value.GetProperty("kind").GetString() != "email-inbound") {
            throw Corrupt("External mail binding requires an email-inbound V5 Observation.");
        }
        GalateaSenderSnapshot sender = GalateaInputContentValidation.ReadSender(value.GetProperty("sender"));
        JsonElement action = value.GetProperty("action");
        if (sender != GalateaObservationContent.RuntimeSender || action.GetProperty("messageId").GetString() != row.MessageId
            || action.GetProperty("from").GetString() != row.From || action.GetProperty("to").GetString() != row.TargetCharacterName
            || action.GetProperty("subject").GetString() != row.Subject || action.GetProperty("body").GetString() != row.Body
            || action.GetProperty("attachmentCount").GetInt32() != row.AttachmentCount) {
            throw Corrupt("External mail binding disagrees with frozen delivery content.");
        }
    }

    private static uint ReadImapUint(SqliteDataReader reader, int ordinal, bool allowZero) {
        long value = reader.GetInt64(ordinal);
        if (value < (allowZero ? 0 : 1) || value > uint.MaxValue) { throw Corrupt("IMAP UID numeric value is invalid."); }
        return (uint)value;
    }

    private static void RequireImapUidValidity(uint value) { ArgumentOutOfRangeException.ThrowIfZero(value); }

    private void RequireImapAdvance(GalateaImapCheckpointSnapshot expected, uint uid) {
        ArgumentNullException.ThrowIfNull(expected);
        ValidateImapCheckpoint(expected, _owner);
        if (uid <= expected.ScannedThroughUid) { throw Conflict("IMAP cursor must advance without replay."); }
    }

    private static void RequireImapReference(string value, string characterId) {
        ArgumentNullException.ThrowIfNull(value);
        string prefix = "imap:" + characterId + ":";
        if (!value.StartsWith(prefix, StringComparison.Ordinal) || !IsLowerHexSha256(value[prefix.Length..])) {
            throw new ArgumentException("Invalid IMAP account reference.", nameof(value));
        }
    }

    private static void RequireImapAddress(string value) {
        if (!GalateaExternalMailAddress.TryParse(value, out var address) || address!.Value != value) {
            throw new ArgumentException("IMAP declared sender requires a canonical single ASCII mailbox address.", nameof(value));
        }
    }

    private static void RequireExternalMailPayload(string targetName, string from, string? subject, string body, int attachmentCount) {
        RequireBoundedText(targetName, nameof(targetName));
        RequireImapAddress(from);
        if (subject is not null) {
            RequireText(subject, GalateaImapPersistenceBounds.MaximumSubjectUtf8Bytes, nameof(subject), allowLineBreaks: false);
            XmlConvert.VerifyXmlChars(subject);
        }
        RequireText(body, GalateaImapPersistenceBounds.MaximumBodyUtf8Bytes, nameof(body), allowLineBreaks: true);
        XmlConvert.VerifyXmlChars(body);
        XmlConvert.VerifyXmlChars(targetName);
        ArgumentOutOfRangeException.ThrowIfNegative(attachmentCount);
    }
}
