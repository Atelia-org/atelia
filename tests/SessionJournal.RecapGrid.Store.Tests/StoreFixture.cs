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
        RefId? refId = null
    ) {
        recipe ??= Recipe();
        HistoryRowId actualRow = row ?? new HistoryRowId(new string('c', 64));
        var coordinate = new RowViewCoordinate(
            refId ?? new RefId(1), Timeline, actualRow,
            new HistorySegmentDescriptorDigest(actualRow.Value), recipe.Digest, recipe.Target.Digest,
            previous?.HistoryRowId, previous?.Id, bootstrapCompleted: true);
        RowBuildAssignment[] assignments = recipe.Target.OrderedColumns.Select(column =>
            (RowBuildAssignment)new RowBuildAssignment.Evaluate(new CellSlot(recipe.Digest, actualRow, column.LogicalColumnId))).ToArray();
        return RowBuildSpec.CreateFull(recipe, coordinate, assignments);
    }

    internal static RecapCellDraft Draft(RowBuildSpec spec, string content = "answer", int column = 0) {
        var assignment = (RowBuildAssignment.Evaluate)spec.OrderedAssignments[column];
        return RecapCellDraft.Create(assignment.Slot, Definition, RecapCellOutcome.Updated, content,
            RecapGridLimits.MaximumContentUtf8Bytes);
    }

    internal static RecapCellArtifact Proposed(RowBuildSpec spec, string content = "answer", int column = 0) => new(
        new CellId(Guid.NewGuid().ToString("N")), ((RowBuildAssignment.Evaluate)spec.OrderedAssignments[column]).Slot,
        Definition, RecapCellOutcome.Updated, content);

    internal static RecapCellArtifact Put(RecapGridStoreHandle handle, RowBuildSpec spec, string content = "answer", int column = 0) =>
        Assert.IsType<RecapGridCellPutResult.Inserted>(handle.Writer.PutCell(spec, Draft(spec, content, column))).Winner;

    internal static FulfilledViewKey Fulfilled(RowBuildSpec spec, long generation = 1) {
        var head = new TimelineHeadRef(Timeline, spec.Coordinate.RefId, null, new string('d', 64), null,
            0, HistoryTimelineSelectedPath.EmptyDigest, generation);
        return FulfilledViewKey.Create(head.RefId, head, spec.HistorySegmentDigest, spec.Recipe);
    }
}
