namespace Atelia.SessionJournal.RecapGrid;

/// <summary>
/// Provider-neutral canonical contract for the single-shot V1 recap rewriter.
/// Runtime/provider implementations may execute this protocol, but do not own
/// its durable identifiers or terminal schema.
/// </summary>
public static class RecapRewriterProtocolV1 {
    public const string RuntimeProtocolId = "tool-runtime-v1";
    public const string OutputProtocolId = "atelia.recap.output.v1";
    public const string InputProtocolId = "atelia.recap.input.v1";
    public const string PriorProjectionSchemaId = "atelia.recap.prior.v1";
    public const string HistorySegmentRenderingSchemaId =
        "atelia.history.segment.v1";
    public const string TerminalToolName = "submit";
    public const string UpdatedOutcome = "updated";
    public const string KeepUnchangedOutcome = "keep-unchanged";

    /// <summary>
    /// Creates the exact V1 terminal tool. The description is Family-owned
    /// canonical prompt material; the name and input schema are protocol-owned.
    /// </summary>
    public static FamilyToolDefinition CreateTerminalTool(string description)
        => new(
            TerminalToolName,
            description,
            new FamilyObjectInputSchema([
                new FamilyToolProperty(
                    "outcome",
                    new FamilyScalarInputSchema(
                        FamilyScalarType.String,
                        orderedEnum: [
                            UpdatedOutcome,
                            KeepUnchangedOutcome
                        ]
                    ),
                    required: true
                ),
                new FamilyToolProperty(
                    "content",
                    new FamilyScalarInputSchema(
                        FamilyScalarType.String,
                        nullable: true
                    ),
                    required: true
                )
            ])
        );

    /// <summary>Creates the exact V1 output envelope.</summary>
    public static FamilyOutputProtocol CreateOutputProtocol() => new(
        OutputProtocolId,
        TerminalToolName,
        FamilyToolChoice.Required,
        allowParallel: false
    );

    /// <summary>Creates the exact V1 prior/history rendering envelope.</summary>
    public static FamilyInputRenderingProtocol CreateInputRenderingProtocol()
        => new(
            InputProtocolId,
            PriorProjectionSchemaId,
            HistorySegmentRenderingSchemaId
        );
}
