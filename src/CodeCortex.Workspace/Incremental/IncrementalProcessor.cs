using CodeCortex.Core.Hashing;
using CodeCortex.Core.Index;
using CodeCortex.Core.Outline;
using CodeCortex.Core.Ids;
using Microsoft.CodeAnalysis;
namespace CodeCortex.Workspace.Incremental;
#pragma warning disable 1591
public sealed record IncrementalResult(int ChangedTypeCount, int RemovedTypeCount, long DurationMs, int OutlineWrittenCount, int OutlineSkippedCount);
public interface IIncrementalProcessor { IncrementalResult Process(CodeCortexIndex index, ImpactResult impact, ITypeHasher hasher, IOutlineExtractor outline, Func<string, INamedTypeSymbol?> resolveById, string outlineDir, IFileSystem fs, CancellationToken ct); }
public sealed class IncrementalProcessor : IIncrementalProcessor {
    public IncrementalResult Process(CodeCortexIndex index, ImpactResult impact, ITypeHasher hasher, IOutlineExtractor outline, Func<string, INamedTypeSymbol?> resolveById, string outlineDir, IFileSystem fs, CancellationToken ct) {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (!fs.FileExists(outlineDir)) {
            fs.CreateDirectory(outlineDir);
        }
        int changed = 0;
        int outlineWritten = 0;
        int outlineSkipped = 0;
        // 1. 先处理已知受影响类型
        foreach (var id in impact.AffectedTypeIds) {
            var symbol = resolveById(id);
            if (symbol == null) {
                continue;
            }

            var hashes = hasher.Compute(symbol, Array.Empty<string>(), new CodeCortex.Core.Hashing.HashConfig());
            var fqn = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", "", StringComparison.Ordinal);
            var entry = index.Types.FirstOrDefault(t => t.Id == id);
            if (entry == null) {
                entry = new TypeEntry { Id = id, Fqn = fqn, Kind = symbol.TypeKind.ToString() };
                index.Types.Add(entry);
            }
            var oldFiles = entry.Files.ToList();
            bool structureChanged = entry.StructureHash != hashes.Structure;
            entry.StructureHash = hashes.Structure;
            entry.PublicImplHash = hashes.PublicImpl;
            entry.InternalImplHash = hashes.InternalImpl;
            entry.XmlDocHash = hashes.XmlDoc;
            entry.Files = symbol.DeclaringSyntaxReferences.Select(r => r.SyntaxTree.FilePath).Distinct().ToList();
            // ensure maps reflect latest fqn
            index.Maps.FqnIndex[fqn] = id;
            if (structureChanged) {
                var md = outline.BuildOutline(symbol, hashes, new CodeCortex.Core.Outline.OutlineOptions());
                fs.WriteAllText(System.IO.Path.Combine(outlineDir, id + ".outline.md"), md);
                outlineWritten++;
            } else {
                outlineSkipped++;
            }
            changed++;
            // Update name maps (simple ensure exists)
            if (!index.Maps.FqnIndex.ContainsKey(fqn)) {
                index.Maps.FqnIndex[fqn] = id;
            }

            var simple = symbol.Name;
            if (!index.Maps.NameIndex.TryGetValue(simple, out var nameList)) {
                nameList = new List<string>();
                index.Maps.NameIndex[simple] = nameList;
            }
            if (!nameList.Contains(id)) {
                nameList.Add(id);
            }
            // Update manifest timestamps for involved files
            foreach (var file in entry.Files) {
                try {
                    if (fs.FileExists(file)) {
                        index.FileManifest[file] = new FileEntry { LastWriteUtcTicks = fs.GetLastWriteTicks(file) };
                    }
                } catch { }
            }
        }
        // 2. 自动补全新增类型（AddedTypeFqns）
        foreach (var fqn in impact.AddedTypeFqns) {
            if (index.Maps.FqnIndex.ContainsKey(fqn)) {
                continue; // 已存在
            }
            // 尝试 resolve
            INamedTypeSymbol? symbol = null;
            foreach (var id in index.Types.Select(t => t.Id)) {
                var s = resolveById(id);
                if (s != null && s.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", "", StringComparison.Ordinal) == fqn) {
                    symbol = s;
                    break;
                }
            }
            if (symbol == null) {
                continue; // 无法定位
            }

            var newId = CodeCortex.Core.Ids.TypeIdGenerator.GetId(symbol);
            var hashes = hasher.Compute(symbol, Array.Empty<string>(), new CodeCortex.Core.Hashing.HashConfig());
            var entry = new TypeEntry { Id = newId, Fqn = fqn, Kind = symbol.TypeKind.ToString() };
            entry.StructureHash = hashes.Structure;
            entry.PublicImplHash = hashes.PublicImpl;
            entry.InternalImplHash = hashes.InternalImpl;
            entry.XmlDocHash = hashes.XmlDoc;
            entry.Files = symbol.DeclaringSyntaxReferences.Select(r => r.SyntaxTree.FilePath).Distinct().ToList();
            index.Types.Add(entry);
            index.Maps.FqnIndex[fqn] = newId;
            var simple = symbol.Name;
            if (!index.Maps.NameIndex.TryGetValue(simple, out var nameList)) {
                nameList = new List<string>();
                index.Maps.NameIndex[simple] = nameList;
            }
            if (!nameList.Contains(newId)) {
                nameList.Add(newId);
            }

            var md = outline.BuildOutline(symbol, hashes, new CodeCortex.Core.Outline.OutlineOptions());
            fs.WriteAllText(System.IO.Path.Combine(outlineDir, newId + ".outline.md"), md);
            outlineWritten++;
            changed++;
            foreach (var file in entry.Files) {
                try {
                    if (fs.FileExists(file)) {
                        index.FileManifest[file] = new FileEntry { LastWriteUtcTicks = fs.GetLastWriteTicks(file) };
                    }
                } catch { }
            }
        }
        int removed = 0;
        if (impact.RemovedTypeIds.Count > 0) {
            index.Types.RemoveAll(t => impact.RemovedTypeIds.Contains(t.Id));
            // also cleanup maps
            foreach (var id in impact.RemovedTypeIds) {
                foreach (var kv in index.Maps.FqnIndex.Where(kv => kv.Value == id).ToList()) {
                    index.Maps.FqnIndex.Remove(kv.Key);
                }

                foreach (var kv in index.Maps.NameIndex.ToList()) {
                    kv.Value.RemoveAll(v => v == id);
                    if (kv.Value.Count == 0) {
                        index.Maps.NameIndex.Remove(kv.Key);
                    }
                }
                var outlinePath = System.IO.Path.Combine(outlineDir, id + ".outline.md");
                try {
                    if (fs.FileExists(outlinePath)) {
                        fs.TryDelete(outlinePath);
                    }
                } catch { }
            }
            removed = impact.RemovedTypeIds.Count;
        }

        // Clean up manifest entries for deleted files
        var toRemoveFiles = new List<string>();
        foreach (var kv in index.FileManifest) {
            if (!fs.FileExists(kv.Key)) {
                toRemoveFiles.Add(kv.Key);
            }
        }
        foreach (var f in toRemoveFiles) {
            index.FileManifest.Remove(f);
        }

        // 增量元信息统计
        index.Incremental.OutlineVersion++;
        sw.Stop();
        index.Incremental.LastIncrementalMs = sw.ElapsedMilliseconds;
        index.Incremental.LastChangedTypeCount = changed;
        index.Incremental.LastRemovedTypeCount = removed;
        index.Incremental.LastOutlineWrittenCount = outlineWritten;
        index.Incremental.LastOutlineSkippedCount = outlineSkipped;
        return new IncrementalResult(changed, removed, sw.ElapsedMilliseconds, outlineWritten, outlineSkipped);
    }
}

public interface IFileSystem {
    bool FileExists(string path);
    long GetLastWriteTicks(string path);
    void WriteAllText(string path, string content);
    bool TryDelete(string path);
    void CreateDirectory(string path);
}

public sealed class DefaultFileSystem : IFileSystem {
    public bool FileExists(string path) => System.IO.File.Exists(path) || System.IO.Directory.Exists(path);
    public long GetLastWriteTicks(string path) => System.IO.File.GetLastWriteTimeUtc(path).Ticks;
    public void WriteAllText(string path, string content) {
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir)) {
            System.IO.Directory.CreateDirectory(dir);
        }
        System.IO.File.WriteAllText(path, content);
    }
    public bool TryDelete(string path) {
        try {
            System.IO.File.Delete(path);
            return true;
        } catch { return false; }
    }
    public void CreateDirectory(string path) {
        System.IO.Directory.CreateDirectory(path);
    }
}
#pragma warning restore 1591
