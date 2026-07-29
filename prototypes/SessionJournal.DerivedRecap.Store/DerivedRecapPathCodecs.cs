using Atelia.EventJournal;

namespace Atelia.SessionJournal.DerivedRecap.Store;

public static class EventAddressFileNameCodec {
    public const int TextLength = EventAddressCodec.EventAddressLength * 2;

    public static string Format(EventAddress address) {
        Span<byte> bytes =
            stackalloc byte[EventAddressCodec.EventAddressLength];
        EventAddressCodec.Encode(address, bytes);
        return Convert.ToHexStringLower(bytes);
    }

    public static EventAddress Parse(string value)
        => TryParse(value, out EventAddress address)
            ? address
            : throw new FormatException(
                $"Invalid EventAddress filename token '{value}'."
            );

    public static bool TryParse(string? value, out EventAddress address) {
        address = default;
        if (value is null
            || value.Length != TextLength
            || value.Any(static ch =>
                !((ch >= '0' && ch <= '9')
                  || (ch >= 'a' && ch <= 'f')))) {
            return false;
        }
        byte[] bytes;
        try {
            bytes = Convert.FromHexString(value);
        }
        catch (FormatException) {
            return false;
        }
        var result = EventAddressCodec.Decode(bytes);
        if (result.IsFailure) {
            return false;
        }
        address = result.Unwrap();
        return true;
    }
}
