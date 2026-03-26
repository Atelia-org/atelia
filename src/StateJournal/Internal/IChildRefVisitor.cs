namespace Atelia.StateJournal.Internal;

/// <summary>GC 图遍历时的子引用访问器。同时覆盖 DurableObject（LocalId）和 Symbol（SymbolId）两种引用。</summary>
internal interface IChildRefVisitor {
    void Visit(LocalId childId);
    void Visit(SymbolId symbolId);
}
