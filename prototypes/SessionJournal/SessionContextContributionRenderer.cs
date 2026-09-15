namespace Atelia.SessionJournal;

/// <summary>
/// Transiently renders validated derived contributions at the request boundary.
/// Historical v7/v8 snapshots remain already-rendered facts and bypass this renderer
/// during exact recovery; v9 persists only the original semantic contributions.
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
