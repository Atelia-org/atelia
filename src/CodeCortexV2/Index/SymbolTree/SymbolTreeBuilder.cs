using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

using Atelia.Diagnostics;
using CodeCortexV2.Abstractions;

namespace CodeCortexV2.Index.SymbolTreeInternal;

/// <summary>
/// Mutable construction surface shared by <see cref="SymbolTreeB.WithDelta"/>。
/// 承载节点数组、别名桶与常用辅助操作，后续阶段将进一步拓展至完整 Builder 生命周期。
/// 当前阶段仅由 <see cref="SymbolTreeB.WithDelta"/> 使用，保持逻辑不变。调用方必须遵守 <see cref="SymbolsDelta"/>
/// 的排序契约（TypeAdds 按 DocCommentId.Length 升序、TypeRemovals 降序，DocId 起始为 "T:"）；当发现违背
/// 契约或父节点缺失时，本 Builder 会优先选择 fail-fast（Debug.Assert 或抛异常）。
/// </summary>
internal sealed class SymbolTreeBuilder {
    internal List<NodeB> Nodes { get; }
    internal Dictionary<string, ImmutableArray<AliasRelation>> ExactAliases { get; }
    internal Dictionary<string, ImmutableArray<AliasRelation>> NonExactAliases { get; }

    // Sentinel parent value (< -1) used to mark nodes stored in the freelist. This keeps the
    // invariant that any node with Parent < 0 is detached from the logical tree, while allowing
    // us to piggy-back FirstChild as the "next" pointer in the freelist chain.
    private const int FreeParentSentinel = -2;
    private int _freeHead;
    private int _freedThisDelta;
    private int _reusedThisDelta;

    private Dictionary<(string DocId, string Assembly), List<int>>? _entryReusePool;
    private Dictionary<(string DocId, string Assembly), int>? _entryReuseCursor;

    internal static SymbolTreeBuilder CreateEmpty()
        => new(
            new List<NodeB>(capacity: 1) {
                new NodeB(string.Empty, parent: -1, firstChild: -1, nextSibling: -1, NodeKind.Namespace, entry: null)
            },
            new Dictionary<string, ImmutableArray<AliasRelation>>(StringComparer.Ordinal),
            new Dictionary<string, ImmutableArray<AliasRelation>>(StringComparer.Ordinal),
            freeHead: -1
        );

    internal readonly record struct DeltaStats(
        int TypeAddCount,
        int TypeRemovalCount,
        int CascadeCandidateCount,
        int DeletedNamespaceCount,
        int ReusedNodeCount,
        int FreedNodeCount
    );

    internal SymbolTreeBuilder(
        List<NodeB> nodes,
        Dictionary<string, ImmutableArray<AliasRelation>> exactAliases,
        Dictionary<string, ImmutableArray<AliasRelation>> nonExactAliases,
        int freeHead
    ) {
        Nodes = nodes ?? throw new ArgumentNullException(nameof(nodes));
        ExactAliases = exactAliases ?? throw new ArgumentNullException(nameof(exactAliases));
        NonExactAliases = nonExactAliases ?? throw new ArgumentNullException(nameof(nonExactAliases));
        _freeHead = freeHead;
    }

    internal int FreeHead => _freeHead;

    internal DeltaStats ApplyDelta(SymbolsDelta delta) {
        if (delta is null) { return new DeltaStats(0, 0, 0, 0, 0, 0); }

        ValidateTypeAddsContract(delta.TypeAdds);
        ValidateTypeRemovalsContract(delta.TypeRemovals);

        _freedThisDelta = 0;
        _reusedThisDelta = 0;

        var cascadeCandidates = new HashSet<int>();
        ApplyTypeRemovals(delta.TypeRemovals, cascadeCandidates);
        ApplyTypeAdds(delta.TypeAdds);
        int deletedNamespaces = CascadeEmptyNamespaces(cascadeCandidates);

        return new DeltaStats(
            delta.TypeAdds?.Count ?? 0,
            delta.TypeRemovals?.Count ?? 0,
            cascadeCandidates.Count,
            deletedNamespaces,
            _reusedThisDelta,
            _freedThisDelta
        );
    }

    private static void ValidateTypeAddsContract(IReadOnlyList<SymbolEntry>? additions) {
        if (additions is null || additions.Count == 0) { return; }

        int previousLength = -1;
        for (int i = 0; i < additions.Count; i++) {
            var entry = additions[i];
            var docId = entry.DocCommentId;
            if (string.IsNullOrEmpty(docId) || !docId.StartsWith("T:", StringComparison.Ordinal)) { throw new InvalidOperationException($"TypeAdds[{i}] must have a DocCommentId starting with 'T:' (DocCommentId='{docId ?? "<null>"}')"); }
            if (string.IsNullOrWhiteSpace(entry.Assembly)) { throw new InvalidOperationException($"TypeAdds[{i}] '{docId}' must specify Assembly"); }

            int length = docId.Length;
            if (previousLength > length) { throw new InvalidOperationException($"TypeAdds must be sorted by ascending DocCommentId length. Violation at index {i} for '{docId}' (length={length}, previousLength={previousLength})."); }
            previousLength = length;
        }
    }

    private static void ValidateTypeRemovalsContract(IReadOnlyList<TypeKey>? removals) {
        if (removals is null || removals.Count == 0) { return; }

        int previousLength = int.MaxValue;
        for (int i = 0; i < removals.Count; i++) {
            var entry = removals[i];
            var docId = entry.DocCommentId;
            if (string.IsNullOrEmpty(docId) || !docId.StartsWith("T:", StringComparison.Ordinal)) { throw new InvalidOperationException($"TypeRemovals[{i}] must have a DocCommentId starting with 'T:' (DocCommentId='{docId ?? "<null>"}')"); }
            if (string.IsNullOrWhiteSpace(entry.Assembly)) { throw new InvalidOperationException($"TypeRemovals[{i}] '{docId}' must specify Assembly"); }

            int length = docId.Length;
            if (previousLength < length) { throw new InvalidOperationException($"TypeRemovals must be sorted by descending DocCommentId length. Violation at index {i} for '{docId}' (length={length}, previousLength={previousLength})."); }
            previousLength = length;
        }
    }

    private void ApplyTypeRemovals(IReadOnlyList<TypeKey>? removals, HashSet<int> cascadeCandidates) {
        if (removals is null || removals.Count == 0) { return; }

        DebugUtil.Print("SymbolTree.WithDelta", $"TypeRemovals detail: [{string.Join(", ", removals.Take(8))}] total={removals.Count}");

        foreach (var typeKey in removals) {
            DebugUtil.Print("SymbolTree.Removal.Trace", $"Processing removal: docId={typeKey.DocCommentId} assembly={typeKey.Assembly}");
            if (string.IsNullOrEmpty(typeKey.DocCommentId) || !typeKey.DocCommentId.StartsWith("T:", StringComparison.Ordinal)) { throw new InvalidOperationException($"TypeRemovals entry must have a DocCommentId starting with 'T:' (DocCommentId='{typeKey.DocCommentId ?? "<null>"}')"); }
            if (string.IsNullOrWhiteSpace(typeKey.Assembly)) { throw new InvalidOperationException($"TypeRemovals entry '{typeKey.DocCommentId}' must specify Assembly"); }

            var s = typeKey.DocCommentId[2..];
            var segs = SymbolNormalization.SplitSegmentsWithNested(s);
            DebugUtil.Print("SymbolTree.Removal.Trace", $"Segmented '{s}' into: [{string.Join(", ", segs)}]");
            var leaf = segs.Length > 0 ? segs[^1] : s;
            var (bn0, ar0) = ParseName(leaf);
            var aliasKey = ar0 > 0 ? (bn0 + "`" + ar0.ToString()) : bn0;
            DebugUtil.Print("SymbolTree.Removal.Trace", $"Generated aliasKey='{aliasKey}' from leaf='{leaf}' (bn={bn0}, ar={ar0})");

            if (ExactAliases.TryGetValue(aliasKey, out var rels) && !rels.IsDefaultOrEmpty) {
                DebugUtil.Print("SymbolTree.Removal.Trace", $"Found {rels.Length} candidates in alias bucket '{aliasKey}': [{string.Join(", ", rels.Select(r => $"nodeId={r.NodeId}"))}]");
                foreach (var r in rels) {
                    int nid = r.NodeId;
                    if (nid < 0 || nid >= Nodes.Count) {
                        DebugUtil.Print("SymbolTree.Removal.Trace", $"Skipping invalid nodeId={nid}");
                        continue;
                    }

                    var entry = Nodes[nid].Entry;
                    bool shouldRemove = false;
                    if (entry is not null) {
                        DebugUtil.Print("SymbolTree.Removal.Trace", $"Checking nodeId={nid}: docId='{entry.DocCommentId}' assembly='{entry.Assembly}' against target docId='{typeKey.DocCommentId}' assembly='{typeKey.Assembly}'");
                        if (string.Equals(entry.DocCommentId, typeKey.DocCommentId, StringComparison.Ordinal) &&
                            string.Equals(entry.Assembly, typeKey.Assembly, StringComparison.Ordinal)) {
                            shouldRemove = true;
                        }
                        else if (string.Equals(entry.DocCommentId, typeKey.DocCommentId, StringComparison.Ordinal)) {
                            DebugUtil.Print("SymbolTree.WithDelta", $"Skip removal for docId={entry.DocCommentId}: existingAsm={entry.Assembly} != targetAsm={typeKey.Assembly}");
                        }
                    }
                    else {
                        DebugUtil.Print("SymbolTree.Removal.Trace", $"Checking path node {nid} (name={Nodes[nid].Name}) for matching descendants");
                        if (HasMatchingDescendant(nid, typeKey.DocCommentId)) {
                            shouldRemove = true;
                            DebugUtil.Print("SymbolTree.Removal.Trace", $"Will remove path node {nid} because it has matching descendant");
                        }
                        else {
                            DebugUtil.Print("SymbolTree.Removal.Trace", $"Skipping nodeId={nid} with null entry (no matching descendants)");
                        }
                    }

                    if (shouldRemove) {
                        int nsAncestor = FindNearestNamespaceAncestor(nid);
                        if (nsAncestor > 0) {
                            DebugUtil.Print("SymbolTree.WithDelta", $"Type removal matched node={nid} name={Nodes[nid].Name}, nsAncestorId={nsAncestor} nsName={Nodes[nsAncestor].Name}");
                        }
                        DebugUtil.Print("SymbolTree.WithDelta", $"Removing type subtree nid={nid}, name={Nodes[nid].Name}, docId={entry?.DocCommentId ?? "null"}, asm={entry?.Assembly ?? "null"}, nsAncestor={nsAncestor}");
                        DebugUtil.Print("SymbolTree.Removal.Trace", $"About to call RemoveTypeSubtree for nid={nid}");
                        RemoveTypeSubtree(nid);
                        if (nsAncestor > 0) { cascadeCandidates.Add(nsAncestor); }
                    }
                    else if (entry is not null && string.Equals(entry.DocCommentId, typeKey.DocCommentId, StringComparison.Ordinal)) {
                        DebugUtil.Print("SymbolTree.WithDelta", $"Skip removal for docId={entry.DocCommentId}: existingAsm={entry.Assembly} != targetAsm={typeKey.Assembly}");
                    }
                }
            }
            else {
                DebugUtil.Print("SymbolTree.Removal.Trace", $"No candidates found for aliasKey='{aliasKey}' (bucket empty or missing)");
            }
        }
    }

    private void ApplyTypeAdds(IReadOnlyList<SymbolEntry>? additions) {
        if (additions is null || additions.Count == 0) { return; }

        var reusePool = new Dictionary<(string DocId, string Assembly), List<int>>();
        var materializedDocIds = new HashSet<string>(StringComparer.Ordinal);
        for (int idx = 0; idx < Nodes.Count; idx++) {
            var node = Nodes[idx];
            if (node.Parent < 0) { continue; }
            var entry = node.Entry;
            if (entry is null) { continue; }
            if (!string.IsNullOrEmpty(entry.DocCommentId) && entry.DocCommentId.StartsWith("T:", StringComparison.Ordinal)) {
                materializedDocIds.Add(entry.DocCommentId);
            }
            var key = (entry.DocCommentId ?? string.Empty, entry.Assembly ?? string.Empty);
            if (!reusePool.TryGetValue(key, out var list)) {
                list = new List<int>();
                reusePool[key] = list;
            }
            list.Add(idx);
        }

        _entryReusePool = reusePool;
        _entryReuseCursor = new Dictionary<(string DocId, string Assembly), int>();

        try {
            foreach (var e in additions) {
                if ((e.Kind & SymbolKinds.Type) == 0) { continue; }

                var nsSegs = SplitNamespace(e.ParentNamespaceNoGlobal);
                int nsParent = EnsureNamespaceChain(nsSegs);

                if (!(e.DocCommentId?.StartsWith("T:", StringComparison.Ordinal) == true)) { throw new InvalidOperationException($"TypeAdds entry must have a DocCommentId starting with 'T:' (DocCommentId='{e.DocCommentId ?? "<null>"}')"); }
                if (string.IsNullOrWhiteSpace(e.Assembly)) { throw new InvalidOperationException($"TypeAdds entry '{e.DocCommentId}' must specify Assembly"); }

                var s = e.DocCommentId[2..];
                var allSegs = SymbolNormalization.SplitSegmentsWithNested(s);
                int skip = nsSegs.Length;
                if (skip < 0 || skip > allSegs.Length) { skip = 0; }
                string[] typeSegs = allSegs.Skip(skip).ToArray();

                int currentParent = nsParent;
                for (int i = 0; i < typeSegs.Length; i++) {
                    var (bn, ar) = ParseName(typeSegs[i]);
                    var nodeName = ar > 0 ? bn + "`" + ar.ToString() : bn;
                    bool isLast = i == typeSegs.Length - 1;
                    DebugUtil.Print("SymbolTreeB.WithDelta", $"处理类型段 {i}: nodeName='{nodeName}', isLast={isLast}");

                    if (!isLast) {
                        int structuralParent = currentParent;
                        int structuralNode = FindTypeChildPreferStructural(structuralParent, nodeName);
                        SymbolEntry? removedEntry = null;

                        if (structuralNode < 0) {
                            structuralNode = NewChild(structuralParent, nodeName, NodeKind.Type, entry: null);
                            AddAliasesForNode(structuralNode);
                        }
                        else {
                            var existing = Nodes[structuralNode];
                            if (existing.Entry is SymbolEntry existingEntry) {
                                removedEntry = existingEntry;
                                RemoveNodeFromPool(existingEntry, structuralNode);
                                ReplaceNodeEntry(structuralNode, null, refreshAliases: false);
                            }
                        }

                        if (removedEntry is SymbolEntry preservedEntry) {
                            EnsureTypeEntryNode(structuralParent, nodeName, preservedEntry);
                        }

                        var intermediateDocId = BuildIntermediateTypeDocId(nsSegs, typeSegs, i);
                        if (!materializedDocIds.Contains(intermediateDocId)) { throw new InvalidOperationException($"TypeAdds entry '{e.DocCommentId}' depends on missing parent '{intermediateDocId}'. Ensure parent types are materialized earlier in the delta."); }

                        currentParent = structuralNode;
                        continue;
                    }

                    string docId = e.DocCommentId ?? string.Empty;
                    string assembly = e.Assembly ?? string.Empty;
                    int placeholderNode;
                    int targetNode;
                    if (TryReuseEntryNodeFromPool(e, out var reusedLeaf)) {
                        targetNode = reusedLeaf;
                        ReplaceNodeEntry(targetNode, e, refreshAliases: true);
                    }
                    else {
                        int entryNode = FindTypeEntryNode(currentParent, nodeName, docId, assembly, out placeholderNode);
                        if (entryNode >= 0) {
                            targetNode = entryNode;
                            ReplaceNodeEntry(targetNode, e, refreshAliases: true);
                        }
                        else if (placeholderNode >= 0) {
                            targetNode = placeholderNode;
                            ReplaceNodeEntry(targetNode, e, refreshAliases: true);
                        }
                        else {
                            targetNode = NewChild(currentParent, nodeName, NodeKind.Type, e);
                            AddAliasesForNode(targetNode);
                        }
                    }

                    materializedDocIds.Add(docId);
                }

                if (!string.IsNullOrEmpty(e.DocCommentId)) {
                    materializedDocIds.Add(e.DocCommentId);
                }
            }
        }
        finally {
            _entryReusePool = null;
            _entryReuseCursor = null;
        }
    }

    /// <summary>
    /// Cascade-delete empty namespace nodes after type removals.
    /// Relies on the invariant that <see cref="RemoveTypeSubtree"/> detaches every structural child,
    /// so an empty namespace is observable via <c>FirstChild &lt; 0</c> without scanning descendants.
    /// </summary>
    private int CascadeEmptyNamespaces(HashSet<int> cascadeCandidates) {
        if (cascadeCandidates.Count == 0) { return 0; }

        int deletedNsCount = 0;
        foreach (var cand in cascadeCandidates) {
            int cur = cand;
            while (cur > 0) {
                bool hasAnyChild = Nodes[cur].FirstChild >= 0;
                DebugUtil.Print("SymbolTree.WithDelta", $"Cascade check nsId={cur} nsName={Nodes[cur].Name}, hasChild={hasAnyChild}");
                if (!hasAnyChild) {
                    int parentBefore = Nodes[cur].Parent;
                    RemoveAliasesSubtree(cur);
                    DetachNode(cur);
                    deletedNsCount++;
                    cur = parentBefore;
                }
                else { break; }
            }
        }

        return deletedNsCount;
    }

    // --- Shared static helpers ---

    internal static string[] SplitNamespace(string? ns)
        => string.IsNullOrEmpty(ns)
            ? Array.Empty<string>()
            : ns!.Split('.', StringSplitOptions.RemoveEmptyEntries);

    internal static (string BaseName, int Arity) ParseName(string segment) {
        var (baseName, arity, _) = SymbolNormalization.ParseGenericArity(segment);
        return (baseName, arity);
    }

    private static string BuildIntermediateTypeDocId(string[] nsSegments, string[] typeSegments, int currentTypeIndex) {
        var nsPrefix = nsSegments.Length > 0 ? string.Join('.', nsSegments) + "." : string.Empty;
        var typePrefix = string.Join("+", typeSegments.Take(currentTypeIndex + 1));
        return "T:" + nsPrefix + typePrefix;
    }

    // --- Node helpers ---

    internal int FindChildByNameKind(int parent, string name, NodeKind kind) {
        if (parent < 0 || parent >= Nodes.Count) { return -1; }
        int current = Nodes[parent].FirstChild;
        while (current >= 0) {
            var node = Nodes[current];
            if (node.Kind == kind && string.Equals(node.Name, name, StringComparison.Ordinal)) { return current; }
            current = node.NextSibling;
        }
        return -1;
    }

    private int FindTypeChildPreferStructural(int parent, string name) {
        if (parent < 0 || parent >= Nodes.Count) { return -1; }
        int current = Nodes[parent].FirstChild;
        int fallback = -1;
        while (current >= 0) {
            var node = Nodes[current];
            if (node.Kind == NodeKind.Type && string.Equals(node.Name, name, StringComparison.Ordinal)) {
                if (node.Entry is null) { return current; }
                if (fallback < 0) { fallback = current; }
            }
            current = node.NextSibling;
        }
        return fallback;
    }

    private int FindTypeEntryNode(int parent, string name, string docId, string assembly, out int placeholderNode) {
        placeholderNode = -1;
        if (parent < 0 || parent >= Nodes.Count) { return -1; }
        string assemblyNorm = assembly ?? string.Empty;
        int current = Nodes[parent].FirstChild;
        while (current >= 0) {
            var node = Nodes[current];
            if (node.Kind == NodeKind.Type && string.Equals(node.Name, name, StringComparison.Ordinal)) {
                var entry = node.Entry;
                if (entry is not null && string.Equals(entry.DocCommentId, docId, StringComparison.Ordinal)) {
                    var entryAsm = entry.Assembly ?? string.Empty;
                    if (string.Equals(entryAsm, assemblyNorm, StringComparison.Ordinal)) { return current; }
                    if (string.IsNullOrEmpty(entryAsm) && placeholderNode < 0) {
                        placeholderNode = current;
                    }
                }
            }
            current = node.NextSibling;
        }
        return -1;
    }

    private void EnsureTypeEntryNode(int parent, string name, SymbolEntry entry) {
        if (TryReuseEntryNodeFromPool(entry, out var reusedNode)) {
            ReplaceNodeEntry(reusedNode, entry, refreshAliases: true);
            return;
        }

        string docId = entry.DocCommentId ?? string.Empty;
        string assembly = entry.Assembly ?? string.Empty;
        int existing = FindTypeEntryNode(parent, name, docId, assembly, out int placeholderNode);

        if (existing >= 0) {
            var existingNode = Nodes[existing];
            if (!ReferenceEquals(existingNode.Entry, entry)) {
                ReplaceNodeEntry(existing, entry, refreshAliases: true);
            }
            return;
        }

        if (placeholderNode >= 0) {
            ReplaceNodeEntry(placeholderNode, entry, refreshAliases: true);
            return;
        }

        int newNode = NewChild(parent, name, NodeKind.Type, entry);
        AddAliasesForNode(newNode);
    }

    /// <summary>
    /// Retrieve the next cached node (matched by DocId+Assembly) that can be reused for the provided entry.
    /// Nodes are collected during <see cref="ApplyTypeAdds"/> to minimize churn when types are updated.
    /// </summary>
    private bool TryReuseEntryNodeFromPool(SymbolEntry entry, out int nodeId) {
        nodeId = -1;
        if (_entryReusePool is null || _entryReuseCursor is null) { return false; }

        var key = (entry.DocCommentId ?? string.Empty, entry.Assembly ?? string.Empty);
        if (!_entryReusePool.TryGetValue(key, out var list) || list is null) { return false; }

        if (!_entryReuseCursor.TryGetValue(key, out var cursor)) { cursor = 0; }
        while (cursor < list.Count) {
            int candidate = list[cursor];
            cursor++;
            if (candidate >= 0 && candidate < Nodes.Count && Nodes[candidate].Parent >= 0) {
                _entryReuseCursor[key] = cursor;
                nodeId = candidate;
                return true;
            }
        }

        _entryReuseCursor[key] = cursor;
        return false;
    }

    /// <summary>
    /// Remove a node from the reuse pool once it has been repurposed, keeping cursor state consistent.
    /// </summary>
    private void RemoveNodeFromPool(SymbolEntry entry, int nodeId) {
        if (_entryReusePool is null || _entryReuseCursor is null) { return; }

        var key = (entry.DocCommentId ?? string.Empty, entry.Assembly ?? string.Empty);
        if (!_entryReusePool.TryGetValue(key, out var list) || list is null) { return; }

        int index = list.IndexOf(nodeId);
        if (index < 0) { return; }

        list.RemoveAt(index);
        if (_entryReuseCursor.TryGetValue(key, out var cursor) && cursor > index) {
            _entryReuseCursor[key] = cursor - 1;
        }
    }

    internal void ReplaceNode(int index, NodeB node)
        => Nodes[index] = node;

    /// <summary>
    /// Replace the <see cref="SymbolEntry"/> attached to an existing node and optionally rebuild its alias buckets.
    /// </summary>
    /// <param name="nodeId">Target node to mutate.</param>
    /// <param name="entry">New entry payload; <c>null</c> clears the node payload but leaves the structural shell.</param>
    /// <param name="refreshAliases">
    /// Set to <c>true</c> when alias membership may need deduplication (e.g., node reuse for a new symbol).
    /// Use <c>false</c> when only clearing an entry for a structural placeholder to avoid redundant churn.
    /// </param>
    private void ReplaceNodeEntry(int nodeId, SymbolEntry? entry, bool refreshAliases) {
        var existing = Nodes[nodeId];
        ReplaceNode(nodeId, new NodeB(existing.Name, existing.Parent, existing.FirstChild, existing.NextSibling, existing.Kind, entry));
        if (refreshAliases) {
            RefreshAliasesForNode(nodeId);
        }
    }

    internal int NewChild(int parent, string name, NodeKind kind, SymbolEntry? entry) {
        int oldFirst = Nodes[parent].FirstChild;
        var newNode = new NodeB(name, parent, firstChild: -1, nextSibling: oldFirst, kind, entry);

        int newIndex;
        if (TryPopFreeNode(out var reused)) {
            newIndex = reused;
            ReplaceNode(newIndex, newNode);
        }
        else {
            newIndex = Nodes.Count;
            Nodes.Add(newNode);
        }

        var parentNode = Nodes[parent];
        ReplaceNode(parent, new NodeB(parentNode.Name, parentNode.Parent, newIndex, parentNode.NextSibling, parentNode.Kind, parentNode.Entry));
        return newIndex;
    }

    internal void DetachNode(int nodeId) {
        if (nodeId <= 0 || nodeId >= Nodes.Count) { return; }
        var node = Nodes[nodeId];
        int parent = node.Parent;
        if (parent < 0) { return; }

        int firstChild = Nodes[parent].FirstChild;
        if (firstChild == nodeId) {
            int next = node.NextSibling;
            var parentNode = Nodes[parent];
            ReplaceNode(parent, new NodeB(parentNode.Name, parentNode.Parent, next, parentNode.NextSibling, parentNode.Kind, parentNode.Entry));
        }
        else {
            int current = firstChild;
            while (current >= 0) {
                var sibling = Nodes[current];
                if (sibling.NextSibling == nodeId) {
                    ReplaceNode(current, new NodeB(sibling.Name, sibling.Parent, sibling.FirstChild, node.NextSibling, sibling.Kind, sibling.Entry));
                    break;
                }
                current = sibling.NextSibling;
            }
        }

        ReplaceNode(nodeId, new NodeB(node.Name, -1, node.FirstChild, -1, node.Kind, null));
        ReleaseNode(nodeId);
    }

    private bool TryPopFreeNode(out int nodeId) {
        nodeId = -1;
        if (_freeHead < 0) { return false; }

        int current = _freeHead;
        if (current < 0 || current >= Nodes.Count) {
            _freeHead = -1;
            return false;
        }

        var freeNode = Nodes[current];
        if (freeNode.Parent != FreeParentSentinel) {
            _freeHead = -1;
            return false;
        }

        // Rehydrate the freelist head, relying on FirstChild as our "next" pointer.
        _freeHead = freeNode.FirstChild;
        nodeId = current;
        _reusedThisDelta++;
        return true;
    }

    private void ReleaseNode(int nodeId) {
        if (nodeId <= 0 || nodeId >= Nodes.Count) { return; }

        var existing = Nodes[nodeId];
        if (existing.Parent == FreeParentSentinel) { return; /* already released */ }

        // Overwrite the detached slot so future allocations can reuse it. We intentionally drop
        // name/entry metadata here; callers will rebuild those fields when assigning the node.
        ReplaceNode(nodeId,
            new NodeB(
                name: string.Empty,
                parent: FreeParentSentinel,
                firstChild: _freeHead,
                nextSibling: -1,
                kind: NodeKind.Type,
                entry: null
            )
        );
        _freeHead = nodeId;
        _freedThisDelta++;
    }

    // --- Alias helpers ---

    /// <summary>
    /// Rebuild the alias bucket entries for a node by removing and re-adding specs derived from <see cref="NodeB.Name"/>.
    /// This keeps alias state consistent when nodes are reused for different symbol entries.
    /// </summary>
    private void RefreshAliasesForNode(int nodeId) {
        if (nodeId < 0 || nodeId >= Nodes.Count) { return; }
        if (nodeId != 0 && Nodes[nodeId].Parent < 0) { return; }
        RemoveAliasesForNode(nodeId);
        AddAliasesForNode(nodeId);
    }

    private static ImmutableArray<AliasRelation> RemoveAliasFromBucket(ImmutableArray<AliasRelation> bucket, int nodeId) {
        if (bucket.IsDefaultOrEmpty) { return bucket; }
        var list = new List<AliasRelation>(bucket.Length);
        foreach (var relation in bucket) {
            if (relation.NodeId != nodeId) { list.Add(relation); }
        }
        return list.Count == bucket.Length ? bucket : list.ToImmutableArray();
    }

    private static ImmutableArray<AliasRelation> AddAliasToBucket(ImmutableArray<AliasRelation> bucket, int nodeId, MatchFlags flags) {
        if (!bucket.IsDefaultOrEmpty) {
            for (int i = 0; i < bucket.Length; i++) {
                var relation = bucket[i];
                if (relation.NodeId == nodeId) {
                    var merged = new AliasRelation(relation.Kind | flags, relation.NodeId);
                    if (merged.Kind == relation.Kind) { return bucket; }
                    var builder = bucket.ToBuilder();
                    builder[i] = merged;
                    return builder.ToImmutable();
                }
            }
        }

        var list = bucket.IsDefaultOrEmpty ? new List<AliasRelation>() : bucket.ToList();
        list.Add(new AliasRelation(flags, nodeId));
        return list.ToImmutableArray();
    }

    internal void AddAliasExact(string key, int nodeId, MatchFlags flags = MatchFlags.None) {
        if (string.IsNullOrEmpty(key)) { return; }
        ExactAliases[key] = AddAliasToBucket(ExactAliases.TryGetValue(key, out var bucket) ? bucket : default, nodeId, flags);
    }

    internal void AddAliasNonExact(string key, int nodeId, MatchFlags flags) {
        if (string.IsNullOrEmpty(key)) { return; }
        NonExactAliases[key] = AddAliasToBucket(NonExactAliases.TryGetValue(key, out var bucket) ? bucket : default, nodeId, flags);
    }

    internal void RemoveAliasExact(string key, int nodeId) {
        if (string.IsNullOrEmpty(key)) { return; }
        if (ExactAliases.TryGetValue(key, out var bucket)) {
            var next = RemoveAliasFromBucket(bucket, nodeId);
            if (!next.IsDefaultOrEmpty) { ExactAliases[key] = next; }
            else { ExactAliases.Remove(key); }
        }
    }

    internal void RemoveAliasNonExact(string key, int nodeId) {
        if (string.IsNullOrEmpty(key)) { return; }
        if (NonExactAliases.TryGetValue(key, out var bucket)) {
            var next = RemoveAliasFromBucket(bucket, nodeId);
            if (!next.IsDefaultOrEmpty) { NonExactAliases[key] = next; }
            else { NonExactAliases.Remove(key); }
        }
    }

    /// <remarks>
    /// AliasGeneration currently derives aliases solely from <see cref="NodeB.Name"/>.
    /// If future changes make aliases depend on entry metadata, refresh logic must be revisited.
    /// </remarks>
    internal void AddAliasesForNode(int nodeId) {
        if (nodeId < 0 || nodeId >= Nodes.Count) { return; }
        var node = Nodes[nodeId];
        IEnumerable<AliasGeneration.AliasSpec> specs = node.Kind switch {
            NodeKind.Namespace => AliasGeneration.GetNamespaceAliases(node.Name),
            NodeKind.Type => AliasGeneration.GetTypeAliases(node.Name),
            _ => Array.Empty<AliasGeneration.AliasSpec>()
        };
        foreach (var spec in specs) {
            if (spec.IsExact) { AddAliasExact(spec.Key, nodeId, spec.Flags); }
            else { AddAliasNonExact(spec.Key, nodeId, spec.Flags); }
        }
    }

    internal void RemoveAliasesForNode(int nodeId) {
        if (nodeId < 0 || nodeId >= Nodes.Count) { return; }
        var node = Nodes[nodeId];
        IEnumerable<AliasGeneration.AliasSpec> specs = node.Kind switch {
            NodeKind.Namespace => AliasGeneration.GetNamespaceAliases(node.Name),
            NodeKind.Type => AliasGeneration.GetTypeAliases(node.Name),
            _ => Array.Empty<AliasGeneration.AliasSpec>()
        };
        foreach (var spec in specs) {
            if (spec.IsExact) { RemoveAliasExact(spec.Key, nodeId); }
            else { RemoveAliasNonExact(spec.Key, nodeId); }
        }
    }

    // --- Tree queries ---

    internal int FindNearestNamespaceAncestor(int nodeId) {
        int current = (nodeId >= 0 && nodeId < Nodes.Count) ? Nodes[nodeId].Parent : -1;
        while (current >= 0 && Nodes[current].Kind != NodeKind.Namespace) {
            current = Nodes[current].Parent;
        }
        return current;
    }

    internal bool HasMatchingDescendant(int nodeId, string targetDocId) {
        if (nodeId < 0 || nodeId >= Nodes.Count) { return false; }
        var stack = new Stack<int>();
        int child = Nodes[nodeId].FirstChild;
        while (child >= 0) {
            stack.Push(child);
            child = Nodes[child].NextSibling;
        }

        while (stack.Count > 0) {
            int id = stack.Pop();
            var node = Nodes[id];
            if (node.Entry?.DocCommentId != null) {
                var docId = node.Entry.DocCommentId;
                if (string.Equals(docId, targetDocId, StringComparison.Ordinal) ||
                    docId.StartsWith(targetDocId + "+", StringComparison.Ordinal)) { return true; }
            }

            int nextChild = node.FirstChild;
            while (nextChild >= 0) {
                stack.Push(nextChild);
                nextChild = Nodes[nextChild].NextSibling;
            }
        }

        return false;
    }

    internal int EnsureNamespaceChain(string[] segments) {
        int current = 0; // root
        if (segments.Length == 0) { return current; }

        string currentNamespace = string.Empty;
        for (int i = 0; i < segments.Length; i++) {
            var segment = segments[i];
            int next = FindChildByNameKind(current, segment, NodeKind.Namespace);
            currentNamespace = i == 0 ? segment : currentNamespace + "." + segment;
            var parentNamespace = currentNamespace.Contains('.') ? currentNamespace[..currentNamespace.LastIndexOf('.')] : string.Empty;

            if (next < 0) {
                var nsEntry = new SymbolEntry(
                    DocCommentId: "N:" + currentNamespace,
                    Assembly: string.Empty,
                    Kind: SymbolKinds.Namespace,
                    ParentNamespaceNoGlobal: parentNamespace,
                    FqnNoGlobal: currentNamespace,
                    FqnLeaf: segment
                );
                next = NewChild(current, segment, NodeKind.Namespace, nsEntry);
                AddAliasesForNode(next);
            }
            else {
                var node = Nodes[next];
                if (node.Entry is null) {
                    var nsEntry = new SymbolEntry(
                        DocCommentId: "N:" + currentNamespace,
                        Assembly: string.Empty,
                        Kind: SymbolKinds.Namespace,
                        ParentNamespaceNoGlobal: parentNamespace,
                        FqnNoGlobal: currentNamespace,
                        FqnLeaf: segment
                    );
                    ReplaceNodeEntry(next, nsEntry, refreshAliases: true);
                }
            }

            current = next;
        }

        return current;
    }

    internal SymbolEntry CreateIntermediateTypeEntry(string[] nsSegments, string[] typeSegments, int currentTypeIndex, string assembly) {
        DebugUtil.Print("SymbolTreeB.WithDelta", $"CreateIntermediateTypeEntry被调用: currentTypeIndex={currentTypeIndex}, typeSegs=[{string.Join(",", typeSegments)}]");

        var docId = BuildIntermediateTypeDocId(nsSegments, typeSegments, currentTypeIndex);

        DebugUtil.Print("SymbolTreeB.WithDelta", $"创建中间类型 DocId: {docId}");

        var fqnParts = new List<string>();
        if (nsSegments.Length > 0) {
            fqnParts.Add(string.Join('.', nsSegments));
        }
        for (int i = 0; i <= currentTypeIndex; i++) {
            var typeSegment = typeSegments[i];
            var (baseName, arity) = ParseName(typeSegment);
            if (arity > 0) {
                var genericParams = string.Join(',', Enumerable.Range(1, arity).Select(n => "T"));
                fqnParts.Add($"{baseName}<{genericParams}>");
            }
            else {
                fqnParts.Add(baseName);
            }
        }

        var fqnNoGlobal = string.Join('.', fqnParts);
        var leafSegment = typeSegments[currentTypeIndex];
        var (leafBaseName, leafArity) = ParseName(leafSegment);
        var leafWithArity = leafArity > 0 ? leafBaseName + "`" + leafArity.ToString() : leafBaseName;
        var parentNamespace = nsSegments.Length > 0 ? string.Join('.', nsSegments) : string.Empty;

        return new SymbolEntry(
            DocCommentId: docId,
            Assembly: assembly,
            Kind: SymbolKinds.Type,
            ParentNamespaceNoGlobal: parentNamespace,
            FqnNoGlobal: fqnNoGlobal,
            FqnLeaf: leafWithArity
        );
    }

    internal void RemoveAliasesSubtree(int rootId) {
        if (rootId < 0 || rootId >= Nodes.Count) { return; }
        var stack = new Stack<int>();
        stack.Push(rootId);
        while (stack.Count > 0) {
            int id = stack.Pop();
            var node = Nodes[id];
            if (node.Kind == NodeKind.Type || node.Kind == NodeKind.Namespace) {
                RemoveAliasesForNode(id);
            }
            int child = node.FirstChild;
            while (child >= 0) {
                stack.Push(child);
                child = Nodes[child].NextSibling;
            }
        }
    }

    internal void RemoveTypeSubtree(int rootTypeId) {
        if (rootTypeId < 0 || rootTypeId >= Nodes.Count) { return; }
        if (Nodes[rootTypeId].Kind != NodeKind.Type) { return; }

        var toDelete = new List<int>();
        var stack = new Stack<int>();
        stack.Push(rootTypeId);
        while (stack.Count > 0) {
            int id = stack.Pop();
            toDelete.Add(id);
            var node = Nodes[id];
            int child = node.FirstChild;
            while (child >= 0) {
                stack.Push(child);
                child = Nodes[child].NextSibling;
            }
        }

        toDelete.Reverse();

        foreach (var id in toDelete) {
            RemoveAliasesForNode(id);
        }

        foreach (var id in toDelete) {
            DetachNode(id);
        }
    }
}
