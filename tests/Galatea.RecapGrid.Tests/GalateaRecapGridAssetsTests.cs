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
            "641270e3044efe3678538740ab7c7995702232de67c8e2e61d812dae8bab6a93",
            ResourceSha256(PromptResourceLoader.WorldUnderstandingResourceName)
        );
        Assert.Equal(
            "9f685b51f6fb607b7e21956a3402e02593f532e1f3d606338227a5f9a6a1ee7d",
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
            "829e780acd637c8d7f98a10c47e2dfda7c6fe40f308ee9dc6fcbeabca0ed23c6",
            "98461a42b4d2de31613957f09a265f016183d6d114ec6ae18f40831effd8b9a6",
            "d9082013b5a62c377080e0d19210545a8ed39eeec54f3b9a5f7978ca1a906a17"
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
