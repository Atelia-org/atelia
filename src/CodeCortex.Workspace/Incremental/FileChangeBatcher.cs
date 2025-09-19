using System.Collections.Concurrent;
using Atelia.Diagnostics;
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
        DebugUtil.Print("Watcher", $"Batcher received: {change.Kind} {change.Path}");
        lock (_sync) {
            _buffer.Add(change);
            _timer?.Dispose();
            _timer = new System.Threading.Timer(_ => FlushInternal(), null, _debounce, Timeout.InfiniteTimeSpan);
            DebugUtil.Print("Watcher", $"Batcher buffer size: {_buffer.Count}, debounce timer set for {_debounce.TotalMilliseconds}ms");
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
            DebugUtil.Print("Watcher", "Batcher flush: no changes to process");
            return;
        }

        DebugUtil.Print("Watcher", $"Batcher flushing {snapshot.Length} changes");
        try {
            var handlers = Flushed?.GetInvocationList()?.Length ?? 0;
            DebugUtil.Print("Watcher", $"Batcher invoking Flushed ({handlers} handlers) with {snapshot.Length} changes");
            Flushed?.Invoke(snapshot);
            DebugUtil.Print("Watcher", "Batcher Flushed handlers executed");
        }
        catch (Exception ex) {
            DebugUtil.Print("Watcher", $"Batcher Flushed handlers threw: {ex.Message}");
        }
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
