using System.Globalization;

namespace Atelia.SessionJournal.MemoPod;

public readonly record struct MemoId {
    public const string Prefix = "m1:";
    public const int HexLength = 8;
    public const int TextLength = 11;

    private readonly uint _ordinal;

    private MemoId(uint ordinal) {
        _ordinal = ordinal;
    }

    public string Value => IsDefault
        ? string.Empty
        : string.Create(
            CultureInfo.InvariantCulture,
            $"{Prefix}{_ordinal:x8}"
        );

    internal bool IsDefault => _ordinal == 0;
    internal uint Ordinal => _ordinal;

    public static MemoId Parse(string value) {
        ArgumentNullException.ThrowIfNull(value);
        return TryParse(value, out MemoId memoId)
            ? memoId
            : throw new FormatException(
                "MemoId must use the canonical m1: prefix followed by eight lowercase hexadecimal characters for a non-zero ordinal."
            );
    }

    public static bool TryParse(string? value, out MemoId memoId) {
        memoId = default;
        if (value is null
            || value.Length != TextLength
            || !value.StartsWith(Prefix, StringComparison.Ordinal)) {
            return false;
        }

        ReadOnlySpan<char> hex = value.AsSpan(Prefix.Length);
        if (!MemoPodSyntax.IsLowerHex(hex)
            || !uint.TryParse(
                hex,
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out uint ordinal)
            || ordinal == 0) {
            return false;
        }

        memoId = new MemoId(ordinal);
        return true;
    }

    internal static MemoId FromOrdinal(uint ordinal) {
        if (ordinal == 0) {
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        }
        return new MemoId(ordinal);
    }

    public override string ToString() => Value;
}
