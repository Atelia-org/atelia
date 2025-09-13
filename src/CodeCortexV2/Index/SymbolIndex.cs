// using System.Text.RegularExpressions;
// using System.Diagnostics;
// using System.Linq;
// using System.Collections.Immutable;

// using Atelia.Diagnostics;
// using CodeCortexV2.Abstractions;

// namespace CodeCortexV2.Index;

// /// <summary>
// /// Immutable, thread-safe symbol index snapshot containing only precomputed data and query logic.
// /// Deltas are applied functionally via <see cref="WithDelta"/> to produce a new snapshot.
// /// This type does not depend on Roslyn and has no knowledge of workspace lifetime.
// /// </summary>
// public sealed class SymbolIndex : ISymbolIndex {

//     // Immutable snapshot storages
//     private readonly ImmutableDictionary<string, SymbolEntry> _all;
//     private readonly ImmutableDictionary<string, string> _fqnCaseSensitive;
//     private readonly ImmutableDictionary<string, string> _fqnIgnoreCase;
//     private readonly ImmutableDictionary<string, ImmutableHashSet<string>> _byGenericBase;

//     private SymbolIndex(
//         ImmutableDictionary<string, SymbolEntry> all,
//         ImmutableDictionary<string, string> fqnCase,
//         ImmutableDictionary<string, string> fqnICase,
//         ImmutableDictionary<string, ImmutableHashSet<string>> byGenericBase
//     ) {
//         _all = all;
//         _fqnCaseSensitive = fqnCase;
//         _fqnIgnoreCase = fqnICase;
//         _byGenericBase = byGenericBase;
//     }

//     /// <summary>
//     /// An empty snapshot used as the initial value before applying any <see cref="SymbolsDelta"/>.
//     /// </summary>
//     public static readonly SymbolIndex Empty = new(
//         ImmutableDictionary<string, SymbolEntry>.Empty,
//         ImmutableDictionary.Create<string, string>(StringComparer.Ordinal),
//         ImmutableDictionary.Create<string, string>(StringComparer.OrdinalIgnoreCase),
//         ImmutableDictionary.Create<string, ImmutableHashSet<string>>(StringComparer.OrdinalIgnoreCase)
//     );

//     // NOTE: Roslyn-based builders were removed to keep this class independent of IDE/Workspace.
//     // Build immutable snapshots via IndexSynchronizer and apply with WithDelta.

//     /// <summary>
//     /// Execute a layered symbol search over the snapshot.
//     /// Layers (in priority order): Id → Exact → Prefix → Contains → Suffix → Wildcard → GenericBase → Fuzzy.
//     /// Results are ordered by match-kind, then score (ascending), then name (ordinal), and paged.
//     /// </summary>
//     /// <param name="query">FQN, doc-id (T:/N:), simple name or wildcard pattern.</param>
//     /// <param name="limit">Maximum number of items in the returned page.</param>
//     /// <param name="offset">Zero-based offset after ordering.</param>
//     /// <param name="kinds">Filter flags; use <see cref="SymbolKinds.All"/> for no filter.</param>
//     /// <returns>A stable page with total count and next-offset for continuation.</returns>
//     public SearchResults Search(string query, int limit, int offset, SymbolKinds kinds) {
//         if (string.IsNullOrWhiteSpace(query)) {
//             return new SearchResults(Array.Empty<SearchHit>(), 0, 0, limit, 0);
//         }

//         var sw = Stopwatch.StartNew();
//         query = query.Trim();
//         var added = new HashSet<string>(StringComparer.Ordinal);
//         var results = new List<SearchHit>(256);
//         bool hasWildcard = ContainsWildcard(query);
//         bool hasDot = query.Contains('.');
//         DebugUtil.Print("Search", $"query='{query}', kinds={kinds}, limit={limit}, offset={offset}");

//         // 0) DocCommentId fast path (direct id)
//         if ((query.StartsWith("T:", StringComparison.Ordinal) || query.StartsWith("N:", StringComparison.Ordinal))
//             && _all.TryGetValue(query, out var e0)) {
//             if ((kinds & e0.Kind) != 0) {
//                 AddResult(results, added, e0.ToHit(MatchFlags.Id, 0));
//                 var ordered = results.OrderBy(m => (int)m.MatchFlags).ThenBy(m => m.Score).ThenBy(m => m.Name, StringComparer.Ordinal).ToList();
//                 return new SearchResults(ordered, ordered.Count, 0, ordered.Count, null);
//             }
//         }

//         // 1) Exact FQN (case) + quick path without 'global::' prefix
//         var qHasGlobal = query.StartsWith("global::", StringComparison.Ordinal);
//         var qWithGlobal = qHasGlobal ? query : "global::" + query;
//         {
//             string? id1final = null;
//             if (_fqnCaseSensitive.TryGetValue(query, out var tCase1)) {
//                 id1final = tCase1;
//             } else if (!qHasGlobal && _fqnCaseSensitive.TryGetValue(qWithGlobal, out var tCase2)) {
//                 id1final = tCase2;
//             }
//             if (id1final is not null) {
//                 var e = FindEntry(id1final);
//                 if (e != null && (kinds & e.Kind) != 0) {
//                     AddResult(results, added, e.ToHit(MatchFlags.Exact, 0));
//                 }
//             }
//         }
//         // 2) Exact FQN (ignore case) + quick path without 'global::' prefix
//         if (results.Count < limit) {
//             string? id2final = null;
//             if (_fqnIgnoreCase.TryGetValue(query, out var tICase1)) {
//                 id2final = tICase1;
//             } else if (!qHasGlobal && _fqnIgnoreCase.TryGetValue(qWithGlobal, out var tICase2)) {
//                 id2final = tICase2;
//             }
//             if (id2final is not null) {
//                 var e = FindEntry(id2final);
//                 if (e != null && (kinds & e.Kind) != 0) {
//                     AddResult(results, added, e.ToHit(MatchFlags.ExactIgnoreCase, 10));
//                 }
//             }
//         }
//         // 3) Prefix (FQN) when query contains '.'
//         if (hasDot && results.Count < limit) {
//             // 3a) raw FQN（忽略 global::）
//             foreach (var e in _all.Values) {
//                 if ((kinds & e.Kind) == 0) {
//                     continue;
//                 }

//                 var fqnNoGlobal = e.FqnNoGlobal;
//                 if (fqnNoGlobal.StartsWith(query, StringComparison.OrdinalIgnoreCase)) {
//                     AddResult(results, added, e.ToHit(MatchFlags.Prefix, fqnNoGlobal.Length - query.Length));
//                 }
//             }
//             // 3b) base FQN（去泛型）
//             if (results.Count < limit) {
//                 foreach (var e in _all.Values) {
//                     if ((kinds & e.Kind) == 0) {
//                         continue;
//                     }

//                     var fqnBase = e.FqnBase;
//                     if (fqnBase.StartsWith(query, StringComparison.OrdinalIgnoreCase)) {
//                         AddResult(results, added, e.ToHit(MatchFlags.Prefix, (fqnBase.Length - query.Length) + 5));
//                     }
//                 }
//             }
//         }
//         // 4) Contains (FQN) when query contains '.'
//         if (hasDot && results.Count < limit) {
//             // 4a) raw FQN（忽略 global::）
//             foreach (var e in _all.Values) {
//                 if ((kinds & e.Kind) == 0) {
//                     continue;
//                 }

//                 var fqnNoGlobal = e.FqnNoGlobal;
//                 if (fqnNoGlobal.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) {
//                     AddResult(results, added, e.ToHit(MatchFlags.Contains, fqnNoGlobal.Length));
//                 }
//             }
//             // 4b) base FQN（去泛型）
//             if (results.Count < limit) {
//                 foreach (var e in _all.Values) {
//                     if ((kinds & e.Kind) == 0) {
//                         continue;
//                     }

//                     var fqnBase = e.FqnBase;
//                     if (fqnBase.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) {
//                         AddResult(results, added, e.ToHit(MatchFlags.Contains, fqnBase.Length + 5));
//                     }
//                 }
//             }
//         }

//         // 5) Suffix
//         HashSet<string>? suffixIdsForAmbiguity = null;
//         int suffixTotalMatches = 0;
//         if (results.Count < limit) {
//             // Only needed for simple-name queries (no dot) for ambiguity marking later
//             bool needAmbiguity = !hasDot;
//             foreach (var e in _all.Values) {
//                 if ((kinds & e.Kind) == 0) {
//                     continue;
//                 }

//                 var fqnNoGlobal = e.FqnNoGlobal;
//                 if (fqnNoGlobal.EndsWith(query, StringComparison.OrdinalIgnoreCase)) {
//                     suffixTotalMatches++;
//                     AddResult(results, added, e.ToHit(MatchFlags.Suffix, fqnNoGlobal.Length - query.Length));
//                     if (needAmbiguity) {
//                         suffixIdsForAmbiguity ??= new HashSet<string>(StringComparer.Ordinal);
//                         suffixIdsForAmbiguity.Add(e.SymbolId);
//                     }
//                 }
//             }
//         }
//         // 6) Wildcard
//         if (hasWildcard && results.Count < limit) {
//             var rx = WildcardToRegex(query);
//             foreach (var e in _all.Values) {
//                 if ((kinds & e.Kind) == 0) {
//                     continue;
//                 }

//                 if (rx.IsMatch(e.Fqn)) {
//                     AddResult(results, added, e.ToHit(MatchFlags.Wildcard, e.Fqn.Length));
//                 }
//             }
//         }
//         // 7) GenericBase (higher than fuzzy)
//         if (results.Count < limit) {
//             var simple = ExtractSimpleName(query);
//             var baseName = ExtractGenericBase(simple);
//             if (!string.IsNullOrEmpty(baseName) && _byGenericBase.TryGetValue(baseName, out var ids)) {
//                 foreach (var id in ids) {
//                     var e = FindEntry(id);
//                     if (e != null && (kinds & e.Kind) != 0) {
//                         AddResult(results, added, e.ToHit(MatchFlags.GenericBase, 50));
//                     }
//                 }
//             }
//         }
//         // 8) Fuzzy (simple name only) - only when no other matches found
//         if (!hasWildcard && results.Count == 0) {
//             int threshold = ComputeFuzzyThreshold(query);
//             foreach (var e in _all.Values) {
//                 if ((kinds & e.Kind) == 0) {
//                     continue;
//                 }
//                 if (!added.Contains(e.SymbolId) && Math.Abs(e.Simple.Length - query.Length) <= threshold) {
//                     int dist = BoundedLevenshtein(e.Simple, query, threshold);
//                     if (dist >= 0 && dist <= threshold) {
//                         AddResult(results, added, e.ToHit(MatchFlags.Fuzzy, 100 + dist));
//                     }
//                 }
//             }
//         }

//         // Ambiguity marking for suffix simple-name queries
//         if (!hasDot && suffixIdsForAmbiguity is { Count: > 1 } && suffixTotalMatches > 1) {
//             var suffixIds = suffixIdsForAmbiguity;
//             for (int i = 0; i < results.Count; i++) {
//                 var r = results[i];
//                 if (r.MatchFlags == MatchFlags.Suffix && suffixIds.Contains(r.SymbolId.Value)) {
//                     results[i] = r with { IsAmbiguous = true };
//                 }
//             }
//         }
//         // Kind filter (flags)
//         if (kinds != SymbolKinds.All && kinds != SymbolKinds.None) {
//             results = results.Where(r => (kinds & r.Kind) != 0).ToList();
//         }
//         // Order and paginate
//         var orderedAll = results
//             .OrderBy(m => (int)m.MatchFlags)
//             .ThenBy(m => m.Score)
//             .ThenBy(m => m.Name, StringComparer.Ordinal)
//             .ToList();
//         var total = orderedAll.Count;
//         var off = Math.Max(0, offset);
//         var lim = Math.Max(0, limit);
//         var page = orderedAll.Skip(off).Take(lim).ToList();
//         int? nextOff = off + lim < total ? off + lim : null;
//         DebugUtil.Print("Search", $"done: total={total}, page={page.Count}, off={off}, lim={lim}, elapsed={sw.ElapsedMilliseconds}ms");
//         return new SearchResults(page, total, off, lim, nextOff);
//     }

//     private SymbolEntry? FindEntry(string id) => _all.TryGetValue(id, out var e) ? e : null;

//     /// <summary>
//     /// Apply a <see cref="SymbolsDelta"/> to this snapshot and return a new immutable snapshot.
//     /// Removals are processed before adds to ensure upsert semantics and correct reindexing.
//     /// </summary>
//     /// <param name="delta">Adds/removals for types and namespaces.</param>
//     /// <returns>New snapshot; the current instance is not modified.</returns>
//     public SymbolIndex WithDelta(SymbolsDelta delta) {
//         if (delta is null) {
//             return this;
//         }

//         var allB = _all.ToBuilder();
//         var fqnCaseB = _fqnCaseSensitive.ToBuilder();
//         var fqnICaseB = _fqnIgnoreCase.ToBuilder();
//         var byBaseB = _byGenericBase.ToBuilder();

//         // local helpers
//         void RemoveEntry(string docId) {
//             if (!allB.TryGetValue(docId, out var old)) {
//                 return;
//             }

//             allB.Remove(docId);
//             if (!string.IsNullOrEmpty(old.Fqn)) {
//                 fqnCaseB.Remove(old.Fqn);
//                 fqnICaseB.Remove(old.Fqn);
//             }
//             if (!string.IsNullOrEmpty(old.GenericBase)) {
//                 if (byBaseB.TryGetValue(old.GenericBase, out var set)) {
//                     var newSet = set.Remove(docId);
//                     if (newSet.IsEmpty) {
//                         byBaseB.Remove(old.GenericBase);
//                     } else {
//                         byBaseB[old.GenericBase] = newSet;
//                     }
//                 }
//             }
//         }

//         void AddOrUpdate(SymbolEntry entry) {
//             if (string.IsNullOrEmpty(entry.SymbolId)) {
//                 return;
//             }

//             var docId = entry.SymbolId;
//             // update: remove old first
//             if (allB.ContainsKey(docId)) {
//                 RemoveEntry(docId);
//             }

//             allB[docId] = entry;
//             // FQN maps
//             if (!string.IsNullOrEmpty(entry.Fqn)) {
//                 fqnCaseB[entry.Fqn] = docId;
//                 fqnICaseB[entry.Fqn] = docId; // OrdinalIgnoreCase dictionary in Empty ensures case-insensitive semantics
//             }
//             // Generic base index
//             if (!string.IsNullOrEmpty(entry.GenericBase)) {
//                 if (!byBaseB.TryGetValue(entry.GenericBase, out var set)) {
//                     set = ImmutableHashSet.Create<string>(StringComparer.Ordinal);
//                 }
//                 byBaseB[entry.GenericBase] = set.Add(docId);
//             }
//         }

//         // Apply removals first
//         if (delta.TypeRemovals is { Count: > 0 }) {
//             foreach (var id in delta.TypeRemovals) {
//                 RemoveEntry(id);
//             }
//         }
//         if (delta.NamespaceRemovals is { Count: > 0 }) {
//             foreach (var id in delta.NamespaceRemovals) {
//                 RemoveEntry(id);
//             }
//         }

//         // Then adds/updates
//         if (delta.TypeAdds is { Count: > 0 }) {
//             foreach (var e in delta.TypeAdds) {
//                 AddOrUpdate(e);
//             }
//         }
//         if (delta.NamespaceAdds is { Count: > 0 }) {
//             foreach (var e in delta.NamespaceAdds) {
//                 AddOrUpdate(e);
//             }
//         }

//         return new SymbolIndex(
//             allB.ToImmutable(),
//             fqnCaseB.ToImmutable(),
//             fqnICaseB.ToImmutable(),
//             byBaseB.ToImmutable()
//         );
//     }

//     private static int ComputeFuzzyThreshold(string q) => q.Length > 12 ? 2 : 1;

//     private static bool ContainsWildcard(string q) => q.Contains('*') || q.Contains('?');

//     private static Regex WildcardToRegex(string pattern) {
//         var escaped = Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal).Replace("\\?", ".", StringComparison.Ordinal);
//         return new Regex("^" + escaped + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
//     }

//     /// <summary>Bounded Levenshtein distance; returns -1 if exceeds limit.</summary>
//     private static int BoundedLevenshtein(string a, string b, int limit) {
//         if (a == b) {
//             return 0;
//         }

//         int m = a.Length, n = b.Length;
//         if (Math.Abs(m - n) > limit) {
//             return -1;
//         }

//         if (limit == 0) {
//             return -1;
//         }

//         var dp = new int[m + 1, n + 1];
//         for (int i = 0; i <= m; i++) {
//             dp[i, 0] = i;
//         }

//         for (int j = 0; j <= n; j++) {
//             dp[0, j] = j;
//         }

//         for (int i = 1; i <= m; i++) {
//             int rowBest = int.MaxValue;
//             for (int j = 1; j <= n; j++) {
//                 int cost = a[i - 1] == b[j - 1] ? 0 : 1;
//                 int val = Math.Min(Math.Min(dp[i - 1, j] + 1, dp[i, j - 1] + 1), dp[i - 1, j - 1] + cost);
//                 dp[i, j] = val;
//                 if (val < rowBest) {
//                     rowBest = val;
//                 }
//             }

//             if (rowBest > limit) {
//                 return -1;
//             }
//         }
//         var d = dp[m, n];
//         return d > limit ? -1 : d;
//     }

//     private static string ExtractSimpleName(string fqn) {
//         var i = fqn.LastIndexOf('.');
//         return i >= 0 ? fqn[(i + 1)..] : fqn;
//     }

//     private static string ExtractGenericBase(string name) => IndexStringUtil.ExtractGenericBase(name);
//     private static string StripGlobal(string fqn) => IndexStringUtil.StripGlobal(fqn);
//     private static string NormalizeFqnBase(string fqn) => IndexStringUtil.NormalizeFqnBase(fqn);


//     private static void AddResult(List<SearchHit> list, HashSet<string> added, SearchHit hit) {
//         if (added.Add(hit.SymbolId.Value)) {
//             list.Add(hit);
//         }
//     }
// }

