namespace Atelia.TextAdv2.Runtime;

/// <summary>
/// 标识一次进程内 runtime 生命周期。
///
/// durable world reopen 后会生成新的 epoch，用于明确区分易失状态边界。
/// 当前它仍只作为 internal strong type 使用；
/// 若需要暴露给 machine-facing DTO，应先投影为字符串边界 token，而不是直接公开该类型。
/// </summary>
internal readonly record struct RuntimeEpochId(Guid Value) {
    public static RuntimeEpochId CreateNew() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("N");
}
