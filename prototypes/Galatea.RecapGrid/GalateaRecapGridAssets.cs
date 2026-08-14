using Atelia.SessionJournal;
using Atelia.SessionJournal.RecapGrid;
using Atelia.SessionJournal.RecapGrid.Control;

namespace Atelia.Galatea.RecapGrid;

/// <summary>Code-owned, provider-free Galatea RecapGrid assets.</summary>
public static class GalateaRecapGridAssets {
    public const string RollingRewriteZhCnV2 =
        "galatea-rolling-rewrite-zh-cn-v2";

    public static IReadOnlyList<string> AssetIds { get; } =
        Array.AsReadOnly([RollingRewriteZhCnV2]);

    public static bool TryCreateRegistrationBundle(
        string assetId,
        out RecapGridControlRegistrationBundle? bundle
    ) {
        if (!string.Equals(
                assetId,
                RollingRewriteZhCnV2,
                StringComparison.Ordinal)) {
            bundle = null;
            return false;
        }

        string systemPrompt = PromptResourceLoader.ReadText(
            PromptResourceLoader.FamilySystemResourceName,
            RecapGridLimits.MaximumSystemPromptUtf8Bytes
        );
        string worldPrompt = PromptResourceLoader.ReadText(
            PromptResourceLoader.WorldUnderstandingResourceName,
            RecapGridLimits.MaximumUserPromptUtf8Bytes
        );
        string autobiographyPrompt = PromptResourceLoader.ReadText(
            PromptResourceLoader.AutobiographyResourceName,
            RecapGridLimits.MaximumUserPromptUtf8Bytes
        );

        FamilyDefinition family = FamilyDefinition.Create(
            systemPrompt,
            [RecapRewriterProtocolV2.CreateTerminalTool(
                "提交这个 recap 成员的完整维护结果。"
            )],
            RecapRewriterProtocolV2.CreateOutputProtocol(),
            RecapRewriterProtocolV2.CreateInputRenderingProtocol()
        );
        var capability = new MaintainerCapabilitySpec(
            RecapRewriterProtocolV2.RuntimeProtocolId,
            MaintainerReadableScope
                .FullPriorBuildTargetAndCurrentHistorySegmentV1,
            semanticModelId: null
        );
        MaintainerDefinitionRevision world =
            MaintainerDefinitionRevision.Create(
                new LogicalColumnId("world-understanding"),
                family.Digest,
                new ContextHeaderBlockPath(
                    ContextHeaderCarrier.Observation,
                    "roleplay.world-understanding"
                ),
                capability,
                new MaintainerDeclarativeSpec(
                    "维护 Galatea 当前的世界理解",
                    worldPrompt
                ),
                maxContentUtf8Bytes: 32 * 1024
            );
        MaintainerDefinitionRevision autobiography =
            MaintainerDefinitionRevision.Create(
                new LogicalColumnId("autobiography"),
                family.Digest,
                new ContextHeaderBlockPath(
                    ContextHeaderCarrier.Action,
                    "roleplay.first-person-autobiography"
                ),
                capability,
                new MaintainerDeclarativeSpec(
                    "维护 Galatea 的第一人称自传",
                    autobiographyPrompt
                ),
                maxContentUtf8Bytes: 32 * 1024
            );

        bundle = new RecapGridControlRegistrationBundle(
            [family],
            [world, autobiography],
            []
        );
        return true;
    }
}
