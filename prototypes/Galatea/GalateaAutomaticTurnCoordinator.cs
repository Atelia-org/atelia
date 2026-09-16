using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using Atelia.Completion;
using Atelia.Diagnostics;
using Atelia.SessionJournal;

namespace Atelia.Galatea.Server;

internal abstract record GalateaAutomaticTurnResult {
    internal sealed record Started(GalateaLiveTurn Turn, string Origin) : GalateaAutomaticTurnResult;
    internal sealed record Status(GalateaAgentStatusDto Value) : GalateaAutomaticTurnResult;
    internal sealed record Busy(string? TurnId) : GalateaAutomaticTurnResult;
    internal sealed record Blocked(string Code, string Message) : GalateaAutomaticTurnResult;
}

/// <summary>Owns automatic admission and its pre-handoff compensation.</summary>
internal sealed class GalateaAutomaticTurnCoordinator(
    GalateaHostService host,
    GalateaAcceptedTurnRunner runner
) {
    private readonly ConcurrentDictionary<string, string> _attachFailures = new(StringComparer.Ordinal);

    internal GalateaAgentStatusDto ReadStatus(string characterId) {
        bool configured = host.TryGetCharacter(characterId, out GalateaCharacterConfig? character);
        string? connection = configured ? character.DefaultConnectionId : null;
        string? state = host.IsStopping ? "stopping"
            : host.MaintenanceMode ? "maintenance"
            : !configured ? "disabled" : null;
        if (state is not null) { return new(state, connection, null, null, null); }
        if (_attachFailures.TryGetValue(characterId, out string? code)) {
            return new("blocked", connection, null, null, code);
        }
        return host.ReadAttachedSession(characterId)?.ReadAgentStatus()
            ?? new(
                character.AutonomyIntervalMinutes == 0 ? "waiting" : "starting",
                connection,
                null,
                null,
                null
            );
    }

    internal void BlockAfterFailure(string characterId) {
        CharacterSessionHost? session = host.ReadAttachedSession(characterId);
        if (session is null) { _attachFailures[characterId] = "AUTOMATIC_ADMISSION_FAILED"; }
    }

    // Explicitly retries settlement only. It never claims a reply or creates a
    // main-model turn; automatic pulses share this same TurnLock.
    internal async Task<GalateaAutomaticTurnResult> RetryAdmissionAsync(string characterId, CancellationToken ct) {
        GalateaAgentStatusDto status = ReadStatus(characterId);
        if (status.State is "disabled" or "maintenance" or "stopping") {
            return new GalateaAutomaticTurnResult.Status(status);
        }
        ct.ThrowIfCancellationRequested();
        bool replyOnly = IsReplyOnlyCharacter(characterId);
        if (replyOnly && host.DelegationSupervisor.ReadAutomaticWakeReason(characterId)
            == GalateaAutomaticWakeReason.None) {
            return new GalateaAutomaticTurnResult.Status(status);
        }
        CharacterSessionHost? session = host.ReadAttachedSession(characterId);
        if (session is null || _attachFailures.ContainsKey(characterId)) {
            if (replyOnly && !_attachFailures.ContainsKey(characterId)) {
                session = await host.GetSessionAsync(characterId, ct)
                    .ConfigureAwait(false);
            }
        }
        if (session is null || _attachFailures.ContainsKey(characterId)) {
            return new GalateaAutomaticTurnResult.Blocked("session-unavailable", "会话尚未就绪；请检查服务端初始化诊断。");
        }
        if (!session.TurnLock.Wait(0)) {
            return new GalateaAutomaticTurnResult.Busy(session.GetCurrentTurn()?.TurnId);
        }
        try {
            ct.ThrowIfCancellationRequested();
            if (host.IsStopping || host.MaintenanceMode) {
                return new GalateaAutomaticTurnResult.Status(ReadStatus(characterId));
            }
            if (!session.AutomaticAdmissionFailed) {
                if (replyOnly) {
                    await host.ReconcileDurableAdmissionAsync(session, ct)
                        .ConfigureAwait(false);
                }
                return new GalateaAutomaticTurnResult.Status(session.ReadAgentStatus());
            }
            await host.ReconcileDurableAdmissionAsync(session, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            host.RequireRunning();
            SessionRuntimeRecoveryRequirements recovery = session.Engine.InspectRuntimeRecoveryRequirements(ct);
            if (recovery is not SessionRuntimeRecoveryRequirements.NoRuntimeRequired { Phase: SessionExecutionPhase.Idle }) {
                bool empty = recovery.Phase == SessionExecutionPhase.Empty;
                session.SetAgentStatus("blocked", empty ? "SESSION_UNPROVISIONED" : "RECOVERY_REQUIRED");
                return new GalateaAutomaticTurnResult.Blocked(
                    empty ? "session-unprovisioned" : "recovery-required",
                    empty ? "会话仓库尚未完成初始化。" : "未完成处理已核对，但会话仍有待恢复轮次；请使用恢复轮次入口。"
                );
            }
            session.AutomaticAdmissionFailed = false;
            session.AutomaticAdmissionFailure = null;
            session.PublishAutonomyStatus();
            return new GalateaAutomaticTurnResult.Status(session.ReadAgentStatus());
        }
        catch (Exception exception) when (GalateaExceptionClassifier.IsNonFatal(exception)
            && !ct.IsCancellationRequested && !host.IsStopping) {
            RecordAdmissionFailure(session, exception);
            DebugUtil.Error("Galatea.Autonomy", $"Admission retry failed: character={characterId}", exception);
            ApiErrorDto failure = session.AutomaticAdmissionFailure!;
            return new GalateaAutomaticTurnResult.Blocked(failure.Code, failure.Error);
        }
        finally { session.TurnLock.Release(); }
    }

    internal static void RecordAdmissionFailure(CharacterSessionHost session, Exception exception) {
        string code = exception is GalateaTurnException { FailureReason: { } reason }
            ? reason : "automatic-admission-failed";
        string message = code switch {
            "character-memory-extraction-timeout" => "Note 提取超时，尚未完成保存。",
            "character-memory-extraction-aborted" => "Note 提取被中止，尚未完成保存。",
            "character-memory-extraction-unavailable" => "Note 提取未成功完成。",
            "character-memory-pod-unavailable" => "Note 存储暂时不可用。",
            "character-memory-settlement-deferred" => "Note 保存处理暂未完成。",
            "character-memory-quarantined" or "character-memory-state-invalid" => "Note 存储状态需要检查。",
            "delegation-extraction-unavailable" => "发信请求提取未成功完成。",
            _ => "轮次开始前的未完成处理失败，请重试或检查服务端诊断。",
        };
        // Only expose known diagnostic categories, never provider response text
        // or arbitrary exception messages that can include request content.
        Exception? cause = exception;
        while (cause is not null && cause is not TextExtractionException) { cause = cause.InnerException; }
        if (cause is TextExtractionException extraction) {
            message += $" 提取错误：{extraction.Kind}。";
        }
        session.AutomaticAdmissionFailed = true;
        session.AutomaticAdmissionFailure = new ApiErrorDto(code, message);
        session.PublishAutonomyStatus();
    }

    internal async Task<GalateaAutomaticTurnResult> TryPulseAsync(string characterId, CancellationToken ct) {
        GalateaAgentStatusDto status = ReadStatus(characterId);
        if (status.State is "disabled" or "maintenance" or "stopping") {
            return new GalateaAutomaticTurnResult.Status(status);
        }
        if (_attachFailures.ContainsKey(characterId)) {
            return new GalateaAutomaticTurnResult.Blocked("automatic-admission-failed", "服务端自动轮次初始化失败；请检查服务端诊断。");
        }
        ct.ThrowIfCancellationRequested();
        bool replyOnly = IsReplyOnlyCharacter(characterId);
        if (replyOnly && host.DelegationSupervisor.ReadAutomaticWakeReason(characterId)
            == GalateaAutomaticWakeReason.None) {
            return new GalateaAutomaticTurnResult.Status(status);
        }
        CharacterSessionHost session = await host.GetSessionAsync(characterId, ct).ConfigureAwait(false);
        if (!session.TurnLock.Wait(0)) {
            return new GalateaAutomaticTurnResult.Busy(session.GetCurrentTurn()?.TurnId);
        }

        GalateaLiveTurn? liveTurn = null;
        bool transferred = false;
        try {
            ct.ThrowIfCancellationRequested();
            if (host.IsStopping) { return new GalateaAutomaticTurnResult.Status(ReadStatus(characterId)); }
            if (session.AutomaticReplyFailed || session.AutomaticAdmissionFailed) {
                // A settlement retry may have established a more specific
                // recovery boundary. Keep that diagnosis until explicit turn
                // recovery succeeds instead of offering settlement again.
                GalateaAgentStatusDto blockedStatus = session.ReadAgentStatus();
                if (session.AutomaticAdmissionFailed
                    && blockedStatus.Code is "RECOVERY_REQUIRED" or "SESSION_UNPROVISIONED") {
                    bool empty = blockedStatus.Code == "SESSION_UNPROVISIONED";
                    return new GalateaAutomaticTurnResult.Blocked(
                        empty ? "session-unprovisioned" : "recovery-required",
                        empty ? "会话仓库尚未完成初始化。" : "会话仍有待恢复轮次；请使用恢复轮次入口。"
                    );
                }
                session.PublishAutonomyStatus();
                return new GalateaAutomaticTurnResult.Blocked(
                    session.AutomaticReplyFailed ? "automatic-reply-failed" : "automatic-admission-failed",
                    "上一次自动轮次未完成；成功完成人工轮次后恢复自动检查。"
                );
            }
            await host.ReconcileDurableAdmissionAsync(session, ct).ConfigureAwait(false);
            SessionRuntimeRecoveryRequirements recovery = session.Engine.InspectRuntimeRecoveryRequirements(ct);
            if (recovery is not SessionRuntimeRecoveryRequirements.NoRuntimeRequired { Phase: SessionExecutionPhase.Idle }) {
                bool empty = recovery.Phase == SessionExecutionPhase.Empty;
                session.SetAgentStatus("blocked", empty ? "SESSION_UNPROVISIONED" : "RECOVERY_REQUIRED");
                return new GalateaAutomaticTurnResult.Blocked(
                    empty ? "session-unprovisioned" : "recovery-required",
                    empty ? "会话仓库尚未完成初始化。" : "当前会话存在待恢复的持久化轮次；自动轮次未启动。"
                );
            }
            if (!host.TryGetConnection(session.Character, null, out CompletionConnectionConfig connection)) {
                throw new InvalidOperationException("The configured per-character default connection is unavailable.");
            }
            await host.PrepareFreshTurnAdmissionAsync(session, recovery, ct).ConfigureAwait(false);
            GalateaReadyReplyTurnStartResult reply = host.StartReadyReplyTurn(session, new(connection.Id));
            string origin;
            if (reply is GalateaReadyReplyTurnStartResult.Started started) {
                liveTurn = started.Turn;
                origin = "delegate-reply";
            }
            else if (reply is GalateaReadyReplyTurnStartResult.Empty) {
                if (replyOnly) {
                    session.PublishAutonomyStatus();
                    return new GalateaAutomaticTurnResult.Status(
                        session.ReadAgentStatus()
                    );
                }
                if (session.AutonomyCadence?.ObservePulse()
                    != GalateaAutonomyCadencePulseResult.AutonomousActivationDue) {
                    session.PublishAutonomyStatus();
                    return new GalateaAutomaticTurnResult.Status(session.ReadAgentStatus());
                }
                liveTurn = host.StartHeartbeatActivationTurn(session, new(connection.Id));
                origin = "heartbeat-activation";
            }
            else { throw new InvalidDataException("Unknown ready-reply turn start result."); }
            _ = runner.Start(session, liveTurn);
            transferred = true;
            return new GalateaAutomaticTurnResult.Started(liveTurn, origin);
        }
        catch (Exception original) when (!transferred) {
            if (GalateaExceptionClassifier.IsNonFatal(original)
                && !ct.IsCancellationRequested && !host.IsStopping) {
                RecordAdmissionFailure(session, original);
            }
            if (liveTurn is null) { throw; }
            try { await host.ReconcileDurableAdmissionAsync(session, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception cleanup) when (GalateaExceptionClassifier.IsNonFatal(cleanup)) {
                if (!GalateaExceptionClassifier.IsNonFatal(original)) { ExceptionDispatchInfo.Capture(original).Throw(); }
                throw new AggregateException("Automatic turn acceptance and durable cleanup both failed.", original, cleanup);
            }
            throw;
        }
        finally {
            if (!transferred) {
                try {
                    if (liveTurn is not null) {
                        if (liveTurn.FreshInput is GalateaFreshInput.HeartbeatActivation) {
                            host.RollbackHeartbeatActivationAdmission(session, liveTurn);
                        }
                        if (liveTurn.Status == "running") { liveTurn.PublishError(GalateaSseErrorCode.InternalFailure); }
                        host.FinishTurn(session, liveTurn);
                        liveTurn.Complete();
                        await host.RefreshRecentTurnsBestEffortAsync(session, CancellationToken.None).ConfigureAwait(false);
                    }
                }
                finally { session.TurnLock.Release(); }
            }
        }
    }

    private bool IsReplyOnlyCharacter(string characterId) =>
        host.TryGetCharacter(characterId, out GalateaCharacterConfig? character)
        && character.AutonomyIntervalMinutes == 0;
}
