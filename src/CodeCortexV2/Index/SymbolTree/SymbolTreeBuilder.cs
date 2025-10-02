using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;

using Atelia.Diagnostics;
using CodeCortexV2.Abstractions;

namespace CodeCortexV2.Index.SymbolTreeInternal;

/// <summary>
/// Mutable construction surface shared by <see cref="SymbolTreeB.WithDelta"/>。
/// 承载节点数组、别名桶与常用辅助操作，后续阶段将进一步拓展至完整 Builder 生命周期。
///
/// <para>&lt;b&gt;单节点拓扑（Single-Node Topology）&lt;/b&gt;</para>
/// 当前设计运行"单节点"拓扑：每个类型节点直接承载完整的 <see cref="SymbolEntry"/>（DocCommentId + Assembly 唯一）。
/// 我们不再为嵌套类型的外层类型创建"占位节点+Entry双节点"结构，而是合并为单一节点。
///
/// <para>&lt;b&gt;严格的契约与 Fail-Fast 策略&lt;/b&gt;</para>
/// 调用方（通常是 <see cref="IndexSynchronizer"/>）必须遵守 <see cref="SymbolsDelta"/> 的排序契约：
/// - <c>TypeAdds</c> 按 DocCommentId.Length 升序（外层类型先于嵌套类型）
/// - <c>TypeRemovals</c> 按 DocCommentId.Length 降序（嵌套类型先于外层类型）
/// - 所有 DocId 起始为 "T:"，且必须提供 Assembly
///
/// <para>&lt;b&gt;为何拒绝创建占位节点（Design Rationale）&lt;/b&gt;</para>
/// 当处理嵌套类型的中间节点时，如果父类型节点不存在，本 Builder 会抛出异常而非创建占位节点。原因：
/// 1. &lt;b&gt;防止故障扩散&lt;/b&gt;：占位节点（Entry 为 null）会导致索引处于不一致状态，使得后续查询返回不完整的类型信息，
///    进而引发级联故障。在早期发现契约违反（fail-fast）比传播脏数据更易于诊断和修复。
/// 2. &lt;b&gt;保证元数据完整性&lt;/b&gt;：只有 <see cref="IndexSynchronizer"/> 中的 <c>INamedTypeSymbol.ToDisplayString()</c>
///    才能正确生成包含泛型形参的准确 DisplayName（如 <c>Dictionary&lt;TKey, TValue&gt;</c>）。
///    由 Builder 自行构造的占位节点无法获得这些关键的类型元数据，会产生不完整或错误的展示名称。
/// 3. &lt;b&gt;简化维护&lt;/b&gt;：占位节点引入了复杂的状态转换逻辑（占位→实体→回收），是典型的技术债务（屎山代码）。
///    通过严格契约将责任前移到 Delta 生成端，使得 Builder 逻辑更清晰、更易测试。
///
/// <para>&lt;b&gt;历史占位节点的清理&lt;/b&gt;</para>
/// 旧版设计中的占位节点（Entry 为 null）只会在加载历史快照时短暂存在，并会在下一次 Delta 应用时
/// 通过 <see cref="TidyTypeSiblings"/>/<see cref="CollapseEmptyTypeAncestors"/> 被自动回收。
/// 后续重构将系统性地移除所有"占位节点"相关的概念和代码路径。
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
        ApplyTypeAddsSingleNode(delta.TypeAdds);
        int deletedNamespaces = CascadeEmptyNamespaces(cascadeCandidates);

        // 清理历史快照中残留的占位节点（Entry is null 的空类型节点）
        int cleanedPlaceholders = CleanupLegacyPlaceholders();

        if (_reusedThisDelta > 0 || _freedThisDelta > 0) {
            DebugUtil.Print(
                "SymbolTree.SingleNode.Freelist",
                $"Delta freelist stats: reused={_reusedThisDelta}, freed={_freedThisDelta}, freeHead={_freeHead}, cleanedPlaceholders={cleanedPlaceholders}"
            );
        }

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
            var aliasKey = string.IsNullOrEmpty(leaf) ? s : leaf;
            DebugUtil.Print("SymbolTree.Removal.Trace", $"Generated aliasKey='{aliasKey}' from leaf='{leaf}'");

            bool removedAny = false;
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
                        int parentBefore = Nodes[nid].Parent;
                        string removedName = Nodes[nid].Name;
                        string removedDocId = entry?.DocCommentId ?? typeKey.DocCommentId;
                        string removedAssembly = entry?.Assembly ?? typeKey.Assembly ?? string.Empty;
                        int nsAncestor = FindNearestNamespaceAncestor(nid);
                        if (nsAncestor > 0) {
                            DebugUtil.Print("SymbolTree.WithDelta", $"Type removal matched node={nid} name={Nodes[nid].Name}, nsAncestorId={nsAncestor} nsName={Nodes[nsAncestor].Name}");
                        }
                        DebugUtil.Print("SymbolTree.WithDelta", $"Removing type subtree nid={nid}, name={Nodes[nid].Name}, docId={entry?.DocCommentId ?? "null"}, asm={entry?.Assembly ?? "null"}, nsAncestor={nsAncestor}");
                        DebugUtil.Print("SymbolTree.Removal.Trace", $"About to call RemoveTypeSubtree for nid={nid}");
                        RemoveTypeSubtree(nid);
                        if (parentBefore >= 0) {
                            TidyTypeSiblings(parentBefore, removedName, removedDocId, removedAssembly, keepNodeId: -1);
                            CollapseEmptyTypeAncestors(parentBefore);
                        }
                        if (nsAncestor > 0) { cascadeCandidates.Add(nsAncestor); }
                        removedAny = true;
                    }
                    else if (entry is not null && string.Equals(entry.DocCommentId, typeKey.DocCommentId, StringComparison.Ordinal)) {
                        DebugUtil.Print("SymbolTree.WithDelta", $"Skip removal for docId={entry.DocCommentId}: existingAsm={entry.Assembly} != targetAsm={typeKey.Assembly}");
                    }
                }
            }
            else {
                DebugUtil.Print("SymbolTree.Removal.Trace", $"No candidates found for aliasKey='{aliasKey}' (bucket empty or missing)");
            }

            // 契约验证：仅在 Debug 模式下检查别名索引一致性（避免 Release 模式的 O(n) 全树扫描）
            VerifyRemovalCompleteness(removedAny, typeKey);
        }
    }

    /// <summary>
    /// 【Debug 专用】验证类型移除的完整性：如果别名查找失败（removedAny=false），检查节点是否真的不存在。
    /// 若节点存在但别名查找失败，说明别名索引损坏，抛出异常防止索引不一致。
    ///
    /// <para>&lt;b&gt;性能考虑&lt;/b&gt;：</para>
    /// 使用 O(n) 全树扫描，仅在 DEBUG 模式编译。Release 构建会完全移除此方法调用（零开销）。
    /// 生产环境信任别名索引的确定性逻辑；开发/测试环境通过此检查尽早发现索引不一致。
    /// </summary>
    [System.Diagnostics.Conditional("DEBUG")]
    private void VerifyRemovalCompleteness(bool removedAny, TypeKey typeKey) {
        if (!removedAny) {
            int existingNode = FindNodeByDocIdAndAssembly(typeKey.DocCommentId, typeKey.Assembly);
            if (existingNode >= 0) {
                throw new InvalidOperationException(
                    $"[DEBUG] Alias index inconsistency detected: TypeRemovals entry '{typeKey.DocCommentId}' " +
                    $"(assembly '{typeKey.Assembly}') exists at node {existingNode} but alias lookup failed. " +
                    $"This indicates alias index corruption. Aborting to prevent divergence."
                );
            }
        }
    }

    private void ApplyTypeAddsSingleNode(IReadOnlyList<SymbolEntry>? additions) {
        if (additions is null || additions.Count == 0) { return; }

        int createdCount = 0;
        int reusedCount = 0;

        foreach (var e in additions) {
            var identifier = !string.IsNullOrEmpty(e.DocCommentId)
                ? e.DocCommentId
                : (string.IsNullOrEmpty(e.FullDisplayName) ? "<unknown>" : e.FullDisplayName);

            if ((e.Kind & SymbolKinds.Type) == 0) { throw new InvalidOperationException($"TypeAdds entry '{identifier}' must have Kind=Type."); }

            if (string.IsNullOrEmpty(e.DocCommentId) || !e.DocCommentId.StartsWith("T:", StringComparison.Ordinal)) { throw new InvalidOperationException($"TypeAdds entry '{identifier}' must provide a DocCommentId starting with 'T:'."); }

            var nsSegs = e.NamespaceSegments ?? Array.Empty<string>();
            int currentParent = EnsureNamespaceChain(nsSegs);

            string[] typeSegs = e.TypeSegments ?? Array.Empty<string>();
            if (typeSegs.Length == 0) {
                var docIdBody = e.DocCommentId[2..];
                var allSegs = SymbolNormalization.SplitSegmentsWithNested(docIdBody);
                int skip = nsSegs.Length;
                if (skip < 0 || skip > allSegs.Length) { skip = 0; }
                typeSegs = allSegs.Skip(skip).ToArray();
                if (typeSegs.Length == 0) {
                    typeSegs = new[] { docIdBody };
                }
            }

            string assembly = e.Assembly ?? string.Empty;

            for (int i = 0; i < typeSegs.Length; i++) {
                var nodeName = typeSegs[i];
                bool isLast = i == typeSegs.Length - 1;

                // 对于中间节点（非最后一段），必须已经存在对应的父类型节点。
                // 如果不存在，说明违反了 SymbolsDelta 的排序契约（父类型应该先于子类型被添加）。
                //
                // 【设计决策】我们选择 fail-fast 而非创建占位节点，原因参见类注释：
                // 1. 占位节点会导致索引不一致，使故障扩散；
                // 2. 只有 IndexSynchronizer 能正确生成 DisplayName（含泛型形参）；
                // 3. 占位节点逻辑复杂，是技术债务（屎山代码）。
                if (!isLast) {
                    int intermediateNode = FindTypeChild(currentParent, nodeName);
                    if (intermediateNode < 0) {
                        throw new InvalidOperationException(
                            $"Parent type node '{nodeName}' not found when processing '{e.DocCommentId}'. " +
                            $"This violates the SymbolsDelta ordering contract: all parent types must be added before their nested types. " +
                            $"Current path: {string.Join(".", nsSegs.Concat(typeSegs.Take(i + 1)))}"
                        );
                    }
                    currentParent = intermediateNode;
                    continue;
                }

                // 最后一段：使用实际的 entry
                var targetEntry = e;
                var docId = targetEntry.DocCommentId ?? string.Empty;
                int parentBefore = currentParent;

                // 查找已存在的匹配节点
                int existing = FindTypeEntryNode(parentBefore, nodeName, docId, assembly);
                if (existing >= 0) {
                    var existingEntry = Nodes[existing].Entry;
                    if (!ReferenceEquals(existingEntry, targetEntry)) {
                        ReplaceNodeEntry(existing, targetEntry);
                    }
                    currentParent = existing;
                    TidyTypeSiblings(parentBefore, nodeName, docId, assembly, existing);
                    reusedCount++;
                    continue;
                }

                // 创建新节点
                int newNode = NewChild(parentBefore, nodeName, NodeKind.Type, targetEntry);
                AddAliasesForNode(newNode);
                currentParent = newNode;
                TidyTypeSiblings(parentBefore, nodeName, docId, assembly, newNode);
                createdCount++;
            }
        }

        if (createdCount > 0 || reusedCount > 0) {
            DebugUtil.Print(
                "SymbolTree.SingleNode",
                $"Prototype adds: created={createdCount}, reused={reusedCount}"
            );
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

    /// <summary>
    /// 清理历史快照中残留的占位节点（Entry is null 的类型节点）。
    /// 这些节点是旧版设计的遗留物，在新设计中不应该存在。
    /// 此方法提供向后兼容性，确保从历史快照加载的数据能够收敛到一致状态。
    ///
    /// 清理策略：
    /// - 删除所有 Entry is null 的类型节点（无论是否有子节点）
    /// - 如果占位节点有子节点，子节点会随之被标记为孤立（Parent=-1），稍后被 freelist 回收
    /// </summary>
    private int CleanupLegacyPlaceholders() {
        int cleanedCount = 0;
        var toRemove = new List<int>();

        // 收集所有需要清理的占位节点
        for (int i = 1; i < Nodes.Count; i++) {
            var node = Nodes[i];
            if (node.Parent < 0) { continue; } // 已detached
            if (node.Kind != NodeKind.Type) { continue; }
            if (node.Entry is null) {
                toRemove.Add(i);
            }
        }

        // 清理收集到的节点及其子树
        foreach (var nodeId in toRemove) {
            var node = Nodes[nodeId];
            if (node.FirstChild < 0) {
                DebugUtil.Print("SymbolTree.SingleNode", $"Cleaning up empty legacy placeholder nodeId={nodeId} name={node.Name}");
            }
            else {
                DebugUtil.Print("SymbolTree.SingleNode", $"Cleaning up legacy placeholder subtree nodeId={nodeId} name={node.Name}");
                // 移除整个子树（包括占位节点本身）
                RemoveTypeSubtree(nodeId);
                cleanedCount++;
                continue;
            }
            RemoveAliasesForNode(nodeId);
            DetachNode(nodeId);
            cleanedCount++;
        }

        return cleanedCount;
    }

    // --- Shared static helpers ---

    internal static string[] SplitNamespace(string? ns)
        => string.IsNullOrEmpty(ns)
            ? Array.Empty<string>()
            : ns!.Split('.', StringSplitOptions.RemoveEmptyEntries);

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

    /// <summary>
    /// 查找匹配名称的类型子节点（必须有 Entry）。
    /// 用于在处理嵌套类型时查找已经存在的父类型节点。
    /// 忽略占位节点（Entry 为 null），因为当前设计不再创建占位节点。
    /// </summary>
    private int FindTypeChild(int parent, string name) {
        if (parent < 0 || parent >= Nodes.Count) { return -1; }
        int current = Nodes[parent].FirstChild;
        while (current >= 0) {
            var node = Nodes[current];
            if (node.Kind == NodeKind.Type && node.Entry is not null && string.Equals(node.Name, name, StringComparison.Ordinal)) { return current; }
            current = node.NextSibling;
        }
        return -1;
    }

    private void TidyTypeSiblings(int parentId, string nodeName, string docId, string assembly, int keepNodeId = -1) {
        if (parentId < 0 || parentId >= Nodes.Count) { return; }
        int current = Nodes[parentId].FirstChild;
        string assemblyNorm = assembly ?? string.Empty;

        while (current >= 0) {
            int next = Nodes[current].NextSibling;
            if (keepNodeId >= 0 && current == keepNodeId) {
                current = next;
                continue;
            }

            var node = Nodes[current];
            if (node.Kind != NodeKind.Type || !string.Equals(node.Name, nodeName, StringComparison.Ordinal)) {
                current = next;
                continue;
            }

            var entry = node.Entry;
            bool matchesDoc = entry is not null &&
                !string.IsNullOrEmpty(docId) &&
                string.Equals(entry.DocCommentId, docId, StringComparison.Ordinal) &&
                string.Equals(entry.Assembly ?? string.Empty, assemblyNorm, StringComparison.Ordinal);

            if (matchesDoc) {
                DebugUtil.Print("SymbolTree.SingleNode", $"Removing duplicate type nodeId={current} docId={docId} assembly={assemblyNorm}");
                RemoveAliasesForNode(current);
                DetachNode(current);
            }
            // 🏗️ Design invariant: In single-node topology, all type nodes must have Entry
            // Legacy placeholder nodes are cleaned up globally by CleanupLegacyPlaceholders
            Debug.Assert(entry is not null || node.FirstChild >= 0,
                $"Unexpected empty placeholder node in TidyTypeSiblings: nodeId={current}, name={nodeName}"
            );

            current = next;
        }
    }

    private void CollapseEmptyTypeAncestors(int startNodeId) {
        int current = startNodeId;
        while (current > 0 && current < Nodes.Count) {
            var node = Nodes[current];
            if (node.Parent < 0) { break; }

            if (node.Kind != NodeKind.Type) {
                current = node.Parent;
                continue;
            }

            // 🏗️ Design invariant: In single-node topology, type nodes must have Entry or children
            // Legacy placeholder nodes are cleaned up globally by CleanupLegacyPlaceholders
            Debug.Assert(node.Entry is not null || node.FirstChild >= 0,
                $"Unexpected empty placeholder node in CollapseEmptyTypeAncestors: nodeId={current}, name={node.Name}"
            );

            if (node.Entry is SymbolEntry entry) {
                var docId = entry.DocCommentId ?? string.Empty;
                var assembly = entry.Assembly ?? string.Empty;
                TidyTypeSiblings(node.Parent, node.Name, docId, assembly, keepNodeId: current);
            }

            current = node.Parent;
        }
    }

    /// <summary>
    /// 查找匹配 DocCommentId 和 Assembly 的类型节点。
    /// </summary>
    private int FindTypeEntryNode(int parent, string name, string docId, string assembly) {
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
                }
            }
            current = node.NextSibling;
        }
        return -1;
    }

    /// <summary>
    /// 【诊断专用】通过全树扫描查找匹配 DocCommentId 和 Assembly 的类型节点。
    ///
    /// <para>&lt;b&gt;性能警告&lt;/b&gt;：O(n) 全树扫描，仅用于 Debug 模式的一致性验证（<see cref="VerifyRemovalCompleteness"/>）。</para>
    /// 正常路径应通过别名索引查找（O(1)）。此方法存在是为了在开发阶段检测别名索引损坏。
    /// </summary>
    internal int FindNodeByDocIdAndAssembly(string docCommentId, string? assembly) {
        if (string.IsNullOrEmpty(docCommentId)) { return -1; }
        string assemblyNorm = assembly ?? string.Empty;
        for (int i = 0; i < Nodes.Count; i++) {
            if (Nodes[i].Kind != NodeKind.Type) { continue; }
            var entry = Nodes[i].Entry;
            if (entry is null) { continue; }
            if (!string.Equals(entry.DocCommentId, docCommentId, StringComparison.Ordinal)) { continue; }
            var entryAsm = entry.Assembly ?? string.Empty;
            if (string.Equals(entryAsm, assemblyNorm, StringComparison.Ordinal)) { return i; }
        }
        return -1;
    }

    internal void ReplaceNode(int index, NodeB node)
        => Nodes[index] = node;

    /// <summary>
    /// Replace the <see cref="SymbolEntry"/> attached to an existing node without altering its tree connectivity.
    /// </summary>
    /// <param name="nodeId">Target node to mutate.</param>
    /// <param name="entry">New entry payload; <c>null</c> clears the node payload but leaves the structural shell.</param>
    /// <remarks>
    /// Aliases are derived solely from <see cref="NodeB.Name"/> and <see cref="NodeB.Kind"/> (see <see cref="AliasGeneration"/>).
    /// Since this method keeps both fields unchanged, alias refresh is unnecessary—aliases remain valid after Entry replacement.
    /// </remarks>
    private void ReplaceNodeEntry(int nodeId, SymbolEntry? entry) {
        var existing = Nodes[nodeId];
        ReplaceNode(nodeId, new NodeB(existing.Name, existing.Parent, existing.FirstChild, existing.NextSibling, existing.Kind, entry));
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
        var relationToInsert = new AliasRelation(flags, nodeId);
        int insertIndex = list.Count;
        for (int i = 0; i < list.Count; i++) {
            if (nodeId < list[i].NodeId) {
                insertIndex = i;
                break;
            }
        }
        list.Insert(insertIndex, relationToInsert);
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

        for (int i = 0; i < segments.Length; i++) {
            var segment = segments[i];
            int next = FindChildByNameKind(current, segment, NodeKind.Namespace);

            var namespaceSegments = new string[i + 1];
            Array.Copy(segments, 0, namespaceSegments, 0, i + 1);
            var docId = "N:" + string.Join('.', namespaceSegments);
            var fullDisplay = string.Join('.', namespaceSegments);

            if (next < 0) {
                var nsEntry = new SymbolEntry(
                    DocCommentId: docId,
                    Assembly: string.Empty,
                    Kind: SymbolKinds.Namespace,
                    NamespaceSegments: namespaceSegments,
                    TypeSegments: Array.Empty<string>(),
                    FullDisplayName: fullDisplay,
                    DisplayName: segment
                );
                next = NewChild(current, segment, NodeKind.Namespace, nsEntry);
                AddAliasesForNode(next);
            }
            else {
                // 命名空间节点在创建时总是带有 Entry（除根节点外，见 Line 57）。
                // 此断言验证不变量：已存在的命名空间节点应该有 Entry。
                System.Diagnostics.Debug.Assert(
                    Nodes[next].Entry is not null,
                    $"Namespace node '{segment}' at index {next} unexpectedly has null Entry. " +
                    $"This should never happen in normal operation."
                );
            }

            current = next;
        }

        return current;
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
