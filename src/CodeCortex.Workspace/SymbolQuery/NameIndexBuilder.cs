using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;

namespace CodeCortex.Workspace.SymbolQuery {
    public sealed class NameIndexBuilder {
        public async Task<NameIndex> BuildAsync(LoadedSolution loaded, CancellationToken ct = default) {
            var fqns = new List<string>(capacity: 4096);
            foreach (var proj in loaded.Projects) {
                ct.ThrowIfCancellationRequested();
                var comp = await proj.GetCompilationAsync(ct).ConfigureAwait(false);
                if (comp is null) continue;
                foreach (var t in new RoslynTypeEnumerator().Enumerate(comp, ct)) {
                    var fqn = t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                    if (!string.IsNullOrEmpty(fqn)) fqns.Add(fqn);
                }
            }
            fqns = fqns.Distinct(StringComparer.Ordinal).ToList();
            return new NameIndex(
                Version: "1",
                SolutionPath: loaded.Solution.FilePath ?? string.Empty,
                BuiltAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ProjectCount: loaded.Projects.Count,
                TypeCount: fqns.Count,
                Fqns: fqns.ToArray()
            );
        }
    }
}

