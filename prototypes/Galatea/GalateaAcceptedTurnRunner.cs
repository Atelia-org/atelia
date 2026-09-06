using Atelia.Diagnostics;
using System.Runtime.ExceptionServices;

namespace Atelia.Galatea.Server;

/// <summary>
/// Runs an admitted turn independently of its HTTP request or automatic trigger.
/// </summary>
internal sealed class GalateaAcceptedTurnRunner {
    private readonly GalateaHostService hostService;
    private readonly IHostApplicationLifetime applicationLifetime;
    private readonly CancellationTokenSource _stopping;
    private readonly object _gate = new();
    private readonly HashSet<Task> _active = [];
    private bool _closed;
    private ExceptionDispatchInfo? _failure;

    public GalateaAcceptedTurnRunner(GalateaHostService hostService, IHostApplicationLifetime applicationLifetime) {
        this.hostService = hostService;
        this.applicationLifetime = applicationLifetime;
        _stopping = CancellationTokenSource.CreateLinkedTokenSource(applicationLifetime.ApplicationStopping);
        hostService.RegisterTurnRunner(this);
    }

    internal void BeginShutdown() {
        lock (_gate) { _closed = true; }
        _stopping.Cancel();
    }

    internal async Task DrainAsync() {
        BeginShutdown();
        Task[] tasks;
        lock (_gate) { tasks = _active.ToArray(); }
        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
        catch (Exception) { /* The observer preserves the original cause below. */ }
        ExceptionDispatchInfo? failure;
        lock (_gate) { failure = _failure; }
        failure?.Throw();
    }
    /// <summary>
    /// The caller holds TurnLock and has installed liveTurn. A successful return
    /// transfers the lock to this runner and binds RunTask; a synchronous failure
    /// leaves admission cleanup and lock release with the caller.
    /// </summary>
    internal Task Start(UserSessionHost session, GalateaLiveTurn liveTurn) {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(liveTurn);
        lock (_gate) {
            if (_closed || _stopping.IsCancellationRequested) {
                throw new OperationCanceledException("The accepted-turn runner is stopping.", _stopping.Token);
            }
            if (liveTurn.RunTask is not null) {
                throw new InvalidOperationException("The accepted turn is already running.");
            }
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task runTask = Task.Run(async () => {
                await ready.Task.ConfigureAwait(false);
                try { await RunAsync(session, liveTurn).ConfigureAwait(false); }
                catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
                catch (Exception ex) {
                    lock (_gate) { _failure ??= ExceptionDispatchInfo.Capture(ex); }
                    applicationLifetime.StopApplication();
                    throw;
                }
            }, CancellationToken.None);
            liveTurn.RunTask = runTask;
            _active.Add(runTask);
            _ = runTask.ContinueWith(completed => {
                _ = completed.Exception; // Observe the outer task even if it finished before drain.
                lock (_gate) { _active.Remove(completed); }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            ready.SetResult();
            return runTask;
        }
    }

    private async Task RunAsync(
        UserSessionHost session,
        GalateaLiveTurn liveTurn
    ) {
        CancellationToken stopping = _stopping.Token;
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
