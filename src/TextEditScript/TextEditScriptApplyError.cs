namespace Atelia.TextEditScript;

public sealed record TextEditScriptApplyError(
    string Message,
    string? RecoveryHint = null,
    IReadOnlyDictionary<string, string>? Details = null,
    AteliaError? Cause = null)
    : AteliaError("TextEditScript.Apply", Message, RecoveryHint, Details, Cause);
