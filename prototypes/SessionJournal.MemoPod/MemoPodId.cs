namespace Atelia.SessionJournal.MemoPod;

public readonly record struct MemoPodId {
    public const int TextLength = 32;

    private readonly string? _value;

    private MemoPodId(string value) {
        _value = value;
    }

    public string Value => _value ?? string.Empty;

    internal bool IsDefault => _value is null;

    public static MemoPodId Parse(string value) {
        ArgumentNullException.ThrowIfNull(value);
        return TryParse(value, out MemoPodId podId)
            ? podId
            : throw new FormatException(
                "MemoPodId must be exactly 32 lowercase hexadecimal characters and must not be all zero."
            );
    }

    public static bool TryParse(string? value, out MemoPodId podId) {
        podId = default;
        if (value is null
            || value.Length != TextLength
            || !MemoPodSyntax.IsLowerHex(value)) {
            return false;
        }

        bool allZero = true;
        foreach (char character in value) {
            if (character != '0') {
                allZero = false;
                break;
            }
        }
        if (allZero) {
            return false;
        }

        podId = new MemoPodId(value);
        return true;
    }

    public override string ToString() => Value;
}
