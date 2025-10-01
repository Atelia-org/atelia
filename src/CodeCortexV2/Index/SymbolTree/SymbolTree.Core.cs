using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.InteropServices;

using Atelia.Diagnostics;
using CodeCortexV2.Abstractions;

namespace CodeCortexV2.Index.SymbolTreeInternal;

internal readonly record struct AliasRelation(MatchFlags Kind, int NodeId);

/// <summary>
/// Tick-Tock buffer: alternative tree-based index implementing the two-layer alias design.
/// - Structure layer: immutable Node array + entry refs
/// - Alias layer: exact vs non-exact alias → node ids
/// </summary>
internal sealed partial class SymbolTreeB : ISymbolIndex {
    private readonly ImmutableArray<NodeB> _nodes;
    private readonly int _freeHead;

    private readonly Dictionary<string, ImmutableArray<AliasRelation>> _exactAliasToNodes;    // case-sensitive aliases
    private readonly Dictionary<string, ImmutableArray<AliasRelation>> _nonExactAliasToNodes; // generic-base / ignore-case etc.

    private SymbolTreeB(
        ImmutableArray<NodeB> nodes,
        Dictionary<string, ImmutableArray<AliasRelation>> exactAliasToNodes,
        Dictionary<string, ImmutableArray<AliasRelation>> nonExactAliasToNodes,
        int freeHead
    ) {
        _nodes = nodes;
        _exactAliasToNodes = exactAliasToNodes;
        _nonExactAliasToNodes = nonExactAliasToNodes;
        _freeHead = freeHead;
    }

    private SymbolTreeBuilder CloneBuilder() => new(
        _nodes.ToList(),
        new Dictionary<string, ImmutableArray<AliasRelation>>(_exactAliasToNodes, _exactAliasToNodes.Comparer),
        new Dictionary<string, ImmutableArray<AliasRelation>>(_nonExactAliasToNodes, _nonExactAliasToNodes.Comparer),
        _freeHead
    );

    internal ImmutableArray<NodeB> DebugNodes => _nodes;

    public static SymbolTreeB Empty { get; } = new(
        ImmutableArray<NodeB>.Empty,
        new Dictionary<string, ImmutableArray<AliasRelation>>(StringComparer.Ordinal),
        new Dictionary<string, ImmutableArray<AliasRelation>>(StringComparer.Ordinal),
        freeHead: -1
    );
}
