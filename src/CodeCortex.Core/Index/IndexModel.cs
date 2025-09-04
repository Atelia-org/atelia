namespace CodeCortex.Core.Index;

#pragma warning disable 1591 // Suppress XML doc warnings for Phase1 minimal model

/// <summary>Phase1 minimal persisted index model (S3).</summary>
public sealed class CodeCortexIndex {
    public string SchemaVersion { get; set; } = "1.0";
    public BuildInfo Build { get; set; } = new();
    public List<TypeEntry> Types { get; set; } = new();
    public NameMaps Maps { get; set; } = new();
    public Stats Stats { get; set; } = new();
    // Quick invalidation manifest: path -> metadata (timestamps / optional hash)
    public Dictionary<string, FileEntry> FileManifest { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, object> FutureReserved { get; set; } = new();
    public IncrementalInfo Incremental { get; set; } = new();
}

public sealed class BuildInfo {
    public string SolutionPath { get; set; } = string.Empty;
    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;
    public long DurationMs { get; set; }
    public bool Reused { get; set; }
}

public sealed class TypeEntry {
    public string Id { get; set; } = string.Empty;
    public string Fqn { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string StructureHash { get; set; } = string.Empty;
    public string PublicImplHash { get; set; } = string.Empty;
    public string InternalImplHash { get; set; } = string.Empty;
    public string XmlDocHash { get; set; } = string.Empty;
    public List<string> Files { get; set; } = new();
}

public sealed class NameMaps {
    public Dictionary<string, List<string>> NameIndex { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> FqnIndex { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, List<string>> GenericBaseNameIndex { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class Stats { public int ProjectCount { get; set; } public int TypeCount { get; set; } }

public sealed class FileEntry {
    public long LastWriteUtcTicks { get; set; }
    // Reserved for future stronger validation (content hash / length)
    public string? ContentHash { get; set; }
}

public sealed class IncrementalInfo {
    public long OutlineVersion { get; set; }
    public long LastIncrementalMs { get; set; }
    public int LastChangedTypeCount { get; set; }
    public int LastRemovedTypeCount { get; set; }
    public int LastOutlineWrittenCount { get; set; } = 0;
    public int LastOutlineSkippedCount { get; set; } = 0;
}

#pragma warning restore 1591
