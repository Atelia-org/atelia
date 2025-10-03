using CodeCortexV2.Abstractions;

namespace CodeCortexV2.Index.SymbolTreeInternal;

internal enum NodeKind { Namespace = 1, Type = 2 }

/// <summary>
/// Node implementation.
/// - Holds at most one entry (EntryIndex), favoring simplicity over indirection.
/// - For duplicate types across assemblies, multiple sibling nodes will exist.
/// </summary>
internal readonly struct Node {
    public readonly string Name;
    public readonly int Parent;      // -1 for root
    public readonly int FirstChild;  // -1 if none
    public readonly int NextSibling; // -1 if none
    public readonly NodeKind Kind;   // Namespace or Type
    public readonly SymbolEntry? Entry;  // null if none

    public Node(string name, int parent, int firstChild, int nextSibling, NodeKind kind, SymbolEntry? entry) {
        Name = name;
        Parent = parent;
        FirstChild = firstChild;
        NextSibling = nextSibling;
        Kind = kind;
        Entry = entry;
    }
}

