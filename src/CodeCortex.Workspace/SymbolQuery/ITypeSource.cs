using Microsoft.CodeAnalysis;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CodeCortex.Workspace.SymbolQuery {
    public sealed record TypeEntry(string Fqn, string SimpleName);

    public interface ITypeSource {
        Task<IReadOnlyList<TypeEntry>> ListAsync(LoadedSolution loaded, CancellationToken ct = default);
    }
}

