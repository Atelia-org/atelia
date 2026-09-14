using System.Text;
using Atelia.EventJournal;
using Atelia.SessionJournal.HistoryTimeline;
using Xunit;

namespace Atelia.SessionJournal.RecapGrid.Abstractions.Tests;

public sealed class StoredResultContractTests {
    private static readonly TimelineId Timeline = new(new string('1', 32));
    private static readonly MaintainerDefinitionDigest Definition = new(new string('d', 64));

    [Fact]
    public void SlotUsesRecipeRowAndColumnValueEquality() {
        GridBuildRecipe recipe = Recipe("alpha");
        CellSlot slot = Slot(recipe, 'a', "alpha");
        Assert.Equal(slot, Slot(recipe, 'a', "alpha"));
        Assert.NotEqual(slot, Slot(recipe, 'b', "alpha"));
        Assert.NotEqual(slot, Slot(recipe, 'a', "beta"));
        GridBuildRecipe otherRecipe = GridBuildRecipe.CreateFull(Timeline, Row('b'), recipe.Target);
        Assert.NotEqual(slot, Slot(otherRecipe, 'a', "alpha"));
        Assert.Single(new HashSet<CellSlot> { slot, Slot(recipe, 'a', "alpha") });
    }

    [Theory]
    [InlineData(31, 'a')]
    [InlineData(33, 'a')]
    [InlineData(64, 'a')]
    [InlineData(32, 'A')]
    [InlineData(32, 'g')]
    public void StoredIdsRejectInvalidShape(int length, char character) {
        string value = new(character, length);
        Assert.Throws<ArgumentException>(() => new CellId(value));
        Assert.Throws<ArgumentException>(() => new RowResultId(value));
    }

    [Fact]
    public void ExplicitIdDoesNotDependOnContentOrOutcome() {
        CellSlot slot = Slot(Recipe("alpha"), 'a', "alpha");
        RecapCellArtifact first = Cell('1', slot, "same");
        RecapCellArtifact second = Cell('2', slot, "same");
        RecapCellArtifact unchanged = new(first.Id, slot, Definition, RecapCellOutcome.KeepUnchanged, "changed");
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(first.Id, unchanged.Id);
        Assert.Equal(RecapCellOutcome.KeepUnchanged, unchanged.Outcome);
        Assert.Equal("changed", unchanged.Content);
        // These are representations; Store publication owns first-winner semantics.
    }

    [Fact]
    public void FactoriesRejectDefaultIdsSlotsAndDefinitions() {
        GridBuildRecipe recipe = Recipe("alpha");
        CellSlot slot = Slot(recipe, 'a', "alpha");
        Assert.Throws<ArgumentException>(() => new CellSlot(default, Row('a'), new("alpha")));
        Assert.Throws<ArgumentException>(() => new CellSlot(recipe.Digest, default, new("alpha")));
        Assert.Throws<ArgumentNullException>(() => new CellSlot(recipe.Digest, Row('a'), default));
        Assert.Throws<ArgumentException>(() => new RecapCellArtifact(default, slot, Definition, RecapCellOutcome.Updated, ""));
        Assert.Throws<ArgumentNullException>(() => new RecapCellArtifact(new(new string('1', 32)), null!, Definition, RecapCellOutcome.Updated, ""));
        Assert.Throws<ArgumentException>(() => new RecapCellArtifact(new(new string('1', 32)), slot, default, RecapCellOutcome.Updated, ""));
        Assert.Throws<ArgumentException>(() => RecapRowView.Create(default, Spec(recipe), [Cell('1', slot)]));
    }

    [Fact]
    public void ContentLimitsCountUtf8BytesAndRejectInvalidUtf16OrOutcome() {
        CellSlot slot = Slot(Recipe("alpha"), 'a', "alpha");
        CellId id = new(new string('1', 32));
        Assert.Equal("中文", new RecapCellArtifact(id, slot, Definition, RecapCellOutcome.Updated, "中文", 6).Content);
        Assert.Equal("", new RecapCellArtifact(id, slot, Definition, RecapCellOutcome.KeepUnchanged, "", 1).Content);
        Assert.Throws<ArgumentOutOfRangeException>(() => new RecapCellArtifact(id, slot, Definition, RecapCellOutcome.Updated, "中文", 5));
        Assert.Throws<ArgumentException>(() => new RecapCellArtifact(id, slot, Definition, RecapCellOutcome.Updated, "\ud800"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RecapCellArtifact(id, slot, Definition, (RecapCellOutcome)99, ""));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RecapCellArtifact(id, slot, Definition, RecapCellOutcome.Updated, "", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RecapCellArtifact(id, slot, Definition, RecapCellOutcome.Updated, "", RecapGridLimits.MaximumContentUtf8Bytes + 1));
    }

    [Fact]
    public void RowRequiresExactOrderedMembershipAndDefinitions() {
        GridBuildRecipe recipe = Recipe("alpha", "beta");
        RecapCellArtifact alpha = Cell('1', Slot(recipe, 'a', "alpha"));
        RecapCellArtifact beta = Cell('2', Slot(recipe, 'a', "beta"));
        RowBuildSpec spec = Spec(recipe);
        RowResultId id = new(new string('3', 32));
        RecapRowView view = RecapRowView.Create(id, spec, [alpha, beta]);
        Assert.Equal(id, view.Id);
        Assert.Equal(new[] { alpha.Id, beta.Id }, view.OrderedCells.Select(static member => member.CellId));
        Assert.Throws<ArgumentException>(() => RecapRowView.Create(id, spec, [alpha]));
        Assert.Throws<ArgumentException>(() => RecapRowView.Create(id, spec, [beta, alpha]));
        Assert.Throws<ArgumentException>(() => RecapRowView.Create(id, spec, [alpha, alpha]));
        Assert.Throws<ArgumentException>(() => RecapRowView.Create(id, spec, [alpha, null!]));
        RecapCellArtifact wrongDefinition = new(beta.Id, beta.Slot, new(new string('e', 64)), beta.Outcome, beta.Content);
        Assert.Throws<ArgumentException>(() => RecapRowView.Create(id, spec, [alpha, wrongDefinition]));
        Assert.Throws<ArgumentException>(() => RowBuildSpec.CreateFull(recipe, Coordinate(recipe), [new RowBuildAssignment.Evaluate(beta.Slot), new RowBuildAssignment.Evaluate(alpha.Slot)]));
    }

    [Fact]
    public void EvaluationRejectsAnotherRowOrRecipeAndFullRecipeRejectsReuse() {
        GridBuildRecipe recipe = Recipe("alpha");
        GridBuildRecipe other = GridBuildRecipe.CreateFull(Timeline, Row('b'), recipe.Target);
        Assert.Throws<ArgumentException>(() => RowBuildSpec.CreateFull(recipe, Coordinate(recipe), [new RowBuildAssignment.Evaluate(Slot(recipe, 'b', "alpha"))]));
        Assert.Throws<ArgumentException>(() => RowBuildSpec.CreateFull(recipe, Coordinate(recipe), [new RowBuildAssignment.Evaluate(Slot(other, 'a', "alpha"))]));
        Assert.Throws<ArgumentException>(() => RowBuildSpec.CreateFull(recipe, Coordinate(recipe), [new RowBuildAssignment.Reuse(new("alpha"), Cell('1', Slot(recipe, 'a', "alpha")))]));
    }

    [Fact]
    public void OverlayReusesExactBaseCellWithoutChangingItsSourceSlot() {
        GridBuildRecipe basis = Recipe("alpha", "beta");
        GridBuildRecipe overlay = GridBuildRecipe.CreateOverlay(basis, Row('a'), basis.Target, [new LogicalColumnId("alpha")]);
        RecapCellArtifact reused = Cell('1', Slot(basis, 'a', "beta"));
        RecapCellArtifact evaluated = Cell('2', Slot(overlay, 'a', "alpha"));
        RowBuildSpec spec = RowBuildSpec.CreateOverlayBootstrap(overlay, Coordinate(overlay), [
            new RowBuildAssignment.Evaluate(evaluated.Slot),
            new RowBuildAssignment.Reuse(new("beta"), reused)
        ]);
        RecapRowView result = RecapRowView.Create(new(new string('3', 32)), spec, [evaluated, reused]);
        Assert.Equal(reused.Id, result.OrderedCells[1].CellId);
        Assert.Equal(basis.Digest, reused.Slot.RecipeDigest);
        RecapCellArtifact relabeled = Cell('1', Slot(overlay, 'a', "beta"));
        Assert.Throws<ArgumentException>(() => RecapRowView.Create(result.Id, spec, [evaluated, relabeled]));
        Assert.Throws<ArgumentException>(() => RowBuildSpec.CreateOverlayBootstrap(overlay, Coordinate(overlay), [
            new RowBuildAssignment.Evaluate(evaluated.Slot),
            new RowBuildAssignment.Evaluate(relabeled.Slot)
        ]));
        Assert.Throws<ArgumentException>(() => RowBuildSpec.CreateOverlayBootstrap(overlay, Coordinate(overlay), [
            new RowBuildAssignment.Evaluate(evaluated.Slot),
            new RowBuildAssignment.Reuse(new("beta"), Cell('1', Slot(basis, 'b', "beta")))
        ]));
    }

    [Fact]
    public void EmptyRowsStillHaveExplicitIdentityAndPredecessor() {
        GridBuildRecipe recipe = Recipe();
        RowResultId firstId = new(new string('1', 32));
        RecapRowView first = RecapRowView.Create(firstId, Spec(recipe), []);
        RowViewCoordinate coordinate = Coordinate(recipe, 'b', Row('a'), firstId);
        RecapRowView second = RecapRowView.Create(new(new string('2', 32)), RowBuildSpec.CreateFull(recipe, coordinate, []), []);
        Assert.Empty(first.OrderedCells);
        Assert.Empty(second.OrderedCells);
        Assert.Null(first.PreviousRowResultId);
        Assert.Equal(first.Id, second.PreviousRowResultId);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Throws<ArgumentException>(() => Coordinate(recipe, 'b', Row('a'), null));
        Assert.Throws<ArgumentException>(() => Coordinate(recipe, 'b', null, firstId));
        Assert.Throws<ArgumentException>(() => Coordinate(recipe, 'a', Row('a'), firstId));
        Assert.Throws<ArgumentException>(() => RecapRowView.Create(firstId, RowBuildSpec.CreateFull(recipe, coordinate, []), []));
    }

    [Fact]
    public void FulfillmentRemainsBoundToExactTimelineSelectionAndRecipe() {
        GridBuildRecipe recipe = Recipe("alpha");
        TimelineHeadRef head = new(Timeline, new RefId(1), null, new string('c', 64), null, 0,
            HistoryTimelineSelectedPath.EmptyDigest, generation: 0);
        HistorySegmentDescriptorDigest descriptor = new(new string('b', 64));
        FulfilledViewKey key = FulfilledViewKey.Create(new RefId(1), head, descriptor, recipe);
        Assert.Equal(key, FulfilledViewKey.Create(new RefId(1), head, descriptor, recipe));
        Assert.NotEqual(key, FulfilledViewKey.Create(new RefId(1), head, new(new string('d', 64)), recipe));
        GridBuildRecipe other = GridBuildRecipe.CreateFull(Timeline, Row('b'), recipe.Target);
        Assert.NotEqual(key, FulfilledViewKey.Create(new RefId(1), head, descriptor, other));
        Assert.Throws<ArgumentException>(() => FulfilledViewKey.Create(new RefId(2), head, descriptor, recipe));
        GridBuildRecipe foreignTimeline = GridBuildRecipe.CreateFull(new(new string('2', 32)), Row('a'), recipe.Target);
        Assert.Throws<ArgumentException>(() => FulfilledViewKey.Create(new RefId(1), head, descriptor, foreignTimeline));
        Assert.Throws<ArgumentException>(() => FulfilledViewKey.Create(default, head, descriptor, recipe));
    }

    [Theory]
    [InlineData('\u00a0')]
    [InlineData('\u2028')]
    [InlineData('\\')]
    [InlineData('"')]
    public void MaximumColumnsAndIdentifierUtf8LimitsApplyWithoutProjectionSerialization(char character) {
        string[] names = Enumerable.Range(0, RecapGridLimits.MaximumColumnCount).Select(index =>
            index.ToString("D3") + "a" + new string(character, 120 / Encoding.UTF8.GetByteCount(character.ToString())) + "tail").ToArray();
        Assert.All(names, name => Assert.Equal(128, Encoding.UTF8.GetByteCount(name)));
        GridBuildRecipe recipe = Recipe(names);
        RowBuildSpec spec = Spec(recipe);
        RecapCellArtifact[] cells = names.Select((name, index) => new RecapCellArtifact(
            new CellId(index.ToString("x32")), Slot(recipe, 'a', name), Definition, RecapCellOutcome.Updated, "content")).ToArray();
        RecapRowView view = RecapRowView.Create(new(new string('1', 32)), spec, cells);
        Assert.Equal(128, view.OrderedCells.Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => new LogicalColumnId(names[0] + "a"));
        Assert.Throws<ArgumentOutOfRangeException>(() => RowBuildSpec.CreateFull(recipe, Coordinate(recipe),
            spec.OrderedAssignments.Concat([spec.OrderedAssignments[0]])));
    }

    private static GridBuildRecipe Recipe(params string[] names) => GridBuildRecipe.CreateFull(Timeline, Row('a'),
        BuildTarget.Create(names.Select(static name => new BuildTargetColumn(new LogicalColumnId(name), Definition))));
    private static CellSlot Slot(GridBuildRecipe recipe, char row, string column) => new(recipe.Digest, Row(row), new(column));
    private static HistoryRowId Row(char value) => new(new string(value, 64));
    private static RecapCellArtifact Cell(char id, CellSlot slot, string content = "content") =>
        new(new CellId(new string(id, 32)), slot, Definition, RecapCellOutcome.Updated, content);
    private static RowBuildSpec Spec(GridBuildRecipe recipe) => RowBuildSpec.CreateFull(recipe, Coordinate(recipe),
        recipe.Target.OrderedColumns.Select(column => new RowBuildAssignment.Evaluate(new CellSlot(recipe.Digest, Row('a'), column.LogicalColumnId))));
    private static RowViewCoordinate Coordinate(GridBuildRecipe recipe, char row = 'a', HistoryRowId? previousRow = null, RowResultId? previousResult = null) =>
        new(new RefId(1), Timeline, Row(row), new(new string('b', 64)), recipe.Digest, recipe.Target.Digest, previousRow, previousResult, true);
}
