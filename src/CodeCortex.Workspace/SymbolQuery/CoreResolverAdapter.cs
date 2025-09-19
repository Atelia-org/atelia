using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodeCortex.Core.Index;
using CoreSymbolResolver = CodeCortex.Core.Symbols.SymbolResolver;

namespace CodeCortex.Workspace.SymbolQuery {
    /// <summary>
    /// ISymbolResolver adapter that prefers a lightweight name index (Workspace-built) or Core index-based resolver when available;
    /// falls back to on-demand Workspace enumeration otherwise. Sorting/paging is unified via IMatchSortStrategy.
    /// </summary>
    public sealed class CoreResolverAdapter : ISymbolResolver {
        private readonly ITypeSource _fallbackSource;
        private readonly IMatchSortStrategy _sorter;

        public CoreResolverAdapter(ITypeSource fallbackSource, IMatchSortStrategy? sorter = null) {
            _fallbackSource = fallbackSource;
            _sorter = sorter ?? new FqnAlphaSortStrategy();
        }

        public async Task<PagedMatches> SearchAsync(LoadedSolution loaded, string pattern, int offset, int limit, CancellationToken ct = default) {
            var solPath = loaded.Solution?.FilePath ?? string.Empty;
            var ctxRoot = TryGetContextRoot(loaded) ?? Directory.GetCurrentDirectory();

            // 1) Try Core heavy index if present
            var store = new IndexStore(ctxRoot);
            var index = store.TryLoad(out _);
            if (index != null) {
                var core = new CoreSymbolResolver(index);
                int fetch = Math.Max(limit + offset, 200);
                if (fetch <= 0) { fetch = 200; }
                var coreMatches = core.Search(pattern, fetch);
                var mapped = coreMatches.Select(m => new SymbolMatch(m.Fqn, MapKind(m.MatchKind))).ToArray();
                return Page(mapped, offset, limit);
            }

            // 2) Try lightweight name index
            var nameStore = new NameIndexStore();
            var names = nameStore.TryLoad(solPath);
            if (names == null || names.Fqns.Length == 0) {
                // Build on demand and persist (keeps V2: no cache dependency; cache only optimizes)
                var builder = new NameIndexBuilder();
                names = await builder.BuildAsync(loaded, ct).ConfigureAwait(false);
                _ = nameStore.Save(names);
            }
            var entries = names.Fqns.Select(f => new TypeEntry(f, ExtractSimple(f))).ToArray();
            var matches = SimpleSymbolMatcher.Match(entries, pattern);
            return Page(matches, offset, limit);

            static string ExtractSimple(string fqn) {
                var s = fqn.StartsWith("global::", StringComparison.Ordinal) ? fqn.Substring(8) : fqn;
                var i = s.LastIndexOf('.');
                return i >= 0 ? s[(i + 1)..] : s;
            }
        }

        private PagedMatches Page(System.Collections.Generic.IEnumerable<SymbolMatch> mapped, int offset, int limit) {
            var list = mapped as SymbolMatch[] ?? mapped.ToArray();
            var total = list.Length;
            offset = Math.Clamp(offset, 0, Math.Max(0, total - 1));
            limit = Math.Max(0, limit);
            var page = (limit == 0) ? Array.Empty<SymbolMatch>() : _sorter.Order(list).Skip(offset).Take(limit).ToArray();
            var items = page.Select(m => m.Fqn).ToArray();
            var kinds = page.Select(m => m.Category.ToString()).ToArray();
            return new PagedMatches(items, total, kinds);
        }

        private static string TryGetContextRoot(LoadedSolution loaded) {
            try {
                var solPath = loaded.Solution?.FilePath;
                if (!string.IsNullOrEmpty(solPath)) {
                    var dir = Path.GetDirectoryName(solPath)!;
                    return Path.Combine(dir, ".codecortex");
                }
            }
            catch { }
            return Path.Combine(Directory.GetCurrentDirectory(), ".codecortex");
        }

        private static MatchCategory MapKind(CodeCortex.Core.Symbols.MatchKind k) => k switch {
            CodeCortex.Core.Symbols.MatchKind.Exact => MatchCategory.Exact,
            CodeCortex.Core.Symbols.MatchKind.ExactIgnoreCase => MatchCategory.Exact,
            CodeCortex.Core.Symbols.MatchKind.Suffix => MatchCategory.Suffix,
            CodeCortex.Core.Symbols.MatchKind.Wildcard => MatchCategory.WildcardFqn,
            CodeCortex.Core.Symbols.MatchKind.GenericBase => MatchCategory.WildcardSimple, // temporary bucket; dedicated category later
            CodeCortex.Core.Symbols.MatchKind.Fuzzy => MatchCategory.Contains, // temporary bucket; dedicated category later
            _ => MatchCategory.Contains
        };
    }
}

