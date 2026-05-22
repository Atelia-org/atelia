namespace Atelia.TextAdv;

internal sealed record TextAdvError(
    string ErrorCode,
    string Message,
    string? RecoveryHint = null,
    IReadOnlyDictionary<string, string>? Details = null,
    AteliaError? Cause = null
)
    : AteliaError(ErrorCode, Message, RecoveryHint, Details, Cause);
