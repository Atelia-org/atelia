using System.Security.Cryptography;
using Atelia.SessionJournal;
using Atelia.SessionJournal.RecapGrid;
using Atelia.SessionJournal.RecapGrid.Control;
using Xunit;

namespace Atelia.Galatea.RecapGrid.Tests;

public sealed class GalateaRecapGridAssetsTests {
    [Fact]
    public void RollingRewriteV2_IsExactProviderNeutralCanonicalBundle() {
        Assert.Equal(
            [GalateaRecapGridAssets.RollingRewriteZhCnV2],
            GalateaRecapGridAssets.AssetIds
        );
        Assert.False(GalateaRecapGridAssets.TryCreateRegistrationBundle(
            "unknown",
            out RecapGridControlRegistrationBundle? unknown
        ));
        Assert.Null(unknown);
        Assert.True(GalateaRecapGridAssets.TryCreateRegistrationBundle(
            GalateaRecapGridAssets.RollingRewriteZhCnV2,
            out RecapGridControlRegistrationBundle? bundle
        ));
        Assert.NotNull(bundle);

        FamilyDefinition family = Assert.Single(bundle.Families);
        Assert.Empty(bundle.Recipes);
        Assert.Equal(2, bundle.Definitions.Count);
        FamilyToolDefinition tool = Assert.Single(family.OrderedTools);
        Assert.Equal(RecapRewriterProtocolV2.TerminalToolName, tool.Name);
        Assert.Equal(RecapRewriterProtocolV2.OutputProtocolId,
            family.OutputProtocol.ProtocolId);
        Assert.Equal(RecapRewriterProtocolV2.TerminalToolName,
            family.OutputProtocol.TerminalToolName);
        Assert.Equal(FamilyToolChoice.Required,
            family.OutputProtocol.ToolChoice);
        Assert.False(family.OutputProtocol.AllowParallel);
        Assert.Equal(RecapRewriterProtocolV2.InputProtocolId,
            family.InputRenderingProtocol.ProtocolId);
        Assert.Equal(RecapRewriterProtocolV2.PriorProjectionSchemaId,
            family.InputRenderingProtocol.PriorProjectionSchemaId);
        Assert.Equal(RecapRewriterProtocolV2.HistorySegmentRenderingSchemaId,
            family.InputRenderingProtocol.HistorySegmentRenderingSchemaId);
        Assert.DoesNotContain(
            RecapRewriterProtocolV2.ReservedProtocolToken,
            family.SystemPrompt,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            RecapRewriterProtocolV2.ReservedProtocolToken,
            tool.Description,
            StringComparison.Ordinal
        );

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
                RecapRewriterProtocolV2.UpdatedOutcome,
                RecapRewriterProtocolV2.KeepUnchangedOutcome
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
            Assert.Equal(RecapRewriterProtocolV2.RuntimeProtocolId,
                definition.Capability.RuntimeProtocolId);
            Assert.Null(definition.Capability.SemanticModelId);
            Assert.Equal(32 * 1024, definition.MaxContentUtf8Bytes);
        });

        AssertGoldenDigests(bundle);
    }

    [Fact]
    public void Materialization_IsDeterministicAndResourcesAreExact() {
        Assert.True(GalateaRecapGridAssets.TryCreateRegistrationBundle(
            GalateaRecapGridAssets.RollingRewriteZhCnV2,
            out RecapGridControlRegistrationBundle? first
        ));
        Assert.True(GalateaRecapGridAssets.TryCreateRegistrationBundle(
            GalateaRecapGridAssets.RollingRewriteZhCnV2,
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
            "27fa49f793f0e41cff8eb3f6e6294951fb4fe402039d8f2ca919d22ccc255cb8",
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
        Assert.All(first.Definitions, definition => {
            Assert.DoesNotContain(
                RecapRewriterProtocolV2.ReservedProtocolToken,
                definition.DeclarativeSpec.Topic,
                StringComparison.Ordinal
            );
            Assert.DoesNotContain(
                RecapRewriterProtocolV2.ReservedProtocolToken,
                definition.DeclarativeSpec.UserPromptTemplate,
                StringComparison.Ordinal
            );
        });
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
            "46ede01e56d693a8da71175e83dbca776fd2f878d7febdf7252d8c98873d38f4",
            "05716d6ee578465e6c56f5f27b766c6f49b67e9314c4f2d5d62d7caa57ec23d7",
            "a6225a430baed394ecabab21b0468aabba26835952f16190197e1e5f7f6c24df",
            "93bd931814e60107d59d7ebf707f083e9b7739fc415ebc4911e58ad58b57c7c4"
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
