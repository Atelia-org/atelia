namespace Atelia.StateJournal.Internal;

/// <summary>
/// 版本链数据损坏：序列化/反序列化过程中检测到非法数据。
/// </summary>
internal sealed record SjCorruptionError(
    string Message,
    string? RecoveryHint = null,
    IReadOnlyDictionary<string, string>? Details = null,
    AteliaError? Cause = null
) : AteliaError("SJ.Corruption", Message, RecoveryHint, Details, Cause);

/// <summary>
/// 类型解析失败：无法从类型码解码出类型，或无法实例化该类型的 DurableObject。
/// </summary>
internal sealed record SjTypeResolutionError(
    string Message,
    string? RecoveryHint = null,
    IReadOnlyDictionary<string, string>? Details = null,
    AteliaError? Cause = null
) : AteliaError("SJ.TypeResolution", Message, RecoveryHint, Details, Cause);

/// <summary>
/// 运行时状态或 I/O 异常导致提交流程中断。
/// </summary>
internal sealed record SjStateError(
    string Message,
    string? RecoveryHint = null,
    IReadOnlyDictionary<string, string>? Details = null,
    AteliaError? Cause = null
) : AteliaError("SJ.State", Message, RecoveryHint, Details, Cause);

/// <summary>
/// primary commit 已成功后，Compaction 应用阶段（MoveSlot/Rebind/引用重写）失败。
/// </summary>
internal sealed record SjCompactionApplyError(
    string Message,
    string? RecoveryHint = null,
    IReadOnlyDictionary<string, string>? Details = null,
    AteliaError? Cause = null
) : AteliaError("SJ.Compaction.ApplyFailed", Message, RecoveryHint, Details, Cause);

/// <summary>
/// primary commit 已成功后，Compaction 触发的后续持久化提交失败。
/// </summary>
internal sealed record SjCompactionFollowupPersistError(
    string Message,
    string? RecoveryHint = null,
    IReadOnlyDictionary<string, string>? Details = null,
    AteliaError? Cause = null
) : AteliaError("SJ.Compaction.FollowupPersistFailed", Message, RecoveryHint, Details, Cause);

/// <summary>
/// Compaction 应用失败后，回滚阶段失败（或回滚能力尚未实现）。
/// </summary>
internal sealed record SjCompactionRollbackError(
    string Message,
    string? RecoveryHint = null,
    IReadOnlyDictionary<string, string>? Details = null,
    AteliaError? Cause = null
) : AteliaError("SJ.Compaction.RollbackFailed", Message, RecoveryHint, Details, Cause);
