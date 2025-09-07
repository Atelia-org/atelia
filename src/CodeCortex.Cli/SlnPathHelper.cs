using System;
using System.IO;

namespace CodeCortex.Cli.Util {
    public static class SlnPathHelper {
        public static string ResolveSlnOrThrow(string? pathOpt) {
            var path = pathOpt;
            if (string.IsNullOrWhiteSpace(path)) {
                // find unique .sln under CWD
                var cwd = Directory.GetCurrentDirectory();
                var slns = Directory.GetFiles(cwd, "*.sln", SearchOption.TopDirectoryOnly);
                if (slns.Length == 1) {
                    return slns[0];
                }
                throw new InvalidOperationException($"未指定 path 且在当前目录未找到唯一的 .sln 文件（找到 {slns.Length} 个）。");
            }
            if (Directory.Exists(path)) {
                var slns = Directory.GetFiles(path, "*.sln", SearchOption.TopDirectoryOnly);
                if (slns.Length == 1) {
                    return slns[0];
                }
                if (slns.Length == 0) {
                    throw new InvalidOperationException($"目录中未找到 .sln: {path}");
                }
                throw new InvalidOperationException($"目录中存在多个 .sln，请显式指定: {path}");
            }
            try { return Path.GetFullPath(path); } catch { return path!; }
        }
    }
}

