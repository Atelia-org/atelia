using System.Text.Json;
using Atelia.Galatea.Input;
using Atelia.Galatea.Server.Mailbox;
using Atelia.SessionJournal;
using Microsoft.Data.Sqlite;

namespace Atelia.Galatea.Server;

internal sealed partial class GalateaDelegationSqliteStore {
    private const string MailReceiptDeliverySchemaSql = """
        CREATE TABLE mail_receipt_delivery (
            source_action_address TEXT NOT NULL PRIMARY KEY
                REFERENCES action_capture(source_action_address) ON DELETE RESTRICT,
            state TEXT NOT NULL CHECK(state IN ('Pending', 'ObservationBound', 'Delivered')),
            receipt_content BLOB NULL,
            created_revision INTEGER NOT NULL CHECK(created_revision >= 1),
            state_revision INTEGER NOT NULL CHECK(state_revision >= created_revision),
            expected_session_head TEXT NULL,
            bound_input BLOB NULL,
            observation_address TEXT NULL,
            CHECK(
                (state = 'Pending' AND receipt_content IS NOT NULL
                    AND expected_session_head IS NULL AND bound_input IS NULL AND observation_address IS NULL)
                OR (state = 'ObservationBound' AND receipt_content IS NOT NULL
                    AND expected_session_head IS NOT NULL AND bound_input IS NOT NULL AND observation_address IS NULL)
                OR (state = 'Delivered' AND receipt_content IS NULL
                    AND expected_session_head IS NOT NULL AND bound_input IS NULL AND observation_address IS NOT NULL)
            )
        ) STRICT
        """;
    private const string MailReceiptBoundIndexSql = """
        CREATE UNIQUE INDEX ux_mail_receipt_single_bound
        ON mail_receipt_delivery((1)) WHERE state = 'ObservationBound'
        """;
    private const string MailReceiptPendingIndexSql = """
        CREATE INDEX ix_mail_receipt_pending_schedule
        ON mail_receipt_delivery(created_revision, source_action_address) WHERE state = 'Pending'
        """;

    private static void CreateMailReceiptDeliverySchema(
        SqliteConnection connection, SqliteTransaction? transaction = null
    ) {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = MailReceiptDeliverySchemaSql + ";"
            + MailReceiptBoundIndexSql + ";" + MailReceiptPendingIndexSql + ";";
        command.ExecuteNonQuery();
    }

    private static void ValidateMailReceiptDeliverySchema(SqliteConnection connection) {
        RequireExactColumns(connection, "mail_receipt_delivery", [
            "source_action_address", "state", "receipt_content", "created_revision",
            "state_revision", "expected_session_head", "bound_input", "observation_address"
        ]);
        RequireStrictTable(connection, "mail_receipt_delivery");
        RequireExactForeignKeys(connection, "mail_receipt_delivery", [
            "source_action_address->action_capture.source_action_address:RESTRICT"
        ]);
        foreach ((string name, string sql) in new[] {
            ("mail_receipt_delivery", MailReceiptDeliverySchemaSql),
            ("ux_mail_receipt_single_bound", MailReceiptBoundIndexSql),
            ("ix_mail_receipt_pending_schedule", MailReceiptPendingIndexSql)
        }) {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT sql FROM sqlite_schema WHERE name = $name;";
            command.Parameters.AddWithValue("$name", name);
            string? actual = command.ExecuteScalar() as string;
            static string Normalize(string value) => string.Join(" ",
                value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).TrimEnd(';');
            if (actual is null || Normalize(actual) != Normalize(sql)) {
                throw Corrupt("Mail receipt delivery schema definition is not exact.");
            }
        }
    }

    ActionReceiptDeliverySnapshot? IActionReceiptDeliveryStore.ReadPendingReceiptDelivery() => ReadPendingReceiptDelivery();
    ActionReceiptDeliverySnapshot? IActionReceiptDeliveryStore.ReadBoundReceiptDelivery() => ReadBoundReceiptDelivery();
    ActionReceiptDeliverySnapshot? IActionReceiptDeliveryStore.ReadReceiptDeliveryExact(string source) => ReadReceiptDeliveryExact(source);
    ActionReceiptDeliverySnapshot IActionReceiptDeliveryStore.BindReceiptDelivery(
        string source, long revision, string head, SessionInputContent input
    ) => BindReceiptDelivery(source, revision, head, input);
    ActionReceiptDeliverySnapshot IActionReceiptDeliveryStore.RollbackReceiptDelivery(
        string source, long revision
    ) => RollbackReceiptDelivery(source, revision);
    ActionReceiptDeliverySnapshot IActionReceiptDeliveryStore.CompleteReceiptDelivery(
        string source, long revision, string address
    ) => CompleteReceiptDelivery(source, revision, address);

    internal ActionReceiptDeliverySnapshot? ReadPendingReceiptDelivery() =>
        ReadSnapshot().MailReceipts.FirstOrDefault(static row => row.State == ActionReceiptDeliveryState.Pending);

    internal ActionReceiptDeliverySnapshot? ReadBoundReceiptDelivery() =>
        ReadSnapshot().MailReceipts.SingleOrDefault(static row => row.State == ActionReceiptDeliveryState.ObservationBound);

    internal ActionReceiptDeliverySnapshot? ReadReceiptDeliveryExact(string source) {
        RequireEventAddress(source, nameof(source));
        return ReadSnapshot().MailReceipts.SingleOrDefault(row => row.SourceActionAddress == source);
    }

    internal ActionReceiptDeliverySnapshot BindReceiptDelivery(
        string source, long expectedRevision, string expectedHead, SessionInputContent observation
    ) {
        RequireEventAddress(expectedHead, nameof(expectedHead));
        RequireNewBoundObservation(observation);
        if (observation.ToUtf8Json().Length > GalateaDelegationStateBounds.MaximumObservationUtf8Bytes) {
            throw new ArgumentOutOfRangeException(nameof(observation));
        }
        return TransitionMailReceiptDelivery(source, expectedRevision,
            ActionReceiptDeliveryState.Pending, ActionReceiptDeliveryState.ObservationBound,
            expectedHead, observation, null);
    }

    internal ActionReceiptDeliverySnapshot RollbackReceiptDelivery(string source, long expectedRevision) =>
        TransitionMailReceiptDelivery(source, expectedRevision, ActionReceiptDeliveryState.ObservationBound,
            ActionReceiptDeliveryState.Pending, null, null, null);

    internal ActionReceiptDeliverySnapshot CompleteReceiptDelivery(
        string source, long expectedRevision, string observationAddress
    ) {
        RequireEventAddress(observationAddress, nameof(observationAddress));
        return TransitionMailReceiptDelivery(source, expectedRevision, ActionReceiptDeliveryState.ObservationBound,
            ActionReceiptDeliveryState.Delivered, null, null, observationAddress);
    }

    private ActionReceiptDeliverySnapshot TransitionMailReceiptDelivery(
        string source, long expectedRevision, ActionReceiptDeliveryState from, ActionReceiptDeliveryState to,
        string? head, SessionInputContent? input, string? address
    ) {
        RequireEventAddress(source, nameof(source));
        if (expectedRevision < 1) { throw new ArgumentOutOfRangeException(nameof(expectedRevision)); }
        lock (_gate) {
            ThrowIfNotWritable();
            return ExecuteWrite("transition-mail-receipt-delivery", (connection, transaction) => {
                GalateaDelegationStateSnapshot before = ReadSnapshotCore(connection, transaction);
                ActionReceiptDeliverySnapshot current = before.MailReceipts.SingleOrDefault(row => row.SourceActionAddress == source)
                    ?? throw Conflict("Mail receipt delivery is absent.");
                if (current.State != from || current.StateRevision != expectedRevision) {
                    throw Conflict("Mail receipt delivery handle is stale.");
                }
                if (to == ActionReceiptDeliveryState.ObservationBound) {
                    ActionReceiptDeliverySnapshot? earliest = before.MailReceipts.FirstOrDefault(
                        static row => row.State == ActionReceiptDeliveryState.Pending);
                    if (earliest?.SourceActionAddress != source) { throw Conflict("Only the earliest Pending Mail receipt may bind."); }
                    if (before.MailReceipts.Any(static row => row.State == ActionReceiptDeliveryState.ObservationBound)) {
                        throw Conflict("Another Mail receipt delivery is already bound.");
                    }
                    RequireMailReceiptInput(input!, current);
                }
                if (to == ActionReceiptDeliveryState.Delivered) { head = current.ExpectedSessionHead; }
                long revision = IncrementStoreRevision(connection, transaction);
                using SqliteCommand update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE mail_receipt_delivery SET state = $state, state_revision = $revision,
                        receipt_content = CASE WHEN $state = 'Delivered' THEN NULL ELSE receipt_content END,
                        expected_session_head = $head, bound_input = $input, observation_address = $address
                    WHERE source_action_address = $source AND state = $from AND state_revision = $expected;
                    """;
                update.Parameters.AddWithValue("$state", to.ToString());
                update.Parameters.AddWithValue("$revision", revision);
                update.Parameters.AddWithValue("$head", (object?)head ?? DBNull.Value);
                update.Parameters.AddWithValue("$input", (object?)input?.ToUtf8Json() ?? DBNull.Value);
                update.Parameters.AddWithValue("$address", (object?)address ?? DBNull.Value);
                update.Parameters.AddWithValue("$source", source);
                update.Parameters.AddWithValue("$from", from.ToString());
                update.Parameters.AddWithValue("$expected", expectedRevision);
                RequireOne(update.ExecuteNonQuery(), "Mail receipt delivery transition");
                return current with {
                    State = to, StateRevision = revision,
                    FrozenBatch = to == ActionReceiptDeliveryState.Delivered ? null : current.FrozenBatch,
                    ExpectedSessionHead = head, BoundInput = input, ObservationAddress = address
                };
            }, (snapshot, result) => MailReceiptEquivalent(
                snapshot.MailReceipts.SingleOrDefault(row => row.SourceActionAddress == source), result));
        }
    }

    private static IReadOnlyList<ActionReceiptDeliverySnapshot> ReadMailReceiptDeliveries(
        SqliteConnection connection, SqliteTransaction? transaction
    ) {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT source_action_address, state, receipt_content, created_revision, state_revision,
                   expected_session_head, bound_input, observation_address
            FROM mail_receipt_delivery ORDER BY created_revision, source_action_address;
            """;
        var rows = new List<ActionReceiptDeliverySnapshot>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read()) {
            ActionReceiptBatch? batch = null;
            SessionInputContent? input = null;
            try {
                if (!reader.IsDBNull(2)) {
                    byte[] bytes = reader.GetFieldValue<byte[]>(2);
                    if (bytes.Length > GalateaObservationLimits.MaximumActionReceiptNoticesUtf8Bytes) {
                        throw Corrupt("Frozen Mail receipt exceeds its byte limit.");
                    }
                    string json = StrictUtf8.GetString(bytes);
                    batch = ActionReceiptBatchCodec.ReadFrozen(json);
                    if (ActionReceiptBatchCodec.SerializeFrozen(batch) != json) {
                        throw Corrupt("Frozen Mail receipt is not canonical.");
                    }
                }
                if (!reader.IsDBNull(6)) {
                    byte[] bytes = reader.GetFieldValue<byte[]>(6);
                    if (bytes.Length > GalateaDelegationStateBounds.MaximumObservationUtf8Bytes) {
                        throw Corrupt("Bound Mail receipt input exceeds its byte limit.");
                    }
                    input = JsonSerializer.Deserialize<SessionInputContent>(bytes, new JsonSerializerOptions { MaxDepth = 128 })
                        ?? throw Corrupt("Bound Mail receipt input is null.");
                    if (!bytes.SequenceEqual(input.ToUtf8Json())) { throw Corrupt("Bound Mail receipt input is not canonical."); }
                    RequireNewBoundObservation(input);
                }
            }
            catch (Exception exception) when (exception is ArgumentException or JsonException or InvalidOperationException) {
                throw Corrupt("Invalid frozen Mail receipt or bound input.", exception);
            }
            rows.Add(new ActionReceiptDeliverySnapshot(reader.GetString(0),
                ParseExact<ActionReceiptDeliveryState>(reader.GetString(1)),
                reader.GetInt64(3), reader.GetInt64(4), batch,
                ReadNullableString(reader, 5), input, ReadNullableString(reader, 7)));
        }
        return rows;
    }

    private static void ValidateMailReceiptDeliveries(
        IReadOnlyList<ActionReceiptDeliverySnapshot> rows,
        IReadOnlyList<GalateaActionCaptureSnapshot> captures,
        IReadOnlyList<GalateaOutboundMailSnapshot> mails,
        IReadOnlyList<GalateaInternalMailOutboxSnapshot> outboxes, long storeRevision
    ) {
        if (rows.Count(static row => row.State == ActionReceiptDeliveryState.ObservationBound) > 1) {
            throw Corrupt("Multiple Mail receipt deliveries are bound.");
        }
        if (rows.SingleOrDefault(static row => row.State == ActionReceiptDeliveryState.ObservationBound) is { } bound
            && rows.Any(row => row.State == ActionReceiptDeliveryState.Pending && row.CreatedRevision < bound.CreatedRevision)) {
            throw Corrupt("Bound Mail receipt is not the earliest pending obligation.");
        }
        foreach (ActionReceiptDeliverySnapshot row in rows) {
            GalateaActionCaptureSnapshot? capture = captures.SingleOrDefault(value => value.SourceActionAddress == row.SourceActionAddress);
            if (capture is null || capture.ArtifactCount == 0 || row.CreatedRevision != capture.CaptureSequence
                || row.StateRevision < row.CreatedRevision || row.StateRevision > storeRevision
                || row.State != ActionReceiptDeliveryState.Pending && row.StateRevision == row.CreatedRevision) {
                throw Corrupt("Mail receipt does not match its durable capture.");
            }
            bool shape = row.State switch {
                ActionReceiptDeliveryState.Pending => row.FrozenBatch is not null && row.ExpectedSessionHead is null
                    && row.BoundInput is null && row.ObservationAddress is null,
                ActionReceiptDeliveryState.ObservationBound => row.FrozenBatch is not null && IsCanonicalAddress(row.ExpectedSessionHead)
                    && row.BoundInput is not null && row.ObservationAddress is null,
                ActionReceiptDeliveryState.Delivered => row.FrozenBatch is null && IsCanonicalAddress(row.ExpectedSessionHead)
                    && row.BoundInput is null && IsCanonicalAddress(row.ObservationAddress),
                _ => false
            };
            if (!shape) { throw Corrupt("Mail receipt delivery evidence is invalid."); }
            if (row.FrozenBatch is { } frozen) {
                if (frozen is not MailReceiptBatch batch || batch.SourceActionAddress != row.SourceActionAddress
                    || batch.Items.Count != capture.ArtifactCount) { throw Corrupt("Mail receipt batch identity is invalid."); }
                batch.RequireFrozen();
                GalateaOutboundMailSnapshot[] artifacts = mails.Where(value => value.SourceActionAddress == row.SourceActionAddress)
                    .OrderBy(static value => value.ArtifactOrdinal).ToArray();
                for (int ordinal = 0; ordinal < artifacts.Length; ordinal++) {
                    GalateaOutboundMailSnapshot mail = artifacts[ordinal];
                    MailReceiptItem item = batch.Items[ordinal];
                    string outcome = mail.IsCodexRouted || outboxes.Any(value => value.DispatchId == mail.DispatchId) ? "accepted" : "unrouted";
                    if (item.DispatchId != mail.DispatchId || item.Outcome != outcome
                        || item.RecipientPreview != ActionReceiptPreview.Create(mail.Recipient)
                        || mail.Body is { } body && item.Preview != ActionReceiptPreview.Create(body)) {
                        throw Corrupt("Frozen Mail receipt differs from its confirmed artifacts.");
                    }
                }
            }
            if (row.BoundInput is { } input) { RequireMailReceiptInput(input, row); }
        }
    }

    private static void RequireMailReceiptInput(SessionInputContent input, ActionReceiptDeliverySnapshot row) {
        RequireNewBoundObservation(input);
        PlayerTurnObservation observation = GalateaObservationContent.ReadPlayerTurn(input);
        PlayerTurnNotice.ActionReceipt[] receipts = observation.Notices.OfType<PlayerTurnNotice.ActionReceipt>()
            .Where(static notice => notice.Batch is MailReceiptBatch).ToArray();
        if (receipts.Length != 1 || row.FrozenBatch is not MailReceiptBatch frozen
            || !frozen.MatchesProjection(receipts[0].Batch)) {
            throw Corrupt("Bound Mail receipt differs from its frozen batch.");
        }
    }

    private static MailReceiptBatch CreateCapturedMailReceiptBatch(
        GalateaDelegationCaptureRequest request, IReadOnlyList<string> dispatchIds
    ) => new(request.SourceActionAddress, request.Intents.Select((intent, ordinal) => new MailReceiptItem(
        dispatchIds[ordinal], string.Equals(intent.Recipient, GalateaDelegateConfigReader.CanonicalRecipient, StringComparison.Ordinal)
            || request.InternalTargets?[ordinal] is not null ? "accepted" : "unrouted",
        ActionReceiptPreview.Create(intent.Recipient), ActionReceiptPreview.Create(intent.Body))).ToArray());

    private static void InsertPendingMailReceiptDelivery(
        SqliteConnection connection, SqliteTransaction transaction, MailReceiptBatch batch, long revision
    ) {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO mail_receipt_delivery(source_action_address, state, receipt_content,
                created_revision, state_revision, expected_session_head, bound_input, observation_address)
            VALUES ($source, 'Pending', $content, $revision, $revision, NULL, NULL, NULL);
            """;
        command.Parameters.AddWithValue("$source", batch.SourceActionAddress);
        command.Parameters.AddWithValue("$content", StrictUtf8.GetBytes(ActionReceiptBatchCodec.SerializeFrozen(batch)));
        command.Parameters.AddWithValue("$revision", revision);
        RequireOne(command.ExecuteNonQuery(), "Mail receipt creation");
    }

    private static bool MailReceiptEquivalent(ActionReceiptDeliverySnapshot? actual, ActionReceiptDeliverySnapshot expected) =>
        actual == expected;

    private static bool CapturedMailBatchPublished(
        GalateaDelegationStateSnapshot snapshot, GalateaDelegationCaptureResult result,
        GalateaDelegationCaptureRequest request, MailReceiptBatch? expectedBatch,
        IReadOnlyList<GalateaInternalMailOutboxSnapshot> expectedInternalOutboxes
    ) {
        if (snapshot.StoreRevision != result.StoreRevision || !snapshot.Captures.Any(value =>
                value.SourceActionAddress == request.SourceActionAddress && value.CaptureSequence == result.StoreRevision
                && value.VisibleActionSha256 == request.VisibleActionSha256 && value.VisibleActionUtf8Bytes == request.VisibleActionUtf8Bytes
                && value.ExtractorContractId == request.ExtractorContractId && value.ArtifactCount == request.Intents.Count && value.Revision == 0)
            || !snapshot.InternalMailOutboxes.Where(value => value.SourceActionAddress == request.SourceActionAddress)
                .SequenceEqual(expectedInternalOutboxes)) { return false; }
        GalateaOutboundMailSnapshot[] mails = snapshot.Mails.Where(value => value.SourceActionAddress == request.SourceActionAddress)
            .OrderBy(static value => value.ArtifactOrdinal).ToArray();
        if (mails.Length != request.Intents.Count) { return false; }
        for (int ordinal = 0; ordinal < mails.Length; ordinal++) {
            SendMailIntent intent = request.Intents[ordinal];
            GalateaOutboundMailSnapshot mail = mails[ordinal];
            bool codex = intent.Recipient == GalateaDelegateConfigReader.CanonicalRecipient;
            if (mail.DispatchId != result.DispatchIds[ordinal] || mail.ArtifactOrdinal != ordinal
                || mail.Recipient != intent.Recipient || mail.Subject != intent.Subject || mail.Body != intent.Body
                || mail.InReplyToMessageId != intent.InReplyToMessageId || mail.EvidenceQuote != intent.EvidenceQuote
                || mail.SenderName != request.Sender.Name || mail.ContentFormat != "semantic-mail-v1"
                || mail.IsCodexRouted != codex || mail.State != (codex ? GalateaDurableMailState.Queued : GalateaDurableMailState.Unrouted)
                || mail.Revision != 0 || mail.OperationId is not null || mail.RequestedThreadId is not null
                || mail.AcceptedThreadId is not null || mail.AcceptedTurnId is not null
                || mail.TerminalFinalSha256 is not null || mail.TerminalStage is not null || mail.TerminalCode is not null
                || mail.RecoveryFailureCount != 0 || mail.RecoveryLastCode is not null || mail.NextRetryAtUnixTimeMilliseconds is not null
                || mail.TaskSha256 is not null || mail.TaskUtf8Bytes is not null
                || snapshot.InternalMailOutboxes.Any(value => value.DispatchId == mail.DispatchId)
                    != (request.InternalTargets?[ordinal] is not null)) { return false; }
        }
        ActionReceiptDeliverySnapshot? actual = snapshot.MailReceipts.SingleOrDefault(row => row.SourceActionAddress == request.SourceActionAddress);
        return expectedBatch is null ? actual is null : MailReceiptEquivalent(actual, new ActionReceiptDeliverySnapshot(
            request.SourceActionAddress, ActionReceiptDeliveryState.Pending, result.StoreRevision, result.StoreRevision,
            expectedBatch, null, null, null));
    }
}
