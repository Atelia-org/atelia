using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

using Atelia.Diagnostics;
using CodeCortexV2.Abstractions;

namespace CodeCortexV2.Index.SymbolTreeInternal;

partial class SymbolTree {
    /// <summary>
    /// Apply a leaf-oriented <see cref="SymbolsDelta"/> to the current immutable tree and return a new snapshot.
    /// Expectations (namespace fields on delta are deprecated):
    /// - Internally handle namespace chain materialization for added types and cascading deletion for empty namespaces,
    ///   limited to impacted subtrees; avoid full-index scans in production.
    /// - Locality: time/memory costs should scale with the size of <paramref name="delta"/>, not the whole index.
    /// - Idempotency: applying the same delta repeatedly should not change the resulting state.
    /// - Ordering precondition: <paramref name="delta"/>.TypeAdds is sorted by ascending DocCommentId length and
    ///   TypeRemovals is sorted by descending length (DocIds start with "T:"). The builder assumes parents are
    ///   materialized before nested types and may assert/throw when the contract is violated.
    /// - Optional defensive checks: lightweight validations (Debug.Assert, exceptions) and DebugUtil diagnostics are allowed.
    /// </summary>
    public ISymbolIndex WithDelta(SymbolsDelta delta) {
        if (delta is null) { return this; }

        // P0 implementation notes:
        // - Localized edits on a mutable copy of nodes (List<Node>), patching only affected parents/siblings.
        // - Alias maps: for simplicity, we shallow-copy whole dictionaries, then replace mutated buckets per-key.
        //   This is O(#alias-keys) copy once; acceptable for P0 and will be optimized later with true COW.
        bool wasEmptySnapshot = _nodes.Length == 0;
        var builder = wasEmptySnapshot
            ? SymbolTreeBuilder.CreateEmpty()
            : CloneBuilder();
        var stats = builder.ApplyDelta(delta);

        if (stats.TypeAddCount == 0 &&
            stats.TypeRemovalCount == 0 &&
            stats.CascadeCandidateCount == 0 &&
            stats.DeletedNamespaceCount == 0 &&
            stats.ReusedNodeCount == 0 &&
            stats.FreedNodeCount == 0) { return this; }

        DebugUtil.Print("SymbolTree.WithDelta", $"EmptySnapshot={wasEmptySnapshot}, TypeAdds={stats.TypeAddCount}, TypeRemovals={stats.TypeRemovalCount}, CascadeCandidates={stats.CascadeCandidateCount}, DeletedNamespaces={stats.DeletedNamespaceCount}, ReusedNodes={stats.ReusedNodeCount}, FreedNodes={stats.FreedNodeCount}");

        var newTree = new SymbolTree(
            builder.Nodes.ToImmutableArray(),
            builder.ExactAliases,
            builder.NonExactAliases,
            builder.FreeHead
        );
        return newTree;
    }
}
