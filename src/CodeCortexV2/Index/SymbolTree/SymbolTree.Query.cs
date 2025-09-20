using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.InteropServices;

using Atelia.Diagnostics;
using CodeCortexV2.Abstractions;

namespace CodeCortexV2.Index.SymbolTreeInternal;

partial class SymbolTreeB {
    private IEnumerable<AliasRelation> CandidatesExact(string last) {
        if (_exactAliasToNodes.TryGetValue(last, out var rels)) { return rels; }
        return Array.Empty<AliasRelation>();
    }
    private IEnumerable<AliasRelation> CandidatesNonExact(string last) {
        if (_nonExactAliasToNodes.TryGetValue(last, out var rels)) { return rels; }
        return Array.Empty<AliasRelation>();
    }

    private void CollectEntriesAtNode(int nodeIdx, List<SearchHit> acc, MatchFlags kind, SymbolKinds filter) {
        var entry = _nodes[nodeIdx].Entry;
        if (entry is null) { return; }
        if ((filter & entry.Kind) == 0) { return; }
        var score = ComputeScore(kind);
        acc.Add(entry.ToHit(kind, score));
    }

    // ---- Simple scoring model (lower is better) ----
    // Each fallback bit contributes a positive penalty. Exact match has score 0.
    private const int ScoreIgnoreGenericArity = 10;
    private const int ScoreIgnoreCase = 20;
    private const int ScorePartial = 40;
    private const int ScoreWildcard = 60;
    private const int ScoreFuzzy = 80;

    // Only these bits participate in scoring; future bits won't affect score unless added here.
    private const int RelevantFlagsMask =
        (int)(MatchFlags.IgnoreGenericArity | MatchFlags.IgnoreCase | MatchFlags.Partial | MatchFlags.Wildcard | MatchFlags.Fuzzy);

    // Precomputed score lookup for 5 scoring bits (32 entries). Index is (int)flags & RelevantFlagsMask.
    private static readonly int[] ScoreLut = BuildScoreLut();

    private static int ComputeScore(MatchFlags flags)
        => ScoreLut[((int)flags) & RelevantFlagsMask];

    private static int[] BuildScoreLut() {
        var lut = new int[32];
        for (int i = 0; i < lut.Length; i++) {
            int s = 0;
            if ((i & (int)MatchFlags.IgnoreGenericArity) != 0) { s += ScoreIgnoreGenericArity; }
            if ((i & (int)MatchFlags.IgnoreCase) != 0) { s += ScoreIgnoreCase; }
            if ((i & (int)MatchFlags.Partial) != 0) { s += ScorePartial; }
            if ((i & (int)MatchFlags.Wildcard) != 0) { s += ScoreWildcard; }
            if ((i & (int)MatchFlags.Fuzzy) != 0) { s += ScoreFuzzy; }
            lut[i] = s;
        }
        return lut;
    }

    /// <summary>
    /// Execute search with per-segment exact-first fallback:
    /// - For each segment, try exact candidates; if empty, include non-exact alias candidates for that segment only.
    /// - Then prune right-to-left by matching parent relation.
    /// </summary>
    public SearchResults Search(string query, int limit, int offset, SymbolKinds kinds) {
        var effLimit = Math.Max(0, limit);
        var effOffset = Math.Max(0, offset);
        if (string.IsNullOrWhiteSpace(query)) { return new SearchResults(Array.Empty<SearchHit>(), 0, effOffset, effLimit, null); }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        query = query.Trim();
        DebugUtil.Print("SymbolTreeB.Search", $"q='{query}', kinds={kinds}, limit={effLimit}, offset={effOffset}");

        // Centralized query preprocessing
        var qi = QueryPreprocessor.Preprocess(query);
        if (!string.IsNullOrEmpty(qi.RejectionReason)) { return new SearchResults(Array.Empty<SearchHit>(), 0, effOffset, effLimit, null); }
        bool rootConstraint = qi.IsRootAnchored;
        var segs = qi.NormalizedSegments;
        if (segs.Length == 0) { return new SearchResults(Array.Empty<SearchHit>(), 0, effOffset, effLimit, null); }
        var filterKinds = kinds | qi.DocIdKind;

        SearchResults RunUnified() {
            int segCount = segs.Length;

            // Lazy build: start from the last segment only
            int lastIdx = segCount - 1;
            var lastMap = BuildSegmentCandidates(
                segNormalized: qi.NormalizedSegments[lastIdx],
                segNormalizedLowered: qi.LowerNormalizedSegments[lastIdx]
            );
            if (lastMap.Count == 0) { return new SearchResults(Array.Empty<SearchHit>(), 0, effOffset, effLimit, null); }
            DebugUtil.Print("SymbolTreeB.Lazy", $"lastMap candidates={lastMap.Count}");

            // Single-segment root-anchored early filter: keep only namespaces whose parent is directly under root (root.Parent == -1)
            if (rootConstraint && segCount == 1 && lastMap.Count > 0) {
                var keys = lastMap.Keys.ToArray();
                int removed = 0;
                foreach (var id in keys) {
                    var p = _nodes[id].Parent;
                    // remove when parent invalid, parent not root, or node is not a namespace
                    if (p < 0 || _nodes[p].Parent != -1 || _nodes[id].Kind != NodeKind.Namespace) {
                        lastMap.Remove(id);
                        removed++;
                    }
                }
                if (removed > 0) {
                    DebugUtil.Print("SymbolTreeB.Lazy", $"root filter (single seg) removed={removed}, kept={lastMap.Count}");
                }
                if (lastMap.Count == 0) { return new SearchResults(Array.Empty<SearchHit>(), 0, effOffset, effLimit, null); }
            }

            // Start with last segment nodes and prune by matching ancestor chain segment-by-segment (right-to-left)
            // Maintain a mapping from the original last-node id to its "current ancestor" as we move left.
            var survivorsAdv = new Dictionary<int, (MatchFlags flags, int cur)>(lastMap.Count);
            // C) Initialize survivors with last-segment kinds filtering to reduce work early.
            foreach (var kv in lastMap) {
                var nid = kv.Key;
                var entry = _nodes[nid].Entry;
                if (entry is not null && (filterKinds & entry.Kind) != 0) {
                    survivorsAdv[nid] = (kv.Value, nid); // cur starts at the last node itself
                }
            }
            if (survivorsAdv.Count == 0) { return new SearchResults(Array.Empty<SearchHit>(), 0, effOffset, effLimit, null); }
            for (int i = segCount - 2; i >= 0 && survivorsAdv.Count > 0; i--) {
                // Pre-compute the set of parents we will actually probe this round (restrict construction to this set).
                var parents = new HashSet<int>(survivorsAdv.Count);
                foreach (var kv in survivorsAdv) {
                    var parentId = _nodes[kv.Value.cur].Parent;
                    if (parentId >= 0) {
                        parents.Add(parentId); // -1 cannot match any node key
                    }
                }

                // Lazy build the candidates for the current segment (split by exact/non-exact),
                // restricted to the parent set and (optionally) root-first constraint.
                var (allowedExact, allowedNonExact) = BuildSegmentCandidatesSplit(
                    segNormalized: qi.NormalizedSegments[i],
                    segNormalizedLowered: qi.LowerNormalizedSegments[i],
                    restrictTo: parents,
                    rootFirst: rootConstraint && i == 0
                );

                if (allowedExact.Count == 0 && allowedNonExact.Count == 0) { return new SearchResults(Array.Empty<SearchHit>(), 0, effOffset, effLimit, null); }
                var next = new Dictionary<int, (MatchFlags flags, int cur)>(survivorsAdv.Count);
                foreach (var kv in survivorsAdv) {
                    var lastNid = kv.Key;
                    var state = kv.Value;
                    var parent = _nodes[state.cur].Parent; // move one level up on the ancestor chain
                                                           // Per-parent fallback: prefer exact; if no exact, fallback to non-exact
                    if (allowedExact.TryGetValue(parent, out var segFlagsExact)) {
                        next[lastNid] = (state.flags | segFlagsExact, parent);
                    }
                    else if (allowedNonExact.TryGetValue(parent, out var segFlagsNonExact)) {
                        next[lastNid] = (state.flags | segFlagsNonExact, parent);
                    }
                }
                survivorsAdv = next;
                DebugUtil.Print("SymbolTreeB.Lazy", $"i={i}, parents={parents.Count}, allowedExact={allowedExact.Count}, allowedNonExact={allowedNonExact.Count}, survivors(after)={survivorsAdv.Count}");
            }

            if (survivorsAdv.Count == 0) { return new SearchResults(Array.Empty<SearchHit>(), 0, effOffset, effLimit, null); }
            var survivors = new Dictionary<int, MatchFlags>(survivorsAdv.Count);
            foreach (var kv in survivorsAdv) {
                survivors[kv.Key] = kv.Value.flags;
            }

            // Root constraint: for multi-segment only, the ancestor of the first segment must be directly under the root
            if (rootConstraint && segCount > 1) {
                var toRemove = new List<int>();
                foreach (var nid in survivors.Keys) {
                    var curAncestor = survivorsAdv[nid].cur;
                    var p = _nodes[curAncestor].Parent;
                    if (p < 0 || _nodes[p].Parent != -1) {
                        toRemove.Add(nid);
                    }
                }
                if (toRemove.Count > 0) {
                    foreach (var nid in toRemove) {
                        survivors.Remove(nid);
                    }
                    DebugUtil.Print("SymbolTreeB.Root", $"root-anchored filter removed={toRemove.Count}, kept={survivors.Count}");
                }

                if (survivors.Count == 0) { return new SearchResults(Array.Empty<SearchHit>(), 0, effOffset, effLimit, null); }
            }

            // Collect results
            // Semantics note:
            // - IgnoreGenericArity ONLY means "arity-insensitive type equality" (e.g., List matches List`1/List`2 as separate nodes).
            // - It does NOT imply subtree expansion; nested types are NOT included unless the query explicitly targets them via segments.
            // - We intentionally DO NOT call CollectSubtreeEntries here to avoid misinterpreting base-name anchors as "prefix-of-nested".
            var hits = new List<SearchHit>();
            foreach (var kv in survivors) {
                var nid = kv.Key;
                var flags = kv.Value;
                CollectEntriesAtNode(nid, hits, flags, filterKinds);
                if (hits.Count >= effLimit + effOffset) { break; }
            }


            if (hits.Count == 0) { return new SearchResults(Array.Empty<SearchHit>(), 0, effOffset, effLimit, null); }
            var ordered = hits
                .OrderBy(h => h.Score)
                .ThenBy(h => h.Name, StringComparer.Ordinal)
                .ThenBy(h => h.Assembly)
                .ToList();
            var total = ordered.Count;
            var page = ordered.Skip(effOffset).Take(effLimit).ToArray();
            int? nextOff = effOffset + effLimit < total ? effOffset + effLimit : null;
            return new SearchResults(page, total, effOffset, effLimit, nextOff);
        }

        // Single pass with per-segment exact-first fallback
        return RunUnified();
    }

    // Build candidate nodes for a single segment using exact aliases and optional non-exact fallbacks.
    private Dictionary<int, MatchFlags> BuildSegmentCandidates(string segNormalized, string? segNormalizedLowered)
        => BuildSegmentCandidates(segNormalized, segNormalizedLowered, restrictTo: null, rootFirst: false);

    // Restricted variant to reduce construction and lookup cost by building only what will be probed.
    private Dictionary<int, MatchFlags> BuildSegmentCandidates(
        string segNormalized,
        string? segNormalizedLowered,
        ISet<int>? restrictTo,
        bool rootFirst
    ) {
        var map = new Dictionary<int, MatchFlags>(restrictTo?.Count ?? 0);

        bool ShouldKeep(int nid) {
            if (restrictTo is not null && !restrictTo.Contains(nid)) { return false; }
            if (rootFirst) {
                var p = _nodes[nid].Parent;
                if (p < 0 || _nodes[p].Parent != -1) { return false; /* keep only nodes whose parent is directly under root */ }
            }
            return true;
        }

        int matchedRestrict = 0;
        int restrictGoal = restrictTo?.Count ?? int.MaxValue;

        void Add(IEnumerable<AliasRelation> rels, Func<MatchFlags, bool>? flagFilter = null) {
            foreach (var rel in rels) {
                if (flagFilter != null && !flagFilter(rel.Kind)) { continue; }
                var nid = rel.NodeId;
                if (!ShouldKeep(nid)) { continue; }
                if (map.TryGetValue(nid, out var f)) {
                    map[nid] = f | rel.Kind;
                }
                else {
                    map[nid] = rel.Kind;
                    if (restrictTo is not null) {
                        matchedRestrict++;
                        if (matchedRestrict >= restrictGoal) {
                            // All restrict targets matched; no need to keep scanning this bucket.
                            break;
                        }
                    }
                }
            }
        }

        // Exact: normalized key (bn or bn`n). Ensure node name equals normalized for arity-sensitive semantics.
        Add(CandidatesExact(segNormalized));

        // Non-exact phase: no need to branch by arity; buckets already encode:
        // - Generic base anchors (bn → IgnoreGenericArity). NOTE: arity-insensitive equality ONLY; no subtree expansion.
        // - Lower-cased DocId exact for generic (lower "bn`n" → IgnoreCase)
        if (map.Count > 0) { return map; }
        if (!string.IsNullOrEmpty(segNormalized)) {
            Add(CandidatesNonExact(segNormalized));
            if (restrictTo is not null && matchedRestrict >= restrictGoal) { return map; }
        }
        if (!string.IsNullOrEmpty(segNormalizedLowered)) {
            Add(CandidatesNonExact(segNormalizedLowered));
            if (restrictTo is not null && matchedRestrict >= restrictGoal) { return map; }
        }

        return map;
    }

    // Split variant: build exact and non-exact maps separately for per-parent fallback semantics.
    private (Dictionary<int, MatchFlags> exact, Dictionary<int, MatchFlags> nonExact) BuildSegmentCandidatesSplit(
        string segNormalized,
        string? segNormalizedLowered,
        ISet<int>? restrictTo,
        bool rootFirst
    ) {
        var exact = new Dictionary<int, MatchFlags>(restrictTo?.Count ?? 0);
        var nonExact = new Dictionary<int, MatchFlags>(restrictTo?.Count ?? 0);

        bool ShouldKeep(int nid) {
            if (restrictTo is not null && !restrictTo.Contains(nid)) { return false; }
            if (rootFirst) {
                var p = _nodes[nid].Parent;
                if (p < 0 || _nodes[p].Parent != -1) { return false; /* keep only nodes whose parent is directly under root */ }
            }
            return true;
        }

        int matchedRestrict = 0;
        int restrictGoal = restrictTo?.Count ?? int.MaxValue;

        void AddTo(Dictionary<int, MatchFlags> target, IEnumerable<AliasRelation> rels) {
            foreach (var rel in rels) {
                var nid = rel.NodeId;
                if (!ShouldKeep(nid)) { continue; }
                if (target.TryGetValue(nid, out var f)) {
                    target[nid] = f | rel.Kind;
                }
                else {
                    target[nid] = rel.Kind;
                    if (restrictTo is not null) {
                        matchedRestrict++;
                        if (matchedRestrict >= restrictGoal) {
                            // All restrict targets matched; no need to keep scanning this bucket.
                            break;
                        }
                    }
                }
            }
        }

        // Exact phase
        if (!string.IsNullOrEmpty(segNormalized)) {
            AddTo(exact, CandidatesExact(segNormalized));
        }

        // Non-exact phase: only add parents not already covered by exact
        bool CoveredByExact(int nid) => exact.ContainsKey(nid);

        void AddNonExact(IEnumerable<AliasRelation> rels) {
            foreach (var rel in rels) {
                var nid = rel.NodeId;
                if (!ShouldKeep(nid)) { continue; }
                if (CoveredByExact(nid)) { continue; }
                if (nonExact.TryGetValue(nid, out var f)) {
                    nonExact[nid] = f | rel.Kind;
                }
                else {
                    nonExact[nid] = rel.Kind;
                    if (restrictTo is not null) {
                        matchedRestrict++;
                        if (matchedRestrict >= restrictGoal) { break; }
                    }
                }
            }
        }

        if (!string.IsNullOrEmpty(segNormalized)) {
            AddNonExact(CandidatesNonExact(segNormalized));
            if (restrictTo is not null && matchedRestrict >= restrictGoal) { return (exact, nonExact); }
        }
        if (!string.IsNullOrEmpty(segNormalizedLowered)) {
            AddNonExact(CandidatesNonExact(segNormalizedLowered));
            if (restrictTo is not null && matchedRestrict >= restrictGoal) { return (exact, nonExact); }
        }

        return (exact, nonExact);
    }
}
