using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.InteropServices;

using Atelia.Diagnostics;
using CodeCortexV2.Abstractions;

namespace CodeCortexV2.Index.SymbolTreeInternal;

partial class SymbolTreeB {
    /// <summary>
    /// Apply a pre-normalized <see cref="SymbolsDelta"/> to the current immutable tree and return a new snapshot.
    /// Expectations:
    /// - Pure application: do not perform global scans to decide namespace removals/additions; trust the delta closure.
    /// - Locality: time/memory costs should scale with the size of <paramref name="delta"/>, not the whole index.
    /// - Idempotency: applying the same delta repeatedly should not change the resulting state.
    /// - Optional defensive checks: implementations MAY include lightweight validations limited to impacted subtrees
    ///   (guarded by a debug/consistency option) and emit diagnostics via DebugUtil. No full-index traversal.
    /// </summary>
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
