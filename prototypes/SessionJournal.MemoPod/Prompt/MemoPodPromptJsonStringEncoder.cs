using System.Buffers;
using System.Text;

namespace Atelia.SessionJournal.MemoPod;

internal static class MemoPodPromptJsonStringEncoder {
    private const string LowerHex = "0123456789abcdef";

    internal static int GetEncodedUtf8ByteCount(
        string value,
        string parameterName
    ) {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        ReadOnlySpan<char> remaining = value;
        int byteCount = 0;
        while (!remaining.IsEmpty) {
            Rune rune = DecodeNext(remaining, parameterName, out int consumed);
            byteCount = checked(byteCount + GetEncodedUtf8ByteCount(rune));
            remaining = remaining[consumed..];
        }
        return byteCount;
    }

    internal static int WriteEncodedUtf8(
        string value,
        Span<byte> destination,
        string parameterName
    ) {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        ReadOnlySpan<char> remaining = value;
        int written = 0;
        while (!remaining.IsEmpty) {
            Rune rune = DecodeNext(remaining, parameterName, out int consumed);
            int encodedLength = GetEncodedUtf8ByteCount(rune);
            if (destination.Length - written < encodedLength) {
                throw new ArgumentException(
                    "The destination is too short for the encoded JSON string.",
                    nameof(destination)
                );
            }
            written += WriteRune(rune, destination[written..]);
            remaining = remaining[consumed..];
        }
        return written;
    }

    private static Rune DecodeNext(
        ReadOnlySpan<char> value,
        string parameterName,
        out int consumed
    ) {
        OperationStatus status = Rune.DecodeFromUtf16(
            value,
            out Rune rune,
            out consumed
        );
        if (status is not OperationStatus.Done) {
            throw new ArgumentException(
                "The JSON string contains invalid UTF-16.",
                parameterName
            );
        }
        return rune;
    }

    private static int GetEncodedUtf8ByteCount(Rune rune) {
        int scalar = rune.Value;
        if (scalar is '"' or '\\'
            or '\b' or '\t' or '\n' or '\f' or '\r') {
            return 2;
        }
        if (scalar <= 0x1F
            || scalar is >= 0x7F and <= 0x9F
            || scalar is 0x2028 or 0x2029) {
            return 6;
        }
        return rune.Utf8SequenceLength;
    }

    private static int WriteRune(Rune rune, Span<byte> destination) {
        int scalar = rune.Value;
        switch (scalar) {
            case '"':
                return WriteShortEscape('"', destination);
            case '\\':
                return WriteShortEscape('\\', destination);
            case '\b':
                return WriteShortEscape('b', destination);
            case '\t':
                return WriteShortEscape('t', destination);
            case '\n':
                return WriteShortEscape('n', destination);
            case '\f':
                return WriteShortEscape('f', destination);
            case '\r':
                return WriteShortEscape('r', destination);
        }

        if (scalar <= 0x1F
            || scalar is >= 0x7F and <= 0x9F
            || scalar is 0x2028 or 0x2029) {
            destination[0] = (byte)'\\';
            destination[1] = (byte)'u';
            destination[2] = (byte)LowerHex[(scalar >> 12) & 0xF];
            destination[3] = (byte)LowerHex[(scalar >> 8) & 0xF];
            destination[4] = (byte)LowerHex[(scalar >> 4) & 0xF];
            destination[5] = (byte)LowerHex[scalar & 0xF];
            return 6;
        }

        return rune.EncodeToUtf8(destination);
    }

    private static int WriteShortEscape(
        char escaped,
        Span<byte> destination
    ) {
        destination[0] = (byte)'\\';
        destination[1] = (byte)escaped;
        return 2;
    }
}
