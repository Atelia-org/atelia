using System;
using System.IO;
using System.Threading;
using CodeCortex.Core.Index;
using CodeCortex.Core.IO;
using Atelia.Diagnostics;

namespace CodeCortex.Core.Index;

/// <summary>
/// 管理进程内的 CodeCortexIndex，提供线程安全的读写访问；
/// 原先位于 Service 层，迁移到 Core 以便 CLI/Service 共享。
/// </summary>
public sealed class ServiceIndexManager : IDisposable {
    private readonly ReaderWriterLockSlim _lock = new();
    private volatile bool _isUpdating = false;
    private CodeCortexIndex _index;
    private readonly string _indexPath;
    private readonly string _outlineDir;
    private readonly IFileSystem _fs;

    public ServiceIndexManager(CodeCortexIndex initialIndex, string indexPath, string outlineDir, IFileSystem? fs = null) {
        _index = initialIndex ?? throw new ArgumentNullException(nameof(initialIndex));
        _indexPath = indexPath ?? throw new ArgumentNullException(nameof(indexPath));
        _outlineDir = outlineDir ?? throw new ArgumentNullException(nameof(outlineDir));
        _fs = fs ?? new DefaultFileSystem();

        DebugUtil.Print("IndexManager", $"Initialized with {_index.Types.Count} types");
    }

    public bool IsUpdating => _isUpdating;

    public Stats GetStats() {
        _lock.EnterReadLock();
        try { return _index.Stats; } finally { _lock.ExitReadLock(); }
    }

    public T ReadIndex<T>(Func<CodeCortexIndex, T> reader) {
        if (reader == null) { throw new ArgumentNullException(nameof(reader)); }
        _lock.EnterReadLock();
        try { return reader(_index); } finally { _lock.ExitReadLock(); }
    }

    public void UpdateIndex(Action<CodeCortexIndex> updater) {
        if (updater == null) { throw new ArgumentNullException(nameof(updater)); }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _lock.EnterWriteLock();
        try {
            _isUpdating = true;
            DebugUtil.Print("IndexManager", "Starting index update");
            updater(_index);
            // 先维持原持久化策略，后续统一到 IndexStore
            SaveIndexToDisk();
            sw.Stop();
            DebugUtil.Print("IndexManager", $"Index update completed in {sw.ElapsedMilliseconds}ms");
        }
        catch (Exception ex) {
            DebugUtil.Print("IndexManager", $"Index update failed: {ex.Message}");
            throw;
        }
        finally {
            _isUpdating = false;
            _lock.ExitWriteLock();
        }
    }

    private void SaveIndexToDisk() {
        try {
            var dir = Path.GetDirectoryName(_indexPath);
            if (!string.IsNullOrEmpty(dir)) { _fs.CreateDirectory(dir); }
            var json = System.Text.Json.JsonSerializer.Serialize(_index, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            var tmp = _indexPath + ".tmp";
            var bak = _indexPath + ".bak";
            _fs.WriteAllText(tmp, json, System.Text.Encoding.UTF8);
            try { _fs.Replace(tmp, _indexPath, bak, ignoreMetadataErrors: true); } catch { _fs.Move(tmp, _indexPath, overwrite: true); }
            DebugUtil.Print("IndexManager", $"Index saved to {_indexPath}");
        }
        catch (Exception ex) {
            DebugUtil.Print("IndexManager", $"Failed to save index: {ex.Message}");
            throw;
        }
    }

    public void Dispose() { _lock?.Dispose(); }
}

