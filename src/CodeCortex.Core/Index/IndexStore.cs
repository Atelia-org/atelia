using System.Text.Json;

#pragma warning disable 1591

namespace CodeCortex.Core.Index;

/// <summary>Load/Save for CodeCortex index with corruption fallback.</summary>
public sealed class IndexStore {
    private readonly JsonSerializerOptions _opts = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public string RootDir { get; }
    public string IndexPath => Path.Combine(RootDir, "index.json");
    public IndexStore(string rootDir) { RootDir = rootDir; }

    public CodeCortexIndex? TryLoad(out string reason) {
        reason = string.Empty;
        try {
            if (!File.Exists(IndexPath)) {
                reason = "missing";
                return null;
            }
            var json = File.ReadAllText(IndexPath);
            var model = JsonSerializer.Deserialize<CodeCortexIndex>(json, _opts);
            if (model == null || model.Types.Count == 0) {
                reason = "empty";
                return null;
            }
            return model;
        } catch (Exception ex) {
            reason = "corrupt:" + ex.GetType().Name;
            var bak = IndexPath + ".bak";
            if (File.Exists(bak)) {
                try {
                    var json = File.ReadAllText(bak);
                    var model = JsonSerializer.Deserialize<CodeCortexIndex>(json, _opts);
                    if (model != null) {
                        return model;
                    }
                } catch { }
            }
            return null;
        }
    }

    public void Save(CodeCortexIndex index) {
        var json = JsonSerializer.Serialize(index, _opts);
        AtomicFile.WriteAtomic(IndexPath, json, Validate);
    }

    private bool Validate(string tmpPath) {
        try {
            var json = File.ReadAllText(tmpPath);
            var model = JsonSerializer.Deserialize<CodeCortexIndex>(json, _opts);
            return model != null && model.SchemaVersion == "1.0";
        } catch { return false; }
    }
}

#pragma warning restore 1591
