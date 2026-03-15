namespace Atelia.StateJournal.Internal;

/// <summary>GC 图遍历时的子引用访问器。</summary>
internal interface IChildRefVisitor {
    void Visit(LocalId childId);
}
