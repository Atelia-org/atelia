using Microsoft.Data.Sqlite;

namespace Atelia.Galatea.Server;

internal sealed partial class GalateaDelegationSqliteStore {
    internal GalateaOutboundMailSnapshot RecordThreadBindingEnsureMiss(
        string bindingOperationId, long expectedRouteRevision,
        string dispatchId, long expectedMailRevision, string code,
        long nowUnixTimeMilliseconds
    ) {
        RequireOperationId(bindingOperationId);
        return RecoverMail("record-thread-binding-ensure-miss", dispatchId,
            expectedMailRevision, expectedRouteRevision, code, nowUnixTimeMilliseconds,
            [GalateaDurableMailState.Queued], requeue: false, resetBinding: false,
            bindingOperationId);
    }

    internal GalateaOutboundMailSnapshot MarkMailOutcomeUnknown(
        string dispatchId, long expectedMailRevision, string code,
        long nowUnixTimeMilliseconds
    ) => RecoverMail("mark-mail-outcome-unknown", dispatchId, expectedMailRevision,
        expectedRouteRevision: null, code, nowUnixTimeMilliseconds,
        [GalateaDurableMailState.Started], requeue: false, resetBinding: false,
        bindingOperationId: null);

    internal GalateaOutboundMailSnapshot RecordMailPollMiss(
        string dispatchId, long expectedMailRevision, string code,
        long nowUnixTimeMilliseconds
    ) => RecoverMail("record-mail-poll-miss", dispatchId, expectedMailRevision,
        expectedRouteRevision: null, code, nowUnixTimeMilliseconds,
        [GalateaDurableMailState.OutcomeUnknown, GalateaDurableMailState.Accepted],
        requeue: false, resetBinding: false, bindingOperationId: null);

    internal GalateaOutboundMailSnapshot RequeueNotDispatchedMail(
        string dispatchId, long expectedMailRevision, long expectedRouteRevision,
        GalateaDelegateDispatchState dispatchState, string code, bool resetBinding,
        long nowUnixTimeMilliseconds
    ) {
        if (dispatchState != GalateaDelegateDispatchState.NotDispatched) {
            throw new ArgumentException("Requeue requires controlled NotDispatched evidence.", nameof(dispatchState));
        }
        return RecoverMail("requeue-not-dispatched-mail", dispatchId, expectedMailRevision,
            expectedRouteRevision, code, nowUnixTimeMilliseconds,
            [GalateaDurableMailState.Started], requeue: true, resetBinding,
            bindingOperationId: null);
    }

    // Every failed recovery step spends the same mail-owned budget. The last
    // failure settles in this transaction; only queued inbox backpressure may
    // leave an exhausted mail for a later local settlement (never another RPC).
    private GalateaOutboundMailSnapshot RecoverMail(
        string operation, string dispatchId, long expectedMailRevision,
        long? expectedRouteRevision, string code, long nowUnixTimeMilliseconds,
        IReadOnlyCollection<GalateaDurableMailState> expectedStates,
        bool requeue, bool resetBinding, string? bindingOperationId
    ) {
        RequireDispatchId(dispatchId);
        RequireFailureToken(code, nameof(code));
        ArgumentOutOfRangeException.ThrowIfNegative(nowUnixTimeMilliseconds);
        if (nowUnixTimeMilliseconds > long.MaxValue - GalateaDelegationDurableContract.MaximumRecoveryBackoffMilliseconds) {
            throw new ArgumentOutOfRangeException(nameof(nowUnixTimeMilliseconds));
        }
        lock (_gate) {
            ThrowIfNotWritable();
            return ExecuteWrite(operation, (connection, transaction) => {
                GalateaRouteBindingSnapshot route = ReadRoute(connection, transaction);
                GalateaOutboundMailSnapshot mail = ReadMailRequired(connection, transaction, dispatchId);
                if (mail.Revision != expectedMailRevision || !expectedStates.Contains(mail.State)
                    || (expectedRouteRevision is { } revision && route.Revision != revision)
                    || mail.RecoveryFailureCount >= GalateaDelegationDurableContract.MaximumRecoveryFailures) {
                    throw Conflict("Mail recovery identity, revision or budget changed.");
                }
                if (bindingOperationId is not null) {
                    RequireRoute(route, GalateaDelegationRouteState.Binding, expectedRouteRevision!.Value);
                    if (route.BindingOperationId != bindingOperationId) {
                        throw Conflict("Thread binding operation identity changed.");
                    }
                    RequireEarliestQueuedMail(connection, transaction, dispatchId);
                }
                else {
                    RequireActiveRecoveryIdentity(route, mail);
                }
                int failures = checked(mail.RecoveryFailureCount + 1);
                long next = checked(nowUnixTimeMilliseconds + ComputeRecoveryDelayMilliseconds(failures));
                GalateaDurableMailState nextState = requeue ? GalateaDurableMailState.Queued
                    : mail.State == GalateaDurableMailState.Started ? GalateaDurableMailState.OutcomeUnknown
                    : mail.State;
                string persistedCode = failures >= GalateaDelegationDurableContract.MaximumRecoveryFailures
                        && nextState == GalateaDurableMailState.Queued
                    ? GalateaDelegationDurableContract.NotDispatchedRetriesExhaustedCode : code;
                _ = IncrementStoreRevision(connection, transaction);
                using (SqliteCommand update = connection.CreateCommand()) {
                    update.Transaction = transaction;
                    update.CommandText = """
                        UPDATE outbound_mail
                        SET state = $state, recovery_failure_count = $count,
                            recovery_last_code = $code, next_retry_at_ms = $next,
                            operation_id = CASE WHEN $requeue THEN NULL ELSE operation_id END,
                            requested_thread_id = CASE WHEN $requeue THEN NULL ELSE requested_thread_id END,
                            accepted_thread_id = CASE WHEN $requeue THEN NULL ELSE accepted_thread_id END,
                            accepted_turn_id = CASE WHEN $requeue THEN NULL ELSE accepted_turn_id END,
                            task_sha256 = CASE WHEN $requeue THEN NULL ELSE task_sha256 END,
                            task_utf8_bytes = CASE WHEN $requeue THEN NULL ELSE task_utf8_bytes END,
                            revision = revision + 1
                        WHERE dispatch_id = $dispatch AND revision = $revision;
                        """;
                    update.Parameters.AddWithValue("$state", nextState.ToString());
                    update.Parameters.AddWithValue("$count", failures);
                    update.Parameters.AddWithValue("$code", persistedCode);
                    update.Parameters.AddWithValue("$next", next);
                    update.Parameters.AddWithValue("$requeue", requeue);
                    update.Parameters.AddWithValue("$dispatch", dispatchId);
                    update.Parameters.AddWithValue("$revision", expectedMailRevision);
                    RequireOne(update.ExecuteNonQuery(), "mail recovery failure");
                }
                GalateaOutboundMailSnapshot updated = ReadMailRequired(connection, transaction, dispatchId);
                if (failures >= GalateaDelegationDurableContract.MaximumRecoveryFailures) {
                    string terminalCode = requeue || mail.State == GalateaDurableMailState.Queued
                        ? GalateaDelegationDurableContract.NotDispatchedRetriesExhaustedCode
                        : GalateaDelegationDurableContract.ResultUnconfirmedCode;
                    try {
                        // A requeued mail still owns its reservation until this
                        // transaction completes. Exchange it directly for notice.
                        _ = FinishMailLocallyCore(connection, transaction, updated, route,
                            terminalCode, resetBinding: terminalCode == GalateaDelegationDurableContract.ResultUnconfirmedCode
                                || resetBinding || route.State == GalateaDelegationRouteState.Binding,
                            alreadyIncrementedStoreRevision: true);
                        return ReadMailRequired(connection, transaction, dispatchId);
                    }
                    catch (GalateaDelegationInboxBackpressureException) when (mail.State == GalateaDurableMailState.Queued) {
                        // No reservation exists yet. Persist the exhausted budget
                        // so the driver cannot perform any further remote calls.
                    }
                }
                if (requeue) {
                    ReleaseRecoveryRoute(connection, transaction, route, resetBinding);
                }
                return updated;
            }, (snapshot, result) => snapshot.Mails.Contains(result));
        }
    }

    internal GalateaReplyNoticeSnapshot FinishMailLocally(
        string dispatchId, long expectedMailRevision, long expectedRouteRevision,
        string code, bool resetBinding,
        GalateaDelegateDispatchState dispatchState = GalateaDelegateDispatchState.MayHaveDispatched
    ) {
        RequireDispatchId(dispatchId);
        RequireFailureToken(code, nameof(code));
        lock (_gate) {
            ThrowIfNotWritable();
            GalateaDelegationInboxBackpressureException? backpressure = null;
            GalateaReplyNoticeSnapshot? result = ExecuteWrite<GalateaReplyNoticeSnapshot?>("finish-mail-locally", (connection, transaction) => {
                GalateaOutboundMailSnapshot mail = ReadMailRequired(connection, transaction, dispatchId);
                // First durable terminal wins even if the caller's old revision
                // or thread was superseded by a newer independent queued task.
                if (mail.State is GalateaDurableMailState.TerminalCompleted or GalateaDurableMailState.TerminalFailed) {
                    return ReadNotices(connection, transaction).Single(value => value.DispatchId == dispatchId);
                }
                GalateaRouteBindingSnapshot route = ReadRoute(connection, transaction);
                if (mail.Revision != expectedMailRevision || route.Revision != expectedRouteRevision) {
                    throw Conflict("Local settlement revision changed.");
                }
                if (mail.State == GalateaDurableMailState.Queued) {
                    RequireEarliestQueuedMail(connection, transaction, dispatchId);
                    if (route.ActiveDispatchId is not null) {
                        throw Conflict("A different mail owns the route reservation.");
                    }
                }
                else {
                    RequireActiveRecoveryIdentity(route, mail, allowQuarantined: true);
                }
                bool knownUnsent = mail.State == GalateaDurableMailState.Queued
                    || (mail.State == GalateaDurableMailState.Started
                        && dispatchState == GalateaDelegateDispatchState.NotDispatched);
                string terminalCode = knownUnsent ? code : GalateaDelegationDurableContract.ResultUnconfirmedCode;
                try {
                    return FinishMailLocallyCore(connection, transaction, mail, route, terminalCode,
                        resetBinding || !knownUnsent, alreadyIncrementedStoreRevision: false);
                }
                catch (GalateaDelegationInboxBackpressureException exception) when (mail.State == GalateaDurableMailState.Queued) {
                    // Save the exact local failure before reporting backpressure.
                    // An exhausted queued mail can only retry local settlement.
                    backpressure = exception;
                    _ = IncrementStoreRevision(connection, transaction);
                    using SqliteCommand pending = connection.CreateCommand();
                    pending.Transaction = transaction;
                    pending.CommandText = """
                        UPDATE outbound_mail SET recovery_failure_count = $maximum,
                            recovery_last_code = $code, next_retry_at_ms = 0,
                            revision = revision + 1
                        WHERE dispatch_id = $dispatch AND revision = $revision;
                        """;
                    pending.Parameters.AddWithValue("$maximum", GalateaDelegationDurableContract.MaximumRecoveryFailures);
                    pending.Parameters.AddWithValue("$code", terminalCode);
                    pending.Parameters.AddWithValue("$dispatch", dispatchId);
                    pending.Parameters.AddWithValue("$revision", mail.Revision);
                    RequireOne(pending.ExecuteNonQuery(), "defer local mail settlement");
                    return null;
                }
            }, (snapshot, notice) => notice is not null ? snapshot.Notices.Contains(notice)
                : snapshot.Mails.Any(mail => mail.DispatchId == dispatchId
                    && mail.State == GalateaDurableMailState.Queued
                    && mail.Revision == checked(expectedMailRevision + 1)
                    && mail.RecoveryFailureCount == GalateaDelegationDurableContract.MaximumRecoveryFailures
                    && mail.RecoveryLastCode == code && mail.NextRetryAtUnixTimeMilliseconds == 0));
            if (result is null) { throw backpressure!; }
            return result;
        }
    }

    private GalateaReplyNoticeSnapshot FinishMailLocallyCore(
        SqliteConnection connection, SqliteTransaction transaction,
        GalateaOutboundMailSnapshot mail, GalateaRouteBindingSnapshot route,
        string code, bool resetBinding, bool alreadyIncrementedStoreRevision
    ) {
        const string stage = GalateaDelegationDurableContract.LocalRecoveryStage;
        string body = string.Empty;
        bool ownsReservation = route.ActiveDispatchId == mail.DispatchId;
        if (ownsReservation) {
            RequireSettledNoticeCapacity(connection, transaction, _limits, mail.DispatchId,
                GalateaReplyNoticeKind.DeliveryFailure, body, stage, code, null,
                mail.AcceptedThreadId ?? mail.RequestedThreadId, mail.AcceptedTurnId);
        }
        if (!ownsReservation) {
            RequireInboxNoticeCapacity(connection, transaction, _limits, body);
        }
        // AllocateCompletionSequence is also the single store revision increment
        // for direct settlement; recovery steps already incremented once above.
        (long sequence, _) = AllocateCompletionSequence(connection, transaction,
            incrementStoreRevision: !alreadyIncrementedStoreRevision);
        using (SqliteCommand update = connection.CreateCommand()) {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE outbound_mail
                SET state = 'TerminalFailed', body = NULL, evidence_quote = NULL,
                    terminal_stage = $stage, terminal_code = $code,
                    terminal_final_sha256 = NULL, next_retry_at_ms = NULL,
                    revision = revision + 1
                WHERE dispatch_id = $dispatch AND revision = $revision;
                INSERT INTO reply_notice(notice_id, dispatch_id, kind, body, stage, code,
                    completion_sequence, state, revision)
                VALUES ($dispatch, $dispatch, 'DeliveryFailure', $body, $stage, $code, $sequence, 'Ready', 0);
                """;
            update.Parameters.AddWithValue("$dispatch", mail.DispatchId);
            update.Parameters.AddWithValue("$revision", mail.Revision);
            update.Parameters.AddWithValue("$stage", stage);
            update.Parameters.AddWithValue("$code", code);
            update.Parameters.AddWithValue("$body", body);
            update.Parameters.AddWithValue("$sequence", sequence);
            if (update.ExecuteNonQuery() != 2) { throw Conflict("Local mail settlement compare-and-swap failed."); }
        }
        if (ownsReservation || resetBinding || route.State == GalateaDelegationRouteState.Binding) {
            ReleaseRecoveryRoute(connection, transaction, route,
                resetBinding || route.State == GalateaDelegationRouteState.Binding);
        }
        return FinalizeSemanticNotice(connection, transaction, mail.DispatchId);
    }

    private static void RequireActiveRecoveryIdentity(
        GalateaRouteBindingSnapshot route, GalateaOutboundMailSnapshot mail,
        bool allowQuarantined = false
    ) {
        if ((route.State != GalateaDelegationRouteState.Bound
                && !(allowQuarantined && route.State == GalateaDelegationRouteState.Quarantined))
            || route.ActiveDispatchId != mail.DispatchId || route.ThreadId is null
            || mail.RequestedThreadId != route.ThreadId
            || mail.State is not (GalateaDurableMailState.Started or GalateaDurableMailState.OutcomeUnknown
                or GalateaDurableMailState.Accepted or GalateaDurableMailState.Quarantined)) {
            throw Conflict("Active recovery mail does not own this route and thread.");
        }
    }

    private static void ReleaseRecoveryRoute(
        SqliteConnection connection, SqliteTransaction transaction,
        GalateaRouteBindingSnapshot route, bool resetBinding
    ) {
        using SqliteCommand update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE route_binding SET
                state = CASE WHEN $reset THEN 'Unbound' ELSE state END,
                binding_operation_id = CASE WHEN $reset THEN NULL ELSE binding_operation_id END,
                thread_id = CASE WHEN $reset THEN NULL ELSE thread_id END,
                quarantine_code = CASE WHEN $reset THEN NULL ELSE quarantine_code END,
                active_dispatch_id = NULL, revision = revision + 1
            WHERE singleton = 1 AND revision = $revision;
            """;
        update.Parameters.AddWithValue("$reset", resetBinding);
        update.Parameters.AddWithValue("$revision", route.Revision);
        RequireOne(update.ExecuteNonQuery(), "recovery route release");
    }
}
