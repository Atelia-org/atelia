// using System;
// using System.Collections.Generic;
// using System.Collections.Immutable;
// using System.Linq;
// using Atelia.Diagnostics;
// using CodeCortexV2.Abstractions;
// using CodeCortexV2.Index;

// namespace CodeCortexV2.Index.SymbolTreeInternal {
//     /// <summary>
//     /// Prototype of a tree-based symbol index. Conforms to ISymbolIndex for drop-in A/B.
//     /// Query engine will be filled incrementally (right-to-left pruning, aliases, etc.).
//     /// </summary>
//     internal sealed class SymbolTree : ISymbolIndex {
//         private readonly ImmutableArray<Node> _nodes;                 // flat node store
//         private readonly NameTable _names;                            // name table & aliases
//         private readonly Dictionary<string, ImmutableArray<int>> _nameDict; // name -> node ids (bucketed)
//         private readonly ImmutableArray<SymbolEntry> _entries;        // entryIndex -> SymbolEntry
//         private readonly ImmutableArray<int> _entryRefs;              // flat entry refs for nodes
//         private readonly Dictionary<string, int> _idToEntryIndex;     // DocId -> entry index

//         private SymbolTree(
//             ImmutableArray<Node> nodes,
//             NameTable names,
//             Dictionary<string, ImmutableArray<int>> nameDict,
//             ImmutableArray<SymbolEntry> entries,
//             ImmutableArray<int> entryRefs,
//             Dictionary<string, int> idToEntryIndex
//         ) {
//             _nodes = nodes;
//             _names = names;
//             _nameDict = nameDict;
//             _entries = entries;
//             _entryRefs = entryRefs;
//             _idToEntryIndex = idToEntryIndex;
//         }

//         public static SymbolTree Empty { get; } = new(
//             ImmutableArray<Node>.Empty,
//             NameTable.Empty,
//             new Dictionary<string, ImmutableArray<int>>(StringComparer.Ordinal),
//             ImmutableArray<SymbolEntry>.Empty,
//             ImmutableArray<int>.Empty,
//             new Dictionary<string, int>(StringComparer.Ordinal)
//         );

//         public SearchResults Search(string query, int limit, int offset, SymbolKinds kinds) {
//             var effLimit = Math.Max(0, limit);
//             var effOffset = Math.Max(0, offset);
//             if (string.IsNullOrWhiteSpace(query)) {
//                 return new SearchResults(Array.Empty<SearchHit>(), 0, 0, effLimit, 0);
//             }
//             var sw = System.Diagnostics.Stopwatch.StartNew();
//             query = query.Trim();
//             DebugUtil.Print("SymbolTree.Search", $"q='{query}', kinds={kinds}, limit={effLimit}, offset={effOffset}");

//             // DocId fast path
//             if (query.Length > 2 && (query[1] == ':') && (query[0] == 'T' || query[0] == 'N')) {
//                 if (_idToEntryIndex.TryGetValue(query, out var idx)) {
//                     var hit = _entries[idx].ToHit(MatchFlags.Id, 0);
//                     var total = 1;
//                     var page = (effOffset == 0 && effLimit > 0) ? new[] { hit } : Array.Empty<SearchHit>();
//                     int? nextOff = effOffset + effLimit < total ? effOffset + effLimit : null;
//                     return new SearchResults(page, total, effOffset, effLimit, nextOff);
//                 }
//                 return new SearchResults(Array.Empty<SearchHit>(), 0, effOffset, effLimit, null);
//             }

//             // Normalize root constraint
//             bool rootConstraint = false;
//             if (query.StartsWith("global::", StringComparison.Ordinal)) {
//                 rootConstraint = true;
//                 query = query.Substring("global::".Length);
//             }

//             static string[] SplitSegments(string q) {
//                 if (string.IsNullOrEmpty(q)) {
//                     return Array.Empty<string>();
//                 }

//                 var parts = q.Split('.', StringSplitOptions.RemoveEmptyEntries);
//                 if (parts.Length == 0) {
//                     return parts;
//                 }

//                 var last = parts[^1];
//                 var typeSegs = last.Split('+', StringSplitOptions.RemoveEmptyEntries);
//                 if (typeSegs.Length == 1) {
//                     return parts;
//                 }

//                 var list = new List<string>(parts.Length - 1 + typeSegs.Length);
//                 for (int i = 0; i < parts.Length - 1; i++) {
//                     list.Add(parts[i]);
//                 }

//                 list.AddRange(typeSegs);
//                 return list.ToArray();
//             }

//             int[] ToAncestorSegIds(string[] segs, bool ignoreCase) {
//                 // Map all but the last segment to name IDs; last segment may be an alias (e.g., List<T>) not present in NameTable.
//                 int count = Math.Max(0, segs.Length - 1);
//                 var ids = new int[count];
//                 for (int i = 0; i < count; i++) {
//                     var key = ignoreCase ? segs[i].ToLowerInvariant() : segs[i];
//                     if (_names.TryGetId(key, out var id)) {
//                         ids[i] = id;
//                     } else {
//                         return Array.Empty<int>();
//                     }
//                 }
//                 return ids;
//             }

//             IEnumerable<int> CandidatesForLast(string last, bool ignoreCase) {
//                 var key = ignoreCase ? last.ToLowerInvariant() : last;
//                 if (_nameDict.TryGetValue(key, out var ids)) {
//                     return ids;
//                 }

//                 return Array.Empty<int>();
//             }

//             bool AncestorsMatch(int nodeIdx, int[] ancestorSegIds) {
//                 // Compare ancestors excluding the node itself; start from parent of candidate
//                 int i = ancestorSegIds.Length - 1;
//                 int cur = _nodes[nodeIdx].Parent;
//                 while (i >= 0 && cur >= 0) {
//                     var n = _nodes[cur];
//                     if (n.NameId != ancestorSegIds[i]) {
//                         return false;
//                     }

//                     i--;
//                     cur = n.Parent;
//                 }
//                 if (i >= 0) {
//                     return false; // ran out of ancestors
//                 }

//                 if (rootConstraint) {
//                     return cur == 0; // must attach to root
//                 }

//                 return true;
//             }

//             void CollectEntriesAtNode(int nodeIdx, List<SearchHit> acc, MatchFlags kind) {
//                 var n = _nodes[nodeIdx];
//                 if (n.EntryCount <= 0) {
//                     return;
//                 }

//                 int end = n.EntryStart + n.EntryCount;
//                 for (int i = n.EntryStart; i < end; i++) {
//                     var ei = _entryRefs[i];
//                     if ((kinds & _entries[ei].Kind) == 0) {
//                         continue;
//                     }

//                     acc.Add(_entries[ei].ToHit(kind, 0));
//                 }
//             }

//             void CollectSubtreeEntries(int nodeIdx, List<SearchHit> acc, MatchFlags kind, int maxCount) {
//                 var stack = new Stack<int>();
//                 stack.Push(nodeIdx);
//                 while (stack.Count > 0 && acc.Count < maxCount) {
//                     var cur = stack.Pop();
//                     CollectEntriesAtNode(cur, acc, kind);
//                     var child = _nodes[cur].FirstChild;
//                     while (child >= 0) {
//                         stack.Push(child);
//                         child = _nodes[child].NextSibling;
//                     }
//                 }
//             }

//             // Try Exact (case-sensitive first, then ignore-case)
//             var segs = SplitSegments(query);
//             if (segs.Length > 0) {
//                 foreach (var ignoreCase in new[] { false, true }) {
//                     var ancestorIds = ToAncestorSegIds(segs, ignoreCase);
//                     if (ancestorIds.Length == 0 && segs.Length > 1) {
//                         continue;
//                     }

//                     var cands = CandidatesForLast(segs[^1], ignoreCase);
//                     var hits = new List<SearchHit>();
//                     foreach (var nid in cands) {
//                         if (!AncestorsMatch(nid, ancestorIds)) {
//                             continue;
//                         }

//                         CollectEntriesAtNode(nid, hits, ignoreCase ? MatchFlags.ExactIgnoreCase : MatchFlags.Exact);
//                         if (hits.Count >= effLimit + effOffset) {
//                             break;
//                         }
//                     }
//                     if (hits.Count > 0) {
//                         var total = hits.Count;
//                         var page = hits.Skip(effOffset).Take(effLimit).ToArray();
//                         int? nextOff = effOffset + effLimit < total ? effOffset + effLimit : null;
//                         return new SearchResults(page, total, effOffset, effLimit, nextOff);
//                     }
//                 }
//             }

//             // Prefix: treat provided segments as an anchor, return subtree entries
//             if (segs.Length > 0) {
//                 foreach (var ignoreCase in new[] { false, true }) {
//                     var ancestorIds = ToAncestorSegIds(segs, ignoreCase);
//                     if (ancestorIds.Length == 0 && segs.Length > 1) {
//                         continue;
//                     }

//                     var cands = CandidatesForLast(segs[^1], ignoreCase);
//                     var hits = new List<SearchHit>();
//                     foreach (var nid in cands) {
//                         if (!AncestorsMatch(nid, ancestorIds)) {
//                             continue;
//                         }

//                         CollectSubtreeEntries(nid, hits, MatchFlags.Prefix, effLimit + effOffset);
//                         if (hits.Count >= effLimit + effOffset) {
//                             break;
//                         }
//                     }
//                     if (hits.Count > 0) {
//                         var total = hits.Count;
//                         var page = hits.Skip(effOffset).Take(effLimit).ToArray();
//                         int? nextOff = effOffset + effLimit < total ? effOffset + effLimit : null;
//                         return new SearchResults(page, total, effOffset, effLimit, nextOff);
//                     }
//                 }
//             }

//             return new SearchResults(Array.Empty<SearchHit>(), 0, effOffset, effLimit, null);
//         }

//         /// <summary>
//         /// Functional delta application returning a new immutable tree. Placeholder for now.
//         /// </summary>
//         public SymbolTree WithDelta(SymbolsDelta delta) {
//             if (delta is null) {
//                 return this;
//             }
//             // TODO: implement minimal upsert/remove by reconstructing affected subtrees and buckets.
//             return this; // placeholder: no-op
//         }

//         /// <summary>
//         /// Factory from a flat list of entries. Builds a minimal tree with alias buckets and doc-id map.
//         /// </summary>
//         public static SymbolTree FromEntries(IEnumerable<SymbolEntry> entries) {
//             var arr = entries?.ToImmutableArray() ?? ImmutableArray<SymbolEntry>.Empty;
//             var idToEntry = new Dictionary<string, int>(StringComparer.Ordinal);
//             for (int i = 0; i < arr.Length; i++) {
//                 var id = arr[i].SymbolId;
//                 if (!string.IsNullOrEmpty(id)) {
//                     idToEntry[id] = i;
//                 }
//             }

//             // Name table (canonical name id + alias->id map)
//             var canonical = new List<string>();
//             var aliasToId = new Dictionary<string, int>(StringComparer.Ordinal);
//             int EnsureName(string canon) {
//                 if (!aliasToId.TryGetValue(canon, out var id)) {
//                     id = canonical.Count;
//                     canonical.Add(canon);
//                     aliasToId[canon] = id;
//                     var lower = canon.ToLowerInvariant();
//                     if (!string.Equals(lower, canon, StringComparison.Ordinal)) {
//                         aliasToId[lower] = id;
//                     }
//                 }
//                 return id;
//             }

//             // Node builder (mutable)
//             var nodes = new List<(int NameId, int Parent, int FirstChild, int NextSibling, NodeKind Kind, List<int> Entries)>();
//             var keyToIndex = new Dictionary<string, int>(StringComparer.Ordinal);
//             int NewNode(int nameId, int parent, NodeKind kind) {
//                 var idx = nodes.Count;
//                 nodes.Add((nameId, parent, -1, -1, kind, new List<int>()));
//                 // link as first child of parent
//                 if (parent >= 0) {
//                     var p = nodes[parent];
//                     nodes[parent] = (p.NameId, p.Parent, idx, p.FirstChild, p.Kind, p.Entries);
//                     nodes[idx] = (nameId, parent, -1, p.FirstChild, kind, nodes[idx].Entries);
//                 }
//                 return idx;
//             }
//             int GetOrAddNode(int parent, int nameId, NodeKind kind) {
//                 var key = parent.ToString() + "|" + nameId.ToString() + "|" + ((int)kind).ToString();
//                 if (!keyToIndex.TryGetValue(key, out var idx)) {
//                     idx = NewNode(nameId, parent, kind);
//                     keyToIndex[key] = idx;
//                 }
//                 return idx;
//             }

//             // Root (global:: anchor)
//             int root = NewNode(-1, -1, NodeKind.Namespace);

//             // Name buckets: alias -> node ids
//             var nameBuckets = new Dictionary<string, List<int>>(StringComparer.Ordinal);
//             void Bucket(string alias, int nodeIdx) {
//                 if (string.IsNullOrEmpty(alias)) {
//                     return;
//                 }

//                 if (!nameBuckets.TryGetValue(alias, out var list)) {
//                     list = new List<int>();
//                     nameBuckets[alias] = list;
//                 }
//                 list.Add(nodeIdx);
//                 var lower = alias.ToLowerInvariant();
//                 if (!string.Equals(lower, alias, StringComparison.Ordinal)) {
//                     if (!nameBuckets.TryGetValue(lower, out var list2)) {
//                         list2 = new List<int>();
//                         nameBuckets[lower] = list2;
//                     }
//                     list2.Add(nodeIdx);
//                 }
//             }

//             static IEnumerable<string> SplitNs(string? ns)
//                 => string.IsNullOrEmpty(ns) ? Array.Empty<string>() : ns!.Split('.', StringSplitOptions.RemoveEmptyEntries);

//             static (string baseName, int arity) ParseTypeSegment(string seg) {
//                 if (string.IsNullOrEmpty(seg)) {
//                     return (seg, 0);
//                 }

//                 var baseName = seg;
//                 int arity = 0;
//                 var back = seg.IndexOf('`');
//                 if (back >= 0) {
//                     baseName = seg.Substring(0, back);
//                     var numStr = new string(seg.Skip(back + 1).TakeWhile(char.IsDigit).ToArray());
//                     if (int.TryParse(numStr, out var n1)) {
//                         arity = n1;
//                     }
//                 }
//                 var lt = seg.IndexOf('<');
//                 if (lt >= 0) {
//                     baseName = seg.Substring(lt > 0 ? 0 : 0, lt);
//                     var inside = seg.Substring(lt + 1);
//                     var rt = inside.LastIndexOf('>');
//                     if (rt >= 0) {
//                         inside = inside.Substring(0, rt);
//                     }

//                     if (inside.Length > 0) {
//                         arity = inside.Count(c => c == ',') + 1;
//                     }
//                 }
//                 return (baseName, arity);
//             }
//             static string BuildPseudoGeneric(string baseName, int arity) {
//                 if (arity <= 0) {
//                     return baseName;
//                 }

//                 if (arity == 1) {
//                     return baseName + "<T>";
//                 }

//                 var parts = new string[arity];
//                 for (int i = 0; i < arity; i++) {
//                     parts[i] = i == 0 ? "T" : "T" + (i + 1).ToString();
//                 }

//                 return baseName + "<" + string.Join(",", parts) + ">";
//             }

//             foreach (var e in arr) {
//                 if ((e.Kind & SymbolKinds.Namespace) != 0) {
//                     // Build namespace chain from full FQN
//                     var nsSegments = SplitNs(e.FqnNoGlobal).ToArray();
//                     int parent = root;
//                     for (int i = 0; i < nsSegments.Length; i++) {
//                         var canon = nsSegments[i];
//                         var id = EnsureName(canon);
//                         var idx = GetOrAddNode(parent, id, NodeKind.Namespace);
//                         Bucket(canon, idx);
//                         parent = idx;
//                     }
//                     if (nsSegments.Length > 0) {
//                         nodes[parent].Entries.Add(idToEntry.TryGetValue(e.SymbolId, out var ei) ? ei : arr.IndexOf(e));
//                     }
//                 }
//                 if ((e.Kind & SymbolKinds.Type) != 0) {
//                     int parent = root;
//                     foreach (var ns in SplitNs(e.ParentNamespace)) {
//                         var id = EnsureName(ns);
//                         parent = GetOrAddNode(parent, id, NodeKind.Namespace);
//                         Bucket(ns, parent);
//                     }
//                     // Type chain from FQN's last segment(s)
//                     var fqn = e.FqnNoGlobal ?? string.Empty;
//                     var dot = fqn.LastIndexOf('.');
//                     var typeChain = dot >= 0 ? fqn.Substring(dot + 1) : fqn;
//                     var typeSegs = typeChain.Split('+', StringSplitOptions.RemoveEmptyEntries);
//                     int lastTypeNode = parent;
//                     for (int i = 0; i < typeSegs.Length; i++) {
//                         var (bn, ar) = ParseTypeSegment(typeSegs[i]);
//                         var id = EnsureName(bn);
//                         var idx = GetOrAddNode(lastTypeNode, id, NodeKind.Type);
//                         Bucket(bn, idx);
//                         if (ar > 0) {
//                             Bucket(bn + "`" + ar.ToString(), idx);
//                             Bucket(BuildPseudoGeneric(bn, ar), idx);
//                         }
//                         lastTypeNode = idx;
//                     }
//                     nodes[lastTypeNode].Entries.Add(idToEntry.TryGetValue(e.SymbolId, out var ti) ? ti : arr.IndexOf(e));
//                 }
//             }

//             // Seal nodes and entry refs
//             var entryRefs = new List<int>();
//             var sealedNodes = new List<Node>(nodes.Count);
//             for (int i = 0; i < nodes.Count; i++) {
//                 var n = nodes[i];
//                 int start = n.Entries.Count > 0 ? entryRefs.Count : -1;
//                 int count = n.Entries.Count;
//                 if (count > 0) {
//                     entryRefs.AddRange(n.Entries);
//                 }

//                 sealedNodes.Add(new Node(n.NameId, n.Parent, n.FirstChild, n.NextSibling, n.Kind, start, count));
//             }

//             // Seal name table and buckets
//             var nameTable = new NameTable(canonical.ToImmutableArray(), aliasToId);
//             var buckets = new Dictionary<string, ImmutableArray<int>>(StringComparer.Ordinal);
//             foreach (var kv in nameBuckets) {
//                 buckets[kv.Key] = kv.Value.Distinct().ToImmutableArray();
//             }

//             return new SymbolTree(
//                 sealedNodes.ToImmutableArray(),
//                 nameTable,
//                 buckets,
//                 arr,
//                 entryRefs.ToImmutableArray(),
//                 idToEntry
//             );
//         }
//     }
// }

