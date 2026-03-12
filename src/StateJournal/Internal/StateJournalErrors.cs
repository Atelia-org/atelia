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
