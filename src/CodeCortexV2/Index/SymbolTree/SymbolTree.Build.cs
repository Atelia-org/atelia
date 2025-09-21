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
    /// Apply a leaf-oriented <see cref="SymbolsDelta"/> to the current immutable tree and return a new snapshot.
    /// Expectations (namespace fields on delta are deprecated):
    /// - Internally handle namespace chain materialization for added types and cascading deletion for empty namespaces,
    ///   limited to impacted subtrees; avoid full-index scans in production.
    /// - Locality: time/memory costs should scale with the size of <paramref name="delta"/>, not the whole index.
    /// - Idempotency: applying the same delta repeatedly should not change the resulting state.
    /// - Optional defensive checks: lightweight validations and DebugUtil diagnostics are allowed.
    /// </summary>
    public ISymbolIndex WithDelta(SymbolsDelta delta) {
        if (delta is null) { return this; }

        // P0 implementation notes:
        // - Localized edits on a mutable copy of nodes (List<NodeB>), patching only affected parents/siblings.
        // - Alias maps: for simplicity, we shallow-copy whole dictionaries, then replace mutated buckets per-key.
        //   This is O(#alias-keys) copy once; acceptable for P0 and will be optimized later with true COW.

        var nodes = _nodes.ToList(); // node array copy
        if (nodes.Count == 0) {
            // Corner: empty snapshot → synthesize namespaces from TypeAdds and build from flat entries.
            var typeAdds = delta.TypeAdds ?? Array.Empty<SymbolEntry>();
            if (typeAdds.Count > 0) {
                var all = new List<SymbolEntry>(typeAdds.Count * 2);
                // Synthesize namespace entries from types (doc-id "N:<ns>") so that namespace search works without NamespaceAdds.
                var nsSeen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var t in typeAdds) {
                    if ((t.Kind & SymbolKinds.Type) == 0) { continue; }
                    var ns = t.ParentNamespaceNoGlobal;
                    if (string.IsNullOrEmpty(ns)) { continue; }
                    // chain: A, A.B, A.B.C ...
                    var parts = ns.Split('.', StringSplitOptions.RemoveEmptyEntries);
                    string cur = string.Empty;
                    for (int i = 0; i < parts.Length; i++) {
                        cur = i == 0 ? parts[0] : cur + "." + parts[i];
                        var docId = "N:" + cur;
                        if (nsSeen.Add(docId)) {
                            var lastDot = cur.LastIndexOf('.');
                            var parent = lastDot > 0 ? cur.Substring(0, lastDot) : string.Empty;
                            all.Add(
                                new SymbolEntry(
                                    DocCommentId: docId,
                                    Assembly: string.Empty,
                                    Kind: SymbolKinds.Namespace,
                                    ParentNamespaceNoGlobal: parent,
                                    FqnNoGlobal: cur,
                                    FqnLeaf: parts[i]
                                )
                            );
                        }
                    }
                }
                all.AddRange(typeAdds);
                return FromEntries(all);
            }
            return this; // nothing to do
        }

        var exact = new Dictionary<string, ImmutableArray<AliasRelation>>(_exactAliasToNodes, _exactAliasToNodes.Comparer);
        var nonExact = new Dictionary<string, ImmutableArray<AliasRelation>>(_nonExactAliasToNodes, _nonExactAliasToNodes.Comparer);

        // --- Helpers ---
        static string[] SplitNs(string? ns)
            => string.IsNullOrEmpty(ns) ? Array.Empty<string>() : ns!.Split('.', StringSplitOptions.RemoveEmptyEntries);

        static (string bn, int ar) ParseName(string seg) => QueryPreprocessor.ParseTypeSegment(seg);

        int FindChildByNameKind(int parent, string name, NodeKind kind) {
            if (parent < 0 || parent >= nodes.Count) { return -1; }
            int c = nodes[parent].FirstChild;
            while (c >= 0) {
                var n = nodes[c];
                if (n.Kind == kind && string.Equals(n.Name, name, StringComparison.Ordinal)) { return c; }
                c = n.NextSibling;
            }
            return -1;
        }

        // note: previous-sibling lookup folded into DetachNode to keep surface minimal for P0

        void ReplaceNode(int idx, NodeB n)
            => nodes[idx] = n;

        int NewChild(int parent, string name, NodeKind kind, SymbolEntry? entry) {
            int oldFirst = nodes[parent].FirstChild;
            int idx = nodes.Count;
            nodes.Add(new NodeB(name, parent, -1, oldFirst, kind, entry));
            // Patch parent to point to the new child as head
            var p = nodes[parent];
            ReplaceNode(parent, new NodeB(p.Name, p.Parent, idx, p.NextSibling, p.Kind, p.Entry));
            return idx;
        }

        void DetachNode(int nodeId) {
            if (nodeId <= 0 || nodeId >= nodes.Count) { return; /* never remove root (0) */ }
            var n = nodes[nodeId];
            int parent = n.Parent;
            if (parent < 0) { return; }
            int first = nodes[parent].FirstChild;
            if (first == nodeId) {
                // update parent's FirstChild
                var next = n.NextSibling;
                var p = nodes[parent];
                ReplaceNode(parent, new NodeB(p.Name, p.Parent, next, p.NextSibling, p.Kind, p.Entry));
            }
            else {
                // find previous sibling and skip
                int cur = first;
                while (cur >= 0) {
                    var c = nodes[cur];
                    if (c.NextSibling == nodeId) {
                        ReplaceNode(cur, new NodeB(c.Name, c.Parent, c.FirstChild, n.NextSibling, c.Kind, c.Entry));
                        break;
                    }
                    cur = c.NextSibling;
                }
            }

            // make the node a tombstone (detached)
            ReplaceNode(nodeId, new NodeB(n.Name, -1, n.FirstChild, -1, n.Kind, null));
        }

        // --- Alias helpers ---
        static ImmutableArray<AliasRelation> RemoveAliasFromBucket(ImmutableArray<AliasRelation> bucket, int nodeId) {
            if (bucket.IsDefaultOrEmpty) { return bucket; }
            var list = new List<AliasRelation>(bucket.Length);
            foreach (var r in bucket) { if (r.NodeId != nodeId) { list.Add(r); } }
            return list.Count == bucket.Length ? bucket : list.ToImmutableArray();
        }

        static ImmutableArray<AliasRelation> AddAliasToBucket(ImmutableArray<AliasRelation> bucket, int nodeId, MatchFlags flags) {
            if (!bucket.IsDefaultOrEmpty) {
                foreach (var r in bucket) { if (r.NodeId == nodeId) { return bucket; } }
            }
            var list = bucket.IsDefaultOrEmpty ? new List<AliasRelation>() : bucket.ToList();
            list.Add(new AliasRelation(flags, nodeId));
            return list.ToImmutableArray();
        }

        void AddAliasExact(string key, int nodeId, MatchFlags flags = MatchFlags.None) {
            if (string.IsNullOrEmpty(key)) { return; }
            exact[key] = AddAliasToBucket(exact.TryGetValue(key, out var b) ? b : default, nodeId, flags);
        }
        void AddAliasNonExact(string key, int nodeId, MatchFlags flags) {
            if (string.IsNullOrEmpty(key)) { return; }
            nonExact[key] = AddAliasToBucket(nonExact.TryGetValue(key, out var b) ? b : default, nodeId, flags);
        }
        void RemoveAliasExact(string key, int nodeId) {
            if (string.IsNullOrEmpty(key)) { return; }
            if (exact.TryGetValue(key, out var b)) {
                var nb = RemoveAliasFromBucket(b, nodeId);
                if (!nb.IsDefaultOrEmpty) { exact[key] = nb; } else { exact.Remove(key); }
            }
        }
        void RemoveAliasNonExact(string key, int nodeId) {
            if (string.IsNullOrEmpty(key)) { return; }
            if (nonExact.TryGetValue(key, out var b)) {
                var nb = RemoveAliasFromBucket(b, nodeId);
                if (!nb.IsDefaultOrEmpty) { nonExact[key] = nb; } else { nonExact.Remove(key); }
            }
        }

        void AddAliasesForNamespaceNode(int nodeId) {
            var seg = nodes[nodeId].Name;
            AddAliasExact(seg, nodeId, MatchFlags.None);
            var lower = seg.ToLowerInvariant();
            if (!string.Equals(lower, seg, StringComparison.Ordinal)) {
                AddAliasNonExact(lower, nodeId, MatchFlags.IgnoreCase);
            }
        }

        void AddAliasesForTypeNode(int nodeId) {
            var seg = nodes[nodeId].Name; // bn or bn`n
            var (bn, ar) = ParseName(seg);
            if (ar > 0) {
                // exact: bn`n
                AddAliasExact(seg, nodeId, MatchFlags.None);
                // non-exact: bn (arity-insensitive), lower(bn), lower(bn`n)
                AddAliasNonExact(bn, nodeId, MatchFlags.IgnoreGenericArity);
                var lowerBn = bn.ToLowerInvariant();
                if (!string.Equals(lowerBn, bn, StringComparison.Ordinal)) {
                    AddAliasNonExact(lowerBn, nodeId, MatchFlags.IgnoreGenericArity | MatchFlags.IgnoreCase);
                }
                var lowerSeg = seg.ToLowerInvariant();
                if (!string.Equals(lowerSeg, seg, StringComparison.Ordinal)) {
                    AddAliasNonExact(lowerSeg, nodeId, MatchFlags.IgnoreCase);
                }
            }
            else {
                // non-generic simple
                AddAliasExact(bn, nodeId, MatchFlags.None);
                var lowerBn = bn.ToLowerInvariant();
                if (!string.Equals(lowerBn, bn, StringComparison.Ordinal)) {
                    AddAliasNonExact(lowerBn, nodeId, MatchFlags.IgnoreCase);
                }
            }
        }

        void RemoveAliasesForNamespaceNode(int nodeId) {
            var seg = nodes[nodeId].Name;
            RemoveAliasExact(seg, nodeId);
            var lower = seg.ToLowerInvariant();
            if (!string.Equals(lower, seg, StringComparison.Ordinal)) {
                RemoveAliasNonExact(lower, nodeId);
            }
        }

        void RemoveAliasesForTypeNode(int nodeId) {
            var seg = nodes[nodeId].Name; // bn or bn`n
            var (bn, ar) = ParseName(seg);
            if (ar > 0) {
                RemoveAliasExact(seg, nodeId);
                RemoveAliasNonExact(bn, nodeId);
                var lowerBn = bn.ToLowerInvariant();
                if (!string.Equals(lowerBn, bn, StringComparison.Ordinal)) {
                    RemoveAliasNonExact(lowerBn, nodeId);
                }
                var lowerSeg = seg.ToLowerInvariant();
                if (!string.Equals(lowerSeg, seg, StringComparison.Ordinal)) {
                    RemoveAliasNonExact(lowerSeg, nodeId);
                }
            }
            else {
                RemoveAliasExact(bn, nodeId);
                var lowerBn = bn.ToLowerInvariant();
                if (!string.Equals(lowerBn, bn, StringComparison.Ordinal)) {
                    RemoveAliasNonExact(lowerBn, nodeId);
                }
            }
        }

        // --- Removals ---
        // Types: use last-segment exact alias bucket to find candidates, then match full docId via Entry.DocCommentId.
        var cascadeCandidates = new HashSet<int>(); // namespace nodeIds to check later

        int FindNearestNamespaceAncestor(int nodeId) {
            int cur = (nodeId >= 0 && nodeId < nodes.Count) ? nodes[nodeId].Parent : -1;
            while (cur >= 0 && nodes[cur].Kind != NodeKind.Namespace) {
                cur = nodes[cur].Parent;
            }
            return cur; // -1 if none, 0 is root
        }

        if (delta.TypeRemovals is not null) {
            DebugUtil.Print("SymbolTree.WithDelta", $"TypeRemovals detail: [{string.Join(", ", delta.TypeRemovals.Take(8))}] total={delta.TypeRemovals.Count}");
            foreach (var docId in delta.TypeRemovals) {
                if (string.IsNullOrEmpty(docId) || !docId.StartsWith("T:", StringComparison.Ordinal)) { continue; }
                var s = docId[2..];
                int dot = s.LastIndexOf('.');
                var lastSeg = dot >= 0 ? s[(dot + 1)..] : s; // bn or bn`n
                if (_exactAliasToNodes.TryGetValue(lastSeg, out var rels) && !rels.IsDefaultOrEmpty) {
                    foreach (var r in rels) {
                        int nid = r.NodeId;
                        if (nid < 0 || nid >= nodes.Count) { continue; }
                        var entry = nodes[nid].Entry;
                        if (entry is not null && string.Equals(entry.DocCommentId, docId, StringComparison.Ordinal)) {
                            // capture namespace ancestor before detaching
                            int nsAncestor = FindNearestNamespaceAncestor(nid);
                            // remove aliases first, then detach
                            DebugUtil.Print("SymbolTree.WithDelta", $"Removing type node nid={nid}, name={nodes[nid].Name}, docId={entry.DocCommentId}, nsAncestor={nsAncestor}");
                            RemoveAliasesForTypeNode(nid);
                            DetachNode(nid);
                            if (nsAncestor > 0) { cascadeCandidates.Add(nsAncestor); }
                        }
                    }
                }
            }
        }

        // --- Adds/Upserts ---
        // Helper: ensure namespace chain exists; return last ns node id.
        int EnsureNamespaceChain(string[] segs) {
            int cur = 0; // root
            foreach (var seg in segs) {
                int next = FindChildByNameKind(cur, seg, NodeKind.Namespace);
                if (next < 0) {
                    next = NewChild(cur, seg, NodeKind.Namespace, null);
                    AddAliasesForNamespaceNode(next);
                }
                cur = next;
            }
            return cur;
        }

        if (delta.TypeAdds is not null) {
            foreach (var e in delta.TypeAdds) {
                bool isType = (e.Kind & SymbolKinds.Type) != 0;
                if (!isType) { continue; }
                // 1) Ensure namespaces
                var nsSegs = SplitNs(e.ParentNamespaceNoGlobal);
                int nsParent = EnsureNamespaceChain(nsSegs);

                // 2) Build/ensure type chain from docId
                var s = e.DocCommentId?.StartsWith("T:", StringComparison.Ordinal) == true ? e.DocCommentId![2..] : e.FqnNoGlobal;
                var allSegs = QueryPreprocessor.SplitSegments(s);
                int skip = nsSegs.Length;
                if (skip < 0 || skip > allSegs.Length) { skip = 0; }
                string[] typeSegs = allSegs.Skip(skip).ToArray();
                int parent = nsParent;
                for (int i = 0; i < typeSegs.Length; i++) {
                    var (bn, ar) = ParseName(typeSegs[i]);
                    var nodeName = ar > 0 ? bn + "`" + ar.ToString() : bn;
                    bool isLast = i == typeSegs.Length - 1;
                    int child = FindChildByNameKind(parent, nodeName, NodeKind.Type);
                    if (child < 0) {
                        child = isLast ? NewChild(parent, nodeName, NodeKind.Type, e) : NewChild(parent, nodeName, NodeKind.Type, null);
                        AddAliasesForTypeNode(child);
                    }
                    else if (isLast) {
                        // Upsert semantics: if an existing node matches docId+assembly, update its entry; otherwise create a sibling
                        var existing = nodes[child];
                        if (existing.Entry is not null &&
                            string.Equals(existing.Entry.DocCommentId, e.DocCommentId, StringComparison.Ordinal) &&
                            string.Equals(existing.Entry.Assembly, e.Assembly, StringComparison.Ordinal)) {
                            ReplaceNode(child, new NodeB(existing.Name, existing.Parent, existing.FirstChild, existing.NextSibling, existing.Kind, e));
                        }
                        else {
                            // create a new sibling at head for this assembly variant
                            child = NewChild(parent, nodeName, NodeKind.Type, e);
                            AddAliasesForTypeNode(child);
                        }
                    }
                    parent = child;
                }
            }
        }

        // --- Cascading namespace deletion (post-phase) ---
        bool NamespaceHasAnyTypeEntry(int nsNodeId) {
            if (nsNodeId <= 0 || nsNodeId >= nodes.Count) { return false; }
            // Traverse descendants and detect any Type node with non-null Entry
            var stack = new Stack<int>();
            // seed: direct children of the namespace
            int c = nodes[nsNodeId].FirstChild;
            while (c >= 0) {
                stack.Push(c);
                c = nodes[c].NextSibling;
            }
            while (stack.Count > 0) {
                int id = stack.Pop();
                var n = nodes[id];
                if (n.Kind == NodeKind.Type && n.Entry is not null) { return true; }
                // continue DFS
                int ch = n.FirstChild;
                while (ch >= 0) {
                    stack.Push(ch);
                    ch = nodes[ch].NextSibling;
                }
            }
            return false;
        }

        void RemoveAliasesSubtree(int rootId) {
            if (rootId < 0 || rootId >= nodes.Count) { return; }
            var stack = new Stack<int>();
            stack.Push(rootId);
            while (stack.Count > 0) {
                int id = stack.Pop();
                var n = nodes[id];
                if (n.Kind == NodeKind.Type) { RemoveAliasesForTypeNode(id); }
                else { RemoveAliasesForNamespaceNode(id); }
                int ch = n.FirstChild;
                while (ch >= 0) {
                    stack.Push(ch);
                    ch = nodes[ch].NextSibling;
                }
            }
        }

        int deletedNsCount = 0;
        if (cascadeCandidates.Count > 0) {
            foreach (var cand in cascadeCandidates) {
                int cur = cand;
                while (cur > 0) { // stop at root (0)
                    if (!NamespaceHasAnyTypeEntry(cur)) {
                        // remove aliases for the whole namespace subtree, then detach this namespace node
                        int parentBefore = nodes[cur].Parent;
                        RemoveAliasesSubtree(cur);
                        DetachNode(cur);
                        deletedNsCount++;
                        cur = parentBefore;
                    }
                    else { break; }
                }
            }
        }

        DebugUtil.Print("SymbolTree.WithDelta", $"TypeAdds={delta.TypeAdds?.Count ?? 0}, TypeRemovals={delta.TypeRemovals?.Count ?? 0}, CascadeCandidates={cascadeCandidates.Count}, DeletedNamespaces={deletedNsCount}");

        // Produce the new immutable snapshot
        var newTree = new SymbolTreeB(
            nodes.ToImmutableArray(),
            exact,
            nonExact
        );
        return newTree;
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
            string[] nsSegments = isType ? SplitNs(e.ParentNamespaceNoGlobal).ToArray() : SplitNs(e.FqnNoGlobal).ToArray();
            int parent = BuildNamespaceChainAndAliases(root, nsSegments);

            if (isNs) {
                // Namespace entry: attach to the namespace node
                if (nsSegments.Length > 0) {
                    nodes[parent].Entries.Add(ei);
                }
                continue;
            }

            // 2) Build type chain from DocId strictly ("T:"-prefixed)
            System.Diagnostics.Debug.Assert(!string.IsNullOrEmpty(e.DocCommentId) && e.DocCommentId.StartsWith("T:", StringComparison.Ordinal), "Type entries must have DocId starting with 'T:'");
            var s = e.DocCommentId[2..];
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
