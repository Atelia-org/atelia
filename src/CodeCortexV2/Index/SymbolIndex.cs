using System.Text.RegularExpressions;
using CodeCortexV2.Abstractions;
using Microsoft.CodeAnalysis;

namespace CodeCortexV2.Index;

public sealed class SymbolIndex : ISymbolIndex {
    private readonly Solution _solution;
    private readonly List<Compilation> _compilations = new();

    private readonly List<Entry> _all = new();
    private readonly Dictionary<string, string> _fqnCaseSensitive = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _fqnIgnoreCase = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _byGenericBase = new(StringComparer.OrdinalIgnoreCase);

    private SymbolIndex(Solution solution) {
        _solution = solution;
    }

    public static async Task<SymbolIndex> BuildAsync(Solution solution, CancellationToken ct) {
        var idx = new SymbolIndex(solution);
        foreach (var project in solution.Projects) {
            ct.ThrowIfCancellationRequested();
            var comp = await project.GetCompilationAsync(ct).ConfigureAwait(false);
            if (comp is null) {
                continue;
            }

            idx._compilations.Add(comp);
            idx.EnumerateTypes(comp, ct);
        }
        return idx;
    }

    private void EnumerateTypes(Compilation compilation, CancellationToken ct) {
        void WalkNamespace(INamespaceSymbol ns) {
            foreach (var t in ns.GetTypeMembers()) {
                WalkType(t);
            }
            foreach (var sub in ns.GetNamespaceMembers()) {
                ct.ThrowIfCancellationRequested();
                WalkNamespace(sub);
            }
        }
        void WalkType(INamedTypeSymbol t) {
            AddType(t);
            foreach (var nt in t.GetTypeMembers()) {
                ct.ThrowIfCancellationRequested();
                WalkType(nt);
            }
        }
        WalkNamespace(compilation.Assembly.GlobalNamespace);
    }

    private void AddType(INamedTypeSymbol t) {
        var fqn = t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var simple = t.Name;
        var kind = t.TypeKind.ToString();
        var asm = t.ContainingAssembly?.Name ?? string.Empty;
        var docId = Microsoft.CodeAnalysis.DocumentationCommentId.CreateDeclarationId(t) ?? $"T:{fqn}";
        var entry = new Entry(docId, fqn, simple, kind, asm, ExtractGenericBase(simple));
        _all.Add(entry);
        if (!_fqnCaseSensitive.ContainsKey(fqn)) {
            _fqnCaseSensitive[fqn] = docId;
        }

        if (!_fqnIgnoreCase.ContainsKey(fqn)) {
            _fqnIgnoreCase[fqn] = docId;
        }

        if (!string.IsNullOrEmpty(entry.GenericBase)) {
            if (!_byGenericBase.TryGetValue(entry.GenericBase, out var set)) {
                set = new HashSet<string>(StringComparer.Ordinal);
                _byGenericBase[entry.GenericBase] = set;
            }
            set.Add(docId);
        }
    }

    public async Task<SearchResults> SearchAsync(string query, CodeCortexV2.Abstractions.SymbolKind? kindFilter, int limit, int offset, CancellationToken ct) {
        if (string.IsNullOrWhiteSpace(query)) {
            return new SearchResults(Array.Empty<SearchHit>(), 0, 0, limit, 0);
        }

        query = query.Trim();
        var added = new HashSet<string>(StringComparer.Ordinal);
        var results = new List<SearchHit>(256);
        bool hasWildcard = ContainsWildcard(query);

        // 0) Try treat as SymbolKey (direct id)
        if (TryResolveSymbolId(query, out var idSym)) {
            if (idSym is INamedTypeSymbol nts) {
                var disp = nts.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", string.Empty);
                var docId = Microsoft.CodeAnalysis.DocumentationCommentId.CreateDeclarationId(nts) ?? disp;
                AddResult(results, added, new SearchHit(disp, ToKind(nts), nts.ContainingNamespace?.ToDisplayString(), nts.ContainingAssembly?.Name, new SymbolId(docId), MatchKind.Id, IsAmbiguous: false, Score: 0), limit);
                var orderedId = results.OrderBy(m => (int)m.MatchKind).ThenBy(m => m.Score).ThenBy(m => m.Name, StringComparer.Ordinal).ToList();
                return new SearchResults(orderedId, orderedId.Count, 0, orderedId.Count, null);
            }
        }

        // 1) Exact FQN (case)
        if (_fqnCaseSensitive.TryGetValue(query, out var id1)) {
            var e = FindEntry(id1);
            if (e != null) {
                AddResult(results, added, e.ToHit(MatchKind.Exact, 0), limit);
            }
        }
        // 2) Exact FQN (ignore case)
        if (results.Count < limit && _fqnIgnoreCase.TryGetValue(query, out var id2)) {
            var e = FindEntry(id2);
            if (e != null) {
                AddResult(results, added, e.ToHit(MatchKind.ExactIgnoreCase, 10), limit);
            }
        }
        // 3) Suffix
        List<Entry>? allSuffix = null;
        if (results.Count < limit) {
            allSuffix = _all.Where(a => a.Fqn.EndsWith(query, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var e in allSuffix) {
                AddResult(results, added, e.ToHit(MatchKind.Suffix, e.Fqn.Length - query.Length), limit);
            }
        }
        // 4) Wildcard
        if (hasWildcard && results.Count < limit) {
            var rx = WildcardToRegex(query);
            foreach (var e in _all) {
                if (rx.IsMatch(e.Fqn)) {
                    AddResult(results, added, e.ToHit(MatchKind.Wildcard, e.Fqn.Length), limit);
                }
            }
        }
        // 5) GenericBase (higher than fuzzy)
        if (results.Count < limit) {
            var simple = ExtractSimpleName(query);
            var baseName = ExtractGenericBase(simple);
            if (!string.IsNullOrEmpty(baseName) && _byGenericBase.TryGetValue(baseName, out var ids)) {
                foreach (var id in ids) {
                    var e = FindEntry(id);
                    if (e != null) {
                        AddResult(results, added, e.ToHit(MatchKind.GenericBase, 50), limit);
                    }
                }
            }
        }
        // 6) Fuzzy (simple name only)
        if (!hasWildcard && results.Count < limit) {
            int threshold = ComputeFuzzyThreshold(query);
            foreach (var e in _all) {
                if (!added.Contains(e.SymbolId) && Math.Abs(e.Simple.Length - query.Length) <= threshold) {
                    int dist = BoundedLevenshtein(e.Simple, query, threshold);
                    if (dist >= 0 && dist <= threshold) {
                        AddResult(results, added, e.ToHit(MatchKind.Fuzzy, 100 + dist), limit);
                    }
                }
            }
        }

        // Ambiguity marking for suffix simple-name queries
        if (!query.Contains('.') && allSuffix is { Count: > 1 }) {
            var suffixIds = allSuffix.Select(s => s.SymbolId).ToHashSet(StringComparer.Ordinal);
            for (int i = 0; i < results.Count; i++) {
                var r = results[i];
                if (r.MatchKind == MatchKind.Suffix && suffixIds.Contains(r.SymbolId.Value)) {
                    results[i] = r with { IsAmbiguous = true };
                }
            }
        }
        // Order and paginate
        var orderedAll = results
            .OrderBy(m => (int)m.MatchKind)
            .ThenBy(m => m.Score)
            .ThenBy(m => m.Name, StringComparer.Ordinal)
            .ToList();
        var total = orderedAll.Count;
        var off = Math.Max(0, offset);
        var lim = Math.Max(0, limit);
        var page = orderedAll.Skip(off).Take(lim).ToList();
        int? nextOff = off + lim < total ? off + lim : null;
        return new SearchResults(page, total, off, lim, nextOff);
    }

    public async Task<SymbolId?> ResolveAsync(string identifierOrName, CancellationToken ct) {
        var page = await SearchAsync(identifierOrName, null, limit: 2, offset: 0, ct).ConfigureAwait(false);
        if (page.Total == 1) {
            return page.Items[0].SymbolId;
        }

        return null;
    }

    private Entry? FindEntry(string id) => _all.FirstOrDefault(a => a.SymbolId == id);

    private static int ComputeFuzzyThreshold(string q) => q.Length > 12 ? 2 : 1;

    private static bool ContainsWildcard(string q) => q.Contains('*') || q.Contains('?');

    private static Regex WildcardToRegex(string pattern) {
        var escaped = Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal).Replace("\\?", ".", StringComparison.Ordinal);
        return new Regex("^" + escaped + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }

    /// <summary>Bounded Levenshtein distance; returns -1 if exceeds limit.</summary>
    private static int BoundedLevenshtein(string a, string b, int limit) {
        if (a == b) {
            return 0;
        }

        int m = a.Length, n = b.Length;
        if (Math.Abs(m - n) > limit) {
            return -1;
        }

        if (limit == 0) {
            return -1;
        }

        var dp = new int[m + 1, n + 1];
        for (int i = 0; i <= m; i++) {
            dp[i, 0] = i;
        }

        for (int j = 0; j <= n; j++) {
            dp[0, j] = j;
        }

        for (int i = 1; i <= m; i++) {
            int rowBest = int.MaxValue;
            for (int j = 1; j <= n; j++) {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                int val = Math.Min(Math.Min(dp[i - 1, j] + 1, dp[i, j - 1] + 1), dp[i - 1, j - 1] + cost);
                dp[i, j] = val;
                if (val < rowBest) {
                    rowBest = val;
                }
            }
            if (rowBest > limit) {
                return -1;
            }
        }
        var d = dp[m, n];
        return d > limit ? -1 : d;
    }

    private static string ExtractSimpleName(string fqn) {
        var i = fqn.LastIndexOf('.');
        return i >= 0 ? fqn[(i + 1)..] : fqn;
    }

    private static string ExtractGenericBase(string name) {
        if (string.IsNullOrEmpty(name)) {
            return name;
        }

        var tick = name.IndexOf('`');
        if (tick >= 0) {
            return name.Substring(0, tick);
        }

        var lt = name.IndexOf('<');
        if (lt >= 0) {
            return name.Substring(0, lt);
        }

        return name;
    }

    private static CodeCortexV2.Abstractions.SymbolKind ToKind(ISymbol s) => s switch {
        INamespaceSymbol => Abstractions.SymbolKind.Namespace,
        INamedTypeSymbol => Abstractions.SymbolKind.Type,
        IMethodSymbol => Abstractions.SymbolKind.Method,
        IPropertySymbol => Abstractions.SymbolKind.Property,
        IFieldSymbol => Abstractions.SymbolKind.Field,
        IEventSymbol => Abstractions.SymbolKind.Event,
        _ => Abstractions.SymbolKind.Unknown
    };

    private bool TryResolveSymbolId(string id, out ISymbol symbol) {
        symbol = null!;
        if (string.IsNullOrEmpty(id)) {
            return false;
        }
        // Support documentation comment id for types: "T:Namespace.Type`1" (nested uses '+')
        if (id.StartsWith("T:", StringComparison.Ordinal)) {
            var meta = id.Substring(2);
            foreach (var comp in _compilations) {
                var t = comp.GetTypeByMetadataName(meta);
                if (t is not null) {
                    symbol = t;
                    return true;
                }
            }
        }
        return false;
    }

    private static void AddResult(List<SearchHit> list, HashSet<string> added, SearchHit hit, int limit) {
        if (added.Add(hit.SymbolId.Value)) {
            list.Add(hit);
        }
    }

    private sealed record Entry(string SymbolId, string Fqn, string Simple, string Kind, string Assembly, string GenericBase) {
        public SearchHit ToHit(MatchKind matchKind, int score) => new(
            Name: Fqn.Replace("global::", string.Empty),
            Kind: Abstractions.SymbolKind.Type,
            Namespace: null,
            Assembly: Assembly,
            SymbolId: new SymbolId(SymbolId),
            MatchKind: matchKind,
            IsAmbiguous: false,
            Score: score
        );
    }
}

