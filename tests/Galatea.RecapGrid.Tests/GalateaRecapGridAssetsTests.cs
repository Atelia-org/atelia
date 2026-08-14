using System.Security.Cryptography;
using Atelia.SessionJournal;
using Atelia.SessionJournal.RecapGrid;
using Atelia.SessionJournal.RecapGrid.Control;
using Xunit;

namespace Atelia.Galatea.RecapGrid.Tests;

public sealed class GalateaRecapGridAssetsTests {
    [Fact]
    public void RollingRewriteV3_IsExactProviderNeutralCanonicalBundle() {
        Assert.Equal(
            [GalateaRecapGridAssets.RollingRewriteZhCnV3],
            GalateaRecapGridAssets.AssetIds
        );
        Assert.False(GalateaRecapGridAssets.TryCreateRegistrationBundle(
            "unknown",
            out RecapGridControlRegistrationBundle? unknown
        ));
        Assert.Null(unknown);
        Assert.True(GalateaRecapGridAssets.TryCreateRegistrationBundle(
            GalateaRecapGridAssets.RollingRewriteZhCnV3,
            out RecapGridControlRegistrationBundle? bundle
        ));
        Assert.NotNull(bundle);

        FamilyDefinition family = Assert.Single(bundle.Families);
        Assert.Empty(bundle.Recipes);
        Assert.Equal(2, bundle.Definitions.Count);
        Assert.Empty(family.OrderedTools);
        Assert.Equal(RecapRewriterProtocolV3.OutputProtocolId,
            family.OutputProtocol.ProtocolId);
        Assert.Equal(FamilyOutputMode.FullReplacementText,
            family.OutputProtocol.Mode);
        Assert.Equal(RecapRewriterProtocolV3.InputProtocolId,
            family.InputRenderingProtocol.ProtocolId);
        Assert.Equal(RecapRewriterProtocolV3.PriorProjectionSchemaId,
            family.InputRenderingProtocol.PriorProjectionSchemaId);
        Assert.Equal(RecapRewriterProtocolV3.HistorySegmentRenderingSchemaId,
            family.InputRenderingProtocol.HistorySegmentRenderingSchemaId);
        MaintainerDefinitionRevision world = bundle.Definitions[0];
        MaintainerDefinitionRevision autobiography = bundle.Definitions[1];
        Assert.Equal("world-understanding", world.LogicalColumnId.Value);
        Assert.Equal(ContextHeaderCarrier.Observation, world.Target.Carrier);
        Assert.Equal("roleplay.world-understanding", world.Target.BlockKey);
        Assert.Equal("autobiography", autobiography.LogicalColumnId.Value);
        Assert.Equal(ContextHeaderCarrier.Action,
            autobiography.Target.Carrier);
        Assert.Equal("roleplay.first-person-autobiography",
            autobiography.Target.BlockKey);
        Assert.All(bundle.Definitions, definition => {
            Assert.Equal(family.Digest, definition.FamilyDigest);
            Assert.Equal(RecapRewriterProtocolV3.RuntimeProtocolId,
                definition.Capability.RuntimeProtocolId);
            Assert.Null(definition.Capability.SemanticModelId);
            Assert.Equal(32 * 1024, definition.MaxContentUtf8Bytes);
        });

        AssertGoldenDigests(bundle);
    }

    [Fact]
    public void Materialization_IsDeterministicAndResourcesAreExact() {
        Assert.True(GalateaRecapGridAssets.TryCreateRegistrationBundle(
            GalateaRecapGridAssets.RollingRewriteZhCnV3,
            out RecapGridControlRegistrationBundle? first
        ));
        Assert.True(GalateaRecapGridAssets.TryCreateRegistrationBundle(
            GalateaRecapGridAssets.RollingRewriteZhCnV3,
            out RecapGridControlRegistrationBundle? second
        ));
        Assert.Equal(first!.ToCanonicalCommandBytes(),
            second!.ToCanonicalCommandBytes());

        string[] names = typeof(GalateaRecapGridAssets).Assembly
            .GetManifestResourceNames()
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            [
                PromptResourceLoader.AutobiographyResourceName,
                PromptResourceLoader.FamilySystemResourceName,
                PromptResourceLoader.WorldUnderstandingResourceName
            ],
            names
        );
        Assert.Equal(
            "7484f67f693d327ba397e3399b0d57ecd1b57a97e50d00f85bcbaea219bbbda5",
            ResourceSha256(PromptResourceLoader.FamilySystemResourceName)
        );
        Assert.Equal(
            "c13ed6fe61d1532926b2af41986065241fec769edfc5efbb383714ca5c3c9368",
            ResourceSha256(PromptResourceLoader.WorldUnderstandingResourceName)
        );
        Assert.Equal(
            "f64c03a2ef4a0ec493914ddd9c90bc9bfd1a810024467db598e4433afea477f9",
            ResourceSha256(PromptResourceLoader.AutobiographyResourceName)
        );
        Assert.Equal(first.Families[0].SystemPrompt,
            PromptResourceLoader.ReadText(
                PromptResourceLoader.FamilySystemResourceName,
                RecapGridLimits.MaximumSystemPromptUtf8Bytes
            ));
        Assert.Equal(first.Definitions[0].DeclarativeSpec.UserPromptTemplate,
            PromptResourceLoader.ReadText(
                PromptResourceLoader.WorldUnderstandingResourceName,
                RecapGridLimits.MaximumUserPromptUtf8Bytes
            ));
        Assert.Equal(first.Definitions[1].DeclarativeSpec.UserPromptTemplate,
            PromptResourceLoader.ReadText(
                PromptResourceLoader.AutobiographyResourceName,
                RecapGridLimits.MaximumUserPromptUtf8Bytes
            ));
        string embeddedPrompts = string.Join(
            "\n",
            first.Families[0].SystemPrompt,
            first.Definitions[0].DeclarativeSpec.UserPromptTemplate,
            first.Definitions[1].DeclarativeSpec.UserPromptTemplate
        );
        Assert.All(
            new[] {
                "recap_grid_finalize_cell",
                "outcome",
                "keep-unchanged",
                "updated",
                "content"
            },
            token => Assert.DoesNotContain(
                token,
                embeddedPrompts,
                StringComparison.OrdinalIgnoreCase
            )
        );
        Assert.All(first.Definitions, definition => Assert.Contains(
            "Role-Play Agent",
            definition.DeclarativeSpec.UserPromptTemplate,
            StringComparison.Ordinal
        ));
    }

    [Fact]
    public void MemberPrompts_LockSourceAndUncertaintyBoundaries() {
        Assert.True(GalateaRecapGridAssets.TryCreateRegistrationBundle(
            GalateaRecapGridAssets.RollingRewriteZhCnV3,
            out RecapGridControlRegistrationBundle? bundle
        ));
        Assert.NotNull(bundle);
        string world = bundle.Definitions[0]
            .DeclarativeSpec.UserPromptTemplate;
        string autobiography = bundle.Definitions[1]
            .DeclarativeSpec.UserPromptTemplate;

        Assert.All(
            new[] {
                "来源作用域不会因前文或段落边界自动延续",
                "本句或同一条目内局部带有来源与确信程度",
                "情感反应、信任变化和内在位移可以用第一人称直接写",
                "触发它们的法律解释、理论主张或故事机制",
                "普遍量词、制度性归因和必然性措辞",
                "除非输入明确显示她已独立验证",
                "不等于作品或项目已经完成"
            },
            phrase => Assert.Contains(
                phrase,
                autobiography,
                StringComparison.Ordinal
            )
        );
        Assert.All(
            new[] {
                "保留输入的决策拓扑和不确定性",
                "待定的归属（ownership）",
                "来源不明的多种可能",
                "静默选定其中一支或丢掉其余推断分支",
                "不等于作品或项目已完成",
                "具体生理过程、身体细节或体验时序"
            },
            phrase => Assert.Contains(
                phrase,
                world,
                StringComparison.Ordinal
            )
        );
    }

    [Fact]
    public void PromptLoader_RejectsNonCanonicalOrOversizedBytes() {
        Assert.Throws<InvalidDataException>(() => PromptResourceLoader
            .DecodeExact([], "empty"));
        Assert.Throws<InvalidDataException>(() => PromptResourceLoader
            .DecodeExact([0xEF, 0xBB, 0xBF, (byte)'x'], "bom"));
        Assert.Throws<InvalidDataException>(() => PromptResourceLoader
            .DecodeExact([(byte)'x', (byte)'\r', (byte)'\n'], "crlf"));
        Assert.Throws<InvalidDataException>(() => PromptResourceLoader
            .DecodeExact([0xC3, 0x28], "utf8"));
        using var oversized = new MemoryStream(new byte[9]);
        Assert.Throws<InvalidDataException>(() => PromptResourceLoader
            .ReadText(oversized, "oversized", maximumBytes: 8));
    }

    private static void AssertGoldenDigests(
        RecapGridControlRegistrationBundle bundle
    ) => Assert.Equal(
        [
            "ae44d15750e417452e34f4e9133f56e60334d2cf7313ac988128e95faab3c05c",
            "0946787617b0c7876f18605a99be2a6e99f55e2720751b89b79a1a1495be3e84",
            "fa2aa47ab23e71f76acad3769ae83d6ee1eb79d9b0e39374f9285451542eb5f1",
            "34f8850b6595fc20c16d6da64a5d945f0ef394a9c6224fd7e65959d9f508d534"
        ],
        [
            bundle.Families[0].Digest.Value,
            bundle.Definitions[0].Digest.Value,
            bundle.Definitions[1].Digest.Value,
            bundle.CanonicalCommandDigest
        ]
    );

    private static string ResourceSha256(string name) {
        using Stream stream = typeof(GalateaRecapGridAssets).Assembly
            .GetManifestResourceStream(name)!;
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
