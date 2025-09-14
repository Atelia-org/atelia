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
            if (_exactAliasToNodes.TryGetValue(last, out var rels)) {
                return rels;
            }

            return Array.Empty<AliasRelation>();
        }
        private IEnumerable<AliasRelation> CandidatesNonExact(string last) {
            if (_nonExactAliasToNodes.TryGetValue(last, out var rels)) {
                return rels;
            }

            return Array.Empty<AliasRelation>();
        }

        private void CollectEntriesAtNode(int nodeIdx, List<SearchHit> acc, MatchFlags kind, SymbolKinds filter) {
            var entry = _nodes[nodeIdx].Entry;
            if (entry is null) {
                return;
            }
            if ((filter & entry.Kind) == 0) {
                return;
            }
            acc.Add(entry.ToHit(kind, 0));
        }

        private void CollectSubtreeEntries(int nodeIdx, List<SearchHit> acc, MatchFlags kind, int maxCount, SymbolKinds filter) {
            var stack = new Stack<int>();
            stack.Push(nodeIdx);
            while (stack.Count > 0 && acc.Count < maxCount) {
                var cur = stack.Pop();
                CollectEntriesAtNode(cur, acc, kind, filter);
                var child = _nodes[cur].FirstChild;
                while (child >= 0) {
                    stack.Push(child);
                    child = _nodes[child].NextSibling;
                }
            }
        }

        /// <summary>
        /// Execute search via two passes: (1) exact-only per-segment, (2) include non-exact.
        /// Both passes build candidates for each segment and prune right-to-left by parent relation.
        /// </summary>
        public SearchResults Search(string query, int limit, int offset, SymbolKinds kinds) {
            var effLimit = Math.Max(0, limit);
            var effOffset = Math.Max(0, offset);
            if (string.IsNullOrWhiteSpace(query)) {
                return new SearchResults(Array.Empty<SearchHit>(), 0, effOffset, effLimit, null);
            }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            query = query.Trim();
            DebugUtil.Print("SymbolTreeB.Search", $"q='{query}', kinds={kinds}, limit={effLimit}, offset={effOffset}");

            // Centralized query preprocessing
            var qi = QueryPreprocessor.Preprocess(query);
            if (!string.IsNullOrEmpty(qi.RejectionReason)) {
                return new SearchResults(Array.Empty<SearchHit>(), 0, effOffset, effLimit, null);
            }


            bool rootConstraint = qi.IsRootAnchored;
            var segs = qi.NormalizedSegments;
            if (segs.Length == 0) {
                return new SearchResults(Array.Empty<SearchHit>(), 0, effOffset, effLimit, null);
            }
            var filterKinds = kinds | qi.DocIdKind;

            SearchResults RunUnified(bool exactOnly) {
                int segCount = segs.Length;
                var perSeg = new Dictionary<int, MatchFlags>[segCount];

                for (int i = 0; i < segCount; i++) {
                    perSeg[i] = BuildSegmentCandidates(
                        segNormalized: qi.NormalizedSegments[i],
                        segNormalizedLowered: qi.LowerNormalizedSegments[i],
                        exactOnly: exactOnly
                    );
                    if (perSeg[i].Count == 0) {
                        return new SearchResults(Array.Empty<SearchHit>(), 0, effOffset, effLimit, null);
                    }
                }

                // Start with last segment nodes and prune by matching ancestor chain segment-by-segment (right-to-left)
                // Maintain a mapping from the original last-node id to its "current ancestor" as we move left.
                var survivorsAdv = new Dictionary<int, (MatchFlags flags, int cur)>(perSeg[segCount - 1].Count);
                foreach (var kv in perSeg[segCount - 1]) {
                    survivorsAdv[kv.Key] = (kv.Value, kv.Key); // cur starts at the last node itself
                }

                for (int i = segCount - 2; i >= 0 && survivorsAdv.Count > 0; i--) {
                    var allowed = perSeg[i];
                    var next = new Dictionary<int, (MatchFlags flags, int cur)>(survivorsAdv.Count);
                    foreach (var kv in survivorsAdv) {
                        var lastNid = kv.Key;
                        var state = kv.Value;
                        var parent = _nodes[state.cur].Parent; // move one level up on the ancestor chain
                        if (allowed.ContainsKey(parent)) {
                            // Keep the original last node id as the key; advance current ancestor to parent
                            next[lastNid] = (state.flags /* | allowed[parent] */ , parent);
                        }
                    }
                    survivorsAdv = next;
                }

                if (survivorsAdv.Count == 0) {
                    return new SearchResults(Array.Empty<SearchHit>(), 0, effOffset, effLimit, null);
                }

                // Project back to <nodeId, flags> for the remaining pipeline (collection/sorting/paging)
                var survivors = new Dictionary<int, MatchFlags>(survivorsAdv.Count);
                foreach (var kv in survivorsAdv) {
                    survivors[kv.Key] = kv.Value.flags;
                }

                // Root constraint: the ancestor of the first segment must be root (parent == -1)
                if (rootConstraint) {
                    var toRemove = new List<int>();
                    foreach (var nid in survivors.Keys) {
                        var curAncestor = survivorsAdv[nid].cur;
                        if (_nodes[curAncestor].Parent != -1) {
                            toRemove.Add(nid);
                        }
                    }
                    if (toRemove.Count > 0) {
                        foreach (var nid in toRemove) {
                            survivors.Remove(nid);
                        }
                        DebugUtil.Print("SymbolTreeB.Root", $"root-anchored filter removed={toRemove.Count}, kept={survivors.Count}");
                    }

                    if (survivors.Count == 0) {
                        return new SearchResults(Array.Empty<SearchHit>(), 0, effOffset, effLimit, null);
                    }
                }

                // Collect results
                var hits = new List<SearchHit>();
                foreach (var kv in survivors) {
                    var nid = kv.Key;
                    var flags = kv.Value;
                    if ((flags & MatchFlags.IgnoreGenericArity) != 0) {
                        CollectSubtreeEntries(nid, hits, flags | MatchFlags.Partial, effLimit + effOffset, filterKinds);
                    } else {
                        CollectEntriesAtNode(nid, hits, flags, filterKinds);
                    }
                    if (hits.Count >= effLimit + effOffset) {
                        break;
                    }
                }

                if (hits.Count == 0) {
                    return new SearchResults(Array.Empty<SearchHit>(), 0, effOffset, effLimit, null);
                }

                var ordered = hits.OrderBy(h => h.Name, StringComparer.Ordinal).ThenBy(h => h.Assembly).ToList();
                var total = ordered.Count;
                var page = ordered.Skip(effOffset).Take(effLimit).ToArray();
                int? nextOff = effOffset + effLimit < total ? effOffset + effLimit : null;
                return new SearchResults(page, total, effOffset, effLimit, nextOff);
            }

            // Pass 1: exact-only
            var res1 = RunUnified(exactOnly: true);
            if (res1.Total > 0) {
                return res1;
            }

            // Pass 2: include non-exact
            var res2 = RunUnified(exactOnly: false);
            return res2;
        }

        // Build candidate nodes for a single segment using exact aliases and optional non-exact fallbacks.
        private Dictionary<int, MatchFlags> BuildSegmentCandidates(string segNormalized, string? segNormalizedLowered, bool exactOnly) {
            var map = new Dictionary<int, MatchFlags>();

            void Add(IEnumerable<AliasRelation> rels, StringComparison cmp, Func<MatchFlags, bool>? flagFilter = null) {
                foreach (var rel in rels) {
                    if (flagFilter != null && !flagFilter(rel.Kind)) {
                        continue;
                    }

                    var nid = rel.NodeId;
                    if (map.TryGetValue(nid, out var f)) {
                        map[nid] = f | rel.Kind;
                    } else {
                        map[nid] = rel.Kind;
                    }
                }
            }

            // Exact: normalized key (bn or bn`n). Ensure node name equals normalized for arity-sensitive semantics.
            Add(CandidatesExact(segNormalized), cmp: StringComparison.Ordinal);

            // Non-exact phase: no need to branch by arity; buckets already encode
            // - generic base anchors (bn → IgnoreGenericArity)
            // - lower-cased doc-id exact for generic (lower "bn`n" → IgnoreCase)
            if (exactOnly) {
                return map;
            }

            if (!string.IsNullOrEmpty(segNormalized)) {
                Add(CandidatesNonExact(segNormalized), cmp: StringComparison.Ordinal);
            }
            var lower = segNormalizedLowered ?? segNormalized.ToLowerInvariant();
            if (!string.Equals(lower, segNormalized, StringComparison.Ordinal)) {
                Add(CandidatesNonExact(lower), cmp: StringComparison.Ordinal);
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
                if (string.IsNullOrEmpty(alias)) {
                    return;
                }
                // Fast-path insert/merge using CollectionsMarshal to avoid double lookups and extra allocations.
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
                        // 1) Generic base name anchors (Prefix semantics)
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
                    } else {
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
                if (!isNs && !isType) {
                    continue; // current index only materializes Namespace and Type

                }

                // 1) Build namespace chain once
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

