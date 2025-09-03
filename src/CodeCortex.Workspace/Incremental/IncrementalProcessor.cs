using CodeCortex.Core.Hashing;
using CodeCortex.Core.Index;
using CodeCortex.Core.Outline;
using Microsoft.CodeAnalysis;
namespace CodeCortex.Workspace.Incremental;
#pragma warning disable 1591
public sealed record IncrementalResult(int ChangedTypeCount, int RemovedTypeCount, long DurationMs);
public interface IIncrementalProcessor { IncrementalResult Process(CodeCortexIndex index, ImpactResult impact, ITypeHasher hasher, IOutlineExtractor outline, Func<string, INamedTypeSymbol?> resolveById, string outlineDir, CancellationToken ct, CodeCortex.Core.Index.IOutlineWriter? writer = null, IFileSystem? fs = null); }
public sealed class IncrementalProcessor : IIncrementalProcessor {
    public IncrementalResult Process(CodeCortexIndex index, ImpactResult impact, ITypeHasher hasher, IOutlineExtractor outline, Func<string, INamedTypeSymbol?> resolveById, string outlineDir, CancellationToken ct, CodeCortex.Core.Index.IOutlineWriter? writer = null, IFileSystem? fs = null) {
        fs ??= new RealFileSystem();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Directory.CreateDirectory(outlineDir);
        int changed = 0;
        foreach (var id in impact.AffectedTypeIds) {
            var symbol = resolveById(id);
            if (symbol == null) {
                continue;
            }

            var hashes = hasher.Compute(symbol, Array.Empty<string>(), new CodeCortex.Core.Hashing.HashConfig());
            var fqn = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", "", StringComparison.Ordinal);
            var entry = index.Types.FirstOrDefault(t => t.Id == id);
            bool skipOutline = false;
            if (entry == null) {
                entry = new TypeEntry { Id = id, Fqn = fqn, Kind = symbol.TypeKind.ToString() };
                index.Types.Add(entry);
            } else {
                // 如果结构 hash 未变，跳过 outline 写入
                if (entry.StructureHash == hashes.Structure) {
                    skipOutline = true;
                }
            }
            var oldFiles = entry.Files.ToList();
            entry.StructureHash = hashes.Structure;
            entry.PublicImplHash = hashes.PublicImpl;
            entry.InternalImplHash = hashes.InternalImpl;
            entry.XmlDocHash = hashes.XmlDoc;
            entry.Files = symbol.DeclaringSyntaxReferences.Select(r => r.SyntaxTree.FilePath).Distinct().ToList();
            // ensure maps reflect latest fqn
            index.Maps.FqnIndex[fqn] = id;
            if (!skipOutline) {
                var md = outline.BuildOutline(symbol, hashes, new CodeCortex.Core.Outline.OutlineOptions());
                if (writer == null) {
                    fs.WriteAllText(Path.Combine(outlineDir, id + ".outline.md"), md);
                } else {
                    writer.EnsureDirectory();
                    writer.Write(id, md);
                }
                changed++;
            }
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
                var outlinePath = Path.Combine(outlineDir, id + ".outline.md");
                try { fs.TryDelete(outlinePath); } catch { }
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

        index.Incremental.OutlineVersion++;
        sw.Stop();
        index.Incremental.LastIncrementalMs = sw.ElapsedMilliseconds;
        index.Incremental.LastChangedTypeCount = changed;
        index.Incremental.LastRemovedTypeCount = removed;
        return new IncrementalResult(changed, removed, sw.ElapsedMilliseconds);
    }
}
#pragma warning restore 1591
