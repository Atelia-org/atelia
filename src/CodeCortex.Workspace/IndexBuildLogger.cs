using System.Text;

namespace CodeCortex.Workspace;

/// <summary>
/// Minimal append-only logger for S1. TODO: replace with atomic write/rotation in later sessions.
/// </summary>
public static class IndexBuildLogger {
    private static readonly object _gate = new();
    private static string? _logDir;

    public static void Initialize(string rootDir) {
        var codecortex = Path.Combine(rootDir, ".codecortex");
        var logs = Path.Combine(codecortex, "logs");
        Directory.CreateDirectory(logs);
        _logDir = logs;
    }

    public static void Log(string message) {
        try {
            if (_logDir == null) {
                return; // Not initialized; best-effort.
            }

            var line = $"[{DateTime.UtcNow:O}] {message}" + Environment.NewLine;
            var path = Path.Combine(_logDir, "index_build.log");
            lock (_gate) {
                File.AppendAllText(path, line, Encoding.UTF8);
            }
        } catch {
            // Swallow logging errors in S1.
        }
    }
}
