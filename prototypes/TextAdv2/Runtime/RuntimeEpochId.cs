namespace Atelia.TextAdv2.Runtime;

/// <summary>
/// 标识一次进程内 runtime 生命周期。
///
/// durable world reopen 后会生成新的 epoch，用于明确区分易失状态边界。
/// 当前它只作为 internal runtime seam，不进入 machine-facing DTO。
/// </summary>
internal readonly record struct RuntimeEpochId(Guid Value) {
    public static RuntimeEpochId CreateNew() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("N");
}
