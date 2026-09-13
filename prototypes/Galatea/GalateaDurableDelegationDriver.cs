using Atelia.Diagnostics;
using Atelia.Galatea.Server.Mailbox;
using System.Diagnostics;
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
    MailRequeued,
    RecoveredStarted,
    InspectionNotFound,
    AcceptedTurnNotVisible,
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
    internal static readonly TimeSpan DebugRunningLivenessInterval =
        TimeSpan.FromSeconds(60);
    private const string LogCategory = "Galatea.Delegation";
    private const string RecoveredStartedCode = "RECOVERED_STARTED";
    private const string NotFoundCode = "NOT_FOUND";
    private const string AcceptedTurnNotVisibleCode =
        GalateaDelegateDispatchInspection.AcceptedTurnNotVisible.FailureCode;
    private const string BindingCancelledCode = "BINDING_CANCELLED";
    private const string BindingFatalCode = "BINDING_FATAL_TRANSPORT";
    private const string BindingResultCode = "BINDING_RESULT_IDENTITY_MISMATCH";
    private const string StartCancelledCode = "START_CANCELLED";
    private const string StartExceptionCode = "START_EXCEPTION";
    private const string StartResultCode = "START_RESULT_IDENTITY_MISMATCH";
    private const string InspectionCancelledCode = "INSPECTION_CANCELLED";
    private const string InspectionFatalCode = "INSPECTION_FATAL_TRANSPORT";
    private const string InspectionResultCode = "INSPECTION_RESULT_IDENTITY_MISMATCH";
    private const string InspectionTurnCode = "INSPECTION_TURN_IDENTITY_MISMATCH";
    private const string FinalBlankCode = "FINAL_BLANK";
    private const string FinalTooLargeCode = "FINAL_TOO_LARGE";
    private const string FinalInvalidUnicodeCode = "FINAL_INVALID_UNICODE";
    private const string FailureStage = "inspect-dispatch";

    private readonly GalateaDelegationSqliteStore _store;
    private readonly IGalateaDurableDelegateTransport _transport;
    private readonly string _homeDir;
    private readonly TimeProvider _timeProvider;
    private readonly Func<string> _bindingOperationIdFactory;
    private readonly SemaphoreSlim _pulseGate = new(1, 1);
    private string? _retryDispatchId;
    private long _retryRevision = -1;
    private long _retryObservedTimestamp;
    private TimeSpan _retryDelay;
    private string? _debugRunningLivenessDispatchId;
    private long _debugNextRunningLivenessAtUnixTimeMilliseconds;

    internal GalateaDurableDelegationDriver(
        GalateaDelegationSqliteStore store,
        IGalateaDurableDelegateTransport transport,
        string homeDir,
        TimeProvider? timeProvider = null,
        Func<string>? bindingOperationIdFactory = null
    ) {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        ArgumentException.ThrowIfNullOrWhiteSpace(
            homeDir
        );
        _homeDir = homeDir;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _bindingOperationIdFactory = bindingOperationIdFactory
            ?? (() => Guid.NewGuid().ToString("N"));

        GalateaDelegationStateSnapshot snapshot = _store.ReadSnapshot();
        LogStartupReconciliationScheduled(snapshot);
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
        long now = GetUnixTimeMilliseconds();
        GalateaRouteBindingSnapshot route = snapshot.Route;
        GalateaOutboundMailSnapshot? active = ReadActiveMail(snapshot);

        if (route.State == GalateaDelegationRouteState.Quarantined) {
            GalateaOutboundMailSnapshot? stranded = active ?? ReadEarliestQueued(snapshot);
            if (stranded is not null && IsRecoverableLegacyQuarantine(route.QuarantineCode)) {
                return FinishLocally(snapshot, stranded,
                    active is null ? "NOT_DISPATCHED_RETRIES_EXHAUSTED" : "RESULT_UNCONFIRMED");
            }
            return new(GalateaDurableDelegationPulseStep.Quarantined,
                active?.DispatchId, route.ThreadId,
                Code: route.QuarantineCode);
        }
        if (active is not null) {
            if (active.RecoveryFailureCount >= GalateaDelegationDurableContract.MaximumRecoveryFailures) {
                return FinishLocally(snapshot, active, "RESULT_UNCONFIRMED");
            }
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

        if (queued.RecoveryFailureCount >= GalateaDelegationDurableContract.MaximumRecoveryFailures) {
            return FinishLocally(snapshot, queued,
                queued.RecoveryLastCode ?? "NOT_DISPATCHED_RETRIES_EXHAUSTED");
        }
        if (!RetryIsDue(queued, now)) {
            return new(GalateaDurableDelegationPulseStep.Backoff,
                queued.DispatchId, route.ThreadId, Code: queued.RecoveryLastCode);
        }
        return route.State switch {
            GalateaDelegationRouteState.Unbound => BeginBinding(
                snapshot,
                route,
                queued
            ),
            GalateaDelegationRouteState.Binding =>
                await EnsureBindingAsync(
                        snapshot,
                        route,
                        queued,
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
        GalateaRouteBindingSnapshot route,
        GalateaOutboundMailSnapshot queued
    ) {
        string operationId = _bindingOperationIdFactory();
        if (!IsCanonicalLowerHex32(operationId)) {
            throw new InvalidOperationException(
                "The binding operation factory must return 32-lowerhex text."
            );
        }
        GalateaRouteBindingSnapshot bound = _store.BeginThreadBinding(
            operationId,
            route.Revision,
            queued.DispatchId,
            queued.Revision
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
        GalateaOutboundMailSnapshot queued,
        CancellationToken cancellationToken
    ) {
        string operationId = route.BindingOperationId
            ?? throw new InvalidDataException(
                "A Binding route has no durable operation identity."
            );
        GalateaDelegateBindingEstablished result;
        try {
            result = await _transport.EnsureBindingAsync(
                    new(operationId, _homeDir),
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            throw;
        }
        catch (OperationCanceledException) {
            return RecordBindingMiss(snapshot, route, queued, operationId,
                BindingCancelledCode, GetUnixTimeMilliseconds());
        }
        catch (GalateaDurableDelegateTransportException exception) {
            cancellationToken.ThrowIfCancellationRequested();
            string code = SafeCode(exception.Code, BindingFatalCode);
            return IsConfigurationFailure(code)
                ? FinishLocally(snapshot, queued, code)
                : RecordBindingMiss(snapshot, route, queued, operationId,
                    code, GetUnixTimeMilliseconds());
        }
        catch (Exception exception) when (GalateaExceptionClassifier.IsNonFatal(exception)) {
            cancellationToken.ThrowIfCancellationRequested();
            return RecordBindingMiss(snapshot, route, queued, operationId,
                BindingFatalCode, GetUnixTimeMilliseconds());
        }

        if (!string.Equals(
                result.BindingOperationId,
                operationId,
                StringComparison.Ordinal)
            || !IsWireIdentity(result.ThreadId)) {
            return FinishLocally(snapshot, queued, BindingResultCode);
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
        GalateaOutboundMailSnapshot queued,
        string operationId,
        string code,
        long now
    ) {
        GalateaOutboundMailSnapshot deferred;
        try {
            deferred = _store.RecordThreadBindingEnsureMiss(operationId,
                route.Revision, queued.DispatchId, queued.Revision, code, now);
        }
        catch (GalateaDelegationInboxBackpressureException backpressure) {
            LogBackpressure(snapshot, backpressure, queued.DispatchId);
            return new(GalateaDurableDelegationPulseStep.InboxBackpressure, queued.DispatchId);
        }
        ObserveRetry(deferred, now);
        DebugUtil.Warning(LogCategory,
            $"Durable thread binding deferred: user={Safe(snapshot.Owner.UserId)}, "
            + $"dispatchId={queued.DispatchId}, code={code}, "
            + $"attempt={deferred.RecoveryFailureCount}, nextAt={deferred.NextRetryAtUnixTimeMilliseconds}.");
        return new(deferred.State == GalateaDurableMailState.TerminalFailed
                ? GalateaDurableDelegationPulseStep.TerminalFailed
                : GalateaDurableDelegationPulseStep.BindingDeferred,
            queued.DispatchId, Code: deferred.TerminalCode ?? code);
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
                    new(started.DispatchId, threadId, task, _homeDir),
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            throw;
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
            cancellationToken.ThrowIfCancellationRequested();
            string code = SafeCode(exception.Code, StartExceptionCode);
            if (exception.DispatchState == GalateaDelegateDispatchState.NotDispatched) {
                if (IsConfigurationFailure(code)) {
                    return FinishLocally(_store.ReadSnapshot(), started, code, exception.DispatchState);
                }
                GalateaOutboundMailSnapshot requeued = _store.RequeueNotDispatchedMail(
                    started.DispatchId, started.Revision, _store.ReadSnapshot().Route.Revision,
                    exception.DispatchState, code, IsInvalidBinding(code), GetUnixTimeMilliseconds());
                ObserveRetry(requeued, GetUnixTimeMilliseconds());
                return new(requeued.State == GalateaDurableMailState.TerminalFailed
                        ? GalateaDurableDelegationPulseStep.TerminalFailed
                        : GalateaDurableDelegationPulseStep.MailRequeued,
                    started.DispatchId, Code: requeued.TerminalCode ?? code);
            }
            return MarkOutcomeUnknownAfterStart(snapshot, started, code, GetUnixTimeMilliseconds());
        }
        catch (Exception exception) when (
            GalateaExceptionClassifier.IsNonFatal(exception)) {
            cancellationToken.ThrowIfCancellationRequested();
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
            return RejectRemoteResult(
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
        ObserveRetry(unknown, now);
        LogOutcomeUnknown(snapshot, unknown, code);
        return new(
            unknown.State == GalateaDurableMailState.TerminalFailed
                ? GalateaDurableDelegationPulseStep.TerminalFailed
                : GalateaDurableDelegationPulseStep.MailOutcomeUnknown,
            unknown.DispatchId,
            unknown.RequestedThreadId,
            Code: unknown.TerminalCode ?? code
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
        ObserveRetry(unknown, now);
        LogOutcomeUnknown(snapshot, unknown, RecoveredStartedCode);
        return new(
            unknown.State == GalateaDurableMailState.TerminalFailed
                ? GalateaDurableDelegationPulseStep.TerminalFailed
                : GalateaDurableDelegationPulseStep.RecoveredStarted,
            mail.DispatchId,
            mail.RequestedThreadId,
            Code: unknown.TerminalCode ?? RecoveredStartedCode
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
        if (!RetryIsDue(mail, now)) {
            return new(
                GalateaDurableDelegationPulseStep.Backoff,
                mail.DispatchId,
                route.ThreadId,
                Code: mail.RecoveryLastCode
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
        GalateaInspectDelegateDispatchRequest request = mail.State switch {
            GalateaDurableMailState.OutcomeUnknown =>
                GalateaInspectDelegateDispatchRequest.ForOutcomeUnknown(
                    mail.DispatchId,
                    threadId,
                    task
                ),
            GalateaDurableMailState.Accepted =>
                GalateaInspectDelegateDispatchRequest.ForAccepted(
                    mail.DispatchId,
                    threadId,
                    task,
                    mail.AcceptedTurnId ?? throw new InvalidDataException(
                        "An Accepted mail has no durable turn identity."
                    )
                ),
            _ => throw new InvalidDataException(
                "Only OutcomeUnknown or Accepted mail may be inspected."
            )
        };
        try {
            inspection = await _transport.InspectDispatchAsync(
                    request,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested) {
            throw;
        }
        catch (OperationCanceledException) {
            return RecordPollMiss(
                snapshot,
                mail,
                FailureStage,
                InspectionCancelledCode,
                GetUnixTimeMilliseconds(),
                source: null
            );
        }
        catch (GalateaDurableDelegateTransportException exception) {
            cancellationToken.ThrowIfCancellationRequested();
            string code = SafeCode(exception.Code, InspectionFatalCode);
            return IsIdentityFailure(code)
                ? RejectRemoteResult(snapshot, mail, code)
                : RecordPollMiss(snapshot, mail, SafeCode(exception.Stage, FailureStage),
                    code, GetUnixTimeMilliseconds(), source: null);
        }
        catch (Exception exception) when (GalateaExceptionClassifier.IsNonFatal(exception)) {
            cancellationToken.ThrowIfCancellationRequested();
            return RecordPollMiss(snapshot, mail, FailureStage,
                InspectionFatalCode, GetUnixTimeMilliseconds(), source: null);
        }

        if (!string.Equals(
                inspection.DispatchId,
                mail.DispatchId,
                StringComparison.Ordinal)
            || !string.Equals(
                inspection.ThreadId,
                threadId,
                StringComparison.Ordinal)) {
            return RejectRemoteResult(
                snapshot,
                mail,
                InspectionResultCode,
                InspectionSource(inspection),
                FailureStage
            );
        }
        if (inspection is GalateaDelegateDispatchInspection.NotFound
            or GalateaDelegateDispatchInspection.AcceptedTurnNotVisible) {
            cancellationToken.ThrowIfCancellationRequested();
        }
        return inspection switch {
            GalateaDelegateDispatchInspection.NotFound notFound =>
                mail.State == GalateaDurableMailState.OutcomeUnknown
                    && notFound.Source
                        == GalateaDelegateInspectionSource.Persistent
                    ? RecordPollMiss(
                        snapshot,
                        mail,
                        FailureStage,
                        NotFoundCode,
                        GetUnixTimeMilliseconds(),
                        notFound.Source
                    )
                    : RejectRemoteResult(
                        snapshot,
                        mail,
                        InspectionResultCode,
                        notFound.Source,
                        FailureStage
                    ),
            GalateaDelegateDispatchInspection.AcceptedTurnNotVisible
                unavailable => mail.State == GalateaDurableMailState.Accepted
                    && unavailable.Source
                        == GalateaDelegateInspectionSource.Persistent
                    && string.Equals(
                        mail.AcceptedTurnId,
                        unavailable.TurnId,
                        StringComparison.Ordinal)
                    ? RecordPollMiss(
                        snapshot,
                        mail,
                        FailureStage,
                        AcceptedTurnNotVisibleCode,
                        GetUnixTimeMilliseconds(),
                        unavailable.Source
                    )
                    : RejectRemoteResult(
                        snapshot,
                        mail,
                        InspectionTurnCode,
                        unavailable.Source,
                        FailureStage
                    ),
            GalateaDelegateDispatchInspection.Running running =>
                RecordRunning(
                    snapshot,
                    mail,
                    running,
                    cancellationToken
                ),
            GalateaDelegateDispatchInspection.Completed completed =>
                RecordCompleted(snapshot, mail, completed),
            GalateaDelegateDispatchInspection.Failed failed =>
                RecordFailed(snapshot, mail, failed),
            GalateaDelegateDispatchInspection.Ambiguous ambiguous =>
                RejectRemoteResult(
                    snapshot,
                    mail,
                    SafeCode(ambiguous.Code, InspectionResultCode),
                    ambiguous.Source,
                    FailureStage
                ),
            _ => RejectRemoteResult(
                snapshot,
                mail,
                InspectionResultCode,
                InspectionSource(inspection),
                FailureStage
            )
        };
    }

    private GalateaDurableDelegationPulseResult RecordPollMiss(
        GalateaDelegationStateSnapshot snapshot,
        GalateaOutboundMailSnapshot mail,
        string stage,
        string code,
        long now,
        GalateaDelegateInspectionSource? source
    ) {
        GalateaOutboundMailSnapshot deferred = _store.RecordMailPollMiss(
            mail.DispatchId,
            mail.Revision,
            code,
            now
        );
        ObserveRetry(deferred, now);
        if (deferred.State == GalateaDurableMailState.TerminalFailed) {
            return new(GalateaDurableDelegationPulseStep.TerminalFailed,
                deferred.DispatchId, Code: deferred.TerminalCode);
        }
        string message = FormatInspectionDeferredDiagnostic(
            snapshot,
            mail,
            deferred,
            stage,
            code,
            source
        );
        if (ShouldWarnInspectionDeferred(mail, code)) {
            DebugUtil.Warning(LogCategory, message);
        }
        else {
            DebugUtil.Info(LogCategory, message);
        }
        return new(
            code switch {
                NotFoundCode =>
                    GalateaDurableDelegationPulseStep.InspectionNotFound,
                AcceptedTurnNotVisibleCode
                    when mail.State == GalateaDurableMailState.Accepted =>
                    GalateaDurableDelegationPulseStep.AcceptedTurnNotVisible,
                _ => GalateaDurableDelegationPulseStep.Backoff
            },
            mail.DispatchId,
            mail.RequestedThreadId,
            Code: code
        );
    }

    internal static string FormatInspectionDeferredDiagnostic(
        GalateaDelegationStateSnapshot snapshot,
        GalateaOutboundMailSnapshot before,
        GalateaOutboundMailSnapshot after,
        string stage,
        string code,
        GalateaDelegateInspectionSource? source
    ) => "Durable dispatch inspection deferred: "
        + $"user={Safe(snapshot.Owner.UserId)}, "
        + $"dispatchId={before.DispatchId}, "
        + $"selectorMode={SelectorMode(before)}, "
        + $"knownTurnId={before.AcceptedTurnId ?? "<none>"}, "
        + $"source={InspectionSourceText(source)}, "
        + $"stage={stage}, code={code}, "
        + $"recovered={(before.RecoveryFailureCount > 0).ToString().ToLowerInvariant()}, "
        + $"attempt={after.RecoveryFailureCount}, "
        + $"nextAt={after.NextRetryAtUnixTimeMilliseconds}.";

    internal static bool ShouldWarnInspectionDeferred(
        GalateaOutboundMailSnapshot mail,
        string code
    ) => mail.State == GalateaDurableMailState.Accepted
        && code == AcceptedTurnNotVisibleCode;

    private GalateaDurableDelegationPulseResult RecordRunning(
        GalateaDelegationStateSnapshot snapshot,
        GalateaOutboundMailSnapshot mail,
        GalateaDelegateDispatchInspection.Running running,
        CancellationToken cancellationToken
    ) {
        if (!IsWireIdentity(running.TurnId)) {
            return RejectRemoteResult(
                snapshot,
                mail,
                InspectionResultCode,
                running.Source,
                FailureStage
            );
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
                return RejectRemoteResult(
                    snapshot,
                    mail,
                    InspectionTurnCode,
                    running.Source,
                    FailureStage
                );
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (running.Source != GalateaDelegateInspectionSource.Live) {
                return RecordPollMiss(snapshot, mail, FailureStage,
                    "RUNNING_NOT_CONFIRMED", GetUnixTimeMilliseconds(), running.Source);
            }
            if (mail.RecoveryFailureCount > 0) {
                _ = _store.ConfirmAcceptedMailRunning(
                    mail.DispatchId,
                    mail.Revision,
                    running.ThreadId,
                    running.TurnId
                );
            }
            LogRunningConfirmed(snapshot, mail, running);
            return new(
                GalateaDurableDelegationPulseStep.AcceptedRunning,
                mail.DispatchId,
                running.ThreadId,
                running.TurnId
            );
        }
        cancellationToken.ThrowIfCancellationRequested();
        GalateaOutboundMailSnapshot accepted = _store.RecordMailAccepted(
            mail.DispatchId,
            mail.Revision,
            running.ThreadId,
            running.TurnId
        );
        if (running.Source != GalateaDelegateInspectionSource.Live) {
            return RecordPollMiss(_store.ReadSnapshot(), accepted, FailureStage,
                "RUNNING_NOT_CONFIRMED", GetUnixTimeMilliseconds(), running.Source);
        }
        if (accepted.RecoveryFailureCount > 0) {
            _ = _store.ConfirmAcceptedMailRunning(accepted.DispatchId, accepted.Revision,
                running.ThreadId, running.TurnId);
        }
        LogRunningConfirmed(snapshot, mail, running);
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
            return RejectRemoteResult(
                snapshot,
                mail,
                InspectionTurnCode,
                completed.Source,
                FailureStage
            );
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
                failureCode,
                completed.Source
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
        LogTerminal(snapshot, mail, notice, completed.Source);
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
            return RejectRemoteResult(
                snapshot,
                mail,
                InspectionTurnCode,
                failed.Source,
                FailureStage
            );
        }
        string code = SafeCode(failed.Code, "DELEGATE_FAILURE");
        return RecordTerminalFailure(
            snapshot,
            mail,
            failed.ThreadId,
            failed.TurnId,
            code,
            failed.Source
        );
    }

    private GalateaDurableDelegationPulseResult RecordTerminalFailure(
        GalateaDelegationStateSnapshot snapshot,
        GalateaOutboundMailSnapshot mail,
        string threadId,
        string turnId,
        string code,
        GalateaDelegateInspectionSource? source = null
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
        LogTerminal(snapshot, mail, notice, source);
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

    private GalateaDurableDelegationPulseResult RejectRemoteResult(
        GalateaDelegationStateSnapshot snapshot,
        GalateaOutboundMailSnapshot mail,
        string code,
        GalateaDelegateInspectionSource? source = null,
        string? stage = null
    ) {
        DebugUtil.Warning(LogCategory,
            $"Durable delegate result rejected: user={Safe(snapshot.Owner.UserId)}, "
            + $"dispatchId={mail.DispatchId}, stage={stage ?? FailureStage}, code={code}, "
            + $"source={InspectionSourceText(source)}.");
        return FinishLocally(_store.ReadSnapshot(), mail, code);
    }

    private GalateaDurableDelegationPulseResult FinishLocally(
        GalateaDelegationStateSnapshot snapshot,
        GalateaOutboundMailSnapshot mail,
        string code,
        GalateaDelegateDispatchState dispatchState = GalateaDelegateDispatchState.MayHaveDispatched
    ) {
        try {
            GalateaReplyNoticeSnapshot notice = _store.FinishMailLocally(
                mail.DispatchId, mail.Revision, snapshot.Route.Revision, code,
                resetBinding: true, dispatchState: dispatchState);
            LogTerminal(snapshot, mail, notice, source: null);
            return new(GalateaDurableDelegationPulseStep.TerminalFailed,
                mail.DispatchId, mail.RequestedThreadId, Code: notice.Code);
        }
        catch (GalateaDelegationInboxBackpressureException backpressure) {
            LogBackpressure(snapshot, backpressure, mail.DispatchId);
            return new(GalateaDurableDelegationPulseStep.InboxBackpressure,
                mail.DispatchId, Code: code);
        }
    }

    // Only remote lifecycle failures from the old permanent-quarantine policy.
    // Identity conflicts and invalid durable state still require operator recovery.
    private static bool IsRecoverableLegacyQuarantine(string? code) => code is
        "THREAD_NOT_FOUND" or "INSPECTION_UNAVAILABLE" or "INSPECTION_FATAL_TRANSPORT"
        or "BINDING_FATAL_TRANSPORT" or "BINDING_OUTCOME_UNKNOWN"
        or "SIDECAR_PROCESS_EXITED" or "SIDECAR_READY_TIMEOUT";

    private static bool IsConfigurationFailure(string code) => code is
        "INVALID_CWD" or "CWD_NOT_ALLOWED" or "INVALID_CONFIG" or "INVALID_CODEX_CONFIG"
        or "CODEX_VERSION_MISMATCH";

    private static bool IsInvalidBinding(string code) => code is
        "THREAD_NOT_FOUND" or "THREAD_NOT_RESUMABLE" or "THREAD_ID_MISMATCH"
        or "THREAD_CWD_MISMATCH" or "CWD_MISMATCH" or "THREAD_OWNERSHIP_MISMATCH";

    private static bool IsIdentityFailure(string code) => code is
        "INSPECTION_SELECTOR_MISMATCH" or "THREAD_ID_MISMATCH"
        or "THREAD_CWD_MISMATCH" or "CWD_MISMATCH" or "THREAD_OWNERSHIP_MISMATCH";

    private void ObserveRetry(GalateaOutboundMailSnapshot mail, long now) {
        if (_retryDispatchId == mail.DispatchId && _retryRevision == mail.Revision) {
            return;
        }
        _retryDispatchId = mail.DispatchId;
        _retryRevision = mail.Revision;
        _retryObservedTimestamp = _timeProvider.GetTimestamp();
        long remaining = mail.NextRetryAtUnixTimeMilliseconds is { } due
            ? Math.Clamp(due - now, 0, GalateaDelegationDurableContract.MaximumRecoveryBackoffMilliseconds)
            : 0;
        _retryDelay = TimeSpan.FromMilliseconds(remaining);
    }

    private bool RetryIsDue(GalateaOutboundMailSnapshot mail, long now) {
        ObserveRetry(mail, now);
        return _timeProvider.GetElapsedTime(_retryObservedTimestamp) >= _retryDelay;
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

    private static string SelectorMode(GalateaOutboundMailSnapshot mail) =>
        mail.State == GalateaDurableMailState.Accepted
            ? "accepted-turn"
            : "outcome-unknown-dispatch";

    private static string InspectionSourceText(
        GalateaDelegateInspectionSource? source
    ) => source switch {
        GalateaDelegateInspectionSource.Live => "live",
        GalateaDelegateInspectionSource.Persistent => "persistent",
        _ => "none"
    };

    private static GalateaDelegateInspectionSource? InspectionSource(
        GalateaDelegateDispatchInspection inspection
    ) => inspection switch {
        GalateaDelegateDispatchInspection.NotFound value => value.Source,
        GalateaDelegateDispatchInspection.Running value => value.Source,
        GalateaDelegateDispatchInspection.Completed value => value.Source,
        GalateaDelegateDispatchInspection.Failed value => value.Source,
        GalateaDelegateDispatchInspection.Ambiguous value => value.Source,
        GalateaDelegateDispatchInspection.AcceptedTurnNotVisible value =>
            value.Source,
        _ => null
    };

    [Conditional("DEBUG")]
    private static void LogStartupReconciliationScheduled(
        GalateaDelegationStateSnapshot snapshot
    ) {
        GalateaOutboundMailSnapshot? active = ReadActiveMail(snapshot);
        if (active is null
            || active.State is not (
                GalateaDurableMailState.Started
                or GalateaDurableMailState.OutcomeUnknown
                or GalateaDurableMailState.Accepted)) {
            return;
        }
        DebugUtil.Info(
            LogCategory,
            "Durable active dispatch reconciliation scheduled: "
                + $"user={Safe(snapshot.Owner.UserId)}, "
                + $"dispatchId={active.DispatchId}, state={active.State}, "
                + $"threadId={snapshot.Route.ThreadId ?? "<none>"}, "
                + $"turnId={active.AcceptedTurnId ?? "<none>"}."
        );
    }

    [Conditional("DEBUG")]
    private void LogRunningConfirmed(
        GalateaDelegationStateSnapshot snapshot,
        GalateaOutboundMailSnapshot mail,
        GalateaDelegateDispatchInspection.Running running
    ) {
        bool recovered = mail.RecoveryFailureCount > 0;
        if (!ShouldLogDebugRunningLiveness(mail.DispatchId, recovered)) {
            return;
        }
        DebugUtil.Info(
            LogCategory,
            "Durable dispatch Running confirmed: "
                + $"user={Safe(snapshot.Owner.UserId)}, "
                + $"dispatchId={mail.DispatchId}, "
                + $"selectorMode={SelectorMode(mail)}, "
                + $"knownTurnId={mail.AcceptedTurnId ?? "<none>"}, "
                + $"source={InspectionSourceText(running.Source)}, "
                + $"threadId={running.ThreadId}, turnId={running.TurnId}, "
                + $"recovered={recovered.ToString().ToLowerInvariant()}, "
                + $"clearedAttempt={mail.RecoveryFailureCount}, "
                + $"clearedCode={mail.RecoveryLastCode ?? "<none>"}.",
            eventKind: DebugEventKind.Success
        );
    }

    internal bool ShouldLogDebugRunningLiveness(
        string dispatchId,
        bool recovered
    ) {
        long now = GetUnixTimeMilliseconds();
        bool firstForDispatch = !string.Equals(
            _debugRunningLivenessDispatchId,
            dispatchId,
            StringComparison.Ordinal
        );
        if (!recovered
            && !firstForDispatch
            && now < _debugNextRunningLivenessAtUnixTimeMilliseconds) {
            return false;
        }
        _debugRunningLivenessDispatchId = dispatchId;
        long interval = checked(
            (long)DebugRunningLivenessInterval.TotalMilliseconds
        );
        _debugNextRunningLivenessAtUnixTimeMilliseconds =
            now > long.MaxValue - interval
                ? long.MaxValue
                : now + interval;
        return true;
    }

    private static void LogOutcomeUnknown(
        GalateaDelegationStateSnapshot snapshot,
        GalateaOutboundMailSnapshot mail,
        string code
    ) => DebugUtil.Warning(
        LogCategory,
        "Durable mail outcome unknown: "
            + $"user={Safe(snapshot.Owner.UserId)}, "
            + $"dispatchId={mail.DispatchId}, code={code}, "
            + $"attempt={mail.RecoveryFailureCount}, "
            + $"nextAt={mail.NextRetryAtUnixTimeMilliseconds}."
    );

    [Conditional("DEBUG")]
    private static void LogTerminal(
        GalateaDelegationStateSnapshot snapshot,
        GalateaOutboundMailSnapshot mail,
        GalateaReplyNoticeSnapshot notice,
        GalateaDelegateInspectionSource? source
    ) {
        string summary = "Durable terminal notice ready: "
            + $"user={Safe(snapshot.Owner.UserId)}, "
            + $"dispatchId={notice.DispatchId}, kind={notice.Kind}, "
            + $"selectorMode={SelectorMode(mail)}, "
            + $"knownTurnId={mail.AcceptedTurnId ?? "<none>"}, "
            + $"source={InspectionSourceText(source)}, "
            + $"sequence={notice.CompletionSequence}, "
            + $"noticeUtf8Bytes={TextExtractorUtf8.GetByteCount(notice.Body)}";
        if (notice.Kind == GalateaReplyNoticeKind.DeliveryFailure) {
            DebugUtil.Info(
                LogCategory,
                summary
                    + $", stage={notice.Stage ?? "<none>"}, "
                    + $"code={notice.Code ?? "<none>"}.",
                eventKind: DebugEventKind.Failure
            );
            return;
        }
        DebugUtil.Info(
            LogCategory,
            summary + ".",
            eventKind: DebugEventKind.Success
        );
    }

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
