using Atelia.TextAdv2.Runtime;

namespace Atelia.TextAdv2.GameServer;

internal sealed class TextAdv2RuntimeService : IDisposable {
    private readonly object _gate = new();
    private TextAdv2Runtime _runtime;
    private bool _disposed;

    public TextAdv2RuntimeService(TextAdv2Runtime runtime)
        => _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));

    public TResult Invoke<TResult>(Func<TextAdv2Runtime, TResult> operation) {
        ArgumentNullException.ThrowIfNull(operation);
        lock (_gate) {
            EnsureNotDisposed();
            return operation(_runtime);
        }
    }

    public void ReplaceRuntime(Func<TextAdv2Runtime> replacementFactory) {
        ArgumentNullException.ThrowIfNull(replacementFactory);

        lock (_gate) {
            EnsureNotDisposed();

            _runtime.Dispose();
            _runtime = replacementFactory();
        }
    }

    public void Dispose() {
        lock (_gate) {
            if (_disposed) { return; }

            _runtime.Dispose();
            _disposed = true;
        }
    }

    private void EnsureNotDisposed() {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
