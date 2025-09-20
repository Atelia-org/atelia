
namespace CodeCortexV2.Abstractions;
public interface ISymbolIndex {
    SearchResults Search(string query, int limit, int offset, SymbolKinds kinds);
    /// <summary>
    /// Apply a normalized, closed-over <see cref="SymbolsDelta"/> to produce a new immutable snapshot.
    /// Contract and expectations:
    /// - Input delta MUST be pre-normalized and closure-complete by the producer (e.g., <c>IndexSynchronizer</c>):
    ///   - Adds must ensure ancestor namespaces are present (namespace chain is complete).
    ///   - Removals must include any namespaces that become empty in this batch (including conservative cascading removals),
    ///     according to the producer's policy.
    /// - This method should be a pure application of the delta relative to the current snapshot, without performing
    ///   global scans or re-inference of missing removals or additions.
    /// - Idempotency: applying the same delta multiple times yields the same resulting index state.
    /// - Locality: time/memory complexity should be proportional to the size of the delta, not the whole index.
    /// - Defensive checks: implementations MAY include lightweight, optional validations or opportunistic cleanup restricted
    ///   to impacted subtrees, but MUST NOT rely on full-index traversal. Such behavior should be guarded by a debug or
    ///   explicit consistency option and produce diagnostic logs instead of throwing in production builds.
    ///
    /// Producer responsibilities (e.g., <c>IndexSynchronizer</c>):
    /// - Deduplication and conflict resolution inside a single delta (e.g., no simultaneous add+remove of the same id).
    /// - Rename should be represented as remove(oldId) + add(newEntry).
    /// - Building a self-consistent delta even when events are coalesced or debounced.
    /// </summary>
    ISymbolIndex WithDelta(SymbolsDelta delta);
}
