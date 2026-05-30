using Atelia.TextAdv2.Runtime;

namespace Atelia.TextAdv2.GameServer;

internal sealed class TextAdv2RuntimeService : IDisposable {
    private const int MaxOpenRetryCount = 5;

    private readonly object _gate = new();
    private readonly bool _autoBootstrapSampleWorld;
    private TextAdv2Runtime _runtime;
    private bool _disposed;

    public TextAdv2RuntimeService(string repoDir, bool autoBootstrapSampleWorld) {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoDir);

        RepoDir = repoDir;
        _autoBootstrapSampleWorld = autoBootstrapSampleWorld;
        _runtime = OpenConfiguredRuntime();
    }

    public string RepoDir { get; }

    public bool AutoBootstrapSampleWorld => _autoBootstrapSampleWorld;

    public TextAdv2RuntimeCommandResult Execute(TextAdv2RuntimeCommand command) {
        ArgumentNullException.ThrowIfNull(command);

        lock (_gate) {
            EnsureNotDisposed();
            return _runtime.Execute(command);
        }
    }

    public void ResetToSampleWorld() {
        lock (_gate) {
            EnsureNotDisposed();

            _runtime.Dispose();
            if (Directory.Exists(RepoDir)) {
                Directory.Delete(RepoDir, recursive: true);
            }

            _runtime = TextAdv2Runtime.CreateSampleWorld(RepoDir);
        }
    }

    public void Dispose() {
        lock (_gate) {
            if (_disposed) { return; }

            _runtime.Dispose();
            _disposed = true;
        }
    }

    private TextAdv2Runtime OpenConfiguredRuntime() {
        for (int attempt = 0; ; attempt++) {
            try {
                return _autoBootstrapSampleWorld
                    ? TextAdv2Runtime.OpenOrCreateSampleWorld(RepoDir)
                    : TextAdv2Runtime.OpenExisting(RepoDir);
            }
            catch (InvalidOperationException ex) when (attempt < MaxOpenRetryCount && IsRepositoryLockFailure(ex)) {
                // Local host restarts can briefly overlap while the previous instance is still releasing the repo lock.
                Thread.Sleep((attempt + 1) * 50);
            }
        }
    }

    private static bool IsRepositoryLockFailure(InvalidOperationException ex)
        => ex.Message.Contains("Failed to acquire lock", StringComparison.Ordinal);

    private void EnsureNotDisposed() {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
