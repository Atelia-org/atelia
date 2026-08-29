using System.Text;
using Microsoft.Data.Sqlite;

namespace Atelia.Galatea.Server;

internal sealed partial class GalateaDelegationSqliteStore {
    internal GalateaDelegationCaptureResult CaptureActionBatch(
        GalateaDelegationCaptureRequest request
    ) {
        ValidateCaptureRequest(request);
        lock (_gate) {
            ThrowIfNotWritable();
            using (SqliteConnection connection = OpenVerifiedConnection()) {
                GalateaDelegationCaptureResult? existing =
                    TryReadExistingCapture(connection, request);
                if (existing is not null) { return existing; }
            }

            Atelia.EventJournal.EventAddress sourceAddress =
                Atelia.SessionJournal.EventAddressTextCodec.Parse(
                    request.SourceActionAddress
                );
            string[] dispatchIds = request.Intents
                .Select((_, ordinal) => GalateaDelegationDurableContract
                    .CreateDispatchId(_owner.UserId, sourceAddress, ordinal))
                .ToArray();
            return ExecuteWrite(
                "capture-action-batch",
                (connection, transaction) => {
                    RequireCaptureCapacity(
                        connection,
                        transaction,
                        request,
                        _limits
                    );
                    long storeRevision = IncrementStoreRevision(
                        connection,
                        transaction
                    );
                    using (SqliteCommand capture = connection.CreateCommand()) {
                        capture.Transaction = transaction;
                        capture.CommandText = """
                            INSERT INTO action_capture(
                                source_action_address,
                                capture_sequence,
                                visible_action_sha256,
                                visible_action_utf8_bytes,
                                extractor_contract_id,
                                artifact_count,
                                revision
                            ) VALUES (
                                $source, $sequence, $digest, $bytes, $contract,
                                $count, 0
                            );
                            """;
                        capture.Parameters.AddWithValue(
                            "$source",
                            request.SourceActionAddress
                        );
                        capture.Parameters.AddWithValue(
                            "$sequence",
                            storeRevision
                        );
                        capture.Parameters.AddWithValue(
                            "$digest",
                            request.VisibleActionSha256
                        );
                        capture.Parameters.AddWithValue(
                            "$bytes",
                            request.VisibleActionUtf8Bytes
                        );
                        capture.Parameters.AddWithValue(
                            "$contract",
                            request.ExtractorContractId
                        );
                        capture.Parameters.AddWithValue(
                            "$count",
                            request.Intents.Count
                        );
                        capture.ExecuteNonQuery();
                    }
                    for (int ordinal = 0;
                         ordinal < request.Intents.Count;
                         ordinal++) {
                        InsertCapturedMail(
                            connection,
                            transaction,
                            request.SourceActionAddress,
                            ordinal,
                            dispatchIds[ordinal],
                            request.Intents[ordinal]
                        );
                    }
                    return new GalateaDelegationCaptureResult(
                        GalateaDelegationCaptureDisposition.Captured,
                        storeRevision,
                        GalateaDelegationStateSnapshot.Freeze(dispatchIds)
                    );
                },
                (snapshot, result) => snapshot.StoreRevision
                        == result.StoreRevision
                    && snapshot.Captures.Any(value =>
                        string.Equals(
                            value.SourceActionAddress,
                            request.SourceActionAddress,
                            StringComparison.Ordinal)
                        && value.ArtifactCount == request.Intents.Count)
            );
        }
    }

    internal GalateaRouteBindingSnapshot BeginThreadBinding(
        string bindingOperationId,
        long expectedRouteRevision
    ) {
        RequireOperationId(bindingOperationId);
        lock (_gate) {
            ThrowIfNotWritable();
            return ExecuteWrite(
                "begin-thread-binding",
                (connection, transaction) => {
                    GalateaRouteBindingSnapshot route = ReadRoute(
                        connection,
                        transaction
                    );
                    RequireRoute(
                        route,
                        GalateaDelegationRouteState.Unbound,
                        expectedRouteRevision
                    );
                    using (SqliteCommand queued = connection.CreateCommand()) {
                        queued.Transaction = transaction;
                        queued.CommandText = """
                            SELECT mail.body
                            FROM outbound_mail AS mail
                            JOIN action_capture AS capture
                              ON capture.source_action_address
                                = mail.source_action_address
                            WHERE mail.route_class = 'Codex'
                              AND mail.state = 'Queued'
                            ORDER BY capture.capture_sequence,
                                     mail.artifact_ordinal
                            LIMIT 1;
                            """;
                        string? body = queued.ExecuteScalar() as string;
                        if (body is null) {
                            throw Conflict(
                                "Thread binding requires a queued Codex mail."
                            );
                        }
                        if (StrictUtf8.GetByteCount(body)
                                > _limits.MaximumTaskUtf8Bytes) {
                            throw Conflict(
                                "The FIFO head must settle its durable preflight failure before binding."
                            );
                        }
                    }
                    long storeRevision = IncrementStoreRevision(
                        connection,
                        transaction
                    );
                    using SqliteCommand update = connection.CreateCommand();
                    update.Transaction = transaction;
                    update.CommandText = """
                        UPDATE route_binding
                        SET state = 'Binding',
                            binding_operation_id = $operation,
                            revision = revision + 1
                        WHERE singleton = 1
                          AND state = 'Unbound'
                          AND revision = $revision;
                        """;
                    update.Parameters.AddWithValue("$operation", bindingOperationId);
                    update.Parameters.AddWithValue("$revision", expectedRouteRevision);
                    RequireOne(update.ExecuteNonQuery(), "route binding claim");
                    _ = storeRevision;
                    return route with {
                        State = GalateaDelegationRouteState.Binding,
                        BindingOperationId = bindingOperationId,
                        Revision = checked(route.Revision + 1)
                    };
                },
                (snapshot, result) => snapshot.Route == result
            );
        }
    }

    internal GalateaRouteBindingSnapshot RecordThreadBindingEnsureMiss(
        string bindingOperationId,
        long expectedRouteRevision,
        string code,
        long nowUnixTimeMilliseconds
    ) {
        RequireOperationId(bindingOperationId);
        RequireFailureToken(code, nameof(code));
        ArgumentOutOfRangeException.ThrowIfNegative(nowUnixTimeMilliseconds);
        lock (_gate) {
            ThrowIfNotWritable();
            return ExecuteWrite(
                "record-thread-binding-ensure-miss",
                (connection, transaction) => {
                    GalateaRouteBindingSnapshot route = ReadRoute(
                        connection,
                        transaction
                    );
                    RequireRoute(
                        route,
                        GalateaDelegationRouteState.Binding,
                        expectedRouteRevision
                    );
                    if (!string.Equals(
                            route.BindingOperationId,
                            bindingOperationId,
                            StringComparison.Ordinal)) {
                        throw Conflict(
                            "Thread binding operation identity changed."
                        );
                    }
                    if (nowUnixTimeMilliseconds > long.MaxValue - 300_000
                        || (route.NextEnsureAtUnixTimeMilliseconds is { } due
                            && nowUnixTimeMilliseconds < due)) {
                        throw Conflict(
                            "Thread binding ensure backoff is not due."
                        );
                    }
                    int attempt = checked(route.EnsureAttemptCount + 1);
                    long next = checked(
                        nowUnixTimeMilliseconds
                        + ComputeReconcileDelayMilliseconds(attempt)
                    );
                    _ = IncrementStoreRevision(connection, transaction);
                    using SqliteCommand update = connection.CreateCommand();
                    update.Transaction = transaction;
                    update.CommandText = """
                        UPDATE route_binding
                        SET ensure_attempt_count = $attempt,
                            ensure_last_code = $code,
                            next_ensure_at_ms = $next,
                            revision = revision + 1
                        WHERE singleton = 1
                          AND state = 'Binding'
                          AND binding_operation_id = $operation
                          AND revision = $revision;
                        """;
                    update.Parameters.AddWithValue("$attempt", attempt);
                    update.Parameters.AddWithValue("$code", code);
                    update.Parameters.AddWithValue("$next", next);
                    update.Parameters.AddWithValue(
                        "$operation",
                        bindingOperationId
                    );
                    update.Parameters.AddWithValue(
                        "$revision",
                        expectedRouteRevision
                    );
                    RequireOne(
                        update.ExecuteNonQuery(),
                        "thread binding ensure miss"
                    );
                    return route with {
                        EnsureAttemptCount = attempt,
                        EnsureLastCode = code,
                        NextEnsureAtUnixTimeMilliseconds = next,
                        Revision = checked(route.Revision + 1)
                    };
                },
                (snapshot, result) => snapshot.Route == result
            );
        }
    }

    private void QuarantineRouteForTerminalConflict(
        GalateaRouteBindingSnapshot route
    ) {
        const string code = "TERMINAL_EVIDENCE_CONFLICT";
        if (route.State != GalateaDelegationRouteState.Bound
            || route.ActiveDispatchId is not null) {
            return;
        }
        _ = ExecuteWrite(
            "quarantine-terminal-conflict",
            (connection, transaction) => {
                _ = IncrementStoreRevision(connection, transaction);
                using SqliteCommand update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE route_binding
                    SET state = 'Quarantined', quarantine_code = $code,
                        revision = revision + 1
                    WHERE singleton = 1 AND state = 'Bound'
                      AND active_dispatch_id IS NULL
                      AND revision = $revision;
                    """;
                update.Parameters.AddWithValue("$code", code);
                update.Parameters.AddWithValue("$revision", route.Revision);
                RequireOne(update.ExecuteNonQuery(),
                    "terminal conflict quarantine");
                return checked(route.Revision + 1);
            },
            (snapshot, revision) => snapshot.Route.State
                    == GalateaDelegationRouteState.Quarantined
                && snapshot.Route.Revision == revision
                && string.Equals(snapshot.Route.QuarantineCode, code,
                    StringComparison.Ordinal)
        );
    }

    internal GalateaRouteBindingSnapshot CompleteThreadBinding(
        string bindingOperationId,
        string threadId,
        long expectedRouteRevision
    ) {
        RequireOperationId(bindingOperationId);
        RequireWireIdentity(threadId, nameof(threadId));
        lock (_gate) {
            ThrowIfNotWritable();
            return ExecuteWrite(
                "complete-thread-binding",
                (connection, transaction) => {
                    GalateaRouteBindingSnapshot route = ReadRoute(
                        connection,
                        transaction
                    );
                    RequireRoute(
                        route,
                        GalateaDelegationRouteState.Binding,
                        expectedRouteRevision
                    );
                    if (!string.Equals(
                            route.BindingOperationId,
                            bindingOperationId,
                            StringComparison.Ordinal)) {
                        throw Conflict("Thread binding operation identity changed.");
                    }
                    _ = IncrementStoreRevision(connection, transaction);
                    using SqliteCommand update = connection.CreateCommand();
                    update.Transaction = transaction;
                    update.CommandText = """
                        UPDATE route_binding
                        SET state = 'Bound', thread_id = $thread,
                            ensure_attempt_count = 0,
                            ensure_last_code = NULL,
                            next_ensure_at_ms = NULL,
                            revision = revision + 1
                        WHERE singleton = 1
                          AND state = 'Binding'
                          AND binding_operation_id = $operation
                          AND revision = $revision;
                        """;
                    update.Parameters.AddWithValue("$thread", threadId);
                    update.Parameters.AddWithValue("$operation", bindingOperationId);
                    update.Parameters.AddWithValue("$revision", expectedRouteRevision);
                    RequireOne(update.ExecuteNonQuery(), "route binding completion");
                    return route with {
                        State = GalateaDelegationRouteState.Bound,
                        ThreadId = threadId,
                        EnsureAttemptCount = 0,
                        EnsureLastCode = null,
                        NextEnsureAtUnixTimeMilliseconds = null,
                        Revision = checked(route.Revision + 1)
                    };
                },
                (snapshot, result) => snapshot.Route == result
            );
        }
    }

    internal GalateaRouteBindingSnapshot QuarantineThreadBinding(
        string bindingOperationId,
        long expectedRouteRevision,
        string code
    ) {
        RequireOperationId(bindingOperationId);
        RequireFailureToken(code, nameof(code));
        lock (_gate) {
            ThrowIfNotWritable();
            return ExecuteWrite(
                "quarantine-thread-binding",
                (connection, transaction) => {
                    GalateaRouteBindingSnapshot route = ReadRoute(
                        connection,
                        transaction
                    );
                    RequireRoute(
                        route,
                        GalateaDelegationRouteState.Binding,
                        expectedRouteRevision
                    );
                    if (!string.Equals(route.BindingOperationId,
                            bindingOperationId, StringComparison.Ordinal)) {
                        throw Conflict(
                            "Thread binding operation identity changed."
                        );
                    }
                    _ = IncrementStoreRevision(connection, transaction);
                    using SqliteCommand update = connection.CreateCommand();
                    update.Transaction = transaction;
                    update.CommandText = """
                        UPDATE route_binding
                        SET state = 'Quarantined', quarantine_code = $code,
                            ensure_attempt_count = 0,
                            ensure_last_code = NULL,
                            next_ensure_at_ms = NULL,
                            revision = revision + 1
                        WHERE singleton = 1 AND state = 'Binding'
                          AND binding_operation_id = $operation
                          AND revision = $revision;
                        """;
                    update.Parameters.AddWithValue("$code", code);
                    update.Parameters.AddWithValue(
                        "$operation",
                        bindingOperationId
                    );
                    update.Parameters.AddWithValue(
                        "$revision",
                        expectedRouteRevision
                    );
                    RequireOne(update.ExecuteNonQuery(),
                        "thread binding quarantine");
                    return route with {
                        State = GalateaDelegationRouteState.Quarantined,
                        QuarantineCode = code,
                        EnsureAttemptCount = 0,
                        EnsureLastCode = null,
                        NextEnsureAtUnixTimeMilliseconds = null,
                        Revision = checked(route.Revision + 1)
                    };
                },
                (snapshot, result) => snapshot.Route == result
            );
        }
    }

    /// <summary>
    /// Crosses the possible external-effect boundary. The known fixed thread
    /// and route active slot are frozen in the same durable transaction.
    /// </summary>
    internal GalateaOutboundMailSnapshot StartQueuedMail(
        string dispatchId,
        long expectedMailRevision,
        long expectedRouteRevision
    ) {
        RequireDispatchId(dispatchId);
        lock (_gate) {
            ThrowIfNotWritable();
            return ExecuteWrite(
                "start-queued-mail",
                (connection, transaction) => {
                    GalateaRouteBindingSnapshot route = ReadRoute(
                        connection,
                        transaction
                    );
                    RequireRoute(
                        route,
                        GalateaDelegationRouteState.Bound,
                        expectedRouteRevision
                    );
                    if (route.ActiveDispatchId is not null
                        || route.ThreadId is null) {
                        throw Conflict("The fixed route is not idle and bound.");
                    }
                    GalateaOutboundMailSnapshot mail = ReadMailRequired(
                        connection,
                        transaction,
                        dispatchId
                    );
                    RequireMail(
                        mail,
                        GalateaDurableMailState.Queued,
                        expectedMailRevision
                    );
                    if (mail.Body is null
                        || StrictUtf8.GetByteCount(mail.Body)
                            > _limits.MaximumTaskUtf8Bytes) {
                        throw Conflict(
                            "Queued mail must settle its durable preflight failure before start."
                        );
                    }
                    RequireEarliestQueuedMail(
                        connection,
                        transaction,
                        dispatchId
                    );
                    RequireInboxReservationCapacity(
                        connection,
                        transaction,
                        _limits
                    );
                    _ = IncrementStoreRevision(connection, transaction);
                    using (SqliteCommand updateMail = connection.CreateCommand()) {
                        updateMail.Transaction = transaction;
                        updateMail.CommandText = """
                            UPDATE outbound_mail
                            SET state = 'Started', operation_id = $operation,
                                requested_thread_id = $thread,
                                frozen_route_policy_fingerprint = $policy,
                                revision = revision + 1
                            WHERE dispatch_id = $dispatch
                              AND state = 'Queued'
                              AND revision = $revision;
                            """;
                        updateMail.Parameters.AddWithValue("$operation", dispatchId);
                        updateMail.Parameters.AddWithValue("$thread", route.ThreadId);
                        updateMail.Parameters.AddWithValue(
                            "$policy",
                            route.RoutePolicyFingerprint
                        );
                        updateMail.Parameters.AddWithValue("$dispatch", dispatchId);
                        updateMail.Parameters.AddWithValue("$revision", expectedMailRevision);
                        RequireOne(updateMail.ExecuteNonQuery(), "mail start claim");
                    }
                    using (SqliteCommand updateRoute = connection.CreateCommand()) {
                        updateRoute.Transaction = transaction;
                        updateRoute.CommandText = """
                            UPDATE route_binding
                            SET active_dispatch_id = $dispatch,
                                revision = revision + 1
                            WHERE singleton = 1 AND state = 'Bound'
                              AND active_dispatch_id IS NULL
                              AND thread_id = $thread
                              AND revision = $revision;
                            """;
                        updateRoute.Parameters.AddWithValue("$dispatch", dispatchId);
                        updateRoute.Parameters.AddWithValue("$thread", route.ThreadId);
                        updateRoute.Parameters.AddWithValue("$revision", expectedRouteRevision);
                        RequireOne(updateRoute.ExecuteNonQuery(), "route active dispatch claim");
                    }
                    return mail with {
                        State = GalateaDurableMailState.Started,
                        OperationId = dispatchId,
                        RequestedThreadId = route.ThreadId,
                        FrozenRoutePolicyFingerprint =
                            route.RoutePolicyFingerprint,
                        Revision = checked(mail.Revision + 1)
                    };
                },
                (snapshot, result) => snapshot.Mails.Contains(result)
                    && string.Equals(
                        snapshot.Route.ActiveDispatchId,
                        dispatchId,
                        StringComparison.Ordinal)
            );
        }
    }

    internal GalateaReplyNoticeSnapshot FailQueuedMailPreflight(
        string dispatchId,
        long expectedMailRevision
    ) {
        RequireDispatchId(dispatchId);
        lock (_gate) {
            ThrowIfNotWritable();
            return ExecuteWrite(
                "fail-queued-mail-preflight",
                (connection, transaction) => {
                    GalateaRouteBindingSnapshot route = ReadRoute(
                        connection,
                        transaction
                    );
                    if (route.State == GalateaDelegationRouteState.Binding
                        || route.ActiveDispatchId is not null) {
                        throw Conflict(
                            "Queued preflight cannot advance during binding or an active dispatch."
                        );
                    }
                    GalateaOutboundMailSnapshot mail = ReadMailRequired(
                        connection,
                        transaction,
                        dispatchId
                    );
                    RequireMail(
                        mail,
                        GalateaDurableMailState.Queued,
                        expectedMailRevision
                    );
                    RequireEarliestAdmittedMail(
                        connection,
                        transaction,
                        dispatchId
                    );
                    if (mail.Body is null
                        || StrictUtf8.GetByteCount(mail.Body)
                            <= _limits.MaximumTaskUtf8Bytes) {
                        throw Conflict(
                            "Queued mail does not violate the durable task bound."
                        );
                    }
                    RequireInboxNoticeCapacity(
                        connection,
                        transaction,
                        _limits,
                        GalateaDelegationDurableContract.TaskTooLargeNotice
                    );
                    (long sequence, long storeRevision) =
                        AllocateCompletionSequence(connection, transaction);
                    using (SqliteCommand updateMail =
                           connection.CreateCommand()) {
                        updateMail.Transaction = transaction;
                        updateMail.CommandText = """
                            UPDATE outbound_mail
                            SET state = 'TerminalFailed',
                                body = NULL, evidence_quote = NULL,
                                terminal_stage = $stage,
                                terminal_code = $code,
                                reconcile_attempt_count = 0,
                                reconcile_last_code = NULL,
                                next_reconcile_at_ms = NULL,
                                revision = revision + 1
                            WHERE dispatch_id = $dispatch
                              AND state = 'Queued'
                              AND revision = $revision;
                            """;
                        updateMail.Parameters.AddWithValue(
                            "$stage",
                            GalateaDelegationDurableContract.TaskTooLargeStage
                        );
                        updateMail.Parameters.AddWithValue(
                            "$code",
                            GalateaDelegationDurableContract.TaskTooLargeCode
                        );
                        updateMail.Parameters.AddWithValue(
                            "$dispatch",
                            dispatchId
                        );
                        updateMail.Parameters.AddWithValue(
                            "$revision",
                            expectedMailRevision
                        );
                        RequireOne(
                            updateMail.ExecuteNonQuery(),
                            "queued mail preflight failure"
                        );
                    }
                    using (SqliteCommand insertNotice =
                           connection.CreateCommand()) {
                        insertNotice.Transaction = transaction;
                        insertNotice.CommandText = """
                            INSERT INTO reply_notice(
                                notice_id, dispatch_id, kind, body, stage,
                                code, completion_sequence, state, revision
                            ) VALUES (
                                $notice, $dispatch, 'DeliveryFailure', $body,
                                $stage, $code, $sequence, 'Ready', 0
                            );
                            """;
                        insertNotice.Parameters.AddWithValue(
                            "$notice",
                            dispatchId
                        );
                        insertNotice.Parameters.AddWithValue(
                            "$dispatch",
                            dispatchId
                        );
                        insertNotice.Parameters.AddWithValue(
                            "$body",
                            GalateaDelegationDurableContract.TaskTooLargeNotice
                        );
                        insertNotice.Parameters.AddWithValue(
                            "$stage",
                            GalateaDelegationDurableContract.TaskTooLargeStage
                        );
                        insertNotice.Parameters.AddWithValue(
                            "$code",
                            GalateaDelegationDurableContract.TaskTooLargeCode
                        );
                        insertNotice.Parameters.AddWithValue(
                            "$sequence",
                            sequence
                        );
                        insertNotice.ExecuteNonQuery();
                    }
                    _ = storeRevision;
                    return new GalateaReplyNoticeSnapshot(
                        dispatchId,
                        dispatchId,
                        GalateaReplyNoticeKind.DeliveryFailure,
                        GalateaDelegationDurableContract.TaskTooLargeNotice,
                        GalateaDelegationDurableContract.TaskTooLargeStage,
                        GalateaDelegationDurableContract.TaskTooLargeCode,
                        sequence,
                        GalateaReplyNoticeState.Ready,
                        ConsumedActionAddress: null,
                        Revision: 0
                    );
                },
                (snapshot, result) => snapshot.Notices.Contains(result)
                    && snapshot.Mails.Any(value =>
                        string.Equals(
                            value.DispatchId,
                            dispatchId,
                            StringComparison.Ordinal)
                        && value.State
                            == GalateaDurableMailState.TerminalFailed
                        && value.TerminalStage
                            == GalateaDelegationDurableContract
                                .TaskTooLargeStage
                        && value.TerminalCode
                            == GalateaDelegationDurableContract
                                .TaskTooLargeCode)
            );
        }
    }

    internal GalateaOutboundMailSnapshot MarkMailOutcomeUnknown(
        string dispatchId,
        long expectedMailRevision,
        string code,
        long nowUnixTimeMilliseconds
    ) {
        RequireFailureToken(code, nameof(code));
        ArgumentOutOfRangeException.ThrowIfNegative(
            nowUnixTimeMilliseconds
        );
        return TransitionActiveMail(
            "mark-mail-outcome-unknown",
            dispatchId,
            expectedMailRevision,
            [GalateaDurableMailState.Started],
            GalateaDurableMailState.OutcomeUnknown,
            acceptedThreadId: null,
            acceptedTurnId: null,
            reconcileCode: code,
            reconcileNowUnixTimeMilliseconds: nowUnixTimeMilliseconds
        );
    }

    internal GalateaOutboundMailSnapshot RecordMailAccepted(
        string dispatchId,
        long expectedMailRevision,
        string threadId,
        string turnId
    ) {
        RequireWireIdentity(threadId, nameof(threadId));
        RequireWireIdentity(turnId, nameof(turnId));
        return TransitionActiveMail(
            "record-mail-accepted",
            dispatchId,
            expectedMailRevision,
            [
                GalateaDurableMailState.Started,
                GalateaDurableMailState.OutcomeUnknown
            ],
            GalateaDurableMailState.Accepted,
            threadId,
            turnId,
            reconcileCode: null,
            reconcileNowUnixTimeMilliseconds: null
        );
    }

    internal GalateaOutboundMailSnapshot RecordMailPollMiss(
        string dispatchId,
        long expectedMailRevision,
        string code,
        long nowUnixTimeMilliseconds
    ) {
        RequireFailureToken(code, nameof(code));
        ArgumentOutOfRangeException.ThrowIfNegative(
            nowUnixTimeMilliseconds
        );
        RequireDispatchId(dispatchId);
        lock (_gate) {
            ThrowIfNotWritable();
            return ExecuteWrite(
                "record-mail-poll-miss",
                (connection, transaction) => {
                    GalateaRouteBindingSnapshot route = ReadRoute(
                        connection,
                        transaction
                    );
                    GalateaOutboundMailSnapshot mail = ReadMailRequired(
                        connection,
                        transaction,
                        dispatchId
                    );
                    if (route.State != GalateaDelegationRouteState.Bound
                        || !string.Equals(
                            route.ActiveDispatchId,
                            dispatchId,
                            StringComparison.Ordinal)
                        || route.ThreadId is null
                        || mail.State is not (
                            GalateaDurableMailState.OutcomeUnknown
                            or GalateaDurableMailState.Accepted)
                        || mail.Revision != expectedMailRevision
                        || !string.Equals(
                            mail.RequestedThreadId,
                            route.ThreadId,
                            StringComparison.Ordinal)
                        || nowUnixTimeMilliseconds > long.MaxValue - 300_000
                        || (mail.NextReconcileAtUnixTimeMilliseconds
                                is { } previous
                            && nowUnixTimeMilliseconds < previous)) {
                        throw Conflict(
                            "Mail polling identity or backoff precondition failed."
                        );
                    }
                    int attempt = checked(mail.ReconcileAttemptCount + 1);
                    long next = checked(
                        nowUnixTimeMilliseconds
                        + ComputeReconcileDelayMilliseconds(attempt)
                    );
                    _ = IncrementStoreRevision(connection, transaction);
                    using SqliteCommand update = connection.CreateCommand();
                    update.Transaction = transaction;
                    update.CommandText = """
                        UPDATE outbound_mail
                        SET reconcile_attempt_count = $attempt,
                            reconcile_last_code = $code,
                            next_reconcile_at_ms = $next,
                            revision = revision + 1
                        WHERE dispatch_id = $dispatch
                          AND state IN ('OutcomeUnknown', 'Accepted')
                          AND revision = $revision;
                        """;
                    update.Parameters.AddWithValue("$attempt", attempt);
                    update.Parameters.AddWithValue("$code", code);
                    update.Parameters.AddWithValue("$next", next);
                    update.Parameters.AddWithValue("$dispatch", dispatchId);
                    update.Parameters.AddWithValue(
                        "$revision",
                        expectedMailRevision
                    );
                    RequireOne(update.ExecuteNonQuery(), "mail poll miss");
                    return mail with {
                        ReconcileAttemptCount = attempt,
                        ReconcileLastCode = code,
                        NextReconcileAtUnixTimeMilliseconds = next,
                        Revision = checked(mail.Revision + 1)
                    };
                },
                (snapshot, result) => snapshot.Mails.Contains(result)
                    && string.Equals(
                        snapshot.Route.ActiveDispatchId,
                        dispatchId,
                        StringComparison.Ordinal)
            );
        }
    }

    internal GalateaReplyNoticeSnapshot RecordCompletedMail(
        string dispatchId,
        long expectedMailRevision,
        string threadId,
        string turnId,
        string final
    ) {
        RequireReplyBody(final, nameof(final));
        if (StrictUtf8.GetByteCount(final) > _limits.MaximumReplyUtf8Bytes) {
            throw new ArgumentOutOfRangeException(nameof(final));
        }
        return RecordTerminalMail(
            dispatchId,
            expectedMailRevision,
            threadId,
            turnId,
            GalateaDurableMailState.TerminalCompleted,
            GalateaReplyNoticeKind.Reply,
            final,
            stage: null,
            code: null,
            finalSha256: ComputeSha256(final)
        );
    }

    internal GalateaReplyNoticeSnapshot RecordFailedMail(
        string dispatchId,
        long expectedMailRevision,
        string threadId,
        string turnId,
        string stage,
        string code,
        string noticeBody
    ) {
        RequireFailureToken(stage, nameof(stage));
        RequireFailureToken(code, nameof(code));
        RequireFailureNoticeBody(noticeBody, nameof(noticeBody));
        return RecordTerminalMail(
            dispatchId,
            expectedMailRevision,
            threadId,
            turnId,
            GalateaDurableMailState.TerminalFailed,
            GalateaReplyNoticeKind.DeliveryFailure,
            noticeBody,
            stage,
            code,
            finalSha256: null
        );
    }

    internal GalateaOutboundMailSnapshot QuarantineActiveMail(
        string dispatchId,
        long expectedMailRevision,
        string code
    ) {
        RequireDispatchId(dispatchId);
        RequireFailureToken(code, nameof(code));
        lock (_gate) {
            ThrowIfNotWritable();
            return ExecuteWrite(
                "quarantine-active-mail",
                (connection, transaction) => {
                    GalateaRouteBindingSnapshot route = ReadRoute(connection, transaction);
                    GalateaOutboundMailSnapshot mail = ReadMailRequired(
                        connection, transaction, dispatchId);
                    if (route.State != GalateaDelegationRouteState.Bound
                        || !string.Equals(route.ActiveDispatchId, dispatchId,
                            StringComparison.Ordinal)
                        || mail.State is not (
                            GalateaDurableMailState.Started
                            or GalateaDurableMailState.OutcomeUnknown
                            or GalateaDurableMailState.Accepted)
                        || mail.Revision != expectedMailRevision) {
                        throw Conflict("The active mail cannot be quarantined from this state.");
                    }
                    _ = IncrementStoreRevision(connection, transaction);
                    using (SqliteCommand update = connection.CreateCommand()) {
                        update.Transaction = transaction;
                        update.CommandText = """
                            UPDATE outbound_mail
                            SET state = 'Quarantined', terminal_code = $code,
                                revision = revision + 1
                            WHERE dispatch_id = $dispatch
                              AND revision = $revision;
                            UPDATE route_binding
                            SET state = 'Quarantined', quarantine_code = $code,
                                revision = revision + 1
                            WHERE singleton = 1 AND state = 'Bound'
                              AND active_dispatch_id = $dispatch
                              AND revision = $routeRevision;
                            """;
                        update.Parameters.AddWithValue("$code", code);
                        update.Parameters.AddWithValue("$dispatch", dispatchId);
                        update.Parameters.AddWithValue("$revision", expectedMailRevision);
                        update.Parameters.AddWithValue(
                            "$routeRevision",
                            route.Revision
                        );
                        if (update.ExecuteNonQuery() != 2) {
                            throw Conflict("Quarantine compare-and-swap failed.");
                        }
                    }
                    return mail with {
                        State = GalateaDurableMailState.Quarantined,
                        TerminalCode = code,
                        Revision = checked(mail.Revision + 1)
                    };
                },
                (snapshot, result) => snapshot.Mails.Contains(result)
                    && snapshot.Route.State
                        == GalateaDelegationRouteState.Quarantined
                    && string.Equals(snapshot.Route.QuarantineCode, code,
                        StringComparison.Ordinal)
            );
        }
    }

    private GalateaOutboundMailSnapshot TransitionActiveMail(
        string operation,
        string dispatchId,
        long expectedMailRevision,
        IReadOnlyCollection<GalateaDurableMailState> expectedStates,
        GalateaDurableMailState targetState,
        string? acceptedThreadId,
        string? acceptedTurnId,
        string? reconcileCode,
        long? reconcileNowUnixTimeMilliseconds
    ) {
        RequireDispatchId(dispatchId);
        lock (_gate) {
            ThrowIfNotWritable();
            return ExecuteWrite(
                operation,
                (connection, transaction) => {
                    GalateaRouteBindingSnapshot route = ReadRoute(connection, transaction);
                    GalateaOutboundMailSnapshot mail = ReadMailRequired(
                        connection, transaction, dispatchId);
                    if (route.State != GalateaDelegationRouteState.Bound
                        || !string.Equals(route.ActiveDispatchId, dispatchId,
                            StringComparison.Ordinal)
                        || route.ThreadId is null
                        || !expectedStates.Contains(mail.State)
                        || mail.Revision != expectedMailRevision
                        || !string.Equals(mail.RequestedThreadId, route.ThreadId,
                            StringComparison.Ordinal)
                        || (acceptedThreadId is not null
                            && !string.Equals(acceptedThreadId, route.ThreadId,
                                StringComparison.Ordinal))) {
                        throw Conflict("The active mail transition precondition failed.");
                    }
                    if (targetState == GalateaDurableMailState.OutcomeUnknown
                        && (reconcileNowUnixTimeMilliseconds is not { } now
                            || now < 0
                            || now > long.MaxValue - 300_000
                            || (mail.NextReconcileAtUnixTimeMilliseconds
                                    is { } previous
                                && now < previous))) {
                        throw Conflict(
                            "OutcomeUnknown reconciliation backoff is not due."
                        );
                    }
                    int reconcileAttempt = targetState
                            == GalateaDurableMailState.OutcomeUnknown
                        ? checked(mail.ReconcileAttemptCount + 1)
                        : 0;
                    long? nextReconcileAtUnixTimeMilliseconds =
                        targetState == GalateaDurableMailState.OutcomeUnknown
                            ? checked(
                                reconcileNowUnixTimeMilliseconds!.Value
                                + ComputeReconcileDelayMilliseconds(
                                    reconcileAttempt
                                ))
                            : null;
                    _ = IncrementStoreRevision(connection, transaction);
                    using SqliteCommand update = connection.CreateCommand();
                    update.Transaction = transaction;
                    update.CommandText = """
                        UPDATE outbound_mail
                        SET state = $state,
                            accepted_thread_id = $thread,
                            accepted_turn_id = $turn,
                            reconcile_attempt_count = CASE
                                WHEN $state = 'OutcomeUnknown'
                                    THEN reconcile_attempt_count + 1
                                ELSE 0 END,
                            reconcile_last_code = $reconcileCode,
                            next_reconcile_at_ms = $nextReconcileAt,
                            revision = revision + 1
                        WHERE dispatch_id = $dispatch
                          AND revision = $revision;
                        """;
                    update.Parameters.AddWithValue("$state", targetState.ToString());
                    update.Parameters.AddWithValue(
                        "$thread", (object?)acceptedThreadId ?? DBNull.Value);
                    update.Parameters.AddWithValue(
                        "$turn", (object?)acceptedTurnId ?? DBNull.Value);
                    update.Parameters.AddWithValue(
                        "$reconcileCode",
                        (object?)reconcileCode ?? DBNull.Value
                    );
                    update.Parameters.AddWithValue(
                        "$nextReconcileAt",
                        (object?)nextReconcileAtUnixTimeMilliseconds
                            ?? DBNull.Value
                    );
                    update.Parameters.AddWithValue("$dispatch", dispatchId);
                    update.Parameters.AddWithValue("$revision", expectedMailRevision);
                    RequireOne(update.ExecuteNonQuery(), "active mail transition");
                    return mail with {
                        State = targetState,
                        AcceptedThreadId = acceptedThreadId,
                        AcceptedTurnId = acceptedTurnId,
                        ReconcileAttemptCount = reconcileAttempt,
                        ReconcileLastCode = reconcileCode,
                        NextReconcileAtUnixTimeMilliseconds =
                            nextReconcileAtUnixTimeMilliseconds,
                        Revision = checked(mail.Revision + 1)
                    };
                },
                (snapshot, result) => snapshot.Mails.Contains(result)
                    && string.Equals(snapshot.Route.ActiveDispatchId,
                        dispatchId, StringComparison.Ordinal)
            );
        }
    }

    private GalateaReplyNoticeSnapshot RecordTerminalMail(
        string dispatchId,
        long expectedMailRevision,
        string threadId,
        string turnId,
        GalateaDurableMailState targetState,
        GalateaReplyNoticeKind noticeKind,
        string noticeBody,
        string? stage,
        string? code,
        string? finalSha256
    ) {
        RequireDispatchId(dispatchId);
        RequireWireIdentity(threadId, nameof(threadId));
        RequireWireIdentity(turnId, nameof(turnId));
        lock (_gate) {
            ThrowIfNotWritable();
            using (SqliteConnection currentConnection =
                   OpenVerifiedConnection()) {
                GalateaDelegationStateSnapshot current = ReadSnapshotCore(
                    currentConnection,
                    transaction: null
                );
                GalateaOutboundMailSnapshot? currentMail = current.Mails
                    .SingleOrDefault(value => string.Equals(
                        value.DispatchId,
                        dispatchId,
                        StringComparison.Ordinal
                    ));
                if (currentMail?.State is
                        GalateaDurableMailState.TerminalCompleted
                        or GalateaDurableMailState.TerminalFailed) {
                    GalateaReplyNoticeSnapshot? currentNotice = current.Notices
                        .SingleOrDefault(value => string.Equals(
                            value.DispatchId,
                            dispatchId,
                            StringComparison.Ordinal
                        ));
                    bool exact = currentMail.State == targetState
                        && currentNotice is not null
                        && currentNotice.Kind == noticeKind
                        && string.Equals(currentNotice.Body, noticeBody,
                            StringComparison.Ordinal)
                        && string.Equals(currentNotice.Stage, stage,
                            StringComparison.Ordinal)
                        && string.Equals(currentNotice.Code, code,
                            StringComparison.Ordinal)
                        && string.Equals(currentMail.AcceptedThreadId, threadId,
                            StringComparison.Ordinal)
                        && string.Equals(currentMail.AcceptedTurnId, turnId,
                            StringComparison.Ordinal)
                        && string.Equals(currentMail.TerminalFinalSha256,
                            finalSha256, StringComparison.Ordinal);
                    if (exact) { return currentNotice!; }
                    QuarantineRouteForTerminalConflict(current.Route);
                    throw Conflict(
                        "Repeated terminal evidence conflicts with durable state."
                    );
                }
            }
            return ExecuteWrite(
                "record-terminal-mail",
                (connection, transaction) => {
                    GalateaRouteBindingSnapshot route = ReadRoute(connection, transaction);
                    GalateaOutboundMailSnapshot mail = ReadMailRequired(
                        connection, transaction, dispatchId);
                    if (route.State != GalateaDelegationRouteState.Bound
                        || !string.Equals(route.ActiveDispatchId, dispatchId,
                            StringComparison.Ordinal)
                        || !string.Equals(route.ThreadId, threadId,
                            StringComparison.Ordinal)
                        || mail.State is not (
                            GalateaDurableMailState.Started
                            or GalateaDurableMailState.OutcomeUnknown
                            or GalateaDurableMailState.Accepted)
                        || mail.Revision != expectedMailRevision
                        || !string.Equals(mail.RequestedThreadId, threadId,
                            StringComparison.Ordinal)
                        || (mail.AcceptedThreadId is not null
                            && (!string.Equals(mail.AcceptedThreadId, threadId,
                                    StringComparison.Ordinal)
                                || !string.Equals(mail.AcceptedTurnId, turnId,
                                    StringComparison.Ordinal)))) {
                        throw Conflict("Terminal mail identity or state conflicts.");
                    }
                    (long sequence, long storeRevision) =
                        AllocateCompletionSequence(connection, transaction);
                    using (SqliteCommand updateMail = connection.CreateCommand()) {
                        updateMail.Transaction = transaction;
                        updateMail.CommandText = """
                            UPDATE outbound_mail
                            SET state = $state,
                                body = NULL, evidence_quote = NULL,
                                accepted_thread_id = $thread,
                                accepted_turn_id = $turn,
                                terminal_final_sha256 = $final,
                                terminal_stage = $stage,
                                terminal_code = $code,
                                reconcile_attempt_count = 0,
                                reconcile_last_code = NULL,
                                next_reconcile_at_ms = NULL,
                                revision = revision + 1
                            WHERE dispatch_id = $dispatch
                              AND revision = $revision;
                            """;
                        updateMail.Parameters.AddWithValue("$state", targetState.ToString());
                        updateMail.Parameters.AddWithValue("$thread", threadId);
                        updateMail.Parameters.AddWithValue("$turn", turnId);
                        updateMail.Parameters.AddWithValue("$final", (object?)finalSha256 ?? DBNull.Value);
                        updateMail.Parameters.AddWithValue("$stage", (object?)stage ?? DBNull.Value);
                        updateMail.Parameters.AddWithValue("$code", (object?)code ?? DBNull.Value);
                        updateMail.Parameters.AddWithValue("$dispatch", dispatchId);
                        updateMail.Parameters.AddWithValue("$revision", expectedMailRevision);
                        RequireOne(updateMail.ExecuteNonQuery(), "terminal mail update");
                    }
                    using (SqliteCommand insertNotice = connection.CreateCommand()) {
                        insertNotice.Transaction = transaction;
                        insertNotice.CommandText = """
                            INSERT INTO reply_notice(
                                notice_id, dispatch_id, kind, body, stage,
                                code, completion_sequence, state, revision
                            ) VALUES (
                                $notice, $dispatch, $kind, $body, $stage,
                                $code, $sequence, 'Ready', 0
                            );
                            """;
                        insertNotice.Parameters.AddWithValue("$notice", dispatchId);
                        insertNotice.Parameters.AddWithValue("$dispatch", dispatchId);
                        insertNotice.Parameters.AddWithValue("$kind", noticeKind.ToString());
                        insertNotice.Parameters.AddWithValue("$body", noticeBody);
                        insertNotice.Parameters.AddWithValue("$stage", (object?)stage ?? DBNull.Value);
                        insertNotice.Parameters.AddWithValue("$code", (object?)code ?? DBNull.Value);
                        insertNotice.Parameters.AddWithValue("$sequence", sequence);
                        insertNotice.ExecuteNonQuery();
                    }
                    using (SqliteCommand releaseRoute = connection.CreateCommand()) {
                        releaseRoute.Transaction = transaction;
                        releaseRoute.CommandText = """
                            UPDATE route_binding
                            SET active_dispatch_id = NULL,
                                revision = revision + 1
                            WHERE singleton = 1 AND state = 'Bound'
                              AND active_dispatch_id = $dispatch
                              AND thread_id = $thread
                              AND revision = $routeRevision;
                            """;
                        releaseRoute.Parameters.AddWithValue("$dispatch", dispatchId);
                        releaseRoute.Parameters.AddWithValue("$thread", threadId);
                        releaseRoute.Parameters.AddWithValue(
                            "$routeRevision",
                            route.Revision
                        );
                        RequireOne(releaseRoute.ExecuteNonQuery(), "terminal route release");
                    }
                    _ = storeRevision;
                    return new GalateaReplyNoticeSnapshot(
                        dispatchId,
                        dispatchId,
                        noticeKind,
                        noticeBody,
                        stage,
                        code,
                        sequence,
                        GalateaReplyNoticeState.Ready,
                        ConsumedActionAddress: null,
                        Revision: 0
                    );
                },
                (snapshot, result) => snapshot.Notices.Contains(result)
                    && snapshot.Route.ActiveDispatchId is null
            );
        }
    }

    private T ExecuteWrite<T>(
        string operation,
        Func<SqliteConnection, SqliteTransaction, T> apply,
        Func<GalateaDelegationStateSnapshot, T, bool> isPublished
    ) {
        ThrowIfNotWritable();
        T result;
        Exception? uncertain = null;
        using (SqliteConnection connection = OpenVerifiedConnection())
        using (SqliteTransaction transaction =
            connection.BeginTransaction(deferred: false)) {
            result = apply(connection, transaction);
            _hooks.BeforeCommit?.Invoke(operation);
            try {
                transaction.Commit();
                _hooks.AfterCommitBeforeReturn?.Invoke(operation);
            }
            catch (Exception exception) when (
                GalateaExceptionClassifier.IsNonFatal(exception)) {
                uncertain = exception;
            }
        }
        if (uncertain is null) { return result; }

        // The connection that observed the uncertain COMMIT has been disposed.
        // Reopen from disk and classify only the exact domain post-state.
        using SqliteConnection reopened = OpenVerifiedConnection();
        GalateaDelegationStateSnapshot snapshot = ReadSnapshotCore(
            reopened,
            transaction: null
        );
        if (isPublished(snapshot, result)) { return result; }
        throw new GalateaDelegationCommitOutcomeException(
            operation,
            "the exact post-state was absent after reopen",
            uncertain
        );
    }

    private static long IncrementStoreRevision(
        SqliteConnection connection,
        SqliteTransaction transaction
    ) {
        using SqliteCommand update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE delegation_meta SET revision = revision + 1
            WHERE singleton = 1
            RETURNING revision;
            """;
        return Convert.ToInt64(update.ExecuteScalar());
    }

    private static (long Sequence, long StoreRevision)
        AllocateCompletionSequence(
            SqliteConnection connection,
            SqliteTransaction transaction
        ) {
        using SqliteCommand update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE delegation_meta
            SET next_completion_sequence = next_completion_sequence + 1,
                revision = revision + 1
            WHERE singleton = 1
            RETURNING next_completion_sequence - 1, revision;
            """;
        using SqliteDataReader reader = update.ExecuteReader();
        if (!reader.Read()) {
            throw Corrupt("delegation_meta completion allocation failed.");
        }
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    private static GalateaDelegationCaptureResult? TryReadExistingCapture(
        SqliteConnection connection,
        GalateaDelegationCaptureRequest request
    ) {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT visible_action_sha256, visible_action_utf8_bytes,
                   extractor_contract_id, artifact_count,
                   (SELECT revision FROM delegation_meta WHERE singleton = 1)
            FROM action_capture WHERE source_action_address = $source;
            """;
        command.Parameters.AddWithValue("$source", request.SourceActionAddress);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read()) { return null; }
        if (!string.Equals(reader.GetString(0), request.VisibleActionSha256,
                StringComparison.Ordinal)
            || reader.GetInt32(1) != request.VisibleActionUtf8Bytes
            || !string.Equals(reader.GetString(2), request.ExtractorContractId,
                StringComparison.Ordinal)) {
            throw Corrupt("A duplicate Action capture changed exact identity.");
        }
        int artifactCount = reader.GetInt32(3);
        long storeRevision = reader.GetInt64(4);
        reader.Close();
        var dispatchIds = new List<string>(artifactCount);
        using SqliteCommand mails = connection.CreateCommand();
        mails.CommandText = """
            SELECT dispatch_id, artifact_ordinal FROM outbound_mail
            WHERE source_action_address = $source
            ORDER BY artifact_ordinal;
            """;
        mails.Parameters.AddWithValue("$source", request.SourceActionAddress);
        using SqliteDataReader mailReader = mails.ExecuteReader();
        while (mailReader.Read()) {
            if (mailReader.GetInt32(1) != dispatchIds.Count) {
                throw Corrupt("Duplicate capture mail ordinals are invalid.");
            }
            dispatchIds.Add(mailReader.GetString(0));
        }
        if (dispatchIds.Count != artifactCount) {
            throw Corrupt("Duplicate capture artifact_count is invalid.");
        }
        return new GalateaDelegationCaptureResult(
            GalateaDelegationCaptureDisposition.AlreadyCaptured,
            storeRevision,
            GalateaDelegationStateSnapshot.Freeze(dispatchIds)
        );
    }

    private static void InsertCapturedMail(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourceActionAddress,
        int ordinal,
        string dispatchId,
        SendMailIntent intent
    ) {
        bool routed = string.Equals(
            intent.Recipient,
            GalateaDelegateConfigReader.CanonicalRecipient,
            StringComparison.Ordinal
        );
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO outbound_mail(
                dispatch_id, source_action_address, artifact_ordinal,
                recipient, subject, body, in_reply_to_message_id,
                evidence_quote, route_class,
                frozen_route_policy_fingerprint, state, operation_id,
                requested_thread_id, accepted_thread_id,
                accepted_turn_id, terminal_final_sha256,
                terminal_stage, terminal_code, reconcile_attempt_count,
                reconcile_last_code, next_reconcile_at_ms, revision
            ) VALUES (
                $dispatch, $source, $ordinal, $recipient, $subject,
                $body, $reply, $evidence, $route, NULL, $state,
                NULL, NULL, NULL, NULL, NULL, NULL, NULL,
                0, NULL, NULL, 0
            );
            """;
        command.Parameters.AddWithValue("$dispatch", dispatchId);
        command.Parameters.AddWithValue("$source", sourceActionAddress);
        command.Parameters.AddWithValue("$ordinal", ordinal);
        command.Parameters.AddWithValue("$recipient", intent.Recipient);
        command.Parameters.AddWithValue("$subject", (object?)intent.Subject ?? DBNull.Value);
        command.Parameters.AddWithValue("$body", intent.Body);
        command.Parameters.AddWithValue("$reply", (object?)intent.InReplyToMessageId ?? DBNull.Value);
        command.Parameters.AddWithValue("$evidence", intent.EvidenceQuote);
        command.Parameters.AddWithValue("$route", routed ? "Codex" : "Unrouted");
        command.Parameters.AddWithValue("$state", routed ? "Queued" : "Unrouted");
        command.ExecuteNonQuery();
    }

    private static GalateaOutboundMailSnapshot ReadMailRequired(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string dispatchId
    ) => ReadMails(connection, transaction).SingleOrDefault(value =>
            string.Equals(value.DispatchId, dispatchId, StringComparison.Ordinal))
        ?? throw Conflict("The requested outbound mail does not exist.");

    private static void RequireEarliestQueuedMail(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string dispatchId
    ) {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT mail.dispatch_id
            FROM outbound_mail AS mail
            JOIN action_capture AS capture
              ON capture.source_action_address = mail.source_action_address
            WHERE mail.route_class = 'Codex' AND mail.state = 'Queued'
            ORDER BY capture.capture_sequence, mail.artifact_ordinal
            LIMIT 1;
            """;
        string? earliest = command.ExecuteScalar() as string;
        if (!string.Equals(earliest, dispatchId, StringComparison.Ordinal)) {
            throw Conflict("Only the earliest queued Codex mail may start.");
        }
    }

    private static void RequireEarliestAdmittedMail(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string dispatchId
    ) {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT mail.dispatch_id
            FROM outbound_mail AS mail
            JOIN action_capture AS capture
              ON capture.source_action_address = mail.source_action_address
            WHERE mail.route_class = 'Codex'
              AND mail.state IN (
                  'Queued', 'Started', 'OutcomeUnknown', 'Accepted'
              )
            ORDER BY capture.capture_sequence, mail.artifact_ordinal
            LIMIT 1;
            """;
        string? earliest = command.ExecuteScalar() as string;
        if (!string.Equals(earliest, dispatchId, StringComparison.Ordinal)) {
            throw Conflict(
                "Only the earliest admitted Codex mail may settle preflight."
            );
        }
    }

    private static void RequireCaptureCapacity(
        SqliteConnection connection,
        SqliteTransaction transaction,
        GalateaDelegationCaptureRequest request,
        GalateaDelegationStoreLimits limits
    ) {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*), COALESCE(SUM(
                length(CAST(recipient AS BLOB))
                + length(CAST(COALESCE(subject, '') AS BLOB))
                + length(CAST(COALESCE(body, '') AS BLOB))
                + length(CAST(COALESCE(in_reply_to_message_id, '') AS BLOB))
                + length(CAST(COALESCE(evidence_quote, '') AS BLOB))
                + 128
            ), 0)
            FROM outbound_mail;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read()) { throw Corrupt("Mail capacity query failed."); }
        long count = reader.GetInt64(0);
        long bytes = reader.GetInt64(1);
        reader.Close();
        using SqliteCommand captureCount = connection.CreateCommand();
        captureCount.Transaction = transaction;
        captureCount.CommandText = "SELECT COUNT(*) FROM action_capture;";
        long captures = Convert.ToInt64(captureCount.ExecuteScalar());
        using SqliteCommand queued = connection.CreateCommand();
        queued.Transaction = transaction;
        queued.CommandText = """
            SELECT COUNT(*) FROM outbound_mail
            WHERE route_class = 'Codex'
              AND state IN ('Queued', 'Started', 'OutcomeUnknown', 'Accepted');
            """;
        long admitted = Convert.ToInt64(queued.ExecuteScalar());
        int addedRouted = request.Intents.Count(static intent =>
            string.Equals(
                intent.Recipient,
                GalateaDelegateConfigReader.CanonicalRecipient,
                StringComparison.Ordinal
            ));
        long addedBytes = request.Intents.Sum(static intent =>
            checked((long)TextExtractorUtf8.GetByteCount(intent.Recipient)
                + TextExtractorUtf8.GetByteCount(intent.Subject ?? string.Empty)
                + TextExtractorUtf8.GetByteCount(intent.Body)
                + TextExtractorUtf8.GetByteCount(intent.InReplyToMessageId ?? string.Empty)
                + TextExtractorUtf8.GetByteCount(intent.EvidenceQuote)
                + 128));
        if (captures >= GalateaDelegationStateBounds.MaximumCandidateCount
            || count > GalateaDelegationStateBounds.MaximumCandidateCount
                - request.Intents.Count
            || bytes > GalateaDelegationStateBounds.MaximumCandidateUtf8Bytes
                - addedBytes
            || admitted > limits.MaximumQueuedMails - addedRouted) {
            throw new InvalidOperationException(
                "The durable delegation candidate capacity is full."
            );
        }
    }

    private static void RequireInboxReservationCapacity(
        SqliteConnection connection,
        SqliteTransaction transaction,
        GalateaDelegationStoreLimits limits
    ) {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*), COALESCE(SUM(length(CAST(body AS BLOB))), 0)
            FROM reply_notice WHERE state IN ('Ready', 'Leased');
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read()) {
            throw Corrupt("Inbox reservation capacity query failed.");
        }
        long count = reader.GetInt64(0);
        long bytes = reader.GetInt64(1);
        int reservationBytes = Math.Max(
            limits.MaximumReplyUtf8Bytes,
            PlayerTurnObservationEnvelope.MaximumFailureUtf8Bytes
        );
        if (count >= limits.MaximumInboxReplies
            || bytes > limits.MaximumInboxUtf8Bytes
                - reservationBytes) {
            throw new GalateaDelegationInboxBackpressureException(
                count,
                bytes,
                reservedCount: 1,
                reservationBytes,
                limits
            );
        }
    }

    private static void RequireInboxNoticeCapacity(
        SqliteConnection connection,
        SqliteTransaction transaction,
        GalateaDelegationStoreLimits limits,
        string noticeBody
    ) {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                COUNT(*),
                COALESCE(SUM(length(CAST(body AS BLOB))), 0),
                (SELECT COUNT(*) FROM route_binding
                 WHERE active_dispatch_id IS NOT NULL)
            FROM reply_notice WHERE state IN ('Ready', 'Leased');
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read()) {
            throw Corrupt("Inbox notice capacity query failed.");
        }
        long count = reader.GetInt64(0);
        long bytes = reader.GetInt64(1);
        int activeReservations = reader.GetInt32(2);
        int noticeBytes = StrictUtf8.GetByteCount(noticeBody);
        int maximumReplyReservationBytes = Math.Max(
            limits.MaximumReplyUtf8Bytes,
            PlayerTurnObservationEnvelope.MaximumFailureUtf8Bytes
        );
        int reservedCount = checked(activeReservations + 1);
        int reservedBytes = checked(
            activeReservations * maximumReplyReservationBytes + noticeBytes
        );
        if (count > limits.MaximumInboxReplies - reservedCount
            || bytes > limits.MaximumInboxUtf8Bytes - reservedBytes) {
            throw new GalateaDelegationInboxBackpressureException(
                count,
                bytes,
                reservedCount,
                reservedBytes,
                limits
            );
        }
    }

    private static void ValidateCaptureRequest(
        GalateaDelegationCaptureRequest request
    ) {
        ArgumentNullException.ThrowIfNull(request);
        if (!Atelia.SessionJournal.EventAddressTextCodec.TryParse(
                request.SourceActionAddress, out _)) {
            throw new ArgumentException("sourceActionAddress is not canonical.", nameof(request));
        }
        if (!IsLowerHexSha256(request.VisibleActionSha256)) {
            throw new ArgumentException("visibleActionSha256 is not canonical.", nameof(request));
        }
        ArgumentOutOfRangeException.ThrowIfNegative(request.VisibleActionUtf8Bytes);
        if (request.VisibleActionUtf8Bytes
                > TextExtractorBounds.MaximumTargetTextUtf8Bytes) {
            throw new ArgumentOutOfRangeException(nameof(request));
        }
        RequireBoundedText(request.ExtractorContractId, nameof(request.ExtractorContractId));
        ArgumentNullException.ThrowIfNull(request.Intents);
        if (request.Intents.Count > GalateaDelegationStateBounds.MaximumCapturedArtifacts) {
            throw new ArgumentOutOfRangeException(nameof(request));
        }
        foreach (SendMailIntent intent in request.Intents) {
            ArgumentNullException.ThrowIfNull(intent);
            ValidateIntent(intent);
        }
    }

    private static void ValidateIntent(SendMailIntent intent) {
        RequireText(intent.Recipient, GalateaMailboxBounds.MaximumRecipientUtf8Bytes,
            nameof(intent.Recipient), allowLineBreaks: false);
        if (intent.Subject is not null) {
            RequireText(intent.Subject, GalateaMailboxBounds.MaximumSubjectUtf8Bytes,
                nameof(intent.Subject), allowLineBreaks: false);
        }
        RequireText(intent.Body, GalateaMailboxBounds.MaximumBodyUtf8Bytes,
            nameof(intent.Body), allowLineBreaks: true);
        if (intent.InReplyToMessageId is not null
            && !GalateaHttpV1.IsCanonicalTurnId(intent.InReplyToMessageId)) {
            throw new ArgumentException("inReplyToMessageId is not canonical.");
        }
        RequireText(intent.EvidenceQuote, GalateaMailboxBounds.MaximumEvidenceUtf8Bytes,
            nameof(intent.EvidenceQuote), allowLineBreaks: true);
    }

    private static void RequireText(
        string? value,
        int maximumUtf8Bytes,
        string parameter,
        bool allowLineBreaks
    ) {
        if (string.IsNullOrWhiteSpace(value)) {
            throw new ArgumentException($"{parameter} must not be blank.", parameter);
        }
        try {
            if (StrictUtf8.GetByteCount(value) > maximumUtf8Bytes
                || (!allowLineBreaks
                    && GalateaMailboxText.ContainsHeaderLineBreak(value))) {
                throw new ArgumentOutOfRangeException(parameter);
            }
        }
        catch (EncoderFallbackException exception) {
            throw new ArgumentException($"{parameter} must be strict Unicode.", parameter, exception);
        }
    }

    private static void RequireOperationId(string value) =>
        RequireWireIdentity(value, nameof(value),
            GalateaDelegationStateBounds.MaximumOperationIdUtf8Bytes);

    private static void RequireDispatchId(string value) {
        if (value is null || value.Length != 68
            || !value.StartsWith("gd1-", StringComparison.Ordinal)
            || !IsLowerHexSha256(value[4..])) {
            throw new ArgumentException("dispatchId is not canonical.", nameof(value));
        }
    }

    private static void RequireWireIdentity(
        string value,
        string parameter,
        int maximumUtf8Bytes = GalateaDelegationStateBounds.MaximumIdentityUtf8Bytes
    ) => RequireText(value, maximumUtf8Bytes, parameter, allowLineBreaks: false);

    private static void RequireFailureToken(string value, string parameter) {
        RequireWireIdentity(value, parameter,
            GalateaDelegationStateBounds.MaximumFailureTokenUtf8Bytes);
        if (value.Any(static character => !(character is >= 'A' and <= 'Z'
                or >= 'a' and <= 'z' or >= '0' and <= '9'
                or '_' or '-' or '.'))) {
            throw new ArgumentException($"{parameter} is not a failure token.", parameter);
        }
    }

    private static long ComputeReconcileDelayMilliseconds(int attempt) {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        int shift = Math.Min(attempt - 1, 8);
        return Math.Min(1_000L << shift, 300_000L);
    }

    private static void RequireReplyBody(string value, string parameter) =>
        RequireText(value, PlayerTurnObservationEnvelope.MaximumReplyUtf8Bytes,
            parameter, allowLineBreaks: true);

    private static void RequireFailureNoticeBody(string value, string parameter) =>
        RequireText(value, PlayerTurnObservationEnvelope.MaximumFailureUtf8Bytes,
            parameter, allowLineBreaks: true);

    private static void RequireRoute(
        GalateaRouteBindingSnapshot route,
        GalateaDelegationRouteState expectedState,
        long expectedRevision
    ) {
        if (route.State != expectedState || route.Revision != expectedRevision) {
            throw Conflict("The route expected state/revision changed.");
        }
    }

    private static void RequireMail(
        GalateaOutboundMailSnapshot mail,
        GalateaDurableMailState expectedState,
        long expectedRevision
    ) {
        if (mail.State != expectedState || mail.Revision != expectedRevision) {
            throw Conflict("The mail expected state/revision changed.");
        }
    }

    private static void RequireOne(int affected, string operation) {
        if (affected != 1) {
            throw Conflict($"The {operation} compare-and-swap affected {affected} rows.");
        }
    }

    private static GalateaDelegationStoreConflictException Conflict(
        string detail
    ) => new(detail);
}
