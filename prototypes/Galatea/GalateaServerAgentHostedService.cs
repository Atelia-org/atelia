using Atelia.Diagnostics;
using System.Runtime.ExceptionServices;

namespace Atelia.Galatea.Server;

internal sealed class GalateaServerAgentHostedService(
    GalateaHostService host,
    GalateaAutomaticTurnCoordinator coordinator,
    IHostApplicationLifetime lifetime
) : BackgroundService {
    internal static readonly TimeSpan PulseInterval = TimeSpan.FromSeconds(10);
    internal Action<string>? PulseCompletedForTest { get; set; }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) {
        if (host.MaintenanceMode) { return Task.CompletedTask; }
        return Task.WhenAll(host.AutonomyCharacterIds.Select(characterId => RunCharacterAsync(characterId, stoppingToken)));
    }

    private async Task RunCharacterAsync(string characterId, CancellationToken ct) {
        using var timer = new PeriodicTimer(PulseInterval, host.TimeProvider);
        try {
            do {
                try {
                    _ = await coordinator.TryPulseAsync(characterId, ct).ConfigureAwait(false);
                    PulseCompletedForTest?.Invoke(characterId);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested || host.IsStopping) { return; }
                catch (Exception ex) when (GalateaExceptionClassifier.IsNonFatal(ex)) {
                    coordinator.BlockAfterFailure(characterId);
                    DebugUtil.Error("Galatea.Autonomy", $"Automatic admission blocked: character={characterId}", ex);
                }
            } while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested || host.IsStopping) { }
        catch (Exception) {
            lifetime.StopApplication();
            throw;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken) {
        host.BeginShutdown();
        // The host's timeout must not permit disposal while an accepted writer
        // still uses session or Completion resources.
        Exception? loopFailure = null;
        try { await base.StopAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception exception) { loopFailure = exception; }
        try { await host.DisposeAsync().ConfigureAwait(false); }
        catch (Exception exception) when (loopFailure is not null) {
            if (!GalateaExceptionClassifier.IsNonFatal(loopFailure)) {
                ExceptionDispatchInfo.Capture(loopFailure).Throw();
            }
            if (!GalateaExceptionClassifier.IsNonFatal(exception)) { throw; }
            throw new AggregateException(loopFailure, exception);
        }
        if (loopFailure is not null) { ExceptionDispatchInfo.Capture(loopFailure).Throw(); }
    }
}
