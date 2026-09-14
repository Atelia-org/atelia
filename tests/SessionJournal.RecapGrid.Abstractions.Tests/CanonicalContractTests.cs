using System.Text;
using System.Security.Cryptography;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid;
using Xunit;

namespace Atelia.SessionJournal.RecapGrid.Abstractions.Tests;

public sealed class CanonicalContractTests {
    private static readonly TimelineId Timeline = new(
        "00112233445566778899aabbccddeeff"
    );

    [Fact]
    public void FamilyAndDefinitionRoundTripExactCanonicalBytes() {
        FamilyDefinition family = Family();
        FamilyDefinition decoded = FamilyDefinition.DecodeCanonical(
            family.ToCanonicalBytes()
        );
        Assert.Equal(family.Digest, decoded.Digest);
        Assert.Equal(
            family.ToCanonicalBytes(),
            decoded.ToCanonicalBytes()
        );

        MaintainerDefinitionRevision definition = Definition(
            "culprit",
            family.Digest
        );
        MaintainerDefinitionRevision decodedDefinition =
            MaintainerDefinitionRevision.DecodeCanonical(
                definition.ToCanonicalBytes()
            );
        Assert.Equal(definition.Digest, decodedDefinition.Digest);
        Assert.Equal(
            MaintainerReadableScope
                .FullPriorBuildTargetAndCurrentHistorySegmentV1,
            decodedDefinition.Capability.ReadableScope
        );
        Assert.Equal(
            definition.ToCanonicalBytes(),
            decodedDefinition.ToCanonicalBytes()
        );
        Assert.StartsWith(
            "{\"schemaVersion\":2,",
            Encoding.UTF8.GetString(definition.ToCanonicalBytes()),
            StringComparison.Ordinal
        );
        Assert.Equal(
            definition.Target.SemanticHeading,
            decodedDefinition.Target.SemanticHeading
        );
    }

    [Fact]
    public void DefinitionSemanticHeading_ParticipatesInV2Digest() {
        FamilyDefinition family = Family();
        MaintainerDefinitionRevision original = Definition(
            "culprit",
            family.Digest
        );
        MaintainerDefinitionRevision changed =
            MaintainerDefinitionRevision.Create(
                original.LogicalColumnId,
                original.FamilyDigest,
                new ContextHeaderBlockTarget(
                    original.Target.Carrier,
                    original.Target.BlockKey,
                    original.Target.SemanticHeading + " updated"
                ),
                original.Capability,
                original.DeclarativeSpec,
                original.MaxContentUtf8Bytes
            );

        Assert.NotEqual(original.Digest, changed.Digest);
        Assert.Throws<InvalidDataException>(() =>
            MaintainerDefinitionRevision.DecodeCanonical("{"u8)
        );
    }

    [Fact]
    public void DefinitionV2_StrictCanonicalRejectsMissingUnknownOrReorderedSemanticHeading() {
        MaintainerDefinitionRevision definition = Definition(
            "culprit",
            Family().Digest
        );
        string canonical = Encoding.UTF8.GetString(
            definition.ToCanonicalBytes()
        );
        const string Target =
            "\"target\":{\"carrier\":\"system\",\"blockKey\":\"culprit\","
            + "\"semanticHeading\":\"Derived context from prior history: culprit\"}";
        Assert.Contains(Target, canonical, StringComparison.Ordinal);
        string[] malformed = [
            canonical.Replace(
                ",\"semanticHeading\":\"Derived context from prior history: culprit\"",
                "",
                StringComparison.Ordinal
            ),
            canonical.Replace(
                Target,
                Target[..^1] + ",\"unknown\":true}",
                StringComparison.Ordinal
            ),
            canonical.Replace(
                Target,
                "\"target\":{\"carrier\":\"system\","
                + "\"semanticHeading\":\"Derived context from prior history: culprit\","
                + "\"blockKey\":\"culprit\"}",
                StringComparison.Ordinal
            )
        ];

        Assert.All(malformed, value =>
            Assert.Throws<InvalidDataException>(() =>
                MaintainerDefinitionRevision.DecodeCanonical(
                    Encoding.UTF8.GetBytes(value)
                )
            )
        );
    }

    [Theory]
    [InlineData(
        "system",
        "59d07e92d18b09470d57be97d633cc96d2cc33346c4e7a0865adf46c069896c1",
        "Derived context from prior history: culprit"
    )]
    [InlineData(
        "observation",
        "8aa3751bdd1dfca388dea7da3e50adff82e561448f204f7d6b160c3289feab93",
        "Derived context from prior history, not a new user request: culprit"
    )]
    [InlineData(
        "action",
        "511ed63c5b01e43b574b8c5f9f5316ba174b8e1e272b2e54e9380fea788d25dc",
        "Derived context from prior history, not the current Assistant reply: culprit"
    )]
    public void DefinitionV1_DecodesExactBytesWithCarrierAwareLegacyHeading(
        string carrier,
        string digest,
        string expectedHeading
    ) {
        string canonical =
            $"{{\"schemaVersion\":1,\"digest\":\"{digest}\","
            + "\"logicalColumnId\":\"culprit\",\"familyDigest\":\"d53cead7c23bb8a318751127d013971414dee255fed4ef5b81bfd376f18b4fc1\","
            + $"\"target\":{{\"carrier\":\"{carrier}\",\"blockKey\":\"culprit\"}},"
            + "\"capability\":{\"schemaVersion\":1,\"runtimeProtocolId\":\"text-runtime-v3\","
            + "\"readableScope\":\"full-prior-build-target-and-current-history-segment-v1\",\"semanticModelId\":\"model-class-v1\"},"
            + "\"capabilityFingerprint\":\"65c6a496022d0f884be1fa83a26174138c78f18d3d8617688eae9692908f7601\","
            + "\"declarativeSpec\":{\"topic\":\"Investigate culprit\",\"userPromptTemplate\":\"Maintain the culprit hypothesis.\"},"
            + "\"maxContentUtf8Bytes\":8192}";
        byte[] canonicalBytes = Encoding.UTF8.GetBytes(canonical);

        MaintainerDefinitionRevision decoded =
            MaintainerDefinitionRevision.DecodeCanonical(canonicalBytes);

        Assert.Equal(
            digest,
            decoded.Digest.Value
        );
        Assert.Equal(canonicalBytes, decoded.ToCanonicalBytes());
        Assert.Equal(
            expectedHeading,
            decoded.Target.SemanticHeading
        );
    }

    [Fact]
    public void DecoderRejectsWhitespaceUnknownMemberAndWrongCase() {
        byte[] canonical = Family().ToCanonicalBytes();
        Assert.Throws<InvalidDataException>(() =>
            FamilyDefinition.DecodeCanonical(
                Encoding.UTF8.GetBytes(
                    " " + Encoding.UTF8.GetString(canonical)
                )));
        string text = Encoding.UTF8.GetString(canonical);
        Assert.Throws<InvalidDataException>(() =>
            FamilyDefinition.DecodeCanonical(Encoding.UTF8.GetBytes(
                text.Replace(
                    "\"digest\":",
                    "\"unknown\":0,\"digest\":",
                    StringComparison.Ordinal
                ))));
        const string OutputProtocol =
            "\"outputProtocol\":{\"protocolId\":\"atelia.recap.output.v3\","
            + "\"mode\":\"full-replacement-text\"}";
        Assert.Contains(OutputProtocol, text, StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() =>
            FamilyDefinition.DecodeCanonical(Encoding.UTF8.GetBytes(
                text.Replace(
                    OutputProtocol,
                    "\"outputProtocol\":null",
                    StringComparison.Ordinal
                ))));
        Assert.Throws<InvalidDataException>(() =>
            FamilyDefinition.DecodeCanonical([
                0xef, 0xbb, 0xbf, .. canonical
            ]));
        Assert.Throws<InvalidDataException>(() =>
            FamilyDefinition.DecodeCanonical([
                .. canonical, (byte)'\n'
            ]));
        Assert.Throws<InvalidDataException>(() =>
            FamilyDefinition.DecodeCanonical([
                0xff, .. canonical
            ]));
        Assert.Throws<InvalidDataException>(() =>
            FamilyDefinition.DecodeCanonical(Encoding.UTF8.GetBytes(
                text.Replace(
                    "{\"schemaVersion\":2,",
                    "{\"schemaVersion\":2,\"schemaVersion\":2,",
                    StringComparison.Ordinal
                ))));
        string digestPrefix = $"{{\"schemaVersion\":2,\"digest\":\"{Family().Digest.Value}\",";
        Assert.StartsWith(digestPrefix, text, StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() =>
            FamilyDefinition.DecodeCanonical(Encoding.UTF8.GetBytes(
                text.Replace(
                    digestPrefix,
                    $"{{\"digest\":\"{Family().Digest.Value}\",\"schemaVersion\":2,",
                    StringComparison.Ordinal
                ))));
        Assert.Throws<InvalidDataException>(() =>
            FamilyDefinition.DecodeCanonical(Encoding.UTF8.GetBytes(
                text.Replace(
                    "\"systemPrompt\":",
                    "\"SystemPrompt\":",
                    StringComparison.Ordinal
                ))));
    }

    [Fact]
    public void ConstructorsRejectInvalidUtf16AndDuplicateMembers() {
        Assert.Throws<ArgumentException>(() =>
            new LogicalColumnId("bad\ud800"));
        var scalar = new FamilyScalarInputSchema(
            FamilyScalarType.String
        );
        Assert.Throws<ArgumentException>(() =>
            new FamilyObjectInputSchema([
                new FamilyToolProperty("same", scalar, true),
                new FamilyToolProperty("same", scalar, false)
            ]));
        Assert.Throws<ArgumentException>(() => new FamilyScalarInputSchema(
            FamilyScalarType.String,
            orderedEnum: ["same", "same"]
        ));
    }

    [Fact]
    public void InputsAreDefensivelyCopied() {
        var property = new FamilyToolProperty(
            "answer",
            new FamilyScalarInputSchema(FamilyScalarType.String),
            true
        );
        FamilyToolProperty[] properties = [property];
        var schema = new FamilyObjectInputSchema(properties);
        properties[0] = new FamilyToolProperty(
            "other",
            new FamilyScalarInputSchema(FamilyScalarType.Boolean),
            false
        );
        Assert.Equal("answer", Assert.Single(schema.Properties).Name);

        byte[] bytes = Family().ToCanonicalBytes();
        byte original = bytes[0];
        bytes[0] = 0;
        Assert.Equal(original, Family().ToCanonicalBytes()[0]);
    }

    [Fact]
    public void FullAndOverlayEnforceTargetOrderAndCompatibility() {
        FamilyDefinition family = Family();
        MaintainerDefinitionRevision culprit = Definition(
            "culprit",
            family.Digest
        );
        MaintainerDefinitionRevision world = Definition(
            "world",
            family.Digest
        );
        BuildTarget target = Target(culprit, world);
        GridBuildRecipe full = GridBuildRecipe.CreateFull(
            Timeline,
            RowId('f'),
            target
        );
        BuildTarget changed = BuildTarget.Create([
            new BuildTargetColumn(
                culprit.LogicalColumnId,
                ChangedDefinitionDigest(culprit.Digest)
            ),
            new BuildTargetColumn(world.LogicalColumnId, world.Digest)
        ]);
        GridBuildRecipe overlay = GridBuildRecipe.CreateOverlay(
            full,
            RowId('f'),
            changed,
            [culprit.LogicalColumnId]
        );
        Assert.Equal(GridBuildRecipeKind.Overlay, overlay.Kind);
        Assert.Equal(full.Digest, overlay.BaseRecipeDigest);
        Assert.Equal(
            overlay.ToCanonicalBytes(),
            GridBuildRecipe.DecodeCanonical(
                overlay.ToCanonicalBytes()
            ).ToCanonicalBytes()
        );

        BuildTarget reordered = BuildTarget.Create([
            new BuildTargetColumn(world.LogicalColumnId, world.Digest),
            new BuildTargetColumn(culprit.LogicalColumnId, culprit.Digest)
        ]);
        GridBuildRecipe reorderedOverlay = GridBuildRecipe.CreateOverlay(
            full,
            RowId('f'),
            reordered,
            [culprit.LogicalColumnId]
        );
        Assert.Equal(
            [world.LogicalColumnId, culprit.LogicalColumnId],
            reorderedOverlay.Target.OrderedColumns.Select(
                static column => column.LogicalColumnId
            )
        );
        Assert.Throws<ArgumentException>(() =>
            GridBuildRecipe.CreateOverlay(
                full,
                RowId('f'),
                reordered,
                Array.Empty<LogicalColumnId>()
            ));
    }

    [Fact]
    public void RuleIdentitiesAndCanonicalBytesKeepExistingGoldens() {
        FamilyDefinition family = Family();
        MaintainerDefinitionRevision definition = Definition("culprit", family.Digest);
        BuildTarget target = Target(definition);
        GridBuildRecipe recipe = GridBuildRecipe.CreateFull(Timeline, RowId('a'), target);
        Assert.Equal(new[] {
            "d53cead7c23bb8a318751127d013971414dee255fed4ef5b81bfd376f18b4fc1",
            "fadf96f1c8bb19c837d2f3c9c2e2cb0ff3b76cbaacce78aaa0ca18e38e5c6084",
            "d3e5570da30f97bb33fdefe2f3effb14ec706a7320e21471eea34e24958666a3",
            "4b281e3de8409b88eeb330813b36526ed150aaf958c7a29ecfafc64541cadd19"
        }, new[] { family.Digest.Value, definition.Digest.Value, target.Digest.Value, recipe.Digest.Value });
        Assert.Equal(new[] {
            "9522354f4dca93dc4499bb7f5f4567c02c2c885d3e0b1a10a81624dd4dba7315",
            "551f58efc84fc7164e4f7aad1c1f0e4cb38dfa00de77c0e41bcda24028df51d6",
            "111b651063cd05c6c75fb9dc819936cbcb33bf1919f32f22c99356fa13278731",
            "7fd854fa376c08cc22bd3e57c970eef98e2bf344141a945ec3c3f96af38a32cd"
        }, new[] { family.ToCanonicalBytes(), definition.ToCanonicalBytes(),
            target.ToCanonicalBytes(), recipe.ToCanonicalBytes() }
            .Select(static bytes => Convert.ToHexStringLower(SHA256.HashData(bytes))));
        Assert.Equal(target.ToCanonicalBytes(), BuildTarget.DecodeCanonical(target.ToCanonicalBytes()).ToCanonicalBytes());
        Assert.Equal(recipe.ToCanonicalBytes(), GridBuildRecipe.DecodeCanonical(recipe.ToCanonicalBytes()).ToCanonicalBytes());
    }

    [Fact]
    public void NestedRecipeTargetRejectsNonCanonicalBytes() {
        BuildTarget target = Target(Definition("culprit", Family().Digest));
        GridBuildRecipe recipe = GridBuildRecipe.CreateFull(Timeline, RowId('a'), target);
        byte[] targetWithTrailingWhitespace = [.. target.ToCanonicalBytes(), (byte)'\n'];
        string original = Encoding.UTF8.GetString(recipe.ToCanonicalBytes());
        string changed = original.Replace(Convert.ToBase64String(target.ToCanonicalBytes()),
            Convert.ToBase64String(targetWithTrailingWhitespace), StringComparison.Ordinal);
        Assert.NotEqual(original, changed);
        Assert.Throws<InvalidDataException>(() => GridBuildRecipe.DecodeCanonical(Encoding.UTF8.GetBytes(changed)));
    }

    [Fact]
    public void RuleFactoriesRejectDefaultTypedValues() {
        MaintainerDefinitionRevision definition = Definition("culprit", Family().Digest);
        Assert.Throws<ArgumentException>(() => new BuildTargetColumn(default, definition.Digest));
        Assert.Throws<ArgumentException>(() => new BuildTargetColumn(definition.LogicalColumnId, default));
        Assert.Throws<ArgumentException>(() => GridBuildRecipe.CreateFull(default, RowId('a'), Target(definition)));
    }

    [Fact]
    public void EmptyFullTargetAndFullReplacementModeAreCanonical() {
        BuildTarget empty = BuildTarget.Create(
            Array.Empty<BuildTargetColumn>()
        );
        GridBuildRecipe recipe = GridBuildRecipe.CreateFull(
            Timeline,
            bootstrapThroughRowId: null,
            target: empty
        );
        Assert.Empty(recipe.Target.OrderedColumns);
        Assert.Null(recipe.BootstrapThroughRowId);
        GridBuildRecipe decodedRecipe = GridBuildRecipe.DecodeCanonical(
            recipe.ToCanonicalBytes()
        );
        Assert.Empty(decodedRecipe.Target.OrderedColumns);
        Assert.Null(decodedRecipe.BootstrapThroughRowId);

        FamilyDefinition family = FamilyDefinition.Create(
            "prompt",
            [],
            new FamilyOutputProtocol(
                "output-v1",
                FamilyOutputMode.FullReplacementText
            ),
            new FamilyInputRenderingProtocol(
                "input-v1",
                "prior-v1",
                "history-v1"
            )
        );
        Assert.Equal(
            FamilyOutputMode.FullReplacementText,
            FamilyDefinition.DecodeCanonical(family.ToCanonicalBytes())
                .OutputProtocol.Mode
        );
        Assert.Throws<ArgumentException>(() => FamilyDefinition.Create(
            "prompt",
            [new FamilyToolDefinition(
                "done",
                "done",
                new FamilyObjectInputSchema([])
            )],
            new FamilyOutputProtocol(
                "output-v1",
                FamilyOutputMode.FullReplacementText
            ),
            new FamilyInputRenderingProtocol(
                "input-v1",
                "prior-v1",
                "history-v1"
            )
        ));
        Assert.Throws<ArgumentException>(() =>
            new FamilyScalarInputSchema(
                FamilyScalarType.Boolean,
                orderedEnum: ["true"]
            ));
    }

    [Fact]
    public void DenseToolSchemaRemainsExact() {
        var tool = new FamilyToolDefinition(
            "submit_typed_evidence",
            "Submit typed evidence.",
            new FamilyObjectInputSchema([
                    new FamilyToolProperty(
                        "scores",
                        new FamilyArrayInputSchema(
                            new FamilyScalarInputSchema(
                                FamilyScalarType.Int64
                            )
                        ),
                        true
                    ),
                    new FamilyToolProperty(
                        "confirmed",
                        new FamilyScalarInputSchema(
                            FamilyScalarType.Boolean,
                            nullable: true
                        ),
                        false
                    ),
                    new FamilyToolProperty(
                        "verdict",
                        new FamilyScalarInputSchema(
                            FamilyScalarType.String,
                            orderedEnum: ["open", "closed"]
                        ),
                        true
                    )
                ])
        );
        Assert.Throws<ArgumentException>(() => FamilyDefinition.Create(
            "Render every supported V1 schema kind.",
            [tool],
            new FamilyOutputProtocol(
                "typed-output-v1",
                FamilyOutputMode.FullReplacementText
            ),
            new FamilyInputRenderingProtocol(
                "typed-input-v1",
                "prior-v1",
                "history-v1"
            )
        ));
        FamilyObjectInputSchema root = Assert.IsType<
            FamilyObjectInputSchema
        >(tool.InputSchema);
        FamilyArrayInputSchema scores = Assert.IsType<
            FamilyArrayInputSchema
        >(root.Properties[0].Schema);
        Assert.Equal(
            FamilyScalarType.Int64,
            Assert.IsType<FamilyScalarInputSchema>(scores.Item).ScalarType
        );
        Assert.True(Assert.IsType<FamilyScalarInputSchema>(
            root.Properties[1].Schema
        ).Nullable);
        Assert.Equal(
            ["open", "closed"],
            Assert.IsType<FamilyScalarInputSchema>(
                root.Properties[2].Schema
            ).OrderedEnum
        );
    }

    private static FamilyDefinition Family() {
        return FamilyDefinition.Create(
            "Maintain one explicit line of inquiry.",
            [],
            new FamilyOutputProtocol(
                "atelia.recap.output.v3",
                FamilyOutputMode.FullReplacementText
            ),
            new FamilyInputRenderingProtocol(
                "atelia.recap.input.v1",
                "atelia.recap.prior.v1",
                "atelia.history.segment.v1"
            )
        );
    }

    private static MaintainerDefinitionRevision Definition(
        string column,
        FamilyDefinitionDigest familyDigest
    ) => MaintainerDefinitionRevision.Create(
        new LogicalColumnId(column),
        familyDigest,
        new ContextHeaderBlockTarget(
            ContextHeaderCarrier.System,
            column,
            $"Derived context from prior history: {column}"
        ),
        new MaintainerCapabilitySpec(
            "text-runtime-v3",
            MaintainerReadableScope
                .FullPriorBuildTargetAndCurrentHistorySegmentV1,
            "model-class-v1"
        ),
        new MaintainerDeclarativeSpec(
            $"Investigate {column}",
            $"Maintain the {column} hypothesis."
        ),
        maxContentUtf8Bytes: 8 * 1024
    );

    private static BuildTarget Target(
        params MaintainerDefinitionRevision[] definitions
    ) => BuildTarget.Create(definitions.Select(static definition =>
        new BuildTargetColumn(
            definition.LogicalColumnId,
            definition.Digest
        )));

    private static MaintainerDefinitionDigest ChangedDefinitionDigest(MaintainerDefinitionDigest value)
        => new(value.Value[0] == 'a' ? "b" + value.Value[1..] : "a" + value.Value[1..]);

    private static HistoryRowId RowId(char value) => new(new string(value, 64));
}
