using Atelia.SessionJournal.HistoryTimeline;

namespace Atelia.SessionJournal.RecapGrid.Control;

public static class RecapGridSampleAssets {
    public const string MysteryInvestigationV4 =
        "mystery-investigation-v4";

    public static IReadOnlyList<string> AssetIds { get; } =
        Array.AsReadOnly([MysteryInvestigationV4]);

    public static bool TryCreateRegistrationBundle(
        string assetId,
        out RecapGridControlRegistrationBundle? bundle
    ) {
        if (!string.Equals(
                assetId,
                MysteryInvestigationV4,
                StringComparison.Ordinal)) {
            bundle = null;
            return false;
        }
        FamilyDefinition family = CreateMysteryFamily();
        var capability = new MaintainerCapabilitySpec(
            RecapRewriterProtocolV3.RuntimeProtocolId,
            MaintainerReadableScope
                .FullPriorBuildTargetAndCurrentHistorySegmentV1
        );
        MaintainerDefinitionRevision culprit =
            MaintainerDefinitionRevision.Create(
                new LogicalColumnId("case.culprit-hypothesis"),
                family.Digest,
                new ContextHeaderBlockTarget(
                    ContextHeaderCarrier.System,
                    "culprit-hypothesis",
                    "Derived context from prior history: culprit hypothesis"
                ),
                capability,
                new MaintainerDeclarativeSpec(
                    "Who is the culprit?",
                    "Maintain the current culprit hypothesis and reconcile new clues."
                ),
                16 * 1024
            );
        MaintainerDefinitionRevision suspicion =
            MaintainerDefinitionRevision.Create(
                new LogicalColumnId("case.x-suspicion"),
                family.Digest,
                new ContextHeaderBlockTarget(
                    ContextHeaderCarrier.System,
                    "x-suspicion",
                    "Derived context from prior history: suspicion about X"
                ),
                capability,
                new MaintainerDeclarativeSpec(
                    "Is X's behavior suspicious?",
                    "Maintain exact evidence for and against X being suspicious."
                ),
                16 * 1024
            );
        bundle = new RecapGridControlRegistrationBundle(
            [family],
            [culprit, suspicion],
            []
        );
        return true;
    }

    private static FamilyDefinition CreateMysteryFamily() =>
        FamilyDefinition.Create(
            "Maintain one bounded investigation thought. Return only the "
                + "complete replacement text; if unchanged, reproduce the "
                + "prior block verbatim.",
            [],
            RecapRewriterProtocolV3.CreateOutputProtocol(),
            RecapRewriterProtocolV3.CreateInputRenderingProtocol()
        );
}
