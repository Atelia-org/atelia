using System.Text;
using System.Security.Cryptography;
using Atelia.EventJournal;
using Atelia.SessionJournal;
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
            "\"outputProtocol\":{\"protocolId\":\"atelia.recap.output.v2\","
            + "\"terminalToolName\":\"submit_recap\",\"toolChoice\":\"required\","
            + "\"allowParallel\":false}";
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
                    "{\"schemaVersion\":1,",
                    "{\"schemaVersion\":1,\"schemaVersion\":1,",
                    StringComparison.Ordinal
                ))));
        string digestPrefix = $"{{\"schemaVersion\":1,\"digest\":\"{Family().Digest.Value}\",";
        Assert.StartsWith(digestPrefix, text, StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() =>
            FamilyDefinition.DecodeCanonical(Encoding.UTF8.GetBytes(
                text.Replace(
                    digestPrefix,
                    $"{{\"digest\":\"{Family().Digest.Value}\",\"schemaVersion\":1,",
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
    public void ContentEquivalentViewsShareProjectionIdentity() {
        (GridBuildRecipe recipe, MaintainerDefinitionRevision definition) =
            SingleColumnRecipe();
        EvaluationKey evaluation = EvaluationKey.Create(
            HistoryDigest('1'),
            definition.Digest,
            PriorInputReference.FirstRow.Value
        );
        RowBuildSpec spec = RowBuildSpec.CreateFull(
            recipe,
            Coordinate(recipe, RowId('1'), HistoryDigest('1')),
            PriorInputReference.FirstRow.Value,
            [new RowBuildAssignment.Evaluate(
                definition.LogicalColumnId,
                evaluation
            )]
        );
        RecapCellArtifact cell = RecapCellArtifact.Create(
            definition.LogicalColumnId,
            definition.Digest,
            evaluation,
            RecapCellOutcome.Updated,
            "same content",
            definition.MaxContentUtf8Bytes
        );
        RecapRowView first = RecapRowView.Create(spec, [cell]);
        PriorInputProjection firstProjection = PriorInputProjection.Create(
            [new PriorProjectedContent(
                definition.LogicalColumnId,
                cell.ContentDigest
            )]
        );
        PriorInputProjection secondProjection = PriorInputProjection.Create(
            [new PriorProjectedContent(
                definition.LogicalColumnId,
                cell.ContentDigest
            )]
        );
        Assert.Equal(firstProjection.Digest, secondProjection.Digest);
    }

    [Fact]
    public void RowBuildAndViewRejectNonExactMembership() {
        (GridBuildRecipe recipe, MaintainerDefinitionRevision definition) =
            SingleColumnRecipe();
        EvaluationKey evaluation = EvaluationKey.Create(
            HistoryDigest('2'),
            definition.Digest,
            PriorInputReference.FirstRow.Value
        );
        Assert.Throws<ArgumentException>(() => RowBuildSpec.CreateFull(
            recipe,
            Coordinate(recipe, RowId('2'), HistoryDigest('2')),
            PriorInputReference.FirstRow.Value,
            Array.Empty<RowBuildAssignment>()
        ));
        RowBuildSpec spec = RowBuildSpec.CreateFull(
            recipe,
            Coordinate(recipe, RowId('2'), HistoryDigest('2')),
            PriorInputReference.FirstRow.Value,
            [new RowBuildAssignment.Evaluate(
                definition.LogicalColumnId,
                evaluation
            )]
        );
        Assert.Throws<ArgumentException>(() => RecapRowView.Create(
            spec,
            Array.Empty<RecapCellArtifact>()
        ));
    }

    [Fact]
    public void FormalValuesRoundTripAndKeepDomainSeparatedGoldens() {
        FormalFixture value = FormalValues();
        Assert.Equal(
            "4cde3f7dfbcd55913fe46237bcdf45ea8f238a21f328bc62e7dea8bb165ce502\n"
            + "acab1da33c452934852c2ea7a100f7d7849d56f9e170fbb428cf2832654a13cd\n"
            + "b8873a462e8abfdb47fce1bf1799f819a71f9e31a3529626988b949333f5bb94\n"
            + "e2b0bf18e43eb35858beb38809bf35e8be815dd71915f01ee98fcee365336605\n"
            + "7b622b3e5ba946841d45722375ae7a5ae0f15b75b3f28b4ccb7c74ba642cb753\n"
            + "36ff79d729d88173bbb7750c1d6839b2d26e2903f9cb9f251a52e6df7e4293ce\n"
            + "13abe0196f72da491691abb9bf9f1747c7e57ee5f0f2d65e5988fd738ac38344\n"
            + "8805b1e8d2a2b4517adfb1ca572d53b177e72cc985020abb3614b45c9b03d734\n"
            + "19b526966710363c72bf28dcacc774f80f68d8a3a531deabcb6c5be9812578ae",
            string.Join("\n", new[] {
                value.Family.Digest.Value,
                value.Definition.Digest.Value,
                value.Target.Digest.Value,
                value.Recipe.Digest.Value,
                value.Projection.Digest.Value,
                value.Evaluation.Digest.Value,
                value.Cell.ContentDigest.Value,
                value.Cell.CellDigest.Value,
                value.View.Digest.Value
            })
        );
    }

    [Fact]
    public void ArtifactAndKeyCanonicalValuesRoundTripExactly() {
        FormalFixture value = FormalValues();
        Assert.Equal(
            value.Target.ToCanonicalBytes(),
            BuildTarget.DecodeCanonical(
                value.Target.ToCanonicalBytes()
            ).ToCanonicalBytes()
        );
        Assert.Equal(
            value.Recipe.ToCanonicalBytes(),
            GridBuildRecipe.DecodeCanonical(
                value.Recipe.ToCanonicalBytes()
            ).ToCanonicalBytes()
        );
        Assert.Equal(
            value.Projection.ToCanonicalBytes(),
            PriorInputProjection.DecodeCanonical(
                value.Projection.ToCanonicalBytes()
            ).ToCanonicalBytes()
        );
        Assert.Equal(
            value.Evaluation.ToCanonicalBytes(),
            EvaluationKey.DecodeCanonical(
                value.Evaluation.ToCanonicalBytes()
            ).ToCanonicalBytes()
        );
        Assert.Equal(
            value.Cell.ToCanonicalBytes(),
            RecapCellArtifact.DecodeCanonical(
                value.Cell.ToCanonicalBytes()
            ).ToCanonicalBytes()
        );
        Assert.Equal(
            value.View.ToCanonicalBytes(),
            RecapRowView.DecodeCanonical(
                value.View.ToCanonicalBytes()
            ).ToCanonicalBytes()
        );
        Assert.Equal(
            value.View.ToCanonicalBytes(),
            RecapRowView.DecodeCanonical(
                value.Spec,
                [value.Cell],
                value.View.ToCanonicalBytes()
            ).ToCanonicalBytes()
        );
        Assert.Equal(
            value.Fulfilled.ToCanonicalBytes(),
            FulfilledViewKey.DecodeCanonical(
                value.Fulfilled.ToCanonicalBytes()
            ).ToCanonicalBytes()
        );
        Assert.Equal(
            value.Fulfilled.ToCanonicalBytes(),
            FulfilledViewKey.DecodeCanonical(
                value.Recipe,
                value.TimelineHead,
                value.Fulfilled.ToCanonicalBytes()
            ).ToCanonicalBytes()
        );
        Assert.All(new[] {
            value.Family.ToCanonicalBytes(),
            value.Definition.ToCanonicalBytes(),
            value.Target.ToCanonicalBytes(),
            value.Recipe.ToCanonicalBytes(),
            value.Projection.ToCanonicalBytes(),
            value.Evaluation.ToCanonicalBytes(),
            value.Cell.ToCanonicalBytes(),
            value.Fulfilled.ToCanonicalBytes()
        }, bytes => Assert.StartsWith(
            "{\"schemaVersion\":1,",
            Encoding.UTF8.GetString(bytes),
            StringComparison.Ordinal
        ));
        Assert.StartsWith(
            "{\"schemaVersion\":2,",
            Encoding.UTF8.GetString(value.View.ToCanonicalBytes()),
            StringComparison.Ordinal
        );
        Assert.Equal(9, new[] {
            value.Family.Digest.Value,
            value.Definition.Digest.Value,
            value.Target.Digest.Value,
            value.Recipe.Digest.Value,
            value.Projection.Digest.Value,
            value.Evaluation.Digest.Value,
            value.Cell.ContentDigest.Value,
            value.Cell.CellDigest.Value,
            value.View.Digest.Value
        }.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            "9ba652b6646007c2bf347f9a1df78412e68f92d285dca3063bf2fafea15d0a5a\n"
            + "5025e7454f0521584b0bf4adaa525252e49d28dcc3f96b939c1ae060bdf17514\n"
            + "0db1cadcca718cdfb7467897e47e1f419b9e152b1e48af12a3b84eefc11ca2bc\n"
            + "1e76f1f620de8cff67628a19a1eaa59abce8e942f324440cf5d873279d6d4185\n"
            + "436ac01f8031b636eeaf77b02aa1fd4df81f4bb98decae7cf7b01907adced7a2\n"
            + "f26ba200108d175e01852688c027e837a306118630f21a6df60f57927b395f17\n"
            + "953c201059a560f29703155808d5d754390da8a974bfae3b583670bf61876c82\n"
            + "8fcceeab942086c5da505711dfd8eab735d6b72f8027b377acbd80ab1e891055\n"
            + "ab658ea7e9d4f4a138d66b421ea506307e058e0f70c388e23f24ef713a341943",
            string.Join("\n", new[] {
                CanonicalSha(value.Family.ToCanonicalBytes()),
                CanonicalSha(value.Definition.ToCanonicalBytes()),
                CanonicalSha(value.Target.ToCanonicalBytes()),
                CanonicalSha(value.Recipe.ToCanonicalBytes()),
                CanonicalSha(value.Projection.ToCanonicalBytes()),
                CanonicalSha(value.Evaluation.ToCanonicalBytes()),
                CanonicalSha(value.Cell.ToCanonicalBytes()),
                CanonicalSha(value.View.ToCanonicalBytes()),
                CanonicalSha(value.Fulfilled.ToCanonicalBytes())
            })
        );
    }

    [Fact]
    public void DefaultTypedValuesAreRejectedAtFactories() {
        FormalFixture value = FormalValues();
        Assert.Throws<ArgumentException>(() =>
            new BuildTargetColumn(default, value.Definition.Digest));
        Assert.Throws<ArgumentException>(() =>
            new BuildTargetColumn(
                value.Definition.LogicalColumnId,
                default
            ));
        Assert.Throws<ArgumentException>(() => GridBuildRecipe.CreateFull(
            default,
            value.Recipe.BootstrapThroughRowId,
            value.Target
        ));
        Assert.Throws<ArgumentException>(() => EvaluationKey.Create(
            default,
            value.Definition.Digest,
            PriorInputReference.FirstRow.Value
        ));
        Assert.Throws<ArgumentException>(() => RowBuildSpec.CreateFull(
            value.Recipe,
            Coordinate(
                value.Recipe,
                default,
                value.Evaluation.HistorySegmentDigest
            ),
            PriorInputReference.FirstRow.Value,
            [new RowBuildAssignment.Evaluate(
                value.Definition.LogicalColumnId,
                value.Evaluation
            )]
        ));
        Assert.Throws<ArgumentException>(() => FulfilledViewKey.Create(
            default,
            value.TimelineHead,
            value.View.RowDescriptorDigest,
            value.Recipe
        ));
    }

    [Fact]
    public void EmptyFullTargetAndNullableParallelAreCanonical() {
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
            [new FamilyToolDefinition(
                "done",
                "done",
                new FamilyObjectInputSchema([])
            )],
            new FamilyOutputProtocol(
                "output-v1",
                "done",
                FamilyToolChoice.Auto,
                allowParallel: null
            ),
            new FamilyInputRenderingProtocol(
                "input-v1",
                "prior-v1",
                "history-v1"
            )
        );
        Assert.Null(FamilyDefinition.DecodeCanonical(
            family.ToCanonicalBytes()
        ).OutputProtocol.AllowParallel);
        Assert.Throws<ArgumentException>(() =>
            new FamilyScalarInputSchema(
                FamilyScalarType.Boolean,
                orderedEnum: ["true"]
            ));
    }

    [Fact]
    public void DenseFamilySchemaAndProjectionDiscriminantRoundTrip() {
        FamilyDefinition family = FamilyDefinition.Create(
            "Render every supported V1 schema kind.",
            [new FamilyToolDefinition(
                RecapRewriterProtocolV2.TerminalToolName,
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
            )],
            new FamilyOutputProtocol(
                "typed-output-v1",
                RecapRewriterProtocolV2.TerminalToolName,
                FamilyToolChoice.Required,
                allowParallel: null
            ),
            new FamilyInputRenderingProtocol(
                "typed-input-v1",
                "prior-v1",
                "history-v1"
            )
        );

        FamilyDefinition decoded = FamilyDefinition.DecodeCanonical(
            family.ToCanonicalBytes()
        );
        FamilyObjectInputSchema root = Assert.IsType<
            FamilyObjectInputSchema
        >(Assert.Single(decoded.OrderedTools).InputSchema);
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
        Assert.Null(decoded.OutputProtocol.AllowParallel);

        FormalFixture value = FormalValues();
        EvaluationKey first = EvaluationKey.Create(
            value.Evaluation.HistorySegmentDigest,
            value.Definition.Digest,
            PriorInputReference.FirstRow.Value
        );
        EvaluationKey projected = EvaluationKey.Create(
            value.Evaluation.HistorySegmentDigest,
            value.Definition.Digest,
            new PriorInputReference.Projection(value.Projection.Digest)
        );
        Assert.NotEqual(first.Digest, projected.Digest);
        Assert.IsType<PriorInputReference.FirstRow>(
            EvaluationKey.DecodeCanonical(
                first.ToCanonicalBytes()
            ).PriorInput
        );
        Assert.Equal(
            value.Projection.Digest,
            Assert.IsType<PriorInputReference.Projection>(
                EvaluationKey.DecodeCanonical(
                    projected.ToCanonicalBytes()
                ).PriorInput
            ).Digest
        );
    }

    [Fact]
    public void SameTargetOverlayAndKeepUnchangedRemainDistinctCanonicalValues() {
        FormalFixture value = FormalValues();
        GridBuildRecipe overlay = GridBuildRecipe.CreateOverlay(
            value.Recipe,
            value.Recipe.BootstrapThroughRowId,
            value.Target,
            [value.Definition.LogicalColumnId]
        );
        Assert.NotEqual(value.Recipe.Digest, overlay.Digest);
        Assert.Equal(
            overlay.ToCanonicalBytes(),
            GridBuildRecipe.DecodeCanonical(
                overlay.ToCanonicalBytes()
            ).ToCanonicalBytes()
        );

        RecapCellArtifact unchanged = RecapCellArtifact.Create(
            value.Definition.LogicalColumnId,
            value.Definition.Digest,
            value.Evaluation,
            RecapCellOutcome.KeepUnchanged,
            value.Cell.Content,
            value.Definition.MaxContentUtf8Bytes
        );
        Assert.Equal(
            RecapCellOutcome.KeepUnchanged,
            RecapCellArtifact.DecodeCanonical(
                unchanged.ToCanonicalBytes()
            ).Outcome
        );
        Assert.NotEqual(value.Cell.CellDigest, unchanged.CellDigest);
        Assert.Equal(value.Cell.ContentDigest, unchanged.ContentDigest);
    }

    [Fact]
    public void NestedCanonicalChildrenRejectNonCanonicalBytes() {
        FormalFixture value = FormalValues();
        byte[] targetWithTrailingWhitespace = [
            .. value.Target.ToCanonicalBytes(),
            (byte)'\n'
        ];
        string recipe = Encoding.UTF8.GetString(
            value.Recipe.ToCanonicalBytes()
        );
        string tamperedRecipe = recipe.Replace(
            Convert.ToBase64String(value.Target.ToCanonicalBytes()),
            Convert.ToBase64String(targetWithTrailingWhitespace),
            StringComparison.Ordinal
        );
        Assert.NotEqual(recipe, tamperedRecipe);
        Assert.Throws<InvalidDataException>(() =>
            GridBuildRecipe.DecodeCanonical(
                Encoding.UTF8.GetBytes(tamperedRecipe)
            ));

        byte[] keyWithTrailingWhitespace = [
            .. value.Evaluation.ToCanonicalBytes(),
            (byte)'\n'
        ];
        string cell = Encoding.UTF8.GetString(value.Cell.ToCanonicalBytes());
        string tamperedCell = cell.Replace(
            Convert.ToBase64String(value.Evaluation.ToCanonicalBytes()),
            Convert.ToBase64String(keyWithTrailingWhitespace),
            StringComparison.Ordinal
        );
        Assert.NotEqual(cell, tamperedCell);
        Assert.Throws<InvalidDataException>(() =>
            RecapCellArtifact.DecodeCanonical(
                Encoding.UTF8.GetBytes(tamperedCell)
            ));
    }

    [Fact]
    public void WrongRowPriorReuseAndArtifactTamperFailClosed() {
        FormalFixture value = FormalValues();
        Assert.Throws<ArgumentException>(() => RowBuildSpec.CreateFull(
            value.Recipe,
            Coordinate(
                value.Recipe,
                value.Spec.HistoryRowId,
                value.Spec.HistorySegmentDigest
            ),
            PriorInputReference.FirstRow.Value,
            [new RowBuildAssignment.Reuse(
                value.Definition.LogicalColumnId,
                value.Cell
            )]
        ));
        EvaluationKey wrongRow = EvaluationKey.Create(
            HistoryDigest('d'),
            value.Definition.Digest,
            PriorInputReference.FirstRow.Value
        );
        Assert.Throws<ArgumentException>(() => RowBuildSpec.CreateFull(
            value.Recipe,
            Coordinate(
                value.Recipe,
                RowId('a'),
                value.Evaluation.HistorySegmentDigest
            ),
            PriorInputReference.FirstRow.Value,
            [new RowBuildAssignment.Evaluate(
                value.Definition.LogicalColumnId,
                wrongRow
            )]
        ));
        var projected = new PriorInputReference.Projection(
            value.Projection.Digest
        );
        Assert.Throws<ArgumentException>(() => RowBuildSpec.CreateFull(
            value.Recipe,
            Coordinate(
                value.Recipe,
                RowId('a'),
                value.Evaluation.HistorySegmentDigest
            ),
            projected,
            [new RowBuildAssignment.Evaluate(
                value.Definition.LogicalColumnId,
                value.Evaluation
            )]
        ));

        string cell = Encoding.UTF8.GetString(value.Cell.ToCanonicalBytes());
        Assert.Throws<InvalidDataException>(() =>
            RecapCellArtifact.DecodeCanonical(Encoding.UTF8.GetBytes(
                cell.Replace(
                    "service passage",
                    "hidden passage",
                    StringComparison.Ordinal
                ))));
        string view = Encoding.UTF8.GetString(value.View.ToCanonicalBytes());
        Assert.Throws<InvalidDataException>(() =>
            RecapRowView.DecodeCanonical(
                value.Spec,
                [value.Cell],
                Encoding.UTF8.GetBytes(view.Replace(
                    value.View.Digest.Value,
                    new string('f', 64),
                    StringComparison.Ordinal
                ))
            ));
        Assert.Throws<InvalidDataException>(() =>
            RecapRowView.DecodeCanonical(Encoding.UTF8.GetBytes(
                view.Replace(
                    value.View.Digest.Value,
                    new string('f', 64),
                    StringComparison.Ordinal
                ))));
        string fulfilled = Encoding.UTF8.GetString(
            value.Fulfilled.ToCanonicalBytes()
        );
        Assert.Throws<InvalidDataException>(() =>
            FulfilledViewKey.DecodeCanonical(Encoding.UTF8.GetBytes(
                fulfilled.Replace(
                    value.Fulfilled.RecipeDigest.Value,
                    new string('f', 63),
                    StringComparison.Ordinal
                ))));
    }

    private static FamilyDefinition Family() {
        var terminal = new FamilyToolDefinition(
            "submit_recap",
            "Submit the maintained recap.",
            new FamilyObjectInputSchema([
                new FamilyToolProperty(
                    "content",
                    new FamilyScalarInputSchema(
                        FamilyScalarType.String,
                        description: "The replacement recap."
                    ),
                    true
                )
            ])
        );
        return FamilyDefinition.Create(
            "Maintain one explicit line of inquiry.",
            [terminal],
            new FamilyOutputProtocol(
                "atelia.recap.output.v2",
                terminal.Name,
                FamilyToolChoice.Required,
                allowParallel: false
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
        new ContextHeaderBlockPath(ContextHeaderCarrier.System, column),
        new MaintainerCapabilitySpec(
            "tool-runtime-v2",
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

    private static (GridBuildRecipe, MaintainerDefinitionRevision)
        SingleColumnRecipe() {
        FamilyDefinition family = Family();
        MaintainerDefinitionRevision definition = Definition(
            "culprit",
            family.Digest
        );
        return (
            GridBuildRecipe.CreateFull(
                Timeline,
                RowId('f'),
                Target(definition)
            ),
            definition
        );
    }

    private static MaintainerDefinitionDigest ChangedDefinitionDigest(
        MaintainerDefinitionDigest value
    ) => new(value.Value[0] == 'a'
        ? "b" + value.Value[1..]
        : "a" + value.Value[1..]);

    private static HistorySegmentDescriptorDigest HistoryDigest(char value)
        => new(new string(value, 64));

    private static HistoryRowId RowId(char value)
        => new(new string(value, 64));

    private static string CanonicalSha(byte[] bytes)
        => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static FormalFixture FormalValues() {
        FamilyDefinition family = Family();
        MaintainerDefinitionRevision definition = Definition(
            "culprit",
            family.Digest
        );
        BuildTarget target = Target(definition);
        GridBuildRecipe recipe = GridBuildRecipe.CreateFull(
            Timeline,
            RowId('a'),
            target
        );
        EvaluationKey evaluation = EvaluationKey.Create(
            HistoryDigest('b'),
            definition.Digest,
            PriorInputReference.FirstRow.Value
        );
        RecapCellArtifact cell = RecapCellArtifact.Create(
            definition.LogicalColumnId,
            definition.Digest,
            evaluation,
            RecapCellOutcome.Updated,
            "X had access to the service passage.",
            definition.MaxContentUtf8Bytes
        );
        PriorInputProjection projection = PriorInputProjection.Create([
            new PriorProjectedContent(
                definition.LogicalColumnId,
                cell.ContentDigest
            )
        ]);
        RowBuildSpec spec = RowBuildSpec.CreateFull(
            recipe,
            Coordinate(recipe, RowId('a'), HistoryDigest('b')),
            PriorInputReference.FirstRow.Value,
            [new RowBuildAssignment.Evaluate(
                definition.LogicalColumnId,
                evaluation
            )]
        );
        RecapRowView view = RecapRowView.Create(spec, [cell]);
        TimelineHeadRef timelineHead = new(
            Timeline,
            new RefId(1),
            null,
            new string('c', 64),
            null,
            0,
            HistoryTimelineSelectedPath.EmptyDigest,
            generation: 0
        );
        FulfilledViewKey fulfilled = FulfilledViewKey.Create(
            new RefId(1),
            timelineHead,
            view.RowDescriptorDigest,
            recipe
        );
        return new FormalFixture(
            family,
            definition,
            target,
            recipe,
            projection,
            evaluation,
            cell,
            spec,
            view,
            timelineHead,
            fulfilled
        );
    }

    private static RowViewCoordinate Coordinate(
        GridBuildRecipe recipe,
        HistoryRowId rowId,
        HistorySegmentDescriptorDigest descriptor,
        RowViewDigest? previousView = null,
        bool? bootstrapCompleted = null
    ) => new(
        new RefId(1),
        recipe.TimelineId,
        rowId,
        descriptor,
        recipe.Digest,
        recipe.Target.Digest,
        previousView is null ? null : RowId('0'),
        previousView,
        bootstrapCompleted
            ?? (recipe.Kind == GridBuildRecipeKind.Full
                || recipe.BootstrapThroughRowId == rowId)
    );

    private sealed record FormalFixture(
        FamilyDefinition Family,
        MaintainerDefinitionRevision Definition,
        BuildTarget Target,
        GridBuildRecipe Recipe,
        PriorInputProjection Projection,
        EvaluationKey Evaluation,
        RecapCellArtifact Cell,
        RowBuildSpec Spec,
        RecapRowView View,
        TimelineHeadRef TimelineHead,
        FulfilledViewKey Fulfilled
    );
}
