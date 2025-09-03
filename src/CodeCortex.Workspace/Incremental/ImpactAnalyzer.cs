using CodeCortex.Core.Index;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
namespace CodeCortex.Workspace.Incremental;
#pragma warning disable 1591
public sealed record ImpactResult(HashSet<string> AffectedTypeIds, List<string> RemovedTypeIds, List<ClassifiedFileChange> Changes);
public interface ICompilationProvider { Compilation? GetCompilation(Project project, CancellationToken ct); }
public sealed class DefaultCompilationProvider : ICompilationProvider { public Compilation? GetCompilation(Project project, CancellationToken ct) => project.GetCompilationAsync(ct).GetAwaiter().GetResult(); }
public interface IFileSystem { bool FileExists(string path); long GetLastWriteTicks(string path); void WriteAllText(string path, string content); bool TryDelete(string path); }
public sealed class RealFileSystem : IFileSystem {
    public bool FileExists(string path) => File.Exists(path);
    public long GetLastWriteTicks(string path) => File.GetLastWriteTimeUtc(path).Ticks;
    public void WriteAllText(string path, string content) => File.WriteAllText(path, content);
    public bool TryDelete(string path) {
        try {
            if (File.Exists(path)) {
                File.Delete(path);
            }

            return true;
        } catch { return false; }
    }
}
public interface IImpactAnalyzer { ImpactResult Analyze(CodeCortexIndex index, IReadOnlyList<ClassifiedFileChange> changes, Func<string, Project?> resolveProject, CancellationToken ct); }
public sealed class ImpactAnalyzer : IImpactAnalyzer {
    private readonly ICompilationProvider _compProvider;
    public ImpactAnalyzer(ICompilationProvider? compProvider = null) { _compProvider = compProvider ?? new DefaultCompilationProvider(); }
    private static IEnumerable<string> GetTypeFqnsInFile(Compilation comp, SyntaxTree tree) {
        var model = comp.GetSemanticModel(tree, ignoreAccessibility: true);
        var root = tree.GetRoot();
        foreach (var decl in root.DescendantNodes().OfType<TypeDeclarationSyntax>()) {
            var symbol = model.GetDeclaredSymbol(decl) as INamedTypeSymbol;
            if (symbol == null) {
                continue;
            }

            var fqn = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", "", StringComparison.Ordinal);
            yield return fqn;
        }
    }
    public ImpactResult Analyze(CodeCortexIndex index, IReadOnlyList<ClassifiedFileChange> changes, Func<string, Project?> resolveProject, CancellationToken ct) {
        var affected = new HashSet<string>(StringComparer.Ordinal);
        var removed = new List<string>();
        // Build reverse index file -> types
        var rev = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in index.Types) {
            foreach (var f in t.Files) {
                if (!rev.TryGetValue(f, out var list)) {
                    list = new();
                    rev[f] = list;
                }
                list.Add(t.Id);
            }
        }
        foreach (var ch in changes) {
            switch (ch.Kind) {
                case ClassifiedKind.Add:
                case ClassifiedKind.Modify:
                case ClassifiedKind.Rename:
                    var path = ch.Path;
                    if (!File.Exists(path)) {
                        continue;
                    }

                    var proj = resolveProject(path);
                    if (proj == null) {
                        continue;
                    }

                    var comp = _compProvider.GetCompilation(proj, ct);
                    if (comp == null) {
                        continue;
                    }

                    var tree = comp.SyntaxTrees.FirstOrDefault(t => string.Equals(t.FilePath, path, StringComparison.OrdinalIgnoreCase));
                    if (tree == null) {
                        continue;
                    }
                    // find FQNs declared in this file and mark corresponding TypeIds
                    var fqns = GetTypeFqnsInFile(comp, tree).ToList();
                    if (fqns.Count == 0) {
                        // fallback: any types referencing this file (partial) still affected
                        if (rev.TryGetValue(path, out var list2)) {
                            foreach (var id in list2) {
                                affected.Add(id);
                            }
                        }

                        break;
                    }
                    foreach (var fqn in fqns) {
                        if (index.Maps.FqnIndex.TryGetValue(fqn, out var id)) {
                            affected.Add(id);
                        } else {
                            // new type added -> find by file mapping (partial/new) and add all from this file
                            if (rev.TryGetValue(path, out var list3)) {
                                foreach (var id2 in list3) {
                                    affected.Add(id2);
                                }
                            }
                        }
                    }
                    break;
                case ClassifiedKind.Delete:
                    if (rev.TryGetValue(ch.Path, out var list)) {
                        foreach (var id in list) { removed.Add(id); }
                    }
                    break;
            }
        }
        return new ImpactResult(affected, removed, changes.ToList());
    }
}
#pragma warning restore 1591
