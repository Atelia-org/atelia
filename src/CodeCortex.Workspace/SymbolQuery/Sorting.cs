using System.Collections.Generic;
using System.Linq;

namespace CodeCortex.Workspace.SymbolQuery {
    public interface IMatchSortStrategy {
        IOrderedEnumerable<SymbolMatch> Order(IEnumerable<SymbolMatch> matches);
    }

    /// <summary>
    /// Default: purely alphabetical by FQN (stable, matches current RPC contract behavior).
    /// </summary>
    public sealed class FqnAlphaSortStrategy : IMatchSortStrategy {
        public IOrderedEnumerable<SymbolMatch> Order(IEnumerable<SymbolMatch> matches)
            => matches.OrderBy(m => m.Fqn, System.StringComparer.Ordinal);
    }
}

