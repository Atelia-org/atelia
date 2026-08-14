using Atelia.EventJournal;
using Atelia.SessionJournal;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid;

namespace Atelia.SessionJournal.RecapGrid.WalkingSkeleton.Tests;

/// <summary>
/// Cross-package walking skeleton over the formal WP-01/WP-02 identities.
/// This fixture intentionally owns no alternate Grid shape or hasher.
/// </summary>
public sealed class GridWalkingSkeletonTests {
    private static readonly TimelineId Timeline = new(
        "00112233445566778899aabbccddeeff"
    );

    [Fact]
    public void FirstAndSuccessorRowsUseFrozenPriorProjection() {
        Fixture fixture = CreateFixture();
        HistorySegmentDescriptorDigest firstHistory = HistoryDigest('1');
        EvaluationKey firstCulprit = EvaluationKey.Create(
            firstHistory,
            fixture.Culprit.Digest,
            PriorInputReference.FirstRow.Value
        );
        EvaluationKey firstWorld = EvaluationKey.Create(
            firstHistory,
            fixture.World.Digest,
            PriorInputReference.FirstRow.Value
        );
        RowBuildSpec firstSpec = RowBuildSpec.CreateFull(
            fixture.Recipe,
            Coordinate(fixture.Recipe, RowId('1'), firstHistory, null),
            PriorInputReference.FirstRow.Value,
            [
                new RowBuildAssignment.Evaluate(
                    fixture.Culprit.LogicalColumnId,
                    firstCulprit
                ),
                new RowBuildAssignment.Evaluate(
                    fixture.World.LogicalColumnId,
                    firstWorld
                )
            ]
        );
        RecapCellArtifact culpritCell = Cell(
            fixture.Culprit,
            firstCulprit,
            "The witness account conflicts with X's alibi."
        );
        RecapCellArtifact worldCell = Cell(
            fixture.World,
            firstWorld,
            "The locked room requires access to the service passage."
        );
        RecapRowView firstView = View(
            firstSpec,
            culpritCell,
            worldCell
        );
        PriorInputProjection prior = Projection(
            firstView,
            culpritCell,
            worldCell
        );

        HistorySegmentDescriptorDigest secondHistory = HistoryDigest('2');
        var priorReference = new PriorInputReference.Projection(prior.Digest);
        EvaluationKey secondCulprit = EvaluationKey.Create(
            secondHistory,
            fixture.Culprit.Digest,
            priorReference
        );
        EvaluationKey secondWorld = EvaluationKey.Create(
            secondHistory,
            fixture.World.Digest,
            priorReference
        );
        RowBuildSpec secondSpec = RowBuildSpec.CreateFull(
            fixture.Recipe,
            Coordinate(
                fixture.Recipe,
                RowId('2'),
                secondHistory,
                firstView.Digest
            ),
            priorReference,
            [
                new RowBuildAssignment.Evaluate(
                    fixture.Culprit.LogicalColumnId,
                    secondCulprit
                ),
                new RowBuildAssignment.Evaluate(
                    fixture.World.LogicalColumnId,
                    secondWorld
                )
            ]
        );

        Assert.Equal(firstView.Digest, secondSpec.PreviousViewDigest);
        Assert.All(
            secondSpec.OrderedAssignments,
            assignment => Assert.Equal(
                prior.Digest,
                Assert.IsType<PriorInputReference.Projection>(
                    Assert.IsType<RowBuildAssignment.Evaluate>(assignment)
                        .EvaluationKey.PriorInput
                ).Digest
            )
        );
    }

    [Fact]
    public void ContentEquivalentViewsShareProjectionIdentity() {
        Fixture fixture = CreateFixture();
        ContentDigest culprit = ContentDigestFor(
            "same culprit conclusion"
        );
        ContentDigest world = ContentDigestFor("same world conclusion");
        PriorInputProjection first = PriorInputProjection.Create(
            [
                new PriorProjectedContent(
                    fixture.Culprit.LogicalColumnId,
                    culprit
                ),
                new PriorProjectedContent(
                    fixture.World.LogicalColumnId,
                    world
                )
            ]
        );
        PriorInputProjection second = PriorInputProjection.Create(
            [
                new PriorProjectedContent(
                    fixture.Culprit.LogicalColumnId,
                    culprit
                ),
                new PriorProjectedContent(
                    fixture.World.LogicalColumnId,
                    world
                )
            ]
        );

        Assert.Equal(first.Digest, second.Digest);
        Assert.Equal(first.ToCanonicalBytes(), second.ToCanonicalBytes());
    }

    [Fact]
    public void OverlayRecomputesOnlyDeclaredOrderedSubset() {
        Fixture fixture = CreateFixture();
        MaintainerDefinitionRevision changed = Maintainer(
            "culprit",
            fixture.Family.Digest,
            "Focus on X's opportunity and means."
        );
        BuildTarget target = BuildTarget.Create([
            new BuildTargetColumn(
                changed.LogicalColumnId,
                changed.Digest
            ),
            new BuildTargetColumn(
                fixture.World.LogicalColumnId,
                fixture.World.Digest
            )
        ]);

        GridBuildRecipe overlay = GridBuildRecipe.CreateOverlay(
            fixture.Recipe,
            RowId('f'),
            target,
            [changed.LogicalColumnId]
        );

        Assert.Equal(fixture.Recipe.Digest, overlay.BaseRecipeDigest);
        Assert.Equal(
            [changed.LogicalColumnId],
            overlay.RecomputedColumns
        );
        Assert.Equal(
            fixture.World.Digest,
            overlay.Target.OrderedColumns[1].DefinitionDigest
        );
    }

    [Fact]
    public void OverlayMayRemoveReorderAddAndReuseHistoricalCell() {
        Fixture fixture = CreateFixture();
        MaintainerDefinitionRevision suspect = Maintainer(
            "suspect-x",
            fixture.Family.Digest,
            "Are X's actions suspicious?"
        );
        BuildTarget target = BuildTarget.Create([
            new BuildTargetColumn(
                fixture.World.LogicalColumnId,
                fixture.World.Digest
            ),
            new BuildTargetColumn(suspect.LogicalColumnId, suspect.Digest)
        ]);
        GridBuildRecipe overlay = GridBuildRecipe.CreateOverlay(
            fixture.Recipe,
            RowId('2'),
            target,
            [suspect.LogicalColumnId]
        );

        EvaluationKey historicalWorld = EvaluationKey.Create(
            HistoryDigest('2'),
            fixture.World.Digest,
            PriorInputReference.FirstRow.Value
        );
        RecapCellArtifact historicalCell = Cell(
            fixture.World,
            historicalWorld,
            "The service passage remains the only access route."
        );
        PriorInputProjection prior = PriorInputProjection.Create([
            new PriorProjectedContent(
                fixture.World.LogicalColumnId,
                historicalCell.ContentDigest
            )
        ]);
        var currentPrior = new PriorInputReference.Projection(prior.Digest);
        EvaluationKey currentSuspect = EvaluationKey.Create(
            HistoryDigest('2'),
            suspect.Digest,
            currentPrior
        );
        RowBuildSpec spec = RowBuildSpec.CreateOverlayBootstrap(
            overlay,
            Coordinate(
                overlay,
                RowId('2'),
                HistoryDigest('2'),
                new RowViewDigest(new string('d', 64))
            ),
            currentPrior,
            [
                new RowBuildAssignment.Reuse(
                    fixture.World.LogicalColumnId,
                    historicalCell
                ),
                new RowBuildAssignment.Evaluate(
                    suspect.LogicalColumnId,
                    currentSuspect
                )
            ]
        );
        RecapCellArtifact suspectCell = Cell(
            suspect,
            currentSuspect,
            "X knew about the passage before it was disclosed."
        );
        EvaluationKey currentWorld = EvaluationKey.Create(
            HistoryDigest('2'),
            fixture.World.Digest,
            currentPrior
        );
        Assert.Throws<ArgumentException>(() =>
            RowBuildSpec.CreateOverlayBootstrap(
                overlay,
                Coordinate(
                    overlay,
                    RowId('2'),
                    HistoryDigest('2'),
                    new RowViewDigest(new string('d', 64))
                ),
                currentPrior,
                [
                    new RowBuildAssignment.Reuse(
                        fixture.World.LogicalColumnId,
                        historicalCell
                    ),
                    new RowBuildAssignment.Reuse(
                        suspect.LogicalColumnId,
                        suspectCell
                    )
                ]
            ));
        Assert.Throws<ArgumentException>(() =>
            RowBuildSpec.CreateOverlayBootstrap(
                overlay,
                Coordinate(
                    overlay,
                    RowId('2'),
                    HistoryDigest('2'),
                    new RowViewDigest(new string('d', 64))
                ),
                currentPrior,
                [
                    new RowBuildAssignment.Evaluate(
                        fixture.World.LogicalColumnId,
                        currentWorld
                    ),
                    new RowBuildAssignment.Evaluate(
                        suspect.LogicalColumnId,
                        currentSuspect
                    )
                ]
            ));
        Assert.Throws<ArgumentException>(() => RowBuildSpec.CreateNormal(
            overlay,
            Coordinate(
                overlay,
                RowId('2'),
                HistoryDigest('2'),
                new RowViewDigest(new string('d', 64))
            ),
            currentPrior,
            [
                new RowBuildAssignment.Reuse(
                    fixture.World.LogicalColumnId,
                    historicalCell
                ),
                new RowBuildAssignment.Evaluate(
                    suspect.LogicalColumnId,
                    currentSuspect
                )
            ]
        ));
        RecapRowView view = RecapRowView.Create(
            spec,
            [historicalCell, suspectCell]
        );

        Assert.Equal(
            [fixture.World.LogicalColumnId, suspect.LogicalColumnId],
            view.OrderedCells.Select(static cell => cell.LogicalColumnId)
        );
        Assert.Throws<ArgumentException>(() => RecapRowView.Create(
            spec,
            [suspectCell, suspectCell]
        ));
        EvaluationKey wrongRowKey = EvaluationKey.Create(
            HistoryDigest('1'),
            fixture.World.Digest,
            PriorInputReference.FirstRow.Value
        );
        RecapCellArtifact wrongRowCell = Cell(
            fixture.World,
            wrongRowKey,
            historicalCell.Content
        );
        Assert.Throws<ArgumentException>(() => RowBuildSpec.CreateOverlayBootstrap(
            overlay,
            Coordinate(
                overlay,
                RowId('2'),
                HistoryDigest('2'),
                new RowViewDigest(new string('d', 64))
            ),
            currentPrior,
            [
                new RowBuildAssignment.Reuse(
                    fixture.World.LogicalColumnId,
                    wrongRowCell
                ),
                new RowBuildAssignment.Evaluate(
                    suspect.LogicalColumnId,
                    currentSuspect
                )
            ]
        ));
    }

    private static Fixture CreateFixture() {
        FamilyDefinition family = FamilyDefinition.Create(
            "Maintain an evidence-backed line of inquiry.",
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
        MaintainerDefinitionRevision culprit = Maintainer(
            "culprit",
            family.Digest,
            "Who could be the culprit?"
        );
        MaintainerDefinitionRevision world = Maintainer(
            "world",
            family.Digest,
            "Track stable facts about the case."
        );
        BuildTarget target = BuildTarget.Create([
            new BuildTargetColumn(culprit.LogicalColumnId, culprit.Digest),
            new BuildTargetColumn(world.LogicalColumnId, world.Digest)
        ]);
        return new Fixture(
            family,
            culprit,
            world,
            GridBuildRecipe.CreateFull(Timeline, RowId('f'), target)
        );
    }

    private static MaintainerDefinitionRevision Maintainer(
        string column,
        FamilyDefinitionDigest familyDigest,
        string prompt
    ) => MaintainerDefinitionRevision.Create(
        new LogicalColumnId(column),
        familyDigest,
        new ContextHeaderBlockPath(ContextHeaderCarrier.System, column),
        new MaintainerCapabilitySpec(
            "text-runtime-v3",
            MaintainerReadableScope
                .FullPriorBuildTargetAndCurrentHistorySegmentV1
        ),
        new MaintainerDeclarativeSpec(column, prompt),
        maxContentUtf8Bytes: 16 * 1024
    );

    private static RecapCellArtifact Cell(
        MaintainerDefinitionRevision definition,
        EvaluationKey key,
        string content
    ) => RecapCellArtifact.Create(
        definition.LogicalColumnId,
        definition.Digest,
        key,
        RecapCellOutcome.Updated,
        content,
        definition.MaxContentUtf8Bytes
    );

    private static RecapRowView View(
        RowBuildSpec spec,
        params RecapCellArtifact[] cells
    ) => RecapRowView.Create(
        spec,
        cells
    );

    private static PriorInputProjection Projection(
        RecapRowView view,
        params RecapCellArtifact[] cells
    ) => PriorInputProjection.Create(
        cells.Select(static cell => new PriorProjectedContent(
            cell.LogicalColumnId,
            cell.ContentDigest
        ))
    );

    private static ContentDigest ContentDigestFor(string content) {
        FamilyDefinition family = CreateFixture().Family;
        MaintainerDefinitionRevision definition = Maintainer(
            "temp",
            family.Digest,
            "temp"
        );
        EvaluationKey key = EvaluationKey.Create(
            HistoryDigest('f'),
            definition.Digest,
            PriorInputReference.FirstRow.Value
        );
        return Cell(definition, key, content).ContentDigest;
    }

    private static HistorySegmentDescriptorDigest HistoryDigest(char value)
        => new(new string(value, 64));

    private static HistoryRowId RowId(char value)
        => new(new string(value, 64));

    private static RowViewCoordinate Coordinate(
        GridBuildRecipe recipe,
        HistoryRowId rowId,
        HistorySegmentDescriptorDigest descriptor,
        RowViewDigest? previousView
    ) => new(
        new RefId(1),
        recipe.TimelineId,
        rowId,
        descriptor,
        recipe.Digest,
        recipe.Target.Digest,
        previousView is null ? null : RowId('1'),
        previousView,
        recipe.Kind == GridBuildRecipeKind.Full
            || recipe.BootstrapThroughRowId == rowId
    );

    private sealed record Fixture(
        FamilyDefinition Family,
        MaintainerDefinitionRevision Culprit,
        MaintainerDefinitionRevision World,
        GridBuildRecipe Recipe
    );
}
