using System.Text;

namespace Atelia.SessionJournal;

/// <summary>
/// Renders validated, pre-Prepared derived contributions into exact request
/// snapshots. Prepared v5 persists the resulting snapshot and never
/// re-renders it through this contract.
/// </summary>
internal static class SessionContextContributionRenderer {
    private const int MinimumRecapFenceLength = 4;
    private const string RecapFenceInfoString = "recap-block";

    internal static SessionRequestArtifactContextSnapshot RenderOneHot(
        ContextHeaderBlockTarget target,
        string exactText
    ) {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(exactText);
        string rendered = RenderRecapBlock(
            target.SemanticHeading,
            exactText
        );
        return target.Carrier switch {
            ContextHeaderCarrier.System =>
                new SessionRequestArtifactContextSnapshot(rendered, "", ""),
            ContextHeaderCarrier.Observation =>
                new SessionRequestArtifactContextSnapshot("", rendered, ""),
            ContextHeaderCarrier.Action =>
                new SessionRequestArtifactContextSnapshot("", "", rendered),
            _ => throw new InvalidDataException(
                $"Unsupported context contribution carrier '{target.Carrier}'."
            )
        };
    }

    private static string RenderRecapBlock(
        string semanticHeading,
        string exactText
    ) {
        int fenceLength = Math.Max(
            MinimumRecapFenceLength,
            GetLongestTildeRun(exactText) + 1
        );
        string fence = new('~', fenceLength);
        var builder = new StringBuilder();
        builder.Append("## ")
            .Append(semanticHeading)
            .Append("\n\n")
            .Append(fence)
            .Append(RecapFenceInfoString)
            .Append('\n')
            .Append(exactText);
        if (!exactText.EndsWith('\n')) { builder.Append('\n'); }
        builder.Append(fence);
        return builder.ToString();
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
