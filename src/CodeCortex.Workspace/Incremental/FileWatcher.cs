using System.Runtime.InteropServices;
namespace CodeCortex.Workspace.Incremental;
#pragma warning disable 1591
public interface IFileWatcher : IDisposable { void Start(); }
public sealed class SolutionFileWatcher : IFileWatcher {
    private readonly string _root;
    private readonly IFileChangeBatcher _batcher;
    private readonly List<FileSystemWatcher> _watchers = new();
    public SolutionFileWatcher(string root, IFileChangeBatcher batcher) {
        _root = root;
        _batcher = batcher;
    }
    public void Start() {
        AddWatcher(_root);
    }
    private void AddWatcher(string dir) {
        var fsw = new FileSystemWatcher(dir, "*.cs") { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.Size };
        fsw.Changed += OnChanged;
        fsw.Created += OnCreated;
        fsw.Deleted += OnDeleted;
        fsw.Renamed += OnRenamed;
        fsw.EnableRaisingEvents = true;
        _watchers.Add(fsw);
    }
    private void OnCreated(object sender, FileSystemEventArgs e) => _batcher.OnRaw(RawFileChange.Created(e.FullPath));
    private void OnChanged(object sender, FileSystemEventArgs e) => _batcher.OnRaw(RawFileChange.Changed(e.FullPath));
    private void OnDeleted(object sender, FileSystemEventArgs e) => _batcher.OnRaw(RawFileChange.Deleted(e.FullPath));
    private void OnRenamed(object sender, RenamedEventArgs e) => _batcher.OnRaw(RawFileChange.Renamed(e.OldFullPath, e.FullPath));
    public void Dispose() {
        foreach (var w in _watchers) { try { w.Dispose(); } catch { } }

        _watchers.Clear();
    }
}
#pragma warning restore 1591
