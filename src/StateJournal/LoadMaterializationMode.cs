namespace Atelia.StateJournal;

/// <summary>
/// 控制 committed state 在 load/replay 完成后的 current working 视图物化形态。
/// 不改变磁盘上 committed object flags 的真相，只覆盖当前内存对象的 materialization 方式。
/// </summary>
public enum LoadMaterializationMode {
    /// <summary>按持久化 object flags 原样物化 current 视图。</summary>
    Normal = 0,

    /// <summary>无论 committed flags 是否为 frozen，都将 current 视图物化为 mutable。</summary>
    ForceMutable = 1,

    /// <summary>无论 committed flags 是否为 frozen，都将 current 视图物化为 frozen。</summary>
    ForceFrozen = 2,
}
