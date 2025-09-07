using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.SymbolDisplay;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CodeCortex.Workspace.SymbolQuery {
    public sealed class WorkspaceTypeSource : ITypeSource {
        private static readonly SymbolDisplayFormat FqnFormat = new SymbolDisplayFormat(
            globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Included,
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces
        );

        public async Task<IReadOnlyList<TypeEntry>> ListAsync(LoadedSolution loaded, CancellationToken ct = default) {
            var list = new List<TypeEntry>(capacity: 1024);
            foreach (var proj in loaded.Projects) {
                ct.ThrowIfCancellationRequested();
                var comp = await proj.GetCompilationAsync(ct).ConfigureAwait(false);
                if (comp is null) continue;

                var enumerator = new RoslynTypeEnumerator();
                foreach (var t in enumerator.Enumerate(comp, ct)) {
                    var fqn = t.ToDisplayString(FqnFormat);
                    var simple = t.Name ?? string.Empty;
                    if (!string.IsNullOrEmpty(fqn)) {
                        list.Add(new TypeEntry(fqn, simple));
                    }
                }
            }
            // Dedup by FQN to be safe
            return list
                .GroupBy(e => e.Fqn)
                .Select(g => g.First())
                .ToList();
        }
    }
}

