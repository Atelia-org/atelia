using CodeCortex.Core.IO;
using System.IO.Abstractions.TestingHelpers;

namespace Atelia.CodeCortex.Tests;

internal class InMemoryFileSystem : IFileSystem {
    private readonly MockFileSystem _fs;
    public InMemoryFileSystem(MockFileSystem fs) { _fs = fs; }
    public bool FileExists(string path) => _fs.FileExists(path);
    public long GetLastWriteTicks(string path) => _fs.File.GetLastWriteTimeUtc(path).Ticks;
    public void WriteAllText(string path, string content) {
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !_fs.Directory.Exists(dir)) {
            _fs.Directory.CreateDirectory(dir);
        }
        _fs.File.WriteAllText(path, content);
    }
    public void WriteAllText(string path, string content, System.Text.Encoding encoding) {
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !_fs.Directory.Exists(dir)) {
            _fs.Directory.CreateDirectory(dir);
        }
        var bytes = encoding.GetBytes(content);
        using var stream = _fs.File.Open(path, System.IO.FileMode.Create, System.IO.FileAccess.Write);
        stream.Write(bytes, 0, bytes.Length);
    }
    public string ReadAllText(string path) => _fs.File.ReadAllText(path);
    public string ReadAllText(string path, System.Text.Encoding encoding) {
        using var stream = _fs.File.OpenRead(path);
        using var reader = new System.IO.StreamReader(stream, encoding);
        return reader.ReadToEnd();
    }
    public bool TryDelete(string path) {
        _fs.File.Delete(path);
        return true;
    }
    public void CreateDirectory(string path) {
        _fs.Directory.CreateDirectory(path);
    }
    public void Move(string src, string dest, bool overwrite = false) {
        if (overwrite && _fs.FileExists(dest)) {
            _fs.File.Delete(dest);
        }
        _fs.File.Move(src, dest);
    }
    public void Replace(string src, string dest, string backup, bool ignoreMetadataErrors = false) {
        // MockFileSystem 没有 Replace，模拟为 Move+备份
        if (_fs.FileExists(dest)) {
            _fs.File.Copy(dest, backup, true);
            _fs.File.Delete(dest);
        }
        _fs.File.Move(src, dest);
    }
    public System.IO.Stream OpenRead(string path) => _fs.File.OpenRead(path);
}
