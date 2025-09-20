using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.InteropServices;

using Atelia.Diagnostics;
using CodeCortexV2.Abstractions;

namespace CodeCortexV2.Index.SymbolTreeInternal {
    internal readonly record struct AliasRelation(MatchFlags Kind, int NodeId);

    /// <summary>
    /// Tick-Tock buffer: alternative tree-based index implementing the two-layer alias design.
    /// - Structure layer: immutable Node array + entry refs
    /// - Alias layer: exact vs non-exact alias → node ids
    /// </summary>
    internal sealed class SymbolTreeB : ISymbolIndex {
        private readonly ImmutableArray<NodeB> _nodes;

        private readonly Dictionary<string, ImmutableArray<AliasRelation>> _exactAliasToNodes;    // case-sensitive aliases
        private readonly Dictionary<string, ImmutableArray<AliasRelation>> _nonExactAliasToNodes; // generic-base / ignore-case etc.

        private SymbolTreeB(
            ImmutableArray<NodeB> nodes,
            Dictionary<string, ImmutableArray<AliasRelation>> exactAliasToNodes,
            Dictionary<string, ImmutableArray<AliasRelation>> nonExactAliasToNodes
        ) {
            _nodes = nodes;
            _exactAliasToNodes = exactAliasToNodes;
            _nonExactAliasToNodes = nonExactAliasToNodes;
        }

        public static SymbolTreeB Empty { get; } = new(
            ImmutableArray<NodeB>.Empty,
            new Dictionary<string, ImmutableArray<AliasRelation>>(StringComparer.Ordinal),
            new Dictionary<string, ImmutableArray<AliasRelation>>(StringComparer.Ordinal)
        );

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

                    // Lazy build the candidates for the current segment, restricted to the parent set and (optionally) root-first constraint.
                    var allowed = BuildSegmentCandidates(
                        segNormalized: qi.NormalizedSegments[i],
                        segNormalizedLowered: qi.LowerNormalizedSegments[i],
                        restrictTo: parents,
                        rootFirst: rootConstraint && i == 0
                    );

                    if (allowed.Count == 0) { return new SearchResults(Array.Empty<SearchHit>(), 0, effOffset, effLimit, null); }
                    var next = new Dictionary<int, (MatchFlags flags, int cur)>(survivorsAdv.Count);
                    foreach (var kv in survivorsAdv) {
                        var lastNid = kv.Key;
                        var state = kv.Value;
                        var parent = _nodes[state.cur].Parent; // move one level up on the ancestor chain
                        if (allowed.TryGetValue(parent, out var segFlags)) {
                            // Keep the original last node id as the key; advance current ancestor to parent
                            // Merge flags from the current segment into the accumulated flags to reflect non-exact sources across the whole chain.
                            next[lastNid] = (state.flags | segFlags, parent);
                        }
                    }
                    survivorsAdv = next;
                    DebugUtil.Print("SymbolTreeB.Lazy", $"i={i}, parents={parents.Count}, allowed={allowed.Count}, survivors(after)={survivorsAdv.Count}");
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

        public ISymbolIndex WithDelta(SymbolsDelta delta) {
            // Placeholder: to be wired with a builder that only rebuilds affected subtrees and alias buckets.
            return this;
        }

        /// <summary>
        /// Factory from a flat list of entries. Builds nodes and two alias maps (exact / non-exact).
        /// </summary>
        public static SymbolTreeB FromEntries(IEnumerable<SymbolEntry> entries) {
            var arr = entries?.ToImmutableArray() ?? ImmutableArray<SymbolEntry>.Empty;

            // Mutable node list with string names
            var nodes = new List<(string Name, int Parent, int FirstChild, int NextSibling, NodeKind Kind, List<int> Entries)>();
            var keyToIndex = new Dictionary<(int Parent, string Name, NodeKind Kind), int>();
            int NewNode(string name, int parent, NodeKind kind) {
                var idx = nodes.Count;
                nodes.Add((name, parent, -1, -1, kind, new List<int>()));
                if (parent >= 0) {
                    var p = nodes[parent];
                    nodes[parent] = (p.Name, p.Parent, idx, p.FirstChild, p.Kind, p.Entries);
                    nodes[idx] = (name, parent, -1, p.FirstChild, kind, nodes[idx].Entries);
                }
                return idx;
            }
            int GetOrAddNode(int parent, string name, NodeKind kind) {
                var key = (parent, name, kind);
                if (!keyToIndex.TryGetValue(key, out var idx)) {
                    idx = NewNode(name, parent, kind);
                    keyToIndex[key] = idx;
                }
                return idx;
            }

            // Root
            int root = NewNode(string.Empty, -1, NodeKind.Namespace);

            // Alias buckets (store relations with flags)
            // NOTE (extension point): 可在“别名层”引入 Member 级别的别名关系，或为命名空间命中支持“展开子树”语义；当前版本不实现，仅记录扩展点。
            var exactBuckets = new Dictionary<string, Dictionary<int, MatchFlags>>(StringComparer.Ordinal);
            var nonExactBuckets = new Dictionary<string, Dictionary<int, MatchFlags>>(StringComparer.Ordinal);
            static void Bucket(Dictionary<string, Dictionary<int, MatchFlags>> dict, string alias, int nodeIdx, MatchFlags kind) {
                if (string.IsNullOrEmpty(alias)) { return; }
                ref var perNode = ref CollectionsMarshal.GetValueRefOrAddDefault(dict, alias, out _);
                perNode ??= new Dictionary<int, MatchFlags>();
                ref var flagsRef = ref CollectionsMarshal.GetValueRefOrAddDefault(perNode, nodeIdx, out _);
                flagsRef |= kind;
            }

            static IEnumerable<string> SplitNs(string? ns)
                => string.IsNullOrEmpty(ns) ? Array.Empty<string>() : ns!.Split('.', StringSplitOptions.RemoveEmptyEntries);




            // Build namespace chain and add alias buckets; returns the last namespace node index
            int BuildNamespaceChainAndAliases(int startParent, string[] segments) {
                int parent = startParent;
                for (int i = 0; i < segments.Length; i++) {
                    var seg = segments[i];
                    var idx = GetOrAddNode(parent, seg, NodeKind.Namespace);
                    Bucket(exactBuckets, seg, idx, MatchFlags.None);
                    var lower = seg.ToLowerInvariant();
                    if (!string.Equals(lower, seg, StringComparison.Ordinal)) {
                        Bucket(nonExactBuckets, lower, idx, MatchFlags.IgnoreCase);
                    }
                    parent = idx;
                }
                return parent;
            }

            // Build type chain from type segments (bn or bn`n), add alias buckets; returns the last type node index
            int BuildTypeChainAndAliases(int namespaceParent, string[] typeSegs) {
                int lastTypeNode = namespaceParent;
                for (int i = 0; i < typeSegs.Length; i++) {
                    var (bn, ar) = QueryPreprocessor.ParseTypeSegment(typeSegs[i]);
                    var nodeName = ar > 0 ? bn + "`" + ar.ToString() : bn;
                    bool isLast = i == typeSegs.Length - 1;
                    int idx = isLast ? NewNode(nodeName, lastTypeNode, NodeKind.Type) : GetOrAddNode(lastTypeNode, nodeName, NodeKind.Type);

                    if (ar > 0) {
                        // Exact: DocId-like form only (bn`n)
                        var docIdSeg = nodeName;
                        Bucket(exactBuckets, docIdSeg, idx, MatchFlags.None);

                        // NonExact:
                        // 1) Generic base-name anchors (arity-insensitive equality ONLY; no subtree expansion)
                        Bucket(nonExactBuckets, bn, idx, MatchFlags.IgnoreGenericArity);
                        var lowerBn = bn.ToLowerInvariant();
                        if (!string.Equals(lowerBn, bn, StringComparison.Ordinal)) {
                            Bucket(nonExactBuckets, lowerBn, idx, MatchFlags.IgnoreGenericArity | MatchFlags.IgnoreCase);
                        }
                        // 2) Case-insensitive exact for DocId-like form (lower-cased)
                        var lowerDocIdSeg = docIdSeg.ToLowerInvariant();
                        if (!string.Equals(lowerDocIdSeg, docIdSeg, StringComparison.Ordinal)) {
                            Bucket(nonExactBuckets, lowerDocIdSeg, idx, MatchFlags.IgnoreCase);
                        }
                    }
                    else {
                        // Non-generic simple name
                        Bucket(exactBuckets, bn, idx, MatchFlags.None);
                        var lowerBn = bn.ToLowerInvariant();
                        if (!string.Equals(lowerBn, bn, StringComparison.Ordinal)) {
                            Bucket(nonExactBuckets, lowerBn, idx, MatchFlags.IgnoreCase);
                        }
                    }

                    lastTypeNode = idx;
                }
                return lastTypeNode;
            }

            for (int ei = 0; ei < arr.Length; ei++) {
                var e = arr[ei];
                bool isNs = (e.Kind & SymbolKinds.Namespace) != 0;
                bool isType = (e.Kind & SymbolKinds.Type) != 0;
                if (!isNs && !isType) { continue; /* current index only materializes Namespace and Type */ }
                string[] nsSegments = isType ? SplitNs(e.ParentNamespace).ToArray() : SplitNs(e.FqnNoGlobal).ToArray();
                int parent = BuildNamespaceChainAndAliases(root, nsSegments);

                if (isNs) {
                    // Namespace entry: attach to the namespace node
                    if (nsSegments.Length > 0) {
                        nodes[parent].Entries.Add(ei);
                    }
                    continue;
                }

                // 2) Build type chain from DocId strictly ("T:"-prefixed)
                System.Diagnostics.Debug.Assert(!string.IsNullOrEmpty(e.SymbolId) && e.SymbolId.StartsWith("T:", StringComparison.Ordinal), "Type entries must have DocId starting with 'T:'");
                var s = e.SymbolId[2..];
                var allSegs = QueryPreprocessor.SplitSegments(s);
                var nsCount = nsSegments.Length;
                if (nsCount < 0 || nsCount > allSegs.Length) {
                    nsCount = 0;
                }
                string[] typeSegs = allSegs.Skip(nsCount).ToArray();
                int lastTypeNode = BuildTypeChainAndAliases(parent, typeSegs);
                nodes[lastTypeNode].Entries.Add(ei);
            }

            // Seal nodes (NodeB keeps a single entry reference)
            var sealedNodes = new List<NodeB>(nodes.Count);
            for (int i = 0; i < nodes.Count; i++) {
                var n = nodes[i];
                var entry = n.Entries.Count > 0 ? arr[n.Entries[0]] : null;
                sealedNodes.Add(new NodeB(n.Name, n.Parent, n.FirstChild, n.NextSibling, n.Kind, entry));
            }

            static Dictionary<string, ImmutableArray<AliasRelation>> SealBuckets(Dictionary<string, Dictionary<int, MatchFlags>> buckets) {
                var result = new Dictionary<string, ImmutableArray<AliasRelation>>(StringComparer.Ordinal);
                foreach (var kv in buckets) {
                    var arr2 = kv.Value.Select(p => new AliasRelation(p.Value, p.Key)).ToImmutableArray();
                    result[kv.Key] = arr2;
                }
                return result;
            }

            var exact = SealBuckets(exactBuckets);
            var nonExact = SealBuckets(nonExactBuckets);

            return new SymbolTreeB(
                sealedNodes.ToImmutableArray(),
                exact,
                nonExact
            );
        }
    }
}

