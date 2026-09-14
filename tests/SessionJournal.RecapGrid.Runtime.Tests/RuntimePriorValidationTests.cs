using Atelia.SessionJournal.RecapGrid.Manager;
using Xunit;

namespace Atelia.SessionJournal.RecapGrid.Runtime.Tests;

public sealed class RuntimePriorValidationTests {
    [Theory]
    [InlineData("view", "PriorViewMissing")]
    [InlineData("count", "PriorViewMissing")]
    [InlineData("column", "PriorViewMismatch")]
    [InlineData("definition", "PriorViewMismatch")]
    [InlineData("cell", "PriorViewMismatch")]
    public async Task InvalidPriorMembersRejectBeforeAnyProviderCall(string mutation, string code) {
        FrozenRowBatch valid = RuntimeTestFixture.BatchWithPrior();
        RecapCellArtifact cell = Assert.Single(valid.PreviousCells);
        RecapRowView? view = mutation == "view" ? null : valid.PreviousView;
        IReadOnlyList<RecapCellArtifact> cells = mutation switch {
            "count" => [],
            "column" => [ReplaceCell(cell, column: new LogicalColumnId("case.other"))],
            "definition" => [ReplaceCell(cell, definition: new MaintainerDefinitionDigest(new string('e', 64)))],
            "cell" => [ReplaceCell(cell, content: "different content")],
            _ => valid.PreviousCells
        };
        await AssertRejected(Copy(valid, view, cells), code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PriorMemberOrderOrDuplicateRejectsBeforeDispatch(bool duplicate) {
        FrozenRowBatch projected = RuntimeTestFixture.BatchWithPrior(columnCount: 2);
        IReadOnlyList<RecapCellArtifact> cells = projected.PreviousCells;
        RecapCellArtifact[] invalid = duplicate ? [cells[0], cells[0]] : [cells[1], cells[0]];
        await AssertRejected(Copy(projected, projected.PreviousView, invalid), "PriorViewMismatch");
    }

    [Fact]
    public async Task ActualPreviousViewMustMatchIndependentSpecRowId() {
        FrozenRowBatch valid = RuntimeTestFixture.BatchWithPrior();
        // Preserve actual view/cells and all work. Only the independently frozen
        // expected predecessor changes, so member checks cannot explain rejection.
        RowViewCoordinate original = valid.Spec.Coordinate;
        var coordinate = new RowViewCoordinate(original.RefId, original.TimelineId,
            original.HistoryRowId, original.HistorySegmentDigest, original.RecipeDigest,
            original.TargetDigest, original.PreviousHistoryRowId,
            new RowResultId(Guid.NewGuid().ToString("N")), original.BootstrapCompleted);
        RowBuildSpec spec = RowBuildSpec.CreateNormal(valid.Recipe, coordinate,
            valid.Spec.OrderedAssignments);
        await AssertRejected(Copy(valid, valid.PreviousView, valid.PreviousCells, spec),
            "PriorSourceMismatch");
    }

    [Theory]
    [InlineData("recipe")]
    [InlineData("history")]
    [InlineData("column")]
    public async Task WorkWithDifferentSlotCannotBypassSpecAuthority(string axis) {
        FrozenRowBatch valid = RuntimeTestFixture.BatchWithPrior();
        FrozenRecapCellWork original = Assert.Single(valid.OrderedMissingWork);
        var changedSlot = new CellSlot(
            axis == "recipe" ? new GridBuildRecipeDigest(new string('f', 64)) : original.Slot.RecipeDigest,
            axis == "history" ? valid.PreviousView!.HistoryRowId : original.Slot.HistoryRowId,
            axis == "column" ? new LogicalColumnId("case.other") : original.LogicalColumnId);
        FrozenRecapCellWork changed = new(original.Ordinal, changedSlot,
            original.Definition, original.Family);
        await AssertRejected(Copy(valid, valid.PreviousView, valid.PreviousCells, work: [changed]),
            "WorkAuthorityMismatch");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FirstRowRejectsPreviousViewOrCells(bool includeView) {
        FrozenRowBatch first = RuntimeTestFixture.Batch();
        FrozenRowBatch previous = RuntimeTestFixture.BatchWithPrior();
        await AssertRejected(Copy(first, includeView ? previous.PreviousView : null,
            includeView ? [] : previous.PreviousCells), "FirstRowPriorInvalid");
    }

    private static FrozenRowBatch Copy(FrozenRowBatch original, RecapRowView? previous,
        IReadOnlyList<RecapCellArtifact> cells, RowBuildSpec? spec = null,
        IReadOnlyList<FrozenRecapCellWork>? work = null) => new(
        original.TimelineHead, original.ControlHead, original.StoreIdentity, original.Recipe,
        original.HistorySegment, spec ?? original.Spec, previous, cells, work ?? original.OrderedMissingWork);

    private static RecapCellArtifact ReplaceCell(RecapCellArtifact original,
        LogicalColumnId? column = null, MaintainerDefinitionDigest? definition = null, string? content = null) {
        MaintainerDefinitionDigest target = definition ?? original.DefinitionDigest;
        var slot = new CellSlot(original.Slot.RecipeDigest, original.Slot.HistoryRowId,
            column ?? original.LogicalColumnId);
        return new RecapCellArtifact(new CellId(Guid.NewGuid().ToString("N")),
            slot, target, original.Outcome, content ?? original.Content, 16 * 1024);
    }

    private static async Task AssertRejected(FrozenRowBatch batch, string code) {
        var invoker = new ScriptedInvoker((_, _) => throw new InvalidOperationException("must not dispatch"));
        RecapCompletionRoute route = RuntimeTestFixture.Route(batch, invoker);
        var resolver = new ScriptedResolver(_ => new RecapCompletionRouteResolution.Bound(route));
        using var runtime = new RecapCompletionRuntime(resolver);
        var rejected = Assert.IsType<RecapCellBatchExecutionResult.RejectedBeforeDispatch>(
            await runtime.ExecuteAsync(batch, default));
        Assert.Equal(code, rejected.Code);
        Assert.Equal(0, resolver.CallCount);
        Assert.Equal(0, invoker.CallCount);
    }
}
