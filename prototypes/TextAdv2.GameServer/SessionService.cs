using Atelia.TextAdv2.Session;

namespace Atelia.TextAdv2.GameServer;

internal sealed class SessionService : IDisposable {
    private readonly object _gate = new();
    private WorldSession _session;
    private bool _disposed;

    public SessionService(WorldSession session)
        => _session = session ?? throw new ArgumentNullException(nameof(session));

    public TResult Invoke<TResult>(Func<WorldSession, TResult> operation) {
        ArgumentNullException.ThrowIfNull(operation);
        lock (_gate) {
            EnsureNotDisposed();
            return operation(_session);
        }
    }

    public void ReplaceSession(Func<WorldSession> replacementFactory) {
        ArgumentNullException.ThrowIfNull(replacementFactory);

        lock (_gate) {
            EnsureNotDisposed();

            _session.Dispose();
            _session = replacementFactory();
        }
    }

    public void Dispose() {
        lock (_gate) {
            if (_disposed) { return; }

            _session.Dispose();
            _disposed = true;
        }
    }

    private void EnsureNotDisposed() {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
