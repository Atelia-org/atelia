using Atelia.EventJournal;
using Atelia.SessionJournal.HistoryTimeline;
using Xunit;

namespace Atelia.SessionJournal.RecapGrid.Store.Tests;

internal static class StoreFixture {
    internal static readonly TimelineId Timeline = new("00112233445566778899aabbccddeeff");
    internal static readonly MaintainerDefinitionDigest Definition = new(new string('a', 64));
    internal static readonly LogicalColumnId Column = new("case.culprit");

    internal static GridBuildRecipe Recipe(int columns = 1, char bootstrap = 'c') => GridBuildRecipe.CreateFull(
        Timeline, new HistoryRowId(new string(bootstrap, 64)),
        BuildTarget.Create(Enumerable.Range(0, columns).Select(i => new BuildTargetColumn(
            i == 0 ? Column : new LogicalColumnId($"case.column{i}"), Definition)).ToArray()));

    internal static RowBuildSpec Spec(
        GridBuildRecipe? recipe = null,
        HistoryRowId? row = null,
        RecapRowView? previous = null,
        RefId? refId = null,
        bool withWork = true
    ) {
        recipe ??= Recipe();
        HistoryRowId actualRow = row ?? new HistoryRowId(new string('c', 64));
        var coordinate = new RowViewCoordinate(
            refId ?? new RefId(1), Timeline, actualRow,
            recipe.Digest, recipe.Target.Digest,
            previous?.HistoryRowId, previous?.Id, bootstrapCompleted: true);
        RowBuildAssignment[] assignments = recipe.Target.OrderedColumns.Select(column =>
            (RowBuildAssignment)new RowBuildAssignment.Evaluate(new CellSlot(recipe.Digest, actualRow, column.LogicalColumnId))).ToArray();
        RowBuildSpec spec = RowBuildSpec.CreateFull(recipe, coordinate, assignments);
        return withWork ? WithWork(spec) : spec;
    }

    internal static RecapCellDraft Draft(RowBuildSpec spec, string content = "answer", int column = 0) {
        var assignment = (RowBuildAssignment.Evaluate)spec.OrderedAssignments[column];
        return RecapCellDraft.Create(assignment.Slot, Definition, RecapCellOutcome.Updated, content,
            RecapGridLimits.MaximumContentUtf8Bytes);
    }

    internal static RecapCellArtifact Proposed(RowBuildSpec spec, string content = "answer", int column = 0) => new(
        new CellId(Guid.NewGuid().ToString("N")), ((RowBuildAssignment.Evaluate)spec.OrderedAssignments[column]).Slot,
        Definition, RecapCellOutcome.Updated, content);

    internal static RecapCellArtifact Put(RecapGridStoreHandle handle, RowBuildSpec spec, string content = "answer", int column = 0) {
        PutWork(handle, spec);
        return Assert.IsType<RecapGridCellPutResult.Inserted>(handle.Writer.PutCell(spec, Draft(spec, content, column))).Winner;
    }

    internal static void PutWork(RecapGridStoreHandle handle, RowBuildSpec spec) {
        Assert.NotNull(spec.Work);
        RecapGridRowWorkPutResult result = handle.Writer.PutRowWork(spec.Work!);
        Assert.True(result is RecapGridRowWorkPutResult.Inserted
            or RecapGridRowWorkPutResult.AlreadyPresent, result.ToString());
    }

    internal static RowBuildSpec WithWork(RowBuildSpec spec) {
        BuildTarget target = spec.Recipe.Target;
        var work = new RowWork(
            new RowWorkKey(spec.RefId, spec.TimelineId, spec.RecipeDigest, spec.HistoryRowId),
            target,
            spec.PreviousHistoryRowId,
            spec.PreviousRowResultId,
            spec.OrderedAssignments.Select(static assignment => assignment switch {
                RowBuildAssignment.Evaluate evaluate => new RowWorkAssignment(evaluate.LogicalColumnId, null),
                RowBuildAssignment.Reuse reuse => new RowWorkAssignment(reuse.LogicalColumnId, reuse.Cell.Id),
                _ => throw new InvalidOperationException("Unsupported assignment.")
            }));
        RowBuildAssignment[] assignments = spec.OrderedAssignments.Select(assignment => assignment switch {
            RowBuildAssignment.Evaluate evaluate => new RowBuildAssignment.Evaluate(
                new CellSlot(spec.RecipeDigest, spec.HistoryRowId, work.WorkId, evaluate.LogicalColumnId)),
            _ => assignment
        }).ToArray();
        return spec.Recipe.Kind switch {
            GridBuildRecipeKind.Full => RowBuildSpec.CreateFull(spec.Recipe, spec.Coordinate, assignments, work),
            GridBuildRecipeKind.Overlay when !spec.BootstrapCompleted
                || spec.Recipe.BootstrapThroughRowId == spec.HistoryRowId
                => RowBuildSpec.CreateOverlayBootstrap(spec.Recipe, spec.Coordinate, assignments, work),
            GridBuildRecipeKind.Overlay => RowBuildSpec.CreateNormal(spec.Recipe, spec.Coordinate, assignments, work),
            _ => throw new InvalidOperationException("Unsupported recipe kind.")
        };
    }

    internal static FulfilledViewKey Fulfilled(RowBuildSpec spec, long generation = 1) {
        var head = new TimelineHeadRef(Timeline, spec.Coordinate.RefId, null, new string('d', 64), null,
            0, HistoryTimelineSelectedPath.EmptyDigest, generation);
        return FulfilledViewKey.Create(head.RefId, head, spec.HistoryRowId, spec.Recipe);
    }
}
