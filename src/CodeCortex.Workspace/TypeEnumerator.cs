using Microsoft.CodeAnalysis;

namespace CodeCortex.Workspace;

public interface ITypeEnumerator {
    IEnumerable<INamedTypeSymbol> Enumerate(Compilation compilation, CancellationToken ct = default);
}

public sealed class RoslynTypeEnumerator : ITypeEnumerator {
    public IEnumerable<INamedTypeSymbol> Enumerate(Compilation compilation, CancellationToken ct = default) {
        if (compilation == null) {
            yield break;
        }

        var global = compilation.Assembly.GlobalNamespace;
        foreach (var t in EnumerateNamespace(global, ct)) {
            yield return t;
        }
    }

    private IEnumerable<INamedTypeSymbol> EnumerateNamespace(INamespaceSymbol ns, CancellationToken ct) {
        foreach (var type in ns.GetTypeMembers()) {
            foreach (var nested in EnumerateTypeRecursive(type, ct)) {
                yield return nested;
            }
        }
        foreach (var sub in ns.GetNamespaceMembers()) {
            ct.ThrowIfCancellationRequested();
            foreach (var t in EnumerateNamespace(sub, ct)) {
                yield return t;
            }
        }
    }

    private IEnumerable<INamedTypeSymbol> EnumerateTypeRecursive(INamedTypeSymbol t, CancellationToken ct) {
        yield return t;
        foreach (var nt in t.GetTypeMembers()) {
            ct.ThrowIfCancellationRequested();
            foreach (var inner in EnumerateTypeRecursive(nt, ct)) {
                yield return inner;
            }
        }
    }
}
