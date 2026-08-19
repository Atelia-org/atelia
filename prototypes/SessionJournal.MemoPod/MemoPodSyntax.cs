using System.Text;

namespace Atelia.SessionJournal.MemoPod;

internal static class MemoPodSyntax {
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true
    );

    internal static bool IsLowerHex(ReadOnlySpan<char> value) {
        foreach (char character in value) {
            if (character is >= '0' and <= '9'
                or >= 'a' and <= 'f') {
                continue;
            }
            return false;
        }
        return true;
    }

    internal static string RequireTopic(
        string value,
        string parameterName
    ) {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || value.Any(char.IsControl)) {
            throw new ArgumentException(
                "The topic must be non-empty, already trimmed, and contain no control characters.",
                parameterName
            );
        }
        RequireUtf8Length(
            value,
            MemoPodLimits.MaximumTopicUtf8Bytes,
            parameterName
        );
        return value;
    }

    internal static int RequireMemoExactText(
        string value,
        string parameterName
    ) {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (string.IsNullOrWhiteSpace(value)) {
            throw new ArgumentException(
                "Memo exact text must not be blank.",
                parameterName
            );
        }
        return RequireUtf8Length(
            value,
            MemoPodLimits.MaximumMemoExactTextUtf8Bytes,
            parameterName
        );
    }

    internal static MemoPodId RequirePodId(
        MemoPodId value,
        string parameterName
    ) {
        if (value.IsDefault) {
            throw new ArgumentException(
                "A default MemoPodId is not valid.",
                parameterName
            );
        }
        return value;
    }

    internal static MemoId RequireMemoId(
        MemoId value,
        string parameterName
    ) {
        if (value.IsDefault) {
            throw new ArgumentException(
                "A default MemoId is not valid.",
                parameterName
            );
        }
        return value;
    }

    private static int RequireUtf8Length(
        string value,
        int maximumUtf8Bytes,
        string parameterName
    ) {
        try {
            int byteCount = StrictUtf8.GetByteCount(value);
            if (byteCount > maximumUtf8Bytes) {
                throw new ArgumentOutOfRangeException(
                    parameterName,
                    $"The UTF-8 value exceeds {maximumUtf8Bytes} bytes."
                );
            }
            return byteCount;
        }
        catch (EncoderFallbackException exception) {
            throw new ArgumentException(
                "The value contains invalid UTF-16.",
                parameterName,
                exception
            );
        }
    }
}
