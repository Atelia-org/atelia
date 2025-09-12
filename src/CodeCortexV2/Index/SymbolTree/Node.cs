using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace CodeCortexV2.Index.SymbolTreeInternal {
    /// <summary>
    /// Immutable node store for SymbolTree. Nodes are addressed by integer index.
    /// </summary>
    internal readonly struct Node {
        public readonly int NameId;
        public readonly int Parent;       // -1 for root
        public readonly int FirstChild;   // -1 if none
        public readonly int NextSibling;  // -1 if none
        public readonly NodeKind Kind;    // Namespace or Type (members later)
        public readonly int EntryStart;   // start index in entryRefs; -1 if none
        public readonly int EntryCount;   // number of entry refs for this node

        public Node(int nameId, int parent, int firstChild, int nextSibling, NodeKind kind, int entryStart, int entryCount) {
            NameId = nameId;
            Parent = parent;
            FirstChild = firstChild;
            NextSibling = nextSibling;
            Kind = kind;
            EntryStart = entryStart;
            EntryCount = entryCount;
        }

        public bool HasEntries => EntryCount > 0;
    }

    internal enum NodeKind { Namespace = 1, Type = 2 }

    /// <summary>
    /// String table for names; supports aliasing by inserting multiple keys mapping to the same nameId.
    /// </summary>
    internal sealed class NameTable {
        private readonly ImmutableArray<string> _names; // id -> canonical name
        private readonly Dictionary<string, int> _map;  // alias -> id

        public NameTable(ImmutableArray<string> names, Dictionary<string, int> map) {
            _names = names;
            _map = map;
        }

        public int Count => _names.Length;
        public string this[int id] => _names[id];
        public bool TryGetId(string name, out int id) => _map.TryGetValue(name, out id);
        public IEnumerable<KeyValuePair<string, int>> Aliases => _map;

        public static NameTable Empty { get; } = new(
            ImmutableArray<string>.Empty,
            new Dictionary<string, int>(StringComparer.Ordinal)
        );
    }
}

