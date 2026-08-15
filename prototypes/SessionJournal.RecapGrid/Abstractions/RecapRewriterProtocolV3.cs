namespace Atelia.SessionJournal.RecapGrid;

/// <summary>
/// Provider-neutral canonical contract for the single-shot V3 recap rewriter.
/// The model returns one complete replacement text and never receives tools.
/// </summary>
public static class RecapRewriterProtocolV3 {
    public const string RuntimeProtocolId = "text-runtime-v3";
    public const string OutputProtocolId = "atelia.recap.output.v3";
    public const string InputProtocolId = "atelia.recap.input.v1";
    public const string PriorProjectionSchemaId = "atelia.recap.prior.v1";
    public const string HistorySegmentRenderingSchemaId =
        "atelia.history.segment.v1";

    /// <summary>Creates the exact V3 full-replacement text contract.</summary>
    public static FamilyOutputProtocol CreateOutputProtocol() => new(
        OutputProtocolId,
        FamilyOutputMode.FullReplacementText
    );

    /// <summary>
    /// Creates the exact V3 input envelope. The input/prior/history schemas are
    /// unchanged from their independently versioned v1 contracts.
    /// </summary>
    public static FamilyInputRenderingProtocol CreateInputRenderingProtocol()
        => new(
            InputProtocolId,
            PriorProjectionSchemaId,
            HistorySegmentRenderingSchemaId
        );
}
