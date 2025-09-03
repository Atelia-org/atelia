using System.Collections.Concurrent;
namespace CodeCortex.Workspace.Incremental;
#pragma warning disable 1591
public interface IFileChangeBatcher { void OnRaw(RawFileChange change); event Action<IReadOnlyList<RawFileChange>>? Flushed; int PendingCount { get; } }
public sealed class DebounceFileChangeBatcher : IFileChangeBatcher, IDisposable {
    private readonly TimeSpan _debounce;
    private readonly object _sync = new();
    private readonly List<RawFileChange> _buffer = new();
    private System.Threading.Timer? _timer;
    public event Action<IReadOnlyList<RawFileChange>>? Flushed;
    public int PendingCount { get { lock (_sync) { return _buffer.Count; } } }
    public DebounceFileChangeBatcher(TimeSpan? debounce = null) { _debounce = debounce ?? TimeSpan.FromMilliseconds(400); }
    public void OnRaw(RawFileChange change) {
        lock (_sync) {
            _buffer.Add(change);
            _timer?.Dispose();
            _timer = new System.Threading.Timer(_ => FlushInternal(), null, _debounce, Timeout.InfiniteTimeSpan);
        }
    }
    public void FlushNow() {
        FlushInternal();
    }
    private void FlushInternal() {
        RawFileChange[] snapshot;
        lock (_sync) {
            snapshot = _buffer.ToArray();
            _buffer.Clear();
        }
        if (snapshot.Length == 0) {
            return;
        }

        try { Flushed?.Invoke(snapshot); } catch { /* swallow */ }
    }
    public void Dispose() {
        lock (_sync) {
            _timer?.Dispose();
            _timer = null;
            _buffer.Clear();
        }
    }
}
#pragma warning restore 1591
