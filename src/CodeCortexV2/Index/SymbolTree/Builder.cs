using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace CodeCortexV2.Index.SymbolTreeInternal {
    /// <summary>
    /// Minimal builder for SymbolTree. This is a placeholder that will be extended to
    /// construct nodes and name tables from SymbolEntry snapshots and deltas.
    /// </summary>
    internal sealed class Builder {
        private readonly List<Node> _nodes = new();
        private readonly List<string> _names = new();
        private readonly Dictionary<string, int> _nameMap = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ImmutableArray<int>> _nameDict = new(StringComparer.Ordinal);

        public Builder() { }

        public void Clear() {
            _nodes.Clear();
            _names.Clear();
            _nameMap.Clear();
            _nameDict.Clear();
        }

        public (ImmutableArray<Node> nodes, NameTable nameTable, Dictionary<string, ImmutableArray<int>> nameDict) Build() {
            return (ImmutableArray.CreateRange(_nodes),
                    new NameTable(ImmutableArray.CreateRange(_names), _nameMap),
                    _nameDict);
        }

        // TODO: Add methods to insert namespaces/types and wire siblings/children.
    }
}

