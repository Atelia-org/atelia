using System.Text;
using Atelia.Galatea.Prompts;

namespace Atelia.Galatea.Input;

/// <summary>Fact-level rules shared by the JSON reader and Host admission/value construction.</summary>
internal static class GalateaObservationRules {
    internal static string? ValidatePlayerText(string? message) {
        if (string.IsNullOrWhiteSpace(message)) { return "message must not be blank."; }
        try {
            if (GalateaInputValidation.StrictUtf8.GetByteCount(message) > GalateaObservationLimits.MaximumPlayerTextUtf8Bytes) {
                return "message exceeds the 64 KiB UTF-8 limit.";
            }
        }
        catch (EncoderFallbackException) { return "message must contain valid Unicode."; }
        return null;
    }

    internal static bool IsCanonicalMessageId(string? value) => value is { Length: 32 }
        && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static bool ContainsHeaderLineBreak(string value) {
        ArgumentNullException.ThrowIfNull(value);
        return value.EnumerateRunes().Any(static rune => rune.Value is '\r' or '\n' or '\v' or '\f' or 0x0085 or 0x2028 or 0x2029);
    }

    internal static void ValidateMailbox(string messageId, string from, string to, string? subject, string body) {
        if (!IsCanonicalMessageId(messageId)) { throw new ArgumentException("Mailbox messageId must be canonical 32-lowerhex text.", nameof(messageId)); }
        RequireMailboxText(from, GalateaObservationLimits.MaximumMailSenderUtf8Bytes, nameof(from), allowLineBreaks: false);
        _ = new GalateaCharacterName(to);
        if (subject is not null) { RequireMailboxText(subject, GalateaObservationLimits.MaximumMailSubjectUtf8Bytes, nameof(subject), allowLineBreaks: false); }
        RequireMailboxText(body, GalateaObservationLimits.MaximumMailBodyUtf8Bytes, nameof(body), allowLineBreaks: true);
    }

    private static void RequireMailboxText(string value, int maximumBytes, string name, bool allowLineBreaks) {
        if (string.IsNullOrWhiteSpace(value)) { throw new ArgumentException($"{name} must not be blank.", name); }
        try {
            if (GalateaInputValidation.StrictUtf8.GetByteCount(value) > maximumBytes) {
                throw new ArgumentOutOfRangeException(name, $"{name} exceeds its UTF-8 byte limit.");
            }
            if (!allowLineBreaks && ContainsHeaderLineBreak(value)) { throw new ArgumentException($"{name} must be single-line text.", name); }
            _ = System.Xml.XmlConvert.VerifyXmlChars(value);
        }
        catch (Exception exception) when (exception is EncoderFallbackException or System.Xml.XmlException) {
            throw new ArgumentException($"{name} must be strict XML-safe Unicode text.", name, exception);
        }
    }
}
