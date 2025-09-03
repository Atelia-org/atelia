using CodeCortex.Core.Hashing;
using CodeCortex.Core.Index;
using CodeCortex.Core.Outline;
using CodeCortex.Core.Ids;
using Microsoft.CodeAnalysis;

namespace CodeCortex.Core.IO;

#pragma warning disable 1591
public interface IFileSystem {
    bool FileExists(string path);
    long GetLastWriteTicks(string path);
    void WriteAllText(string path, string content);
    void WriteAllText(string path, string content, System.Text.Encoding encoding);
    string ReadAllText(string path);
    string ReadAllText(string path, System.Text.Encoding encoding);
    bool TryDelete(string path);
    void CreateDirectory(string path);
    void Move(string src, string dest, bool overwrite = false);
    void Replace(string src, string dest, string backup, bool ignoreMetadataErrors = false);
    System.IO.Stream OpenRead(string path);
}

public sealed class DefaultFileSystem : IFileSystem {
    public bool FileExists(string path) => System.IO.File.Exists(path) || System.IO.Directory.Exists(path);
    public long GetLastWriteTicks(string path) => System.IO.File.GetLastWriteTimeUtc(path).Ticks;
    public void WriteAllText(string path, string content) {
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir)) {
            System.IO.Directory.CreateDirectory(dir);
        }
        System.IO.File.WriteAllText(path, content);
    }
    public void WriteAllText(string path, string content, System.Text.Encoding encoding) {
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir)) {
            System.IO.Directory.CreateDirectory(dir);
        }
        System.IO.File.WriteAllText(path, content, encoding);
    }
    public string ReadAllText(string path) => System.IO.File.ReadAllText(path);
    public string ReadAllText(string path, System.Text.Encoding encoding) => System.IO.File.ReadAllText(path, encoding);
    public bool TryDelete(string path) {
        try {
            System.IO.File.Delete(path);
            return true;
        } catch { return false; }
    }
    public void CreateDirectory(string path) {
        System.IO.Directory.CreateDirectory(path);
    }
    public void Move(string src, string dest, bool overwrite = false) {
        if (overwrite && System.IO.File.Exists(dest)) {
            System.IO.File.Delete(dest);
        }
        System.IO.File.Move(src, dest);
    }
    public void Replace(string src, string dest, string backup, bool ignoreMetadataErrors = false) {
        System.IO.File.Replace(src, dest, backup, ignoreMetadataErrors);
    }
    public System.IO.Stream OpenRead(string path) => System.IO.File.OpenRead(path);
}
#pragma warning restore 1591
