using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
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

                // Start with last segment nodes and prune by matching parents
                var survivors = new Dictionary<int, MatchFlags>(perSeg[segCount - 1]);
                for (int i = segCount - 2; i >= 0 && survivors.Count > 0; i--) {
                    var allowedParents = new HashSet<int>(perSeg[i].Keys);
                    var toRemove = new List<int>();
                    foreach (var kv in survivors) {
                        var nid = kv.Key;
                        var parent = _nodes[nid].Parent;
                        if (!allowedParents.Contains(parent)) {
                            toRemove.Add(nid);
                        }
                    }
                    foreach (var nid in toRemove) {
                        survivors.Remove(nid);
                    }
                }

                if (survivors.Count == 0) {
                    return new SearchResults(Array.Empty<SearchHit>(), 0, effOffset, effLimit, null);
                }

                // Root constraint: verify the chain above the first segment reaches root (index 0)
                if (rootConstraint) {
                    var toRemove = new List<int>();
                    foreach (var nid in survivors.Keys) {
                        int cur = nid;
                        for (int step = 0; step < segs.Length && cur >= 0; step++) {
                            cur = _nodes[cur].Parent;
                        }

                        if (cur != 0) {
                            toRemove.Add(nid);
                        }
                    }
                    foreach (var nid in toRemove) {
                        survivors.Remove(nid);
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

            void Add(IEnumerable<AliasRelation> rels, bool requireNameMatch, StringComparison cmp, Func<MatchFlags, bool>? flagFilter = null) {
                foreach (var rel in rels) {
                    if (flagFilter != null && !flagFilter(rel.Kind)) {
                        continue;
                    }

                    var nid = rel.NodeId;
                    if (requireNameMatch && !string.Equals(_nodes[nid].Name, segNormalized, cmp)) {
                        continue;
                    }
                    if (map.TryGetValue(nid, out var f)) {
                        map[nid] = f | rel.Kind;
                    } else {
                        map[nid] = rel.Kind;
                    }
                }
            }

            // Exact: normalized key (bn or bn`n). Ensure node name equals normalized for arity-sensitive semantics.
            Add(CandidatesExact(segNormalized), requireNameMatch: true, cmp: StringComparison.Ordinal);

            bool hasArity = segNormalized.IndexOf('`') >= 0;

            if (exactOnly) {
                // In exact-only pass, do not add case-insensitive or generic-base fallbacks
                return map;
            }

            // Inclusive pass: generic-base anchors and lowercase variants.
            if (hasArity) {
                // Case-insensitive exact for generic (lower "bn`n")
                var lowerDoc = segNormalizedLowered ?? segNormalized.ToLowerInvariant();
                Add(CandidatesNonExact(lowerDoc), requireNameMatch: false, cmp: StringComparison.Ordinal);
            } else {
                // Generic-base anchors via original base and its lower variant
                if (!string.IsNullOrEmpty(segNormalized)) {
                    Add(CandidatesNonExact(segNormalized), requireNameMatch: false, cmp: StringComparison.Ordinal);
                }
                var lowerBase = segNormalizedLowered ?? segNormalized.ToLowerInvariant();
                Add(CandidatesNonExact(lowerBase), requireNameMatch: false, cmp: StringComparison.Ordinal);
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
            var keyToIndex = new Dictionary<string, int>(StringComparer.Ordinal);
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
                var key = parent.ToString() + "|" + name + "|" + ((int)kind).ToString();
                if (!keyToIndex.TryGetValue(key, out var idx)) {
                    idx = NewNode(name, parent, kind);
                    keyToIndex[key] = idx;
                }
                return idx;
            }

            // Root
            int root = NewNode(string.Empty, -1, NodeKind.Namespace);

            // Alias buckets (store relations with flags)
            var exactBuckets = new Dictionary<string, List<AliasRelation>>(StringComparer.Ordinal);
            var nonExactBuckets = new Dictionary<string, List<AliasRelation>>(StringComparer.Ordinal);
            static void Bucket(Dictionary<string, List<AliasRelation>> dict, string alias, int nodeIdx, MatchFlags kind) {
                if (string.IsNullOrEmpty(alias)) {
                    return;
                }

                if (!dict.TryGetValue(alias, out var list)) {
                    list = new List<AliasRelation>();
                    dict[alias] = list;
                }
                list.Add(new AliasRelation(kind, nodeIdx));
            }

            static IEnumerable<string> SplitNs(string? ns)
                => string.IsNullOrEmpty(ns) ? Array.Empty<string>() : ns!.Split('.', StringSplitOptions.RemoveEmptyEntries);

            static (string baseName, int arity) ParseTypeSegment(string seg) {
                if (string.IsNullOrEmpty(seg)) {
                    return (seg, 0);
                }

                var baseName = seg;
                int arity = 0;
                var back = seg.IndexOf('`');
                if (back >= 0) {
                    baseName = seg.Substring(0, back);
                    var numStr = new string(seg.Skip(back + 1).TakeWhile(char.IsDigit).ToArray());
                    if (int.TryParse(numStr, out var n1)) {
                        arity = n1;
                    }
                }
                var lt = seg.IndexOf('<');
                if (lt >= 0) {
                    baseName = seg.Substring(0, lt);
                    var inside = seg.Substring(lt + 1);
                    var rt = inside.LastIndexOf('>');
                    if (rt >= 0) {
                        inside = inside.Substring(0, rt);
                    }

                    if (inside.Length > 0) {
                        arity = inside.Count(c => c == ',') + 1;
                    }
                }
                return (baseName, arity);
            }


            for (int ei = 0; ei < arr.Length; ei++) {
                var e = arr[ei];
                if ((e.Kind & SymbolKinds.Namespace) != 0) {
                    var nsSegments = SplitNs(e.FqnNoGlobal).ToArray();
                    int parent = root;
                    for (int i = 0; i < nsSegments.Length; i++) {
                        var canon = nsSegments[i];
                        var idx = GetOrAddNode(parent, canon, NodeKind.Namespace);
                        // aliases for this namespace segment
                        Bucket(exactBuckets, canon, idx, MatchFlags.None);
                        var lower = canon.ToLowerInvariant();
                        if (!string.Equals(lower, canon, StringComparison.Ordinal)) {
                            Bucket(nonExactBuckets, lower, idx, MatchFlags.IgnoreCase);
                        }

                        parent = idx;
                    }
                    if (nsSegments.Length > 0) {
                        nodes[parent].Entries.Add(ei);
                    }
                    // DocId as exact alias for namespace (supports duplicates across assemblies)
                    if (!string.IsNullOrEmpty(e.SymbolId) && e.SymbolId.StartsWith("N:", StringComparison.Ordinal)) {
                        Bucket(exactBuckets, e.SymbolId, parent, MatchFlags.None);
                    }

                }
                if ((e.Kind & SymbolKinds.Type) != 0) {
                    int parent = root;
                    foreach (var ns in SplitNs(e.ParentNamespace)) {
                        parent = GetOrAddNode(parent, ns, NodeKind.Namespace);
                        Bucket(exactBuckets, ns, parent, MatchFlags.None);
                        var lowerNs = ns.ToLowerInvariant();
                        if (!string.Equals(lowerNs, ns, StringComparison.Ordinal)) {
                            Bucket(nonExactBuckets, lowerNs, parent, MatchFlags.IgnoreCase);
                        }
                    }
                    var fqn = e.FqnNoGlobal ?? string.Empty;
                    // Prefer DocId for nested chain (uses '+'), fallback to FQN
                    string typeChain;
                    if (!string.IsNullOrEmpty(e.SymbolId) && e.SymbolId.StartsWith("T:", StringComparison.Ordinal)) {
                        var s = e.SymbolId.Substring(2);
                        if (!string.IsNullOrEmpty(e.ParentNamespace) && s.StartsWith(e.ParentNamespace + ".", StringComparison.Ordinal)) {
                            typeChain = s.Substring(e.ParentNamespace.Length + 1); // keep full nested chain like Outer`1.Inner
                        } else {
                            var lastDotDoc = s.LastIndexOf('.');
                            typeChain = lastDotDoc >= 0 ? s.Substring(lastDotDoc + 1) : s;
                        }
                    } else {
                        var dot = fqn.LastIndexOf('.');
                        typeChain = dot >= 0 ? fqn[(dot + 1)..] : fqn;
                    }
                    var typeSegs = typeChain.Split('.', StringSplitOptions.RemoveEmptyEntries);
                    int lastTypeNode = parent;
                    for (int i = 0; i < typeSegs.Length; i++) {
                        var (bn, ar) = ParseTypeSegment(typeSegs[i]);
                        var nodeName = ar > 0 ? bn + "`" + ar.ToString() : bn;
                        bool isLast = i == typeSegs.Length - 1;
                        int idx;
                        if (isLast) {
                            idx = NewNode(nodeName, lastTypeNode, NodeKind.Type); // allow duplicates per entry
                        } else {
                            idx = GetOrAddNode(lastTypeNode, nodeName, NodeKind.Type);
                        }
                        // aliases for this type segment (apply to actual created node)
                        if (ar > 0) {
                            // Exact: DocId-like form only (bn`n)
                            var docIdSeg = bn + "`" + ar.ToString();
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
                    nodes[lastTypeNode].Entries.Add(ei);
                    // DocId as exact alias for type (supports duplicates across assemblies)
                    if (!string.IsNullOrEmpty(e.SymbolId) && e.SymbolId.StartsWith("T:", StringComparison.Ordinal)) {
                        Bucket(exactBuckets, e.SymbolId, lastTypeNode, MatchFlags.None);
                    }

                }
            }

            // Seal nodes (NodeB keeps a single entry reference)
            var sealedNodes = new List<NodeB>(nodes.Count);
            for (int i = 0; i < nodes.Count; i++) {
                var n = nodes[i];
                var entry = n.Entries.Count > 0 ? arr[n.Entries[0]] : null;
                sealedNodes.Add(new NodeB(n.Name, n.Parent, n.FirstChild, n.NextSibling, n.Kind, entry));
            }

            static Dictionary<string, ImmutableArray<AliasRelation>> SealBuckets(Dictionary<string, List<AliasRelation>> buckets) {
                var result = new Dictionary<string, ImmutableArray<AliasRelation>>(StringComparer.Ordinal);
                foreach (var kv in buckets) {
                    var perNode = new Dictionary<int, MatchFlags>();
                    foreach (var rel in kv.Value) {
                        if (perNode.TryGetValue(rel.NodeId, out var existing)) {
                            perNode[rel.NodeId] = existing | rel.Kind;
                        } else {
                            perNode[rel.NodeId] = rel.Kind;
                        }
                    }
                    var arr2 = perNode.Select(p => new AliasRelation(p.Value, p.Key)).ToImmutableArray();
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

