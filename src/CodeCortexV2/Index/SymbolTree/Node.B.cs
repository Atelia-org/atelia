using System.Collections.Immutable;

namespace CodeCortexV2.Index.SymbolTreeInternal {
    /// <summary>
    /// Node variant for the Tick-Tock B implementation.
    /// - Holds at most one entry (EntryIndex), favoring simplicity over indirection.
    /// - For duplicate types across assemblies, multiple sibling nodes will exist.
    /// </summary>
    internal readonly struct NodeB {
        public readonly int NameId;
        public readonly int Parent;      // -1 for root
        public readonly int FirstChild;  // -1 if none
        public readonly int NextSibling; // -1 if none
        public readonly NodeKind Kind;   // Namespace or Type
        public readonly int EntryIndex;  // -1 if none

        public NodeB(int nameId, int parent, int firstChild, int nextSibling, NodeKind kind, int entryIndex) {
            NameId = nameId;
            Parent = parent;
            FirstChild = firstChild;
            NextSibling = nextSibling;
            Kind = kind;
            EntryIndex = entryIndex;
        }

        public bool HasEntry => EntryIndex >= 0;
    }
}

