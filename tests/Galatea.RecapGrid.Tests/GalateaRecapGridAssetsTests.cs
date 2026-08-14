using System.Security.Cryptography;
using Atelia.SessionJournal;
using Atelia.SessionJournal.RecapGrid;
using Atelia.SessionJournal.RecapGrid.Control;
using Xunit;

namespace Atelia.Galatea.RecapGrid.Tests;

public sealed class GalateaRecapGridAssetsTests {
    [Fact]
    public void RollingRewriteV1_IsExactProviderNeutralCanonicalBundle() {
        Assert.Equal(
            [GalateaRecapGridAssets.RollingRewriteZhCnV1],
            GalateaRecapGridAssets.AssetIds
        );
        Assert.False(GalateaRecapGridAssets.TryCreateRegistrationBundle(
            "unknown",
            out RecapGridControlRegistrationBundle? unknown
        ));
        Assert.Null(unknown);
        Assert.True(GalateaRecapGridAssets.TryCreateRegistrationBundle(
            GalateaRecapGridAssets.RollingRewriteZhCnV1,
            out RecapGridControlRegistrationBundle? bundle
        ));
        Assert.NotNull(bundle);

        FamilyDefinition family = Assert.Single(bundle.Families);
        Assert.Empty(bundle.Recipes);
        Assert.Equal(2, bundle.Definitions.Count);
        FamilyToolDefinition tool = Assert.Single(family.OrderedTools);
        Assert.Equal(RecapRewriterProtocolV1.TerminalToolName, tool.Name);
        Assert.Equal(RecapRewriterProtocolV1.OutputProtocolId,
            family.OutputProtocol.ProtocolId);
        Assert.Equal(RecapRewriterProtocolV1.TerminalToolName,
            family.OutputProtocol.TerminalToolName);
        Assert.Equal(FamilyToolChoice.Required,
            family.OutputProtocol.ToolChoice);
        Assert.False(family.OutputProtocol.AllowParallel);
        Assert.Equal(RecapRewriterProtocolV1.InputProtocolId,
            family.InputRenderingProtocol.ProtocolId);
        Assert.Equal(RecapRewriterProtocolV1.PriorProjectionSchemaId,
            family.InputRenderingProtocol.PriorProjectionSchemaId);
        Assert.Equal(RecapRewriterProtocolV1.HistorySegmentRenderingSchemaId,
            family.InputRenderingProtocol.HistorySegmentRenderingSchemaId);

        Assert.False(tool.InputSchema.Nullable);
        Assert.Equal(2, tool.InputSchema.Properties.Count);
        FamilyToolProperty outcome = tool.InputSchema.Properties[0];
        Assert.Equal("outcome", outcome.Name);
        Assert.True(outcome.Required);
        var outcomeSchema = Assert.IsType<FamilyScalarInputSchema>(
            outcome.Schema
        );
        Assert.False(outcomeSchema.Nullable);
        Assert.Equal(FamilyScalarType.String, outcomeSchema.ScalarType);
        Assert.Equal(
            [
                RecapRewriterProtocolV1.UpdatedOutcome,
                RecapRewriterProtocolV1.KeepUnchangedOutcome
            ],
            outcomeSchema.OrderedEnum
        );
        FamilyToolProperty content = tool.InputSchema.Properties[1];
        Assert.Equal("content", content.Name);
        Assert.True(content.Required);
        var contentSchema = Assert.IsType<FamilyScalarInputSchema>(
            content.Schema
        );
        Assert.True(contentSchema.Nullable);
        Assert.Equal(FamilyScalarType.String, contentSchema.ScalarType);
        Assert.Empty(contentSchema.OrderedEnum);

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
            Assert.Equal(RecapRewriterProtocolV1.RuntimeProtocolId,
                definition.Capability.RuntimeProtocolId);
            Assert.Null(definition.Capability.SemanticModelId);
            Assert.Equal(32 * 1024, definition.MaxContentUtf8Bytes);
        });

        AssertGoldenDigests(bundle);
    }

    [Fact]
    public void Materialization_IsDeterministicAndResourcesAreExact() {
        Assert.True(GalateaRecapGridAssets.TryCreateRegistrationBundle(
            GalateaRecapGridAssets.RollingRewriteZhCnV1,
            out RecapGridControlRegistrationBundle? first
        ));
        Assert.True(GalateaRecapGridAssets.TryCreateRegistrationBundle(
            GalateaRecapGridAssets.RollingRewriteZhCnV1,
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
            "7afa7bcdb664de946884459f43fc3b64b0c9c87a4dfda417ccef621131867583",
            ResourceSha256(PromptResourceLoader.FamilySystemResourceName)
        );
        Assert.Equal(
            "6c1d2d41eacc35e097559be4540cbf280913f3eb901193766edc4c192d9cdddc",
            ResourceSha256(PromptResourceLoader.WorldUnderstandingResourceName)
        );
        Assert.Equal(
            "a031dd45490539abb9f2d6f360f6657f22c1f21dc2bd0404cca299d416fa7886",
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
        Assert.All(first.Definitions, definition => Assert.DoesNotContain(
            RecapRewriterProtocolV1.TerminalToolName,
            definition.DeclarativeSpec.UserPromptTemplate,
            StringComparison.Ordinal
        ));
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
            "0c922f473c63f0fcdbbf8d972ffcc6f405fa0112a924b03011be121651a5f953",
            "d1662eed763e90f01beeec636d08aa5f937b8c4c65d189bd7e81aff3623e82bf",
            "20c301e940c3469e2297368c30892dcdcc5fa5aea792c673ad1b1f9af302732d",
            "3aa41d18db9da5d971961eb33d4c3ac585a635b14bd62f3fffa720c147741b72"
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
