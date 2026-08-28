using Atelia.Galatea.Prompts;
using Atelia.SessionJournal;
using Atelia.SessionJournal.RecapGrid;
using Atelia.SessionJournal.RecapGrid.Control;

namespace Atelia.Galatea.RecapGrid;

/// <summary>Code-owned, provider-free Galatea RecapGrid assets.</summary>
public static class GalateaRecapGridAssets {
    public const string RollingRewriteZhCnV6 =
        "galatea-rolling-rewrite-zh-cn-v6";

    public static IReadOnlyList<string> AssetIds { get; } =
        Array.AsReadOnly([RollingRewriteZhCnV6]);

    public static bool TryCreateRegistrationBundle(
        string assetId,
        GalateaRecapGridAssetParameters parameters,
        out RecapGridControlRegistrationBundle? bundle
    ) {
        if (!string.Equals(
                assetId,
                RollingRewriteZhCnV6,
                StringComparison.Ordinal)) {
            bundle = null;
            return false;
        }
        ArgumentNullException.ThrowIfNull(parameters);

        string systemPrompt = PromptResourceLoader.ReadText(
            PromptResourceLoader.FamilySystemResourceName,
            RecapGridLimits.MaximumSystemPromptUtf8Bytes
        );
        string worldPrompt = GalateaPromptTemplate.Render(
            PromptResourceLoader.ReadText(
                PromptResourceLoader.WorldUnderstandingResourceName,
                RecapGridLimits.MaximumUserPromptUtf8Bytes
            ),
            parameters.CharacterName,
            RecapGridLimits.MaximumUserPromptUtf8Bytes
        );
        string autobiographyPrompt = GalateaPromptTemplate.Render(
            PromptResourceLoader.ReadText(
                PromptResourceLoader.AutobiographyResourceName,
                RecapGridLimits.MaximumUserPromptUtf8Bytes
            ),
            parameters.CharacterName,
            RecapGridLimits.MaximumUserPromptUtf8Bytes
        );
        string characterName = parameters.CharacterName.Value;

        FamilyDefinition family = FamilyDefinition.Create(
            systemPrompt,
            [],
            RecapRewriterProtocolV3.CreateOutputProtocol(),
            RecapRewriterProtocolV3.CreateInputRenderingProtocol()
        );
        var capability = new MaintainerCapabilitySpec(
            RecapRewriterProtocolV3.RuntimeProtocolId,
            MaintainerReadableScope
                .FullPriorBuildTargetAndCurrentHistorySegmentV1,
            semanticModelId: null
        );
        MaintainerDefinitionRevision world =
            MaintainerDefinitionRevision.Create(
                new LogicalColumnId("world-understanding"),
                family.Digest,
                new ContextHeaderBlockTarget(
                    ContextHeaderCarrier.Observation,
                    "galatea.world-understanding",
                    $"galatea.world-understanding {characterName}积累的世界理解："
                ),
                capability,
                new MaintainerDeclarativeSpec(
                    $"维护 {characterName} 当前的世界理解",
                    worldPrompt
                ),
                maxContentUtf8Bytes: 32 * 1024
            );
        MaintainerDefinitionRevision autobiography =
            MaintainerDefinitionRevision.Create(
                new LogicalColumnId("autobiography"),
                family.Digest,
                new ContextHeaderBlockTarget(
                    ContextHeaderCarrier.Action,
                    "galatea.first-person-autobiography",
                    $"galatea.first-person-autobiography {characterName}积累的第一人称自传："
                ),
                capability,
                new MaintainerDeclarativeSpec(
                    $"维护 {characterName} 的第一人称自传",
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

public sealed record GalateaRecapGridAssetParameters {
    public GalateaRecapGridAssetParameters(
        GalateaCharacterName characterName
    ) {
        CharacterName = characterName
            ?? throw new ArgumentNullException(nameof(characterName));
    }

    public GalateaCharacterName CharacterName { get; }
}
