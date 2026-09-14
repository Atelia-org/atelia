using System.Text;
using Atelia.Galatea.Server.Mailbox;
using Microsoft.Data.Sqlite;

namespace Atelia.Galatea.Server;

internal sealed partial class GalateaDelegationSqliteStore {
    private static GalateaDelegationStateSnapshot ReadSnapshotCore(
        SqliteConnection connection,
        SqliteTransaction? transaction
    ) {
        GalateaDelegationStoreOwner owner;
        GalateaDelegationStoreBaseline baseline;
        GalateaDelegationStoreLimits limits;
        long storeRevision;
        long nextCompletionSequence;
        using (SqliteCommand command = connection.CreateCommand()) {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT user_id, session_repository_id,
                       capture_frontier_segment_number,
                       capture_frontier_tail_offset,
                       baseline_selected_head,
                       maximum_queued_mails, maximum_task_utf8_bytes,
                       maximum_reply_utf8_bytes, maximum_inbox_replies,
                       maximum_inbox_utf8_bytes, next_completion_sequence,
                       revision
                FROM delegation_meta WHERE singleton = 1;
                """;
            using SqliteDataReader reader = command.ExecuteReader();
            if (!reader.Read()) {
                throw Corrupt("delegation_meta singleton is missing.");
            }
            owner = new GalateaDelegationStoreOwner(
                reader.GetString(0),
                reader.GetString(1)
            );
            try {
                baseline = new GalateaDelegationStoreBaseline(
                    new Atelia.EventJournal.EventJournalPhysicalAppendFrontier(
                        checked((uint)reader.GetInt64(2)),
                        reader.GetInt64(3)
                    ),
                    ReadNullableString(reader, 4)
                );
            }
            catch (Exception exception) when (exception is
                ArgumentOutOfRangeException or OverflowException) {
                throw Corrupt(
                    "delegation_meta physical frontier is invalid.",
                    exception
                );
            }
            limits = new GalateaDelegationStoreLimits(
                reader.GetInt32(5),
                reader.GetInt32(6),
                reader.GetInt32(7),
                reader.GetInt32(8),
                reader.GetInt32(9)
            );
            nextCompletionSequence = reader.GetInt64(10);
            storeRevision = reader.GetInt64(11);
            if (reader.Read()) {
                throw Corrupt("delegation_meta has multiple rows.");
            }
        }

        GalateaRouteBindingSnapshot route = ReadRoute(
            connection,
            transaction
        );
        List<GalateaActionCaptureSnapshot> captures = ReadCaptures(
            connection,
            transaction
        );
        List<GalateaOutboundMailSnapshot> mails = ReadMails(
            connection,
            transaction
        );
        List<GalateaInternalMailOutboxSnapshot> internalMailOutboxes =
            ReadInternalMailOutboxes(connection, transaction);
        List<GalateaReplyNoticeSnapshot> notices = ReadNotices(
            connection,
            transaction
        );
        GalateaReplyLeaseSnapshot? activeLease = ReadActiveLease(
            connection,
            transaction
        );
        RequireOnlyActiveLeaseRows(connection, transaction, activeLease);

        ValidateProjection(
            owner,
            baseline,
            limits,
            storeRevision,
            nextCompletionSequence,
            route,
            captures,
            mails,
            internalMailOutboxes,
            notices,
            activeLease
        );
        return new GalateaDelegationStateSnapshot(
            owner,
            baseline,
            limits,
            storeRevision,
            nextCompletionSequence,
            route,
            GalateaDelegationStateSnapshot.Freeze(captures),
            GalateaDelegationStateSnapshot.Freeze(mails),
            GalateaDelegationStateSnapshot.Freeze(internalMailOutboxes),
            GalateaDelegationStateSnapshot.Freeze(notices),
            activeLease
        );
    }

    private static GalateaRouteBindingSnapshot ReadRoute(
        SqliteConnection connection,
        SqliteTransaction? transaction
    ) {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT state, binding_operation_id, thread_id,
                   active_dispatch_id,
                   quarantine_code, revision
            FROM route_binding WHERE singleton = 1;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read()) {
            throw Corrupt("route_binding singleton is missing.");
        }
        var result = new GalateaRouteBindingSnapshot(
            ParseExact<GalateaDelegationRouteState>(reader.GetString(0)),
            ReadNullableString(reader, 1),
            ReadNullableString(reader, 2),
            ReadNullableString(reader, 3),
            ReadNullableString(reader, 4),
            reader.GetInt64(5)
        );
        if (reader.Read()) {
            throw Corrupt("route_binding has multiple rows.");
        }
        return result;
    }

    private static List<GalateaActionCaptureSnapshot> ReadCaptures(
        SqliteConnection connection,
        SqliteTransaction? transaction
    ) {
        var result = new List<GalateaActionCaptureSnapshot>();
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT source_action_address, capture_sequence,
                   visible_action_sha256,
                   visible_action_utf8_bytes, extractor_contract_id,
                   artifact_count, revision
            FROM action_capture ORDER BY capture_sequence;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read()) {
            result.Add(new GalateaActionCaptureSnapshot(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetString(4),
                reader.GetInt32(5),
                reader.GetInt64(6)
            ));
        }
        return result;
    }

    private static List<GalateaOutboundMailSnapshot> ReadMails(
        SqliteConnection connection,
        SqliteTransaction? transaction
    ) {
        var result = new List<GalateaOutboundMailSnapshot>();
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT mail.dispatch_id, mail.source_action_address,
                   mail.artifact_ordinal, mail.recipient, mail.subject,
                   mail.body, mail.in_reply_to_message_id,
                   mail.evidence_quote, mail.route_class,
                   mail.state,
                   mail.operation_id, mail.requested_thread_id,
                   mail.accepted_thread_id, mail.accepted_turn_id,
                   mail.terminal_final_sha256, mail.terminal_stage,
                   mail.terminal_code, mail.recovery_failure_count,
                   mail.recovery_last_code, mail.next_retry_at_ms,
                   mail.revision
            FROM outbound_mail AS mail
            JOIN action_capture AS capture
              ON capture.source_action_address = mail.source_action_address
            ORDER BY capture.capture_sequence, mail.artifact_ordinal;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read()) {
            string routeClass = reader.GetString(8);
            if (routeClass is not ("Codex" or "Unrouted")) {
                throw Corrupt("outbound_mail route_class is unknown.");
            }
            result.Add(new GalateaOutboundMailSnapshot(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetString(3),
                ReadNullableString(reader, 4),
                ReadNullableString(reader, 5),
                ReadNullableString(reader, 6),
                ReadNullableString(reader, 7),
                routeClass == "Codex",
                ParseExact<GalateaDurableMailState>(reader.GetString(9)),
                ReadNullableString(reader, 10),
                ReadNullableString(reader, 11),
                ReadNullableString(reader, 12),
                ReadNullableString(reader, 13),
                ReadNullableString(reader, 14),
                ReadNullableString(reader, 15),
                ReadNullableString(reader, 16),
                reader.GetInt32(17),
                ReadNullableString(reader, 18),
                reader.IsDBNull(19) ? null : reader.GetInt64(19),
                reader.GetInt64(20)
            ));
        }
        return result;
    }

    private static List<GalateaInternalMailOutboxSnapshot>
        ReadInternalMailOutboxes(
        SqliteConnection connection,
        SqliteTransaction? transaction
    ) {
        var result = new List<GalateaInternalMailOutboxSnapshot>();
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT outbox.dispatch_id, mail.source_action_address,
                   capture.capture_sequence, mail.artifact_ordinal,
                   outbox.target_user_id, outbox.target_session_repository_id,
                   outbox.from_character_name, outbox.message_id, outbox.state,
                   outbox.expected_session_head, outbox.rendered_observation,
                   outbox.observation_address, outbox.quarantine_code,
                   outbox.revision
            FROM internal_mail_outbox AS outbox
            JOIN outbound_mail AS mail ON mail.dispatch_id = outbox.dispatch_id
            JOIN action_capture AS capture
              ON capture.source_action_address = mail.source_action_address
            ORDER BY capture.capture_sequence, mail.artifact_ordinal;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read()) {
            result.Add(new GalateaInternalMailOutboxSnapshot(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetInt32(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                ParseExact<GalateaInternalMailState>(reader.GetString(8)),
                ReadNullableString(reader, 9),
                ReadNullableString(reader, 10),
                ReadNullableString(reader, 11),
                ReadNullableString(reader, 12),
                reader.GetInt64(13)
            ));
        }
        return result;
    }

    private static List<GalateaReplyNoticeSnapshot> ReadNotices(
        SqliteConnection connection,
        SqliteTransaction? transaction
    ) {
        var result = new List<GalateaReplyNoticeSnapshot>();
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT notice_id, dispatch_id, kind, body, stage, code,
                   completion_sequence, state, consumed_action_address,
                   revision
            FROM reply_notice ORDER BY completion_sequence;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read()) {
            result.Add(new GalateaReplyNoticeSnapshot(
                reader.GetString(0),
                reader.GetString(1),
                ParseExact<GalateaReplyNoticeKind>(reader.GetString(2)),
                reader.GetString(3),
                ReadNullableString(reader, 4),
                ReadNullableString(reader, 5),
                reader.GetInt64(6),
                ParseExact<GalateaReplyNoticeState>(reader.GetString(7)),
                ReadNullableString(reader, 8),
                reader.GetInt64(9)
            ));
        }
        return result;
    }

    private static GalateaReplyLeaseSnapshot? ReadActiveLease(
        SqliteConnection connection,
        SqliteTransaction? transaction
    ) {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT lease_id, state, player_text, expected_session_head,
                   rendered_observation, observation_utf8_bytes,
                   observation_sha256, completion_frontier,
                   observation_address, revision
            FROM reply_lease WHERE active_slot = 1;
            """;
        string leaseId;
        GalateaReplyLeaseState state;
        string playerText;
        string? expectedHead;
        string? observation;
        int? observationBytes;
        string? observationSha;
        long frontier;
        string? observationAddress;
        long revision;
        using (SqliteDataReader reader = command.ExecuteReader()) {
            if (!reader.Read()) { return null; }
            leaseId = reader.GetString(0);
            state = ParseExact<GalateaReplyLeaseState>(reader.GetString(1));
            playerText = reader.GetString(2);
            expectedHead = ReadNullableString(reader, 3);
            observation = ReadNullableString(reader, 4);
            observationBytes = reader.IsDBNull(5) ? null : reader.GetInt32(5);
            observationSha = ReadNullableString(reader, 6);
            frontier = reader.GetInt64(7);
            observationAddress = ReadNullableString(reader, 8);
            revision = reader.GetInt64(9);
            if (reader.Read()) {
                throw Corrupt("Multiple active reply leases exist.");
            }
        }
        return new GalateaReplyLeaseSnapshot(
            leaseId,
            state,
            playerText,
            expectedHead,
            observation,
            observationBytes,
            observationSha,
            frontier,
            observationAddress,
            revision,
            GalateaDelegationStateSnapshot.Freeze(
                ReadLeaseItems(connection, transaction, leaseId)
            )
        );
    }

    private static List<string> ReadLeaseItems(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string leaseId
    ) {
        var result = new List<string>();
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT ordinal, notice_id FROM reply_lease_item
            WHERE lease_id = $lease ORDER BY ordinal;
            """;
        command.Parameters.AddWithValue("$lease", leaseId);
        using SqliteDataReader reader = command.ExecuteReader();
        int expectedOrdinal = 0;
        while (reader.Read()) {
            if (reader.GetInt32(0) != expectedOrdinal++) {
                throw Corrupt("Reply lease item ordinals are not contiguous.");
            }
            result.Add(reader.GetString(1));
        }
        return result;
    }

    private static void RequireOnlyActiveLeaseRows(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        GalateaReplyLeaseSnapshot? activeLease
    ) {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM reply_lease),
                (SELECT COUNT(*) FROM reply_lease_item);
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read()) {
            throw Corrupt("Reply lease row count query failed.");
        }
        long expectedLeases = activeLease is null ? 0 : 1;
        long expectedItems = activeLease?.NoticeIds.Count ?? 0;
        if (reader.GetInt64(0) != expectedLeases
            || reader.GetInt64(1) != expectedItems) {
            throw Corrupt("Inactive reply lease attempt rows are forbidden.");
        }
    }

    private static bool IsCanonicalAddress(string? value) =>
        value is not null
        && Atelia.SessionJournal.EventAddressTextCodec.TryParse(value, out _);

    private static void ValidateProjection(
        GalateaDelegationStoreOwner owner,
        GalateaDelegationStoreBaseline baseline,
        GalateaDelegationStoreLimits limits,
        long storeRevision,
        long nextCompletionSequence,
        GalateaRouteBindingSnapshot route,
        IReadOnlyList<GalateaActionCaptureSnapshot> captures,
        IReadOnlyList<GalateaOutboundMailSnapshot> mails,
        IReadOnlyList<GalateaInternalMailOutboxSnapshot> internalMailOutboxes,
        IReadOnlyList<GalateaReplyNoticeSnapshot> notices,
        GalateaReplyLeaseSnapshot? activeLease
    ) {
        try {
            ValidateOwner(owner);
            ValidateBaseline(baseline);
            ValidateLimits(limits);
        }
        catch (ArgumentException exception) {
            throw Corrupt(
                "Delegation metadata owner, baseline, or limits are invalid.",
                exception
            );
        }
        if (storeRevision < 0 || nextCompletionSequence < 1
            || route.Revision < 0) {
            throw Corrupt("Delegation metadata or route revision is invalid.");
        }
        ValidateRouteShape(route, mails);
        if (captures.Count > GalateaDelegationStateBounds.MaximumCandidateCount
            || mails.Count > GalateaDelegationStateBounds.MaximumCandidateCount) {
            throw Corrupt("Delegation candidate capacity was exceeded.");
        }
        var mailsByAction = mails.GroupBy(static value =>
            value.SourceActionAddress,
            StringComparer.Ordinal
        ).ToDictionary(static group => group.Key, StringComparer.Ordinal);
        long previousCaptureSequence = 0;
        foreach (GalateaActionCaptureSnapshot capture in captures) {
            if (!IsLowerHexSha256(capture.VisibleActionSha256)
                || capture.CaptureSequence <= previousCaptureSequence
                || capture.CaptureSequence > storeRevision
                || capture.VisibleActionUtf8Bytes < 0
                || capture.VisibleActionUtf8Bytes
                    > TextExtractorBounds.MaximumTargetTextUtf8Bytes
                || capture.ArtifactCount is < 0
                    or > GalateaDelegationStateBounds.MaximumCapturedArtifacts
                || capture.Revision < 0
                || !Atelia.SessionJournal.EventAddressTextCodec.TryParse(
                    capture.SourceActionAddress,
                    out _)) {
                throw Corrupt("An action capture row is invalid.");
            }
            try {
                RequireBoundedText(
                    capture.ExtractorContractId,
                    nameof(capture.ExtractorContractId)
                );
            }
            catch (ArgumentException exception) {
                throw Corrupt(
                    "An action capture extractor identity is invalid.",
                    exception
                );
            }
            previousCaptureSequence = capture.CaptureSequence;
            GalateaOutboundMailSnapshot[] actionMails = mailsByAction
                .GetValueOrDefault(capture.SourceActionAddress)?
                .OrderBy(static value => value.ArtifactOrdinal)
                .ToArray() ?? [];
            if (actionMails.Length != capture.ArtifactCount) {
                throw Corrupt("Action artifact_count does not match mail rows.");
            }
            for (int ordinal = 0; ordinal < actionMails.Length; ordinal++) {
                GalateaOutboundMailSnapshot mail = actionMails[ordinal];
                if (mail.ArtifactOrdinal != ordinal) {
                    throw Corrupt("Captured mail ordinals are not contiguous.");
                }
                var address = Atelia.SessionJournal.EventAddressTextCodec
                    .Parse(capture.SourceActionAddress);
                string expectedDispatch = GalateaDelegationDurableContract
                    .CreateDispatchId(owner.UserId, address, ordinal);
                if (!string.Equals(
                        mail.DispatchId,
                        expectedDispatch,
                        StringComparison.Ordinal)) {
                    throw Corrupt("A captured dispatch identity is invalid.");
                }
                ValidateMailShape(mail);
            }
        }
        if (mails.Any(mail => !captures.Any(capture =>
                string.Equals(
                    capture.SourceActionAddress,
                    mail.SourceActionAddress,
                    StringComparison.Ordinal)))) {
            throw Corrupt("An outbound mail has no action capture.");
        }
        ValidateInternalMailOutboxes(mails, internalMailOutboxes);
        long candidateBytes = mails.Sum(static mail => checked(
            (long)StrictUtf8.GetByteCount(mail.Recipient)
            + StrictUtf8.GetByteCount(mail.Subject ?? string.Empty)
            + StrictUtf8.GetByteCount(mail.Body ?? string.Empty)
            + StrictUtf8.GetByteCount(
                mail.InReplyToMessageId ?? string.Empty)
            + StrictUtf8.GetByteCount(mail.EvidenceQuote ?? string.Empty)
            + 128
        ));
        long internalReservation = internalMailOutboxes.Sum(outbox => {
            GalateaOutboundMailSnapshot mail = mails.Single(value =>
                string.Equals(value.DispatchId, outbox.DispatchId,
                    StringComparison.Ordinal));
            return EstimateInternalMailReservation(mail.Recipient,
                mail.Subject, mail.Body!, outbox);
        });
        if (candidateBytes + internalReservation
                > GalateaDelegationStateBounds.MaximumCandidateUtf8Bytes) {
            throw Corrupt("Durable candidate byte capacity was exceeded.");
        }
        long expectedSequence = 1;
        foreach (GalateaReplyNoticeSnapshot notice in notices) {
            if (notice.CompletionSequence != expectedSequence++) {
                throw Corrupt("Reply completion sequence is not contiguous.");
            }
            GalateaOutboundMailSnapshot? mail = mails.FirstOrDefault(value =>
                string.Equals(
                    value.DispatchId,
                    notice.DispatchId,
                    StringComparison.Ordinal
                ));
            if (mail is null
                || mail.State is not (
                    GalateaDurableMailState.TerminalCompleted
                    or GalateaDurableMailState.TerminalFailed)) {
                throw Corrupt("A reply notice has no terminal mail.");
            }
            ValidateNoticeShape(notice, mail, limits);
        }
        if (nextCompletionSequence != expectedSequence) {
            throw Corrupt("next_completion_sequence is inconsistent.");
        }
        if (activeLease is not null) {
            ValidateActiveLease(activeLease, notices);
        }
        int admitted = mails.Count(static value => value.State is
            GalateaDurableMailState.Queued
                or GalateaDurableMailState.Started
                or GalateaDurableMailState.OutcomeUnknown
                or GalateaDurableMailState.Accepted);
        if (admitted > limits.MaximumQueuedMails) {
            throw Corrupt("Durable routed queue exceeds its capacity.");
        }
        ValidateInboxCapacity(limits, route, mails, notices);
    }

    private static void ValidateInternalMailOutboxes(
        IReadOnlyList<GalateaOutboundMailSnapshot> mails,
        IReadOnlyList<GalateaInternalMailOutboxSnapshot> outboxes
    ) {
        if (outboxes.Count > mails.Count
            || outboxes.Select(static value => value.DispatchId)
                .Distinct(StringComparer.Ordinal).Count() != outboxes.Count
            || outboxes.Select(static value => value.MessageId)
                .Distinct(StringComparer.Ordinal).Count() != outboxes.Count) {
            throw Corrupt("Internal mail outbox identities are not unique.");
        }
        foreach (GalateaInternalMailOutboxSnapshot outbox in outboxes) {
            GalateaOutboundMailSnapshot? mail = mails.SingleOrDefault(value =>
                string.Equals(value.DispatchId, outbox.DispatchId,
                    StringComparison.Ordinal));
            if (mail is null || mail.IsCodexRouted
                || !string.Equals(mail.SourceActionAddress,
                    outbox.SourceActionAddress, StringComparison.Ordinal)
                || mail.ArtifactOrdinal != outbox.ArtifactOrdinal
                || outbox.CaptureSequence < 1 || outbox.Revision < 0
                || !IsCanonicalMessageId(outbox.MessageId)) {
                throw Corrupt("An internal mail outbox row has invalid identity.");
            }
            try {
                ValidateInternalMailTarget(new(
                    outbox.TargetUserId,
                    outbox.TargetSessionRepositoryId,
                    outbox.FromCharacterName
                ));
            }
            catch (ArgumentException exception) {
                throw Corrupt("An internal mail target is invalid.", exception);
            }
            bool valid = outbox.State switch {
                GalateaInternalMailState.Pending =>
                    outbox.ExpectedSessionHead is null
                    && outbox.RenderedObservation is null
                    && outbox.ObservationAddress is null
                    && outbox.QuarantineCode is null,
                GalateaInternalMailState.ObservationBound =>
                    IsCanonicalAddress(outbox.ExpectedSessionHead)
                    && outbox.RenderedObservation is not null
                    && outbox.ObservationAddress is null
                    && outbox.QuarantineCode is null,
                GalateaInternalMailState.Delivered =>
                    IsCanonicalAddress(outbox.ExpectedSessionHead)
                    && outbox.RenderedObservation is not null
                    && IsCanonicalAddress(outbox.ObservationAddress)
                    && outbox.QuarantineCode is null,
                GalateaInternalMailState.Quarantined =>
                    IsCanonicalAddress(outbox.ExpectedSessionHead)
                    && outbox.RenderedObservation is not null
                    && outbox.ObservationAddress is null
                    && outbox.QuarantineCode is not null,
                _ => false
            };
            if (!valid) {
                throw Corrupt("An internal mail outbox state shape is invalid.");
            }
            if (outbox.RenderedObservation is not null) {
                try {
                    RequireText(outbox.RenderedObservation,
                        GalateaDelegationStateBounds.MaximumObservationUtf8Bytes,
                        nameof(outbox.RenderedObservation), allowLineBreaks: true);
                }
                catch (ArgumentException exception) {
                    throw Corrupt("An internal mail rendered observation is invalid.", exception);
                }
            }
            if (outbox.QuarantineCode is not null) {
                try { RequireFailureToken(outbox.QuarantineCode, nameof(outbox.QuarantineCode)); }
                catch (ArgumentException exception) { throw Corrupt("An internal mail quarantine code is invalid.", exception); }
            }
        }
    }

    private static bool IsCanonicalMessageId(string? value) => value is { Length: 32 }
        && value.All(static character => character is >= '0' and <= '9'
            or >= 'a' and <= 'f');

    private static void ValidateInboxCapacity(
        GalateaDelegationStoreLimits limits,
        GalateaRouteBindingSnapshot route,
        IReadOnlyList<GalateaOutboundMailSnapshot> mails,
        IReadOnlyList<GalateaReplyNoticeSnapshot> notices
    ) {
        int actualCount = notices.Count(static value => value.State is
            GalateaReplyNoticeState.Ready or GalateaReplyNoticeState.Leased);
        long actualBytes = notices.Where(static value => value.State is
                GalateaReplyNoticeState.Ready or GalateaReplyNoticeState.Leased)
            .Sum(static value => (long)StrictUtf8.GetByteCount(value.Body));
        int reservations = route.ActiveDispatchId is null ? 0 : 1;
        if (reservations == 1
            && !mails.Any(value => string.Equals(
                value.DispatchId,
                route.ActiveDispatchId,
                StringComparison.Ordinal)
                && value.State is GalateaDurableMailState.Started
                    or GalateaDurableMailState.OutcomeUnknown
                    or GalateaDurableMailState.Accepted
                    or GalateaDurableMailState.Quarantined)) {
            throw Corrupt("Inbox reservation has no active mail.");
        }
        int reservationBytes = Math.Max(
            limits.MaximumReplyUtf8Bytes,
            PlayerTurnObservationEnvelope.MaximumFailureUtf8Bytes
        );
        if (actualCount + reservations > limits.MaximumInboxReplies
            || actualBytes + (long)reservations * reservationBytes
                > limits.MaximumInboxUtf8Bytes) {
            throw Corrupt("Inbox actual usage plus reservation exceeds capacity.");
        }
    }

    private static void ValidateRouteShape(
        GalateaRouteBindingSnapshot route,
        IReadOnlyList<GalateaOutboundMailSnapshot> mails
    ) {
        bool valid = route.State switch {
            GalateaDelegationRouteState.Unbound =>
                route.BindingOperationId is null
                && route.ThreadId is null
                && route.ActiveDispatchId is null
                && route.QuarantineCode is null,
            GalateaDelegationRouteState.Binding =>
                route.BindingOperationId is not null
                && route.ThreadId is null
                && route.ActiveDispatchId is null
                && route.QuarantineCode is null
                && mails.Any(mail => mail.State == GalateaDurableMailState.Queued),
            GalateaDelegationRouteState.Bound =>
                route.BindingOperationId is not null
                && route.ThreadId is not null
                && route.QuarantineCode is null,
            GalateaDelegationRouteState.Quarantined =>
                route.QuarantineCode is not null,
            _ => false
        };
        if (!valid) { throw Corrupt("route_binding shape is invalid."); }
        try {
            if (route.BindingOperationId is not null) {
                RequireOperationId(route.BindingOperationId);
            }
            if (route.ThreadId is not null) {
                RequireWireIdentity(route.ThreadId, nameof(route.ThreadId));
            }
            if (route.QuarantineCode is not null) {
                RequireFailureToken(route.QuarantineCode,
                    nameof(route.QuarantineCode));
            }
        }
        catch (Exception exception) when (exception is ArgumentException) {
            throw Corrupt("route_binding contains invalid bounded identity.", exception);
        }
        if (route.ActiveDispatchId is { } active) {
            GalateaOutboundMailSnapshot? mail = mails.FirstOrDefault(value =>
                string.Equals(value.DispatchId, active, StringComparison.Ordinal));
            if (mail is null
                || mail.State is not (
                    GalateaDurableMailState.Started
                    or GalateaDurableMailState.OutcomeUnknown
                    or GalateaDurableMailState.Accepted
                    or GalateaDurableMailState.Quarantined)
                || !string.Equals(
                    mail.RequestedThreadId,
                    route.ThreadId,
                    StringComparison.Ordinal)) {
                throw Corrupt("route active dispatch is inconsistent.");
            }
        }
    }

    private static void ValidateMailShape(
        GalateaOutboundMailSnapshot mail
    ) {
        if (mail.Revision < 0
            || string.IsNullOrWhiteSpace(mail.Recipient)) {
            throw Corrupt("An outbound mail base shape is invalid.");
        }
        bool valid = mail.State switch {
            GalateaDurableMailState.Unrouted =>
                !mail.IsCodexRouted
                && mail.Body is not null
                && mail.EvidenceQuote is not null
                && mail.OperationId is null
                && mail.RecoveryFailureCount == 0
                && mail.RecoveryLastCode is null
                && mail.NextRetryAtUnixTimeMilliseconds is null,
            GalateaDurableMailState.Queued =>
                mail.IsCodexRouted
                && mail.Body is not null
                && mail.EvidenceQuote is not null
                && mail.OperationId is null
                && mail.RequestedThreadId is null
                && mail.AcceptedThreadId is null
                && mail.AcceptedTurnId is null
                && HasValidRecoveryBackoff(mail),
            GalateaDurableMailState.Started =>
                mail.IsCodexRouted
                && mail.Body is not null
                && mail.EvidenceQuote is not null
                && mail.OperationId is not null
                && mail.RequestedThreadId is not null
                && mail.AcceptedThreadId is null
                && mail.AcceptedTurnId is null
                && HasValidRecoveryBackoff(mail),
            GalateaDurableMailState.OutcomeUnknown =>
                mail.IsCodexRouted
                && mail.Body is not null
                && mail.EvidenceQuote is not null
                && mail.OperationId is not null
                && mail.RequestedThreadId is not null
                && mail.AcceptedThreadId is null
                && mail.AcceptedTurnId is null
                && mail.RecoveryFailureCount > 0
                && mail.RecoveryLastCode is not null
                && mail.NextRetryAtUnixTimeMilliseconds is not null,
            GalateaDurableMailState.Accepted =>
                mail.IsCodexRouted
                && mail.Body is not null
                && mail.EvidenceQuote is not null
                && mail.OperationId is not null
                && mail.RequestedThreadId is not null
                && mail.AcceptedThreadId is not null
                && mail.AcceptedTurnId is not null
                && string.Equals(
                    mail.RequestedThreadId,
                    mail.AcceptedThreadId,
                    StringComparison.Ordinal)
                && HasValidRecoveryBackoff(mail),
            GalateaDurableMailState.TerminalCompleted =>
                mail.IsCodexRouted
                && mail.OperationId is not null
                && mail.RequestedThreadId is not null
                && mail.AcceptedThreadId is not null
                && mail.AcceptedTurnId is not null
                && string.Equals(mail.RequestedThreadId,
                    mail.AcceptedThreadId, StringComparison.Ordinal)
                && mail.TerminalFinalSha256 is not null
                && mail.TerminalStage is null
                && mail.TerminalCode is null
                && mail.Body is null
                && mail.EvidenceQuote is null
                && mail.RecoveryFailureCount == 0
                && mail.RecoveryLastCode is null
                && mail.NextRetryAtUnixTimeMilliseconds is null,
            GalateaDurableMailState.TerminalFailed =>
                IsDispatchedTerminalFailure(mail)
                || IsPreflightTaskFailure(mail)
                || IsLocalTerminalFailure(mail),
            GalateaDurableMailState.Quarantined =>
                mail.IsCodexRouted
                && mail.Body is not null
                && mail.EvidenceQuote is not null
                && mail.OperationId is not null
                && mail.RequestedThreadId is not null,
            _ => false
        };
        if (!valid) { throw Corrupt("An outbound mail state shape is invalid."); }
        try {
            ValidateIntent(new SendMailIntent(
                mail.Recipient,
                mail.Subject,
                mail.Body ?? "released",
                mail.InReplyToMessageId,
                mail.EvidenceQuote ?? "released"
            ));
            if (mail.OperationId is not null) {
                RequireDispatchId(mail.OperationId);
                if (!string.Equals(mail.OperationId, mail.DispatchId,
                        StringComparison.Ordinal)) {
                    throw new ArgumentException(
                        "Mail operation ID differs from dispatch ID."
                    );
                }
            }
            if (mail.RequestedThreadId is not null) {
                RequireWireIdentity(mail.RequestedThreadId,
                    nameof(mail.RequestedThreadId));
            }
            if (mail.AcceptedThreadId is not null) {
                RequireWireIdentity(mail.AcceptedThreadId,
                    nameof(mail.AcceptedThreadId));
            }
            if (mail.AcceptedTurnId is not null) {
                RequireWireIdentity(mail.AcceptedTurnId,
                    nameof(mail.AcceptedTurnId));
            }
            if (mail.TerminalFinalSha256 is not null
                && !IsLowerHexSha256(mail.TerminalFinalSha256)) {
                throw new ArgumentException("Terminal final digest is invalid.");
            }
            if (mail.TerminalStage is not null) {
                RequireFailureToken(mail.TerminalStage,
                    nameof(mail.TerminalStage));
            }
            if (mail.TerminalCode is not null) {
                RequireFailureToken(mail.TerminalCode,
                    nameof(mail.TerminalCode));
            }
            if (mail.RecoveryLastCode is not null) {
                RequireFailureToken(mail.RecoveryLastCode,
                    nameof(mail.RecoveryLastCode));
            }
        }
        catch (Exception exception) when (exception is ArgumentException) {
            throw Corrupt("An outbound mail contains invalid bounded data.", exception);
        }
    }

    private static bool IsLocalTerminalFailure(GalateaOutboundMailSnapshot mail) =>
        mail.IsCodexRouted
        && mail.TerminalStage == GalateaDelegationDurableContract.LocalRecoveryStage
        && mail.TerminalCode is not null
        && (mail.OperationId is null) == (mail.RequestedThreadId is null)
        && (mail.AcceptedThreadId is null) == (mail.AcceptedTurnId is null)
        && (mail.AcceptedThreadId is null || mail.AcceptedThreadId == mail.RequestedThreadId)
        && mail.TerminalFinalSha256 is null && mail.Body is null && mail.EvidenceQuote is null
        && mail.NextRetryAtUnixTimeMilliseconds is null
        && (mail.RecoveryFailureCount == 0 ? mail.RecoveryLastCode is null
            : mail.RecoveryFailureCount > 0 && mail.RecoveryLastCode is not null);

    private static bool IsDispatchedTerminalFailure(
        GalateaOutboundMailSnapshot mail
    ) => mail.IsCodexRouted
        && mail.OperationId is not null
        && mail.RequestedThreadId is not null
        && mail.AcceptedThreadId is not null
        && mail.AcceptedTurnId is not null
        && string.Equals(
            mail.RequestedThreadId,
            mail.AcceptedThreadId,
            StringComparison.Ordinal)
        && mail.TerminalStage is not null
        && mail.TerminalCode is not null
        && HasReleasedTerminalPayload(mail);

    private static bool IsPreflightTaskFailure(
        GalateaOutboundMailSnapshot mail
    ) => mail.IsCodexRouted
        && mail.OperationId is null
        && mail.RequestedThreadId is null
        && mail.AcceptedThreadId is null
        && mail.AcceptedTurnId is null
        && string.Equals(
            mail.TerminalStage,
            GalateaDelegationDurableContract.TaskTooLargeStage,
            StringComparison.Ordinal)
        && string.Equals(
            mail.TerminalCode,
            GalateaDelegationDurableContract.TaskTooLargeCode,
            StringComparison.Ordinal)
        && HasReleasedTerminalPayload(mail);

    private static bool HasReleasedTerminalPayload(
        GalateaOutboundMailSnapshot mail
    ) => mail.TerminalFinalSha256 is null
        && mail.Body is null
        && mail.EvidenceQuote is null
        && mail.RecoveryFailureCount == 0
        && mail.RecoveryLastCode is null
        && mail.NextRetryAtUnixTimeMilliseconds is null;

    private static bool HasValidRecoveryBackoff(
        GalateaOutboundMailSnapshot mail
    ) => mail.RecoveryFailureCount switch {
        0 => mail.RecoveryLastCode is null
            && mail.NextRetryAtUnixTimeMilliseconds is null,
        > 0 => mail.RecoveryLastCode is not null
            && mail.NextRetryAtUnixTimeMilliseconds is >= 0,
        _ => false
    };

    private static void ValidateNoticeShape(
        GalateaReplyNoticeSnapshot notice,
        GalateaOutboundMailSnapshot mail,
        GalateaDelegationStoreLimits limits
    ) {
        bool valid = notice.Revision >= 0
            && string.Equals(notice.NoticeId, notice.DispatchId,
                StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(notice.Body)
            && (notice.State switch {
                GalateaReplyNoticeState.Ready
                    or GalateaReplyNoticeState.Leased =>
                    notice.ConsumedActionAddress is null,
                GalateaReplyNoticeState.Consumed =>
                    IsCanonicalAddress(notice.ConsumedActionAddress),
                _ => false
            })
            && notice.Kind switch {
                GalateaReplyNoticeKind.Reply =>
                    mail.State == GalateaDurableMailState.TerminalCompleted
                    && notice.Stage is null
                    && notice.Code is null
                    && string.Equals(
                        ComputeSha256(notice.Body),
                        mail.TerminalFinalSha256,
                        StringComparison.Ordinal),
                GalateaReplyNoticeKind.DeliveryFailure =>
                    mail.State == GalateaDurableMailState.TerminalFailed
                    && notice.Stage is not null
                    && notice.Code is not null
                    && string.Equals(notice.Stage, mail.TerminalStage,
                        StringComparison.Ordinal)
                    && string.Equals(notice.Code, mail.TerminalCode,
                        StringComparison.Ordinal),
                _ => false
            };
        if (!valid) { throw Corrupt("A reply notice shape is invalid."); }
        try {
            RequireDispatchId(notice.NoticeId);
            if (notice.Kind == GalateaReplyNoticeKind.Reply) {
                RequireText(notice.Body, limits.MaximumReplyUtf8Bytes,
                    nameof(notice.Body), allowLineBreaks: true);
            }
            else {
                RequireFailureNoticeBody(notice.Body, nameof(notice.Body));
                RequireFailureToken(notice.Stage!, nameof(notice.Stage));
                RequireFailureToken(notice.Code!, nameof(notice.Code));
            }
        }
        catch (Exception exception) when (exception is ArgumentException) {
            throw Corrupt("A reply notice contains invalid bounded data.", exception);
        }
    }

    private static void ValidateActiveLease(
        GalateaReplyLeaseSnapshot lease,
        IReadOnlyList<GalateaReplyNoticeSnapshot> notices
    ) {
        if (lease.Revision < 0
            || lease.NoticeIds.Count is < 1
                or > GalateaDelegationStateBounds.MaximumReplyNoticeCount) {
            throw Corrupt("Active reply lease shape is invalid.");
        }
        try {
            RequireWireIdentity(lease.LeaseId, nameof(lease.LeaseId));
            string? playerTextError = GalateaHttpV1.ValidateMessage(
                lease.PlayerText
            );
            if (playerTextError is not null) {
                throw new ArgumentException(
                    playerTextError,
                    nameof(lease.PlayerText)
                );
            }
        }
        catch (ArgumentException exception) {
            throw Corrupt("Reply lease player text is invalid.", exception);
        }
        bool hasObservation = lease.ExpectedSessionHead is not null
            && lease.RenderedObservation is not null
            && lease.ObservationUtf8Bytes is not null
            && lease.ObservationSha256 is not null;
        bool shapeValid = lease.State switch {
            GalateaReplyLeaseState.CutoffFrozen => !hasObservation,
            GalateaReplyLeaseState.ObservationBound => hasObservation
                && lease.ObservationAddress is null,
            GalateaReplyLeaseState.ObservationCommitted => hasObservation
                && lease.ObservationAddress is not null,
            GalateaReplyLeaseState.Quarantined => true,
            _ => false
        };
        if (!shapeValid) { throw Corrupt("Active reply lease evidence is invalid."); }
        if ((lease.ExpectedSessionHead is not null
                && !Atelia.SessionJournal.EventAddressTextCodec.TryParse(
                    lease.ExpectedSessionHead,
                    out _))
            || (lease.ObservationAddress is not null
                && !Atelia.SessionJournal.EventAddressTextCodec.TryParse(
                    lease.ObservationAddress,
                    out _))) {
            throw Corrupt("Reply lease contains non-canonical EventAddress evidence.");
        }
        long frontier = 0;
        foreach (string noticeId in lease.NoticeIds) {
            GalateaReplyNoticeSnapshot? notice = notices.FirstOrDefault(value =>
                string.Equals(value.NoticeId, noticeId, StringComparison.Ordinal));
            if (notice is null
                || notice.State != GalateaReplyNoticeState.Leased
                || notice.CompletionSequence <= frontier) {
                throw Corrupt("Reply lease membership is inconsistent.");
            }
            frontier = notice.CompletionSequence;
        }
        if (frontier != lease.CompletionFrontier) {
            throw Corrupt("Reply lease completion frontier is inconsistent.");
        }
        if (hasObservation) {
            int bytes;
            try {
                bytes = StrictUtf8.GetByteCount(lease.RenderedObservation!);
            }
            catch (EncoderFallbackException exception) {
                throw Corrupt("Reply lease Observation is invalid Unicode.", exception);
            }
            if (bytes != lease.ObservationUtf8Bytes
                || !string.Equals(
                    ComputeSha256(lease.RenderedObservation!),
                    lease.ObservationSha256,
                    StringComparison.Ordinal)) {
                throw Corrupt("Reply lease Observation identity is invalid.");
            }
            PlayerTurnNotice[] expectedNotices = ProjectLeaseNotices(
                lease,
                notices
            );
            if (!PlayerTurnObservationEnvelope.TryUnwrap(
                    lease.RenderedObservation,
                    out PlayerTurnObservation parsed)
                || !StoredLeaseObservationMatches(
                    parsed,
                    lease,
                    expectedNotices)) {
                throw Corrupt(
                    "Reply lease Observation is not its canonical cutoff."
                );
            }
        }
    }

    private static string? ReadNullableString(
        SqliteDataReader reader,
        int ordinal
    ) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static T ParseExact<T>(string value) where T : struct, Enum {
        if (!Enum.TryParse(value, ignoreCase: false, out T parsed)
            || !string.Equals(parsed.ToString(), value, StringComparison.Ordinal)) {
            throw Corrupt($"Unknown {typeof(T).Name} value '{value}'.");
        }
        return parsed;
    }

    private static InvalidDataException Corrupt(
        string detail,
        Exception? innerException = null
    ) => new(
        "Delegation SQLite current state is invalid: " + detail,
        innerException
    );
}
