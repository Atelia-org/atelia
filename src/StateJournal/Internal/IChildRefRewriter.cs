namespace Atelia.StateJournal.Internal;

/// <summary>Compaction 时的子引用重写器：若 oldId 需要重映射则返回新值，否则原样返回。
/// 同时覆盖 DurableObject（LocalId）和 Symbol（SymbolId）两种引用。</summary>
internal interface IChildRefRewriter {
    LocalId Rewrite(LocalId oldId);
    SymbolId Rewrite(SymbolId oldId);
}
