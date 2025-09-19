using CodeCortex.Core.IO;
namespace CodeCortex.Core.Index;

internal static class AtomicFile {
    public static void WriteAtomic(string finalPath, string contentUtf8, Func<string, bool> validate, IFileSystem? fs = null) {
        fs ??= new DefaultFileSystem();
        var dir = Path.GetDirectoryName(finalPath)!;
        fs.CreateDirectory(dir);
        var tmp = finalPath + ".tmp";
        fs.WriteAllText(tmp, contentUtf8, System.Text.Encoding.UTF8);
        if (!validate(tmp)) {
            var broken = finalPath + ".broken-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            fs.Move(tmp, broken, true);
            throw new InvalidOperationException("Index validation failed; broken copy saved: " + broken);
        }
        var bak = finalPath + ".bak";
        try {
            if (fs.FileExists(finalPath)) {
                fs.Replace(tmp, finalPath, bak, true);
            }
            else {
                fs.Move(tmp, finalPath, true);
            }
        }
        catch {
            if (fs.FileExists(tmp) && !fs.FileExists(finalPath)) {
                fs.Move(tmp, finalPath, true);
            }
            throw;
        }
    }
}
