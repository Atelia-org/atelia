using System.Text;

namespace Atelia.SessionJournal.RecapGrid.Runtime;

internal static class RuntimeDiagnostics {
    internal const int MaximumExternalCodeUtf8Bytes = 128;
    internal const int MaximumDetailUtf8Bytes = 4 * 1024;
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true
    );

    internal static string BoundDetail(string? detail) {
        if (string.IsNullOrWhiteSpace(detail)) {
            return "Completion execution failed.";
        }
        try {
            if (StrictUtf8.GetByteCount(detail) <= MaximumDetailUtf8Bytes) {
                return detail;
            }
        }
        catch (EncoderFallbackException) {
            return "Provider returned an invalid diagnostic string.";
        }

        var builder = new StringBuilder(MaximumDetailUtf8Bytes);
        int bytes = 0;
        foreach (Rune value in detail.EnumerateRunes()) {
            if (bytes + value.Utf8SequenceLength
                > MaximumDetailUtf8Bytes) {
                break;
            }
            builder.Append(value.ToString());
            bytes += value.Utf8SequenceLength;
        }
        return builder.ToString();
    }

    internal static bool TryValidateExternalCode(
        string? code,
        out string validated
    ) {
        validated = string.Empty;
        if (string.IsNullOrWhiteSpace(code)) { return false; }
        try {
            if (StrictUtf8.GetByteCount(code)
                > MaximumExternalCodeUtf8Bytes) {
                return false;
            }
        }
        catch (EncoderFallbackException) {
            return false;
        }
        foreach (char value in code) {
            if (!(value is >= 'A' and <= 'Z'
                or >= 'a' and <= 'z'
                or >= '0' and <= '9'
                or '.' or '_' or '-')) {
                return false;
            }
        }
        validated = code;
        return true;
    }
}
