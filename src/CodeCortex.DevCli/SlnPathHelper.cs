using System;
using System.IO;

namespace CodeCortex.DevCli.Util {
    public static class SlnPathHelper {
        public static string ResolveEntryPath(string path) {
            if (string.IsNullOrWhiteSpace(path)) {
                throw new InvalidOperationException("path argument is empty.");
            }
            if (Directory.Exists(path)) {
                var slns = Directory.GetFiles(path, "*.sln", SearchOption.TopDirectoryOnly);
                if (slns.Length == 1) {
                    return slns[0];
                }
            }
            try { return Path.GetFullPath(path); } catch { return path; }
        }
    }
}

