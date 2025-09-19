using CodeCortex.Core.Hashing;
using CodeCortex.Core.Ids;
using CodeCortex.Core.Index;
using CodeCortex.Core.Outline;
using Microsoft.CodeAnalysis;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CodeCortex.Workspace;

public sealed class IndexBuilder {
    private readonly ITypeEnumerator _enumerator;
    private readonly ITypeHasher _hasher;
    private readonly IOutlineExtractor _outline;
    private readonly IIndexBuildObserver _observer;

    public IndexBuilder(ITypeEnumerator enumerator, ITypeHasher hasher, IOutlineExtractor outline, IIndexBuildObserver? observer = null) {
        _enumerator = enumerator;
        _hasher = hasher;
        _outline = outline;
        _observer = observer ?? CodeCortex.Core.Index.NullIndexBuildObserver.Instance;
    }

    public CodeCortex.Core.Index.CodeCortexIndex Build(CodeCortex.Core.Index.IndexBuildRequest request) {
        _observer.OnStart(request);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var index = new CodeCortex.Core.Index.CodeCortexIndex();
        index.Build.SolutionPath = request.SolutionPath;

        var all = new List<INamedTypeSymbol>();
        foreach (var p in request.Projects) {
            var comp = p.HasDocuments ? p.GetCompilationAsync().GetAwaiter().GetResult() : null;
            if (comp == null) { continue; }
            index.Stats.ProjectCount++;
            foreach (var t in _enumerator.Enumerate(comp)) {
                if (t.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal)) { continue; }
                all.Add(t);
            }
        }

        var ordered = all.OrderBy(t => t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), StringComparer.Ordinal).ToList();
        foreach (var t in ordered) {
            index.Stats.TypeCount++;
            var hashes = _hasher.Compute(t, Array.Empty<string>(), request.HashConfig);
            var id = TypeIdGenerator.GetId(t);
            var fqn = t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", "", StringComparison.Ordinal);
            var entry = new CodeCortex.Core.Index.TypeEntry {
                Id = id,
                Fqn = fqn,
                Kind = t.TypeKind.ToString(),
                StructureHash = hashes.Structure,
                PublicImplHash = hashes.PublicImpl,
                InternalImplHash = hashes.InternalImpl,
                XmlDocHash = hashes.XmlDoc,
                Files = t.DeclaringSyntaxReferences.Select(r => r.SyntaxTree.FilePath).Distinct().ToList()
            };
            index.Types.Add(entry);
            if (!index.Maps.NameIndex.TryGetValue(t.Name, out var list)) {
                list = new List<string>();
                index.Maps.NameIndex[t.Name] = list;
            }
            list.Add(id);
            index.Maps.FqnIndex[fqn] = id;

            // 为泛型类型构建基础名称索引
            if (t.Arity > 0) {
                var baseName = t.Name; // 不带泛型参数的基础名称
                if (!index.Maps.GenericBaseNameIndex.TryGetValue(baseName, out var genericList)) {
                    genericList = new List<string>();
                    index.Maps.GenericBaseNameIndex[baseName] = genericList;
                }
                genericList.Add(id);
            }

            _observer.OnTypeAdded(id, fqn);
            if (request.GenerateOutlines) {
                request.OutlineWriter.EnsureDirectory();
                var outlineText = _outline.BuildOutline(t, hashes, request.OutlineOptions);
                request.OutlineWriter.Write(id, outlineText);
                _observer.OnOutlineWritten(id);
            }
        }

        sw.Stop();
        index.Build.DurationMs = sw.ElapsedMilliseconds;
        index.Build.GeneratedAtUtc = request.Clock.UtcNow;
        // Populate file manifest for quick reuse invalidation (Level 1)
        try {
            bool wantHash = Environment.GetEnvironmentVariable("CODECORTEX_REUSE_HASH") == "1";
            var allFiles = index.Types.SelectMany(t => t.Files).Where(f => !string.IsNullOrWhiteSpace(f))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var file in allFiles) {
                try {
                    if (!System.IO.File.Exists(file)) { continue; }
                    var ticks = System.IO.File.GetLastWriteTimeUtc(file).Ticks;
                    var fe = new CodeCortex.Core.Index.FileEntry { LastWriteUtcTicks = ticks };
                    if (wantHash) {
                        fe.ContentHash = CodeCortex.Core.Index.IndexReuseDecider.ComputeContentHash(file);
                    }
                    index.FileManifest[file] = fe;
                }
                catch { /* ignore per-file errors */ }
            }
        }
        catch { /* ignore manifest population errors */ }
        _observer.OnCompleted(index, sw.ElapsedMilliseconds);
        return index;
    }
}
