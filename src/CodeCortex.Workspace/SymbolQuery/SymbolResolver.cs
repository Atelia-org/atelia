using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CodeCortex.Workspace.SymbolQuery {
    public sealed record PagedMatches(string[] Items, int Total, string[] MatchKinds);

    public interface ISymbolResolver {
        Task<PagedMatches> SearchAsync(LoadedSolution loaded, string pattern, int offset, int limit, CancellationToken ct = default);
    }

    public sealed class DefaultSymbolResolver : ISymbolResolver {
        private readonly ITypeSource _source;
        private readonly IMatchSortStrategy _sorter;
        public DefaultSymbolResolver(ITypeSource source, IMatchSortStrategy? sorter = null) {
            _source = source;
            _sorter = sorter ?? new FqnAlphaSortStrategy();
        }

        public async Task<PagedMatches> SearchAsync(LoadedSolution loaded, string pattern, int offset, int limit, CancellationToken ct = default) {
            var entries = await _source.ListAsync(loaded, ct).ConfigureAwait(false);
            var matches = SimpleSymbolMatcher.Match(entries, pattern);

            var total = matches.Count;
            offset = Math.Clamp(offset, 0, Math.Max(0, total - 1));
            limit = Math.Max(0, limit);
            var page = (limit == 0)
                ? Array.Empty<SymbolMatch>()
                : _sorter.Order(matches).Skip(offset).Take(limit).ToArray();
            var items = page.Select(m => m.Fqn).ToArray();
            var kinds = page.Select(m => m.Category.ToString()).ToArray();
            return new PagedMatches(items, total, kinds);
        }
    }
}

