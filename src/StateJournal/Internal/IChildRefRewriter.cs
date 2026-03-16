namespace Atelia.StateJournal.Internal;

/// <summary>Compaction 时的子引用重写器：若 oldId 需要重映射则返回新值，否则原样返回。</summary>
internal interface IChildRefRewriter {
    LocalId Rewrite(LocalId oldId);
}
