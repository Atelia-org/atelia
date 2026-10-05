// 作者：姬澄(Galatea-02)；复用来源：38959d0293d4456f62d2c5a4a3af26088dfa3c0e。
namespace Atelia.Galatea.Server.Mailbox;

/// <summary>
/// A deliberately narrow, single ASCII mailbox address. This is not a full
/// RFC mailbox parser, a recipient authorization check, or a delivery check.
/// </summary>
internal sealed class GalateaExternalMailAddress {
    private const int MaximumLocalPartLength = 64;
    private const int MaximumDomainLength = 253;
    private const int MaximumAddressLength = 254;
    private const int MaximumDomainLabelLength = 63;

    private GalateaExternalMailAddress(string value) => Value = value;

    internal string Value { get; }

    /// <summary>
    /// Accepts unquoted ASCII atoms separated by single dots and a dotted
    /// DNS-label domain. Only leading/trailing U+0020 spaces are removed.
    /// Case is preserved; no HTML entity decoding is performed. Newlines are rejected.
    /// Display names, comments, lists, literals, quoted or internationalized
    /// addresses are outside this slice's supported syntax.
    /// </summary>
    internal static bool TryParse(
        string? input,
        out GalateaExternalMailAddress? address
    ) {
        address = null;
        if (input is null) { return false; }

        int start = 0;
        int end = input.Length;
        while (start < end && input[start] == ' ') { start++; }
        while (end > start && input[end - 1] == ' ') { end--; }
        int length = end - start;
        if (length < 5 || length > MaximumAddressLength) { return false; }

        ReadOnlySpan<char> candidate = input.AsSpan(start, length);
        int at = candidate.IndexOf('@');
        if (at < 1 || at != candidate.LastIndexOf('@')) { return false; }

        ReadOnlySpan<char> local = candidate[..at];
        ReadOnlySpan<char> domain = candidate[(at + 1)..];
        if (local.Length > MaximumLocalPartLength
            || domain.Length > MaximumDomainLength
            || !IsLocalPart(local)
            || !IsDottedDomain(domain)) {
            return false;
        }

        address = new GalateaExternalMailAddress(candidate.ToString());
        return true;
    }

    private static bool IsLocalPart(ReadOnlySpan<char> local) {
        bool previousWasDot = true;
        foreach (char c in local) {
            if (c == '.') {
                if (previousWasDot) { return false; }
                previousWasDot = true;
            }
            else {
                if (!IsAsciiAlphaNumeric(c)
                    && "!#$%&'*+-/=?^_`{|}~".IndexOf(c) < 0) {
                    return false;
                }
                previousWasDot = false;
            }
        }
        return !previousWasDot;
    }

    private static bool IsDottedDomain(ReadOnlySpan<char> domain) {
        int labelLength = 0;
        bool sawDot = false;
        char previous = '\0';
        foreach (char c in domain) {
            if (c == '.') {
                if (labelLength == 0 || previous == '-') { return false; }
                labelLength = 0;
                sawDot = true;
            }
            else {
                if ((!IsAsciiAlphaNumeric(c) && c != '-')
                    || (labelLength == 0 && c == '-')
                    || ++labelLength > MaximumDomainLabelLength) {
                    return false;
                }
            }
            previous = c;
        }
        return sawDot && labelLength > 0 && previous != '-';
    }

    private static bool IsAsciiAlphaNumeric(char c) =>
        c is >= 'a' and <= 'z'
            or >= 'A' and <= 'Z'
            or >= '0' and <= '9';
}
