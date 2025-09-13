// using System;
// using System.Collections.Generic;
// using System.Collections.Immutable;

// namespace CodeCortexV2.Index.SymbolTreeInternal {
//     /// <summary>
//     /// Immutable node store for SymbolTree. Nodes are addressed by integer index.
//     /// </summary>
//     internal readonly struct Node {
//         public readonly int NameId;
//         public readonly int Parent;       // -1 for root
//         public readonly int FirstChild;   // -1 if none
//         public readonly int NextSibling;  // -1 if none
//         public readonly NodeKind Kind;    // Namespace or Type (members later)
//         public readonly int EntryStart;   // start index in entryRefs; -1 if none
//         public readonly int EntryCount;   // number of entry refs for this node

//         public Node(int nameId, int parent, int firstChild, int nextSibling, NodeKind kind, int entryStart, int entryCount) {
//             NameId = nameId;
//             Parent = parent;
//             FirstChild = firstChild;
//             NextSibling = nextSibling;
//             Kind = kind;
//             EntryStart = entryStart;
//             EntryCount = entryCount;
//         }

//         public bool HasEntries => EntryCount > 0;
//     }

//     internal enum NodeKind { Namespace = 1, Type = 2 }
// }

