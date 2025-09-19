using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using CodeCortex.Core.Index;

namespace CodeCortex.Core.Symbols;

// Phase1: suppress XML doc warnings for fast iteration
#pragma warning disable 1591
public enum MatchKind { Exact = 0, ExactIgnoreCase = 1, Suffix = 2, Wildcard = 3, GenericBase = 4, Fuzzy = 5 }

public sealed record SymbolMatch(string Id, string Fqn, string Kind, MatchKind MatchKind, int RankScore, int? Distance, bool IsAmbiguous = false);

public interface ISymbolResolver {
    IReadOnlyList<SymbolMatch> Resolve(string query, int limit = 20);
    IReadOnlyList<SymbolMatch> Search(string query, int limit = 50);
}

/// <summary>
/// Basic symbol resolver supporting exact / suffix / wildcard / fuzzy.
/// </summary>
public sealed class SymbolResolver : ISymbolResolver {
    private readonly CodeCortexIndex _index;
    private readonly Dictionary<string, string> _fqnIgnoreCase;
    private readonly List<(string Fqn, string Id, string Kind, string Simple)> _all;
    private readonly HashSet<string> _simpleNames;
    private readonly Dictionary<string, TypeEntry> _byId;

    public SymbolResolver(CodeCortexIndex index) {
        _index = index;
        _fqnIgnoreCase = index.Maps.FqnIndex
            .GroupBy(k => k.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Value, StringComparer.OrdinalIgnoreCase);
        _all = index.Types.Select(t => (t.Fqn, t.Id, t.Kind, Simple: ExtractSimpleName(t.Fqn))).ToList();
        _simpleNames = _all.Select(a => a.Simple).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 处理重复ID的情况：使用第一个遇到的
        _byId = new Dictionary<string, TypeEntry>(StringComparer.Ordinal);
        foreach (var type in index.Types) {
            if (!_byId.ContainsKey(type.Id)) {
                _byId[type.Id] = type;
            }
        }
    }
#pragma warning restore 1591

    private static string ExtractSimpleName(string fqn) {
        var i = fqn.LastIndexOf('.');
        return i >= 0 ? fqn[(i + 1)..] : fqn;
    }

#pragma warning disable 1591
    public IReadOnlyList<SymbolMatch> Resolve(string query, int limit = 20) {
        if (string.IsNullOrWhiteSpace(query)) { return Array.Empty<SymbolMatch>(); }
        query = query.Trim();
        var results = new List<SymbolMatch>();
        var addedIds = new HashSet<string>();
        string? id;
        bool hasWildcard = ContainsWildcard(query);

        // 1. Exact (case sensitive)
        if (_index.Maps.FqnIndex.TryGetValue(query, out id)) {
            var t = _byId[id];
            Add(results, addedIds, new SymbolMatch(t.Id, t.Fqn, t.Kind, MatchKind.Exact, 0, null));
        }
        // 2. Exact ignore case
        if (results.Count == 0 && _fqnIgnoreCase.TryGetValue(query, out id)) {
            var t = _byId[id];
            Add(results, addedIds, new SymbolMatch(t.Id, t.Fqn, t.Kind, MatchKind.ExactIgnoreCase, 10, null));
        }

        // 3. Suffix (collect full list for ambiguity before truncation)
        List<SymbolMatch>? allSuffix = null;
        if (results.Count < limit) {
            allSuffix = _all.Where(a => a.Fqn.EndsWith(query, StringComparison.OrdinalIgnoreCase))
                .Select(a => new SymbolMatch(a.Id, a.Fqn, a.Kind, MatchKind.Suffix, a.Fqn.Length - query.Length, null))
                .ToList();
            foreach (var m in allSuffix) {
                if (Add(results, addedIds, m) && results.Count >= limit) { break; }
            }
        }

        // 4. Wildcard
        if (hasWildcard && results.Count < limit) {
            var regex = WildcardToRegex(query);
            var wc = _all.Where(a => regex.IsMatch(a.Fqn))
                .Select(a => new SymbolMatch(a.Id, a.Fqn, a.Kind, MatchKind.Wildcard, a.Fqn.Length, null));
            foreach (var m in wc) {
                if (Add(results, addedIds, m) && results.Count >= limit) { break; }
            }
        }

        // 5. Fuzzy (only when no wildcard and still need more)
        if (!hasWildcard && results.Count < limit) {
            int threshold = ComputeFuzzyThreshold(query);
            foreach (var a in _all) {
                if (addedIds.Contains(a.Id)) { continue; }
                var simple = a.Simple;
                if (Math.Abs(simple.Length - query.Length) > threshold) { continue; }
                int dist = BoundedLevenshtein(simple, query, threshold);
                if (dist >= 0 && dist <= threshold) {
                    Add(results, addedIds, new SymbolMatch(a.Id, a.Fqn, a.Kind, MatchKind.Fuzzy, 100 + dist, dist));
                    if (results.Count >= limit) { break; }
                }
            }
        }

        // 6. Generic base name matching (now ordered before fuzzy in final sort via enum value)
        // Normalize query to generic base: take simple name, then strip `<...>` or ``n suffix
        if (results.Count < limit) {
            var simple = ExtractSimpleName(query);
            var baseName = ExtractGenericBase(simple);
            if (!string.IsNullOrEmpty(baseName) && _index.Maps.GenericBaseNameIndex.TryGetValue(baseName, out var genericIds)) {
                foreach (var gId in genericIds) {
                    if (addedIds.Contains(gId)) { continue; }
                    var t = _byId[gId];
                    Add(results, addedIds, new SymbolMatch(t.Id, t.Fqn, t.Kind, MatchKind.GenericBase, 50, null));
                    if (results.Count >= limit) { break; }
                }
            }
        }

        // Ambiguous marking (only for suffix group when query is simple name and we had >1 suffix total)
        if (!query.Contains('.') && allSuffix != null && allSuffix.Count > 1) {
            var suffixIds = allSuffix.Select(s => s.Id).ToHashSet();
            for (int i = 0; i < results.Count; i++) {
                var r = results[i];
                if (r.MatchKind == MatchKind.Suffix && suffixIds.Contains(r.Id)) {
                    results[i] = r with { IsAmbiguous = true };
                }
            }
        }

        var ordered = results
            .OrderBy(m => (int)m.MatchKind)
            .ThenBy(m => m.RankScore)
            .ThenBy(m => m.Fqn, StringComparer.Ordinal)
            .Take(limit)
            .ToList();
        return ordered;
    }

    public IReadOnlyList<SymbolMatch> Search(string query, int limit = 50) => Resolve(query, limit);
#pragma warning restore 1591

    internal static int ComputeFuzzyThreshold(string query) => query.Length > 12 ? 2 : 1;

    private static bool Add(List<SymbolMatch> list, HashSet<string> ids, SymbolMatch match) {
        if (ids.Add(match.Id)) {
            list.Add(match);
            return true;
        }
        return false;
    }

    private static string ExtractGenericBase(string name) {
        if (string.IsNullOrEmpty(name)) { return name; }
        var tick = name.IndexOf('`');
        if (tick >= 0) { return name.Substring(0, tick); }
        var lt = name.IndexOf('<');
        if (lt >= 0) { return name.Substring(0, lt); }
        return name;
    }

    private static bool ContainsWildcard(string q) => q.Contains('*') || q.Contains('?');

    private static Regex WildcardToRegex(string pattern) {
        var escaped = Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal).Replace("\\?", ".", StringComparison.Ordinal);
        return new Regex("^" + escaped + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }

    /// <summary>Bounded Levenshtein distance; returns -1 if exceeds limit.</summary>
    public static int BoundedLevenshtein(string a, string b, int limit) {
        if (a == b) { return 0; }
        int m = a.Length, n = b.Length;
        if (Math.Abs(m - n) > limit) { return -1; }
        if (limit == 0) { return -1; }
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
            // Optional pruning: if minimum so far in row already exceeds limit and (m-i) cannot reduce below limit, early exit
            if (rowBest > limit) { return -1; }
        }
        var d = dp[m, n];
        return d > limit ? -1 : d;
    }
}
