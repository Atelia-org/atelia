using System;
using System.IO;
using System.Threading;
using CodeCortex.Core.Index;
using CodeCortex.Core.IO;
using Atelia.Diagnostics;

namespace CodeCortex.Service;

/// <summary>
/// 管理Service进程中的CodeCortexIndex，提供线程安全的读写访问
/// 使用读写锁确保增量更新期间的数据一致性
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

    /// <summary>
    /// 当前是否正在更新索引
    /// </summary>
    public bool IsUpdating => _isUpdating;

    /// <summary>
    /// 获取索引统计信息（线程安全）
    /// </summary>
    public Stats GetStats() {
        _lock.EnterReadLock();
        try {
            return _index.Stats;
        } finally {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// 在读锁保护下执行索引读取操作
    /// 如果正在更新，会等待更新完成
    /// </summary>
    public T ReadIndex<T>(Func<CodeCortexIndex, T> reader) {
        if (reader == null) {
            throw new ArgumentNullException(nameof(reader));
        }

        _lock.EnterReadLock();
        try {
            return reader(_index);
        } finally {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// 在写锁保护下执行索引更新操作
    /// 更新期间所有读取操作会被阻塞
    /// </summary>
    public void UpdateIndex(Action<CodeCortexIndex> updater) {
        if (updater == null) {
            throw new ArgumentNullException(nameof(updater));
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        _lock.EnterWriteLock();
        try {
            _isUpdating = true;
            DebugUtil.Print("IndexManager", "Starting index update");

            updater(_index);

            // 更新后立即保存到磁盘
            SaveIndexToDisk();

            sw.Stop();
            DebugUtil.Print("IndexManager", $"Index update completed in {sw.ElapsedMilliseconds}ms");
        } catch (Exception ex) {
            DebugUtil.Print("IndexManager", $"Index update failed: {ex.Message}");
            throw;
        } finally {
            _isUpdating = false;
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// 保存索引到磁盘
    /// </summary>
    private void SaveIndexToDisk() {
        try {
            var dir = Path.GetDirectoryName(_indexPath);
            if (!string.IsNullOrEmpty(dir)) {
                _fs.CreateDirectory(dir);
            }

            var json = System.Text.Json.JsonSerializer.Serialize(_index,
                new System.Text.Json.JsonSerializerOptions {
                    WriteIndented = true
                }
            );

            // 原子性写盘：先写入临时文件，再替换目标文件
            var tmp = _indexPath + ".tmp";
            var bak = _indexPath + ".bak";
            _fs.WriteAllText(tmp, json, System.Text.Encoding.UTF8);
            try {
                _fs.Replace(tmp, _indexPath, bak, ignoreMetadataErrors: true);
            } catch {
                // 某些文件系统不支持 Replace（例如 MockFileSystem），退化为覆盖移动
                _fs.Move(tmp, _indexPath, overwrite: true);
            }

            DebugUtil.Print("IndexManager", $"Index saved to {_indexPath}");
        } catch (Exception ex) {
            DebugUtil.Print("IndexManager", $"Failed to save index: {ex.Message}");
            throw;
        }
    }

    public void Dispose() {
        _lock?.Dispose();
    }
}
