namespace Atelia.StateJournal.Internal;

/// <summary>
/// GC 图遍历时的子引用访问器。
/// 同时覆盖 DurableObject（LocalId）、typed string facade（string）以及 mixed 容器里直接存储的 SymbolId。
/// </summary>
internal interface IChildRefVisitor {
    void Visit(LocalId childId);
    void Visit(string? value);
    void Visit(SymbolId symbolId);
}
