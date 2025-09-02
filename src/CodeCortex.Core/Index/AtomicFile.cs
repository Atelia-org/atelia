namespace CodeCortex.Core.Index;

internal static class AtomicFile {
    public static void WriteAtomic(string finalPath, string contentUtf8, Func<string, bool> validate) {
        var dir = Path.GetDirectoryName(finalPath)!;
        Directory.CreateDirectory(dir);
        var tmp = finalPath + ".tmp";
        File.WriteAllText(tmp, contentUtf8, System.Text.Encoding.UTF8);
        if (!validate(tmp)) {
            var broken = finalPath + ".broken-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            File.Move(tmp, broken, true);
            throw new InvalidOperationException("Index validation failed; broken copy saved: " + broken);
        }
        var bak = finalPath + ".bak";
        try {
            if (File.Exists(finalPath)) {
                File.Replace(tmp, finalPath, bak, true);
            } else {
                File.Move(tmp, finalPath, true);
            }
        } catch {
            if (File.Exists(tmp) && !File.Exists(finalPath)) {
                File.Move(tmp, finalPath, true);
            }

            throw;
        }
    }
}
