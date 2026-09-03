namespace Atelia.SessionJournal;

/// <summary>
/// Renders validated, pre-Prepared derived contributions into exact request
/// snapshots. Current Prepared v7 persists the resulting snapshot and never
/// re-renders it through this contract.
/// </summary>
internal static class SessionContextContributionRenderer {
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
    ) => "## " + semanticHeading + "\n\n"
        + AdaptiveMarkdownFenceRenderer.RenderBlock(
            RecapFenceInfoString,
            exactText
        );
}
