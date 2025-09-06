using System.Runtime.InteropServices;
using Atelia.Diagnostics;
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
        DebugUtil.Print("Watcher", $"Starting file watcher for root: {_root}");
        AddWatcher(_root);
        DebugUtil.Print("Watcher", "File watcher started successfully");
    }
    private void AddWatcher(string dir) {
        DebugUtil.Print("Watcher", $"Adding watcher for directory: {dir}");
        var fsw = new FileSystemWatcher(dir, "*.cs") { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.Size };
        fsw.Changed += OnChanged;
        fsw.Created += OnCreated;
        fsw.Deleted += OnDeleted;
        fsw.Renamed += OnRenamed;
        fsw.EnableRaisingEvents = true;
        _watchers.Add(fsw);
        DebugUtil.Print("Watcher", $"Watcher added for {dir}, EnableRaisingEvents={fsw.EnableRaisingEvents}");
    }
    private void OnCreated(object sender, FileSystemEventArgs e) {
        DebugUtil.Print("Watcher", $"File created: {e.FullPath}");
        _batcher.OnRaw(RawFileChange.Created(e.FullPath));
    }
    private void OnChanged(object sender, FileSystemEventArgs e) {
        DebugUtil.Print("Watcher", $"File changed: {e.FullPath}");
        _batcher.OnRaw(RawFileChange.Changed(e.FullPath));
    }
    private void OnDeleted(object sender, FileSystemEventArgs e) {
        DebugUtil.Print("Watcher", $"File deleted: {e.FullPath}");
        _batcher.OnRaw(RawFileChange.Deleted(e.FullPath));
    }
    private void OnRenamed(object sender, RenamedEventArgs e) {
        DebugUtil.Print("Watcher", $"File renamed: {e.OldFullPath} -> {e.FullPath}");
        _batcher.OnRaw(RawFileChange.Renamed(e.OldFullPath, e.FullPath));
    }
    public void Dispose() {
        foreach (var w in _watchers) { try { w.Dispose(); } catch { } }

        _watchers.Clear();
    }
}
#pragma warning restore 1591
