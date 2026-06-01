using Atelia.TextAdv2.DevSupport;
using Atelia.TextAdv2.Runtime;

namespace Atelia.TextAdv2.GameServer;

internal sealed class RuntimeService : IDisposable {
    private readonly object _gate = new();
    private readonly HostingRuntimeInfo _runtimeInfo = HostingScaffold.DescribeCurrentState();
    private readonly RuntimeStatusSnapshot _unavailableStatus;
    private SerialWorldRuntime? _runtime;
    private RuntimeStatusSnapshot _status = null!;
    private bool _disposed;

    public RuntimeService(Func<SerialWorldRuntime> openRuntime) {
        ArgumentNullException.ThrowIfNull(openRuntime);
        _unavailableStatus = RuntimeStatusSnapshot.CreateOpenFailed(_runtimeInfo, "Game runtime is unavailable.");
        TryOpenRuntime(openRuntime, rethrowOpenFailure: false);
    }

    public TResult Invoke<TResult>(Func<SerialWorldRuntime, TResult> operation) {
        ArgumentNullException.ThrowIfNull(operation);
        lock (_gate) {
            EnsureNotDisposed();
            if (_runtime is null) {
                throw new RuntimeUnavailableException(_status);
            }

            return operation(_runtime);
        }
    }

    public RuntimeStatusSnapshot DescribeStatus() {
        lock (_gate) {
            EnsureNotDisposed();
            return _status;
        }
    }

    public void ReplaceRuntime(Func<SerialWorldRuntime> replacementFactory) {
        ArgumentNullException.ThrowIfNull(replacementFactory);

        lock (_gate) {
            EnsureNotDisposed();

            _runtime?.Dispose();
            _runtime = null;
            _status = _unavailableStatus;
            TryOpenRuntime(replacementFactory, rethrowOpenFailure: true);
        }
    }

    public void Dispose() {
        lock (_gate) {
            if (_disposed) { return; }

            _runtime?.Dispose();
            _disposed = true;
        }
    }

    private void EnsureNotDisposed() {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private void TryOpenRuntime(Func<SerialWorldRuntime> openRuntime, bool rethrowOpenFailure) {
        try {
            _runtime = openRuntime();
            _status = RuntimeStatusSnapshot.CreateReady(_runtimeInfo);
        }
        catch (InvalidOperationException ex) {
            _runtime = null;
            _status = RuntimeStatusSnapshot.CreateOpenFailed(_runtimeInfo, ex.Message);
            if (rethrowOpenFailure) {
                throw;
            }
        }
    }
}

internal sealed record RuntimeStatusSnapshot(
    string Readiness,
    string EngineAssemblyName,
    RuntimeErrorSnapshot? Error
) {
    public static RuntimeStatusSnapshot CreateReady(HostingRuntimeInfo runtimeInfo)
        => new(Readiness: RuntimeReadiness.Ready, EngineAssemblyName: runtimeInfo.EngineAssemblyName, Error: null);

    public static RuntimeStatusSnapshot CreateOpenFailed(HostingRuntimeInfo runtimeInfo, string message)
        => new(
            Readiness: RuntimeReadiness.OpenFailed,
            EngineAssemblyName: runtimeInfo.EngineAssemblyName,
            Error: new RuntimeErrorSnapshot(message)
        );
}

internal sealed record RuntimeErrorSnapshot(string Message);

internal static class RuntimeReadiness {
    public const string Ready = "ready";
    public const string OpenFailed = "open-failed";
}

internal sealed class RuntimeUnavailableException(RuntimeStatusSnapshot status)
    : Exception(status.Error?.Message ?? "Game runtime is unavailable.") {
    public RuntimeStatusSnapshot Status { get; } = status ?? throw new ArgumentNullException(nameof(status));
}
