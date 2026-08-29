using Atelia.Diagnostics;
using Atelia.Galatea.Server.Mailbox;
using System.Text;

namespace Atelia.Galatea.Server;

internal enum GalateaDurableDelegationPulseStep {
    NoWork,
    Backoff,
    InboxBackpressure,
    QueuedPreflightFailed,
    BindingClaimed,
    BindingDeferred,
    BindingEstablished,
    MailAccepted,
    MailOutcomeUnknown,
    RecoveredStarted,
    InspectionNotFound,
    AcceptedRunning,
    TerminalCompleted,
    TerminalFailed,
    Quarantined
}

internal sealed record GalateaDurableDelegationPulseResult(
    GalateaDurableDelegationPulseStep Step,
    string? DispatchId = null,
    string? ThreadId = null,
    string? TurnId = null,
    string? Code = null
);

/// <summary>
/// Per-user durable delegation driver. One pulse performs at most one external
/// call; the host-wide supervisor owns scheduling.
/// </summary>
internal sealed class GalateaDurableDelegationDriver {
    private const string LogCategory = "Galatea.Delegation";
    private const string RecoveredStartedCode = "RECOVERED_STARTED";
    private const string NotFoundCode = "NOT_FOUND";
    private const string BindingCancelledCode = "BINDING_CANCELLED";
    private const string BindingFatalCode = "BINDING_FATAL_TRANSPORT";
    private const string BindingPolicyCode = "BINDING_FAILURE_POLICY_INVALID";
    private const string BindingResultCode = "BINDING_RESULT_IDENTITY_MISMATCH";
    private const string StartCancelledCode = "START_CANCELLED";
    private const string StartExceptionCode = "START_EXCEPTION";
    private const string StartResultCode = "START_RESULT_IDENTITY_MISMATCH";
    private const string InspectionCancelledCode = "INSPECTION_CANCELLED";
    private const string InspectionFatalCode = "INSPECTION_FATAL_TRANSPORT";
    private const string InspectionPolicyCode = "INSPECTION_FAILURE_POLICY_INVALID";
    private const string InspectionResultCode = "INSPECTION_RESULT_IDENTITY_MISMATCH";
    private const string InspectionTurnCode = "INSPECTION_TURN_IDENTITY_MISMATCH";
    private const string FinalBlankCode = "FINAL_BLANK";
    private const string FinalTooLargeCode = "FINAL_TOO_LARGE";
    private const string FinalInvalidUnicodeCode = "FINAL_INVALID_UNICODE";
    private const string FailureStage = "inspect-dispatch";

    private readonly GalateaDelegationSqliteStore _store;
    private readonly IGalateaDurableDelegateTransport _transport;
    private readonly string _expectedRoutePolicyFingerprint;
    private readonly TimeProvider _timeProvider;
    private readonly Func<string> _bindingOperationIdFactory;
    private readonly SemaphoreSlim _pulseGate = new(1, 1);

    internal GalateaDurableDelegationDriver(
        GalateaDelegationSqliteStore store,
        IGalateaDurableDelegateTransport transport,
        string expectedRoutePolicyFingerprint,
        TimeProvider? timeProvider = null,
        Func<string>? bindingOperationIdFactory = null
    ) {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        ArgumentException.ThrowIfNullOrWhiteSpace(
            expectedRoutePolicyFingerprint
        );
        _expectedRoutePolicyFingerprint = expectedRoutePolicyFingerprint;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _bindingOperationIdFactory = bindingOperationIdFactory
            ?? (() => Guid.NewGuid().ToString("N"));

        GalateaDelegationStateSnapshot snapshot = _store.ReadSnapshot();
        if (!string.Equals(
                snapshot.Owner.RoutePolicyFingerprint,
                _expectedRoutePolicyFingerprint,
                StringComparison.Ordinal)) {
            throw new InvalidOperationException(
                "The durable driver route policy does not match its store."
            );
        }
    }

    internal async Task<GalateaDurableDelegationPulseResult> PulseAsync(
        CancellationToken cancellationToken = default
    ) {
        await _pulseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            return await PulseCoreAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        finally {
            _pulseGate.Release();
        }
    }

    private async Task<GalateaDurableDelegationPulseResult> PulseCoreAsync(
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested();
        GalateaDelegationStateSnapshot snapshot = _store.ReadSnapshot();
        RequirePolicy(snapshot);
        long now = GetUnixTimeMilliseconds();
        GalateaRouteBindingSnapshot route = snapshot.Route;
        GalateaOutboundMailSnapshot? active = ReadActiveMail(snapshot);

        LogPulse(snapshot, active);
        if (route.State == GalateaDelegationRouteState.Quarantined) {
            return new(GalateaDurableDelegationPulseStep.Quarantined,
                active?.DispatchId, route.ThreadId,
                Code: route.QuarantineCode);
        }
        if (active is not null) {
            return await PulseActiveMailAsync(
                    active,
                    route,
                    now,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }

        GalateaOutboundMailSnapshot? queued = ReadEarliestQueued(snapshot);
        if (queued is null) {
            return new(GalateaDurableDelegationPulseStep.NoWork);
        }
        if (IsTaskTooLarge(queued, snapshot.Limits)) {
            try {
                GalateaReplyNoticeSnapshot notice =
                    _store.FailQueuedMailPreflight(
                        queued.DispatchId,
                        queued.Revision
                    );
                DebugUtil.Info(
                    LogCategory,
                    "Durable queued mail failed preflight: "
                        + $"user={Safe(snapshot.Owner.UserId)}, "
                        + $"dispatchId={queued.DispatchId}, "
                        + $"code={notice.Code}.",
                    eventKind: DebugEventKind.Failure
                );
                return new(
                    GalateaDurableDelegationPulseStep.QueuedPreflightFailed,
                    queued.DispatchId,
                    Code: notice.Code
                );
            }
            catch (GalateaDelegationInboxBackpressureException backpressure) {
                LogBackpressure(snapshot, backpressure, queued.DispatchId);
                return new(
                    GalateaDurableDelegationPulseStep.InboxBackpressure,
                    queued.DispatchId
                );
            }
        }

        return route.State switch {
            GalateaDelegationRouteState.Unbound => BeginBinding(
                snapshot,
                route
            ),
            GalateaDelegationRouteState.Binding =>
                await EnsureBindingAsync(
                        snapshot,
                        route,
                        now,
                        cancellationToken
                    )
                    .ConfigureAwait(false),
            GalateaDelegationRouteState.Bound =>
                await StartQueuedMailAsync(
                        snapshot,
                        route,
                        queued,
                        cancellationToken
                    )
                    .ConfigureAwait(false),
            _ => throw new InvalidDataException(
                $"Unknown durable route state '{route.State}'."
            )
        };
    }

    private GalateaDurableDelegationPulseResult BeginBinding(
        GalateaDelegationStateSnapshot snapshot,
        GalateaRouteBindingSnapshot route
    ) {
        string operationId = _bindingOperationIdFactory();
        if (!IsCanonicalLowerHex32(operationId)) {
            throw new InvalidOperationException(
                "The binding operation factory must return 32-lowerhex text."
            );
        }
        GalateaRouteBindingSnapshot bound = _store.BeginThreadBinding(
            operationId,
            route.Revision
        );
        DebugUtil.Info(
            LogCategory,
            "Durable thread binding claimed: "
                + $"user={Safe(snapshot.Owner.UserId)}, "
                + $"bindingOperationId={operationId}, "
                + $"routeRevision={bound.Revision}."
        );
        return new(
            GalateaDurableDelegationPulseStep.BindingClaimed,
            Code: operationId
        );
    }

    private async Task<GalateaDurableDelegationPulseResult>
        EnsureBindingAsync(
        GalateaDelegationStateSnapshot snapshot,
        GalateaRouteBindingSnapshot route,
        long now,
        CancellationToken cancellationToken
    ) {
        string operationId = route.BindingOperationId
            ?? throw new InvalidDataException(
                "A Binding route has no durable operation identity."
            );
        if (route.NextEnsureAtUnixTimeMilliseconds is { } due && now < due) {
            return new(
                GalateaDurableDelegationPulseStep.Backoff,
                Code: route.EnsureLastCode
            );
        }

        GalateaDelegateBindingEstablished result;
        try {
            result = await _transport.EnsureBindingAsync(
                    new(operationId),
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) {
            return RecordBindingMiss(
                snapshot,
                route,
                operationId,
                BindingCancelledCode,
                GetUnixTimeMilliseconds()
            );
        }
        catch (GalateaDurableDelegateTransportException exception) {
            return exception.FailurePolicy switch {
                GalateaDurableDelegateFailurePolicy.RetryableBinding
                    or GalateaDurableDelegateFailurePolicy.PreWriteRejected
                    or GalateaDurableDelegateFailurePolicy.Stopped =>
                    RecordBindingMiss(
                        snapshot,
                        route,
                        operationId,
                        SafeCode(exception.Code, BindingFatalCode),
                        GetUnixTimeMilliseconds()
                    ),
                GalateaDurableDelegateFailurePolicy.DeterministicConflict
                    or GalateaDurableDelegateFailurePolicy.FatalTransport =>
                    QuarantineBinding(
                        snapshot,
                        route,
                        SafeCode(exception.Code, BindingFatalCode)
                    ),
                _ => QuarantineBinding(snapshot, route, BindingPolicyCode)
            };
        }
        catch (Exception exception) when (
            GalateaExceptionClassifier.IsNonFatal(exception)) {
            return QuarantineBinding(snapshot, route, BindingFatalCode);
        }

        if (!string.Equals(
                result.BindingOperationId,
                operationId,
                StringComparison.Ordinal)
            || !IsWireIdentity(result.ThreadId)) {
            return QuarantineBinding(snapshot, route, BindingResultCode);
        }
        GalateaRouteBindingSnapshot established =
            _store.CompleteThreadBinding(
                operationId,
                result.ThreadId,
                route.Revision
            );
        DebugUtil.Info(
            LogCategory,
            "Durable thread binding established: "
                + $"user={Safe(snapshot.Owner.UserId)}, "
                + $"bindingOperationId={operationId}, "
                + $"threadId={result.ThreadId}.",
            eventKind: DebugEventKind.Success
        );
        return new(
            GalateaDurableDelegationPulseStep.BindingEstablished,
            ThreadId: established.ThreadId
        );
    }

    private GalateaDurableDelegationPulseResult RecordBindingMiss(
        GalateaDelegationStateSnapshot snapshot,
        GalateaRouteBindingSnapshot route,
        string operationId,
        string code,
        long now
    ) {
        GalateaRouteBindingSnapshot deferred =
            _store.RecordThreadBindingEnsureMiss(
                operationId,
                route.Revision,
                code,
                now
            );
        DebugUtil.Warning(
            LogCategory,
            "Durable thread binding deferred: "
                + $"user={Safe(snapshot.Owner.UserId)}, "
                + $"bindingOperationId={operationId}, code={code}, "
                + $"attempt={deferred.EnsureAttemptCount}, "
                + $"nextAt={deferred.NextEnsureAtUnixTimeMilliseconds}."
        );
        return new(
            GalateaDurableDelegationPulseStep.BindingDeferred,
            Code: code
        );
    }

    private GalateaDurableDelegationPulseResult QuarantineBinding(
        GalateaDelegationStateSnapshot snapshot,
        GalateaRouteBindingSnapshot route,
        string code
    ) {
        string operationId = route.BindingOperationId
            ?? throw new InvalidDataException(
                "A Binding route has no durable operation identity."
            );
        _ = _store.QuarantineThreadBinding(
            operationId,
            route.Revision,
            code
        );
        LogQuarantine(snapshot, dispatchId: null, code);
        return new(
            GalateaDurableDelegationPulseStep.Quarantined,
            Code: code
        );
    }

    private async Task<GalateaDurableDelegationPulseResult>
        StartQueuedMailAsync(
        GalateaDelegationStateSnapshot snapshot,
        GalateaRouteBindingSnapshot route,
        GalateaOutboundMailSnapshot queued,
        CancellationToken cancellationToken
    ) {
        GalateaOutboundMailSnapshot started;
        try {
            started = _store.StartQueuedMail(
                queued.DispatchId,
                queued.Revision,
                route.Revision
            );
        }
        catch (GalateaDelegationInboxBackpressureException backpressure) {
            LogBackpressure(snapshot, backpressure, queued.DispatchId);
            return new(
                GalateaDurableDelegationPulseStep.InboxBackpressure,
                queued.DispatchId
            );
        }

        string threadId = started.RequestedThreadId
            ?? throw new InvalidDataException(
                "A Started mail has no durable requested thread."
            );
        string task = started.Body
            ?? throw new InvalidDataException(
                "A Started mail has no durable task body."
            );
        DebugUtil.Info(
            LogCategory,
            "Durable mail start requested: "
                + $"user={Safe(snapshot.Owner.UserId)}, "
                + $"dispatchId={started.DispatchId}, threadId={threadId}, "
                + $"taskUtf8Bytes={TextExtractorUtf8.GetByteCount(task)}.",
            eventKind: DebugEventKind.Start
        );
        GalateaDelegateTurnAccepted accepted;
        try {
            accepted = await _transport.StartTurnAsync(
                    new(started.DispatchId, threadId, task),
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) {
            return MarkOutcomeUnknownAfterStart(
                snapshot,
                started,
                StartCancelledCode,
                GetUnixTimeMilliseconds()
            );
        }
        catch (GalateaDurableDelegateTransportException exception) {
            return MarkOutcomeUnknownAfterStart(
                snapshot,
                started,
                SafeCode(exception.Code, StartExceptionCode),
                GetUnixTimeMilliseconds()
            );
        }
        catch (Exception exception) when (
            GalateaExceptionClassifier.IsNonFatal(exception)) {
            return MarkOutcomeUnknownAfterStart(
                snapshot,
                started,
                StartExceptionCode,
                GetUnixTimeMilliseconds()
            );
        }

        if (!string.Equals(
                accepted.DispatchId,
                started.DispatchId,
                StringComparison.Ordinal)
            || !string.Equals(
                accepted.ThreadId,
                threadId,
                StringComparison.Ordinal)
            || !IsWireIdentity(accepted.TurnId)) {
            return QuarantineActive(
                snapshot,
                started,
                StartResultCode
            );
        }
        GalateaOutboundMailSnapshot persisted =
            _store.RecordMailAccepted(
                started.DispatchId,
                started.Revision,
                accepted.ThreadId,
                accepted.TurnId
            );
        DebugUtil.Info(
            LogCategory,
            "Durable mail accepted: "
                + $"user={Safe(snapshot.Owner.UserId)}, "
                + $"dispatchId={persisted.DispatchId}, "
                + $"threadId={accepted.ThreadId}, "
                + $"turnId={accepted.TurnId}.",
            eventKind: DebugEventKind.Success
        );
        return new(
            GalateaDurableDelegationPulseStep.MailAccepted,
            persisted.DispatchId,
            accepted.ThreadId,
            accepted.TurnId
        );
    }

    private GalateaDurableDelegationPulseResult MarkOutcomeUnknownAfterStart(
        GalateaDelegationStateSnapshot snapshot,
        GalateaOutboundMailSnapshot started,
        string code,
        long now
    ) {
        GalateaOutboundMailSnapshot unknown = _store.MarkMailOutcomeUnknown(
            started.DispatchId,
            started.Revision,
            code,
            now
        );
        LogOutcomeUnknown(snapshot, unknown, code);
        return new(
            GalateaDurableDelegationPulseStep.MailOutcomeUnknown,
            unknown.DispatchId,
            unknown.RequestedThreadId,
            Code: code
        );
    }

    private async Task<GalateaDurableDelegationPulseResult>
        PulseActiveMailAsync(
        GalateaOutboundMailSnapshot mail,
        GalateaRouteBindingSnapshot route,
        long now,
        CancellationToken cancellationToken
    ) {
        GalateaDelegationStateSnapshot snapshot = _store.ReadSnapshot();
        return mail.State switch {
            GalateaDurableMailState.Started => RecoverStarted(
                snapshot,
                mail,
                now
            ),
            GalateaDurableMailState.OutcomeUnknown
                or GalateaDurableMailState.Accepted =>
                await InspectActiveMailAsync(
                        snapshot,
                        mail,
                        route,
                        now,
                        cancellationToken
                    )
                    .ConfigureAwait(false),
            GalateaDurableMailState.Quarantined =>
                new(GalateaDurableDelegationPulseStep.Quarantined,
                    mail.DispatchId, route.ThreadId,
                    Code: mail.TerminalCode),
            _ => throw new InvalidDataException(
                $"Route active mail has invalid state '{mail.State}'."
            )
        };
    }

    private GalateaDurableDelegationPulseResult RecoverStarted(
        GalateaDelegationStateSnapshot snapshot,
        GalateaOutboundMailSnapshot mail,
        long now
    ) {
        GalateaOutboundMailSnapshot unknown = _store.MarkMailOutcomeUnknown(
            mail.DispatchId,
            mail.Revision,
            RecoveredStartedCode,
            now
        );
        LogOutcomeUnknown(snapshot, unknown, RecoveredStartedCode);
        return new(
            GalateaDurableDelegationPulseStep.RecoveredStarted,
            mail.DispatchId,
            mail.RequestedThreadId,
            Code: RecoveredStartedCode
        );
    }

    private async Task<GalateaDurableDelegationPulseResult>
        InspectActiveMailAsync(
        GalateaDelegationStateSnapshot snapshot,
        GalateaOutboundMailSnapshot mail,
        GalateaRouteBindingSnapshot route,
        long now,
        CancellationToken cancellationToken
    ) {
        if (mail.NextReconcileAtUnixTimeMilliseconds is { } due && now < due) {
            return new(
                GalateaDurableDelegationPulseStep.Backoff,
                mail.DispatchId,
                route.ThreadId,
                Code: mail.ReconcileLastCode
            );
        }
        string threadId = route.ThreadId
            ?? throw new InvalidDataException(
                "An active Bound route has no thread identity."
            );
        string task = mail.Body
            ?? throw new InvalidDataException(
                "An active nonterminal mail has no task body."
            );
        GalateaDelegateDispatchInspection inspection;
        try {
            inspection = await _transport.InspectDispatchAsync(
                    new(mail.DispatchId, threadId, task),
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) {
            return RecordPollMiss(
                snapshot,
                mail,
                InspectionCancelledCode,
                GetUnixTimeMilliseconds()
            );
        }
        catch (GalateaDurableDelegateTransportException exception) {
            return exception.FailurePolicy switch {
                GalateaDurableDelegateFailurePolicy.InspectionUnavailable
                    or GalateaDurableDelegateFailurePolicy.PreWriteRejected
                    or GalateaDurableDelegateFailurePolicy.Stopped =>
                    RecordPollMiss(
                        snapshot,
                        mail,
                        SafeCode(exception.Code, InspectionFatalCode),
                        GetUnixTimeMilliseconds()
                    ),
                GalateaDurableDelegateFailurePolicy.DeterministicConflict
                    or GalateaDurableDelegateFailurePolicy.FatalTransport =>
                    QuarantineActive(
                        snapshot,
                        mail,
                        SafeCode(exception.Code, InspectionFatalCode)
                    ),
                _ => QuarantineActive(
                    snapshot,
                    mail,
                    InspectionPolicyCode
                )
            };
        }
        catch (Exception exception) when (
            GalateaExceptionClassifier.IsNonFatal(exception)) {
            return QuarantineActive(snapshot, mail, InspectionFatalCode);
        }

        if (!string.Equals(
                inspection.DispatchId,
                mail.DispatchId,
                StringComparison.Ordinal)
            || !string.Equals(
                inspection.ThreadId,
                threadId,
                StringComparison.Ordinal)) {
            return QuarantineActive(snapshot, mail, InspectionResultCode);
        }
        return inspection switch {
            GalateaDelegateDispatchInspection.NotFound => RecordPollMiss(
                snapshot,
                mail,
                NotFoundCode,
                GetUnixTimeMilliseconds()
            ),
            GalateaDelegateDispatchInspection.Running running =>
                RecordRunning(snapshot, mail, running),
            GalateaDelegateDispatchInspection.Completed completed =>
                RecordCompleted(snapshot, mail, completed),
            GalateaDelegateDispatchInspection.Failed failed =>
                RecordFailed(snapshot, mail, failed),
            GalateaDelegateDispatchInspection.Ambiguous ambiguous =>
                QuarantineActive(
                    snapshot,
                    mail,
                    SafeCode(ambiguous.Code, InspectionResultCode)
                ),
            _ => QuarantineActive(snapshot, mail, InspectionResultCode)
        };
    }

    private GalateaDurableDelegationPulseResult RecordPollMiss(
        GalateaDelegationStateSnapshot snapshot,
        GalateaOutboundMailSnapshot mail,
        string code,
        long now
    ) {
        GalateaOutboundMailSnapshot deferred = _store.RecordMailPollMiss(
            mail.DispatchId,
            mail.Revision,
            code,
            now
        );
        DebugUtil.Info(
            LogCategory,
            "Durable dispatch inspection deferred: "
                + $"user={Safe(snapshot.Owner.UserId)}, "
                + $"dispatchId={mail.DispatchId}, code={code}, "
                + $"attempt={deferred.ReconcileAttemptCount}, "
                + $"nextAt={deferred.NextReconcileAtUnixTimeMilliseconds}."
        );
        return new(
            code == NotFoundCode
                ? GalateaDurableDelegationPulseStep.InspectionNotFound
                : GalateaDurableDelegationPulseStep.Backoff,
            mail.DispatchId,
            mail.RequestedThreadId,
            Code: code
        );
    }

    private GalateaDurableDelegationPulseResult RecordRunning(
        GalateaDelegationStateSnapshot snapshot,
        GalateaOutboundMailSnapshot mail,
        GalateaDelegateDispatchInspection.Running running
    ) {
        if (!IsWireIdentity(running.TurnId)) {
            return QuarantineActive(snapshot, mail, InspectionResultCode);
        }
        if (mail.State == GalateaDurableMailState.Accepted) {
            if (!string.Equals(
                    mail.AcceptedThreadId,
                    running.ThreadId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    mail.AcceptedTurnId,
                    running.TurnId,
                    StringComparison.Ordinal)) {
                return QuarantineActive(snapshot, mail, InspectionTurnCode);
            }
            return new(
                GalateaDurableDelegationPulseStep.AcceptedRunning,
                mail.DispatchId,
                running.ThreadId,
                running.TurnId
            );
        }
        GalateaOutboundMailSnapshot accepted = _store.RecordMailAccepted(
            mail.DispatchId,
            mail.Revision,
            running.ThreadId,
            running.TurnId
        );
        return new(
            GalateaDurableDelegationPulseStep.MailAccepted,
            accepted.DispatchId,
            running.ThreadId,
            running.TurnId
        );
    }

    private GalateaDurableDelegationPulseResult RecordCompleted(
        GalateaDelegationStateSnapshot snapshot,
        GalateaOutboundMailSnapshot mail,
        GalateaDelegateDispatchInspection.Completed completed
    ) {
        if (!TerminalTurnMatches(mail, completed.ThreadId, completed.TurnId)) {
            return QuarantineActive(snapshot, mail, InspectionTurnCode);
        }
        if (!TryValidateFinal(
                completed.Final,
                snapshot.Limits.MaximumReplyUtf8Bytes,
                out string failureCode)) {
            return RecordTerminalFailure(
                snapshot,
                mail,
                completed.ThreadId,
                completed.TurnId,
                failureCode
            );
        }
        GalateaReplyNoticeSnapshot notice;
        try {
            notice = _store.RecordCompletedMail(
                mail.DispatchId,
                mail.Revision,
                completed.ThreadId,
                completed.TurnId,
                completed.Final
            );
        }
        catch (GalateaDelegationInboxBackpressureException backpressure) {
            LogBackpressure(snapshot, backpressure, mail.DispatchId);
            return new(
                GalateaDurableDelegationPulseStep.InboxBackpressure,
                mail.DispatchId,
                mail.RequestedThreadId
            );
        }
        LogTerminal(snapshot, notice);
        return new(
            GalateaDurableDelegationPulseStep.TerminalCompleted,
            mail.DispatchId,
            completed.ThreadId,
            completed.TurnId
        );
    }

    private GalateaDurableDelegationPulseResult RecordFailed(
        GalateaDelegationStateSnapshot snapshot,
        GalateaOutboundMailSnapshot mail,
        GalateaDelegateDispatchInspection.Failed failed
    ) {
        if (!TerminalTurnMatches(mail, failed.ThreadId, failed.TurnId)) {
            return QuarantineActive(snapshot, mail, InspectionTurnCode);
        }
        string code = SafeCode(failed.Code, "DELEGATE_FAILURE");
        return RecordTerminalFailure(
            snapshot,
            mail,
            failed.ThreadId,
            failed.TurnId,
            code
        );
    }

    private GalateaDurableDelegationPulseResult RecordTerminalFailure(
        GalateaDelegationStateSnapshot snapshot,
        GalateaOutboundMailSnapshot mail,
        string threadId,
        string turnId,
        string code
    ) {
        string body = GalateaDelegationDurableContract
            .CreateDeliveryFailureNotice(FailureStage, code);
        GalateaReplyNoticeSnapshot notice;
        try {
            notice = _store.RecordFailedMail(
                mail.DispatchId,
                mail.Revision,
                threadId,
                turnId,
                FailureStage,
                code,
                body
            );
        }
        catch (GalateaDelegationInboxBackpressureException backpressure) {
            LogBackpressure(snapshot, backpressure, mail.DispatchId);
            return new(
                GalateaDurableDelegationPulseStep.InboxBackpressure,
                mail.DispatchId,
                mail.RequestedThreadId,
                Code: code
            );
        }
        LogTerminal(snapshot, notice);
        return new(
            GalateaDurableDelegationPulseStep.TerminalFailed,
            mail.DispatchId,
            threadId,
            turnId,
            code
        );
    }

    private static bool TryValidateFinal(
        string? final,
        int maximumUtf8Bytes,
        out string failureCode
    ) {
        if (string.IsNullOrWhiteSpace(final)) {
            failureCode = FinalBlankCode;
            return false;
        }
        try {
            if (TextExtractorUtf8.GetByteCount(final) > maximumUtf8Bytes) {
                failureCode = FinalTooLargeCode;
                return false;
            }
        }
        catch (EncoderFallbackException) {
            failureCode = FinalInvalidUnicodeCode;
            return false;
        }
        failureCode = string.Empty;
        return true;
    }

    private GalateaDurableDelegationPulseResult QuarantineActive(
        GalateaDelegationStateSnapshot snapshot,
        GalateaOutboundMailSnapshot mail,
        string code
    ) {
        _ = _store.QuarantineActiveMail(
            mail.DispatchId,
            mail.Revision,
            code
        );
        LogQuarantine(snapshot, mail.DispatchId, code);
        return new(
            GalateaDurableDelegationPulseStep.Quarantined,
            mail.DispatchId,
            mail.RequestedThreadId,
            Code: code
        );
    }

    private static bool TerminalTurnMatches(
        GalateaOutboundMailSnapshot mail,
        string threadId,
        string turnId
    ) => IsWireIdentity(threadId)
        && IsWireIdentity(turnId)
        && (mail.State != GalateaDurableMailState.Accepted
            || string.Equals(
                mail.AcceptedThreadId,
                threadId,
                StringComparison.Ordinal)
                && string.Equals(
                    mail.AcceptedTurnId,
                    turnId,
                    StringComparison.Ordinal));

    private static GalateaOutboundMailSnapshot? ReadActiveMail(
        GalateaDelegationStateSnapshot snapshot
    ) => snapshot.Route.ActiveDispatchId is { } dispatchId
        ? snapshot.Mails.SingleOrDefault(mail => string.Equals(
            mail.DispatchId,
            dispatchId,
            StringComparison.Ordinal))
            ?? throw new InvalidDataException(
                "The durable route active dispatch has no mail."
            )
        : null;

    private static GalateaOutboundMailSnapshot? ReadEarliestQueued(
        GalateaDelegationStateSnapshot snapshot
    ) {
        Dictionary<string, long> captureOrder = snapshot.Captures.ToDictionary(
            static capture => capture.SourceActionAddress,
            static capture => capture.CaptureSequence,
            StringComparer.Ordinal
        );
        return snapshot.Mails
            .Where(static mail => mail.IsCodexRouted
                && mail.State == GalateaDurableMailState.Queued)
            .OrderBy(mail => captureOrder[mail.SourceActionAddress])
            .ThenBy(static mail => mail.ArtifactOrdinal)
            .FirstOrDefault();
    }

    private static bool IsTaskTooLarge(
        GalateaOutboundMailSnapshot mail,
        GalateaDelegationStoreLimits limits
    ) => mail.Body is null
        || TextExtractorUtf8.GetByteCount(mail.Body)
            > limits.MaximumTaskUtf8Bytes;

    private void RequirePolicy(GalateaDelegationStateSnapshot snapshot) {
        if (!string.Equals(
                snapshot.Owner.RoutePolicyFingerprint,
                _expectedRoutePolicyFingerprint,
                StringComparison.Ordinal)
            || !string.Equals(
                snapshot.Route.RoutePolicyFingerprint,
                _expectedRoutePolicyFingerprint,
                StringComparison.Ordinal)) {
            throw new InvalidDataException(
                "The durable route policy changed after driver construction."
            );
        }
    }

    private long GetUnixTimeMilliseconds() {
        long value = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        return value >= 0
            ? value
            : throw new InvalidOperationException(
                "Durable delegation time must be non-negative Unix time."
            );
    }

    private static bool IsCanonicalLowerHex32(string? value) =>
        value is { Length: 32 }
        && value.All(static character => character is >= '0' and <= '9'
            or >= 'a' and <= 'f');

    private static bool IsWireIdentity(string? value) {
        if (string.IsNullOrWhiteSpace(value)
            || GalateaMailboxText.ContainsHeaderLineBreak(value)) {
            return false;
        }
        try {
            return TextExtractorUtf8.GetByteCount(value)
                <= GalateaDelegationStateBounds.MaximumIdentityUtf8Bytes;
        }
        catch (EncoderFallbackException) {
            return false;
        }
    }

    private static string SafeCode(string? value, string fallback) =>
        GalateaDelegationDurableContract.NormalizeFailureToken(
            value,
            fallback
        );

    private static string Safe(string value) =>
        GalateaMailboxText.SummarizeForLog(value);

    private static void LogPulse(
        GalateaDelegationStateSnapshot snapshot,
        GalateaOutboundMailSnapshot? active
    ) => DebugUtil.Trace(
        LogCategory,
        "Durable pulse selected: "
            + $"user={Safe(snapshot.Owner.UserId)}, "
            + $"storeRevision={snapshot.StoreRevision}, "
            + $"routeState={snapshot.Route.State}, "
            + $"activeDispatchId={active?.DispatchId ?? "<none>"}."
    );

    private static void LogOutcomeUnknown(
        GalateaDelegationStateSnapshot snapshot,
        GalateaOutboundMailSnapshot mail,
        string code
    ) => DebugUtil.Warning(
        LogCategory,
        "Durable mail outcome unknown: "
            + $"user={Safe(snapshot.Owner.UserId)}, "
            + $"dispatchId={mail.DispatchId}, code={code}, "
            + $"attempt={mail.ReconcileAttemptCount}, "
            + $"nextAt={mail.NextReconcileAtUnixTimeMilliseconds}."
    );

    private static void LogTerminal(
        GalateaDelegationStateSnapshot snapshot,
        GalateaReplyNoticeSnapshot notice
    ) => DebugUtil.Info(
        LogCategory,
        "Durable terminal notice ready: "
            + $"user={Safe(snapshot.Owner.UserId)}, "
            + $"dispatchId={notice.DispatchId}, kind={notice.Kind}, "
            + $"sequence={notice.CompletionSequence}, "
            + $"noticeUtf8Bytes={TextExtractorUtf8.GetByteCount(notice.Body)}.",
        eventKind: DebugEventKind.Success
    );

    private static void LogQuarantine(
        GalateaDelegationStateSnapshot snapshot,
        string? dispatchId,
        string code
    ) => DebugUtil.Info(
        LogCategory,
        "Durable delegation quarantined: "
            + $"user={Safe(snapshot.Owner.UserId)}, "
            + $"dispatchId={dispatchId ?? "<none>"}, code={code}.",
        eventKind: DebugEventKind.Failure
    );

    private static void LogBackpressure(
        GalateaDelegationStateSnapshot snapshot,
        GalateaDelegationInboxBackpressureException backpressure,
        string dispatchId
    ) => DebugUtil.Trace(
        LogCategory,
        "Durable delegation inbox backpressure: "
            + $"user={Safe(snapshot.Owner.UserId)}, "
            + $"dispatchId={dispatchId}, "
            + $"currentCount={backpressure.CurrentCount}, "
            + $"currentUtf8Bytes={backpressure.CurrentUtf8Bytes}."
    );
}
