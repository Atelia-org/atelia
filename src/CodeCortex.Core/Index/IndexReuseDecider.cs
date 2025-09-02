namespace CodeCortex.Core.Index;

/// <summary>
/// Quick timestamp-based validation for deciding whether an existing index can be reused.
/// Level 1 strategy: all recorded files must still exist and have identical LastWriteUtc ticks.
/// </summary>
#pragma warning disable 1591
public static class IndexReuseDecider {
    public static bool IsReusable(CodeCortexIndex existing, out int changedCount, out int totalFiles) {
        changedCount = 0;
        totalFiles = 0;
        if (existing.FileManifest == null || existing.FileManifest.Count == 0) {
            return false; // no manifest -> cannot trust
        }
        foreach (var kvp in existing.FileManifest) {
            totalFiles++;
            var path = kvp.Key;
            var meta = kvp.Value;
            try {
                if (!System.IO.File.Exists(path)) {
                    changedCount++;
                    if (changedCount > 0) {
                        return false;
                    }
                } else {
                    var ticks = System.IO.File.GetLastWriteTimeUtc(path).Ticks;
                    if (ticks != meta.LastWriteUtcTicks) {
                        changedCount++;
                        if (changedCount > 0) {
                            return false;
                        }
                    }
                }
            } catch {
                changedCount++;
                return false; // any IO exception -> treat as invalid
            }
        }
        return changedCount == 0;
    }
#pragma warning restore 1591
}
