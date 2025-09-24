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

        static (string bn, int ar) ParseName(string seg) {
            var (b, a, _) = SymbolNormalization.ParseGenericArity(seg);
            return (b, a);
        }

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
            // If the node already exists in the bucket, OR-merge flags to keep semantics consistent with FromEntries
            if (!bucket.IsDefaultOrEmpty) {
                for (int i = 0; i < bucket.Length; i++) {
                    var r = bucket[i];
                    if (r.NodeId == nodeId) {
                        var merged = new AliasRelation(r.Kind | flags, r.NodeId);
                        if (merged.Kind == r.Kind) { return bucket; /* no change */ }
                        var list0 = bucket.ToBuilder();
                        list0[i] = merged;
                        return list0.ToImmutable();
                    }
                }
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

        void AddAliasesForNode(int nodeId) {
            if (nodeId < 0 || nodeId >= nodes.Count) { return; }
            var n = nodes[nodeId];
            IEnumerable<AliasGeneration.AliasSpec> specs = n.Kind switch {
                NodeKind.Namespace => AliasGeneration.GetNamespaceAliases(n.Name),
                NodeKind.Type => AliasGeneration.GetTypeAliases(n.Name),
                _ => Array.Empty<AliasGeneration.AliasSpec>()
            };
            foreach (var spec in specs) {
                if (spec.IsExact) { AddAliasExact(spec.Key, nodeId, spec.Flags); }
                else { AddAliasNonExact(spec.Key, nodeId, spec.Flags); }
            }
        }

        void RemoveAliasesForNode(int nodeId) {
            if (nodeId < 0 || nodeId >= nodes.Count) { return; }
            var n = nodes[nodeId];
            IEnumerable<AliasGeneration.AliasSpec> specs = n.Kind switch {
                NodeKind.Namespace => AliasGeneration.GetNamespaceAliases(n.Name),
                NodeKind.Type => AliasGeneration.GetTypeAliases(n.Name),
                _ => Array.Empty<AliasGeneration.AliasSpec>()
            };
            foreach (var spec in specs) {
                if (spec.IsExact) { RemoveAliasExact(spec.Key, nodeId); }
                else { RemoveAliasNonExact(spec.Key, nodeId); }
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
            foreach (var typeKey in delta.TypeRemovals) {
                if (string.IsNullOrEmpty(typeKey.DocCommentId) || !typeKey.DocCommentId.StartsWith("T:", StringComparison.Ordinal)) { continue; }
                if (string.IsNullOrEmpty(typeKey.Assembly)) {
                    // 按契约 Assembly 不能为空；若为空则跳过以避免误删所有变体
                    DebugUtil.Print("SymbolTree.WithDelta", $"Warning: skip removal because Assembly is empty for docId={typeKey.DocCommentId}");
                    continue;
                }
                // Use the same segmentation semantics as QueryPreprocessor to correctly handle nested types with '+'
                var s = typeKey.DocCommentId[2..];
                var segs = SymbolNormalization.SplitSegmentsWithNested(s);
                var leaf = segs.Length > 0 ? segs[^1] : s;
                var (bn0, ar0) = ParseName(leaf);
                var aliasKey = ar0 > 0 ? (bn0 + "`" + ar0.ToString()) : bn0;
                // aliasKey 命中的是同名节点集合；其中可能包含多个不同 Assembly 的并行变体。
                if (exact.TryGetValue(aliasKey, out var rels) && !rels.IsDefaultOrEmpty) {
                    foreach (var r in rels) {
                        int nid = r.NodeId;
                        if (nid < 0 || nid >= nodes.Count) { continue; }
                        var entry = nodes[nid].Entry;
                        if (entry is null) { continue; }
                        // 精确匹配 (DocId, Assembly)；之前版本仅按 DocId 匹配会误删所有 Assembly 变体。
                        if (string.Equals(entry.DocCommentId, typeKey.DocCommentId, StringComparison.Ordinal) &&
                            string.Equals(entry.Assembly, typeKey.Assembly, StringComparison.Ordinal)) {
                            int nsAncestor = FindNearestNamespaceAncestor(nid); // capture before detach
                            if (nsAncestor > 0) {
                                DebugUtil.Print("SymbolTree.WithDelta", $"Type removal matched node={nid} name={nodes[nid].Name}, nsAncestorId={nsAncestor} nsName={nodes[nsAncestor].Name}");
                            }
                            DebugUtil.Print("SymbolTree.WithDelta", $"Removing type subtree nid={nid}, name={nodes[nid].Name}, docId={entry.DocCommentId}, asm={entry.Assembly}, nsAncestor={nsAncestor}");
                            RemoveAliasesSubtree(nid);
                            DetachNode(nid);
                            if (nsAncestor > 0) { cascadeCandidates.Add(nsAncestor); }
                        }
                        else if (string.Equals(entry.DocCommentId, typeKey.DocCommentId, StringComparison.Ordinal)) {
                            // DocId 相同但 Assembly 不同，显式记录跳过，方便诊断
                            DebugUtil.Print("SymbolTree.WithDelta", $"Skip removal for docId={entry.DocCommentId}: existingAsm={entry.Assembly} != targetAsm={typeKey.Assembly}");
                        }
                    }
                }
            }
        }

        // --- Adds/Upserts ---
        // Helper: ensure namespace chain exists; return last ns node id.
        int EnsureNamespaceChain(string[] segs) {
            int cur = 0; // root
            if (segs.Length == 0) { return cur; }
            string curNs = string.Empty; // build FQN progressively for Namespace entries
            for (int i = 0; i < segs.Length; i++) {
                var seg = segs[i];
                int next = FindChildByNameKind(cur, seg, NodeKind.Namespace);
                curNs = i == 0 ? seg : (curNs + "." + seg);
                var parentNs = curNs.Contains('.') ? curNs.Substring(0, curNs.LastIndexOf('.')) : string.Empty;
                if (next < 0) {
                    // Create namespace node with a concrete Namespace entry so that N: queries work incrementally
                    var nsEntry = new SymbolEntry(
                        DocCommentId: "N:" + curNs,
                        Assembly: string.Empty,
                        Kind: SymbolKinds.Namespace,
                        ParentNamespaceNoGlobal: parentNs,
                        FqnNoGlobal: curNs,
                        FqnLeaf: seg
                    );
                    next = NewChild(cur, seg, NodeKind.Namespace, nsEntry);
                    AddAliasesForNode(next);
                }
                else {
                    // If this namespace node was created previously without an entry, backfill it now
                    var n = nodes[next];
                    if (n.Entry is null) {
                        var nsEntry = new SymbolEntry(
                            DocCommentId: "N:" + curNs,
                            Assembly: string.Empty,
                            Kind: SymbolKinds.Namespace,
                            ParentNamespaceNoGlobal: parentNs,
                            FqnNoGlobal: curNs,
                            FqnLeaf: seg
                        );
                        ReplaceNode(next, new NodeB(n.Name, n.Parent, n.FirstChild, n.NextSibling, n.Kind, nsEntry));
                    }
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
                if (!(e.DocCommentId?.StartsWith("T:", StringComparison.Ordinal) == true)) {
                    DebugUtil.Print("SymbolTree.WithDelta", $"Warning: TypeAdd without 'T:' DocId. Falling back to FqnNoGlobal. Name={e.FqnNoGlobal} Assembly={e.Assembly}");
                }
                var s = e.DocCommentId?.StartsWith("T:", StringComparison.Ordinal) == true ? e.DocCommentId![2..] : e.FqnNoGlobal;
                var allSegs = SymbolNormalization.SplitSegmentsWithNested(s);
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
                        AddAliasesForNode(child);
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
                            AddAliasesForNode(child);
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
                if (n.Kind == NodeKind.Type || n.Kind == NodeKind.Namespace) { RemoveAliasesForNode(id); }
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
                    bool hasType = NamespaceHasAnyTypeEntry(cur);
                    DebugUtil.Print("SymbolTree.WithDelta", $"Cascade check nsId={cur} nsName={nodes[cur].Name}, hasType={hasType}");
                    if (!hasType) {
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
                foreach (var spec in AliasGeneration.GetNamespaceAliases(seg)) {
                    if (spec.IsExact) { Bucket(exactBuckets, spec.Key, idx, spec.Flags); } else { Bucket(nonExactBuckets, spec.Key, idx, spec.Flags); }
                }
                parent = idx;
            }
            return parent;
        }

        // Build type chain from type segments (bn or bn`n), add alias buckets; returns the last type node index
        int BuildTypeChainAndAliases(int namespaceParent, string[] typeSegs) {
            int lastTypeNode = namespaceParent;
            for (int i = 0; i < typeSegs.Length; i++) {
                var (bn, ar, _) = SymbolNormalization.ParseGenericArity(typeSegs[i]);
                var nodeName = ar > 0 ? bn + "`" + ar.ToString() : bn;
                bool isLast = i == typeSegs.Length - 1;
                int idx = isLast ? NewNode(nodeName, lastTypeNode, NodeKind.Type) : GetOrAddNode(lastTypeNode, nodeName, NodeKind.Type);
                foreach (var spec in AliasGeneration.GetTypeAliases(nodeName)) {
                    if (spec.IsExact) { Bucket(exactBuckets, spec.Key, idx, spec.Flags); } else { Bucket(nonExactBuckets, spec.Key, idx, spec.Flags); }
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
            var allSegs = SymbolNormalization.SplitSegmentsWithNested(s);
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
