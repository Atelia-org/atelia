namespace Atelia.TextEditScript;

public sealed record TextEditScriptParseError(
    string Message,
    string? RecoveryHint = null,
    IReadOnlyDictionary<string, string>? Details = null,
    AteliaError? Cause = null)
    : AteliaError("TextEditScript.Parse", Message, RecoveryHint, Details, Cause);
