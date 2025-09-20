using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CodeCortexV2.Abstractions;
using Atelia.Diagnostics;
using RoslynWorkspace = Microsoft.CodeAnalysis.Workspace;
using CodeCortexV2.Index.SymbolTreeInternal;

namespace CodeCortexV2.Index;

/// <summary>
/// Abstraction that exposes the current immutable index snapshot.
/// Implementations must publish snapshots atomically and keep reads lock-free.
/// </summary>
public interface IIndexProvider {
    /// <summary>
    /// The current immutable <see cref="SymbolIndex"/> snapshot. Always safe to read on any thread.
    /// </summary>
    ISymbolIndex Current { get; }
}

/// <summary>
/// Bridges Roslyn Workspace and the immutable <see cref="ISymbolIndex"/> by computing normalized, closed-over <see cref="SymbolsDelta"/>s
/// and atomically publishing new snapshots.
/// Responsibilities and contract:
/// - Single-writer, multi-reader: reads are lock-free via <see cref="Current"/>; writes are serialized and debounced.
/// - Produces self-consistent deltas that are closure-complete:
///   - Ensure ancestor namespace chains for additions are present.
///   - Determine namespace removals (including conservative cascading) when they become empty due to this batch.
///   - Represent renames as remove(oldId) + add(newEntry).
///   - Avoid contradictory operations within the same delta and preserve idempotency.
/// - Consumers (e.g., <c>ISymbolIndex.WithDelta</c>) are expected to apply the delta without global re-inference.
/// - Robust to errors: failures don't affect <see cref="Current"/>; can fall back to a full rebuild when configured.
/// </summary>
public sealed class IndexSynchronizer : IIndexProvider, IDisposable {
    private readonly RoslynWorkspace _workspace;
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentQueue<WorkspaceChangeEventArgs> _pending = new();

    private readonly System.Timers.Timer _debounce;

    private ImmutableDictionary<DocumentId, ImmutableHashSet<string>> _docToTypeIds =
        ImmutableDictionary.Create<DocumentId, ImmutableHashSet<string>>();
    private ImmutableDictionary<string, ImmutableHashSet<DocumentId>> _typeIdToDocs =
        ImmutableDictionary.Create<string, ImmutableHashSet<DocumentId>>(StringComparer.Ordinal);

    // Maintain a map of all current entries to allow building SymbolTree snapshots for A/B.
    private ImmutableDictionary<string, SymbolEntry> _entriesMap =
        ImmutableDictionary.Create<string, SymbolEntry>(StringComparer.Ordinal);


    private ISymbolIndex _current = SymbolTreeB.Empty;
    /// &lt;inheritdoc /&gt;
    public ISymbolIndex Current => System.Threading.Volatile.Read(ref _current);


    /// <summary>Expose current flat entries for building alternative engines (e.g., SymbolTreeB) without exposing internal maps.</summary>
    public IEnumerable<SymbolEntry> CurrentEntries => _entriesMap.Values;

    /// <summary>Debounce duration in milliseconds for coalescing workspace events.</summary>
    public int DebounceMs { get; set; } = 500;
    /// <summary>When true, a failed batch triggers a full rebuild attempt.</summary>
    public bool FullRebuildFallbackOnError { get; set; } = true;

    private IndexSynchronizer(RoslynWorkspace workspace) {
        _workspace = workspace;
        _debounce = new System.Timers.Timer { AutoReset = false, Interval = DebounceMs };
        _debounce.Elapsed += async (_, __) => {
            try {
                await FlushAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) {
                // ignore during shutdown
            }
            catch (ObjectDisposedException) {
                // timer or resources disposed during shutdown
            }
        };
    }

    /// <summary>
    /// Create and initialize a synchronizer for a given Roslyn workspace.
    /// Performs the initial full build and starts listening to workspace changes.
    /// </summary>
    public static async Task<IndexSynchronizer> CreateAsync(RoslynWorkspace ws, CancellationToken ct) {
        var sync = new IndexSynchronizer(ws);
        await sync.InitialBuildAsync(ct).ConfigureAwait(false);
        ws.WorkspaceChanged += sync.OnWorkspaceChanged;
        return sync;
    }

    private void OnWorkspaceChanged(object? sender, WorkspaceChangeEventArgs e) {
        if (_cts.IsCancellationRequested) { return; }
        _pending.Enqueue(e);
        // reset debounce
        _debounce.Interval = DebounceMs;
        _debounce.Stop();
        _debounce.Start();
    }

    private async Task InitialBuildAsync(CancellationToken ct) {
        var sw = Stopwatch.StartNew();
        DebugUtil.Print("IndexSync", $"Initial build started");
        var full = await ComputeFullDeltaAsync(_workspace.CurrentSolution, ct).ConfigureAwait(false);
        var next = Current.WithDelta(full);
        System.Threading.Volatile.Write(ref _current, next); // publish
        // Build entries map from full adds
        var mapB = _entriesMap.ToBuilder();
        foreach (var e in full.TypeAdds) {
            if (!string.IsNullOrEmpty(e.SymbolId)) {
                mapB[e.SymbolId] = e;
            }
        }
        foreach (var e in full.NamespaceAdds) {
            if (!string.IsNullOrEmpty(e.SymbolId)) {
                mapB[e.SymbolId] = e;
            }
        }
        _entriesMap = mapB.ToImmutable();
        // Build doc/type maps
        await RebuildDocTypeMapsAsync(_workspace.CurrentSolution, ct).ConfigureAwait(false);
        DebugUtil.Print("IndexSync", $"Initial build done: types={full.TypeAdds.Count}, namespaces={full.NamespaceAdds.Count}, elapsed={sw.ElapsedMilliseconds}ms");
    }

    private async Task FlushAsync() {
        if (_cts.IsCancellationRequested) { return; }
        List<WorkspaceChangeEventArgs> batch = new();
        while (_pending.TryDequeue(out var e)) {
            batch.Add(e);
        }


        if (batch.Count == 0) { return; }
        await _writer.WaitAsync(_cts.Token).ConfigureAwait(false);
        try {
            await ApplyBatchAsync(batch, _cts.Token).ConfigureAwait(false);
        }
        finally {
            _writer.Release();
        }
    }

    private async Task ApplyBatchAsync(List<WorkspaceChangeEventArgs> batch, CancellationToken ct) {
        var sw = Stopwatch.StartNew();
        try {
            var affected = CollectAffectedDocuments(batch, _workspace.CurrentSolution);
            DebugUtil.Print("IndexSync", $"Batch: events={batch.Count}, docs={affected.Count}");
            var delta = await ComputeDeltaAsync(affected, ct).ConfigureAwait(false);
            DebugUtil.Print("IndexSync", $"Delta: +T={delta.TypeAdds.Count}, -T={delta.TypeRemovals.Count}, +N={delta.NamespaceAdds.Count}, -N={delta.NamespaceRemovals.Count}");
            // Update entries map for A/B tree snapshots
            var mapB = _entriesMap.ToBuilder();
            if (delta.TypeRemovals is { Count: > 0 }) {
                foreach (var id in delta.TypeRemovals) {
                    if (!string.IsNullOrEmpty(id)) {
                        mapB.Remove(id);
                    }
                }
            }
            if (delta.NamespaceRemovals is { Count: > 0 }) {
                foreach (var id in delta.NamespaceRemovals) {
                    if (!string.IsNullOrEmpty(id)) {
                        mapB.Remove(id);
                    }
                }
            }
            if (delta.TypeAdds is { Count: > 0 }) {
                foreach (var e in delta.TypeAdds) {
                    if (!string.IsNullOrEmpty(e.SymbolId)) {
                        mapB[e.SymbolId] = e;
                    }
                }
            }
            if (delta.NamespaceAdds is { Count: > 0 }) {
                foreach (var e in delta.NamespaceAdds) {
                    if (!string.IsNullOrEmpty(e.SymbolId)) {
                        mapB[e.SymbolId] = e;
                    }
                }
            }
            _entriesMap = mapB.ToImmutable();

            var next = Current.WithDelta(delta);
            System.Threading.Volatile.Write(ref _current, next); // publish (single-writer model)
            DebugUtil.Print("IndexSync", $"Applied in {sw.ElapsedMilliseconds}ms");
        }
        catch (Exception ex) {
            DebugUtil.Print("IndexSync", $"ERROR applying batch: {ex.Message}");
            if (FullRebuildFallbackOnError) {
                try {
                    var full = await ComputeFullDeltaAsync(_workspace.CurrentSolution, ct).ConfigureAwait(false);
                    var next = Current.WithDelta(full);
                    System.Threading.Volatile.Write(ref _current, next);
                    await RebuildDocTypeMapsAsync(_workspace.CurrentSolution, ct).ConfigureAwait(false);
                    DebugUtil.Print("IndexSync", $"Fallback full rebuild succeeded");
                }
                catch (Exception ex2) {
                    DebugUtil.Print("IndexSync", $"Fallback full rebuild failed: {ex2.Message}");
                }
            }
        }
    }

    public void Dispose() {
        try {
            _workspace.WorkspaceChanged -= OnWorkspaceChanged;
        }
        catch { }
        _debounce.Stop();
        _debounce.Dispose();
        _cts.Cancel();
        _cts.Dispose();
    }

    // -------- Delta computation --------

    private static HashSet<DocumentId> CollectAffectedDocuments(List<WorkspaceChangeEventArgs> batch, Solution solution) {
        var set = new HashSet<DocumentId>();
        foreach (var e in batch) {
            switch (e.Kind) {
                case WorkspaceChangeKind.DocumentAdded:
                case WorkspaceChangeKind.DocumentChanged:
                case WorkspaceChangeKind.DocumentRemoved:
                case WorkspaceChangeKind.DocumentReloaded:
                case WorkspaceChangeKind.DocumentInfoChanged:
                    if (e.DocumentId is not null) {
                        set.Add(e.DocumentId);
                    }

                    break;
                case WorkspaceChangeKind.ProjectAdded:
                case WorkspaceChangeKind.ProjectChanged:
                case WorkspaceChangeKind.ProjectRemoved:
                case WorkspaceChangeKind.ProjectReloaded:
                    if (e.ProjectId is ProjectId pid) {
                        var proj = solution.GetProject(pid);
                        if (proj != null) {
                            foreach (var d in proj.DocumentIds) {
                                set.Add(d);
                            }
                        }
                    }
                    break;
                case WorkspaceChangeKind.SolutionAdded:
                case WorkspaceChangeKind.SolutionChanged:
                case WorkspaceChangeKind.SolutionCleared:
                case WorkspaceChangeKind.SolutionReloaded:
                    foreach (var p in solution.Projects) {
                        foreach (var d in p.DocumentIds) {
                            set.Add(d);
                        }
                    }

                    break;
                default:
                    break;
            }
        }
        return set;
    }

    private async Task<SymbolsDelta> ComputeFullDeltaAsync(Solution solution, CancellationToken ct) {
        var typeAdds = new List<SymbolEntry>(1024);
        var nsAdds = new List<SymbolEntry>(256);

        foreach (var project in solution.Projects) {
            ct.ThrowIfCancellationRequested();
            var comp = await project.GetCompilationAsync(ct).ConfigureAwait(false);
            if (comp is null) { continue; }
            WalkNamespace(comp.Assembly.GlobalNamespace);

            void WalkNamespace(INamespaceSymbol ns) {
                if (!ns.IsGlobalNamespace) {
                    var fqn = ns.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                    var fqnNoGlobal = IndexStringUtil.StripGlobal(fqn);
                    var fqnBase = IndexStringUtil.NormalizeFqnBase(fqn);
                    var simple = ns.Name;
                    var docId = "N:" + fqnNoGlobal;
                    var lastDot = fqnNoGlobal.LastIndexOf('.');
                    var parentNs = lastDot > 0 ? fqnNoGlobal.Substring(0, lastDot) : string.Empty;
                    nsAdds.Add(new SymbolEntry(docId, fqn, fqnNoGlobal, fqnBase, simple, SymbolKinds.Namespace, string.Empty, string.Empty, parentNs));
                }
                foreach (var t in ns.GetTypeMembers()) {
                    WalkType(t);
                }

                foreach (var sub in ns.GetNamespaceMembers()) {
                    ct.ThrowIfCancellationRequested();
                    WalkNamespace(sub);
                }
            }

            void WalkType(INamedTypeSymbol t) {
                var fqn = t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                var fqnNoGlobal = IndexStringUtil.StripGlobal(fqn);
                var fqnBase = IndexStringUtil.NormalizeFqnBase(fqn);
                var simple = t.Name;
                var asm = t.ContainingAssembly?.Name ?? string.Empty;
                var docId = DocumentationCommentId.CreateDeclarationId(t) ?? "T:" + fqnNoGlobal;
                var parentNs = t.ContainingNamespace?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)?.Replace("global::", string.Empty) ?? string.Empty;
                typeAdds.Add(new SymbolEntry(docId, fqn, fqnNoGlobal, fqnBase, simple, SymbolKinds.Type, asm, IndexStringUtil.ExtractGenericBase(simple), parentNs));
                foreach (var nt in t.GetTypeMembers()) {
                    ct.ThrowIfCancellationRequested();
                    WalkType(nt);
                }
            }
        }

        return new SymbolsDelta(typeAdds, Array.Empty<string>(), nsAdds, Array.Empty<string>());
    }

    private async Task<SymbolsDelta> ComputeDeltaAsync(HashSet<DocumentId> docs, CancellationToken ct) {
        var solution = _workspace.CurrentSolution;
        var typeAdds = new List<SymbolEntry>();
        var typeRemovals = new List<string>();
        var nsAdds = new List<SymbolEntry>();
        var nsRemovals = new List<string>();

        // Group by project and reuse compilation
        var groups = docs.GroupBy(d => d.ProjectId);
        foreach (var g in groups) {
            var proj = solution.GetProject(g.Key);
            if (proj is null) { continue; }
            var comp = await proj.GetCompilationAsync(ct).ConfigureAwait(false);
            if (comp is null) { continue; }
            foreach (var docId in g) {
                var doc = solution.GetDocument(docId);
                if (doc is null) {
                    // Document has been removed from the workspace. Remove all types declared solely in this document.
                    if (_docToTypeIds.TryGetValue(docId, out var oldSetRemoved) && oldSetRemoved is { Count: > 0 }) {
                        foreach (var id in oldSetRemoved) {
                            if (_typeIdToDocs.TryGetValue(id, out var ds)) {
                                var nds = ds.Remove(docId);
                                if (nds.IsEmpty) {
                                    // last declaration removed -> remove type and consider cascading namespace removals
                                    typeRemovals.Add(id);
                                    var parentNs = GetParentNamespaceFromTypeDocId(id);
                                    CascadeNamespaceRemovalsIfEmpty(parentNs, nsRemovals, pendingTypeRemovals: typeRemovals);
                                    _typeIdToDocs = _typeIdToDocs.Remove(id);
                                }
                                else {
                                    _typeIdToDocs = _typeIdToDocs.SetItem(id, nds);
                                }
                            }
                            else {
                                // No mapping -> conservatively mark removal
                                typeRemovals.Add(id);
                                var parentNs = GetParentNamespaceFromTypeDocId(id);
                                CascadeNamespaceRemovalsIfEmpty(parentNs, nsRemovals, pendingTypeRemovals: typeRemovals);
                            }
                        }
                        // finally drop the document mapping
                        _docToTypeIds = _docToTypeIds.Remove(docId);
                    }
                    continue;
                }

                var model = await doc.GetSemanticModelAsync(ct).ConfigureAwait(false);
                if (model is null) { continue; }
                var root = await doc.GetSyntaxRootAsync(ct).ConfigureAwait(false);
                if (root is null) { continue; }
                var declared = new List<(string Id, SymbolEntry Entry, string ParentNs)>();
                foreach (var node in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>()) {
                    var sym = model.GetDeclaredSymbol(node, ct) as INamedTypeSymbol;
                    if (sym is null) { continue; }
                    var fqn = sym.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                    var fqnNoGlobal = IndexStringUtil.StripGlobal(fqn);
                    var fqnBase = IndexStringUtil.NormalizeFqnBase(fqn);
                    var simple = sym.Name;
                    var asm = sym.ContainingAssembly?.Name ?? string.Empty;
                    var sid = DocumentationCommentId.CreateDeclarationId(sym) ?? "T:" + fqnNoGlobal;
                    var parentNs = sym.ContainingNamespace?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)?.Replace("global::", string.Empty) ?? string.Empty;
                    var entry = new SymbolEntry(sid, fqn, fqnNoGlobal, fqnBase, simple, SymbolKinds.Type, asm, IndexStringUtil.ExtractGenericBase(simple), parentNs);
                    declared.Add((sid, entry, parentNs));
                }

                var newIds = declared.Select(d => d.Id).ToImmutableHashSet(StringComparer.Ordinal);
                _docToTypeIds.TryGetValue(docId, out var oldSet);
                oldSet ??= ImmutableHashSet<string>.Empty;

                var addedIds = newIds.Except(oldSet);
                var removedIds = oldSet.Except(newIds);

                // Adds
                foreach (var id in addedIds) {
                    var e = declared.First(d => d.Id == id).Entry;
                    typeAdds.Add(e);
                    // ensure namespace chain
                    EnsureNamespaceChain(declared.First(d => d.Id == id).ParentNs, nsAdds);
                }

                // Removals with partial check
                foreach (var id in removedIds) {
                    if (_typeIdToDocs.TryGetValue(id, out var docsSet)) {
                        var left = docsSet.Remove(docId);
                        if (left.IsEmpty) {
                            typeRemovals.Add(id);
                            var parentNs = GetParentNamespaceFromTypeDocId(id);
                            CascadeNamespaceRemovalsIfEmpty(parentNs, nsRemovals, pendingTypeRemovals: typeRemovals);
                        }
                    }
                    else {
                        // unknown mapping -> treat as removable
                        typeRemovals.Add(id);
                    }
                }

                // Update maps for this document
                _docToTypeIds = _docToTypeIds.SetItem(docId, newIds);
                // update typeId -> docs mapping for added/removed
                foreach (var id in addedIds) {
                    _typeIdToDocs.TryGetValue(id, out var ds);
                    ds = (ds ?? ImmutableHashSet<DocumentId>.Empty).Add(docId);
                    _typeIdToDocs = _typeIdToDocs.SetItem(id, ds);
                }
                foreach (var id in removedIds) {
                    if (_typeIdToDocs.TryGetValue(id, out var ds)) {
                        var nds = ds.Remove(docId);
                        if (nds.IsEmpty) {
                            _typeIdToDocs = _typeIdToDocs.Remove(id);
                        }
                        else {
                            _typeIdToDocs = _typeIdToDocs.SetItem(id, nds);
                        }
                    }
                }
            }
        }

        return new SymbolsDelta(typeAdds, typeRemovals, nsAdds, nsRemovals);
    }

    private async Task RebuildDocTypeMapsAsync(Solution solution, CancellationToken ct) {
        var docTo = ImmutableDictionary.CreateBuilder<DocumentId, ImmutableHashSet<string>>();
        var typeTo = ImmutableDictionary.CreateBuilder<string, ImmutableHashSet<DocumentId>>();

        foreach (var project in solution.Projects) {
            ct.ThrowIfCancellationRequested();
            foreach (var doc in project.Documents) {
                var model = await doc.GetSemanticModelAsync(ct).ConfigureAwait(false);
                if (model is null) { continue; }
                var root = await doc.GetSyntaxRootAsync(ct).ConfigureAwait(false);
                if (root is null) { continue; }
                var setBuilder = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
                foreach (var node in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>()) {
                    var sym = model.GetDeclaredSymbol(node, ct) as INamedTypeSymbol;
                    if (sym is null) { continue; }
                    var fqn = sym.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                    var fqnNoGlobal = IndexStringUtil.StripGlobal(fqn);
                    var sid = DocumentationCommentId.CreateDeclarationId(sym) ?? "T:" + fqnNoGlobal;
                    setBuilder.Add(sid);
                }
                var set = setBuilder.ToImmutable();
                docTo[doc.Id] = set;
                foreach (var id in set) {
                    if (!typeTo.TryGetValue(id, out var ds)) {
                        ds = ImmutableHashSet<DocumentId>.Empty;
                    }

                    typeTo[id] = ds.Add(doc.Id);
                }
            }
        }

        _docToTypeIds = docTo.ToImmutable();
        _typeIdToDocs = typeTo.ToImmutable();
    }

    private static void EnsureNamespaceChain(string parentNs, List<SymbolEntry> nsAdds) {
        if (string.IsNullOrEmpty(parentNs)) { return; }
        var parts = parentNs.Split('.', StringSplitOptions.RemoveEmptyEntries);
        string cur = string.Empty;
        for (int i = 0; i < parts.Length; i++) {
            cur = i == 0 ? parts[0] : cur + "." + parts[i];
            var fqn = "global::" + cur;
            var fqnBase = IndexStringUtil.NormalizeFqnBase(fqn);
            var docId = "N:" + cur;
            var lastDot = cur.LastIndexOf('.');
            var parent = lastDot > 0 ? cur.Substring(0, lastDot) : string.Empty;
            nsAdds.Add(new SymbolEntry(docId, fqn, cur, fqnBase, parts[i], SymbolKinds.Namespace, string.Empty, string.Empty, parent));
        }
    }

    private void CascadeNamespaceRemovalsIfEmpty(string parentNs, List<string> nsRemovals, List<string> pendingTypeRemovals) {
        // conservative check using Search: only remove when the namespace appears to have no children except those being removed in this batch
        var cur = parentNs;
        while (!string.IsNullOrEmpty(cur)) {
            if (CanRemoveNamespaceConservatively(cur, pendingTypeRemovals)) {
                nsRemovals.Add("N:" + cur);
                var lastDot = cur.LastIndexOf('.');
                cur = lastDot > 0 ? cur.Substring(0, lastDot) : string.Empty;
            }
            else { break; /* stop cascading upward when current ns still has other children */ }
        }
    }

    private bool CanRemoveNamespaceConservatively(string ns, List<string> pendingTypeRemovals) {
        // If there are any descendants other than the pending type removals, keep the namespace.
        var q = ns + "."; // ensure dot so prefix branch is used in Search
        var page = Current.Search(q, limit: 10, offset: 0, kinds: SymbolKinds.All);
        if (page.Total == 0) { return true; }
        bool allPending = page.Items.All(h => h.Kind == SymbolKinds.Type && pendingTypeRemovals.Contains(h.SymbolId.Value ?? string.Empty));
        if (!allPending) { return false; }
        return page.Total <= page.Items.Count;
    }

    // helpers moved to IndexStringUtil

    private static string GetParentNamespaceFromTypeDocId(string typeDocId) {
        // typeDocId examples: "T:Ns1.Ns2.Type", "T:Ns1.Outer+Inner", "T:Ns1.Ns2.Generic`1"
        if (string.IsNullOrEmpty(typeDocId)) { return string.Empty; }
        if (!typeDocId.StartsWith("T:", StringComparison.Ordinal)) { return string.Empty; }
        var rest = typeDocId.Substring(2);
        var lastDot = rest.LastIndexOf('.');
        return lastDot > 0 ? rest.Substring(0, lastDot) : string.Empty;
    }
}
