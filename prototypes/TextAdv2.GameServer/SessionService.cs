using Atelia.TextAdv2.DevSupport;
using Atelia.TextAdv2.Runtime;

namespace Atelia.TextAdv2.GameServer;

internal sealed class SessionService : IDisposable {
    private readonly object _gate = new();
    private readonly HostingRuntimeInfo _runtimeInfo = HostingScaffold.DescribeCurrentState();
    private readonly SessionStatusSnapshot _unavailableStatus;
    private SerialWorldRuntime? _session;
    private SessionStatusSnapshot _status = null!;
    private bool _disposed;

    public SessionService(Func<SerialWorldRuntime> openSession) {
        ArgumentNullException.ThrowIfNull(openSession);
        _unavailableStatus = SessionStatusSnapshot.CreateOpenFailed(_runtimeInfo, "Game session is unavailable.");
        TryOpenSession(openSession, rethrowOpenFailure: false);
    }

    public TResult Invoke<TResult>(Func<SerialWorldRuntime, TResult> operation) {
        ArgumentNullException.ThrowIfNull(operation);
        lock (_gate) {
            EnsureNotDisposed();
            if (_session is null) {
                throw new SessionUnavailableException(_status);
            }

            return operation(_session);
        }
    }

    public SessionStatusSnapshot DescribeStatus() {
        lock (_gate) {
            EnsureNotDisposed();
            return _status;
        }
    }

    public void ReplaceSession(Func<SerialWorldRuntime> replacementFactory) {
        ArgumentNullException.ThrowIfNull(replacementFactory);

        lock (_gate) {
            EnsureNotDisposed();

            _session?.Dispose();
            _session = null;
            _status = _unavailableStatus;
            TryOpenSession(replacementFactory, rethrowOpenFailure: true);
        }
    }

    public void Dispose() {
        lock (_gate) {
            if (_disposed) { return; }

            _session?.Dispose();
            _disposed = true;
        }
    }

    private void EnsureNotDisposed() {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private void TryOpenSession(Func<SerialWorldRuntime> openSession, bool rethrowOpenFailure) {
        try {
            _session = openSession();
            _status = SessionStatusSnapshot.CreateReady(_runtimeInfo);
        }
        catch (InvalidOperationException ex) {
            _session = null;
            _status = SessionStatusSnapshot.CreateOpenFailed(_runtimeInfo, ex.Message);
            if (rethrowOpenFailure) {
                throw;
            }
        }
    }
}

internal sealed record SessionStatusSnapshot(
    string Readiness,
    string EngineAssemblyName,
    SessionErrorSnapshot? Error
) {
    public static SessionStatusSnapshot CreateReady(HostingRuntimeInfo runtimeInfo)
        => new(Readiness: SessionReadiness.Ready, EngineAssemblyName: runtimeInfo.EngineAssemblyName, Error: null);

    public static SessionStatusSnapshot CreateOpenFailed(HostingRuntimeInfo runtimeInfo, string message)
        => new(
            Readiness: SessionReadiness.OpenFailed,
            EngineAssemblyName: runtimeInfo.EngineAssemblyName,
            Error: new SessionErrorSnapshot(message)
        );
}

internal sealed record SessionErrorSnapshot(string Message);

internal static class SessionReadiness {
    public const string Ready = "ready";
    public const string OpenFailed = "open-failed";
}

internal sealed class SessionUnavailableException(SessionStatusSnapshot status)
    : Exception(status.Error?.Message ?? "Game session is unavailable.") {
    public SessionStatusSnapshot Status { get; } = status ?? throw new ArgumentNullException(nameof(status));
}
