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
    bool TryDelete(string path);
    void CreateDirectory(string path);
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
    public bool TryDelete(string path) {
        try {
            System.IO.File.Delete(path);
            return true;
        } catch { return false; }
    }
    public void CreateDirectory(string path) {
        System.IO.Directory.CreateDirectory(path);
    }
}
#pragma warning restore 1591
