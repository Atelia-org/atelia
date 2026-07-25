using System.Globalization;
using Atelia.Data;
using Atelia.EventJournal;

namespace Atelia.SessionJournal;

/// <summary>
/// Canonical, culture-independent textual representation of an EventJournal address.
/// </summary>
public static class EventAddressTextCodec {
    public const string Prefix = "ej1:";
    public const int HexLength = 32;
    public const int TextLength = 36;

    public static string Format(EventAddress address)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{Prefix}{address.Ticket.Packed:x16}{address.SegmentNumber:x8}{address.Hint.Packed:x8}"
        );

    public static string? FormatNullable(EventAddress? address)
        => address is null ? null : Format(address.Value);

    public static EventAddress Parse(string value)
        => TryParse(value, out var address)
            ? address
            : throw new FormatException($"Invalid EventAddress text '{value}'.");

    public static EventAddress? ParseNullable(string? value)
        => TryParseNullable(value, out var address)
            ? address
            : throw new FormatException($"Invalid nullable EventAddress text '{value}'.");

    public static bool TryParse(string? value, out EventAddress address) {
        address = default;
        if (value is null ||
            value.Length != TextLength ||
            !value.StartsWith(Prefix, StringComparison.Ordinal)) {
            return false;
        }

        ReadOnlySpan<char> hex = value.AsSpan(Prefix.Length);
        if (!IsLowerHex(hex)) { return false; }
        if (!ulong.TryParse(hex[..16], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong ticketPacked) ||
            !uint.TryParse(hex[16..24], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint segmentNumber) ||
            !uint.TryParse(hex[24..32], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hintPacked)) {
            return false;
        }

        if (ticketPacked == 0 || segmentNumber == 0) { return false; }

        address = new EventAddress(SizedPtr.FromPacked(ticketPacked), segmentNumber, new AddressHint(hintPacked));
        return true;
    }

    public static bool TryParseNullable(string? value, out EventAddress? address) {
        address = null;
        if (value is null) { return true; }
        if (!TryParse(value, out var parsed)) { return false; }
        address = parsed;
        return true;
    }

    private static bool IsLowerHex(ReadOnlySpan<char> text) {
        foreach (char ch in text) {
            if ((ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f')) { continue; }
            return false;
        }

        return true;
    }

    internal static string GetPhysicalCoordinateSortKey(string value) {
        if (!TryParse(value, out var address)) { return string.Empty; }
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{address.SegmentNumber:x8}:{address.Ticket.Offset:x16}:{address.Ticket.Length:x8}:{address.Hint.Packed:x8}"
        );
    }
}
