// 作者：姬澄(Galatea-02)；复用来源：38959d0293d4456f62d2c5a4a3af26088dfa3c0e。
using Atelia.Galatea.Input;

namespace Atelia.Galatea.Server.Mailbox;

/// <summary>
/// A deliberately narrow, single ASCII mailbox address. This is not a full
/// RFC mailbox parser, a recipient authorization check, or a delivery check.
/// </summary>
internal sealed class GalateaExternalMailAddress {
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
        // Bound allocation before materializing a trimmed candidate. The shared
        // validator remains the authority for the complete supported grammar.
        if (length is < 5 or > 254) { return false; }
        string candidate = input.Substring(start, length);
        if (!GalateaObservationRules.IsCanonicalExternalMailAddress(candidate)) { return false; }
        address = new GalateaExternalMailAddress(candidate);
        return true;
    }

    /// <summary>Compare canonical ASCII mailboxes without rewriting local-part or aliases.</summary>
    internal static bool SameMailbox(string? left, string? right) {
        if (!GalateaObservationRules.IsCanonicalExternalMailAddress(left)
            || !GalateaObservationRules.IsCanonicalExternalMailAddress(right)) { return false; }
        int leftAt = left!.IndexOf('@');
        int rightAt = right!.IndexOf('@');
        return left.AsSpan(0, leftAt).Equals(right.AsSpan(0, rightAt), StringComparison.Ordinal)
            && left.AsSpan(leftAt + 1).Equals(right.AsSpan(rightAt + 1), StringComparison.OrdinalIgnoreCase);
    }
}
