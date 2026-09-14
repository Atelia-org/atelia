using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid.Getter;
using Atelia.SessionJournal.RecapGrid.Store;
using Xunit;

namespace Atelia.SessionJournal.RecapGrid.Getter.Tests;

public sealed partial class GetterVerticalTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OverlayReportsActualPriorSourceWithoutRejectingReusedContent(bool missingSource) {
        using Fixture fixture = await CreateControlFixture(turns: 2, activate: false, createStore: true);
        var second = MaintainerDefinitionRevision.Create(
            new LogicalColumnId("case.second"), fixture.Family.Digest,
            new ContextHeaderBlockTarget(ContextHeaderCarrier.System, "second", "Second context"),
            fixture.Definition.Capability,
            new MaintainerDeclarativeSpec("Second question", "Maintain the second context."), 16 * 1024);
        BuildTarget target = BuildTarget.Create([
            new BuildTargetColumn(fixture.Definition.LogicalColumnId, fixture.Definition.Digest),
            new BuildTargetColumn(second.LogicalColumnId, second.Digest)
        ]);
        GridBuildRecipe basis = GridBuildRecipe.CreateFull(fixture.TimelineHead.TimelineId,
            fixture.TimelineHead.HeadRowId, target);
        ForgeActiveRecipe(fixture, second, basis);
        GridBuildRecipe overlay = GridBuildRecipe.CreateOverlay(basis,
            fixture.TimelineHead.HeadRowId, target, [second.LogicalColumnId]);
        ForgeActiveRecipe(fixture, second, overlay);

        Dictionary<HistoryRowId, RecapRowView> baseRows;
        using (RecapGridStoreHandle store = Assert.IsType<RecapGridStoreOpenResult.Opened>(
                   RecapGridStoreFactory.Open(fixture.Path)).Handle) {
            baseRows = BuildSourceRows(fixture, store, basis, [fixture.Definition, second]);
            Dictionary<HistoryRowId, RecapRowView> overlayRows = BuildSourceRows(
                fixture, store, overlay, [fixture.Definition, second], baseRows);
            HistorySegmentDescriptor last = fixture.Rows[^1].Descriptor;
            RecapRowView active = overlayRows[last.RowId];
            Assert.Equal(baseRows[last.RowId].OrderedCells[0].CellId, active.OrderedCells[0].CellId);
            Assert.NotEqual(baseRows[last.RowId].OrderedCells[1].CellId, active.OrderedCells[1].CellId);
            Assert.IsType<RecapGridFulfilledPutResult.Inserted>(store.Writer.PutFulfilled(
                FulfilledViewKey.Create(fixture.Journal.BranchRefId, fixture.TimelineHead,
                    last.DescriptorDigest, overlay), active.Id));
        }

        if (missingSource) {
            HistoryRowId sourcePrevious = fixture.Rows[^1].Descriptor.PreviousRowId!.Value;
            ExecuteStoreSql(fixture.Path,
                "PRAGMA foreign_keys=OFF; DELETE FROM row_view_member WHERE row_result_id=$id; DELETE FROM row_view WHERE row_result_id=$id;",
                ("$id", baseRows[sourcePrevious].Id.Value));
        }
        using RecapGridContextHandle getter = OpenGetter(fixture.Journal);
        var available = Assert.IsType<RecapGridContextMaterializeResult.Available>(
            getter.Materialize(Select(getter, fixture.Journal.ReadCurrentHead()!.Value)));
        Assert.Equal(missingSource ? RecapGridProvenanceStatus.Incomplete : RecapGridProvenanceStatus.NotSatisfied,
            available.Provenance.PriorSourceAligned);
        Assert.Equal(RecapGridProvenanceStatus.NotSatisfied, available.Provenance.FullRebuildChain);
        if (!missingSource) {
            // Both recipes generated the same two-column text. Source identity,
            // unlike retired content equality, still distinguishes predecessors.
            Assert.Equal(3, available.Provenance.ExaminedRows);
            Assert.Equal(6, available.Provenance.ExaminedMembers);
            Assert.Equal(4, available.Provenance.ExaminedCells);
            Assert.Equal(4 * System.Text.Encoding.UTF8.GetByteCount("same content"),
                available.Provenance.ExaminedContentUtf8Bytes);
        }
    }

    private static Dictionary<HistoryRowId, RecapRowView> BuildSourceRows(
        Fixture fixture, RecapGridStoreHandle store, GridBuildRecipe recipe,
        IReadOnlyList<MaintainerDefinitionRevision> definitions,
        IReadOnlyDictionary<HistoryRowId, RecapRowView>? baseRows = null) {
        var result = new Dictionary<HistoryRowId, RecapRowView>();
        RecapRowView? previous = null;
        foreach (HistoryTimelineSelectedRow row in fixture.Rows) {
            HistorySegmentDescriptor descriptor = row.Descriptor;
            RecapCellArtifact? reused = baseRows is null ? null : Assert.IsType<
                RecapGridStoreReadResult<RecapCellArtifact>.Found>(store.Reader.ReadCell(
                baseRows[descriptor.RowId].OrderedCells[0].CellId)).Value;
            RowBuildAssignment[] assignments = definitions.Select((definition, index) =>
                index == 0 && reused is not null
                    ? (RowBuildAssignment)new RowBuildAssignment.Reuse(definition.LogicalColumnId, reused)
                    : new RowBuildAssignment.Evaluate(new CellSlot(recipe.Digest, descriptor.RowId,
                        definition.LogicalColumnId))).ToArray();
            var coordinate = new RowViewCoordinate(fixture.Journal.BranchRefId, descriptor.TimelineId,
                descriptor.RowId, descriptor.DescriptorDigest, recipe.Digest, recipe.Target.Digest,
                descriptor.PreviousRowId, previous?.Id,
                bootstrapCompleted: baseRows is null || recipe.BootstrapThroughRowId == descriptor.RowId);
            RowBuildSpec spec = baseRows is null
                ? RowBuildSpec.CreateFull(recipe, coordinate, assignments)
                : RowBuildSpec.CreateOverlayBootstrap(recipe, coordinate, assignments);
            RecapCellArtifact[] cells = assignments.Select((assignment, index) =>
                assignment is RowBuildAssignment.Reuse ? reused!
                    : Assert.IsType<RecapGridCellPutResult.Inserted>(store.Writer.PutCell(spec,
                        RecapCellDraft.Create(((RowBuildAssignment.Evaluate)assignment).Slot,
                            definitions[index].Digest, RecapCellOutcome.Updated, "same content", 16 * 1024))).Winner)
                .ToArray();
            previous = Assert.IsType<RecapGridRowViewPutResult.Inserted>(store.Writer.PutRowView(spec, cells)).Winner;
            result.Add(descriptor.RowId, previous);
        }
        return result;
    }
}
