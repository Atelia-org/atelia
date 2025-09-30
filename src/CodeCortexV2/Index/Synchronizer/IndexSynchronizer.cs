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
/// Bridges Roslyn Workspace and the immutable <see cref="ISymbolIndex"/> by computing leaf-oriented <see cref="SymbolsDelta"/>s
/// (types only) and atomically publishing new snapshots.
/// Responsibilities and contract:
/// - Single-writer, multi-reader: reads are lock-free via <see cref="Current"/>; writes are serialized and debounced.
/// - Produce self-consistent deltas primarily with <c>TypeAdds</c>/<c>TypeRemovals</c>; namespace fields are deprecated.
/// - Represent renames as remove(oldId) + add(newEntry); avoid contradictory operations within a batch; ensure idempotency.
/// - Consumers (e.g., <c>ISymbolIndex.WithDelta</c>) handle namespace chain materialization and cascading empty-namespace removal
///   internally and locally (impacted subtrees only).
/// - Robust to errors: failures don't affect <see cref="Current"/>; can fall back to a full rebuild when configured.
/// </summary>
public sealed class IndexSynchronizer : IIndexProvider, IDisposable {
    private readonly RoslynWorkspace _workspace;
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentQueue<WorkspaceChangeEventArgs> _pending = new();

    private readonly System.Timers.Timer _debounce;
    private ImmutableDictionary<DocumentId, ImmutableHashSet<TypeKey>> _docToTypeKeys =
        ImmutableDictionary.Create<DocumentId, ImmutableHashSet<TypeKey>>();
    private ImmutableDictionary<TypeKey, ImmutableHashSet<DocumentId>> _typeKeyToDocs =
        ImmutableDictionary.Create<TypeKey, ImmutableHashSet<DocumentId>>();

    // --- Atomic snapshot: single source of truth for Index + Entries ---
    private sealed record IndexSnapshot(
        ISymbolIndex Index,
        ImmutableDictionary<string, SymbolEntry> Entries,
        long Version
    );

    private IndexSnapshot _snap = new(
        SymbolTreeB.Empty,
        ImmutableDictionary.Create<string, SymbolEntry>(StringComparer.Ordinal),
        0
    );

    /// &lt;inheritdoc /&gt;
    public ISymbolIndex Current => System.Threading.Volatile.Read(ref _snap).Index;

    /// <summary>Expose current flat entries for building alternative engines (e.g., SymbolTreeB) without exposing internal maps.</summary>
    public IEnumerable<SymbolEntry> CurrentEntries => System.Threading.Volatile.Read(ref _snap).Entries.Values;

    /// <summary>Debounce duration in milliseconds for coalescing workspace events.</summary>
    public int DebounceMs { get; set; } = 500;
    /// <summary>When true, a failed batch triggers a full rebuild attempt.</summary>
    public bool FullRebuildFallbackOnError { get; set; } = true;
    /// <summary>
    /// Burst flush threshold: when pending event count reaches this value, bypass debounce and trigger an immediate flush.
    /// Set to a positive value to enable; non-positive disables the feature.
    /// </summary>
    public int BurstFlushThreshold { get; set; } = 2000;

    // Prevent concurrent immediate flushes when burst threshold is exceeded.
    private int _flushing = 0;

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

        // If pending grows large, bypass debounce and trigger immediate flush (best-effort, no reentrancy)
        if (BurstFlushThreshold > 0 && _pending.Count >= BurstFlushThreshold) {
            if (System.Threading.Interlocked.CompareExchange(ref _flushing, 1, 0) == 0) {
                _ = Task.Run(
                    async () => {
                        try { await FlushAsync().ConfigureAwait(false); }
                        catch (Exception ex) { DebugUtil.Print("IndexSync", $"ERROR burst flush: {ex.ToString()}"); }
                        finally { System.Threading.Interlocked.Exchange(ref _flushing, 0); }
                    }
                );
            }
        }
    }

    private async Task InitialBuildAsync(CancellationToken ct) {
        var sw = Stopwatch.StartNew();
        DebugUtil.Print("IndexSync", $"Initial build started");
        var full = await ComputeFullDeltaAsync(_workspace.CurrentSolution, ct).ConfigureAwait(false);
        // Replace-style snapshot build: start from empty index and rebuild entries
        var nextIndex = SymbolTreeB.Empty.WithDelta(full);
        var entriesB = ImmutableDictionary.CreateBuilder<string, SymbolEntry>(StringComparer.Ordinal);
        foreach (var e in full.TypeAdds) {
            if (!string.IsNullOrEmpty(e.DocCommentId)) {
                entriesB[e.DocCommentId] = e;
            }
        }
        var nextSnap = new IndexSnapshot(nextIndex, entriesB.ToImmutable(), System.Threading.Volatile.Read(ref _snap).Version + 1);
        System.Threading.Interlocked.Exchange(ref _snap, nextSnap);
        // Build doc/type maps
        await RebuildDocTypeMapsAsync(_workspace.CurrentSolution, ct).ConfigureAwait(false);
        DebugUtil.Print("IndexSync", $"Initial build done: types={full.TypeAdds.Count}, version={nextSnap.Version}, elapsed={sw.ElapsedMilliseconds}ms");
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

        // If new events were enqueued during processing (e.g., retries), schedule another flush.
        if (!_cts.IsCancellationRequested && !_pending.IsEmpty) {
            _debounce.Interval = DebounceMs;
            _debounce.Stop();
            _debounce.Start();
        }
    }

    private async Task ApplyBatchAsync(List<WorkspaceChangeEventArgs> batch, CancellationToken ct) {
        var sw = Stopwatch.StartNew();
        try {
            var affected = CollectAffectedDocuments(batch, _workspace.CurrentSolution);
            DebugUtil.Print("IndexSync", $"Batch: events={batch.Count}, docs={affected.Count}");
            var swDelta = Stopwatch.StartNew();
            var delta = await ComputeDeltaAsync(affected, ct).ConfigureAwait(false);
            swDelta.Stop();
            DebugUtil.Print("IndexSync", $"Delta: +T={delta.TypeAdds.Count}, -T={delta.TypeRemovals.Count}, compute={swDelta.ElapsedMilliseconds}ms");

            // Build next snapshot from current snapshot + delta
            var currSnap = System.Threading.Volatile.Read(ref _snap);
            var mapB = currSnap.Entries.ToBuilder();
            if (delta.TypeRemovals is { Count: > 0 }) {
                foreach (var r in delta.TypeRemovals) {
                    var id = r.DocCommentId;
                    var asm = r.Assembly;
                    if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(asm)) { continue; }
                    if (mapB.TryGetValue(id, out var existing) && string.Equals(existing.Assembly, asm, StringComparison.Ordinal)) {
                        mapB.Remove(id);
                    }
                }
            }
            if (delta.TypeAdds is { Count: > 0 }) {
                foreach (var e in delta.TypeAdds) {
                    if (!string.IsNullOrEmpty(e.DocCommentId)) {
                        mapB[e.DocCommentId] = e;
                    }
                }
            }
            var nextIndex = currSnap.Index.WithDelta(delta);
            var nextSnap = new IndexSnapshot(nextIndex, mapB.ToImmutable(), currSnap.Version + 1);
            System.Threading.Interlocked.Exchange(ref _snap, nextSnap);
            DebugUtil.Print("IndexSync", $"Applied in {sw.ElapsedMilliseconds}ms, version={nextSnap.Version}");
        }
        catch (Exception ex) {
            DebugUtil.Print("IndexSync", $"ERROR applying batch: {ex.ToString()}");
            if (FullRebuildFallbackOnError) {
                try {
                    var full = await ComputeFullDeltaAsync(_workspace.CurrentSolution, ct).ConfigureAwait(false);
                    // Replace-style rebuild on fallback
                    var entriesB = ImmutableDictionary.CreateBuilder<string, SymbolEntry>(StringComparer.Ordinal);
                    foreach (var e in full.TypeAdds) {
                        if (!string.IsNullOrEmpty(e.DocCommentId)) {
                            entriesB[e.DocCommentId] = e;
                        }
                    }
                    var rebuiltIndex = SymbolTreeB.Empty.WithDelta(full);
                    var curr = System.Threading.Volatile.Read(ref _snap);
                    var rebuiltSnap = new IndexSnapshot(rebuiltIndex, entriesB.ToImmutable(), curr.Version + 1);
                    System.Threading.Interlocked.Exchange(ref _snap, rebuiltSnap);

                    await RebuildDocTypeMapsAsync(_workspace.CurrentSolution, ct).ConfigureAwait(false);
                    DebugUtil.Print("IndexSync", $"Fallback full rebuild succeeded: types={full.TypeAdds.Count}, version={rebuiltSnap.Version}");
                }
                catch (Exception ex2) {
                    DebugUtil.Print("IndexSync", $"Fallback full rebuild failed: {ex2.ToString()}");
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
            try {
                switch (e.Kind) {
                    // Document-level events: DocumentId 足够，让后续在当前解中查不到则按“删除路径”处理
                    case WorkspaceChangeKind.DocumentAdded:
                    case WorkspaceChangeKind.DocumentChanged:
                    case WorkspaceChangeKind.DocumentRemoved:
                    case WorkspaceChangeKind.DocumentReloaded:
                    case WorkspaceChangeKind.DocumentInfoChanged:
                        if (e.DocumentId is not null) { set.Add(e.DocumentId); }
                        break;

                    // Project-level：根据 Old/New 区分
                    case WorkspaceChangeKind.ProjectAdded:
                    case WorkspaceChangeKind.ProjectChanged:
                    case WorkspaceChangeKind.ProjectReloaded:
                        if (e.ProjectId is ProjectId pidNew) {
                            var projNew = e.NewSolution?.GetProject(pidNew) ?? solution.GetProject(pidNew);
                            if (projNew != null) {
                                foreach (var d in projNew.DocumentIds) { set.Add(d); }
                            }
                        }
                        break;
                    case WorkspaceChangeKind.ProjectRemoved:
                        if (e.ProjectId is ProjectId pidOld) {
                            var projOld = e.OldSolution?.GetProject(pidOld);
                            if (projOld != null) {
                                foreach (var d in projOld.DocumentIds) { set.Add(d); }
                            }
                        }
                        break;

                    // Solution-level：SolutionChanged 合并 Old+New；Added/Reloaded 用 New；Cleared 用 Old
                    case WorkspaceChangeKind.SolutionAdded:
                    case WorkspaceChangeKind.SolutionReloaded: {
                        var sol = e.NewSolution ?? solution;
                        foreach (var p in sol.Projects) {
                            foreach (var d in p.DocumentIds) { set.Add(d); }
                        }
                        break;
                    }
                    case WorkspaceChangeKind.SolutionCleared: {
                        var sol = e.OldSolution;
                        if (sol != null) {
                            foreach (var p in sol.Projects) {
                                foreach (var d in p.DocumentIds) { set.Add(d); }
                            }
                        }
                        break;
                    }
                    case WorkspaceChangeKind.SolutionChanged: {
                        var oldSol = e.OldSolution;
                        if (oldSol != null) {
                            foreach (var p in oldSol.Projects) {
                                foreach (var d in p.DocumentIds) { set.Add(d); }
                            }
                        }
                        var newSol = e.NewSolution ?? solution;
                        foreach (var p in newSol.Projects) {
                            foreach (var d in p.DocumentIds) { set.Add(d); }
                        }
                        break;
                    }
                    default:
                        break;
                }
            }
            catch (Exception ex) {
                DebugUtil.Print("IndexSync", $"CollectAffectedDocuments: error on {e.Kind}: {ex.ToString()}");
            }
        }
        return set;
    }

    private async Task<SymbolsDelta> ComputeFullDeltaAsync(Solution solution, CancellationToken ct) {
        var typeAdds = new List<SymbolEntry>(1024);

        foreach (var project in solution.Projects) {
            ct.ThrowIfCancellationRequested();
            var comp = await project.GetCompilationAsync(ct).ConfigureAwait(false);
            if (comp is null) { continue; }
            WalkNamespace(comp.Assembly.GlobalNamespace);

            void WalkNamespace(INamespaceSymbol ns) {
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
                var fqnLeaf = t.Name;
                var asm = t.ContainingAssembly?.Name ?? string.Empty;
                var docId = DocumentationCommentId.CreateDeclarationId(t) ?? "T:" + fqnNoGlobal;
                var parentNsFqn = t.ContainingNamespace?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ?? string.Empty;
                var parentNs = IndexStringUtil.StripGlobal(parentNsFqn);
                typeAdds.Add(new SymbolEntry(DocCommentId: docId, Assembly: asm, Kind: SymbolKinds.Type, ParentNamespaceNoGlobal: parentNs, FqnNoGlobal: fqnNoGlobal, FqnLeaf: fqnLeaf));
                foreach (var nt in t.GetTypeMembers()) {
                    ct.ThrowIfCancellationRequested();
                    WalkType(nt);
                }
            }
        }

        return SymbolsDeltaContract.Normalize(typeAdds, Array.Empty<TypeKey>());
    }

    private async Task<SymbolsDelta> ComputeDeltaAsync(HashSet<DocumentId> docs, CancellationToken ct) {
        var solution = _workspace.CurrentSolution;
        var typeAdds = new List<SymbolEntry>();
        var typeRemovals = new List<Abstractions.TypeKey>();

        foreach (var docId in docs) {
            if (docId is null) { continue; }
            var docIdNN = docId; // assert non-null for analyzers
            var doc = solution.GetDocument(docIdNN);
            DebugUtil.Print("IndexSync", $"ComputeDelta: handling doc={docId?.Id.ToString() ?? "<null>"} hasDoc={(doc != null)}");
            if (doc is null) {
                // Document has been removed from the workspace. Remove all types declared solely in this document.
                if (_docToTypeKeys.TryGetValue(docIdNN, out var oldSetRemoved) && oldSetRemoved is { Count: > 0 }) {
                    DebugUtil.Print("IndexSync", $"DocRemoved: old type keys count={oldSetRemoved.Count}");
                    foreach (var key in oldSetRemoved) {
                        if (_typeKeyToDocs.TryGetValue(key, out var ds)) {
                            var nds = ds.Remove(docIdNN);
                            if (nds.IsEmpty) {
                                // last declaration removed -> remove precise type (DocId+Assembly)
                                typeRemovals.Add(new Abstractions.TypeKey(key.DocCommentId, key.Assembly));
                                _typeKeyToDocs = _typeKeyToDocs.Remove(key);
                            }
                            else {
                                // There are still declarations elsewhere. Since the index does not support per-declaration multiplicity removal,
                                // we replace the type by remove+add using one remaining declaration to keep the final assembly consistent.
                                typeRemovals.Add(new Abstractions.TypeKey(key.DocCommentId, key.Assembly));
                                var remainingDoc = nds.First();
                                var rebuilt = await TryBuildEntryForTypeIdInDocumentAsync(solution, remainingDoc, key.DocCommentId, ct).ConfigureAwait(false);
                                if (rebuilt is not null) {
                                    typeAdds.Add(rebuilt);
                                }
                                _typeKeyToDocs = _typeKeyToDocs.SetItem(key, nds);
                            }
                        }
                        else {
                            // No mapping -> conservatively mark removal
                            typeRemovals.Add(new Abstractions.TypeKey(key.DocCommentId, key.Assembly));
                        }
                    }
                    // finally drop the document mapping
                    _docToTypeKeys = _docToTypeKeys.Remove(docIdNN);
                }
                else {
                    DebugUtil.Print("IndexSync", $"DocRemoved: no mapping found; nothing to remove");
                }
                continue;
            }

            var model = await doc.GetSemanticModelAsync(ct).ConfigureAwait(false);
            if (model is null) {
                // Semantic model not ready yet (e.g., freshly added document in the same batch).
                // Re-enqueue this document to retry on the next debounce tick to avoid dropping adds.
                _pending.Enqueue(new WorkspaceChangeEventArgs(WorkspaceChangeKind.DocumentChanged, _workspace.CurrentSolution, _workspace.CurrentSolution, doc.Project.Id, docId));
                continue;
            }
            var root = await doc.GetSyntaxRootAsync(ct).ConfigureAwait(false);
            if (root is null) {
                // Syntax root not ready yet; retry later.
                _pending.Enqueue(new WorkspaceChangeEventArgs(WorkspaceChangeKind.DocumentChanged, _workspace.CurrentSolution, _workspace.CurrentSolution, doc.Project.Id, docId));
                continue;
            }
            var declared = new List<(TypeKey Key, SymbolEntry Entry, string ParentNs)>();
            foreach (var node in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>()) {
                var sym = model.GetDeclaredSymbol(node, ct) as INamedTypeSymbol;
                if (sym is null) { continue; }
                var fqn = sym.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                var fqnNoGlobal = IndexStringUtil.StripGlobal(fqn);
                var simple = sym.Name;
                var asm = sym.ContainingAssembly?.Name ?? string.Empty;
                var sid = DocumentationCommentId.CreateDeclarationId(sym) ?? "T:" + fqnNoGlobal;
                var parentNsFqn = sym.ContainingNamespace?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ?? string.Empty;
                var parentNs = IndexStringUtil.StripGlobal(parentNsFqn);
                var entry = new SymbolEntry(DocCommentId: sid, Assembly: asm, Kind: SymbolKinds.Type, ParentNamespaceNoGlobal: parentNs, FqnNoGlobal: fqnNoGlobal, FqnLeaf: simple);
                declared.Add((new TypeKey(sid, asm), entry, parentNs));
            }

            var newKeys = declared.Select(d => d.Key).ToImmutableHashSet();
            _docToTypeKeys.TryGetValue(docIdNN, out var oldSet);
            oldSet ??= ImmutableHashSet<TypeKey>.Empty;

            var added = newKeys.Except(oldSet);
            var removed = oldSet.Except(newKeys);
            DebugUtil.Print("IndexSync", $"DocChanged: +={added.Count()} -={removed.Count()}");
            DebugUtil.Print("IndexSync", $"Doc {docIdNN.Id}: declared={newKeys.Count}, +={added.Count()}, -={removed.Count()}");

            // Adds
            foreach (var key in added) {
                var e = declared.First(d => d.Key.Equals(key)).Entry;
                typeAdds.Add(e);
            }

            // Removals with partial check
            foreach (var key in removed) {
                if (_typeKeyToDocs.TryGetValue(key, out var docsSet)) {
                    var left = docsSet.Remove(docIdNN);
                    if (left.IsEmpty) {
                        typeRemovals.Add(new Abstractions.TypeKey(key.DocCommentId, key.Assembly));
                    }
                }
                else {
                    // unknown mapping -> treat as removable
                    typeRemovals.Add(new Abstractions.TypeKey(key.DocCommentId, key.Assembly));
                }
            }

            // Update maps for this document
            _docToTypeKeys = _docToTypeKeys.SetItem(docIdNN, newKeys);
            // update typeId -> docs mapping for added/removed
            foreach (var key in added) {
                _typeKeyToDocs.TryGetValue(key, out var ds);
                ds = (ds ?? ImmutableHashSet<DocumentId>.Empty).Add(docIdNN);
                _typeKeyToDocs = _typeKeyToDocs.SetItem(key, ds);
            }
            foreach (var key in removed) {
                if (_typeKeyToDocs.TryGetValue(key, out var ds)) {
                    var nds = ds.Remove(docIdNN);
                    if (nds.IsEmpty) {
                        _typeKeyToDocs = _typeKeyToDocs.Remove(key);
                    }
                    else {
                        _typeKeyToDocs = _typeKeyToDocs.SetItem(key, nds);
                    }
                }
            }
        }

        DebugUtil.Print("IndexSync", $"ComputeDelta: result +T={typeAdds.Count} -T={typeRemovals.Count}");
        return SymbolsDeltaContract.Normalize(typeAdds, typeRemovals);
    }

    private static async Task<SymbolEntry?> TryBuildEntryForTypeIdInDocumentAsync(Solution solution, DocumentId docId, string typeDocId, CancellationToken ct) {
        var doc = solution.GetDocument(docId);
        if (doc is null) { return null; }
        var model = await doc.GetSemanticModelAsync(ct).ConfigureAwait(false);
        if (model is null) { return null; }
        var root = await doc.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        if (root is null) { return null; }
        foreach (var node in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>()) {
            var sym = model.GetDeclaredSymbol(node, ct) as INamedTypeSymbol;
            if (sym is null) { continue; }
            var fqn = sym.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            var fqnNoGlobal = IndexStringUtil.StripGlobal(fqn);
            var sid = DocumentationCommentId.CreateDeclarationId(sym) ?? "T:" + fqnNoGlobal;
            if (!string.Equals(sid, typeDocId, StringComparison.Ordinal)) { continue; }
            var asm = sym.ContainingAssembly?.Name ?? string.Empty;
            var parentNsFqn = sym.ContainingNamespace?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ?? string.Empty;
            var parentNs = IndexStringUtil.StripGlobal(parentNsFqn);
            var entry = new SymbolEntry(
                DocCommentId: sid,
                Assembly: asm,
                Kind: SymbolKinds.Type,
                ParentNamespaceNoGlobal: parentNs,
                FqnNoGlobal: fqnNoGlobal,
                FqnLeaf: sym.Name
            );
            return entry;
        }
        return null;
    }

    internal async Task RebuildDocTypeMapsAsync(Solution solution, CancellationToken ct) {
        var docTo = ImmutableDictionary.CreateBuilder<DocumentId, ImmutableHashSet<TypeKey>>();
        var typeTo = ImmutableDictionary.CreateBuilder<TypeKey, ImmutableHashSet<DocumentId>>();

        foreach (var project in solution.Projects) {
            ct.ThrowIfCancellationRequested();
            foreach (var doc in project.Documents) {
                var model = await doc.GetSemanticModelAsync(ct).ConfigureAwait(false);
                if (model is null) { continue; }
                var root = await doc.GetSyntaxRootAsync(ct).ConfigureAwait(false);
                if (root is null) { continue; }
                var setBuilder = ImmutableHashSet.CreateBuilder<TypeKey>();
                foreach (var node in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>()) {
                    var sym = model.GetDeclaredSymbol(node, ct) as INamedTypeSymbol;
                    if (sym is null) { continue; }
                    var fqn = sym.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                    var fqnNoGlobal = IndexStringUtil.StripGlobal(fqn);
                    var sid = DocumentationCommentId.CreateDeclarationId(sym) ?? "T:" + fqnNoGlobal;
                    var asm = sym.ContainingAssembly?.Name ?? string.Empty;
                    setBuilder.Add(new TypeKey(sid, asm));
                }
                var set = setBuilder.ToImmutable();
                docTo[doc.Id] = set;
                foreach (var key in set) {
                    if (!typeTo.TryGetValue(key, out var ds)) {
                        ds = ImmutableHashSet<DocumentId>.Empty;
                    }

                    typeTo[key] = ds.Add(doc.Id);
                }
            }
        }

        _docToTypeKeys = docTo.ToImmutable();
        _typeKeyToDocs = typeTo.ToImmutable();

        // Health stats & reconciliation log
        var solDocIds = new HashSet<DocumentId>(solution.Projects.SelectMany(p => p.DocumentIds));
        var mapDocIds = new HashSet<DocumentId>(_docToTypeKeys.Keys);
        var outOfSolutionDocIdsInDocMap = mapDocIds.Where(id => !solDocIds.Contains(id)).Count();
        var outOfSolutionDocIdsInTypeMap = _typeKeyToDocs.SelectMany(kv => kv.Value).Where(id => !solDocIds.Contains(id)).Distinct().Count();
        var orphanTypeIds = _typeKeyToDocs.Count(kv => kv.Value == null || kv.Value.IsEmpty);
        var docsMissingInDocMap = solDocIds.Count - mapDocIds.Count;
        DebugUtil.Print("IndexSync", $"RebuildDocTypeMaps: docMap={mapDocIds.Count}, typeMap={_typeKeyToDocs.Count}, outDoc(docMap)={outOfSolutionDocIdsInDocMap}, outDoc(typeMap)={outOfSolutionDocIdsInTypeMap}, orphanTypes={orphanTypeIds}, solDocs={solDocIds.Count}, docsMissingInDocMap={docsMissingInDocMap}");
    }

    // Internal health snapshot for tests/diagnostics.
    internal (int DocMapCount, int TypeMapCount, int OutDocInDocMap, int OutDocInTypeMap, int OrphanTypeIds, int SolutionDocCount, int DocsMissingInDocMap)
        GetMappingHealth(Solution solution) {
        var solDocIds = new HashSet<DocumentId>(solution.Projects.SelectMany(p => p.DocumentIds));
        var mapDocIds = new HashSet<DocumentId>(_docToTypeKeys.Keys);
        var outOfSolutionDocIdsInDocMap = mapDocIds.Where(id => !solDocIds.Contains(id)).Count();
        var outOfSolutionDocIdsInTypeMap = _typeKeyToDocs.SelectMany(kv => kv.Value).Where(id => !solDocIds.Contains(id)).Distinct().Count();
        var orphanTypeIds = _typeKeyToDocs.Count(kv => kv.Value == null || kv.Value.IsEmpty);
        var docsMissingInDocMap = solDocIds.Count - mapDocIds.Count;
        return (mapDocIds.Count, _typeKeyToDocs.Count, outOfSolutionDocIdsInDocMap, outOfSolutionDocIdsInTypeMap, orphanTypeIds, solDocIds.Count, docsMissingInDocMap);
    }

    // Note: Namespace chain materialization and namespace cascade removals are handled inside ISymbolIndex.WithDelta.
}
