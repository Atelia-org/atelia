using Atelia.Diagnostics;

namespace Atelia.Galatea.Server;

/// <summary>
/// Runs an admitted turn independently of its HTTP request or automatic trigger.
/// </summary>
internal sealed class GalateaAcceptedTurnRunner(
    GalateaHostService hostService,
    IHostApplicationLifetime applicationLifetime
) {
    /// <summary>
    /// The caller holds TurnLock and has installed liveTurn. A successful return
    /// transfers the lock to this runner and binds RunTask; a synchronous failure
    /// leaves admission cleanup and lock release with the caller.
    /// </summary>
    internal Task Start(UserSessionHost session, GalateaLiveTurn liveTurn) {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(liveTurn);
        if (liveTurn.RunTask is not null) {
            throw new InvalidOperationException("The accepted turn is already running.");
        }

        // Even shutdown must run the body so that its finally releases TurnLock.
        // The request cancellation token never owns an accepted turn.
        Task runTask = Task.Run(
            () => RunAsync(session, liveTurn),
            CancellationToken.None
        );
        liveTurn.RunTask = runTask;
        return runTask;
    }

    private async Task RunAsync(
        UserSessionHost session,
        GalateaLiveTurn liveTurn
    ) {
        CancellationToken stopping = applicationLifetime.ApplicationStopping;
        try {
            DebugUtil.Info(
                "Galatea.TurnRunner",
                $"Accepted turn start: user={session.User.UserId}, turnId={liveTurn.TurnId}, head={session.Engine.ReadCurrentHead()}"
            );
            await hostService.RunTurnAsync(session, liveTurn, stopping);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) {
            DebugUtil.Warning("Galatea.TurnRunner", $"Turn cancelled by shutdown: user={session.User.UserId}, turnId={liveTurn.TurnId}");
            liveTurn.PublishError(GalateaSseErrorCode.ServerShutdown);
        }
        catch (GalateaTurnException ex) {
            DebugUtil.Warning("Galatea.TurnRunner", $"Turn failed with GalateaTurnException: user={session.User.UserId}, turnId={liveTurn.TurnId}, reason={ex.FailureReason}, detail={ex.Message}");
            liveTurn.PublishError(GalateaSseErrorClassifier.Classify(ex));
        }
        catch (Exception ex) when (GalateaExceptionClassifier.IsNonFatal(ex)) {
            DebugUtil.Error("Galatea.TurnRunner", $"Turn failed with exception: user={session.User.UserId}, turnId={liveTurn.TurnId}", ex);
            liveTurn.PublishError(GalateaSseErrorCode.InternalFailure);
        }
        catch (Exception) {
            liveTurn.AbortTransportWithoutTerminal();
            throw;
        }
        finally {
            try {
                hostService.FinishTurn(session, liveTurn);
                liveTurn.Complete();
                if (!liveTurn.TransportAborted
                    && !string.Equals(liveTurn.Status, "completed", StringComparison.Ordinal)) {
                    await hostService
                        .RefreshRecentTurnsBestEffortAsync(session, stopping)
                        .ConfigureAwait(false);
                }
                DebugUtil.Info(
                    "Galatea.TurnRunner",
                    $"Accepted turn finish: user={session.User.UserId}, turnId={liveTurn.TurnId}, status={liveTurn.Status}"
                );
            }
            finally {
                session.TurnLock.Release();
            }
        }
    }
}
