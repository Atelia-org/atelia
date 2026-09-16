namespace Atelia.Galatea.Server;

internal sealed record GalateaAdmissionStatusDto(string? OperationId, string State);

/// <summary>Transient ownership for one awaited, lock-protected admission reconciliation.</summary>
internal sealed class GalateaAdmissionOperation : IAsyncDisposable {
    private readonly CharacterSessionHost _host;
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationTokenSource _lifetime;
    private Task? _stopCallbacks;
    internal GalateaAdmissionOperation(CharacterSessionHost host, CancellationToken caller, CancellationToken shutdown) {
        _host = host;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(caller, shutdown, _stop.Token);
    }
    internal string Id { get; } = Guid.NewGuid().ToString("N");
    internal bool StopRequested => _stop.IsCancellationRequested;
    internal CancellationToken Token => _lifetime.Token;
    // Called under the session gate. Never execute arbitrary client token
    // callbacks while holding that gate; cancellation callbacks are drained
    // asynchronously before the operation's token sources are disposed.
    internal void RequestStop() => _stopCallbacks ??= _stop.CancelAsync();
    public async ValueTask DisposeAsync() {
        _host.FinishAdmission(this);
        if (_stopCallbacks is { } callbacks) { await callbacks.ConfigureAwait(false); }
        _lifetime.Dispose();
        _stop.Dispose();
    }
}
