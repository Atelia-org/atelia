using CodeCortex.Core.Index;
using CodeCortex.Core.IO;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
namespace CodeCortex.Workspace.Incremental;
#pragma warning disable 1591
public sealed record ImpactResult(
    HashSet<string> AffectedTypeIds,
    List<string> RemovedTypeIds,
    List<ClassifiedFileChange> Changes,
    List<string> AddedTypeFqns,
    List<string> RemovedTypeFqns,
    List<string> RetainedTypeFqns
);
public interface IImpactAnalyzer { ImpactResult Analyze(CodeCortexIndex index, IReadOnlyList<ClassifiedFileChange> changes, Func<string, Project?> resolveProject, CancellationToken ct); }
public sealed class ImpactAnalyzer : IImpactAnalyzer {
    private readonly IFileSystem _fs;
    public ImpactAnalyzer(IFileSystem? fs = null) { _fs = fs ?? new DefaultFileSystem(); }
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
        var addedFqns = new List<string>();
        var removedFqns = new List<string>();
        var retainedFqns = new List<string>();
        // Build reverse index file -> types and FQN
        var rev = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var fileToFqns = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var fqnToFiles = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var t in index.Types) {
            foreach (var f in t.Files) {
                if (!rev.TryGetValue(f, out var list)) {
                    list = new();
                    rev[f] = list;
                }
                list.Add(t.Id);
                if (!fileToFqns.TryGetValue(f, out var fqns)) {
                    fqns = new();
                    fileToFqns[f] = fqns;
                }
                fqns.Add(t.Fqn);
                if (!fqnToFiles.TryGetValue(t.Fqn, out var files)) {
                    files = new();
                    fqnToFiles[t.Fqn] = files;
                }
                files.Add(f);
            }
        }
        foreach (var ch in changes) {
            var path = ch.Path;
            switch (ch.Kind) {
                case ClassifiedKind.Add:
                case ClassifiedKind.Modify:
                case ClassifiedKind.Rename:
                    if (!_fs.FileExists(path)) {
                        continue;
                    }

                    var proj = resolveProject(path);
                    if (proj == null) {
                        continue;
                    }

                    var comp = proj.GetCompilationAsync(ct).GetAwaiter().GetResult();
                    if (comp == null) {
                        continue;
                    }

                    var tree = comp.SyntaxTrees.FirstOrDefault(t => string.Equals(t.FilePath, path, StringComparison.OrdinalIgnoreCase));
                    if (tree == null) {
                        continue;
                    }

                    var newFqns = new HashSet<string>(GetTypeFqnsInFile(comp, tree));
                    var oldFqns = fileToFqns.TryGetValue(path, out var oldSet) ? oldSet : new HashSet<string>();
                    var added = newFqns.Except(oldFqns).ToList();
                    var removedSet = oldFqns.Except(newFqns).ToList();
                    var retained = newFqns.Intersect(oldFqns).ToList();
                    addedFqns.AddRange(added);
                    removedFqns.AddRange(removedSet);
                    retainedFqns.AddRange(retained);
                    // 新增类型：无须受影响（后续增量处理会新建 TypeEntry）
                    // 删除类型：需判定 partial 是否所有文件都被删
                    foreach (var fqn in removedSet) {
                        if (fqnToFiles.TryGetValue(fqn, out var allFiles)) {
                            var stillExists = allFiles.Any(f => !string.Equals(f, path, StringComparison.OrdinalIgnoreCase) && _fs.FileExists(f));
                            if (!stillExists && index.Maps.FqnIndex.TryGetValue(fqn, out var id)) {
                                removed.Add(id);
                            }
                        }
                    }
                    // 保留类型：如结构有变，affected
                    foreach (var fqn in retained) {
                        if (index.Maps.FqnIndex.TryGetValue(fqn, out var id)) {
                            affected.Add(id);
                        }
                    }
                    // 新增类型不在 index，后续增量处理会补全
                    break;
                case ClassifiedKind.Delete:
                    // 文件被删除，所有类型需判定 partial
                    if (fileToFqns.TryGetValue(path, out var oldFqnsDel)) {
                        foreach (var fqn in oldFqnsDel) {
                            if (fqnToFiles.TryGetValue(fqn, out var allFiles)) {
                                var stillExists = allFiles.Any(f => !string.Equals(f, path, StringComparison.OrdinalIgnoreCase) && _fs.FileExists(f));
                                if (!stillExists && index.Maps.FqnIndex.TryGetValue(fqn, out var id)) {
                                    removed.Add(id);
                                    removedFqns.Add(fqn);
                                }
                            }
                        }
                    }
                    break;
            }
        }
        return new ImpactResult(affected, removed, changes.ToList(), addedFqns, removedFqns, retainedFqns);
    }
}
#pragma warning restore 1591
