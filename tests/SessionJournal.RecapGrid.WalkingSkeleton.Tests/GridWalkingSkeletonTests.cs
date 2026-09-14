using Atelia.EventJournal;
using Atelia.SessionJournal.HistoryTimeline;

namespace Atelia.SessionJournal.RecapGrid.WalkingSkeleton.Tests;

/// <summary>Cross-package rules with explicit synthetic stored IDs.
/// Store/Manager integration tests prove actual allocation and first-winner behavior.</summary>
public sealed class GridWalkingSkeletonTests {
    private static readonly TimelineId Timeline = new("00112233445566778899aabbccddeeff");

    [Fact]
    public void SuccessorKeepsIndependentPredecessorAndCurrentSlots() {
        Fixture fixture = CreateFixture();
        RowBuildSpec first = FullSpec(fixture, RowId('1'), null);
        RecapCellArtifact culprit = Cell(fixture.Culprit,
            Slot(fixture.Recipe, RowId('1'), fixture.Culprit), "Conflicting alibi.");
        RecapCellArtifact world = Cell(fixture.World,
            Slot(fixture.Recipe, RowId('1'), fixture.World), "Locked room.");
        RecapRowView view = View(first, culprit, world);
        RowBuildSpec second = FullSpec(fixture, RowId('2'), view.Id);

        Assert.Equal(view.Id, second.PreviousRowResultId);
        Assert.Equal(RowId('1'), second.PreviousHistoryRowId);
        Assert.All(second.OrderedAssignments, assignment => {
            CellSlot slot = Assert.IsType<RowBuildAssignment.Evaluate>(assignment).Slot;
            Assert.Equal(fixture.Recipe.Digest, slot.RecipeDigest);
            Assert.Equal(RowId('2'), slot.HistoryRowId);
            Assert.Equal(assignment.LogicalColumnId, slot.LogicalColumnId);
        });
        Assert.Throws<ArgumentException>(() => RowBuildSpec.CreateFull(fixture.Recipe,
            second.Coordinate, first.OrderedAssignments));
        Assert.Throws<ArgumentException>(() => View(first, world, culprit));
    }

    [Fact]
    public void SameContentAcrossRecipesHasDistinctSlots() {
        Fixture fixture = CreateFixture();
        var changed = Maintainer("culprit", fixture.Family.Digest, "A different rule.");
        var target = BuildTarget.Create([
            new BuildTargetColumn(changed.LogicalColumnId, changed.Digest),
            new BuildTargetColumn(fixture.World.LogicalColumnId, fixture.World.Digest)]);
        var otherRecipe = GridBuildRecipe.CreateFull(Timeline, RowId('f'), target);
        var first = Cell(fixture.World, Slot(fixture.Recipe, RowId('1'), fixture.World), "same text");
        var second = Cell(fixture.World, Slot(otherRecipe, RowId('1'), fixture.World), "same text");
        Assert.Equal(first.Content, second.Content);
        Assert.NotEqual(first.Slot, second.Slot);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(first.Slot, Slot(fixture.Recipe, RowId('1'), fixture.World));
    }

    [Fact]
    public void OverlayRecomputesOnlyDeclaredOrderedSubset() {
        Fixture fixture = CreateFixture();
        var changed = Maintainer("culprit", fixture.Family.Digest, "Focus on opportunity.");
        var target = BuildTarget.Create([
            new BuildTargetColumn(changed.LogicalColumnId, changed.Digest),
            new BuildTargetColumn(fixture.World.LogicalColumnId, fixture.World.Digest)]);
        var overlay = GridBuildRecipe.CreateOverlay(fixture.Recipe, RowId('f'), target,
            [changed.LogicalColumnId]);
        Assert.Equal(fixture.Recipe.Digest, overlay.BaseRecipeDigest);
        Assert.Equal([changed.LogicalColumnId], overlay.RecomputedColumns);
        Assert.Equal(fixture.World.Digest, overlay.Target.OrderedColumns[1].DefinitionDigest);
    }

    [Fact]
    public void OverlayMayRemoveReorderAddAndReuseBaseCellWithoutChangingItsSlot() {
        Fixture fixture = CreateFixture();
        var suspect = Maintainer("suspect-x", fixture.Family.Digest, "Are X's actions suspicious?");
        var target = BuildTarget.Create([
            new BuildTargetColumn(fixture.World.LogicalColumnId, fixture.World.Digest),
            new BuildTargetColumn(suspect.LogicalColumnId, suspect.Digest)]);
        var overlay = GridBuildRecipe.CreateOverlay(fixture.Recipe, RowId('2'), target,
            [suspect.LogicalColumnId]);
        var coordinate = Coordinate(overlay, RowId('2'), HistoryDigest('2'),
            new RowResultId(new string('d', 32)));
        var historical = Cell(fixture.World, Slot(fixture.Recipe, RowId('2'), fixture.World), "Service passage.");
        var current = Cell(suspect, Slot(overlay, RowId('2'), suspect), "X knew the passage.");
        RowBuildAssignment[] assignments = [
            new RowBuildAssignment.Reuse(fixture.World.LogicalColumnId, historical),
            new RowBuildAssignment.Evaluate(current.Slot)];
        var spec = RowBuildSpec.CreateOverlayBootstrap(overlay, coordinate, assignments);
        var view = View(spec, historical, current);
        Assert.Equal([fixture.World.LogicalColumnId, suspect.LogicalColumnId],
            view.OrderedCells.Select(cell => cell.LogicalColumnId));
        Assert.Equal(historical.Id, view.OrderedCells[0].CellId);
        Assert.Equal(fixture.Recipe.Digest, historical.Slot.RecipeDigest);
        Assert.NotEqual(overlay.Digest, historical.Slot.RecipeDigest);
        Assert.Throws<ArgumentException>(() => RowBuildSpec.CreateNormal(overlay, coordinate, assignments));
        Assert.Throws<ArgumentException>(() => View(spec, current, current));
        Assert.Throws<ArgumentException>(() => RowBuildSpec.CreateOverlayBootstrap(overlay, coordinate, [
            assignments[0], new RowBuildAssignment.Reuse(suspect.LogicalColumnId, current)]));
        Assert.Throws<ArgumentException>(() => RowBuildSpec.CreateOverlayBootstrap(overlay, coordinate, [
            new RowBuildAssignment.Evaluate(Slot(overlay, RowId('2'), fixture.World)), assignments[1]]));
        var wrongRow = Cell(fixture.World, Slot(fixture.Recipe, RowId('1'), fixture.World), historical.Content);
        Assert.Throws<ArgumentException>(() => RowBuildSpec.CreateOverlayBootstrap(overlay, coordinate, [
            new RowBuildAssignment.Reuse(fixture.World.LogicalColumnId, wrongRow), assignments[1]]));
    }

    private static CellSlot Slot(GridBuildRecipe recipe, HistoryRowId row,
        MaintainerDefinitionRevision definition) => new(recipe.Digest, row, definition.LogicalColumnId);

    private static RowBuildSpec FullSpec(Fixture fixture, HistoryRowId row, RowResultId? previous)
        => RowBuildSpec.CreateFull(fixture.Recipe,
            Coordinate(fixture.Recipe, row, new HistorySegmentDescriptorDigest(row.Value), previous),
            [new RowBuildAssignment.Evaluate(Slot(fixture.Recipe, row, fixture.Culprit)),
             new RowBuildAssignment.Evaluate(Slot(fixture.Recipe, row, fixture.World))]);

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
        new ContextHeaderBlockTarget(
            ContextHeaderCarrier.System,
            column,
            $"Derived context from prior history: {column}"
        ),
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
        CellSlot slot,
        string content
    ) => new(
        new CellId(Guid.NewGuid().ToString("N")),
        slot,
        definition.Digest,
        RecapCellOutcome.Updated,
        content,
        definition.MaxContentUtf8Bytes
    );

    private static RecapRowView View(
        RowBuildSpec spec,
        params RecapCellArtifact[] cells
    ) => RecapRowView.Create(
        new RowResultId(Guid.NewGuid().ToString("N")),
        spec,
        cells
    );

    private static HistorySegmentDescriptorDigest HistoryDigest(char value)
        => new(new string(value, 64));

    private static HistoryRowId RowId(char value)
        => new(new string(value, 64));

    private static RowViewCoordinate Coordinate(
        GridBuildRecipe recipe,
        HistoryRowId rowId,
        HistorySegmentDescriptorDigest descriptor,
        RowResultId? previousView
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
