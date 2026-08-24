namespace Atelia.StateJournal.Internal;

/// <summary>
/// Repository-owned、Revision-shared 的轻量生命周期信号。
/// 不持有 Revision/Object 引用，因此也覆盖历史 detached Revision 等非 branch-cache 物化路径。
/// </summary>
internal sealed class RepositoryLifetime {
    private int _disposed;

    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    internal void SignalDisposed() => Interlocked.Exchange(ref _disposed, 1);

    internal void ThrowIfDisposed() {
        if (IsDisposed) {
            throw new ObjectDisposedException(
                typeof(Repository).FullName,
                "The owning Repository has been disposed."
            );
        }
    }
}
