using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CodeCortex.Workspace.SymbolQuery {
    public sealed class NameIndexStore {
        private const string FileName = "name-index.v1.json";

        public static string GetStoreDir(string solutionPath) {
            try {
                var dir = Path.GetDirectoryName(solutionPath);
                if (!string.IsNullOrEmpty(dir)) {
                    return Path.Combine(dir!, ".codecortex");
                }
            } catch { }
            return Path.Combine(Directory.GetCurrentDirectory(), ".codecortex");
        }

        public static string GetIndexPath(string solutionPath)
            => Path.Combine(GetStoreDir(solutionPath), FileName);

        public NameIndex? TryLoad(string solutionPath) {
            var path = GetIndexPath(solutionPath);
            if (!File.Exists(path)) return null;
            try {
                var json = File.ReadAllText(path);
                var index = JsonSerializer.Deserialize<NameIndex>(json);
                return index;
            } catch { return null; }
        }

        public bool Save(NameIndex index) {
            try {
                var dir = GetStoreDir(index.SolutionPath);
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, FileName);
                var json = JsonSerializer.Serialize(index, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(path, json);
                return true;
            } catch { return false; }
        }
    }
}

