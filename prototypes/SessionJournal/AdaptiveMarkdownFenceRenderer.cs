using System.Text;

namespace Atelia.SessionJournal;

/// <summary>
/// Renders exact text inside a Markdown tilde fence that cannot be closed by
/// any tilde run already present in the text.
/// </summary>
public static class AdaptiveMarkdownFenceRenderer {
    public const int MinimumFenceLength = 4;
    public const int MaximumInfoStringLength = 64;

    /// <summary>
    /// Renders one fenced block without trimming, normalizing, or escaping
    /// <paramref name="exactBody"/>. The info string is restricted to a
    /// code-owned ASCII token.
    /// </summary>
    public static string RenderBlock(
        string infoString,
        string exactBody
    ) {
        ValidateInfoString(infoString);
        ArgumentNullException.ThrowIfNull(exactBody);

        int fenceLength = Math.Max(
            MinimumFenceLength,
            checked(GetLongestTildeRun(exactBody) + 1)
        );
        string fence = new('~', fenceLength);
        var builder = new StringBuilder();
        _ = builder.Append(fence)
            .Append(infoString)
            .Append('\n')
            .Append(exactBody);
        if (!exactBody.EndsWith('\n')) { _ = builder.Append('\n'); }
        return builder.Append(fence).ToString();
    }

    private static void ValidateInfoString(string infoString) {
        ArgumentNullException.ThrowIfNull(infoString);
        if (infoString.Length is 0 or > MaximumInfoStringLength
            || infoString.Any(static character => character is not (
                >= 'A' and <= 'Z'
                or >= 'a' and <= 'z'
                or >= '0' and <= '9'
                or '-'
                or '_'
            ))) {
            throw new ArgumentException(
                "Markdown fence info string must be a 1..64 character ASCII token.",
                nameof(infoString)
            );
        }
    }

    private static int GetLongestTildeRun(string text) {
        int longestRun = 0;
        int currentRun = 0;
        foreach (char character in text) {
            if (character == '~') {
                currentRun++;
                longestRun = Math.Max(longestRun, currentRun);
            }
            else {
                currentRun = 0;
            }
        }
        return longestRun;
    }
}
