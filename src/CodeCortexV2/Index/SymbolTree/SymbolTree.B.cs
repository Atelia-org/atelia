using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Atelia.Diagnostics;
using CodeCortexV2.Abstractions;

namespace CodeCortexV2.Index.SymbolTreeInternal {
    // Alias relation flags for how an alias relates to a node.
    [System.Flags]
    internal enum AliasKinds : ushort {
        None = 0,
        IgnoreCase = 1 << 0,
        OmitArity = 1 << 1,
    }

    internal readonly record struct AliasRelation(AliasKinds Kind, int NodeId);

    /// <summary>
    /// Tick-Tock buffer: alternative tree-based index implementing the two-layer alias design.
    /// - Structure layer: immutable Node array + entry refs
    /// - Alias layer: exact vs non-exact alias → node ids
    /// </summary>
    internal sealed class SymbolTreeB {
        private readonly ImmutableArray<NodeB> _nodes;
        private readonly NameTable _names;

        private readonly Dictionary<string, ImmutableArray<AliasRelation>> _exactAliasToNodes;    // case-sensitive aliases
        private readonly Dictionary<string, ImmutableArray<AliasRelation>> _nonExactAliasToNodes; // generic-base / ignore-case etc.

        private SymbolTreeB(
            ImmutableArray<NodeB> nodes,
            NameTable names,
            Dictionary<string, ImmutableArray<AliasRelation>> exactAliasToNodes,
            Dictionary<string, ImmutableArray<AliasRelation>> nonExactAliasToNodes
        ) {
            _nodes = nodes;
            _names = names;
            _exactAliasToNodes = exactAliasToNodes;
            _nonExactAliasToNodes = nonExactAliasToNodes;
        }

        public static SymbolTreeB Empty { get; } = new(
            ImmutableArray<NodeB>.Empty,
            NameTable.Empty,
            new Dictionary<string, ImmutableArray<AliasRelation>>(StringComparer.Ordinal),
            new Dictionary<string, ImmutableArray<AliasRelation>>(StringComparer.Ordinal)
        );

        // --- Query helpers ---
        private static string[] SplitSegments(string q) {
            if (string.IsNullOrEmpty(q)) {
                return Array.Empty<string>();
            }

            var parts = q.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) {
                return parts;
            }

            var last = parts[^1];
            var typeSegs = last.Split('+', StringSplitOptions.RemoveEmptyEntries);
            if (typeSegs.Length == 1) {
                return parts;
            }

            var list = new List<string>(parts.Length - 1 + typeSegs.Length);
            for (int i = 0; i < parts.Length - 1; i++) {
                list.Add(parts[i]);
            }

            list.AddRange(typeSegs);
            return list.ToArray();
        }

        private static string BaseName(string s) {
            if (string.IsNullOrEmpty(s)) {
                return s;
            }
            var i = s.IndexOf('`');
            if (i >= 0) {
                return s[..i];
            }
            var j = s.IndexOf('<');
            return j >= 0 ? s[..j] : s;
        }

        private int[] ToAncestorSegIds(string[] segs) {
            int count = Math.Max(0, segs.Length - 1);
            var ids = new int[count];
            for (int i = 0; i < count; i++) {
                var canon = BaseName(segs[i]);
                if (_names.TryGetId(canon, out var id)) {
                    ids[i] = id;
                } else {
                    return Array.Empty<int>();
                }
            }
            return ids;
        }

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

        private bool AncestorsMatch(int nodeIdx, int[] ancestorSegIds, bool requireRoot) {
            int i = ancestorSegIds.Length - 1;
            int cur = _nodes[nodeIdx].Parent;
            while (i >= 0 && cur >= 0) {
                var n = _nodes[cur];
                if (n.NameId != ancestorSegIds[i]) {
                    return false;
                }

                i--;
                cur = n.Parent;
            }
            if (i >= 0) {
                return false; // ran out of ancestors
            }

            if (requireRoot) {
                return cur == 0; // attach to root
            }

            return true;
        }

        private void CollectEntriesAtNode(int nodeIdx, List<SearchHit> acc, MatchKind kind, SymbolKinds filter) {
            var entry = _nodes[nodeIdx].Entry;
            if (entry is null) {
                return;
            }
            if ((filter & entry.Kind) == 0) {
                return;
            }
            acc.Add(entry.ToHit(kind, 0));
        }

        private void CollectSubtreeEntries(int nodeIdx, List<SearchHit> acc, MatchKind kind, int maxCount, SymbolKinds filter) {
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
        /// Execute search with two stages: Exact → NonExact (fallback).
        /// Note: This minimal version implements Exact + Prefix (anchored subtree) behaviors consistent with SymbolTree.
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

            // DocId handling via exact-alias dictionary (supports duplicates across assemblies)
            if (qi.IsDocId) {
                var rels = CandidatesExact(qi.Effective).ToArray();
                if (rels.Length == 0) {
                    return new SearchResults(Array.Empty<SearchHit>(), 0, effOffset, effLimit, null);
                }

                var all = new List<SearchHit>(rels.Length);
                foreach (var rel in rels) {
                    CollectEntriesAtNode(rel.NodeId, all, MatchKind.Id, kinds);
                }

                var total = all.Count;
                var page = all.Skip(effOffset).Take(effLimit).ToArray();
                int? nextOff = effOffset + effLimit < total ? effOffset + effLimit : null;
                return new SearchResults(page, total, effOffset, effLimit, nextOff);
            }

            bool rootConstraint = qi.RootConstraint;
            var segs = qi.SegmentsNormalized;
            if (segs.Length == 0) {
                return new SearchResults(Array.Empty<SearchHit>(), 0, effOffset, effLimit, null);
            }

            var ancestorIds = ToAncestorSegIds(segs);
            if (ancestorIds.Length == 0 && segs.Length > 1) {
                // Ancestor segments could not be resolved → no matches
                return new SearchResults(Array.Empty<SearchHit>(), 0, effOffset, effLimit, null);
            }

            // Stage 1: Exact (case-sensitive alias); last segment already normalized to DocId-like when generic
            {
                var last = segs[^1];
                var hits = new List<SearchHit>();
                var seen = new HashSet<int>();
                var lastLower = qi.LastIsLower;
                foreach (var rel in CandidatesExact(last)) {
                    var nid = rel.NodeId;
                    if (seen.Contains(nid)) {
                        continue;
                    }

                    if (!AncestorsMatch(nid, ancestorIds, rootConstraint)) {
                        continue;
                    }

                    seen.Add(nid);
                    var flags = rel.Kind;

                    var mk = (flags & AliasKinds.OmitArity) != 0
                        ? MatchKind.GenericBase
                        : MatchKind.Exact;
                    CollectEntriesAtNode(nid, hits, mk, kinds);
                    if (hits.Count >= effLimit + effOffset) {
                        break;
                    }
                }
                if (hits.Count > 0) {
                    var ordered = hits.OrderBy(h => h.Name, StringComparer.Ordinal).ThenBy(h => h.Assembly).ToList();
                    var total = ordered.Count;
                    var page = ordered.Skip(effOffset).Take(effLimit).ToArray();
                    int? nextOff = effOffset + effLimit < total ? effOffset + effLimit : null;
                    return new SearchResults(page, total, effOffset, effLimit, nextOff);
                }
            }

            // Stage 2: Non-exact. Try original key first, then lower(original) if original isn't all-lowercase.
            {
                var hits = new List<SearchHit>();
                var lastOrig = qi.LastOriginal;
                var lastLowerStr = lastOrig.ToLowerInvariant();
                var lastIsLower = qi.LastIsLower;

                // Try original key
                foreach (var rel in CandidatesNonExact(lastOrig)) {
                    var nid = rel.NodeId;
                    if (!AncestorsMatch(nid, ancestorIds, rootConstraint)) {
                        continue;
                    }

                    var flags = rel.Kind;

                    if ((flags & AliasKinds.OmitArity) != 0) {
                        CollectSubtreeEntries(nid, hits, MatchKind.Prefix, effLimit + effOffset, kinds);
                    } else {
                        var mk = (flags & AliasKinds.IgnoreCase) != 0 ? MatchKind.ExactIgnoreCase : MatchKind.Exact;
                        CollectEntriesAtNode(nid, hits, mk, kinds);
                    }
                    if (hits.Count >= effLimit + effOffset) {
                        break;
                    }
                }

                // Try lower(original) if needed
                if (hits.Count < effLimit + effOffset && !lastIsLower) {
                    foreach (var rel in CandidatesNonExact(lastLowerStr)) {
                        var nid = rel.NodeId;
                        if (!AncestorsMatch(nid, ancestorIds, rootConstraint)) {
                            continue;
                        }

                        var flags = rel.Kind; // user intent indicates ignore-case by using lower
                        // Note: we do not forcibly OR here; builder already marks lower-key relations with IgnoreCase.
                        if ((flags & AliasKinds.OmitArity) != 0) {
                            CollectSubtreeEntries(nid, hits, MatchKind.Prefix, effLimit + effOffset, kinds);
                        } else {
                            CollectEntriesAtNode(nid, hits, MatchKind.ExactIgnoreCase, kinds);
                        }
                        if (hits.Count >= effLimit + effOffset) {
                            break;
                        }
                    }
                }

                if (hits.Count > 0) {
                    var ordered = hits.OrderBy(h => h.Name, StringComparer.Ordinal).ThenBy(h => h.Assembly).ToList();
                    var total = ordered.Count;
                    var page = ordered.Skip(effOffset).Take(effLimit).ToArray();
                    int? nextOff = effOffset + effLimit < total ? effOffset + effLimit : null;
                    return new SearchResults(page, total, effOffset, effLimit, nextOff);
                }
            }

            return new SearchResults(Array.Empty<SearchHit>(), 0, effOffset, effLimit, null);
        }

        public SymbolTreeB WithDelta(SymbolsDelta delta) {
            // Placeholder: to be wired with a builder that only rebuilds affected subtrees and alias buckets.
            return this;
        }

        /// <summary>
        /// Factory from a flat list of entries. Builds nodes and two alias maps (exact / non-exact).
        /// </summary>
        public static SymbolTreeB FromEntries(IEnumerable<SymbolEntry> entries) {
            var arr = entries?.ToImmutableArray() ?? ImmutableArray<SymbolEntry>.Empty;
            // Name table
            var canonical = new List<string>();
            var aliasToId = new Dictionary<string, int>(StringComparer.Ordinal);
            int EnsureName(string canon) {
                if (!aliasToId.TryGetValue(canon, out var id)) {
                    id = canonical.Count;
                    canonical.Add(canon);
                    aliasToId[canon] = id;
                }
                return id;
            }

            // Mutable node list
            var nodes = new List<(int NameId, int Parent, int FirstChild, int NextSibling, NodeKind Kind, List<int> Entries)>();
            var keyToIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            int NewNode(int nameId, int parent, NodeKind kind) {
                var idx = nodes.Count;
                nodes.Add((nameId, parent, -1, -1, kind, new List<int>()));
                if (parent >= 0) {
                    var p = nodes[parent];
                    nodes[parent] = (p.NameId, p.Parent, idx, p.FirstChild, p.Kind, p.Entries);
                    nodes[idx] = (nameId, parent, -1, p.FirstChild, kind, nodes[idx].Entries);
                }
                return idx;
            }
            int GetOrAddNode(int parent, int nameId, NodeKind kind) {
                var key = parent.ToString() + "|" + nameId.ToString() + "|" + ((int)kind).ToString();
                if (!keyToIndex.TryGetValue(key, out var idx)) {
                    idx = NewNode(nameId, parent, kind);
                    keyToIndex[key] = idx;
                }
                return idx;
            }

            // Root
            int root = NewNode(-1, -1, NodeKind.Namespace);

            // Alias buckets (store relations with flags)
            var exactBuckets = new Dictionary<string, List<AliasRelation>>(StringComparer.Ordinal);
            var nonExactBuckets = new Dictionary<string, List<AliasRelation>>(StringComparer.Ordinal);
            static void Bucket(Dictionary<string, List<AliasRelation>> dict, string alias, int nodeIdx, AliasKinds kind) {
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
                        var id = EnsureName(canon);
                        var idx = GetOrAddNode(parent, id, NodeKind.Namespace);
                        // aliases for this namespace segment
                        Bucket(exactBuckets, canon, idx, AliasKinds.None);
                        var lower = canon.ToLowerInvariant();
                        if (!string.Equals(lower, canon, StringComparison.Ordinal)) {
                            Bucket(nonExactBuckets, lower, idx, AliasKinds.IgnoreCase);
                        }

                        parent = idx;
                    }
                    if (nsSegments.Length > 0) {
                        nodes[parent].Entries.Add(ei);
                    }
                    // DocId as exact alias for namespace (supports duplicates across assemblies)
                    if (!string.IsNullOrEmpty(e.SymbolId) && e.SymbolId.StartsWith("N:", StringComparison.Ordinal)) {
                        Bucket(exactBuckets, e.SymbolId, parent, AliasKinds.None);
                    }

                }
                if ((e.Kind & SymbolKinds.Type) != 0) {
                    int parent = root;
                    foreach (var ns in SplitNs(e.ParentNamespace)) {

                        var id = EnsureName(ns);
                        parent = GetOrAddNode(parent, id, NodeKind.Namespace);
                        Bucket(exactBuckets, ns, parent, AliasKinds.None);
                        var lowerNs = ns.ToLowerInvariant();
                        if (!string.Equals(lowerNs, ns, StringComparison.Ordinal)) {
                            Bucket(nonExactBuckets, lowerNs, parent, AliasKinds.IgnoreCase);
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
                        var id = EnsureName(bn);
                        bool isLast = i == typeSegs.Length - 1;
                        int idx;
                        if (isLast) {
                            idx = NewNode(id, lastTypeNode, NodeKind.Type); // allow duplicates per entry
                        } else {
                            idx = GetOrAddNode(lastTypeNode, id, NodeKind.Type);
                        }
                        // aliases for this type segment (apply to actual created node)
                        if (ar > 0) {
                            // Exact: DocId-like form only (bn`n)
                            var docIdSeg = bn + "`" + ar.ToString();
                            Bucket(exactBuckets, docIdSeg, idx, AliasKinds.None);

                            // NonExact:
                            // 1) Generic base name anchors (Prefix semantics)
                            Bucket(nonExactBuckets, bn, idx, AliasKinds.OmitArity);
                            var lowerBn = bn.ToLowerInvariant();
                            if (!string.Equals(lowerBn, bn, StringComparison.Ordinal)) {
                                Bucket(nonExactBuckets, lowerBn, idx, AliasKinds.OmitArity | AliasKinds.IgnoreCase);
                            }
                            // 2) Case-insensitive exact for DocId-like form (lower-cased)
                            var lowerDocIdSeg = docIdSeg.ToLowerInvariant();
                            if (!string.Equals(lowerDocIdSeg, docIdSeg, StringComparison.Ordinal)) {
                                Bucket(nonExactBuckets, lowerDocIdSeg, idx, AliasKinds.IgnoreCase);
                            }
                        } else {
                            // Non-generic simple name
                            Bucket(exactBuckets, bn, idx, AliasKinds.None);
                            var lowerBn = bn.ToLowerInvariant();
                            if (!string.Equals(lowerBn, bn, StringComparison.Ordinal)) {
                                Bucket(nonExactBuckets, lowerBn, idx, AliasKinds.IgnoreCase);
                            }
                        }
                        lastTypeNode = idx;
                    }
                    nodes[lastTypeNode].Entries.Add(ei);
                    // DocId as exact alias for type (supports duplicates across assemblies)
                    if (!string.IsNullOrEmpty(e.SymbolId) && e.SymbolId.StartsWith("T:", StringComparison.Ordinal)) {
                        Bucket(exactBuckets, e.SymbolId, lastTypeNode, AliasKinds.None);
                    }

                }
            }

            // Seal nodes (NodeB keeps a single entry reference)
            var sealedNodes = new List<NodeB>(nodes.Count);
            for (int i = 0; i < nodes.Count; i++) {
                var n = nodes[i];
                var entry = n.Entries.Count > 0 ? arr[n.Entries[0]] : null;
                sealedNodes.Add(new NodeB(n.NameId, n.Parent, n.FirstChild, n.NextSibling, n.Kind, entry));
            }

            // Seal tables with NodeId de-dup and flags OR aggregation
            var nameTable = new NameTable(canonical.ToImmutableArray(), aliasToId);

            static Dictionary<string, ImmutableArray<AliasRelation>> SealBuckets(Dictionary<string, List<AliasRelation>> buckets) {
                var result = new Dictionary<string, ImmutableArray<AliasRelation>>(StringComparer.Ordinal);
                foreach (var kv in buckets) {
                    var perNode = new Dictionary<int, AliasKinds>();
                    foreach (var rel in kv.Value) {
                        if (perNode.TryGetValue(rel.NodeId, out var existing)) {
                            perNode[rel.NodeId] = existing | rel.Kind;
                        } else {
                            perNode[rel.NodeId] = rel.Kind;
                        }
                    }
                    var arr = perNode.Select(p => new AliasRelation(p.Value, p.Key)).ToImmutableArray();
                    result[kv.Key] = arr;
                }
                return result;
            }

            var exact = SealBuckets(exactBuckets);
            var nonExact = SealBuckets(nonExactBuckets);

            return new SymbolTreeB(
                sealedNodes.ToImmutableArray(),
                nameTable,
                exact,
                nonExact
            );
        }
    }
}

