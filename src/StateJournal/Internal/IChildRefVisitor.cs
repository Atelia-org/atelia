namespace Atelia.StateJournal.Internal;

/// <summary>
/// GC 图遍历时的子引用访问器。
/// 同时覆盖 DurableObject（LocalId）、typed symbol facade（Symbol）以及 mixed 容器里直接存储的 SymbolId。
/// </summary>
internal interface IChildRefVisitor {
    void Visit(LocalId childId);
    void Visit(Symbol value);
    void Visit(SymbolId symbolId);
}
