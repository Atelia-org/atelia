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
/// 由对象图状态、宿主环境或 I/O 等外部不可控因素导致的提交流程失败。
/// 纯实现 bug / 内部不变量破坏不应使用此错误建模。
/// </summary>
internal sealed record SjStateError(
    string Message,
    string? RecoveryHint = null,
    IReadOnlyDictionary<string, string>? Details = null,
    AteliaError? Cause = null
) : AteliaError("SJ.State", Message, RecoveryHint, Details, Cause);

/// <summary>
/// primary commit 已成功后，Compaction 触发的后续持久化提交失败。
/// 该错误对应外部不可控因素（如 I/O / 文件状态），会回到 <see cref="CommitOutcome"/> 作为可诊断结果。
/// </summary>
internal sealed record SjCompactionPersistError(
    string Message,
    string? RecoveryHint = null,
    IReadOnlyDictionary<string, string>? Details = null,
    AteliaError? Cause = null
) : AteliaError("SJ.Compaction.FollowupPersistFailed", Message, RecoveryHint, Details, Cause);

/// <summary>
/// Repository 层面的操作失败：文件发现、锁获取、head.json 读写等。
/// </summary>
internal sealed record SjRepositoryError(
    string Message,
    string? RecoveryHint = null,
    IReadOnlyDictionary<string, string>? Details = null,
    AteliaError? Cause = null
) : AteliaError("SJ.Repository", Message, RecoveryHint, Details, Cause);
