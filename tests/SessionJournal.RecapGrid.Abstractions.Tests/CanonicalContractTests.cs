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
            "\"outputProtocol\":{\"protocolId\":\"atelia.recap.output.v1\","
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
            RowId('1'),
            HistoryDigest('1'),
            null,
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
            RowId('2'),
            HistoryDigest('2'),
            null,
            PriorInputReference.FirstRow.Value,
            Array.Empty<RowBuildAssignment>()
        ));
        RowBuildSpec spec = RowBuildSpec.CreateFull(
            recipe,
            RowId('2'),
            HistoryDigest('2'),
            null,
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
            "45ca17f0b170c6a6197f243d33bc974c86b66d249f501b67597b68b3cfa14c49\n"
            + "8dbec2b10510ca49bf56cd187c89e19b30435fc359654129d258fe1462d6529a\n"
            + "5e00f9857fbb96ac9bda6d6902c35bb410c64a075d4c754dce60878451bcf000\n"
            + "b559feb44dc2a73378299b02f9cd9cdc4084fd7067169ed3ed0f27a450196ca1\n"
            + "7b622b3e5ba946841d45722375ae7a5ae0f15b75b3f28b4ccb7c74ba642cb753\n"
            + "c3e41a7bcaa525d8fca556ff17e369827b8984155f67cbc129e30a105a975176\n"
            + "13abe0196f72da491691abb9bf9f1747c7e57ee5f0f2d65e5988fd738ac38344\n"
            + "182f802b3819dd13f57a417c1af09b7c71ec479f91409481d850d02a99d5e423\n"
            + "1329ad5b3ec48a2f21bf1119a121170791f3fbcd98fc4fde566726064a3ed8b5",
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
            value.View.ToCanonicalBytes(),
            value.Fulfilled.ToCanonicalBytes()
        }, bytes => Assert.StartsWith(
            "{\"schemaVersion\":1,",
            Encoding.UTF8.GetString(bytes),
            StringComparison.Ordinal
        ));
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
            "b498db80d6f45666697be36d6331b8a117aaa1dbeddd1c87dd7383a2fd6db76e\n"
            + "ae56b23f120260ce9b44dfb01e983d4119f9d7799fc6676a1239ce6b07f91e66\n"
            + "80a6b63fc6b1517604582118fdae7542f3dd55a023fcf22ae59ae9077fe662d6\n"
            + "f1b8a247db94f9b515db6eeafaca486f8f161221cbfa79179d8372abc17d387e\n"
            + "436ac01f8031b636eeaf77b02aa1fd4df81f4bb98decae7cf7b01907adced7a2\n"
            + "da233a6fd81d80d761d66126b11d7d66e4113c84611262c6c38e9b6367308fde\n"
            + "b8d0dd60d9913b6f57477f2aa02a0c6a5a5c75fd87bd6a397dec3112cf268688\n"
            + "550bb8e3c28108621cee54dfbd40bc61baf330d04d8d1d35a0b2dbd2cd49905e\n"
            + "c08fc09804a0b40dd63e54f88d44c1bf64cd0431304428e440fa96c10ef776ff",
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
            default,
            value.Evaluation.HistorySegmentDigest,
            null,
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
                "submit",
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
                "submit",
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
            value.Spec.HistoryRowId,
            value.Spec.HistorySegmentDigest,
            null,
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
            RowId('a'),
            value.Evaluation.HistorySegmentDigest,
            null,
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
            RowId('a'),
            value.Evaluation.HistorySegmentDigest,
            null,
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
                "atelia.recap.output.v1",
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
            "tool-runtime-v1",
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
            RowId('a'),
            HistoryDigest('b'),
            null,
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
