namespace CodeCortex.Core.Index;

#pragma warning disable 1591
using CodeCortex.Core.IO;
/// <summary>
/// Quick timestamp-based validation for deciding whether an existing index can be reused.
/// Level 1 strategy: all recorded files must still exist and have identical LastWriteUtc ticks.
/// </summary>
public static class IndexReuseDecider {
    public static bool IsReusable(CodeCortexIndex existing, out int changedCount, out int totalFiles, IFileSystem? fs = null) {
        changedCount = 0;
        totalFiles = 0;
        if (existing.FileManifest == null || existing.FileManifest.Count == 0) { return false; /* no manifest -> cannot trust */ }
        fs ??= new DefaultFileSystem();
        foreach (var kvp in existing.FileManifest) {
            totalFiles++;
            var path = kvp.Key;
            var meta = kvp.Value;
            try {
                if (!fs.FileExists(path)) {
                    changedCount++;
                    if (changedCount > 0) { return false; }
                }
                else {
                    var ticks = fs.GetLastWriteTicks(path);
                    if (ticks != meta.LastWriteUtcTicks) {
                        changedCount++;
                        if (changedCount > 0) { return false; }
                    }
                }
            }
            catch {
                changedCount++;
                return false; // any IO exception -> treat as invalid
            }
        }
        return changedCount == 0;
    }
    public static bool IsReusableHash(CodeCortexIndex existing, out int changedCount, out int totalFiles, out int hashChecked, IFileSystem? fs = null) {
        changedCount = 0;
        totalFiles = 0;
        hashChecked = 0;
        if (existing.FileManifest == null || existing.FileManifest.Count == 0) { return false; }
        fs ??= new DefaultFileSystem();
        foreach (var kvp in existing.FileManifest) {
            totalFiles++;
            var path = kvp.Key;
            var meta = kvp.Value;
            try {
                if (!fs.FileExists(path)) {
                    changedCount++;
                    return false;
                }
                if (string.IsNullOrEmpty(meta.ContentHash)) {
                    changedCount++;
                    return false;
                }
                var cur = ComputeContentHash(path, fs);
                hashChecked++;
                if (!string.Equals(cur, meta.ContentHash, System.StringComparison.OrdinalIgnoreCase)) {
                    changedCount++;
                    return false;
                }
            }
            catch {
                changedCount++;
                return false;
            }
        }
        return changedCount == 0;
    }
    public static string ComputeContentHash(string filePath, IFileSystem? fs = null) {
        fs ??= new DefaultFileSystem();
        using var sha = System.Security.Cryptography.SHA256.Create();
        using var stream = fs.OpenRead(filePath);
        var hash = sha.ComputeHash(stream);
        char[] chars = new char[hash.Length * 2];
        int i = 0;
        foreach (var b in hash) {
            chars[i++] = GetHex(b >> 4);
            chars[i++] = GetHex(b & 0xF);
        }
        return new string(chars);
    }
    private static char GetHex(int v) => (char)(v < 10 ? '0' + v : 'A' + (v - 10));
}
#pragma warning restore 1591
